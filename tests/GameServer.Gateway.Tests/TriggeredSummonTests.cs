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
            var gem = Game.SupportedSkill(f.Conn, source);
            Assert.True(Game.TriggerPayloadAvailable(f.Conn, gem));
            if (name == "Raise Zombie")
            {
                var variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie")!;
                var at = f.Conn.Walk.Position(0);
                f.Conn.Monsters[900] = new("corpse", new(at.X+20,at.Y,1),100,8,1000)
                    { Hostile = true, Dead = true, Life = 0, Variety = variety, Level = 70 };
            }
            int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
            uint cost = (uint)Game.SupportedStats(f.Conn, source)[Program.Stats.Key("mana_cost")!.Value];
            int action = f.Conn.ActionCount;
            Game.RecordDamageTakenTriggers(f.Conn, threshold, 1000);
            uint before = f.Conn.PlayerMana;
            Game.ReleaseDamageTakenTriggers(f.Conn, f.Session);
            Assert.Equal(before - cost, f.Conn.PlayerMana);
            await Drain(f.Session);
            Assert.Equal(action, f.Conn.ActionCount);
            int expected = name == "Summon Skeletons" ? 3 : name == "Desecrate" ? 0 : 1;
            Assert.True(expected == f.Conn.Summons.Fighters.Count + f.Conn.Golems.All.Count, name);
            if (name == "Raise Zombie") Assert.True(f.Conn.Monsters[900].CorpseConsumed);
            if (name == "Desecrate") Assert.Equal(5, f.Conn.Summons.Corpses.Count);
            f.Conn.PlayerMana = 0;
            Game.RecordDamageTakenTriggers(f.Conn, threshold, 2000);
            Game.ReleaseDamageTakenTriggers(f.Conn, f.Session); await Drain(f.Session);
            Assert.True(expected == f.Conn.Summons.Fighters.Count + f.Conn.Golems.All.Count, name);
            Assert.Equal(0u, f.Conn.PlayerMana);
            f.Conn.PlayerMana = before;
            Game.RecordDamageTakenTriggers(f.Conn, threshold, 3000);
            f.Equip((name, 20), ("Cast when Damage Taken Support", 20));
            Game.ReleaseDamageTakenTriggers(f.Conn, f.Session); await Drain(f.Session);
            Assert.Equal(before, f.Conn.PlayerMana);
            Assert.True(expected == f.Conn.Summons.Fighters.Count + f.Conn.Golems.All.Count, name);
            if (name == "Raise Zombie")
            {
                var zombie = Assert.Single(f.Conn.Summons.Fighters).Value;
                uint restored = await Game.SummonFighter("restore", f.Stream, f.Ciphers, f.Conn, source,
                    Game.SummonData.Find("RaiseZombie")!, (100,100), restoredVariety: zombie.Monster.Variety!.Id, life: .5);
                Assert.NotEqual(0u, restored); // Restoring a persistent triggered minion is not a manual cast.
                Assert.InRange(f.Conn.Summons.Fighters[restored].Monster.Life / (double)zombie.Monster.MaxLife, .49, .51);
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
            var driver = Game.GemSkillEffects(f.Conn).Single(s => s.Id == ids[0]);
            var gem = Game.SupportedSkill(f.Conn, ids[1]);
            uint cost = (uint)Game.SupportedStats(f.Conn, ids[1])[Program.Stats.Key("mana_cost")!.Value];
            uint mana = f.Conn.PlayerMana; int action = f.Conn.ActionCount;
            if (channel)
            {
                f.Conn.ChannelSkill = driver.Id; f.Conn.ChannelGeneration = 1; f.Conn.LastChannelUpdateAt = 10000;
                f.Conn.PaidChannel = new(driver.Id, driver.Effect, action, 1, Game.CombatLifetime!);
                Game.BeginChannelTriggers(f.Conn, f.Session, driver.Id, action, 1, (20,0), 10);
                double interval = Game.SupportedSkill(f.Conn, driver.Id).Stats[Program.Stats.Key("cast_while_channelling_time_ms")!.Value] / 1000d;
                Game.TickChannelTriggers(f.Conn, f.Session, 10 + interval - .001);
                Assert.Equal(mana, f.Conn.PlayerMana);
                Game.TickChannelTriggers(f.Conn, f.Session, 10 + interval + .001);
            }
            else
            {
                var captured = Assert.IsType<Game.LinkedTriggerSource>(Game.CaptureCriticalTriggerSource(f.Conn, driver.Id));
                Assert.False(Game.ReleaseCriticalTrigger(f.Conn,f.Session,captured,false,true,(20,0),10));
                Assert.False(Game.ReleaseCriticalTrigger(f.Conn,f.Session,captured,true,false,(20,0),10));
                Assert.Equal(mana, f.Conn.PlayerMana);
                Assert.True(Game.ReleaseCriticalTrigger(f.Conn,f.Session,captured,true,true,(20,0),10));
            }
            Assert.Equal(mana-cost, f.Conn.PlayerMana); await Drain(f.Session);
            Assert.Equal(3, f.Conn.Summons.Fighters.Count); Assert.Equal(action, f.Conn.ActionCount);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Session.Enqueue(async () => { entered.SetResult(); await resume.Task; }); await entered.Task;
            try
            {
                if (channel) Game.TickChannelTriggers(f.Conn, f.Session, 11);
                else Game.ReleaseCriticalTrigger(f.Conn,f.Session,Game.CaptureCriticalTriggerSource(f.Conn,driver.Id),true,true,(20,0),11);
                f.Equip(("Cyclone",20), ("Summon Skeletons",20),
                    (channel ? "Cast while Channelling Support" : "Cast On Critical Strike Support",20));
            }
            finally { resume.SetResult(); }
            await Drain(f.Session);
            Assert.Equal(3, f.Conn.Summons.Fighters.Count); Assert.Equal(action, f.Conn.ActionCount);
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
        internal readonly ClientState Conn = new(new(1, "animate", 1000, 1000, 100, 100, null, []), 1, 1)
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
            foreach (string s in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                Conn.PlayerStats[Program.Stats.Key(s)!.Value] = 1000;
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
            Session = new("trigger-summon", Stream, Ciphers, Conn);
        }
        internal Task<uint> Summon(string effect, uint target)
        {
            ushort source = Game.GemSkillEffects(Conn).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            return Game.SummonFighter("test", Stream, Ciphers, Conn, source, Game.SummonData.Find(effect)!, (100, 100), target);
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
                Links = [(byte)gems.Length] });
            return Game.GemSkillEffects(Conn).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Session.Invalidate(); Session.OutputCompleted.GetAwaiter().GetResult();
            Conn.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Conn.Character!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
