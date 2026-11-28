using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed record LinkedSpell(ushort Skill, SupportedGem Gem);
    public sealed record LinkedTriggerSource(ushort Skill, SupportedGem Gem, LinkedSpell[] Spells,
        int Epoch, object? Rules);
    sealed class LinkedTriggerState(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public readonly Dictionary<int, double> ReadyAt = [];
        public Dictionary<ushort, ManaforgedProgress>? Manaforged;
        public readonly Dictionary<(uint Host, int Group, string Family), (LinkedSpell[]? Spells, int Next)> Rotation = [];
    }
    static readonly Store<Player, LinkedTriggerState> LinkedTriggers = new();
    static LinkedTriggerState LinkedTriggersOf(Player player)
    {
        if (LinkedTriggers.TryGetValue(player, out var state) && state.Epoch == player.ProjectileEpoch
            && ReferenceEquals(state.Rules, CombatLifetime)) return state;
        LinkedTriggers.Remove(player);
        LinkedTriggers.Add(player, state = new(player.ProjectileEpoch, CombatLifetime));
        return state;
    }
    static bool SupportHas(GemSupports.Applied support, uint key) => support.Stats.Any(s => s.Key == key && s.Value != 0);
    static bool LinkedSourceCurrent(Player player, LinkedTriggerSource source) => CurrentGameRules
        && source.Epoch == player.ProjectileEpoch && ReferenceEquals(source.Rules, CombatLifetime)
        && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && !player.Paused && player.PlayerLife > 0
        && SameTriggerSource(source.Gem, SupportedSkill(player, source.Skill));

    static readonly RuleTable<LinkSkills.Link, LinkedTriggerSource> IntuitiveLinkSources = new();

    static void CaptureIntuitiveLink(Player player, LinkSkills.Link link, SupportedGem gem)
    {
        if (CurrentPlayerSkillSourceLocked(player, link.SkillId) is not { } active) return;
        var spells = GemSkillEffects(player).Select(row => new LinkedSpell(row.Id, SupportedSkill(player, row.Id)))
            .Where(spell => spell.Gem.Host == gem.Host && spell.Gem.Group == gem.Group
                && spell.Gem.Supports.Any(support => support.Effect.Id == "SupportIntuitiveLink"
                    && support.Gem.Socket == active.Identity.Socket && SupportHas(support, Stat.TriggerOnTriggerLinkTargetHit))
                && TriggeredSpellExecutable(player, spell.Skill, spell.Gem)).ToArray();
        if (spells.Length > 0)
            IntuitiveLinkSources.Add(link, new(link.SkillId, gem, spells, player.ProjectileEpoch, CombatLifetime));
    }

    static void ReleaseIntuitiveLinkLocked(Player player, GolemState.Golem fighter, (int X, int Y) aim, double now)
    {
        if (fighter.Monster.Dead || ActiveMinionLink(player, fighter, now) is not { EffectId: "IntuitiveLink" } link
            || !IntuitiveLinkSources.TryGetValue(link, out var source) || !GolemCurrent(player, fighter)
            || !LinkedSourceCurrent(player, source)) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;
        bool StillLinked() => GolemCurrent(player, fighter) && !fighter.Monster.Dead
            && ReferenceEquals(ActiveMinionLink(player, fighter, AreaTime(player)), link);
        ReleaseLinkedSpell(player, session, source, "Intuitive Link", aim, now, Stat.TriggerOnTriggerLinkTargetHit,
            fighter.Monster.Walk.Position(now), StillLinked);
    }

    /// <summary>Triggers Intuitive Link's supported spells when a linked companion hits an enemy.</summary>
    public static void ReleaseCompanionIntuitiveLinkLocked(Player player, ActiveCompanion companion, (int X, int Y) aim, double now)
    {
        if (!CompanionAliveInArea(player, companion)
            || ActiveCompanionLink(player, companion.EntityId, now) is not { EffectId: "IntuitiveLink" } link
            || !IntuitiveLinkSources.TryGetValue(link, out var source)
            || !LinkedSourceCurrent(player, source)) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;
        bool StillLinked() => CompanionAliveInArea(player, companion)
            && ReferenceEquals(ActiveCompanionLink(player, companion.EntityId, AreaTime(player)), link);
        ReleaseLinkedSpell(player, session, source, "Intuitive Link", aim, now, Stat.TriggerOnTriggerLinkTargetHit,
            companion.Monster.Walk.Position(now), StillLinked);
    }

    static void TriggerIntuitivePlayerLinkLocked(Player peer, SpawnedMonster enemy, double now)
    {
        if (peer.Links.Incoming is not { } incoming || incoming.Link.EffectId != "IntuitiveLink"
            || enemy is not { Hostile: true, Invulnerable: false, EntranceGrace: false }
            || !ActivePlayerLink(incoming.Source, peer, incoming.Link, now)
            || !IntuitiveLinkSources.TryGetValue(incoming.Link, out var source)) return;

        var caster = incoming.Source;
        var contact = enemy.Walk.Position(now);
        var origin = peer.Walk.Position(now);
        // Queue across actor actions so an immediate spell hit (including a reciprocal link) does not recurse.
        peer.Area.Defer(_ =>
        {
            double at = AreaTime(caster);
            bool StillLinked() => ActivePlayerLink(caster, peer, incoming.Link, AreaTime(caster));
            if (!StillLinked() || !LinkedSourceCurrent(caster, source)) return;
            var session = ProjectilesFor(caster);
            if (session.Active)
                ReleaseLinkedSpell(caster, session, source, "Intuitive Link", contact, at, Stat.TriggerOnTriggerLinkTargetHit,
                    origin, StillLinked);
        });
    }

    public static LinkedTriggerSource? CaptureCriticalTriggerSource(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (!SkillsFor(player).IsAttack(gem.Effect) || gem.SourceGem is null || PlacedKind(gem) is not (false, false)
            || GolemStat(gem.Stats, Stat.IsTotem) != 0 || GolemStat(gem.Stats, Stat.CastLinkedSpellsOnAttackCritPct) <= 0) return null;
        var spells = CaptureLinkedSpells(player, gem, Stat.CastLinkedSpellsOnAttackCritPct, Stat.CastSpellOnLinkedAttackCrit);
        return spells.Length == 0 ? null : new(skill, gem, spells, player.ProjectileEpoch, CombatLifetime);
    }
    static LinkedSpell[] CaptureLinkedSpells(Player player, SupportedGem source, uint sourceFlag, uint spellFlag) =>
        [.. GemSkillEffects(player).Select(s => new LinkedSpell(s.Id, SupportedSkill(player, s.Id)))
            .Where(s => s.Gem.Host == source.Host && s.Gem.Group == source.Group
                && TriggeredSpellExecutable(player, s.Skill, s.Gem)
                && s.Gem.Supports.Any(trigger => SupportHas(trigger, spellFlag)
                    && source.Supports.Any(driver => driver.Gem.Socket == trigger.Gem.Socket && SupportHas(driver, sourceFlag))))];

    // Called only after attack accuracy, block and critical confirmation, including lethal critical hits.
    public static bool ReleaseCriticalTrigger(Player player, ProjectileSession session, LinkedTriggerSource? source,
        bool critical, bool hostile, (int X, int Y) aim, double at)
    {
        if (!critical || !hostile || source is null || !LinkedSourceCurrent(player, source) || !session.Active) return false;
        return ReleaseLinkedSpell(player, session, source, "CoC", aim, at, Stat.CastSpellOnLinkedAttackCrit);
    }

    // Cast on Melee Kill: a linked melee attack's killing blow releases the next linked spell, as a critical does.
    public static LinkedTriggerSource? CaptureMeleeKillTriggerSource(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (!SkillsFor(player).IsAttack(gem.Effect) || !SkillsFor(player).IsMelee(gem.Effect) || gem.SourceGem is null
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0
            || GolemStat(gem.Stats, Stat.CastLinkedSpellsOnMeleeKillPct) <= 0) return null;
        var spells = CaptureLinkedSpells(player, gem, Stat.CastLinkedSpellsOnMeleeKillPct, Stat.CastSpellOnLinkedMeleeKill);
        return spells.Length == 0 ? null : new(skill, gem, spells, player.ProjectileEpoch, CombatLifetime);
    }

    // Called only for a hostile monster that the linked melee attack's own accepted hit killed.
    public static bool ReleaseMeleeKillTrigger(Player player, ProjectileSession session, LinkedTriggerSource? source,
        bool killed, bool hostile, (int X, int Y) aim, double at)
    {
        if (!killed || !hostile || source is null || !LinkedSourceCurrent(player, source) || !session.Active) return false;
        return ReleaseLinkedSpell(player, session, source, "CoMK", aim, at, Stat.CastSpellOnLinkedMeleeKill);
    }

    static bool SameLinkedSpells(LinkedSpell[] previous, LinkedSpell[] current)
    {
        if (ReferenceEquals(previous, current)) return true;
        if (previous.Length != current.Length) return false;
        for (int i = 0; i < previous.Length; i++)
            if (previous[i].Skill != current[i].Skill || !SameTriggerSource(previous[i].Gem, current[i].Gem))
                return false;
        return true;
    }

    // rotation: the host and group the cycle over the spells belongs to, when it is not the driving gem's own socket
    // group (an item's trigger cycles for the item: ReleaseItemCriticalTrigger).
    static bool ReleaseLinkedSpell(Player player, ProjectileSession session, LinkedTriggerSource source,
        string family, (int X, int Y) aim, double at, uint spellFlag, (int X, int Y)? origin = null,
        Func<bool>? activeTarget = null, (uint Host, int Group)? rotation = null, bool announce = false)
    {
        var state = LinkedTriggersOf(player);
        var key = (rotation?.Host ?? source.Gem.Host, rotation?.Group ?? source.Gem.Group, family);
        // Any qualifying driver in this group can continue the cycle over the same ordered spell sources.
        int next = state.Rotation.TryGetValue(key, out var previous) && previous.Spells is { } spells
            && SameLinkedSpells(spells, source.Spells)
            ? previous.Next : 0;
        double now = Math.Max(at, AreaTime(player));
        for (int i = 0; i < source.Spells.Length; i++)
        {
            int index = (next + i) % source.Spells.Length;
            var spell = source.Spells[index];
            var gem = SupportedSkill(player, spell.Skill);
            if (!SameTriggerSource(spell.Gem, gem) || now < state.ReadyAt.GetValueOrDefault(gem.Effect)
                || GemSkillInstanceDisabled(player, gem.Effect, spell.Skill, triggered: true) is not null) continue;
            bool Current() => session.Active && LinkedSourceCurrent(player, source)
                && SameTriggerSource(spell.Gem, SupportedSkill(player, spell.Skill)) && (activeTarget?.Invoke() ?? true);
            var release = PrepareTriggeredPayload(player, session, spell.Skill, aim, Current, origin);
            if (release is null) continue;
            if (gem.Supports.FirstOrDefault(s => SupportHas(s, spellFlag)) is not { } trigger) continue;
            if (!PayMana(player, spell.Skill, out uint mana, out uint life))
            {
                state.Rotation[key] = (source.Spells, (index + 1) % source.Spells.Length);
                return false;
            }
            int milliseconds = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
                SkillsFor(player).CooldownMs(trigger.Gem.Effect, trigger.Gem.Level));
            if (gem.Stats.GetValueOrDefault(Stat.SpellCastTimeAddedToCooldownIfTriggered) != 0)
                milliseconds += SkillsFor(player).CastTimeMs(gem.Effect);
            var playerStats = CharacterPlayerStats(player);
            double rate = Math.Max(0, 1 + (GolemStat(playerStats, Stat.VirtualCooldownSpeedPlusPct)
                + GolemStat(playerStats, Stat.BaseSpellCooldownSpeedPlusPct) + GolemStat(gem.Stats, Stat.BaseSpellCooldownSpeedPlusPct)
                + GolemStat(gem.Stats, Stat.BaseCooldownSpeedPlusPct) + GolemStat(gem.Stats, Stat.VirtualCooldownSpeedPlusPct)) / 100d);
            state.ReadyAt[gem.Effect] = milliseconds == 0 ? now
                : rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
            state.Rotation[key] = (source.Spells, (index + 1) % source.Spells.Length);
            if (announce)
            {
                bool lockstep = player.Connection.LockstepClient;
                ushort sequence = NextLockstepSequence(player);
                if (!lockstep) sequence &= 0x7fff;
                session.World(player.Id, InstanceProtocol.InstantAction(player.Id, spell.Skill, aim.X, aim.Y,
                    sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                    $"0x01ce {family} triggers skill 0x{spell.Skill:x4} at {aim}");
            }
            release();
            if (mana > 0 || life > 0)
                session.Enqueue(() => SendSkillCost(player, life, family + " cost"));
            return true;
        }
        return false;
    }
}
