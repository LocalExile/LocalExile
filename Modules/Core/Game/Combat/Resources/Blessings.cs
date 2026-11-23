public static partial class Game
{
    static bool IsBlessingSkill(SupportedGem skill) => GolemStat(skill.Stats, Stat.SkillIsBlessingSkill) != 0;

    static bool AuraHasStat(AuraCatalog.Aura aura, uint stat) => aura.SkillStats is { } own
        && own.Any(row => row.Key == stat && row.Value != 0);

    static bool IsBlessingAura(AuraCatalog.Aura aura) => AuraHasStat(aura, Stat.SkillIsBlessingSkill);

    static bool EternalBlessingAura(AuraCatalog.Aura aura) => aura.AffectsSelf
        && AuraHasStat(aura, Stat.SkillAuraAlsoDisablesNonBlessingManaReservationSkills);

    static bool OwnEternalBlessingActive(Player player) => player.Auras.Values.Any(EternalBlessingAura);

    static bool EternalBlessingDisables(Player player, ushort? skill) =>
        skill is ushort id && OwnEternalBlessingActive(player)
        && SupportedReservationCost(player, id) is { } cost && (cost.ManaFlat > 0 || cost.ManaPercent > 0)
        && !IsBlessingSkill(SupportedSkill(player, id));

    static void EndOtherBlessingsLocked(Player player)
    {
        foreach (var aura in player.Auras.Values.Where(IsBlessingAura).ToArray())
        {
            if (IsTemporaryAura(player, aura))
                RemoveTemporaryAura(player, TemporaryAuraStates.GetOrCreateValue(player), aura.SkillId);
            else
            {
                player.Auras.Remove(aura.SkillId);
                EndReservationRuntimeLocked(player, aura);
                CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, false);
            }
        }
    }

    static bool OrdinaryManaAura(AuraCatalog.Aura aura) => !aura.ReservesLife
        && (aura.ManaFlat > 0 || aura.ManaPercent > 0) && !IsBlessingAura(aura);

    // Keep their saved selection so the normal restore path re-enables them when the Blessing ends.
    static void SuspendManaReservedAurasLocked(Player player)
    {
        foreach (var aura in player.Auras.Values.Where(OrdinaryManaAura).ToArray())
        {
            player.Auras.Remove(aura.SkillId);
            EndReservationRuntimeLocked(player, aura);
        }
        SuspendPetrifiedBloodIfUnavailable(player);
    }
}
