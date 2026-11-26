using GameServer.Core.Projectiles;

public static partial class Game
{
    const string PactOfBeidatEffect = "PactOfBeidat";
    const ushort BeidatEmpowermentTag = 1;

    internal sealed class BeidatEmpowerment(ushort skill, SupportedGem source, long ends, int charges,
        int projectiles, int extraAreas, int chains, double more)
    {
        internal readonly ushort Skill = skill;
        internal readonly SupportedGem Source = source;
        internal readonly long Ends = ends;
        internal int Charges = charges;
        internal readonly int Projectiles = projectiles, ExtraAreas = extraAreas, Chains = chains;
        internal readonly double More = more;
    }

    internal sealed record BeidatCast(Player Player, ushort Skill, int Projectiles, int ExtraAreas, int Chains, double More);
    static readonly AsyncLocal<BeidatCast?> BeidatCastInUse = new();

    sealed class BeidatCastScope : IDisposable
    {
        readonly BeidatCast? previous = BeidatCastInUse.Value;
        internal BeidatCastScope(BeidatCast cast) => BeidatCastInUse.Value = cast;
        public void Dispose() => BeidatCastInUse.Value = previous;
    }

    static BeidatCast? CastingBeidatSpell(Player player, ushort skill) =>
        BeidatCastInUse.Value is { } cast && cast.Player == player && cast.Skill == skill ? cast : null;

    public sealed partial class ProjectileSession
    {
        internal BeidatEmpowerment? BeidatSpells;
    }

    static void StartPactOfBeidat(ProjectileSession session, ushort skill, SupportedGem source)
    {
        int Own(uint stat) => source.Stats.GetValueOrDefault(stat);
        int charges = Math.Clamp(Own(Stat.SkillEmpowersNextXSpellsCast), 0, ushort.MaxValue);
        if (charges <= 0) return;
        long now = session.Runtime.Now;
        var pact = new BeidatEmpowerment(skill, source, now + pactEmpowermentTime, charges,
            Math.Max(0, Own(Stat.PactSkillGrantXAdditionalProjectilesFiredInNova)),
            Math.Max(0, Own(Stat.PactSkillGrantXCascadesToDoInSpiral)),
            Math.Max(0, Own(Stat.PactSkillAdditionalBeamOnlyChains)),
            Math.Max(0, 1 + Own(Stat.PactSkillDamagePlusPctFinalWithHitsAndAilmentsToGrant) / 100d));
        session.BeidatSpells = pact;
        ShowBeidatEmpowerment(session, pact, now, keepInstance: false);
        session.ScheduleContact(pact.Ends, () => session.BeidatSpells == pact, () => ClearBeidatEmpowerment(session));
    }

    static void ShowBeidatEmpowerment(ProjectileSession session, BeidatEmpowerment pact, long now, bool keepInstance) =>
        session.Player.Area.Buffs.Set(session.Player.Id, new(EmpoweredSpellsRow, session.Player.Id, pact.Skill,
            U68: PactCountdown(pact.Ends, now), Stacks: (ushort)pact.Charges, Tag: BeidatEmpowermentTag, KeepInstance: keepInstance));

    static void ClearBeidatEmpowerment(ProjectileSession session)
    {
        if (session.BeidatSpells is null) return;
        session.BeidatSpells = null;
        session.Player.Area.Buffs.Clear(session.Player.Id, EmpoweredSpellsRow, session.Player.Id, tag: BeidatEmpowermentTag);
    }

    static BeidatCast? TakeBeidatSpell(Player player, ushort skill, SupportedGem source)
    {
        if (player.ProjectileRuntime is not ProjectileSession { BeidatSpells: not null } session) return null;
        var skills = SkillsFor(player);
        int effect = source.Effect;
        if (!skills.IsSpell(effect) || skills.IsInstant(effect) || skills.IsChannelled(effect)
            || skills.HasType(effect, "Triggered") || IsTotemSkill(player, skill) || TriggerOnlyReason(source) is not null
            || PlacedKind(source) != (false, false)) return null;

        bool projectile = skills.HasType(effect, "Projectile") && UsesProjectileRuntime(effect)
            && !skills.HasType(effect, "ProjectilesNumberModifiersNotApplied");
        string? id = skills.EffectId(effect);
        bool beam = PlacedArcPayload(id) || IsDarkBargain(player, effect);
        bool area = skills.HasType(effect, "Cascadable") && !skills.IsBrand(effect) && !skills.HasType(effect, "Minion");
        if (!projectile && !area && !beam) return null;

        using (ProjectileMutation(player, advance: false))
        {
            if (session.BeidatSpells is not { } pact || !session.Active) return null;
            long now = ProjectileRuntime.Time(ProjectileWorldTime(player));
            if (now >= pact.Ends || !SameTriggerSource(pact.Source, SupportedSkill(player, pact.Skill)))
            {
                ClearBeidatEmpowerment(session);
                return null;
            }
            if (--pact.Charges == 0) ClearBeidatEmpowerment(session);
            else ShowBeidatEmpowerment(session, pact, now, keepInstance: true);
            return new(player, skill, projectile ? pact.Projectiles : 0, area ? pact.ExtraAreas : 0,
                beam ? pact.Chains : 0, pact.More);
        }
    }

    static void AppendBeidatAreas(Player player, int effect, IReadOnlyDictionary<uint, int> stats,
        (int X, int Y) from, (int X, int Y) aim, int count, List<(int X, int Y)> areas)
    {
        if (count <= 0) return;
        AppendBeidatAreas(from, aim, SkillRadius(player, stats, spell: true, effect), count, areas);
    }

    static void AppendBeidatAreas((int X, int Y) from, (int X, int Y) aim, int radius, int count, List<(int X, int Y)> areas)
    {
        if (radius <= 0) return;
        double direction = Math.Atan2(aim.Y - from.Y, aim.X - from.X);
        const double goldenAngle = 2.399963229728653;
        // Data supplies the number and the skill radius. Use an even-area spiral for the centres.
        for (int step = 0; step < count; step++)
        {
            double distance = radius * Math.Sqrt(step + 1);
            double angle = direction + step * goldenAngle;
            var at = (aim.X + (int)Math.Round(distance * Math.Cos(angle)), aim.Y + (int)Math.Round(distance * Math.Sin(angle)));
            if (at != aim && !areas.Contains(at)) areas.Add(at);
        }
    }
}
