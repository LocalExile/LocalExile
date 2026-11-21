static partial class Game
{
    static bool TriggerSummonSpell(Player player, SupportedGem gem)
    {
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        return effect == "Desecrate" || GolemData.Find(effect) is not null || SummonData.Find(effect) is not null;
    }

    internal static async Task ReleaseTriggeredSummon(Player player, ProjectileSession session, ushort source,
        (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(player, source);
        var lifetime = CombatLifetime;
        bool Current() => current() && session.Active && CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime)
            && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && !player.Paused && player.PlayerLife > 0
            && SameTriggerSource(gem, SupportedSkill(player, source));
        if (!Current() || !gem.RequirementsMet) return;
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        if (effect == "Desecrate")
            await Desecrate(player, source, aim, Current, triggered: true);
        else if (GolemData.Find(effect) is { } golem)
            await CastGolem(player, source, golem, aim,
                player.ActionInterruptionGeneration, Current, triggered: true);
        else if (SummonData.Find(effect) is { } summon)
            await CastSummon(player, source, summon, aim, 0,
                player.ActionInterruptionGeneration, Current, triggered: true);
    }
}
