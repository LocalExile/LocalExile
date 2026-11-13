using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Maximum_endurance_source_owns_onslaught_and_zero_maximum(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        var savedOverrides = conn.PlayerStats.ToArray();
        conn.PlayerStats.Clear(); conn.RecentEvents.Clear();
        var saved = inventory.EquipmentSnapshot().ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        var kill = Game.KillOnslaughtOf(conn);
        var savedKill = (kill.Until, kill.SentUntil, kill.Buff);
        kill.Until = kill.SentUntil = double.NegativeInfinity; kill.Buff = 0;
        wear("Astramentis", 5);
        wear("Lifesprig", PlayerInventories.Weapon1);
        var attributes = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!) with
        {
            Mods = [Program.Items.Mods.ById("Strength10")!.Roll(ModSection.Explicit, new Random(504))],
        };
        inventory.Add(6, 0, 0, attributes);
        var shield = wear("Ahn's Heritage", PlayerInventories.Offhand1);
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        Dictionary<uint, int> stats = [];
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Read() { conn.Pools = Game.PoolsFor(conn, 100); stats = Game.CharacterPlayerStats(conn); }
        Read(); conn.PlayerLife = conn.PlayerMaxLife;
        var charges = Game.ChargesOf(conn);
        void Count(int count)
        {
            charges.Admit(Game.ResourceEffectsTime(conn), [new ChargeEvent.Set(ChargeType.Endurance, count)],
                Get, () => Game.WeaponProfileFor(conn), true, new PhasingRoll(0));
            Read();
        }
        Assert.Equal(2, charges.Limits(ChargeType.Endurance).Maximum);
        Assert.Equal(1, ItemModStats.Sum([shield.Item], Program.Items.Mods)
            .GetValueOrDefault("gain_onslaught_while_at_maximum_endurance_charges"));
        Count(0);
        Assert.False(Game.HasOnslaught(conn));
        var baseline = conn.Pools;
        foreach (int count in new[] { 1, 2, 1, 2 })
        {
            Count(count);
            bool active = count == 2;
            Assert.Equal(active, Game.ChargeOnslaughtActive(conn));
            Assert.Equal(active, Game.HasOnslaught(conn));
            Assert.Equal(baseline.AttackSpeedPct + (active ? 20 : 0), conn.Pools.AttackSpeedPct);
            Assert.Equal(baseline.MovementVelocityPct + (active ? 20 : 0), conn.Pools.MovementVelocityPct);
            foreach (var (name, value) in Program.Items.Onslaught!.Stats)
                if (UtilityFlasks.SupportOf(name) != UtilityFlasks.Support.Unsupported)
                    Assert.Equal(active ? value : 0, Game.UtilityStats(conn, Get).Where(s => s.Stat == name).Sum(s => s.Value));
        }
        Assert.NotEmpty(Game.ChargeOnslaughtPresentation(conn)!);
        Assert.Null(Game.ChargeOnslaughtPresentation(conn));
        // An independent owner neither doubles the buff's stats nor disappears with this shield.
        kill.Until = Game.ResourceEffectsTime(conn) + 4;
        Assert.NotEmpty(Game.OnslaughtPresentation(conn));
        Assert.True(kill.Applied);
        Read();
        Assert.Equal(baseline.AttackSpeedPct + 20, conn.Pools.AttackSpeedPct);
        Assert.Null(Game.ChargeOnslaughtPresentation(conn)); // The combined publication already hid the duplicate icon.
        inventory.Remove(PlayerInventories.Offhand1, shield.Id); Read();
        Assert.False(Game.ChargeOnslaughtActive(conn)); Assert.True(Game.HasOnslaught(conn));
        kill.SentUntil = kill.Until = double.NegativeInfinity; Read();
        Assert.False(Game.HasOnslaught(conn));
        shield = inventory.Add(PlayerInventories.Offhand1, 0, 0, shield.Item);
        Read(); Assert.True(Game.ChargeOnslaughtActive(conn));
        var conversion = wear("Arn's Anguish", PlayerInventories.Belt);
        Read(); Assert.Equal(0, Get("current_endurance_charges"));
        Assert.False(Game.ChargeOnslaughtActive(conn));
        inventory.Remove(PlayerInventories.Belt, conversion.Id); Read();
        Assert.True(Game.ChargeOnslaughtActive(conn));
        Count(0);
        conn.PlayerStats[Key("base_minimum_endurance_charges")] = 2; Read();
        Assert.True(Game.ChargeOnslaughtActive(conn));
        conn.PlayerStats.Remove(Key("base_minimum_endurance_charges"));
        conn.PlayerStats[Key("max_endurance_charges")] = -3; Read();
        Assert.Equal(0, Get("current_endurance_charges"));
        Assert.Equal(0, charges.Limits(ChargeType.Endurance).Maximum);
        Assert.True(Game.ChargeOnslaughtActive(conn));
        using (Game.PlayerStatsScope(conn))
        {
            conn.PlayerStats[Key("onslaught_effect_+%")] = 50;
            stats = Game.CharacterPlayerStats(conn); // Do not explicitly rebuild Pools: this must invalidate the live view.
            Assert.Equal(baseline.AttackSpeedPct + 30, conn.Pools.AttackSpeedPct);
            Assert.Equal(baseline.MovementVelocityPct + 30, conn.Pools.MovementVelocityPct);
            conn.PlayerStats.Remove(Key("onslaught_effect_+%"));
            stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(baseline.AttackSpeedPct + 20, conn.Pools.AttackSpeedPct);
            Assert.Equal(baseline.MovementVelocityPct + 20, conn.Pools.MovementVelocityPct);
        }
        // Changed maxima reconcile before the same-snapshot condition; a zero maximum does not create a gain.
        Assert.False(conn.RecentEvents.Happened(StatConditions.GainedEnduranceCharge, Game.ResourceEffectsTime(conn)));
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        Game.CharacterPlayerStats(conn);
        Assert.True(Game.IsInactive(conn, shield.Id)); Assert.False(Game.ChargeOnslaughtActive(conn));
        conn.Experience = CharacterStats.ExperienceFor(100); Read(); Assert.True(Game.ChargeOnslaughtActive(conn));
        conn.PlayerLife = 0; Game.CharacterPlayerStats(conn); Assert.False(Game.ChargeOnslaughtActive(conn));
        conn.PlayerLife = conn.PlayerMaxLife; Read(); Assert.True(Game.ChargeOnslaughtActive(conn));
        inventory.Remove(PlayerInventories.Offhand1, shield.Id); Read(); Assert.False(Game.HasOnslaught(conn));
        Assert.NotEmpty(Game.ChargeOnslaughtPresentation(conn)!);
        conn.PlayerStats.Remove(Key("max_endurance_charges"));
        foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        (kill.Until, kill.SentUntil, kill.Buff) = savedKill;
        foreach (var (key, value) in savedOverrides) conn.PlayerStats[key] = value;
        Read();
    }
}
