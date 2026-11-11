using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Existing_resource_conditions_reach_ailment_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear, Func<string, ItemMod> minimum)
    {
        conn.Clock.Stop(); // Freeze this controlled owner timeline before any ailment or recovery admission.
        var inventory = Game.ItemsOf(conn);
        Dictionary<uint, int> Read() => Game.CharacterPlayerStats(conn);
        var low = Read();
        int Get(string name) => Program.Stats.Key(name) is uint key ? low.GetValueOrDefault(key) : 0;
        const string closeResources = "unaffected_by_ignite_and_shock_while_max_life_mana_within_500";
        var scalesRow = WithMod("ImmuneToElementalAilmentsWhileLifeAndManaCloseUnique__1", 1);
        foreach (int difference in new[] { -501, -500, -499, 0, 499, 500, 501 })
        {
            var subject = new StatConditions.Subject(Value: n => n == "life" ? 1000 : n == "mana" ? 1000 + difference : null);
            bool holds = Math.Abs(difference) <= 500;
            Assert.Equal(holds ? StatConditions.State.Holds : StatConditions.State.Fails, StatConditions.Of(closeResources, subject));
            var summed = ItemModStats.Sum([scalesRow], Program.Items.Mods, subject: subject, statExists: Program.Stats.Has);
            Assert.Equal(holds ? 1 : 0, summed.GetValueOrDefault("unaffected_by_ignite"));
            Assert.Equal(holds ? 1 : 0, summed.GetValueOrDefault("unaffected_by_shock"));
        }
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(closeResources,
            new StatConditions.Subject(Value: n => n == "life" ? 1000 : null)));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of(closeResources,
            new StatConditions.Subject(Value: n => n == "life" ? int.MaxValue : n == "mana" ? int.MaxValue - 500 : null)));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(closeResources,
            new StatConditions.Subject(Value: n => n == "life" ? int.MaxValue : n == "mana" ? 0 : null)));
        const string composed = "damage_+%_while_on_low_life_while_max_life_mana_within_500";
        foreach (bool lowLife in new[] { false, true })
            Assert.Equal(lowLife ? StatConditions.State.Holds : StatConditions.State.Fails, StatConditions.Of(composed,
                new StatConditions.Subject(Value: n => n is "life" or "mana" ? 1000 : null,
                    Flags: new Dictionary<string, bool> { ["_while_on_low_life"] = lowLife })));
        inventory.Add(PlayerInventories.Helmet, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!)
                with { Rarity = ItemRarity.Rare, Mods = [minimum("Strength10"), minimum("Intelligence10")] });
        wear("Atziri's Foible", 5); wear("Wurm's Molt", PlayerInventories.Belt);
        var scales = wear("The Scales of Justice", PlayerInventories.Offhand1);
        low = Read();
        Assert.True(Math.Abs((long)conn.Pools.Life - conn.Pools.Mana) <= 500,
            $"Actual Scales/Foible pools: {conn.Pools.Life}/{conn.Pools.Mana}");
        Assert.Equal(1, Get("unaffected_by_ignite")); Assert.Equal(1, Get("unaffected_by_shock"));
        conn.ElementalAilments.Clear();
        double ailmentAt = Math.Floor(conn.Clock.Elapsed.TotalSeconds);
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 100, 4, ailmentAt, Get, new PhasingRoll(99));
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Shock, 30, 4, ailmentAt, Get, new PhasingRoll(99));
        Assert.True(conn.ElementalAilments.Snapshot(ailmentAt).Ignite);
        Assert.Equal(30, conn.ElementalAilments.Snapshot(ailmentAt).Shock);
        Assert.Equal(1, conn.ElementalAilments.DamageTakenFactor(ailmentAt));
        Assert.Equal(0, conn.ElementalAilments.Advance(ailmentAt, ailmentAt + 1, Get, 0, 75, fireImmune: false));
        var heart = wear("Kaom's Heart", PlayerInventories.BodyArmour);
        low = Read();
        Assert.True(Math.Abs((long)conn.Pools.Life - conn.Pools.Mana) > 500);
        Assert.Equal(0, Get("unaffected_by_ignite")); Assert.Equal(0, Get("unaffected_by_shock"));
        Assert.Equal(1.3, conn.ElementalAilments.DamageTakenFactor(ailmentAt));
        Assert.Equal(130, conn.ElementalAilments.Advance(ailmentAt + 1, ailmentAt + 2, Get, 0, 75, fireImmune: false));
        inventory.Remove(PlayerInventories.BodyArmour, heart.Id);
        conn.Pools = Game.PoolsFor(conn, 100); low = Read();
        Assert.Equal(1, Get("unaffected_by_ignite"));
        inventory.Remove(PlayerInventories.Offhand1, scales.Id);
        conn.Pools = Game.PoolsFor(conn, 100); low = Read();
        Assert.Equal(0, Get("unaffected_by_ignite"));
        conn.ElementalAilments.Clear();

        var seeker = wear("Stormseeker", PlayerInventories.Gloves);
        conn.PlayerMana = (uint)Game.UnreservedMana(conn) / 2;
        low = Read();
        Game.ConfigureLeech(conn, Get, conn.PlayerStats, onLowLife: false);
        ailmentAt = Math.Floor(conn.Clock.Elapsed.TotalSeconds);
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Chill, 20, 100, ailmentAt, Get, new PhasingRoll(99));
        Assert.Equal(.8, conn.ElementalAilments.ActionFactor(ailmentAt));
        var leechTarget = new SpawnedMonster("Mana leech source", new(50, 50, 8), 60, 2, 1_000_000) { Hostile = true };
        conn.Clock.Start(); // Actual hit admission rejects paused owners; resume only for the synchronous commit.
        Game.LeechOnHitLocked(conn, leechTarget, DamagePacket.Of(DamageType.Physical, 100_000, 100_000),
            100_000, Game.HitLeechSources(low, attack: true));
        conn.Clock.Stop();
        low = Read();
        Assert.True(Game.LeechOf(conn).Active(conn.Clock.Elapsed.TotalSeconds).Mana);
        Assert.Equal(1, Get("unaffected_by_chill"));
        Assert.Equal(20, conn.ElementalAilments.Snapshot(ailmentAt).Chill);
        Assert.Equal(1, conn.ElementalAilments.ActionFactor(ailmentAt));
        inventory.Remove(PlayerInventories.Gloves, seeker.Id);
        conn.Pools = Game.PoolsFor(conn, 100); low = Read();
        Assert.True(Game.LeechOf(conn).Active(conn.Clock.Elapsed.TotalSeconds).Mana);
        Assert.Equal(0, Get("unaffected_by_chill"));
        Assert.Equal(.8, conn.ElementalAilments.ActionFactor(ailmentAt));
        wear("Stormseeker", PlayerInventories.Gloves);
        conn.PlayerMana = (uint)Game.UnreservedMana(conn); Game.TrimLeechLocked(conn); low = Read();
        Assert.False(Game.LeechOf(conn).Active(conn.Clock.Elapsed.TotalSeconds).Mana);
        Assert.Equal(0, Get("unaffected_by_chill"));
    }
}
