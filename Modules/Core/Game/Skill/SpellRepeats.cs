static partial class Game
{
    // Spell Echo (PoE Wiki "Spell Echo", "Repeat"): a supported spell repeats as part of its one cast, with no second cost
    // or critical roll. The client's spell action base (0x1419e2a50) keeps spell_repeat_count (0x912) at +0x2d4 and the
    // repeats left at +0x2d0, none for a Channel skill; at the animation's end (0x1419e3960) it takes one off and plays
    // the animation again, so the contact and its projectiles come again every cast time. The count is the client's
    // calculator 0x1427e4310: base_spell_repeat_count + skill_repeat_count, + skill_repeat_count_if_mined on a mined
    // skill, none with disable_skill_repeats. Network stats run no calculators, so the skill panel sends it.
    // shortcut: Ice Nova's action reads the count again for further rings 0.3 s apart (0x141c00180); it gets no repeats
    // until that is read, so no nova is drawn that the server does not hit with. Game_ICE_NOVA_REPEATS=1 sends it anyway,
    // for scripts/re/frida/trace-ice-nova-repeat.js only.
    internal static int SpellRepeats(ClientState conn, int effect, Func<string, int> get) =>
        !SkillsFor(conn).IsSpell(effect) || SkillsFor(conn).IsAttack(effect) || Supports.Of(effect)?.Types.Contains(47) == true
            || SkillsFor(conn).ActionHash(effect) == IceNovaRing.Action && Environment.GetEnvironmentVariable("Game_ICE_NOVA_REPEATS") != "1"
            || get("disable_skill_repeats") != 0 ? 0
            : Math.Max(0, get("base_spell_repeat_count") + get("skill_repeat_count")
                + (get("skill_is_mined") != 0 ? get("skill_repeat_count_if_mined") : 0));

    internal static int SpellRepeats(ClientState conn, ushort skillId, int effect)
    {
        Dictionary<uint, int>? stats = null; // Only a repeatable spell reads them.
        return SpellRepeats(conn, effect, name => Stats.Key(name) is uint k
            ? (stats ??= MergeSkillStats(CharacterPlayerStats(conn), SupportedSkill(conn, skillId).Stats)).GetValueOrDefault(k) : 0);
    }
}
