using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
namespace GameServer.Gateway.Tests;
public sealed class MinionUtilityTests
{
    [Fact]
    public async Task Convocation_recalls_followers_clears_actions_and_limits_regeneration_to_its_duration()
    {
        using var f = new Fixture();
        ushort source = f.Equip(("Summon Holy Relic",20)).Single();
        uint id = await Game.SummonFighter(f.Conn,source,Game.SummonData.Find("SummonHolyRelic")!,(160,160));
        var relic=f.Conn.Summons.Fighters[id];
        source=f.Equip(("Summon Skeletons",20)).Single();
        uint skeleton=await Game.SummonFighter(f.Conn,source,Game.SummonData.Find("SummonSkeletons")!,(180,180));
        var stationary=f.Conn.Summons.Fighters[skeleton].Monster.Walk.Position(0);
        source=f.Equip(("Convocation",20)).Single();
        var gem=Game.SupportedSkill(f.Conn,source);
        relic.Monster.Life=relic.Monster.MaxLife/2;
        Game.RecallMinionsLocked(f.Conn,gem,10);
        Assert.True(Game.Dist(relic.Monster.Walk.Position(10),f.Conn.Walk.Position(10))<20);
        Assert.Equal(stationary,f.Conn.Summons.Fighters[skeleton].Monster.Walk.Position(10));
        Assert.Equal(12,relic.RecallUntil);Assert.Equal(99,relic.RecallRate);
        Assert.Null(relic.Pending);
        uint before = relic.Monster.Life;
        Game.GolemStepLocked(f.Conn, relic, 13);
        Assert.InRange(relic.Monster.Life - before, (uint)(relic.Monster.MaxLife * .033) - 1, (uint)(relic.Monster.MaxLife * .033) + 1);
        uint healed = relic.Monster.Life;
        Game.GolemStepLocked(f.Conn, relic, 14);
        Assert.Equal(healed, relic.Monster.Life);
        Assert.Contains(f.Conn.Minions.OutputQueue,o=>o.Log=="Convocation recall"&&o.TargetId==id);
        Assert.DoesNotContain(f.Conn.Minions.OutputQueue,o=>o.Log=="Convocation recall"&&o.TargetId==skeleton);
        Assert.Equal(3000,Program.Skills.CooldownMs(gem.Effect,gem.Level));
    }
    internal sealed class Fixture : IDisposable
    {
        readonly string save = Path.Combine(Path.GetTempPath(), $"animated-admission-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly Task drain;
        internal ModuleLifetime Lifetime => lifetime;
        internal readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        internal NetworkStream Stream => writer.GetStream();
        internal readonly Player Conn = new(new(1, "animate", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Animate" + Guid.NewGuid().ToString("N"), Instance = new(), PlayerLife = 10000,
            PlayerMana = 10000, PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("animate-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture()
        {
            Saves.Open(save); Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Conn.Clock.Start(); Conn.Summons.Restored = Conn.Golems.Restored = true;
            Item Gem(string name)
            {
                var type = Program.Items.FindGem(name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
            }
            var host = ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!) with
            {
                Sockets = new[] { "Animate Weapon", "Animate Guardian", "Animate Weapon of Ranged Arms" }
                    .Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(), Links = [1, 1, 1]
            };
            Game.ItemsOf(Conn).Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
            listener.Start(); var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
            drain = Task.Run(async () => { try { var bytes = new byte[65536]; while (await reader.GetStream().ReadAsync(bytes) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
        }
        internal Task<uint> Summon(string effect, uint target)
        {
            ushort source = Game.GemSkillEffects(Conn).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            return Game.SummonFighter(Conn, source, Game.SummonData.Find(effect)!, (100, 100), target);
        }
        internal ushort[] Equip(params (string Name, int Level)[] gems)
        {
            Item Gem((string Name, int Level) gem)
            {
                var type = Program.Items.FindGem(gem.Name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(gem.Level) };
            }
            uint id = Game.ItemsOf(Conn).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(Conn).Change(PlayerInventories.Weapon1, id, item => item with
            { Sockets = gems.Select(g => new ItemSocket(SocketColour.White, Gem(g))).ToArray(),
                Links = Enumerable.Repeat((byte)1, gems.Length).ToArray() });
            return Game.GemSkillEffects(Conn).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Conn.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Conn.Character!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
