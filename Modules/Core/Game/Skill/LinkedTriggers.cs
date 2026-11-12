using System.Runtime.CompilerServices;

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
        public readonly Dictionary<(uint Host, int Group, string Family), (LinkedTriggerSource Source, int Next)> Rotation = [];
    }
    static readonly ConditionalWeakTable<ClientState, LinkedTriggerState> LinkedTriggers = new();
    static LinkedTriggerState LinkedTriggersOf(ClientState conn)
    {
        if (LinkedTriggers.TryGetValue(conn, out var state) && state.Epoch == conn.ProjectileEpoch
            && ReferenceEquals(state.Rules, CombatLifetime)) return state;
        LinkedTriggers.Remove(conn);
        LinkedTriggers.Add(conn, state = new(conn.ProjectileEpoch, CombatLifetime));
        return state;
    }
    static bool SupportHas(GemSupports.Applied support, string name) => Stats.Key(name) is uint key
        && support.Stats.Any(s => s.Key == key && s.Value != 0);
    static bool LinkedSourceCurrent(ClientState conn, LinkedTriggerSource source) => CurrentGameRules
        && source.Epoch == conn.ProjectileEpoch && ReferenceEquals(source.Rules, CombatLifetime)
        && !conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && !conn.Paused && conn.PlayerLife > 0
        && SameTriggerSource(source.Gem, SupportedSkill(conn, source.Skill));

    public static LinkedTriggerSource? CaptureCriticalTriggerSource(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        if (!SkillsFor(conn).IsAttack(gem.Effect) || gem.SourceGem is null || PlacedKind(gem) is not (false, false)
            || GolemStat(gem.Stats, "is_totem") != 0 || GolemStat(gem.Stats, "cast_linked_spells_on_attack_crit_%") <= 0) return null;
        var spells = CaptureLinkedSpells(conn, gem, "cast_linked_spells_on_attack_crit_%", "cast_spell_on_linked_attack_crit");
        return spells.Length == 0 ? null : new(skill, gem, spells, conn.ProjectileEpoch, CombatLifetime);
    }
    static LinkedSpell[] CaptureLinkedSpells(ClientState conn, SupportedGem source, string sourceFlag, string spellFlag) =>
        [.. GemSkillEffects(conn).Select(s => new LinkedSpell(s.Id, SupportedSkill(conn, s.Id)))
            .Where(s => s.Gem.Host == source.Host && s.Gem.Group == source.Group
                && TriggeredSpellExecutable(conn, s.Skill, s.Gem)
                && s.Gem.Supports.Any(trigger => SupportHas(trigger, spellFlag)
                    && source.Supports.Any(driver => driver.Gem.Socket == trigger.Gem.Socket && SupportHas(driver, sourceFlag))))];

    // Called only after attack accuracy, block and critical confirmation, including lethal critical hits.
    public static bool ReleaseCriticalTrigger(ClientState conn, ProjectileSession session, LinkedTriggerSource? source,
        bool critical, bool hostile, (int X, int Y) aim, double at)
    {
        if (!critical || !hostile || source is null || !LinkedSourceCurrent(conn, source) || !session.Active) return false;
        return ReleaseLinkedSpell(conn, session, source, "CoC", aim, at, "cast_spell_on_linked_attack_crit");
    }

    static bool ReleaseLinkedSpell(ClientState conn, ProjectileSession session, LinkedTriggerSource source,
        string family, (int X, int Y) aim, double at, string spellFlag)
    {
        var state = LinkedTriggersOf(conn);
        var key = (source.Gem.Host, source.Gem.Group, family);
        bool Same(LinkedTriggerSource a, LinkedTriggerSource b) => SameTriggerSource(a.Gem, b.Gem)
            && a.Spells.Length == b.Spells.Length && a.Spells.Zip(b.Spells)
                .All(p => p.First.Skill == p.Second.Skill && SameTriggerSource(p.First.Gem, p.Second.Gem));
        int next = state.Rotation.TryGetValue(key, out var previous) && Same(previous.Source, source) ? previous.Next : 0;
        double now = Math.Max(at, conn.Clock.Elapsed.TotalSeconds);
        for (int i = 0; i < source.Spells.Length; i++)
        {
            int index = (next + i) % source.Spells.Length;
            var spell = source.Spells[index];
            var gem = SupportedSkill(conn, spell.Skill);
            if (!SameTriggerSource(spell.Gem, gem) || now < state.ReadyAt.GetValueOrDefault(gem.Effect)
                || GemSkillInstanceDisabled(conn, gem.Effect, spell.Skill, triggered: true) is not null) continue;
            bool Current() => session.Active && LinkedSourceCurrent(conn, source)
                && SameTriggerSource(spell.Gem, SupportedSkill(conn, spell.Skill));
            var release = PrepareTriggeredPayload(conn, session, spell.Skill, aim, Current);
            if (release is null) continue;
            var trigger = gem.Supports.Single(s => SupportHas(s, spellFlag));
            if (!PayMana(conn, spell.Skill, out uint mana, out uint life))
            {
                state.Rotation[key] = (source, (index + 1) % source.Spells.Length);
                return false;
            }
            int milliseconds = Math.Max(SkillsFor(conn).CooldownMs(gem.Effect, gem.Level),
                SkillsFor(conn).CooldownMs(trigger.Gem.Effect, trigger.Gem.Level));
            var player = CharacterPlayerStats(conn);
            double rate = Math.Max(0, 1 + (GolemStat(player, "virtual_cooldown_speed_+%")
                + GolemStat(player, "base_spell_cooldown_speed_+%") + GolemStat(gem.Stats, "base_spell_cooldown_speed_+%")
                + GolemStat(gem.Stats, "base_cooldown_speed_+%") + GolemStat(gem.Stats, "virtual_cooldown_speed_+%")) / 100d);
            state.ReadyAt[gem.Effect] = milliseconds == 0 ? now
                : rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
            state.Rotation[key] = (source, (index + 1) % source.Spells.Length);
            release();
            if (mana > 0 || life > 0)
                session.Enqueue(() => SendSkillCost(session.remote, session.stream, session.ciphers, conn, life, family + " cost"));
            return true;
        }
        return false;
    }
}
