/// <summary>Commander of Darkness (Necromancer ascendancy): the three values the node's stats carry, read from the
/// caster's raw passive and gear sources (CharacterStatSources) so the aura reconciliation can stamp them onto each
/// running aura (AuraCatalog.Aura's Commander* fields), which is how the allies and minions the aura reaches learn
/// them. Stats.dat rows 3773, 4817, 4482 (PassiveSkills 1514, values 3/30/30); a stat key is its row + 1.</summary>
static partial class Game
{
    /// <summary>The caster's Commander of Darkness values from its raw sources: the speed each of its auras grants,
    /// the elemental resistance and the damage for anyone its auras affect. 0 for a value the caster does not have
    /// (the node is not allocated, or the stat key is absent).</summary>
    internal static (int Speed, int Resistance, int Damage) CommanderOfDarkness(IReadOnlyDictionary<uint, int> sources)
    {
        return (sources.GetValueOrDefault(Stat.AurasGrantAttackAndCastSpeedPlusPctToYouAndYourAllies),
            sources.GetValueOrDefault(Stat.ElementalResistancesPlusPctForYouAndAlliesAffectedByYourAuras),
            sources.GetValueOrDefault(Stat.DamagePlusPctForYouAndAlliesAffectedByYourAuras));
    }

    // The Sundered Will: increases and reductions to chaos damage (chaos_damage_+%) also apply to the effect of auras
    // from chaos skills at additive_chaos_damage_modifiers_apply_to_chaos_aura_effect_at_%_value of their value (two
    // rings add), the result capped at 150% (and at -150% for reductions).
    internal static int ChaosDamageAuraEffectPct(IReadOnlyDictionary<uint, int> sources)
    {
        int share = sources.GetValueOrDefault(Stat.AdditiveChaosDamageModifiersApplyToChaosAuraEffectAtPctValue);
        if (share == 0) return 0;
        long value = (long)sources.GetValueOrDefault(Stat.ChaosDamagePlusPct) * share / 100;
        return (int)Math.Clamp(value, -150, 150);
    }

    static int ChaosDamageAuraEffectPct(Player player, AuraCatalog.Aura aura, IReadOnlyDictionary<uint, int> sources) =>
        SkillsFor(player).HasType(aura.Effect, "Chaos")
            && (SkillsFor(player).IsAura(aura.Effect) || CurseCatalog.Hex(aura.EffectId))
            ? ChaosDamageAuraEffectPct(sources) : 0;

    internal static AuraCatalog.Aura WithCasterAuraStamps(Player player, AuraCatalog.Aura aura, IReadOnlyDictionary<uint, int> sources)
    {
        var commander = CommanderOfDarkness(sources);
        bool supreme = sources.GetValueOrDefault(Stat.KeystoneSupremeEgo) != 0 && SkillsFor(player).IsAura(aura.Effect);
        int more = supreme && player.Pools.Mana > 0
            ? (int)Math.Min(50, (long)AuraCatalog.RequiredMana(player.Pools, [aura]) * 50 / player.Pools.Mana) : 0;
        int perAura = AuraCatalog.PerAuraEffectPctFor(aura, sources) + ChaosDamageAuraEffectPct(player, aura, sources);
        if (aura.SelfOnly == supreme && aura.MoreEffectPct == more && aura.PerAuraEffectPct == perAura
            && aura.CommanderAttackCastSpeedPct == commander.Speed
            && aura.CommanderElementalResistancePct == commander.Resistance && aura.CommanderDamagePct == commander.Damage)
            return aura;
        if (GameServer.Core.Diagnostics.ServerTrace.Skills && aura.PerAuraEffectPct != perAura)
            Console.WriteLine($"[gateway] aura {aura.EffectId}: per-aura effect {perAura}% (chaos damage share {ChaosDamageAuraEffectPct(player, aura, sources)}%)");
        return aura with
        {
            SelfOnly = supreme,
            MoreEffectPct = more,
            PerAuraEffectPct = perAura,
            CommanderAttackCastSpeedPct = commander.Speed,
            CommanderElementalResistancePct = commander.Resistance,
            CommanderDamagePct = commander.Damage,
        };
    }
}
