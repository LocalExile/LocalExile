using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
namespace GameServer.Gateway.Tests;
public sealed class TriggeredSummonTests
{
    static async Task Drain(Game.ProjectileSession session)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
        await done.Task;
    }

    [Fact]
    public async Task Cwdt_releases_paid_summons_and_corpses_without_an_action_or_echo_and_rejects_stale_sources()
    {
        foreach (string name in new[] { "Summon Skeletons", "Summon Raging Spirit", "Raise Zombie", "Summon Stone Golem", "Desecrate" })
        {
            using var f = new Fixture();
            ushort source = f.Equip((name, 20), ("Cast when Damage Taken Support", 20), ("Spell Echo Support", 20)).First();
            var gem = Game.SupportedSkill(f.Player, source);
            Assert.True(Game.TriggerPayloadAvailable(f.Player, gem));
            if (name == "Raise Zombie")
            {
                var variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie")!;
                var at = f.Player.Walk.Position(0);
                f.Player.Area.Monsters[900] = new("corpse", new(at.X+20,at.Y,1),100,8,1000)
                    { Hostile = true, Dead = true, Life = 0, Variety = variety, Level = 70 };
            }
            int threshold = gem.Stats[Stat.CastOnDamageTakenThreshold];
            uint cost = (uint)Game.SupportedStats(f.Player, source)[Stat.ManaCost];
            int action = f.Player.ActionCount;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 1000);
            uint before = f.Player.PlayerMana;
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session);
            Assert.Equal(before - cost, f.Player.PlayerMana);
            await Drain(f.Session);
            Assert.Equal(action, f.Player.ActionCount);
            int expected = name == "Summon Skeletons" ? 3 : name == "Desecrate" ? 0 : 1;
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            if (name == "Raise Zombie") Assert.True(f.Player.Area.Monsters[900].CorpseConsumed);
            if (name == "Desecrate") Assert.Equal(5, f.Player.Summons.Corpses.Count);
            f.Player.PlayerMana = 0;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 2000);
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session); await Drain(f.Session);
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            Assert.Equal(0u, f.Player.PlayerMana);
            f.Player.PlayerMana = before;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 3000);
            f.Equip((name, 20), ("Cast when Damage Taken Support", 20));
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session); await Drain(f.Session);
            Assert.Equal(before, f.Player.PlayerMana);
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            if (name == "Raise Zombie")
            {
                var zombie = Assert.Single(f.Player.Summons.Fighters).Value;
                uint restored = await Game.SummonFighter(f.Player, source,
                    Game.SummonData.Find("RaiseZombie")!, (100,100), restoredVariety: zombie.Monster.Variety!.Id, life: .5);
                Assert.NotEqual(0u, restored); // Restoring a persistent triggered minion is not a manual cast.
                Assert.InRange(f.Player.Summons.Fighters[restored].Monster.Life / (double)zombie.Monster.MaxLife, .49, .51);
            }
        }
    }

    [Fact]
    public async Task Coc_and_cwc_summon_once_and_a_queued_release_cannot_outlive_its_source()
    {
        foreach (bool channel in new[] { false, true })
        {
            using var f = new Fixture();
            var ids = f.Equip(("Cyclone",20), ("Summon Skeletons",20),
                (channel ? "Cast while Channelling Support" : "Cast On Critical Strike Support",20), ("Spell Echo Support",20));
            var driver = Game.GemSkillEffects(f.Player).Single(s => s.Id == ids[0]);
            var gem = Game.SupportedSkill(f.Player, ids[1]);
            uint cost = (uint)Game.SupportedStats(f.Player, ids[1])[Stat.ManaCost];
            uint mana = f.Player.PlayerMana; int action = f.Player.ActionCount;
            if (channel)
            {
                f.Player.ChannelSkill = driver.Id; f.Player.ChannelGeneration = 1; f.Player.LastChannelUpdateAt = 10000;
                f.Player.PaidChannel = new(driver.Id, driver.Effect, action, 1, Game.CombatLifetime!);
                Game.BeginChannelTriggers(f.Player, f.Session, driver.Id, action, 1, (20,0), 10);
                double interval = Game.SupportedSkill(f.Player, driver.Id).Stats[Stat.CastWhileChannellingTimeMs] / 1000d;
                Game.TickChannelTriggers(f.Player, f.Session, 10 + interval - .001);
                Assert.Equal(mana, f.Player.PlayerMana);
                Game.TickChannelTriggers(f.Player, f.Session, 10 + interval + .001);
            }
            else
            {
                var captured = Assert.IsType<Game.LinkedTriggerSource>(Game.CaptureCriticalTriggerSource(f.Player, driver.Id));
                Assert.False(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,false,true,(20,0),10));
                Assert.False(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,true,false,(20,0),10));
                Assert.Equal(mana, f.Player.PlayerMana);
                Assert.True(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,true,true,(20,0),10));
            }
            Assert.Equal(mana-cost, f.Player.PlayerMana); await Drain(f.Session);
            Assert.Equal(3, f.Player.Summons.Fighters.Count); Assert.Equal(action, f.Player.ActionCount);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Session.Enqueue(async () => { entered.SetResult(); await resume.Task; }); await entered.Task;
            try
            {
                if (channel) Game.TickChannelTriggers(f.Player, f.Session, 11);
                else Game.ReleaseCriticalTrigger(f.Player,f.Session,Game.CaptureCriticalTriggerSource(f.Player,driver.Id),true,true,(20,0),11);
                f.Equip(("Cyclone",20), ("Summon Skeletons",20),
                    (channel ? "Cast while Channelling Support" : "Cast On Critical Strike Support",20));
            }
            finally { resume.SetResult(); }
            await Drain(f.Session);
            Assert.Equal(3, f.Player.Summons.Fighters.Count); Assert.Equal(action, f.Player.ActionCount);
        }
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
        internal readonly Game.ProjectileSession Session;
        internal readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        internal NetworkStream Stream => writer.GetStream();
        internal readonly Player Player = new(new(1, "animate", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Animate" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            PlayerLife = 10000,
            PlayerMana = 10000,
            Connection = { PlayerSent = true },
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("animate-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture()
        {
            Saves.Open(save); Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Player.Clock.Start(); Player.Summons.Restored = Player.Golems.Restored = true;
            TestRequirements.Waive(Player);
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
            Game.ItemsOf(Player).Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
            listener.Start(); var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
            Player.Connection.Session = new("test", Stream, Ciphers);
            drain = Task.Run(async () => { try { var bytes = new byte[65536]; while (await reader.GetStream().ReadAsync(bytes) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
            Session = new(Player);
        }
        internal Task<uint> Summon(string effect, uint target)
        {
            ushort source = Game.GemSkillEffects(Player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            return Game.SummonFighter(Player, source, Game.SummonData.Find(effect)!, (100, 100), target);
        }
        internal ushort[] Equip(params (string Name, int Level)[] gems)
        {
            Item Gem((string Name, int Level) gem)
            {
                var type = Program.Items.FindGem(gem.Name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(gem.Level) };
            }
            uint id = Game.ItemsOf(Player).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(Player).Change(PlayerInventories.Weapon1, id, item => item with
            { Sockets = gems.Select(g => new ItemSocket(SocketColour.White, Gem(g))).ToArray(),
                Links = [(byte)gems.Length] });
            return Game.GemSkillEffects(Player).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Session.Invalidate(); Session.OutputCompleted.GetAwaiter().GetResult();
            Player.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Player.Character.Name!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
