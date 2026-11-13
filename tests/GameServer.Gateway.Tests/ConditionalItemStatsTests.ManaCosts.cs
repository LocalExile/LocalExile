using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_flat_cost_sources_reach_skill_panels_and_payment(ClientState conn)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Weapon1, PlayerInventories.Offhand1];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        var unique = new UniqueCatalog(Program.Items);
        var definition = unique.Find("Watcher's Eye")!;
        var group = Assert.Single(definition.RollGroups!);
        const string modifier = "ClarityReducedManaCostNonChannelled";
        Assert.Contains(modifier, group.Options);
        int count = group.HighLevel is int threshold && definition.ItemLevel >= threshold
            ? group.HighLevelCount ?? group.Count : group.Count;
        string[] options = [modifier, .. group.Options.Where(id => !id.StartsWith("ClarityReducedManaCost", StringComparison.Ordinal)).Take(count - 1)];
        var source = unique.Create(definition with { RollGroups = [group with { Options = options }] }, new Random(504))!;
        int flat = ItemModStats.Sum([source], Program.Items.Mods)
            .GetValueOrDefault("skill_mana_cost_+_with_non_channelling_skills_while_affected_by_clarity");
        Assert.InRange(flat, -10, -5);
        ushort node = Program.Passives.JewelNodeForSlot(0)!.Value;
        bool allocated = conn.Passives.Contains(node); conn.Passives.Add(node);
        var jewel = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, source);
        Item Gem(string id) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + id)!);
        var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        var weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0, host with
        {
            Sockets = [new(SocketColour.White, Gem("SkillGemFireball")), new(SocketColour.White, Gem("SkillGemIncinerate")),
                new(SocketColour.White, Gem("SkillGemClarity"))],
            Links = [1, 1, 1],
        });
        var effects = Game.GemSkillEffects(conn).ToArray();
        var fireball = effects.Single(g => g.Effect == Program.Skills.EffectRow("Fireball"));
        var incinerate = effects.Single(g => Program.Skills.IsChannelled(g.Effect));
        uint increased = Key("mana_cost_+%"), blood = Key("keystone_blood_magic");
        conn.PlayerStats[increased] = 300;
        int Cost(ushort id) => Game.SupportedStats(conn, id)[Key("mana_cost")];
        int baseline = Cost(fireball.Id), channelBaseline = Cost(incinerate.Id);
        Assert.True(baseline >= 20);
        var clarity = effects.Single(g => g.Effect == Program.Skills.EffectRow("Clarity"));
        ushort auraId = clarity.Id;
        Assert.False(conn.Auras.ContainsKey(auraId));
        var aura = new AuraCatalog.Aura(auraId, clarity.Effect, "Clarity", "Clarity", 60000,
            AuraCatalog.BuffRow("Clarity"), 34, 0);
        conn.Auras[auraId] = aura;
        var captured = Game.CharacterPlayerStats(conn);
        Assert.True(captured.GetValueOrDefault(Key("skill_mana_cost_+_with_non_channelling_skills")) == flat,
            $"Clarity={string.Join(',', conn.Auras.Values.Select(a => a.EffectId))}, inactive={Game.IsInactive(conn, jewel.Id)}, " +
            string.Join(',', captured.Where(p => Program.Stats.Name(p.Key).Contains("cost", StringComparison.Ordinal))
                .Select(p => Program.Stats.Name(p.Key) + "=" + p.Value)));
        Assert.Equal(baseline + flat, Cost(fireball.Id));
        Assert.Equal(channelBaseline, Cost(incinerate.Id));
        var panel = Game.PlayerSkills(conn).Single(g => g.Id == fireball.Id);
        Assert.Equal(baseline + flat, panel.Stats!.Single(p => p.Item1 == Key("mana_cost")).Item2);
        conn.PlayerMana = (uint)(baseline + flat - 1);
        Assert.False(Game.PayMana(conn, fireball.Id, out _, out _));
        Assert.Equal((uint)(baseline + flat - 1), conn.PlayerMana);
        conn.PlayerMana = 1000;
        Assert.True(Game.PayMana(conn, fireball.Id, out uint paid, out uint life));
        Assert.Equal(((uint)(baseline + flat), 0u, (uint)(1000 - baseline - flat)), (paid, life, conn.PlayerMana));
        conn.PlayerStats[blood] = 1; conn.PlayerLife = 1000;
        Assert.True(Game.PayMana(conn, fireball.Id, out paid, out life));
        Assert.Equal((0u, (uint)(baseline + flat)), (paid, life));
        conn.PlayerStats.Remove(blood);
        conn.Auras.Remove(auraId); Assert.Equal(baseline, Cost(fireball.Id));
        conn.Auras[auraId] = aura;
        conn.Passives.Remove(node); Assert.Equal(baseline, Cost(fireball.Id));
        conn.Passives.Add(node); Assert.Equal(baseline + flat, Cost(fireball.Id));
        inventory.Remove(PlayerInventories.PassiveJewels, jewel.Id); Assert.Equal(baseline, Cost(fireball.Id));
        conn.Auras.Remove(auraId); conn.PlayerStats.Remove(increased);
        if (!allocated) conn.Passives.Remove(node);
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
        // Flat terms neither scale with supports/percentage reductions nor cross the channel classification.
        var raw = new Dictionary<string, int>
        {
            ["skill_mana_cost_+"] = -2, ["skill_mana_cost_+_with_non_channelling_skills"] = -5,
            ["skill_mana_cost_+_with_channelling_skills"] = -1, ["mana_cost_+%"] = 50,
            ["support_inspiration_mana_cost_+%_final"] = -40,
        };
        Assert.Equal(11, ManaRecoveryRules.Cost(20, raw.GetValueOrDefault, false));
        Assert.Equal(15, ManaRecoveryRules.Cost(20, raw.GetValueOrDefault, true));
        Assert.Equal(16, ManaRecoveryRules.Cost(20, raw.GetValueOrDefault));
        Assert.Equal(29, ManaRecoveryRules.Cost(20, raw.GetValueOrDefault, false, more: 2));
        Assert.Equal(0, ManaRecoveryRules.Cost(0, raw.GetValueOrDefault, false));
        raw["skill_mana_cost_+"] = int.MaxValue;
        raw["skill_mana_cost_+_with_non_channelling_skills"] = int.MaxValue;
        Assert.Equal(int.MaxValue, ManaRecoveryRules.Cost(int.MaxValue, raw.GetValueOrDefault, false));
    }
}
