using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The player stat snapshot reuses its tail while the tail's inputs are the same (Modules/Core/Game/Player/StatSnapshotCache.cs).
// Every stat source the game changes is changed here in turn, as the game changes it; after each change the snapshot
// must be the one a connection without a reused tail computes (same stats, same order), a repeated snapshot must reuse
// the tail, and the verification mode (on in every Gateway test) compares that reuse with a fresh tail. The connection's
// clock is stopped, so nothing timed changes between the snapshots of a step.
public class StatSnapshotTailTests
{
    [Fact]
    public void A_reused_tail_is_the_fresh_one_through_every_stat_source()
    {
        using var setup = new SnapshotSetup();
        Assert.True((bool)Rules.GetField("VerifyStatSnapshotTail", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
        string character = "StatTail" + Guid.NewGuid().ToString("N");
        var conn = Connection(character);
        var items = (ItemCatalog)Game("ItemsFor", conn)!;
        Dictionary<uint, int>? previous = null;
        uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
        double Now() => conn.Clock.Elapsed.TotalSeconds;
        void RebuildPools() { lock (conn.Monsters) conn.Pools = Pools(conn, conn.Pools.Level); }

        // changes: whether this step must change the snapshot (a step that only moves an input the tail reads, such as
        // the stored life, may leave every published value as it was).
        void Step(string what, bool changes = true)
        {
            long misses = Counter("StatSnapshotTailMisses"), hits = Counter("StatSnapshotTailHits");
            var first = Snapshot(conn);
            var again = Snapshot(conn);
            Assert.True(Counter("StatSnapshotTailHits") > hits, $"{what}: the unchanged snapshot reused its tail");
            ForgetTail(conn);
            var fresh = Snapshot(conn);
            Assert.True(SameInOrder(fresh, first), $"{what}: {Differences(fresh, first)}");
            Assert.True(SameInOrder(fresh, again), $"{what} (reused): {Differences(fresh, again)}");
            if (previous is not null && changes)
            {
                Assert.True(Counter("StatSnapshotTailMisses") > misses, $"{what}: the changed inputs computed the tail again");
                Assert.False(SameInOrder(previous, fresh), $"{what}: the snapshot did not change");
            }
            previous = fresh;
        }

        Step("start");
        lock (conn.Monsters) conn.PlayerStats[Key("critical_strike_chance_+%")] = 50;
        Step("/set_stat");
        lock (conn.Monsters) conn.PlayerStats.Remove(Key("critical_strike_chance_+%"));
        Step("/set_stat removed");
        lock (conn.Monsters) conn.Pools = Pools(conn, 41);
        Step("level up");
        var tree = (PassiveTree)Game("PassivesFor", conn)!;
        ushort node = Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
            .First(i => tree.Find(i) is { Stats.Count: > 0, IsAscendancy: false, IsStart: false, IsKeystone: false, IsMastery: false,
                IsJewelSocket: false, IsProxy: false, IsExpansion: false });
        lock (conn.Monsters) conn.Passives.Add(node);
        RebuildPools();
        Step("passive allocated");
        lock (conn.Monsters) conn.Passives.Remove(node);
        RebuildPools();
        Step("passive refunded");

        var inventory = (PlayerInventories)Game("ItemsOf", conn)!;
        var wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(items.Find("Driftwood Wand")!, random: new Random(1)));
        RebuildPools();
        Step("main hand equipped");
        var offWand = inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Goat's Horn")!, random: new Random(1)));
        RebuildPools();
        Step("off hand equipped (dual wield)");
        var gem = ItemCatalog.Create(items.Find("Fireball")!, random: new Random(1));
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
        wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand.Item with { Sockets = [new ItemSocket(SocketColour.Blue, gem)] });
        RebuildPools();
        Step("gem socketed", changes: false);
        Assert.NotNull(inventory.Remove(PlayerInventories.Offhand1, offWand.Id));
        inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Imbued Wand")!, random: new Random(1)));
        RebuildPools();
        Step("off hand swapped for one whose requirements are not met");
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
        RebuildPools();
        Step("main hand unequipped");

        lock (conn.Monsters) conn.PlayerLife = 1;
        Step("low life");
        lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife;
        Step("full life");
        lock (conn.Monsters) conn.BloodCharges = 2;
        Step("blood charges");
        void SetFrenzy(bool gain)
        {
            lock (conn.Monsters)
            {
                object session = Game("ChargesOf", conn)!;
                var state = (Charges.State)session.GetType().GetProperty("State")!.GetValue(session)!;
                var limits = (Charges.Limits)session.GetType().GetMethod("Limits")!.Invoke(session, [ChargeType.Frenzy])!;
                if (gain) state.Gain(ChargeType.Frenzy, 2, Now(), limits);
                else state.Set(ChargeType.Frenzy, 0, Now(), limits);
            }
        }
        SetFrenzy(gain: true);
        Step("frenzy charges gained");
        SetFrenzy(gain: false);
        Step("frenzy charges lost");
        lock (conn.Monsters)
        {
            object fortification = Game("FortificationOf", conn)!;
            Assert.True((bool)fortification.GetType().GetMethod("Hit")!.Invoke(fortification, [Now(), 1_000_000m, 1000m, MonsterRarity.Unique,
                (Func<string, int>)(name => name == "fortify_on_hit" ? 1 : 0), new Random(1)])!);
        }
        Step("fortified");
        lock (conn.Monsters) { object f = Game("FortificationOf", conn)!; f.GetType().GetMethod("Clear")!.Invoke(f, []); }
        Step("fortification gone");
        lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife / 2;
        Step("hit", changes: false);
        lock (conn.Monsters)
        {
            object leech = Game("LeechOf", conn)!;
            leech.GetType().GetMethod("Hit")!.Invoke(leech, [Now(), (500.0, 0.0, 0.0), false, false, false, true]);
        }
        Step("leeching");
        lock (conn.Monsters)
        {
            conn.PlayerLife = conn.PlayerMaxLife;
            object leech = Game("LeechOf", conn)!;
            leech.GetType().GetMethod("Clear")!.Invoke(leech, [Now()]);
        }
        Step("leech stopped");
        lock (conn.Monsters)
            conn.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 400, 400), DamagePacket.Of(DamageType.Cold, 400, 400),
                false, conn.PlayerMaxLife, Now(), _ => 0, new Random(1));
        Step("chilled");
        lock (conn.Monsters) conn.ElementalAilments.Clear();
        Step("chill gone");

        var diamond = items.Find("Diamond Flask")!;
        conn.UtilityFlaskEffects.Start(new UtilityFlasks.Active(1, 0, diamond.Name, items.UtilityFlaskOf(diamond)!, 1, null,
            [new UtilityFlasks.Source("buff", "critical_strike_chance_+%", 100)], Now(), Now() + 60));
        RebuildPools();
        Step("flask used");
        conn.UtilityFlaskEffects.End(Now(), all: true, out _);
        RebuildPools();
        Step("flask effect ended");

        lock (conn.Monsters) conn.Pools = conn.Pools with { EnergyShield = 200 };
        conn.EnergyShield.SetMaximum(200, refill: true);
        Step("energy shield");
        lock (conn.Monsters)
        {
            conn.EnergyShield.Damage(100, false, Now());
            conn.EnergyShield.Advance(Now() + 2.1, Now() + 2.2, _ => 0, alive: true);
            Assert.True(conn.EnergyShield.Recharging);
        }
        Step("energy shield recharging");

        // A new area is a new connection: its snapshot starts without a tail of its own.
        conn = Connection(character);
        previous = null;
        long before = Counter("StatSnapshotTailMisses");
        Snapshot(conn);
        Assert.True(Counter("StatSnapshotTailMisses") > before, "a new area's connection computes its tail");
        Step("area changed");
        Assert.Equal(0, Counter("StatSnapshotTailMismatches"));
    }

    [Fact]
    public void Warm_stages_still_observe_rampage_and_reconcile_ailments_with_current_action_speed()
    {
        using var setup = new SnapshotSetup();
        var conn = Connection("StatEffects" + Guid.NewGuid().ToString("N"));
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        conn.PlayerStats[Key(RampageSession.Source)] = 1;
        Snapshot(conn);
        conn.Rampage.Restore(new(40, 5), conn.Clock.Elapsed.TotalSeconds);
        var rampaging = Snapshot(conn);
        Assert.Equal(40, rampaging[Key("current_rampage_stacks")]);
        Snapshot(conn); // All three pure stages are warm.
        // An area's frozen transfer time can move independently of every cached input.
        conn.LeavingArea = true;
        conn.RampageTransferAt = conn.Clock.Elapsed.TotalSeconds + 6;
        var expired = Snapshot(conn);
        Assert.Equal(0, conn.Rampage.Current.Stacks);
        Assert.Equal(0, expired[Key("current_rampage_stacks")]);
        conn.LeavingArea = false;
        conn.RampageTransferAt = null;

        void Chill() => conn.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 400, 400),
            DamagePacket.Of(DamageType.Cold, 400, 400), false, conn.PlayerMaxLife,
            conn.Clock.Elapsed.TotalSeconds, _ => 0, new Random(1));
        conn.PlayerStats[Key("action_speed_-%")] = -20;
        Chill();
        var chilled = Snapshot(conn);
        Assert.Equal(-20, chilled[Key("action_speed_-%")]); // Explicit overrides still replace the final row.
        Assert.Equal(-10, chilled[Key("intermediary_combined_action_speed_+%")]); // 30% chill + 20% faster.
        conn.PlayerStats[Key("action_speed_cannot_be_reduced_below_base")] = 1;
        var floored = Snapshot(conn);
        Assert.Equal(0, floored[Key("intermediary_combined_action_speed_+%")]);
        conn.PlayerStats[Key("cannot_be_chilled")] = 1;
        Snapshot(conn); // Reconcile removes chill and warms all stages.
        Chill(); // Add an effect after the warm snapshot without changing any stat input.
        Snapshot(conn);
        Assert.Equal(0, conn.ElementalAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds).Chill);
        Assert.Equal(0, Counter("StatSnapshotTailMismatches"));
    }

    // Keep the snapshot test's rules, saves and lifetime scoped. Starting the whole module host would register the
    // optional Phrecia provider for unrelated admission tests and retain a database those tests can close.
    sealed class SnapshotSetup : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"localexile-snapshot-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = global::Game.CombatLifetime;
        readonly bool previousVerify = global::Game.VerifyStatSnapshotTail;
        readonly ModuleLifetime lifetime = new("Game");
        public SnapshotSetup()
        {
            Saves.Open(path);
            global::Game.VerifyStatSnapshotTail = true;
            global::Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public void Dispose()
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            global::Game.VerifyStatSnapshotTail = previousVerify;
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    static Type Rules => typeof(global::Game);

    // A rule of the Game module, by name and argument count.
    static object? Game(string method, params object?[] args) =>
        Rules.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(null, args);

    static long Counter(string name) => (long)Rules.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    static CharacterStats.Pools Pools(ClientState conn, int level) => (CharacterStats.Pools)Game("PoolsFor", conn, level)!;

    // The connection's reused tail goes, as on a connection that never had one.
    static void ForgetTail(ClientState conn)
    {
        object tails = Rules.GetField("StatSnapshotTails", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        tails.GetType().GetMethod("Remove", [typeof(ClientState)])!.Invoke(tails, [conn]);
    }

    static ClientState Connection(string character)
    {
        var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
        {
            Character = character, ClassRow = 1, PlayerSent = true, Instance = new(),
        };
        // Stopped, as the Escape menu's pause stops it: no timed value changes during the test.
        conn.Clock.Stop();
        conn.Pools = Pools(conn, 40);
        conn.PlayerLife = conn.Pools.Life;
        conn.PlayerMana = conn.Pools.Mana;
        return conn;
    }

    static Dictionary<uint, int> Snapshot(ClientState conn)
    {
        lock (conn.Monsters) return (Dictionary<uint, int>)Game("CharacterPlayerStatsCore", conn, null)!;
    }

    static bool SameInOrder(Dictionary<uint, int> a, Dictionary<uint, int> b) => a.SequenceEqual(b);

    static string Differences(Dictionary<uint, int> expected, Dictionary<uint, int> actual) =>
        string.Join(", ", expected.Keys.Union(actual.Keys).Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Take(8).Select(k => $"{Program.Stats.Name(k)} {expected.GetValueOrDefault(k)} vs {actual.GetValueOrDefault(k)}"))
        + (expected.OrderBy(kv => kv.Key).SequenceEqual(actual.OrderBy(kv => kv.Key)) ? " (order only)" : "");
}
