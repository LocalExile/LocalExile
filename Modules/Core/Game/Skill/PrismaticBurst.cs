using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string PrismaticBurstEffect = "PrismaticBurst";
    static readonly DamageType[] PrismaticBurstElements = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];

    // The 3.29.3c trigger rolls a weighted reservoir in fire, cold, lightning order (FUN_141b3db10).
    // Its tooltip uses the same squared attributes and the same three Combat Focus exclusions.
    internal static int PrismaticBurstElementIndex(uint owner, ushort sequence, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> skillStats)
    {
        Span<int> attributes =
        [
            playerStats.GetValueOrDefault(Stat.Strength),
            playerStats.GetValueOrDefault(Stat.Dexterity),
            playerStats.GetValueOrDefault(Stat.Intelligence),
        ];
        Span<uint> weights = stackalloc uint[3];
        bool anyWeight = false;
        for (int i = 0; i < weights.Length; i++)
        {
            uint ban = ElementalHitBans[i];
            weights[i] = playerStats.GetValueOrDefault(ban) != 0 || skillStats.GetValueOrDefault(ban) != 0
                ? 0 : unchecked((uint)(attributes[i] * attributes[i]));
            anyWeight |= weights[i] != 0;
        }
        // The native instant-action handler gives a triggered cast sub-id 1.
        var random = new AnimationVariantSelector.NativeRandom([sequence, 1, owner, 0, 0, 0, 0, 0]);
        uint total = 0;
        int selected = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            uint weight = weights[i];
            if (!anyWeight && playerStats.GetValueOrDefault(ElementalHitBans[i]) == 0
                && skillStats.GetValueOrDefault(ElementalHitBans[i]) == 0)
                weight = 1;
            if (weight == 0) continue;
            total = unchecked(total + weight);
            if ((total == 0 ? random.NextUInt() : random.Uniform(total)) < weight)
                selected = i;
        }
        return selected;
    }

    static void TriggerPrismaticBurstLocked(Player player, ushort parentSkill, int parentEffect, (int X, int Y) centre, double at,
        IReadOnlyDictionary<uint, int> playerStats, int chance)
    {
        if (player is { Closed: true } or { LeavingArea: true } or { Paused: true } or { PlayerLife: 0 }) return;
        const uint trigger = Stat.TriggerPrismaticBurstOnHitPctChance;
        var parent = SupportedSkill(player, parentSkill);
        if (parentEffect >= 0 && parent.Effect != parentEffect
            || parent.Stats.GetValueOrDefault(trigger) <= 0 || !SkillsFor(player).IsAttack(parent.Effect)
            || PlacedKind(parent) is not (false, false) || parent.Stats.GetValueOrDefault(Stat.IsTotem) != 0
            || TriggerOnlyReason(parent) is not null || !parent.RequirementsMet
            || parent.Supports.FirstOrDefault(s => SupportHas(s, trigger)) is not { } support)
            return;

        var child = SupportCompanionSourceLocked(player, parent, support, PrismaticBurstEffect, "HungryLoopSupportedByPrismaticBurst");
        if (child is null || GemSkillInstanceDisabled(player, child.Effect, child.Id, triggered: true) is not null
            || CooldownForSkill(player, child.Id) is not { } cooldown) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;
        double now = Math.Max(at, ProjectileWorldTime(player));
        int key = CooldownKey(player, child.Effect, child.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(chance, 100, AreaRng(player))) return;

        var spell = SupportedSkill(player, child.Id);
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = NextLockstepSequence(player);
        if (!lockstep) sequence &= 0x7fff;
        int element = PrismaticBurstElementIndex(player.Id, sequence, playerStats, spell.Stats);
        if (element < 0 || !PayMana(player, child.Id, out uint mana, out uint life)) return;
        state.TryUse(now, cooldown);
        player.CooldownSources[child.Effect] = child.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        // Lockstep consumes the use when the instant action executes on the client.
        state.ConfirmNativeUse(state.UseVersion, child.Id, synchronizeProgress: !lockstep);

        var stats = new Dictionary<uint, int>(spell.Stats) { [ElementalHitOnly[element]] = 1 };
        var source = spell with { Stats = stats, PrismaticBurstElement = PrismaticBurstElements[element] };
        var use = (child.Id, child.Effect, Name: PrismaticBurstEffect, spell.Level, spell.Quality, child.Gem);
        var critical = CaptureSkillCriticalRoll(player, child.Id);
        int epoch = player.ProjectileEpoch;
        bool Current() => session.Active && player.ProjectileActive(epoch) && player.PlayerLife > 0
            && !player.Paused && SameTriggerSource(spell, SupportedSkill(player, child.Id));
        session.World(player.Id, InstanceProtocol.InstantAction(player.Id, child.Id, centre.X, centre.Y,
            sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
            $"0x01ce Prismatic Burst {source.PrismaticBurstElement} at {centre}");
        session.Enqueue(async () =>
        {
            if (mana > 0 || life > 0) await SendSkillCost(player, life, "Prismatic Burst cost");
            if (!lockstep) await SendNativeCooldownRefresh(player);
            await AreaSkillHitStrike(player, child.Id, use, centre, centre, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: false, damageMore: 1, excludeMonster: 0, critical,
                channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: null, once: null, repeatDelay: 0,
                radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Current, discharge: null,
                strikeIndex: 0, firstContactAt: null, ruthlessMore: 1, priorTargets: null, scaledBlastChaos: 0,
                singleStrikeTarget: null, onHit: null, scheduledSession: null, cryExerted: false,
                prepaidRawExtra: 0, baseChaos: 0, hammer: null, nearbyOnly: true, triggeredSpellSource: source);
        });
    }
}
