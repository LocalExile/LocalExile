using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(false, 98, 2520, 2470)]
    [InlineData(true, 239, 44750, 106953)]
    public void Selected_Burning_Arrow_creation_refresh_and_active_hand_use_one_raw_packet_and_the_emitted_attack_rate(
        bool modifiers, int expectedRate, int expectedPerUse, int expectedDps)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 3, baseClass: 2, choice: "Wildspeaker");
        var bow = Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!;
        var club = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/OneHandMace1")!;
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(bow));
        game.Items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(club));
        EquipBlueGems(game, "Burning Arrow");
        if (modifiers)
            foreach (var (name, value) in new (string, int)[]
            {
                ("main_hand_attack_speed_+%", 100), (ActionSpeed.Reduction, -18),
                (ActionSpeed.HasTailwind, 1), (ActionSpeed.TailwindEffect, 50),
                (DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1),
                (DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1),
                ("attack_damage_+%", 50), ("physical_damage_+%", 20), ("melee_damage_+%", 1000),
                ("attack_minimum_added_physical_damage", 6), ("attack_maximum_added_physical_damage", 12),
                (DamageStats.MaxManaAddedLightning, 6), ("maximum_mana", 1000), ("attack_damage_+%_final_from_rage", 30),
            }) game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        game.State.Pools = Program.PoolsFor(game.State, 1);
        var gem = game.GemEffects().Single(g => g.Effect == Program.Skills.EffectRow("BurningArrow"));
        var raw = Program.CharacterPlayerStats(game.State);
        var rawBefore = raw.OrderBy(kv => kv.Key).ToArray();
        var created = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State).Single(s => s.Id == gem.Id)
            .Stats!.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(714, created[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(1020, created[StatKeys.MainHandTotalAttackDurationMs]);
        Assert.Equal(expectedRate, created[StatKeys.HundredTimesAttacksPerSecond]);
        Assert.Equal(expectedPerUse, created[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        Assert.Equal(expectedDps, created[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal(0, created[StatKeys.MainHandMaxPhysical]);

        var weapon = ProgramValue<SkillCatalog.Unarmed>("WeaponFor", game.State);
        var packet = AttackSkillHit.Resolve(weapon, raw, Program.Skills.EffectStats(gem.Effect, gem.Level), Program.Stats,
            Program.Skills.DamageEffectiveness(gem.Effect, gem.Level), melee: false, Program.Skills.QualityStats(gem.Effect, gem.Quality),
            baseMultiplier: Program.Skills.AttackBaseMultiplier(gem.Effect, gem.Level));
        Assert.Equal(modifiers ? 403.4212 : 14, packet.MinTotal, 8);
        Assert.Equal(modifiers ? 491.582 : 36.4, packet.MaxTotal, 8);
        Assert.Equal((int)Math.Round(packet.AverageTotal * expectedRate), created[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal((int)Math.Floor(packet.MinTotal), created[StatKeys.MainHandMinTotal]);
        Assert.Equal((int)Math.Floor(packet.MaxTotal), created[StatKeys.MainHandMaxTotal]);
        Assert.Equal(rawBefore, raw.OrderBy(kv => kv.Key).ToArray());

        var inherited = ProgramValue<IEnumerable<(uint, int)>>("FinalPlayerStats", game.State).ToDictionary(s => s.Item1, s => s.Item2);
        foreach (var key in new[] { StatKeys.MainHandMinPhysical, StatKeys.MainHandMaxPhysical,
            StatKeys.MainHandMinTotal, StatKeys.MainHandMaxTotal, StatKeys.DisplayHundredTimesDamagePerSkillUse,
            StatKeys.HundredTimesDamagePerSecond }) inherited[key] = 999999;
        var inheritedBefore = inherited.OrderBy(kv => kv.Key).ToArray();
        var refreshed = ProgramValue<IEnumerable<GrantedSkill>>("GemSkills", game.State,
                inherited.Select(s => (s.Key, s.Value))).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(created.OrderBy(kv => kv.Key), refreshed.OrderBy(kv => kv.Key));
        Assert.Equal(inheritedBefore, inherited.OrderBy(kv => kv.Key).ToArray());

        game.Items.SwapWeaponSets();
        game.State.Pools = Program.PoolsFor(game.State, 1);
        var swapped = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State).Single(s => s.Id == gem.Id)
            .Stats!.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(690, swapped[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(986, swapped[StatKeys.MainHandTotalAttackDurationMs]);
        var activeRaw = Program.CharacterPlayerStats(game.State);
        var activePacket = AttackSkillHit.Resolve(ProgramValue<SkillCatalog.Unarmed>("WeaponFor", game.State), activeRaw,
            Program.Skills.EffectStats(gem.Effect, gem.Level), Program.Stats, Program.Skills.DamageEffectiveness(gem.Effect, gem.Level),
            melee: false, Program.Skills.QualityStats(gem.Effect, gem.Quality),
            baseMultiplier: Program.Skills.AttackBaseMultiplier(gem.Effect, gem.Level));
        Assert.Equal(modifiers ? 409.7184 : 16.8, activePacket.MinTotal, 8);
        Assert.Equal((int)Math.Floor(activePacket.MinTotal), swapped[StatKeys.MainHandMinTotal]);
        Assert.Equal((int)Math.Round(activePacket.AverageTotal * swapped[StatKeys.HundredTimesAttacksPerSecond]),
            swapped[StatKeys.HundredTimesDamagePerSecond]);
    }
}
