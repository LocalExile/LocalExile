using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // BuffDefinitions skill_location_memory (invisible, CancelOnDeath, behaviour RecordSkillUseLocations): the client keeps a
    // Strike skill's previous locations in it, and its melee action (0x141c9d7c0) finds it by the skill id and ActionTypes
    // hash of its bit-3 association. Its behaviour (0x142060950) takes its first entry from the message's u32 list unchecked:
    // the use's sequence, the player's cell and the target cell; later uses it records itself as they start (0x142060ba0).
    const ushort SkillLocationMemoryBuffRow = 1451;

    // A Strike use's extra targets, decided as its swing starts, as the client decides them when its melee action starts
    // (StrikeTargets): the spirit strikes (Ancestral Reach) and the previous location to strike again (Ancestral Echo),
    // whose use also records this one. The returned message, when not null, adds the skill's skill_location_memory buff.
    // A non-Strike gets nothing. Call in the area's frame.
    static (List<uint> Spirit, (int X, int Y)? Echo, byte[]? MemoryBuff) StrikeExtrasLocked(Player player, ushort skillId,
        int effect, IReadOnlyDictionary<uint, int> ownStats, (int X, int Y) playerAt, (int X, int Y) centre, ushort sequence,
        uint mainTarget, double now)
    {
        var skills = SkillsFor(player);
        if (effect < 0 || !skills.IsStrike(effect)) return ([], null, null);
        var stats = MergeSkillStats(CharacterPlayerStats(player), ownStats);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int count = StrikeTargets.SpiritStrikes(Get, strike: true, skills.IsVaal(effect), MeleeAttack(player, effect));
        var spirit = StrikeTargets.Pick(player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                .Select(e => new StrikeTargets.Candidate(e.Key, e.Value.Walk.Position(now), LivingCollision.Size(e.Value))),
            playerAt, StrikeTargets.Range(Get(Stat.ExtraTargetTargetingDistancePlusPct)), count, sequence, player.Id,
            mainTarget == 0 ? new HashSet<uint>() : [mainTarget]);
        if (!StrikeTargets.StrikesPreviousLocation(Get, strike: true, WeaponProfileFor(player, activeSet: true).Melee))
            return (spirit, null, null);
        var echo = player.StrikeLocations.Use(skillId, playerAt, centre);
        var (_, added) = player.StrikeLocations.Buff(skillId, () => 1);
        // One memory buff per skill (its tag): where it was used.
        if (added)
            player.Area.Buffs.Set(player.Id, new(SkillLocationMemoryBuffRow, player.Id, skillId,
                ActiveSkill: skills.ActionHash(effect) is ushort hash ? (skillId, hash) : null,
                List: [sequence, (uint)playerAt.X, (uint)playerAt.Y, (uint)centre.X, (uint)centre.Y], Tag: skillId));
        return (spirit, echo, null);
    }
}
