using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed class NativeMinionAuraState
    {
        public PlayerMinions.Aura[] Auras = [];
        public double Until = double.PositiveInfinity;
        public Dictionary<byte, Item>? GuardianItems;
    }

    static readonly RuleTable<GolemState.Golem, NativeMinionAuraState> NativeMinionAuraStates = new();

    static bool SpectreAuraEffect(int effect) => effect is 1962 or 2001 or 2022 or 2034;

    static bool HasSpectreAura(GolemState.Golem fighter, double now) =>
        NativeMinionAuraStates.TryGetValue(fighter, out var state) && state.Auras.Length > 0 && now < state.Until;

    static PlayerMinions.Aura NativeMinionAura(string skill, ushort row, int radius,
        (uint Key, int Value)[] stats, bool displayAura = false)
    {
        var granted = ClientTables.Table("BuffDefinitions")[row].Ints("StatsKeys");
        var values = new uint[granted.Length];
        for (int i = 0; i < values.Length; i++)
        {
            uint key = (uint)granted[i] + 1;
            values[i] = displayAura && i == 0 ? 1 : unchecked((uint)Array.Find(stats, value => value.Key == key).Value);
        }
        return new(skill, row, radius, stats) { SharesWithAllies = true, BuffValues = values };
    }

    static bool ActivateNativeMinionAuraLocked(Player player, GolemState.Golem fighter, GolemState.Ability ability, double now)
    {
        if (ability.Effect != 5906 && (fighter.Definition.Kind != "Spectre" || !SpectreAuraEffect(ability.Effect))) return false;
        int Get(uint key) => GolemStat(ability.Stats, key);
        int radius = 60 + 60 * Get(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal) / 100;
        var aura = ability.Effect switch
        {
            1962 => NativeMinionAura("Haste", 152, radius,
                [(Stat.AttackSpeedPlusPct, Get(Stat.AttackSpeedPlusPctGrantedFromSkill)),
                 (Stat.CastSpeedPlusPct, Get(Stat.CastSpeedPlusPctGrantedFromSkill)),
                 (Stat.BaseMovementVelocityPlusPct, Get(Stat.BaseMovementVelocityPlusPct))], displayAura: true),
            2001 => NativeMinionAura("Determination", 142, radius,
                [(Stat.BasePhysicalDamageReductionRating, Get(Stat.BasePhysicalDamageReductionRating)),
                 (Stat.DeterminationAuraArmourPlusPctFinal, Get(Stat.DeterminationAuraArmourPlusPctFinal))], displayAura: true),
            2022 => NativeMinionAura("Hatred", 150, radius,
                [(Stat.PhysicalDamagePctToAddAsCold, Get(Stat.PhysicalDamagePctToAddAsCold)),
                 (Stat.HatredAuraColdDamagePlusPctFinal, Get(Stat.HatredAuraColdDamagePlusPctFinal))], displayAura: true),
            2034 => NativeMinionAura("Wrath", 151, radius,
                [(Stat.AttackMinimumAddedLightningDamage, Get(Stat.AttackMinimumAddedLightningDamage)),
                 (Stat.AttackMaximumAddedLightningDamage, Get(Stat.AttackMaximumAddedLightningDamage)),
                 (Stat.WrathAuraSpellLightningDamagePlusPctFinal, Get(Stat.WrathAuraSpellLightningDamagePlusPctFinal))], displayAura: true),
            _ => NativeMinionAura("LightningGolemWrath", 676, radius,
                [(Stat.AttackMinimumAddedLightningDamage, Get(Stat.AttackMinimumAddedLightningDamage)),
                 (Stat.AttackMaximumAddedLightningDamage, Get(Stat.AttackMaximumAddedLightningDamage)),
                 (Stat.SpellMinimumAddedLightningDamage, Get(Stat.SpellMinimumAddedLightningDamage)),
                 (Stat.SpellMaximumAddedLightningDamage, Get(Stat.SpellMaximumAddedLightningDamage))]),
        };
        var state = NativeMinionAuraStates.GetOrCreateValue(fighter);
        state.Auras = [aura];
        state.Until = ability.Effect == 5906 ? now + Get(Stat.BaseSkillEffectDuration) / 1000.0 : double.PositiveInfinity;
        if (ability.Effect == 5906) fighter.WrathUntil = state.Until;
        if (player.Minions.All.FirstOrDefault(minion => minion.Id == fighter.Id) is { } source)
            SyncSharedMinionAuras(player, source, state.Auras, now, state.Until);
        return true;
    }

    static void SyncNativeMinionAuras(Player player, PlayerMinions.Minion source, double now)
    {
        if (source.Kind is not ("Spectre" or "AnimatedGuardian" or "Lightning")
            || OwnedFighter(player, source.Id) is not { } fighter) return;
        if (source.Kind == "AnimatedGuardian") SyncGuardianItemAuras(player, fighter);
        if (!NativeMinionAuraStates.TryGetValue(fighter, out var state)) return;
        SyncSharedMinionAuras(player, source, state.Auras, now, state.Until);
    }

    static void SyncGuardianItemAuras(Player player, GolemState.Golem guardian)
    {
        var state = NativeMinionAuraStates.GetOrCreateValue(guardian);
        if (ReferenceEquals(state.GuardianItems, guardian.Equipment)) return;
        state.GuardianItems = guardian.Equipment;
        var local = ItemModStats.Sum(guardian.Equipment.Values, ItemsFor(player).Mods, local: true);
        int leer = GolemStat(local, Stat.LocalDisplayAuraDamagePlusPct);
        int dyingBreath = GolemStat(local, Stat.LocalDisplayAuraDamagePlusPctAlliesOnly);
        var auras = new List<PlayerMinions.Aura>(2);
        if (leer > 0) auras.Add(NativeMinionAura("LeerCast", 221, 75, [(Stat.DamagePlusPct, leer)]));
        if (dyingBreath > 0)
            auras.Add(NativeMinionAura("DyingBreath", 292, 75, [(Stat.DamagePlusPct, dyingBreath)]) with { AffectsSource = false });
        state.Auras = [.. auras];
    }
}
