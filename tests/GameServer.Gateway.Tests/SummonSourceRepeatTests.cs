using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class SummonSourceRepeatTests
{
    [Fact]
    public async Task Persistent_summons_restore_their_source_and_rebuild_only_the_affected_fighter()
    {
        await VerifyGolemSources();
        using var f = new Fixture();
        var ids = f.Equip(("Raise Zombie", 1), ("Raise Zombie", 20));
        var def = Game.SummonData.Find("RaiseZombie")!;
        var saved = new SummonedMinions.Saved("RaiseZombie", ids[1], def.Variety, .5);
        CharacterSaves.ChangeModuleData(f.Conn.Character!, "Game.SummonedMinions", _ => JsonSerializer.Serialize(new[] { saved }));
        f.Conn.Summons.Restored = false;
        await Game.SummonsTick("test", f.Stream, f.Ciphers, f.Conn, 0);
        var original = Assert.Single(f.Conn.Summons.Fighters).Value;
        Assert.Equal(ids[1], original.Source); Assert.Equal(20, original.GemLevel);
        Game.SaveSummons(f.Conn);
        var durable = JsonSerializer.Deserialize<SummonedMinions.Saved[]>(CharacterSaves.Load(f.Conn.Character!)!.ModuleData!["Game.SummonedMinions"])!.Single();
        Assert.Equal((uint)1, durable.Host); Assert.Equal(1, durable.Socket);
        Assert.Equal(ids[1], Game.RestoredSummonSource(f.Conn, durable with { Source = ids[0] }));
        Assert.Equal((ushort)0, Game.RestoredSummonSource(f.Conn, durable with { Socket = 9 }));
        Assert.Equal(ids[0], Game.RestoredSummonSource(f.Conn, saved with { Source = 0 }));

        uint other = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, ids[0], def,
            (100, 100), restoredVariety: def.Variety);
        Assert.NotEqual(0u, other);
        var preserved = f.Conn.Summons.Fighters[other];
        uint priorLife = original.Monster.Life;
        var changed = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
        var sockets = changed.Item.Sockets.ToArray();
        var gem = sockets[1].Gem!;
        sockets[1] = sockets[1] with { Gem = gem with { GemExperience = Program.Items.Gem(Program.Items.ByHash(gem.TypeHash)!)!.ExperienceFor(19) } };
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, changed.Id, item => item with { Sockets = sockets });
        f.Conn.Summons.RefreshAt = 0;
        original.LastStep = 1; // Isolate the resource-preserving replacement from this tick's regeneration.
        await Game.SummonsTick("test", f.Stream, f.Ciphers, f.Conn, 1);
        Assert.Same(preserved, f.Conn.Summons.Fighters[other]);
        var replacement = f.Conn.Summons.Fighters.Values.Single(g => g.Source == ids[1]);
        Assert.NotEqual(original.Id, replacement.Id); Assert.Equal(19, replacement.GemLevel);
        Assert.Equal(Math.Min(priorLife, replacement.Monster.MaxLife), replacement.Monster.Life);
    }

    static async Task VerifyGolemSources()
    {
        using var f = new Fixture();
        var schedule = new Schedule();
        var oldContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, schedule);
        try
        {
            var ids = f.Equip(("Summon Stone Golem", 1), ("Summon Stone Golem", 20));
            f.Conn.PlayerStats[Program.Stats.Key("base_number_of_golems_allowed")!.Value] = 1;
            var def = Game.GolemData.Find("SummonStoneGolem")!;
            var saved = new GolemState.Saved(def.Id, ids[1], .5);
            CharacterSaves.ChangeModuleData(f.Conn.Character!, "Game.Golems", _ => JsonSerializer.Serialize(new[] { saved }));
            f.Conn.Golems.Restored = false;
            await Game.GolemsTick("test", f.Stream, f.Ciphers, f.Conn, 0);
            var original = Assert.Single(f.Conn.Golems.All).Value;
            Assert.Equal(ids[1], original.Source); Assert.Equal(20, original.GemLevel);
            Game.SaveGolems(f.Conn);
            var durable = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.Load(f.Conn.Character!)!.ModuleData!["Game.Golems"])!.Single();
            Assert.Equal((uint)1, durable.Host); Assert.Equal(1, durable.Socket);
            Assert.Equal(ids[1], Game.RestoredGolemSource(f.Conn, durable with { Skill = ids[0] }));
            Assert.Equal((ushort)0, Game.RestoredGolemSource(f.Conn, durable with { Socket = 9 }));
            Assert.Equal(ids[0], Game.RestoredGolemSource(f.Conn, saved with { Skill = 0 }));

            uint other = await Game.SummonGolem("test", f.Stream, f.Ciphers, f.Conn, ids[0], def, (100, 100));
            var preserved = f.Conn.Golems.All[other];
            uint priorLife = original.Monster.Life;
            Assert.Equal(0u, await Game.SummonGolem("test", f.Stream, f.Ciphers, f.Conn, ids[1], def,
                (100, 100), replacingOld: original, current: () => false));
            Assert.Same(original, f.Conn.Golems.All[original.Id]);
            var host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
            var sockets = host.Item.Sockets.ToArray();
            var gem = sockets[1].Gem!;
            sockets[1] = sockets[1] with { Gem = gem with { GemExperience = Program.Items.Gem(Program.Items.ByHash(gem.TypeHash)!)!.ExperienceFor(19) } };
            Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host.Id, item => item with { Sockets = sockets });
            f.Conn.Golems.RefreshAt = 0;
            schedule.Time = 1;
            original.LastStep = 1; // Source replacement preserves absolute resources; regeneration has its own controls.
            original.RecallUntil = 3; original.RecallRate = 99;
            await Game.GolemsTick("test", f.Stream, f.Ciphers, f.Conn, 1);
            Assert.Same(preserved, f.Conn.Golems.All[other]);
            var replacement = f.Conn.Golems.All.Values.Single(g => g.Source == ids[1]);
            Assert.NotEqual(original.Id, replacement.Id); Assert.Equal(19, replacement.GemLevel);
            Assert.Equal(Math.Min(priorLife, replacement.Monster.MaxLife), replacement.Monster.Life);
            Assert.Equal(3, replacement.RecallUntil); Assert.Equal(99, replacement.RecallRate);

            var beforeRevival = f.Conn.Golems.All.Keys.ToHashSet();
            f.Conn.Golems.Waiting.Add(new(durable with { Skill = ids[0], ReviveSeconds = .1 }, 2));
            f.Conn.Golems.Waiting.Add(new(durable with { Socket = 9, ReviveSeconds = .1 }, 2));
            schedule.Time = 2;
            await Game.GolemsTick("test", f.Stream, f.Ciphers, f.Conn, 2);
            Assert.Empty(f.Conn.Golems.Waiting);
            Assert.Equal(2, f.Conn.Golems.All.Count);
            uint revived = Assert.Single(f.Conn.Golems.All.Keys.Except(beforeRevival));
            Assert.Equal(ids[1], f.Conn.Golems.All[revived].Source);
        }
        finally { Game.AreaUpdateContext.Value = oldContext; }
    }

    [Fact]
    public async Task Echo_releases_summon_groups_and_golems_but_cancels_after_an_interruption()
    {
        using var f = new Fixture();
        ushort Echo(string name)
        {
            f.Equip((name, 20), ("Spell Echo Support", 20));
            var host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [2] });
            return Game.GemSkillEffects(f.Conn).Single().Id;
        }
        var schedule = new Schedule();
        var oldContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, schedule);
        try
        {
            f.Conn.PlayerStats[Program.Stats.Key("base_number_of_skeletons_allowed")!.Value] = 20;
            ushort source = Echo("Summon Skeletons");
            var supported = Game.SupportedSkill(f.Conn, source);
            Assert.Equal(1, Game.SpellRepeats(f.Conn, source, supported.Effect));
            Assert.True(await Game.CastSummon("test", f.Stream, f.Ciphers, f.Conn, source,
                Game.SummonData.Find("SummonSkeletons")!, (100, 100), 0, 0, () => true));
            Assert.Equal(6, f.Conn.Summons.Fighters.Count);
            Assert.True(schedule.Time >= (Game.CastMs(f.Conn, supported.Effect, source) ?? 0) / 500d);
            int before = f.Conn.Summons.Fighters.Count;
            bool Interruption()
            {
                if (f.Conn.Summons.Fighters.Count > before) f.Conn.FreezeGeneration++;
                return true;
            }
            Assert.False(await Game.CastSummon("test", f.Stream, f.Ciphers, f.Conn, source,
                Game.SummonData.Find("SummonSkeletons")!, (100, 100), 0, 0, Interruption));
            Assert.Equal(9, f.Conn.Summons.Fighters.Count);
            source = Echo("Summon Stone Golem");
            double start = schedule.Time;
            int recovery = f.Conn.ActionInterruptionGeneration;
            Assert.True(await Game.CastGolem("test", f.Stream, f.Ciphers, f.Conn, source,
                Game.GolemData.Find("SummonStoneGolem")!, (100, 100), recovery, () => true));
            var golem = Assert.Single(f.Conn.Golems.All).Value;
            double interval = (Game.CastMs(f.Conn, Game.SupportedSkill(f.Conn, source).Effect, source) ?? 0) / 1000d;
            Assert.True(golem.SummonedAt >= start + interval * 2);
        }
        finally { Game.AreaUpdateContext.Value = oldContext; }
    }

    sealed class Schedule : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(ClientState conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
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
