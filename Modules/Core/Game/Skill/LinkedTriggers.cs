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

    static bool ReleaseLinkedSpell(Player player, ProjectileSession session, LinkedTriggerSource source,
        string family, (int X, int Y) aim, double at, uint spellFlag)
    {
        var state = LinkedTriggersOf(player);
        var key = (source.Gem.Host, source.Gem.Group, family);
        bool Same(LinkedTriggerSource a, LinkedTriggerSource b) => SameTriggerSource(a.Gem, b.Gem)
            && a.Spells.Length == b.Spells.Length && a.Spells.Zip(b.Spells)
                .All(p => p.First.Skill == p.Second.Skill && SameTriggerSource(p.First.Gem, p.Second.Gem));
        int next = state.Rotation.TryGetValue(key, out var previous) && Same(previous.Source, source) ? previous.Next : 0;
        double now = Math.Max(at, AreaTime(player));
        for (int i = 0; i < source.Spells.Length; i++)
        {
            int index = (next + i) % source.Spells.Length;
            var spell = source.Spells[index];
            var gem = SupportedSkill(player, spell.Skill);
            if (!SameTriggerSource(spell.Gem, gem) || now < state.ReadyAt.GetValueOrDefault(gem.Effect)
                || GemSkillInstanceDisabled(player, gem.Effect, spell.Skill, triggered: true) is not null) continue;
            bool Current() => session.Active && LinkedSourceCurrent(player, source)
                && SameTriggerSource(spell.Gem, SupportedSkill(player, spell.Skill));
            var release = PrepareTriggeredPayload(player, session, spell.Skill, aim, Current);
            if (release is null) continue;
            var trigger = gem.Supports.Single(s => SupportHas(s, spellFlag));
            if (!PayMana(player, spell.Skill, out uint mana, out uint life))
            {
                state.Rotation[key] = (source, (index + 1) % source.Spells.Length);
                return false;
            }
            int milliseconds = Math.Max(SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
                SkillsFor(player).CooldownMs(trigger.Gem.Effect, trigger.Gem.Level));
            var playerStats = CharacterPlayerStats(player);
            double rate = Math.Max(0, 1 + (GolemStat(playerStats, Stat.VirtualCooldownSpeedPlusPct)
                + GolemStat(playerStats, Stat.BaseSpellCooldownSpeedPlusPct) + GolemStat(gem.Stats, Stat.BaseSpellCooldownSpeedPlusPct)
                + GolemStat(gem.Stats, Stat.BaseCooldownSpeedPlusPct) + GolemStat(gem.Stats, Stat.VirtualCooldownSpeedPlusPct)) / 100d);
            state.ReadyAt[gem.Effect] = milliseconds == 0 ? now
                : rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
            state.Rotation[key] = (source, (index + 1) % source.Spells.Length);
            release();
            if (mana > 0 || life > 0)
                session.Enqueue(() => SendSkillCost(player, life, family + " cost"));
            return true;
        }
        return false;
    }
}
