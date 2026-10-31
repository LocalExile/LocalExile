using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Rules = global::Game;

namespace GameServer.Gateway.Tests;

// The player stat snapshot reuses its tail while the tail's inputs are the same (Modules/Core/Game/Player/StatSnapshotCache.cs).
// Every stat source the game changes is changed here in turn, as the game changes it; after each change the snapshot
// must be the one a connection without a reused tail computes (same stats, same order), a repeated snapshot must reuse
// the tail, and the verification mode compares that reuse with a fresh tail.
public class StatSnapshotTailTests
{
    [Fact]
    public void A_reused_tail_is_the_fresh_one_through_every_stat_source()
    {
        new GameServer.Modules.Game.GameModule().Load(null!);
        Saves.Open(Path.Combine(Path.GetTempPath(), $"localexile-tests-{Guid.NewGuid():N}.db"));
        bool verify = Rules.VerifyStatSnapshotTail;
        Rules.VerifyStatSnapshotTail = true;
        try
        {
            string character = "StatTail" + Guid.NewGuid().ToString("N");
            var conn = Connection(character);
            var items = Rules.ItemsFor(conn);
            Dictionary<uint, int>? previous = null;
            uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
            double Now() => conn.Clock.Elapsed.TotalSeconds;
            void RebuildPools() { lock (conn.Monsters) conn.Pools = Rules.PoolsFor(conn, conn.Pools.Level); }

            // changes: whether this step must change the snapshot (a step that only moves an input the tail reads, such as
            // the stored life, may leave every published value as it was).
            void Step(string what, bool changes = true)
            {
                long misses = Rules.StatSnapshotTailMisses, hits = Rules.StatSnapshotTailHits;
                var first = Snapshot(conn);
                var again = Snapshot(conn);
                Assert.True(Rules.StatSnapshotTailHits > hits, $"{what}: the unchanged snapshot reused its tail");
                Rules.StatSnapshotTails.Remove(conn);
                var fresh = Snapshot(conn);
                Assert.True(Rules.SameInOrder(fresh, first), $"{what}: {Differences(fresh, first)}");
                Assert.True(Rules.SameInOrder(fresh, again), $"{what} (reused): {Differences(fresh, again)}");
                if (previous is not null && changes)
                {
                    Assert.True(Rules.StatSnapshotTailMisses > misses, $"{what}: the changed inputs computed the tail again");
                    Assert.False(Rules.SameInOrder(previous, fresh), $"{what}: the snapshot did not change");
                }
                previous = fresh;
            }

            Step("start");
            lock (conn.Monsters) conn.PlayerStats[Key("critical_strike_chance_+%")] = 50;
            Step("/set_stat");
            lock (conn.Monsters) conn.PlayerStats.Remove(Key("critical_strike_chance_+%"));
            Step("/set_stat removed");
            lock (conn.Monsters) conn.Pools = Rules.PoolsFor(conn, 41);
            Step("level up");
            var tree = Rules.PassivesFor(conn);
            ushort node = Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
                .First(i => tree.Find(i) is { Stats.Count: > 0, IsAscendancy: false, IsStart: false, IsKeystone: false, IsMastery: false,
                    IsJewelSocket: false, IsProxy: false, IsExpansion: false });
            lock (conn.Monsters) conn.Passives.Add(node);
            RebuildPools();
            Step("passive allocated");
            lock (conn.Monsters) conn.Passives.Remove(node);
            RebuildPools();
            Step("passive refunded");

            var inventory = Rules.ItemsOf(conn);
            var wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(items.Find("Driftwood Wand")!));
            RebuildPools();
            Step("main hand equipped");
            var offWand = inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Goat's Horn")!));
            RebuildPools();
            Step("off hand equipped (dual wield)");
            var gem = ItemCatalog.Create(items.Find("Fireball")!);
            Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
            wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand.Item with { Sockets = [new ItemSocket(SocketColour.Blue, gem)] });
            RebuildPools();
            Step("gem socketed", changes: false);
            Assert.NotNull(inventory.Remove(PlayerInventories.Offhand1, offWand.Id));
            inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Imbued Wand")!));
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
            lock (conn.Monsters)
            {
                var charges = Rules.ChargesOf(conn);
                charges.State.Gain(ChargeType.Frenzy, 2, Now(), charges.Limits(ChargeType.Frenzy));
            }
            Step("frenzy charges gained");
            lock (conn.Monsters)
            {
                var charges = Rules.ChargesOf(conn);
                charges.State.Set(ChargeType.Frenzy, 0, Now(), charges.Limits(ChargeType.Frenzy));
            }
            Step("frenzy charges lost");
            lock (conn.Monsters)
                Assert.True(Rules.FortificationOf(conn).Hit(Now(), 1_000_000, 1000, MonsterRarity.Unique,
                    name => name == FortificationSession.OnMeleeHit ? 1 : 0, new Random(1)));
            Step("fortified");
            lock (conn.Monsters) Rules.FortificationOf(conn).Clear();
            Step("fortification gone");
            lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife / 2;
            Step("hit", changes: false);
            lock (conn.Monsters) Rules.LeechOf(conn).Hit(Now(), (500, 0, 0), lifeFull: false, manaFull: false, shieldFull: false, attack: true);
            Step("leeching");
            lock (conn.Monsters) { conn.PlayerLife = conn.PlayerMaxLife; Rules.LeechOf(conn).Clear(Now()); }
            Step("leech stopped");
            lock (conn.Monsters)
                conn.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 400, 400), DamagePacket.Of(DamageType.Cold, 400, 400),
                    false, conn.PlayerMaxLife, Now(), _ => 0, new Random(1));
            Step("chilled");
            lock (conn.Monsters) conn.ElementalAilments.Clear();
            Step("chill gone");

            var diamond = items.Find("Diamond Flask")!;
            var flask = items.UtilityFlaskOf(diamond)!;
            conn.UtilityFlaskEffects.Start(new UtilityFlasks.Active(1, 0, diamond.Name, flask, 1, null,
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
            long before = Rules.StatSnapshotTailMisses;
            Snapshot(conn);
            Assert.True(Rules.StatSnapshotTailMisses > before, "a new area's connection computes its tail");
            Step("area changed");
            Assert.Equal(0, Rules.StatSnapshotTailMismatches);
        }
        finally { Rules.VerifyStatSnapshotTail = verify; }
    }

    static ClientState Connection(string character)
    {
        var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
        {
            Character = character, ClassRow = 1, PlayerSent = true, Instance = new(),
        };
        conn.Pools = Rules.PoolsFor(conn, 40);
        conn.PlayerLife = conn.Pools.Life;
        conn.PlayerMana = conn.Pools.Mana;
        return conn;
    }

    static Dictionary<uint, int> Snapshot(ClientState conn)
    {
        lock (conn.Monsters) return Rules.CharacterPlayerStatsCore(conn, out _);
    }

    static string Differences(Dictionary<uint, int> expected, Dictionary<uint, int> actual) =>
        string.Join(", ", expected.Keys.Union(actual.Keys).Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Take(8).Select(k => $"{Program.Stats.Name(k)} {expected.GetValueOrDefault(k)} vs {actual.GetValueOrDefault(k)}"))
        + (Rules.SameInOrder(new Dictionary<uint, int>(expected.OrderBy(kv => kv.Key)), new Dictionary<uint, int>(actual.OrderBy(kv => kv.Key)))
            ? " (order only)" : "");
}
