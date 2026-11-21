
public static partial class Game
{
    // The player's final stats for a skill's area (SkillArea): the panel total skill_area_of_effect_+% already holds the
    // base_skill_area_of_effect_+% of passives, items, /god and /set_aoe (PanelStats), so the base form is left out
    // here and only a skill's own base (gem quality) goes in when the skill's stats are laid over. SkillArea.Increased
    // sums both forms; with the player's base still in, every area skill counted it twice.
    public static Dictionary<uint, int> AreaPlayerStats(ClientState conn) =>
        SkillArea.WithoutPlayerBase(FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2), Stats.Key(SkillArea.BaseIncreased));

    // The stats every player skill's area reads: the player's stats without their base increased area (AreaPlayerStats,
    // or the snapshot a caller already holds with that base taken out the same way), the skill's own stats laid over, and
    // the increased area scoped to the skill's types (SkillArea.TypedIncrease) added to skill_area_of_effect_+%. Every
    // player skill's area goes through here, so no caller merges the player's base form a second time. Totem, trap and
    // mine come from the supported stats (is_totem, is_trap, is_remote_mine), so a support that makes one counts.
    public static Dictionary<uint, int> SkillAreaStats(ClientState conn, IReadOnlyDictionary<uint, int> own, int effect,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        var stats = MergeSkillStats(player is null ? AreaPlayerStats(conn)
            : SkillArea.WithoutPlayerBase(new(player), Stats.Key(SkillArea.BaseIncreased)), own);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var skills = SkillsFor(conn);
        bool known = effect >= 0;
        var types = new SkillArea.Types(known && skills.IsMelee(effect), known && skills.IsAttack(effect),
            known && skills.IsWarcry(effect), known && skills.IsVaal(effect),
            Get("is_totem") != 0, Get("is_trap") != 0, Get("is_remote_mine") != 0);
        int increased = SkillArea.TypedIncrease(Get, types);
        // ActiveSkills.ice_shot maps this input to increased area before the shared area-to-radius conversion.
        if (skills.EffectId(effect) == "IceShot")
            increased += Get("ice_shot_radius_+%");
        if (increased != 0 && Stats.Key(SkillArea.Increase) is uint total)
            stats[total] = stats.GetValueOrDefault(total) + increased;
        return stats;
    }

    // A player skill's radius in cells from baseRadius (its own active_skill_base_radius_+ added), through SkillAreaStats.
    public static int SkillRadius(ClientState conn, int baseRadius, IReadOnlyDictionary<uint, int> own, bool spell, int effect,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        var stats = SkillAreaStats(conn, own, effect, player);
        return SkillArea.Radius(baseRadius, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, spell);
    }
}
