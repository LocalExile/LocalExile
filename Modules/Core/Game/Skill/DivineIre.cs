using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const int DivineIreMaxStages = 10;
    const int DivineIreBaseRadius = 38;
    const int DivineIreBeamLength = 110;
    const double DivineIreBaseBeamWidth = 10;

    sealed class DivineIreChannel(ushort skill, ushort sequence, SupportedGem gem, GemSourceLifetimes.SourceLifetime source,
        int generation, int action, int interruption, int epoch, double start, (int X, int Y) aim, bool beamOnly)
    {
        public readonly ushort Skill = skill, Sequence = sequence;
        public readonly SupportedGem Gem = gem;
        public readonly GemSourceLifetimes.SourceLifetime Source = source;
        public readonly int Generation = generation, Action = action, Interruption = interruption, Epoch = epoch;
        public readonly double StartedAt = start;
        public readonly bool BeamOnly = beamOnly;
        public (int X, int Y) Aim = aim;
        public ProjectileVector Direction = new(1, 0);
        public int Stages;
    }

    static readonly RuleTable<Player, DivineIreChannel> DivineIreChannels = new();

    public static bool IsDivineIre(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "DivineIre" or "DivineIreAltX" or "DivineIreAltY" or "DivineIreRoyale";

    static void BeginDivineIre(Player player, ushort skill, ushort sequence, SupportedGem gem,
        int generation, int action, (int X, int Y) aim)
    {
        if (CaptureGemSourceLifetimeLocked(player, skill, gem.Effect) is not { } source) return;
        var channel = new DivineIreChannel(skill, sequence, gem, source, generation, action,
            player.ActionInterruptionGeneration, player.ProjectileEpoch, AreaTime(player), aim,
            SkillsFor(player).EffectId(gem.Effect) == "DivineIreAltY");
        DivineIreAimDirection(channel, player.Walk.Position(channel.StartedAt));
        DivineIreChannels.AddOrUpdate(player, channel);
    }

    static DivineIreChannel? CurrentDivineIre(Player player) =>
        DivineIreChannels.TryGetValue(player, out var channel) && channel.Generation == player.ChannelGeneration
            && channel.Skill == player.ChannelSkill && channel.Action == player.ActionCount ? channel : null;

    static bool AimDivineIre(Player player, (int X, int Y) aim)
    {
        if (CurrentDivineIre(player) is not { } channel || aim == channel.Aim) return false;
        channel.Aim = aim;
        DivineIreAimDirection(channel, player.Walk.Position(AreaTime(player)));
        return true;
    }

    static ProjectileVector DivineIreAimDirection(DivineIreChannel channel, (int X, int Y) origin)
    {
        var toward = ProjectileSession.Vector(channel.Aim) - ProjectileSession.Vector(origin);
        if (toward.Length > 0) channel.Direction = toward.Unit;
        return channel.Direction;
    }

    static bool DivineIreLive(Player player, DivineIreChannel channel) => CurrentGameWork
        && player.ProjectileActive(channel.Epoch) && player.PlayerLife > 0 && !player.RageRetired
        && channel.Interruption == player.ActionInterruptionGeneration;

    static bool DivineIreSourceCurrent(Player player, DivineIreChannel channel) => DivineIreLive(player, channel)
        && GemSourceLifetimeCurrentLocked(player, channel.Skill, channel.Gem.Effect, channel.Source)
        && SameTriggerSource(channel.Gem, SupportedSkill(player, channel.Skill))
        && GemSkillInstanceDisabled(player, channel.Gem.Effect, channel.Skill) is null;

    static bool DivineIreVisible(Player player, (int X, int Y) origin, (int X, int Y) at) =>
        player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(origin, at, sight, 1);

    static void SendDivineIreStages(ProjectileSession session, Player player, int stages)
    {
        var content = InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, stages)]);
        session.Enqueue(() => SendWorldRecord(player, player.Id, content, $"0x01b3 Divine Ire stage {stages}"));
    }

    static void RetireDivineIre(Player player, int generation)
    {
        if (!DivineIreChannels.TryGetValue(player, out var channel) || channel.Generation != generation) return;
        DivineIreChannels.Remove(player);
        if (channel.Stages > 0 && player.ProjectileRuntime is ProjectileSession { Active: true } session)
            SendDivineIreStages(session, player, 0);
    }

    static void PulseDivineIre(Player player, DivineIreChannel channel,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, double pact)
    {
        using var mutation = ProjectileMutation(player);
        double now = AreaTime(player);
        if (!ChannelCurrent(player, channel.Skill, channel.Action, channel.Generation)) return;
        if (PlayerActionBlocked(player, now, now) || !DivineIreSourceCurrent(player, channel))
        {
            AbortStartedChannel(player, channel.Generation);
            return;
        }
        var session = ProjectilesFor(player);
        int next = channel.Stages + 1;
        var stats = channel.Gem.Stats;
        if (!channel.BeamOnly)
        {
            int count = Math.Clamp(stats.GetValueOrDefault(Stat.DivineTempestBaseNumberOfNearbyEnemiesToZap)
                + stats.GetValueOrDefault(Stat.DivineTempestNumberOfAdditionalNearbyEnemiesToZap), 0, 64);
            var origin = player.Walk.Position(now);
            int radius = SkillRadius(player, DivineIreBaseRadius, stats, spell: true, gem.Effect);
            var targets = player.Area.MonstersNear(origin, radius)
                .Where(entry => entry.Value is { Hostile: true, OwnerId: null, Dead: false, Life: > 0, WorldHidden: false })
                .Select(entry => (Id: entry.Key, At: entry.Value.Walk.Position(now)))
                .Where(entry => Dist(origin, entry.At) <= radius && DivineIreVisible(player, origin, entry.At))
                .OrderBy(entry => Dist(origin, entry.At)).ThenBy(entry => entry.Id)
                .Take(count).Select(entry => entry.Id).ToArray();
            if (targets.Length != 0)
            {
                double more = DivineIreFocusedMore(channel, now, pact)
                    * Math.Max(0, 1 + stats.GetValueOrDefault(Stat.DivineTempestDamagePlusPctFinalWhileChannelling) / 100d);
                var hit = AreaSkillHitForUse(null, player, channel.Skill, gem, origin, origin, immediate: true,
                    sequence: channel.Sequence, channelGeneration: channel.Generation, actionToken: channel.Action,
                    scheduledSession: session, radiusOverride: radius, contactTargets: targets, hitAndAilmentMore: more,
                    onDirectHit: GainStageOnContact);
                if (!hit.IsCompleted) throw new InvalidOperationException("Divine Ire zap contact must not yield.");
                hit.GetAwaiter().GetResult();
            }
        }

        if (!ReferenceEquals(channel, CurrentDivineIre(player)) || player.PlayerLife == 0) return;
        int stages = Math.Min(DivineIreMaxStages, next);
        if (stages == channel.Stages) return;
        channel.Stages = stages;
        SendDivineIreStages(session, player, stages);

        void GainStageOnContact(uint id)
        {
            if (next >= DivineIreMaxStages || !player.Area.Monsters.TryGetValue(id, out var monster)) return;
            bool boss = monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique;
            if (boss ? stats.GetValueOrDefault(Stat.DivineTempestStageOnHittingRareUnique) != 0
                : RandomRolls.Chance(stats.GetValueOrDefault(Stat.DivineTempestStageOnHittingNormalMagicPctChance), 100, AreaRng(player)))
                next++;
        }
    }

    static double DivineIreFocusedMore(DivineIreChannel channel, double now, double pact) =>
        pact * FocusedChannelMore(now - channel.StartedAt,
            channel.Gem.Stats.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));

    static void ReleaseDivineIreLocked(Player player, double now, LyciaChannel? lycia)
    {
        if (CurrentDivineIre(player) is not { } channel) return;
        RetireDivineIre(player, channel.Generation);
        if (channel.Stages == 0 || PlayerActionBlocked(player, now, now) || !DivineIreSourceCurrent(player, channel)
            || player.ProjectileRuntime is not ProjectileSession { Active: true } session) return;

        var origin = player.Walk.Position(now);
        var direction = DivineIreAimDirection(channel, origin);
        double pact = lycia?.Generation == channel.Generation ? lycia.Pact.DamageMore : 1;
        session.ScheduleContact(ProjectileRuntime.Time(now), () => DivineIreSourceCurrent(player, channel),
            () => DivineIreReleaseContacts(session, player, channel, origin, direction, now, pact));
    }

    static void DivineIreReleaseContacts(ProjectileSession session, Player player, DivineIreChannel channel,
        (int X, int Y) origin, ProjectileVector direction, double now, double pact)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(entry => entry.Id == channel.Skill && entry.Effect == channel.Gem.Effect);
        if (gem.Name is null) return;
        var stats = channel.Gem.Stats;
        var caster = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key) + caster.GetValueOrDefault(key);
        double halfWidth = Math.Max(0, (DivineIreBaseBeamWidth + Get(Stat.DivineTempestBeamWidthPlus))
            * (1 + Get(Stat.DivineTempestBeamWidthPlusPct) / 100d) / 2);
        bool beam = Get(Stat.DivineTempestNoBeam) == 0;
        int extra = channel.Stages - 1;
        double more = DivineIreFocusedMore(channel, now, pact);
        double hit = Math.Max(0, 1 + extra * Get(Stat.DivineTempestHitDamagePlusPctFinalPerStage) / 100d);
        double ailment = Math.Max(0, 1 + extra * Get(Stat.DivineTempestAilmentDamagePlusPctFinalPerStage) / 100d);
        var critical = CaptureSkillCriticalRoll(player, channel.Skill);

        if (beam) Contact(DivineIreBeamLength + (int)Math.Ceiling(halfWidth), hit, ailment, InBeam);
        if (!channel.BeamOnly)
        {
            int radius = SkillRadius(player, DivineIreBaseRadius, stats, spell: true, gem.Effect);
            Contact(radius, beam ? 1 : hit, beam ? 1 : ailment, at => !beam || !InBeam(at));
        }

        bool InBeam((int X, int Y) at)
        {
            var offset = ProjectileSession.Vector(at) - ProjectileSession.Vector(origin);
            double along = offset.Dot(direction);
            return along > 0 && along <= DivineIreBeamLength
                && Math.Abs(offset.X * direction.Y - offset.Y * direction.X) <= halfWidth;
        }

        void Contact(int radius, double hits, double ailments, Func<(int X, int Y), bool> shape)
        {
            var contact = AreaSkillHitForUse(null, player, channel.Skill, gem, origin, origin, immediate: true,
                sequence: channel.Sequence, criticalRoll: critical, radiusOverride: radius, nearbyOnly: true,
                scheduledSession: session, triggeredCurrent: () => DivineIreLive(player, channel),
                hitAndAilmentMore: more, hitMore: hits, ailmentMore: ailments,
                targetFilter: (id, at) => shape(at) && DivineIreVisible(player, origin, at)
                    && player.Area.Monsters.TryGetValue(id, out var monster) && !monster.WorldHidden);
            if (!contact.IsCompleted) throw new InvalidOperationException("Divine Ire release contact must not yield.");
            contact.GetAwaiter().GetResult();
        }
    }
}
