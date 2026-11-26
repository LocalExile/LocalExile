using GameServer.Core.Projectiles;

public static partial class Game
{
    const string PactOfKtashEffect = "PactOfKtash";
    const ushort KtashEmpowermentTag = 2;

    internal sealed record KtashEmpowerment(ushort Skill, SupportedGem Source, long Ends, double DamageMore,
        double PreventionMore, int RefundChance)
    {
        internal int Charges { get; set; }
    }

    public sealed partial class ProjectileSession
    {
        internal KtashEmpowerment? KtashSpells;
    }

    static void StartPactOfKtash(ProjectileSession session, ushort skill, SupportedGem source)
    {
        int Own(uint stat) => source.Stats.GetValueOrDefault(stat);
        int charges = Math.Clamp(Own(Stat.SkillEmpowersNextXSpellsCast), 0, ushort.MaxValue);
        if (charges == 0) return;
        long now = session.Runtime.Now;
        var empowerment = new KtashEmpowerment(skill, source, now + pactEmpowermentTime,
            Math.Max(0, 1 + Own(Stat.PactSkillGrantDamagePlusPctFinalToExertedSkills) / 100d),
            Math.Max(0, 1 + Own(Stat.ActiveSkillOsmVaalSkillSoulGainPreventionPlusPctFinalToGrant) / 100d),
            Math.Clamp(Own(Stat.ActiveSkillOsmVaalSkillSoulRefundChancePctToGrant), 0, 100)) { Charges = charges };
        session.KtashSpells = empowerment;
        ShowKtashEmpowerment(session.Player, empowerment, now, keepInstance: false);
        session.ScheduleContact(empowerment.Ends, () => ReferenceEquals(session.KtashSpells, empowerment),
            () => ClearKtashEmpowerment(session));
    }

    static KtashEmpowerment? KtashSpellFor(Player player, ushort skill, int effect)
    {
        if (player.ProjectileRuntime is not ProjectileSession { KtashSpells: { } empowerment }) return null;
        var skills = SkillsFor(player);
        if (!skills.IsSpell(effect) || !skills.HasType(effect, "Damage")
            || skills.HasType(effect, "Triggered") || TriggerOnlyReason(SupportedSkill(player, skill)) is not null) return null;
        long now = ProjectileRuntime.Time(ProjectileWorldTime(player));
        if (now >= empowerment.Ends || !SameTriggerSource(empowerment.Source, SupportedSkill(player, empowerment.Skill)))
            return null;
        return empowerment;
    }

    static void UseKtashEmpowerment(Player player, KtashEmpowerment empowerment)
    {
        if (player.ProjectileRuntime is not ProjectileSession session || !ReferenceEquals(session.KtashSpells, empowerment))
            return;
        if (--empowerment.Charges == 0) ClearKtashEmpowerment(session);
        else ShowKtashEmpowerment(player, empowerment, ProjectileRuntime.Time(ProjectileWorldTime(player)), keepInstance: true);
    }

    static void ShowKtashEmpowerment(Player player, KtashEmpowerment empowerment, long now, bool keepInstance) =>
        player.Area.Buffs.Set(player.Id, new(EmpoweredSpellsRow, player.Id, empowerment.Skill,
            U68: PactCountdown(empowerment.Ends, now), Stacks: (ushort)empowerment.Charges,
            Tag: KtashEmpowermentTag, KeepInstance: keepInstance));

    static void ClearKtashEmpowerment(ProjectileSession session)
    {
        if (session.KtashSpells is null) return;
        session.KtashSpells = null;
        var player = session.Player;
        player.Area.Buffs.Clear(player.Id, EmpoweredSpellsRow, player.Id, tag: KtashEmpowermentTag);
    }
}
