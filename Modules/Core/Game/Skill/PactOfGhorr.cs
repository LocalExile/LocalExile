using GameServer.Core.Projectiles;

public static partial class Game
{
    const string PactOfGhorrEffect = "PactOfGhorr";
    const string FleshrendEffect = "TriggeredBloodrend";
    const ushort HungerOfGhorrRow = 2761;
    const ushort EmpoweredSpellsRow = 2790;

    internal sealed class GhorrHunger(ushort skill, SupportedGem source, (int X, int Y)? aim, long ends,
        long interval, int faster)
    {
        internal ushort Skill { get; } = skill;
        internal SupportedGem Source { get; } = source;
        internal (int X, int Y)? Aim { get; } = aim;
        internal long Ends { get; } = ends;
        internal readonly HashSet<int> Spells = [];
        internal long Next;
        internal long Interval => Math.Max(1, (long)Math.Round(interval / (1 + Spells.Count * faster / 100d)));
    }

    internal sealed class GhorrEmpowerment(ushort skill, SupportedGem source, long ends, int charges, double more)
    {
        internal ushort Skill { get; } = skill;
        internal SupportedGem Source { get; } = source;
        internal long Ends { get; } = ends;
        internal int Charges = charges;
        internal double More { get; } = more;
    }

    sealed record GhorrCast(Player Player, ushort Skill, double More);
    static readonly AsyncLocal<GhorrCast?> GhorrCastInUse = new();

    sealed class GhorrCastScope : IDisposable
    {
        readonly GhorrCast? previous = GhorrCastInUse.Value;
        public GhorrCastScope(GhorrCast cast) => GhorrCastInUse.Value = cast;
        public void Dispose() => GhorrCastInUse.Value = previous;
    }

    static GhorrCast? CastingGhorrSpell(Player player, ushort skill) =>
        GhorrCastInUse.Value is { } cast && cast.Player == player && cast.Skill == skill ? cast : null;

    static double GhorrDotMore(Player player, ushort skill) => CastingGhorrSpell(player, skill)?.More ?? 1;

    public sealed partial class ProjectileSession
    {
        internal GhorrHunger? HungerOfGhorr;
        internal GhorrEmpowerment? GhorrSpells;
    }

    static GhorrCast? TakeGhorrSpell(Player player, ushort skill, SupportedGem spell)
    {
        if (player.ProjectileRuntime is not ProjectileSession { GhorrSpells: not null } session) return null;
        var catalog = SkillsFor(player);
        int effect = spell.Effect;
        if (!catalog.IsSpell(effect) || catalog.IsInstant(effect) || catalog.IsBrand(effect)
            || catalog.HasType(effect, "Vaal") || catalog.HasType(effect, "Triggered")
            || IsTotemSkill(player, skill) || TriggerOnlyReason(spell) is not null
            || PlacedKind(spell) != (false, false)
            || !GhorrSpellHasNonAilmentDamage(catalog, spell))
            return null;
        using (ProjectileMutation(player))
        {
            if (session.GhorrSpells is not { } empowerment) return null;
            long now = session.Runtime.Now;
            if (now >= empowerment.Ends || !SameTriggerSource(empowerment.Source, SupportedSkill(player, empowerment.Skill)))
            {
                ClearGhorrEmpowerment(session);
                return null;
            }
            empowerment.Charges--;
            if (empowerment.Charges == 0) ClearGhorrEmpowerment(session);
            else ShowGhorrEmpowerment(session, empowerment, now);

            if (session.HungerOfGhorr is { } hunger && now < hunger.Ends)
            {
                long previousInterval = hunger.Interval;
                if (hunger.Spells.Count < 3 && hunger.Spells.Add(effect))
                {
                    double remaining = Math.Clamp((hunger.Next - now) / (double)previousInterval, 0, 1);
                    ShowGhorrHunger(player, hunger, now, keepInstance: true);
                    ScheduleGhorrTrigger(session, hunger, now + Math.Max(1, (long)Math.Round(hunger.Interval * remaining)));
                }
            }
            return new(player, skill, empowerment.More);
        }
    }

    static bool GhorrSpellHasNonAilmentDamage(SkillCatalog catalog, SupportedGem spell)
    {
        var stats = spell.Stats;
        int Own(uint stat) => stats.GetValueOrDefault(stat);
        // Desecrate has its own client flag. Some variants inherit the DamageOverTime tag without its damage.
        return Own(Stat.PactOfGhorrCanModifyDamageOverTimeOfSkill) != 0
            || catalog.HasType(spell.Effect, "DamageOverTime") && (Own(Stat.BasePhysicalDamageToDealPerMinute) > 0
                || Own(Stat.BaseFireDamageToDealPerMinute) > 0 || Own(Stat.BaseColdDamageToDealPerMinute) > 0
                || Own(Stat.BaseLightningDamageToDealPerMinute) > 0 || Own(Stat.BaseChaosDamageToDealPerMinute) > 0
                || Own(Stat.BaseRighteousFirePctOfMaxManaToDealToNearbyPerMinute) > 0);
    }

    static void StartPactOfGhorr(ProjectileSession session, ushort id, SupportedGem source, (int X, int Y)? aim,
        IReadOnlyDictionary<uint, int> stats)
    {
        var player = session.Player;
        int Own(uint stat) => source.Stats.GetValueOrDefault(stat);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.SkillGrantsBloodriteForBaseXMsOnUse),
            stat => stats.GetValueOrDefault(stat), Own, DurationKinds(player, source.Effect));
        int frequency = Own(Stat.BloodriteBaseTriggerFrequencyMs);
        long now = session.Runtime.Now;
        if (seconds > 0 && frequency > 0)
        {
            var hunger = new GhorrHunger(id, source, aim, now + ProjectileRuntime.Time(seconds),
                ProjectileRuntime.Time(frequency / 1000d), Math.Max(0, Own(Stat.BloodriteTriggerFrequencyPlusPctPerStage)));
            session.HungerOfGhorr = hunger;
            ShowGhorrHunger(player, hunger, now, keepInstance: false);
            ScheduleGhorrTrigger(session, hunger, now + hunger.Interval);
        }
        int charges = Math.Clamp(Own(Stat.SkillEmpowersNextXSpellsCast), 0, ushort.MaxValue);
        if (charges <= 0) return;
        var empowerment = new GhorrEmpowerment(id, source, now + pactEmpowermentTime, charges,
            Math.Max(0, 1 + Own(Stat.PactSkillGrantDamageOverTimePlusPctFinalToSpells) / 100d));
        session.GhorrSpells = empowerment;
        ShowGhorrEmpowerment(session, empowerment, now, keepInstance: false);
        session.ScheduleContact(empowerment.Ends, () => session.GhorrSpells == empowerment,
            () => ClearGhorrEmpowerment(session));
    }

    static void ScheduleGhorrTrigger(ProjectileSession session, GhorrHunger hunger, long trigger)
    {
        var player = session.Player;
        // If stages change before the last trigger, the old scheduled callback no longer owns the deadline.
        if (hunger.Next == trigger) return;
        hunger.Next = trigger;
        session.ScheduleContact(Math.Min(trigger, hunger.Ends), () => session.HungerOfGhorr == hunger && hunger.Next == trigger, () =>
        {
            if (player.PlayerLife == 0 || !SameTriggerSource(hunger.Source, SupportedSkill(player, hunger.Skill))
                || GemSkillInstanceDisabled(player, hunger.Source.Effect, hunger.Skill) is not null)
            {
                ClearGhorrHunger(player);
                return;
            }
            if (trigger <= hunger.Ends)
            {
                var source = PlayerInventories.GemIdentity(hunger.Source.SourceGem!);
                foreach (var child in GemSkillEffects(player))
                {
                    if (SkillsFor(player).EffectId(child.Effect) != FleshrendEffect
                        || SupportedSkill(player, child.Id).SourceGem is not { } gem
                        || !ReferenceEquals(source, PlayerInventories.GemIdentity(gem))
                        || GemSkillInstanceDisabled(player, child.Effect, child.Id, triggered: true) is not null) continue;
                    ReleaseFleshrend(session, child.Id, child.Effect, hunger.Aim);
                    break;
                }
            }
            if (session.Runtime.Now >= hunger.Ends)
                ClearGhorrHunger(player);
            else
                ScheduleGhorrTrigger(session, hunger, trigger + hunger.Interval);
        });
    }

    static void ClearGhorrHunger(Player player)
    {
        if (player.ProjectileRuntime is not ProjectileSession { HungerOfGhorr: not null } session) return;
        session.HungerOfGhorr = null;
        player.Area.Buffs.Clear(player.Id, HungerOfGhorrRow, player.Id);
    }

    static void ShowGhorrHunger(Player player, GhorrHunger hunger, long now, bool keepInstance) =>
        player.Area.Buffs.Set(player.Id, new(HungerOfGhorrRow, player.Id, hunger.Skill,
            U68: PactCountdown(hunger.Ends, now), Stacks: (ushort)hunger.Spells.Count, KeepInstance: keepInstance));

    static void ShowGhorrEmpowerment(ProjectileSession session, GhorrEmpowerment empowerment, long now, bool keepInstance = true) =>
        session.Player.Area.Buffs.Set(session.Player.Id, new(EmpoweredSpellsRow, session.Player.Id, empowerment.Skill,
            U68: PactCountdown(empowerment.Ends, now), Stacks: (ushort)empowerment.Charges, KeepInstance: keepInstance));

    static void ClearGhorrEmpowerment(ProjectileSession session)
    {
        if (session.GhorrSpells is null) return;
        session.GhorrSpells = null;
        session.Player.Area.Buffs.Clear(session.Player.Id, EmpoweredSpellsRow, session.Player.Id);
    }
}
