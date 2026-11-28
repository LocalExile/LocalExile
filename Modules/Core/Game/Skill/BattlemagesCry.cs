using GameServer.Core.Projectiles;

public static partial class Game
{
    const string BattlemagesCrySupport = "BattlemagesCrySupport";

    sealed record BattlemagesCrySource(LinkedTriggerSource Link, GemSourceLifetimes.SourceLifetime Lifetime);

    // The same critical roll identifies a paid attack through every melee contact and projectile it releases.
    sealed class MeleeWarcryTriggers(int count, CombustSource? combust, BattlemagesCrySource? spell)
    {
        public readonly int Count = count;
        public readonly CombustSource? Combust = combust;
        public readonly BattlemagesCrySource? Spell = spell;
        public bool Triggered;
    }

    static readonly RuleTable<SkillCriticalRoll, MeleeWarcryTriggers> ExertedMeleeTriggers = new();

    internal static void GrantBattlemagesCryExertsLocked(Player player, ushort skillId, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> stats)
    {
        var cry = CryOf(player);
        long count = (long)own.GetValueOrDefault(Stat.SkillEmpowersNextXMeleeAttacks)
            + stats.GetValueOrDefault(Stat.BattlemagesCryExertsXAdditionalAttacks);
        cry.BattlemagesExerts = WarcryExertionCount(count, stats, own, AreaRng(player));
        cry.BattlemagesDisplay = ResolveWarcryDisplaySkill(player, skillId, "BattlemagesCry");
        cry.BattlemagesSource = null;
        SyncExertedAttackBuffsLocked(player);
        if (cry.BattlemagesExerts == 0 || CurrentPlayerSkillSourceLocked(player, skillId) is not { Active: true } driver)
            return;
        var source = SupportedSkill(player, skillId);
        if (source.SourceGem is null) return;
        var spells = GemSkillEffects(player).Select(row => new LinkedSpell(row.Id, SupportedSkill(player, row.Id)))
            .Where(spell => spell.Gem.Host == source.Host && spell.Gem.Group == source.Group
                && spell.Gem.Supports.Any(support => support.Effect.Id == BattlemagesCrySupport
                    && support.Gem.Socket == driver.Identity.Socket && SupportHas(support, Stat.TriggeredByDivineCry))
                && TriggeredSpellExecutable(player, spell.Skill, spell.Gem)).ToArray();
        if (spells.Length > 0)
            cry.BattlemagesSource = new(new(skillId, source, spells, player.ProjectileEpoch, CombatLifetime),
                CaptureGemSourceLifetimeLocked(player, driver));
    }

    static MeleeWarcryTriggers? ConsumeMeleeWarcryTriggers(CryRuntime cry, SkillCriticalRoll attack, bool consume = true)
    {
        bool infernal = cry.InfernalExerts > 0;
        bool battlemages = cry.BattlemagesExerts > 0;
        if (!infernal && !battlemages) return null;
        if (ExertedMeleeTriggers.TryGetValue(attack, out var previous)) return previous;
        if (consume)
        {
            if (infernal) cry.InfernalExerts--;
            if (battlemages) cry.BattlemagesExerts--;
        }
        var triggers = new MeleeWarcryTriggers((infernal ? 1 : 0) + (battlemages ? 1 : 0),
            infernal ? cry.Combust : null, battlemages ? cry.BattlemagesSource : null);
        ExertedMeleeTriggers.Add(attack, triggers);
        return triggers;
    }

    static MeleeWarcryTriggers? MeleeWarcryTriggersFor(SkillCriticalRoll attack) =>
        ExertedMeleeTriggers.TryGetValue(attack, out var triggers) ? triggers : null;

    static void ExertedMeleeHitLocked(Player player, MeleeWarcryTriggers? triggers, (int X, int Y) contact, double at)
    {
        if (triggers is not { Triggered: false }) return;
        triggers.Triggered = true;
        if (triggers.Combust is { } combust) ReleaseCombust(player, combust, contact, at);
        if (triggers.Spell is not { } spell) return;
        var source = spell.Link;
        var session = ProjectilesFor(player);
        bool Current() => LinkedSourceCurrent(player, source)
            && GemSourceLifetimeCurrentLocked(player, source.Skill, source.Gem.Effect, spell.Lifetime);
        session.ScheduleContact(ProjectileRuntime.Time(at), Current, () =>
            ReleaseLinkedSpell(player, session, source, "Battlemage's Cry", contact, at,
                Stat.TriggeredByDivineCry, activeTarget: Current, announce: true));
    }
}
