
public static partial class Game
{
    // The player's final stats for a skill's area (SkillArea): the panel total skill_area_of_effect_+% already holds the
    // base_skill_area_of_effect_+% of passives, items, /god and /set_aoe (PanelStats), so the base form is left out
    // here and only a skill's own base (gem quality) goes in when the skill's stats are laid over. SkillArea.Increased
    // sums both forms; with the player's base still in, every area skill counted it twice.
    public static Dictionary<uint, int> AreaPlayerStats(Player player) =>
        SkillArea.WithoutPlayerBase(FinalPlayerStats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2), SkillArea.BaseIncreased);

    // The stats every player skill's area reads: the player's stats without their base increased area (AreaPlayerStats,
    // or the snapshot a caller already holds with that base taken out the same way), the skill's own stats laid over, and
    // the increased area scoped to the skill's types (SkillArea.TypedIncrease) added to skill_area_of_effect_+%. Every
    // player skill's area goes through here, so no caller merges the player's base form a second time. Totem, trap and
    // mine come from the supported stats (is_totem, is_trap, is_remote_mine), so a support that makes one counts.
    public static Dictionary<uint, int> SkillAreaStats(Player player, IReadOnlyDictionary<uint, int> own, int effect,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var stats = MergeSkillStats(playerStats is null ? AreaPlayerStats(player)
            : SkillArea.WithoutPlayerBase(new(playerStats), SkillArea.BaseIncreased), own);
        int Get(uint key) => stats.GetValueOrDefault(key);
        var skills = SkillsFor(player);
        bool known = effect >= 0;
        var types = new SkillArea.Types(known && skills.IsMelee(effect), known && skills.IsAttack(effect),
            known && skills.IsWarcry(effect), known && skills.IsVaal(effect),
            Get(Stat.IsTotem) != 0, Get(Stat.IsTrap) != 0, Get(Stat.IsRemoteMine) != 0);
        if (Get(Stat.GladiatorAreaOfEffectPlusPctFinalWhileWieldingMace) != 0
            && !WeaponProfileFor(player, activeSet: true).Mace
            && ActiveMainHand(player)?.Type?.Class != "Sceptre" && ActiveOffHand(player)?.Type?.Class != "Sceptre")
            stats.Remove(Stat.GladiatorAreaOfEffectPlusPctFinalWhileWieldingMace);
        int typed = SkillArea.TypedIncrease(Get, types);
        if (Get(Stat.RetaliationSkillAreaOfEffectPlusPct) is int retaliationArea and not 0 && known && skills.IsRetaliation(effect))
            typed = ActorScale.Saturate((long)typed + retaliationArea);
        if (known && skills.EffectId(effect) is { } id)
        {
            if (IsHeraldAura(id)) typed += Get(Stat.AreaOfEffectPlusPctWithHeraldSkills);
            if (IsDischarge(id) && Get(Stat.DischargeAreaOfEffectPlusPctFinal) is int dischargeArea and not 0)
            {
                const uint more = Stat.SkillAreaOfEffectPlusPctFinalFromSkillSpecificStat;
                stats[more] = ActorScale.Saturate(Math.Max(0L, 100L + Get(more))
                    * Math.Max(0L, 100L + dischargeArea) / 100 - 100);
            }
            if (IsGalvanicArrow(id))
            {
                typed += Get(Stat.ShrapnelShotRadiusPlusPct);
                if (Get(Stat.AdditiveArrowSpeedModifiersApplyToAreaOfEffect) != 0)
                    typed += stats.GetValueOrDefault(Stat.ProjectileSpeedPlusPct)
                        + own.GetValueOrDefault(Stat.BaseProjectileSpeedPlusPct)
                        + Get(Stat.GalvanicArrowProjectileSpeedPlusPct);
            }
        }
        if (Get(Stat.AreaOfEffectPlusPctWhileWieldingBow) is int bow and not 0 && WeaponFor(player).Bow)
            typed += bow;
        if (Get(Stat.AoePlusPctPerSecondWhileStationaryUpTo50) is int stationaryRate and > 0)
        {
            StatGraph.ReadInput((int)StatInput.Walk);
            double now = AreaTime(player), rest = player.Walk.ArrivesAt;
            double fullSeconds = Math.Max(0, Math.Floor(now - rest));
            int increase = (int)Math.Min(50, fullSeconds * stationaryRate);
            if (increase < 50) StatsChangeIn(rest + fullSeconds + 1 - now);
            typed = ActorScale.Saturate((long)typed + increase);
        }
        if (Get(Stat.CycloneAreaOfEffectPlusPctPerAdditionalMeleeRange) is int perRange and > 0)
        {
            int additional = Math.Max(0, CycloneAdditionalMeleeRange(player, effect, own));
            if (additional > 0)
            {
                int bonus = ActorScale.Saturate((long)additional * perRange);
                stats[Stat.VirtualCycloneSkillAreaOfEffectPlusPctFromMeleeRange] = bonus;
                typed = ActorScale.Saturate((long)typed + bonus);
            }
        }
        if (typed != 0)
            stats[SkillArea.Increase] = stats.GetValueOrDefault(SkillArea.Increase) + typed;
        if (Get(Stat.StygianSpireTotemAreaOfEffectPlusPctFinalPerMaximumTotem) is int totemMore and not 0)
            stats[Stat.StygianSpireTotemAreaOfEffectPlusPctFinalPerMaximumTotem] = types.Totem
                ? ActorScale.Saturate((long)totemMore * TotemAllowance(player, effect, own, stats)) : 0;
        return stats;
    }

    static int CycloneAdditionalMeleeRange(Player player, int effect, IReadOnlyDictionary<uint, int> own)
    {
        var weapon = WeaponFor(player);
        if (!weapon.Melee) return 0;
        int Additional(SkillCatalog.Unarmed w) =>
            MeleeSkillReach(player, w, effect, own) - w.Range + (w.LocalSources?.GetValueOrDefault(Stat.LocalWeaponRangePlus) ?? 0);
        int main = Additional(weapon);
        if (OffhandWeaponFor(player) is { Melee: true } off)
            return (main + Additional(off)) / 2;
        return main;
    }

    // A player skill's radius in cells from baseRadius (its own active_skill_base_radius_+ added), through SkillAreaStats.
    public static int SkillRadius(Player player, int baseRadius, IReadOnlyDictionary<uint, int> own, bool spell, int effect,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var stats = SkillAreaStats(player, own, effect, playerStats);
        return SkillArea.Radius(baseRadius, key => stats.GetValueOrDefault(key), spell);
    }
}
