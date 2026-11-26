using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CycloneAndChargeSupportsTests
{
    sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override double NextDouble() => 0;
    }

    [Fact]
    public void Cyclone_quality_and_charge_supports_apply_mechanics_end_to_end()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var items = Game.ItemsOf(player);
        uint weaponId = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        int extraMeleeRange = 0;
        using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player) && extraMeleeRange != 0) values[Stat.MeleeRangePlus] = extraMeleeRange;
            return values;
        });

        // 1. Cyclone quality scales additional melee strike range -> AoE without overwriting SkillAreaOfEffectPlusPct.
        ushort cycloneId = Assert.Single(f.Equip(("Cyclone", 20)));
        Dictionary<uint, int> PanelOf(ushort skillId) =>
            Game.PlayerSkills(player).Single(s => s.Id == skillId).Stats!.ToDictionary(s => s.Key, s => s.Value);
        extraMeleeRange = 4;
        player.MarkStatsDirty(StatInput.Passives);
        Assert.Equal(60, PanelOf(cycloneId)[Stat.SkillAreaOfEffectPlusPct]);
        items.Change(PlayerInventories.Weapon1, weaponId, item => item with
        {
            Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = 20 } }],
        });
        var panel20Q = PanelOf(cycloneId);
        Assert.Equal(80, panel20Q[Stat.SkillAreaOfEffectPlusPct]);
        Assert.Equal(21, panel20Q[Stat.ActiveSkillAreaOfEffectRadius]);

        // 2. Ice Bite adds flat cold once, expands per-Frenzy stats (via damage effectiveness), and grants Frenzy on frozen kill.
        ushort soloId = Assert.Single(f.Equip(("Frostbolt", 20)));
        var soloInputs = Game.SupportedHitInputsFor(player, soloId)!;
        ushort iceId = f.Equip(("Frostbolt", 20), ("Ice Bite Support", 20))
            .Single(id => Program.Skills.EffectId(Game.SupportedSkill(player, id).Effect) == "FrostBolt");
        var iceGem = Game.SupportedSkill(player, iceId);
        var iceInputs0 = Game.SupportedHitInputsFor(player, iceId)!;
        double addedCold0 = iceInputs0.Raw[DamageType.Cold].Min - soloInputs.Raw[DamageType.Cold].Min;
        double eff = addedCold0 / iceGem.Stats[Stat.GlobalMinimumAddedColdDamage];
        var charges = Game.ChargesOf(player);
        charges.Admit(1, [new ChargeEvent.Grant(ChargeType.Frenzy, 3, "test")],
            name => Game.CharacterPlayerStats(player).GetValueOrDefault(name), () => default, alive: true, new ZeroRandom());
        player.MarkStatsDirty(StatInput.Pools);
        var iceInputs3 = Game.SupportedHitInputsFor(player, iceId)!;
        Assert.Equal(iceInputs0.Raw[DamageType.Cold].Min + 3 * iceGem.Stats[Stat.MinimumAddedColdDamagePerFrenzyCharge] * eff,
            iceInputs3.Raw[DamageType.Cold].Min, 6);

        charges.Admit(2, [new ChargeEvent.Set(ChargeType.Frenzy, 0)], _ => 0, () => default, alive: true, new ZeroRandom());
        var frozen = new SpawnedMonster("frozen", new(105, 100, 0), 100, 3, 1000) { Hostile = true };
        frozen.Ailments.Inflict(DamagePacket.Of(DamageType.Cold, 10_000, 10_000), critical: true, threshold: 100,
            now: Game.AreaTime(player), _ => 0, _ => 0, new ZeroRandom());
        Game.ChargesOnKill(player, frozen, iceGem.Stats);
        charges.Step(Game.AreaTime(player), name => Game.CharacterPlayerStats(player).GetValueOrDefault(name),
            () => Game.WeaponProfileFor(player), alive: true, moving: false, inTown: false, new ZeroRandom());
        Assert.Equal(1, charges.Effective(ChargeType.Frenzy));

        // 3. PCoC scales tooltip DPS and ECoMS grants a single Endurance Charge on lethal melee stun without duplicate OnStun.
        ushort brandId = f.Equip(("Storm Brand", 20), ("Power Charge On Critical Support", 20))
            .Single(id => Program.Skills.EffectId(Game.SupportedSkill(player, id).Effect) == "StormBrand");
        int baseDps = PanelOf(brandId)[Stat.HundredTimesDamagePerSecond];
        charges.Admit(3, [new ChargeEvent.Grant(ChargeType.Power, 3, "test")],
            name => Game.CharacterPlayerStats(player).GetValueOrDefault(name), () => default, alive: true, new ZeroRandom());
        player.MarkStatsDirty(StatInput.Pools);
        Assert.True(PanelOf(brandId)[Stat.HundredTimesDamagePerSecond] > baseDps * 1.11);

        int onStunCount = 0;
        var deadMonster = new SpawnedMonster("lethal", new(105, 100, 0), 0, 0, 500)
        {
            Hostile = true, Dead = true, Life = 0, HitAnimations = [new(0, 2)],
        };
        var ecomsStats = new Dictionary<uint, int> { [Stat.GainEnduranceChargeOnMeleeStun] = 1 };
        var lethalStun = new StunHit(10_000, 0, 0, Melee: true, Owner: player, Skill: ecomsStats)
        {
            OnStun = () => onStunCount++,
        };
        Assert.False(HitReaction.TryFlinch(deadMonster, lethalStun, 4, new ZeroRandom()));
        Assert.Equal(1, onStunCount);
        charges.Step(4, name => Game.CharacterPlayerStats(player).GetValueOrDefault(name),
            () => Game.WeaponProfileFor(player), alive: true, moving: false, inTown: false, new ZeroRandom());
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
    }
}
