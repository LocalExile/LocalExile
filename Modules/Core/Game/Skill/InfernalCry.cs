using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string CombustEffect = "Combust";

    sealed record CombustSource(PlayerSkillSource Skill, GemSourceLifetimes.SourceLifetime Lifetime);

    // The attack's critical roll already identifies its paid use across all melee contacts.
    // Keep the exertion on that same lifetime, including after its one trigger is spent.
    sealed class InfernalExertion(CombustSource? source)
    {
        public readonly CombustSource? Source = source;
        public bool Triggered;
    }

    static readonly RuleTable<SkillCriticalRoll, InfernalExertion> InfernalExertions = new();

    internal static void GrantInfernalCryExertsLocked(Player player, ushort skillId, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> stats)
    {
        var cry = CryOf(player);
        cry.InfernalExerts = WarcryExertionCount(own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks), stats);
        cry.Combust = null;
        if (cry.InfernalExerts == 0 || CurrentPlayerSkillSourceLocked(player, skillId) is not { Active: true } warcry)
            return;
        var catalog = SkillsFor(player);
        var source = PlayerSkillSourcesLocked(player).FirstOrDefault(row => row.Active
            && row.Identity.Host == warcry.Identity.Host && row.Identity.Socket == warcry.Identity.Socket
            && row.Identity.Source == warcry.Identity.Source && catalog.EffectId(row.Effect) == CombustEffect);
        if (source is not null)
            cry.Combust = new(source, CaptureGemSourceLifetimeLocked(player, source));
    }

    static void ConsumeInfernalCry(CryRuntime cry, SkillCriticalRoll attack)
    {
        if (cry.InfernalExerts <= 0 || InfernalExertions.TryGetValue(attack, out _)) return;
        cry.InfernalExerts--;
        InfernalExertions.AddOrUpdate(attack, new(cry.Combust));
    }

    static InfernalExertion? InfernalExertionFor(SkillCriticalRoll attack) =>
        InfernalExertions.TryGetValue(attack, out var exertion) ? exertion : null;

    static void InfernalMeleeHitLocked(Player player, InfernalExertion? exertion, (int X, int Y) contact,
        double at)
    {
        if (exertion is not { Triggered: false, Source: { } source }) return;
        exertion.Triggered = true;
        var skill = source.Skill;
        var session = ProjectilesFor(player);
        int epoch = player.ProjectileEpoch;
        bool Current() => CurrentGameWork && player.ProjectileActive(epoch) && player.PlayerLife > 0 && !player.Paused
            && GemSourceLifetimeCurrentLocked(player, skill.Id, skill.Effect, source.Lifetime);
        session.ScheduleContact(ProjectileRuntime.Time(at), Current, () =>
        {
            if (CharacterPlayerStats(player).GetValueOrDefault(Stat.CombustIsDisabled) != 0
                || GemSkillInstanceDisabled(player, skill.Effect, skill.Id, triggered: true) is not null) return;
            var supported = SupportedSkill(player, skill.Id);
            bool lockstep = player.Connection.LockstepClient;
            ushort sequence = NextLockstepSequence(player);
            if (!lockstep) sequence &= 0x7fff;
            session.World(player.Id, InstanceProtocol.InstantAction(player.Id, skill.Id, contact.X, contact.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce Combust at {contact}");
            var use = (skill.Id, skill.Effect, Name: CombustEffect, supported.Level, supported.Quality, skill.Gem);
            var hit = AreaSkillHitForUse(null, player, skill.Id, use, contact, contact, immediate: true,
                sequence: sequence, melee: true, triggeredCurrent: Current, scheduledSession: session,
                cryExerted: false, allowMeleeSplash: false, nearbyOnly: true);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Combust contact must not yield.");
            hit.GetAwaiter().GetResult();
        });
    }
}
