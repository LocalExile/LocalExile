using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Life_mana_and_fortify_sources_share_the_existing_onslaught_owner(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot().ToArray();
        var overrides = conn.PlayerStats.ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        conn.PlayerStats.Clear(); conn.PlayerLife = 1000; conn.Experience = CharacterStats.ExperienceFor(100);
        var kill = Game.KillOnslaughtOf(conn); var savedKill = (kill.Until, kill.SentUntil, kill.Buff);
        kill.Until = kill.SentUntil = double.NegativeInfinity; kill.Buff = 0;
        var fortification = Game.FortificationOf(conn);
        double now = Game.ResourceEffectsTime(conn);
        using var clock = fortification.Capture(now);
        Assert.Equal(0, fortification.Read(now).Current);
        Dictionary<uint, int> stats = [];
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Read() { conn.Pools = Game.PoolsFor(conn, 100); stats = Game.CharacterPlayerStats(conn); }
        try
        {
            wear("Astramentis", 5);
            foreach (byte slot in new byte[] { 6, 7 })
                inventory.Add(slot, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!) with
                {
                    Mods = [.. new[] { "Strength10", "Dexterity10", "Intelligence10" }
                        .Select(id => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504)))],
                });
            foreach (var (name, slot, input) in new[]
            {
                ("Dreadbeak", PlayerInventories.Weapon1, "gain_onslaught_while_on_low_life"),
                ("Dance of the Offered", PlayerInventories.Boots, "gain_onslaught_while_not_on_low_mana"),
                ("Omeyocan", PlayerInventories.Boots, "gain_onslaught_while_not_on_low_mana"),
                ("Perseverance", PlayerInventories.Belt, "gain_onslaught_while_you_have_fortify"),
            })
            {
                var source = wear(name, slot);
                Assert.Equal(1, ItemModStats.Sum([source.Item], Program.Items.Mods).GetValueOrDefault(input));
                void State(bool active)
                {
                    conn.PlayerLife = conn.PlayerMaxLife;
                    conn.PlayerMana = (uint)Math.Max(0, conn.Pools.Mana / 2);
                    fortification.Clear();
                    if (input.Contains("low_life"))
                        conn.PlayerLife = conn.PlayerMaxLife / 2 + (active ? 0u : 1u);
                    else if (input.Contains("low_mana"))
                        conn.PlayerMana += active ? 1u : 0u;
                    else if (active) Assert.True(fortification.State.GrantExplicitStacks(now, 5));
                    Read();
                }
                State(false); Assert.False(Game.HasOnslaught(conn));
                var baseline = conn.Pools;
                int attackGain = Game.WeaponProfileFor(conn).Unarmed ? 22 : 20;
                foreach (bool active in new[] { true, false, true })
                {
                    State(active);
                    Assert.Equal(active, Game.ChargeOnslaughtActive(conn));
                    Assert.Equal(active, Game.HasOnslaught(conn));
                    // Pools hold global speed; the unarmed controls additionally have 10% more attack speed.
                    Assert.True(baseline.AttackSpeedPct + (active ? attackGain : 0) == conn.Pools.AttackSpeedPct,
                        $"{name} active={active} baseline={baseline.AttackSpeedPct} final={conn.Pools.AttackSpeedPct} "
                        + $"raw={Get("attack_speed_+%")} local={Game.WeaponFor(conn).LocalSources?.GetValueOrDefault("local_attack_speed_+%")} "
                        + $"unarmed={Game.WeaponProfileFor(conn).Unarmed} dual={Game.WeaponProfileFor(conn).DualWield}");
                    Assert.Equal(baseline.MovementVelocityPct + (active ? 20 : 0), conn.Pools.MovementVelocityPct);
                    Assert.Equal(active ? 20 : 0, Game.UtilityStats(conn, Get).Where(s => s.Stat == "base_cast_speed_+%").Sum(s => s.Value));
                }
                kill.Until = now + 4; Read();
                Assert.Equal(baseline.AttackSpeedPct + attackGain, conn.Pools.AttackSpeedPct);
                kill.Until = double.NegativeInfinity;
                conn.PlayerLife = 0; Read(); Assert.False(Game.HasOnslaught(conn)); State(true);
                inventory.Remove(slot, source.Id); Read(); Assert.False(Game.HasOnslaught(conn));
                source = inventory.Add(slot, 0, 0, source.Item); Read(); State(true);
                Assert.True(Game.HasOnslaught(conn), name + " restored against the new maximum pools");
                conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
                Game.CharacterPlayerStats(conn); Assert.True(Game.IsInactive(conn, source.Id)); Assert.False(Game.HasOnslaught(conn));
                conn.Experience = CharacterStats.ExperienceFor(100); Read(); State(true); Assert.True(Game.HasOnslaught(conn));
                if (name == "Dreadbeak")
                {
                    State(false); Assert.False(Game.HasOnslaught(conn));
                    var curse = wear("Coward's Legacy", PlayerInventories.Belt);
                    conn.PlayerLife = conn.PlayerMaxLife; Read(); Assert.True(Game.HasOnslaught(conn));
                    inventory.Remove(PlayerInventories.Belt, curse.Id); Read(); Assert.False(Game.HasOnslaught(conn));
                }
                inventory.Remove(slot, source.Id); State(false); Assert.False(Game.HasOnslaught(conn));
            }
        }
        finally
        {
            fortification.Clear();
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.PlayerStats.Clear(); foreach (var p in overrides) conn.PlayerStats[p.Key] = p.Value;
            (kill.Until, kill.SentUntil, kill.Buff) = savedKill;
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = (uint)conn.Pools.Mana; Game.CharacterPlayerStats(conn);
        }
    }
}
