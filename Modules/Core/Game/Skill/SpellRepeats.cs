public static partial class Game
{
    // Spell Echo (PoE Wiki "Spell Echo", "Repeat"): a supported spell repeats as part of its one cast, with no second cost
    // or critical roll. The client's spell action base (0x1419e5460) keeps spell_repeat_count (0x912) at +0x2d4 and the
    // repeats left at +0x2d0, none for a Channel skill; at the animation's end (0x1419e6370) it takes one off and plays
    // the animation again, so the contact and its projectiles come again every cast time. The count is the client's
    // calculator 0x1427eaec0: base_spell_repeat_count + skill_repeat_count, + skill_repeat_count_if_mined on a mined
    // skill, none with disable_skill_repeats. Network stats run no calculators, so the skill panel sends it.
    // Ordinary Ice Nova starts one native ring per cast replay. Transfigured actions can expand from Frostbolts.
    public static int SpellRepeats(Player conn, int effect, Func<uint, int> get) =>
        !SkillsFor(conn).IsSpell(effect) || SkillsFor(conn).IsAttack(effect) || Supports.Of(effect)?.Types.Contains(47) == true
            || SkillsFor(conn).ActionHash(effect) == IceNovaRing.Action && SkillsFor(conn).EffectId(effect) is not ("IceNova" or "IceNovaHardMode")
            || get(Stat.DisableSkillRepeats) != 0 ? 0
            : Math.Max(0, get(Stat.BaseSpellRepeatCount) + get(Stat.SkillRepeatCount)
                + (get(Stat.SkillIsMined) != 0 ? get(Stat.SkillRepeatCountIfMined) : 0));

    public static double GreaterEchoHitMore(int repeat, Func<uint, int> own) => repeat <= 0 ? 1 :
        Math.Max(0, 1 + repeat * (own(Stat.SupportGreaterSpellEchoSpellDamagePlusPctFinalPerRepeat) / 100d));

    public static int SpellRepeats(Player conn, ushort skillId, int effect)
    {
        Dictionary<uint, int>? stats = null; // Only a repeatable spell reads them.
        return SpellRepeats(conn, effect, name => name is var k
            ? (stats ??= MergeSkillStats(CharacterPlayerStats(conn), SupportedSkill(conn, skillId).Stats)).GetValueOrDefault(k) : 0);
    }
}
