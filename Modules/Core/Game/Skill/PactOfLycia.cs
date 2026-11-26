using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string PactOfLyciaEffect = "PactOfLycia";
    const string HeavensScourgeEffect = "TriggeredHeavensScourge";
    const ushort LyciaEmpowermentTag = 3;

    internal sealed class LyciaEmpowerment(ushort skill, SupportedGem source, long ends, int charges, double damageMore)
    {
        internal readonly ushort Skill = skill;
        internal readonly SupportedGem Source = source;
        internal readonly long Ends = ends;
        internal readonly double DamageMore = damageMore;
        internal int Charges = charges;
    }

    internal sealed class LyciaChannel(LyciaEmpowerment pact, ushort skill, SupportedGem source, int action, int generation,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) bolt, SupportedGem boltSource,
        long interval, long unwindInterval, int limit, (int X, int Y) aim)
    {
        internal readonly LyciaEmpowerment Pact = pact;
        internal readonly ushort Skill = skill;
        internal readonly SupportedGem Source = source;
        internal readonly int Action = action, Generation = generation;
        internal readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Bolt = bolt;
        internal readonly SupportedGem BoltSource = boltSource;
        internal readonly long Interval = interval, UnwindInterval = unwindInterval;
        internal readonly int Limit = limit;
        internal (int X, int Y) Aim = aim;
        internal int Strikes;
    }

    public sealed partial class ProjectileSession
    {
        internal LyciaEmpowerment? LyciaSpells;
        internal LyciaChannel? LyciaChannel;
    }

    static void StartPactOfLycia(ProjectileSession session, ushort skill, SupportedGem source)
    {
        int charges = Math.Clamp(source.Stats.GetValueOrDefault(Stat.SkillEmpowersNextXSpellsCast), 0, ushort.MaxValue);
        if (charges == 0) return;
        long now = session.Runtime.Now;
        var pact = new LyciaEmpowerment(skill, source, now + pactEmpowermentTime, charges,
            Math.Max(0, 1 + source.Stats.GetValueOrDefault(Stat.PactSkillGrantDamagePlusPctFinalToExertedSkills) / 100d));
        session.LyciaSpells = pact;
        ShowLyciaEmpowerment(session, pact, now, keepInstance: false);
        session.ScheduleContact(pact.Ends, () => session.LyciaSpells == pact, () => ClearLyciaEmpowerment(session));
    }

    static void ShowLyciaEmpowerment(ProjectileSession session, LyciaEmpowerment pact, long now, bool keepInstance) =>
        session.Player.Area.Buffs.Set(session.Player.Id, new(EmpoweredSpellsRow, session.Player.Id, pact.Skill,
            U68: PactCountdown(pact.Ends, now), Stacks: (ushort)pact.Charges, Tag: LyciaEmpowermentTag, KeepInstance: keepInstance));

    static void ClearLyciaEmpowerment(ProjectileSession session)
    {
        if (session.LyciaSpells is null) return;
        session.LyciaSpells = null;
        session.Player.Area.Buffs.Clear(session.Player.Id, EmpoweredSpellsRow, session.Player.Id, tag: LyciaEmpowermentTag);
    }

    static LyciaChannel? StartLyciaChannel(Player player, ushort skill, int effect, int action, int generation,
        (int X, int Y) aim, double started)
    {
        var skills = SkillsFor(player);
        if (player.ProjectileRuntime is not ProjectileSession { LyciaSpells: not null } session
            || !skills.IsSpell(effect) || skills.IsVaal(effect) || skills.IsBrand(effect)) return null;
        using (ProjectileMutation(player, advance: false))
        {
            if (session.LyciaSpells is not { } pact || !session.Active
                || player.PaidChannel is not { } paid || !PaidChannelCurrent(player, paid)
                || paid.Skill != skill || paid.Generation != generation || paid.Action != action)
                return null;
            long now = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(started));
            if (now >= pact.Ends || !SameTriggerSource(pact.Source, SupportedSkill(player, pact.Skill)))
            {
                ClearLyciaEmpowerment(session);
                return null;
            }
            var source = SupportedSkill(player, skill);
            if (PlacedKind(source) != (false, false) || IsTotemSkill(player, skill) || TriggerOnlyReason(source) is not null)
                return null;

            var identity = PlayerInventories.GemIdentity(pact.Source.SourceGem!);
            foreach (var bolt in GemSkillEffects(player))
            {
                if (SkillsFor(player).EffectId(bolt.Effect) != HeavensScourgeEffect
                    || SupportedSkill(player, bolt.Id) is not { SourceGem: { } item } child
                    || !ReferenceEquals(PlayerInventories.GemIdentity(item), identity))
                    continue;
                int Own(uint stat) => child.Stats.GetValueOrDefault(stat);
                int intervalMs = Own(Stat.HeavensScourgeChannelingTriggerIntervalMs);
                int unwindMs = Own(Stat.HeavensScourgeUnwindingTriggerIntervalMs);
                int limit = Own(Stat.HeavensScourgeMaxChannellingLightningStrikes);
                if (intervalMs <= 0 || unwindMs <= 0 || limit <= 0) return null;

                if (session.LyciaChannel is { } previous) FinishLyciaChannel(session, previous, now);
                var channel = new LyciaChannel(pact, skill, source, action, generation, bolt, child,
                    ProjectileRuntime.Time(intervalMs / 1000d), ProjectileRuntime.Time(unwindMs / 1000d), limit, aim);
                session.LyciaChannel = channel;
                if (--pact.Charges == 0) ClearLyciaEmpowerment(session);
                else ShowLyciaEmpowerment(session, pact, now, keepInstance: true);
                ScheduleLyciaStrike(session, channel, now + channel.Interval);
                return channel;
            }
        }
        return null;
    }

    static bool LyciaBoltCurrent(ProjectileSession session, LyciaChannel channel)
    {
        var player = session.Player;
        return session.Active && player.PlayerLife > 0 && !player.LeavingArea
            && SameTriggerSource(channel.Pact.Source, SupportedSkill(player, channel.Pact.Skill))
            && SameTriggerSource(channel.BoltSource, SupportedSkill(player, channel.Bolt.Id));
    }

    static void ScheduleLyciaStrike(ProjectileSession session, LyciaChannel channel, long due) =>
        session.ScheduleContact(due, () => session.LyciaChannel == channel, () =>
        {
            var player = session.Player;
            if (!LyciaBoltCurrent(session, channel) || !ChannelCurrent(player, channel.Skill, channel.Action, channel.Generation)
                || !SameTriggerSource(channel.Source, SupportedSkill(player, channel.Skill)))
            {
                FinishLyciaChannel(session, channel, session.Runtime.Now);
                return;
            }
            double now = ProjectileWorldTime(player);
            if (!PlayerActionBlocked(player, now, now))
            {
                ReleaseLyciaStrike(session, channel, unwinding: false);
                channel.Strikes++;
            }
            if (channel.Strikes < channel.Limit) ScheduleLyciaStrike(session, channel, due + channel.Interval);
        });

    static void FinishLyciaChannel(ProjectileSession session, LyciaChannel channel, long at)
    {
        if (session.LyciaChannel != channel) return;
        session.LyciaChannel = null;
        if (!LyciaBoltCurrent(session, channel)) return;
        long start = Math.Max(at, session.Runtime.Now);
        for (int i = 1; i <= channel.Strikes; i++)
            session.ScheduleContact(start + i * channel.UnwindInterval, () => LyciaBoltCurrent(session, channel),
                () => ReleaseLyciaStrike(session, channel, unwinding: true));
    }

    static double LyciaDamageMore(Player player, ushort skill, int? generation) =>
        generation is not null && player.ProjectileRuntime is ProjectileSession { LyciaChannel: { } channel }
            && channel.Skill == skill && channel.Generation == generation ? channel.Pact.DamageMore : 1;

    static void ReleaseLyciaStrike(ProjectileSession session, LyciaChannel channel, bool unwinding)
    {
        var player = session.Player;
        var bolt = channel.Bolt;
        double now = ProjectileWorldTime(player);
        var origin = player.Walk.Position(now);
        int autoRadius = Math.Max(0, channel.BoltSource.Stats.GetValueOrDefault(Stat.ActiveSkillBaseAutoTargettingRadius));
        var aim = player.Area.MonstersNear(origin, autoRadius)
            .Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false, Invulnerable: false, OwnerId: null })
            .Select(e => (e.Key, Position: e.Value.Walk.Position(now)))
            .Where(e => Dist(origin, e.Position) <= autoRadius
                && session.TraceTerrain(ProjectileSession.Vector(origin), ProjectileSession.Vector(e.Position)) is null)
            .OrderBy(e => Dist(origin, e.Position)).ThenBy(e => e.Key)
            .Select(e => ((int X, int Y)?)e.Position).FirstOrDefault() ?? channel.Aim;
        if (Dist(origin, aim) > autoRadius || !player.Layout.WalkableFor(aim.X, aim.Y, 0)) aim = origin;
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = lockstep ? NextLockstepSequence(player) : (ushort)(++session.PactTriggerSequence & 0x7fff);
        session.WorldRecords(player.Id, () => [InstanceProtocol.InstantAction(player.Id, bolt.Id, aim.X, aim.Y,
            sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true)],
            ServerTrace.Projectiles ? $"Heaven's Scourge {(unwinding ? "unwind" : "channel")} seq={sequence} t={now:0.000}" : null);
        var task = AreaSkillHitForUse(null, player, bolt.Id, bolt, origin, aim, immediate: true,
            sequence: sequence, triggeredCurrent: () => LyciaBoltCurrent(session, channel),
            criticalRoll: CaptureSkillCriticalRoll(player, bolt.Id), scheduledSession: session);
        if (!task.IsCompleted) throw new InvalidOperationException("A Pact of Lycia strike must settle in its area frame.");
        task.GetAwaiter().GetResult();
    }
}
