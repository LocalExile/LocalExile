using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string FoulgraspEffect = "TriggeredSupportFoulgrasp";
    const string FoulgraspHandAo = "Metadata/Effects/Spells/monsters_effects/league_chayula/graftsystem/active_esh_03/breach_arm_01.ao";
    const string FoulgraspBurstAo = "Metadata/Effects/Spells/monsters_effects/league_chayula/graftsystem/active_esh_03/orb_explosion_01.ao";

    sealed record FoulgraspHand(uint Object, ushort Skill, SupportedGem Gem, (int X, int Y) At, int Radius, double Ends);

    static ushort? FoulgraspForRecallLocked(Player player, ActiveBrand brand)
    {
        const uint trigger = Stat.SupportTriggerEnervatingGraspOnSupportedBrandRecallChancePct;
        if (brand.Gem.Stats.GetValueOrDefault(trigger) <= 0
            || brand.Gem.Supports.FirstOrDefault(s => SupportHas(s, trigger)) is not { } support
            || !SameTriggerSource(brand.Gem, SupportedSkill(player, brand.SkillId))
            || !RandomRolls.Chance(brand.Gem.Stats.GetValueOrDefault(trigger), 100, AreaRng(player)))
            return null;

        return SupportCompanionSourceLocked(player, brand.Gem, support, FoulgraspEffect, "HungryLoopSupportedByFoulgrasp")?.Id;
    }

    static bool TryReleaseFoulgraspLocked(Player player, BrandState state, ushort skill, int brands,
        (int X, int Y) centre, double now, IReadOnlyDictionary<uint, int> playerStats, WorldRecordBuilder bytes,
        out uint mana, out uint life)
    {
        mana = life = 0;
        var gem = SupportedSkill(player, skill);
        if (SkillsFor(player).EffectId(gem.Effect) != FoulgraspEffect
            || GemSkillInstanceDisabled(player, gem.Effect, skill, triggered: true) is not null
            || CooldownForSkill(player, skill) is not { } plan)
            return false;
        int key = CooldownKey(player, gem.Effect, skill);
        var cooldown = CooldownStateLocked(player, key, plan, now);
        if (!cooldown.Ready) return false;

        int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
        int limit = Math.Max(0, Own(Stat.GraftSkillEshLightningHandsMaximumHands));
        long requested = (long)Own(Stat.GraftSkillEshLightningHandsNumberOfHandsSpawned)
            + (long)brands * Own(Stat.GraftSkillEshLightningHandsNumberOfAdditionalHandsSpawnedPerBrand);
        int count = (int)Math.Clamp(requested, 0, limit);
        int spread = SkillRadius(player, gem.Stats, spell: true, gem.Effect);
        int radius = SkillSecondaryRadius(player, gem.Stats, spell: true, gem.Effect);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), playerStats.GetValueOrDefault, Own);
        if (count == 0 || spread <= 0 || radius <= 0 || duration <= 0) return false;
        var cells = FoulgraspCells(player, centre, spread, count);
        if (cells.Count == 0 || !PayMana(player, skill, out mana, out life)) return false;
        cooldown.TryUse(now, plan);
        player.CooldownSources[gem.Effect] = skill;
        player.SkillReadyAt[key] = now + cooldown.RemainingSeconds;
        cooldown.ConfirmNativeUse(cooldown.UseVersion, skill, synchronizeProgress: true);

        ushort sequence = NextLockstepSequence(player);
        if (!player.Connection.LockstepClient) sequence &= 0x7fff;
        bytes.World(player.Id, InstanceProtocol.InstantAction(player.Id, skill, centre.X, centre.Y,
            sequence: sequence, flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true),
            $"0x01ce Foulgrasp at {centre}");

        foreach (var at in cells)
        {
            if (state.FoulgraspHands.Count >= limit)
            {
                bytes.Remove(state.FoulgraspHands[0].Object);
                state.FoulgraspHands.RemoveAt(0);
            }
            uint id = player.TakeObjectId();
            state.FoulgraspHands.Add(new(id, skill, gem, at, radius, now + duration));
            bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, FoulgraspHandAo) { Flags0 = EffectObject.FixedRotation, Awake = true }.Blob()),
                "Foulgrasp hand");
        }
        return true;
    }

    static List<(int X, int Y)> FoulgraspCells(Player player, (int X, int Y) centre, int spread, int count)
    {
        var random = AreaRng(player);
        List<(int X, int Y)> cells = new(count);
        for (int attempt = 0; cells.Count < count && attempt < count * 8; attempt++)
        {
            double angle = Math.Tau * random.NextDouble();
            double distance = spread * Math.Sqrt(random.NextDouble());
            var at = (X: centre.X + (int)Math.Round(distance * Math.Cos(angle)),
                Y: centre.Y + (int)Math.Round(distance * Math.Sin(angle)));
            if (!cells.Contains(at) && Dist(centre, at) <= spread && player.Layout.WalkableFor(at.X, at.Y, 0)
                && !player.Barrier.Contains(at) && FoulgraspClearSight(player, centre, at))
                cells.Add(at);
        }
        return cells;
    }

    static bool FoulgraspClearSight(Player player, (int X, int Y) from, (int X, int Y) to) =>
        player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(from, to, sight, 1);

    static void TickFoulgraspHandsLocked(Player player, BrandState state, double now,
        IReadOnlyDictionary<uint, int> playerStats, WorldRecordBuilder bytes)
    {
        for (int i = 0; i < state.FoulgraspHands.Count;)
        {
            var hand = state.FoulgraspHands[i];
            if (now >= hand.Ends)
            {
                state.FoulgraspHands.RemoveAt(i);
                bytes.Remove(hand.Object);
                continue;
            }

            bool InRange(KeyValuePair<uint, SpawnedMonster> entry)
            {
                var (id, monster) = entry;
                if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Invulnerable: false, WorldHidden: false }
                    || MonsterAsleep(player, id))
                    return false;
                var at = monster.Walk.Position(now);
                return Dist(at, hand.At) <= hand.Radius + LivingCollision.Size(monster)
                    && FoulgraspClearSight(player, hand.At, at);
            }

            var targets = player.Area.MonstersNear(hand.At, hand.Radius).Where(InRange).ToArray();
            if (targets.Length == 0)
            {
                i++;
                continue;
            }
            state.FoulgraspHands.RemoveAt(i);
            bytes.Remove(hand.Object);
            SpawnTempEffectLocked(player, state, hand.At, FoulgraspBurstAo, now + .5, bytes, hand.Radius / 15f);
            var multipliers = BrandHitMultipliers(playerStats, hand.Gem);
            foreach (var (id, target) in targets)
                DealBrandSpellHitLocked(player, hand.Skill, hand.Gem, playerStats, multipliers, id, target, now,
                    hitMore: 1, ailmentMore: 1, label: "Foulgrasp");
        }
    }
}
