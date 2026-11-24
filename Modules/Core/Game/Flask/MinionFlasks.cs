public static partial class Game
{
    static IEnumerable<GolemState.Golem> FlaskMinions(Player player, bool raisedOnly)
    {
        IEnumerable<GolemState.Golem> minions = player.Summons.Fighters.Values;
        if (!raisedOnly) minions = minions.Concat(player.Golems.All.Values).Concat(player.Reservations.Fighters.Values);
        return minions.Where(minion => !minion.Monster.Dead && minion.Monster.Life > 0 && GolemCurrent(player, minion)
            && (!raisedOnly || minion.Definition.Kind is "Zombie" or "Spectre"));
    }

    static bool CanRecoverRaisedMinionsWithFlask(Player player, Flasks.Flask flask, Func<uint, int> character) =>
        flask.LifePerUse > 0 && character(Stat.FlasksApplyToYourZombiesAndSpectres) != 0
        && FlaskMinions(player, raisedOnly: true).Any(minion => minion.Monster.Life < minion.Monster.MaxLife);

    static void ApplyMinionRecoveryFlasksLocked(Player player, Item flask, byte slot, Flasks.Flask type,
        FlaskRecovery.Use recovery, IReadOnlyDictionary<uint, int> local, Func<uint, int> character, double now)
    {
        if (type.LifePerUse <= 0) return;
        if (character(Stat.FlasksApplyToYourZombiesAndSpectres) != 0)
        {
            foreach (var minion in FlaskMinions(player, raisedOnly: true))
                ApplyMinionRecoveryFlaskLocked(player, minion, flask, slot, type, local, character, now);
            return;
        }
        double share = (local.GetValueOrDefault(Stat.LocalFlaskMinionHealPct)
            + (long)character(Stat.FlaskMinionHealPct)) / 100d;
        if (share <= 0) return;
        foreach (var minion in FlaskMinions(player, raisedOnly: false))
            StartMinionRecoveryFlaskLocked(player, minion, slot, recovery, now, share);
    }

    static void ApplyMinionRecoveryFlaskLocked(Player player, GolemState.Golem minion, Item flask, byte slot,
        Flasks.Flask type, IReadOnlyDictionary<uint, int> local, Func<uint, int> character, double now)
    {
        var recovery = FlaskRecovery.For(type, flask, local, stat => MinionFlaskStat(minion, character, stat),
            minion.Monster.Life, minion.Monster.MaxLife, finalMultiplier: MapFlaskEffectMultiplier(player));
        StartMinionRecoveryFlaskLocked(player, minion, slot, recovery, now);
    }

    static int MinionFlaskStat(GolemState.Golem minion, Func<uint, int> character, uint stat) =>
        stat == Stat.FlaskDurationPlusPct ? character(Stat.FlaskDurationOnMinionsPlusPct) : minion.Monster.Effects[stat];

    static void StartMinionRecoveryFlaskLocked(Player player, GolemState.Golem minion, byte slot,
        FlaskRecovery.Use recovery, double now, double share = 1)
    {
        RecoverMinionLifeLocked(player, minion, (long)Math.Min(long.MaxValue, Math.Floor(recovery.InstantLife * share)));
        if (minion.Monster.Life == minion.Monster.MaxLife && !recovery.LifeStaysAtFull) return;
        var life = recovery with
        {
            Life = recovery.Life * share,
            Mana = 0,
            ManaAtEnd = 0,
        };
        foreach (var effect in Flasks.RecoveryEffects(life, slot, now, () => player.NextBuff++, FlaskEffectLifeBuffRow, FlaskEffectManaBuffRow))
        {
            (minion.RecoveryFlasks ??= new()).Start(effect);
            if (FlaskBuffs)
                player.Area.Buffs.Set(minion.Id, Flasks.Buff(player.Id, effect.Buff, effect.BuffRow, slot,
                    effect.Ends - now, [(uint)Math.Clamp(Math.Round(effect.LifePerSecond * 60), 0, uint.MaxValue)]));
        }
        if (recovery.ExtraLife > 0)
            (minion.RecoveryFlasks ??= new()).Start(new(slot, 0, 0, recovery.ExtraLife * share / FlaskRecovery.ExtraLifeSeconds,
                0, now, now + FlaskRecovery.ExtraLifeSeconds));
    }

    static double AdvanceMinionRecoveryFlasksLocked(Player player, GolemState.Golem minion, double from, double to)
    {
        if (minion.RecoveryFlasks is not { } recovery) return 0;
        var result = recovery.Advance(from, to);
        ClearMinionRecoveryFlaskBuffs(player, minion, result.Ended);
        if (!recovery.Any) minion.RecoveryFlasks = null;
        return LifeRecovery.NonLeechBlocked(key => minion.Monster.Effects[key]) ? 0 : result.Life;
    }

    static void EndMinionRecoveryFlasksLocked(Player player, GolemState.Golem minion, Func<Flasks.Effect, bool> predicate)
    {
        if (minion.RecoveryFlasks is not { } recovery) return;
        ClearMinionRecoveryFlaskBuffs(player, minion, recovery.End(predicate));
        if (!recovery.Any) minion.RecoveryFlasks = null;
    }

    static void ClearMinionRecoveryFlaskBuffs(Player player, GolemState.Golem minion, IEnumerable<Flasks.Effect> ended)
    {
        foreach (var effect in ended)
            if (effect.BuffRow != 0) player.Area.Buffs.Clear(minion.Id, effect.BuffRow, player.Id, tag: effect.Buff);
    }

    static void ApplyMinionUtilityFlaskLocked(Player player, GolemState.Golem minion, InventoryItem flask,
        ItemCatalog.BaseType type, UtilityFlasks.Utility utility, IReadOnlyDictionary<uint, int> local,
        Func<uint, int> character, double now)
    {
        var (stats, seconds) = ItemUtilityFlaskFor(utility, flask.Item, local,
            stat => MinionFlaskStat(minion, character, stat), finalMultiplier: MapFlaskEffectMultiplier(player));
        var effects = minion.UtilityFlasks ??= new();
        if (effects.Of(flask.Id) is { } previous) ClearMinionUtilityFlaskBuffs(player, minion, previous);
        var effect = new UtilityFlasks.Active(flask.Id, flask.X, type.Name, utility, player.NextBuff++,
            utility.Extra is null ? null : player.NextBuff++, stats, now, now + seconds);
        effects.Start(effect);
        uint duration = Buff.Seconds(seconds);
        player.Area.Buffs.Set(minion.Id, new(utility.BuffRow, player.Id, U68: duration, Tag: effect.Buff));
        if (utility.Extra is { } extra && effect.ExtraBuff is ushort tag)
            player.Area.Buffs.Set(minion.Id, new(extra.BuffRow, player.Id, U68: duration, Tag: tag));
        RefreshMinionFlaskStatsLocked(player, minion, now);
    }

    static void RefreshMinionFlaskStatsLocked(Player player, GolemState.Golem minion, double now)
    {
        if (!GolemCurrent(player, minion) || minion.Monster.Dead) return;
        RefreshGolemLocked(player, minion, minion.Player, now);
        var body = minion.Monster;
        body.Walk.SetSpeed(GolemWalkSpeed(body, now, MonsterActionFactor(body, now)), now);
    }

    static IReadOnlyList<UtilityFlasks.Source> MinionUtilityFlaskSources(Player player, GolemState.Golem minion, double now)
    {
        var direct = minion.UtilityFlasks?.Sources(now);
        if (player.Links.Flasks.Count == 0 || minion.Id == 0) return direct ?? [];
        var linked = player.Links.Flasks.Where(flask => flask.Target == minion.Id && now < flask.Until)
            .SelectMany(flask => flask.Stats);
        return direct is null ? [.. linked] : [.. direct, .. linked];
    }

    static void EndMinionUtilityFlasksLocked(Player player, GolemState.Golem minion, double now, bool all = false)
    {
        if (minion.UtilityFlasks is not { } effects) return;
        foreach (var effect in effects.End(now, all, out bool _)) ClearMinionUtilityFlaskBuffs(player, minion, effect);
        if (!effects.Any) minion.UtilityFlasks = null;
    }

    static void ClearMinionUtilityFlaskBuffs(Player player, GolemState.Golem minion, UtilityFlasks.Active effect)
    {
        player.Area.Buffs.Clear(minion.Id, effect.Flask.BuffRow, player.Id, tag: effect.Buff);
        if (effect.Flask.Extra is { } extra && effect.ExtraBuff is ushort tag)
            player.Area.Buffs.Clear(minion.Id, extra.BuffRow, player.Id, tag: tag);
    }

    static void ClearMinionFlasksLocked(Player player, GolemState.Golem minion, double now)
    {
        EndMinionRecoveryFlasksLocked(player, minion, _ => true);
        EndMinionUtilityFlasksLocked(player, minion, now, all: true);
    }
}
