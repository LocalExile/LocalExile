public static partial class Game
{
    sealed class ManaforgedProgress(SupportedGem gem)
    {
        public readonly SupportedGem Gem = gem;
        public long ManaSpent;
    }

    static bool ManaforgedAttackExecutable(Player player, ushort skillId, SupportedGem gem)
    {
        if (gem.Stats.GetValueOrDefault(Stat.SupportManaforgedArrowsManaCostPctThreshold) <= 0) return false;
        var skills = SkillsFor(player);
        return skills.IsAttack(gem.Effect) && !skills.IsChannelled(gem.Effect)
            && PlacedKind(gem) is (false, false) && !IsTotemSkill(player, skillId)
            && gem.Supports.Count(s => s.Effect.Added.Contains(36)) == 1
            && skills.VaalBudget(gem.Effect, gem.Level) is null && TriggerPayloadAvailable(player, gem);
    }

    // A normal paid bow use is the driver. Automatic payload payment does not enter this callback.
    static void RecordPaidBowAttackLocked(Player player, ushort driver, SupportedGem source, uint mana, (int X, int Y) aim)
    {
        var skills = SkillsFor(player);
        if (!skills.IsAttack(source.Effect) || !WeaponProfileFor(player, activeSet: true).Bow
            || source.Stats.GetValueOrDefault(Stat.IsTotem) != 0 || source.Supports.Any(s => s.Effect.Added.Contains(36))) return;

        var state = LinkedTriggersOf(player);
        var progress = state.Manaforged;
        var live = progress is { Count: > 0 } ? new HashSet<ushort>() : null;
        double now = AreaTime(player);
        foreach (var skill in GemSkillEffects(player))
        {
            if (skill.Id == driver || !skills.IsAttack(skill.Effect)) continue;
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet || !ManaforgedAttackExecutable(player, skill.Id, gem)) continue;
            var support = gem.Supports.FirstOrDefault(s => SupportHas(s, Stat.SupportManaforgedArrowsManaCostPctThreshold));
            if (support is null) continue;
            live?.Add(skill.Id);

            state.Manaforged ??= [];
            if (!state.Manaforged.TryGetValue(skill.Id, out var counter) || !SameTriggerSource(counter.Gem, gem))
                state.Manaforged[skill.Id] = counter = new(gem);
            bool payable = SkillCostLocked(player, skill.Id, gem, out uint manaCost, out _, out _, out _, out _);
            long required = (long)manaCost * gem.Stats.GetValueOrDefault(Stat.SupportManaforgedArrowsManaCostPctThreshold) / 100 + 1;
            counter.ManaSpent = Math.Min(required, counter.ManaSpent + mana);
            if (!payable || counter.ManaSpent < required || now < state.ReadyAt.GetValueOrDefault(gem.Effect)
                || GemSkillInstanceDisabled(player, gem.Effect, skill.Id, triggered: true) is not null) continue;

            var session = ProjectilesFor(player);
            bool Current() => session.Active && CurrentGameRules && ReferenceEquals(state.Rules, CombatLifetime)
                && state.Epoch == player.ProjectileEpoch && !player.Paused && !player.LeavingArea
                && player.PendingTransfer is null && player.PlayerLife > 0 && WeaponProfileFor(player, activeSet: true).Bow
                && SameTriggerSource(gem, SupportedSkill(player, skill.Id));
            if (TryReleaseAutomaticPayload(player, session, skill.Id, gem, support, aim, Current, now,
                "Manaforged Arrows", out _))
                counter.ManaSpent = 0;
        }

        if (live is not null)
            foreach (ushort id in progress!.Keys.Where(id => !live.Contains(id)).ToArray()) progress.Remove(id);
    }
}
