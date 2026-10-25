namespace GameServer.Gateway.Tests;

public class AuraTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static AuraCatalog.Aura Aura(SkillCatalog skills, string effect, ushort buffRow, int flat, int percent) =>
        new(0x0103, skills.EffectRow(effect), effect, effect, 1, buffRow, flat, percent);

    [Fact]
    public void Simple_aura_reservations_come_from_GrantedEffectsPerLevel()
    {
        var skills = new SkillCatalog(Data());

        Assert.Equal((0, 5000), skills.Reservation("PurityOfElements", 1));
        Assert.Equal((0, 3500), skills.Reservation("PurityOfFire", 1));
        Assert.Equal((34, 0), skills.Reservation("Clarity", 1));
        Assert.Equal((28, 0), skills.Reservation("Vitality", 1));
        Assert.Equal((22, 0), skills.Reservation("Precision", 1));
        Assert.Equal((0, 5000), skills.Reservation("Haste", 1));
    }

    [Fact]
    public void Instant_skills_come_from_the_ActiveSkillType_Instant()
    {
        var skills = new SkillCatalog(Data());

        Assert.True(skills.IsInstant(skills.EffectRow("Haste")));
        Assert.True(skills.IsInstant(skills.EffectRow("RighteousFire")));
        Assert.False(skills.IsInstant(skills.EffectRow("Fireball")));
        Assert.False(skills.IsInstant(-1));

        uint instant = new StatCatalog(Data()).Key("skill_is_instant")!.Value;
        Assert.Contains((instant, 1), skills.EffectStats(skills.EffectRow("Haste"), 1));
        Assert.DoesNotContain(skills.EffectStats(skills.EffectRow("Fireball"), 1), s => s.Item1 == instant);
    }

    [Fact]
    public void A_skill_with_a_cooldown_carries_its_uses()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        uint uses = stats.Key("virtual_total_cooldown_count")!.Value;

        Assert.Contains((uses, 1), skills.EffectStats(skills.EffectRow("Haste"), 1));
        Assert.Contains((uses, 1), skills.EffectStats(skills.EffectRow("PurityOfElements"), 1));
        Assert.DoesNotContain(skills.EffectStats(skills.EffectRow("Fireball"), 1), s => s.Item1 == uses);

        Assert.Equal(1200, skills.CooldownMs(skills.EffectRow("Haste"), 1));
        Assert.Equal(300, skills.CooldownMs(skills.EffectRow("RighteousFire"), 1));
        Assert.Equal(0, skills.CooldownMs(skills.EffectRow("Fireball"), 1));
    }

    [Fact]
    public void Reservation_efficiency_divides_percent_and_flat_reservations()
    {
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0, ManaReservationEfficiencyPct: 50);
        var skills = new SkillCatalog(Data());
        var auras = new[]
        {
            Aura(skills, "PurityOfElements", 144, 0, 5000),
            Aura(skills, "Clarity", 155, 34, 0),
        };

        // 50% / 1.5 = 33.33%, rounded up to 34 mana; 34 flat / 1.5 = 23.
        Assert.Equal(57, AuraCatalog.ReservedMana(pools, auras));
        Assert.Equal(43, AuraCatalog.UnreservedMana(pools, auras));
    }

    [Fact]
    public void Purity_and_precision_effects_reach_the_player_bonus()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[]
        {
            Aura(skills, "PurityOfElements", 144, 0, 5000),
            Aura(skills, "Precision", 1293, 22, 0),
        };

        var bonus = AuraCatalog.Bonus(auras, 50, skills, stats);

        Assert.Equal(30, bonus.FireResistance);
        Assert.Equal(30, bonus.ColdResistance);
        Assert.Equal(30, bonus.LightningResistance);
        Assert.Equal(140, bonus.Accuracy);       // 93 * 1.5
        Assert.Equal(30, bonus.CriticalStrikeChancePct);
        Assert.Equal(30, bonus.SpellCriticalStrikeChancePct);
    }

    [Fact]
    public void Clarity_and_vitality_regeneration_are_aura_stats()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Clarity", 155, 34, 0), Aura(skills, "Vitality", 153, 28, 0) };
        var raw = AuraCatalog.RawStats(auras, 0, skills, stats).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(176, raw[stats.Key("base_mana_regeneration_rate_per_minute")!.Value]);
        Assert.Equal(600, raw[stats.Key("base_life_regeneration_rate_per_minute")!.Value]);
    }

    [Fact]
    public void Haste_grants_attack_movement_and_cast_speed()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Haste", 152, 0, 5000) };

        var bonus = AuraCatalog.Bonus(auras, 50, skills, stats);
        var raw = AuraCatalog.RawStats(auras, 50, skills, stats).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(23, bonus.AttackSpeedPct);       // 15% increased * 1.5
        Assert.Equal(15, bonus.MovementVelocityPct);  // 10% increased * 1.5
        Assert.Equal(23, raw[stats.Key("cast_speed_+%")!.Value]);
    }

    [Fact]
    public void Previously_rejected_auras_have_reservations_and_individual_buff_rows()
    {
        var skills = new SkillCatalog(Data());
        foreach (var (effect, row) in new (string, ushort)[]
        {
            ("Determination", 142), ("Grace", 143), ("Discipline", 148), ("Anger", 149),
            ("Hatred", 150), ("Wrath", 151), ("Zealotry", 1272), ("Malevolence", 1274), ("Envy", 828)
        })
        {
            Assert.True(AuraCatalog.Supported(effect));
            Assert.Equal(row, AuraCatalog.BuffRow(effect));
            var reservation = skills.Reservation(effect, 1);
            Assert.NotNull(reservation);
            Assert.True(reservation!.Value.Flat > 0 || reservation.Value.Percent > 0);
            var aura = Aura(skills, effect, row, reservation.Value.Flat, reservation.Value.Percent) with { SourceBuff = 2 };
            Assert.Equal(2, AuraCatalog.StartPackets(aura, 0x1000, skills.ActionHash(aura.Effect)).Count());
        }
    }

    [Fact]
    public void Reservation_validation_detects_overflow_instead_of_clamping_it_away()
    {
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0);
        var skills = new SkillCatalog(Data());
        var auras = new[] { Aura(skills, "Determination", 142, 0, 5000), Aura(skills, "Grace", 143, 0, 5000) };
        Assert.Equal(100, AuraCatalog.RequiredMana(pools, auras));
        Assert.Equal(150, AuraCatalog.RequiredMana(pools, auras.Append(Aura(skills, "Anger", 149, 0, 5000))));
        Assert.Equal(100, AuraCatalog.ReservedMana(pools, auras));
    }

    [Fact]
    public void Determination_and_grace_apply_flat_and_more_defences()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Determination", 142, 0, 5000), Aura(skills, "Grace", 143, 0, 5000) };
        var bonus = AuraCatalog.Bonus(auras, 0, skills, stats);
        Assert.Equal(90, bonus.Armour);
        Assert.Equal(40, bonus.ArmourMorePct);
        Assert.Equal(136, bonus.Evasion);
        Assert.Equal(20, bonus.EvasionMorePct);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus);
        Assert.Equal(126, pools.Armour);
        Assert.Equal((int)((15 + 136) * 1.02 * 1.2), pools.Evasion);
    }

    [Fact]
    public void Anger_damage_and_hatred_gain_as_cold_reach_attack_packets()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Anger", 149, 0, 5000), Aura(skills, "Hatred", 150, 0, 5000) };
        var raw = AuraCatalog.RawStats(auras, 0, skills, stats).ToDictionary(x => x.Key, x => x.Value);
        int Get(string name) => stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0;
        var hit = DamageStats.Added(GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), Get, false, melee: false)
            .Convert(DamageStats.Conversion(raw, stats, true));
        Assert.Equal(16, hit[GameServer.Core.World.DamageType.Fire].Min);
        Assert.Equal(23, hit[GameServer.Core.World.DamageType.Fire].Max);
        Assert.Equal(30, hit[GameServer.Core.World.DamageType.Cold].Min);
        Assert.Equal(100, hit[GameServer.Core.World.DamageType.Physical].Min);
    }

    [Fact]
    public void Spell_auras_respect_added_effectiveness_and_element_specific_more_damage()
    {
        var values = new Dictionary<string, int>
        {
            ["spell_minimum_added_fire_damage"] = 10, ["spell_maximum_added_fire_damage"] = 20,
            ["spell_damage_aura_spell_damage_+%_final"] = 10,
            ["wrath_aura_spell_lightning_damage_+%_final"] = 20
        };
        int Get(string name) => values.GetValueOrDefault(name);
        var hit = SpellDamage.Packet(GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Lightning, 100, 100), Get, 2);
        Assert.Equal(22, hit[GameServer.Core.World.DamageType.Fire].Min, 6);
        Assert.Equal(44, hit[GameServer.Core.World.DamageType.Fire].Max, 6);
        Assert.Equal(132, hit[GameServer.Core.World.DamageType.Lightning].Min, 6);
    }

    [Fact]
    public void Default_attack_totals_include_aura_damage_without_replacing_physical_damage()
    {
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var packet = GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100)
            .Add(GameServer.Core.World.DamageType.Fire, new(16, 23));
        var stats = weapon.Stats(totalHit: packet).ToDictionary(x => x.Item1, x => x.Item2);
        Assert.Equal(100, stats[StatKeys.MainHandMinPhysical]);
        Assert.Equal(116, stats[StatKeys.MainHandMinTotal]);
        Assert.Equal(123, stats[StatKeys.MainHandMaxTotal]);
    }

    [Fact]
    public void Only_the_implemented_auras_have_a_buff_row()
    {
        Assert.True(AuraCatalog.Supported("PurityOfElements"));
        Assert.True(AuraCatalog.Supported("Precision"));
        Assert.True(AuraCatalog.Supported("Haste"));
    }
}
