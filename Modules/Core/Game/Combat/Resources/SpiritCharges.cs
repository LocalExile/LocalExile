using GameServer.Core.World;

public static partial class Game
{
    const string SpiritBurstEffect = "SpiritBurst";
    const ushort SpiritChargeBuffRow = 31;
    const double SpiritChargeSeconds = 10;
    static readonly RuleTable<Player, SpiritChargeState> SpiritCharges = new();

    sealed class SpiritChargeState
    {
        public int Held;
        public double Until;
    }

    static int SpiritChargeLimit(int maximum, int perJewel, int jewels) =>
        (int)Math.Clamp((long)maximum + (long)perJewel * jewels, 0, ushort.MaxValue);

    static int SpiritChargeLimit(IReadOnlyDictionary<uint, int> stats) =>
        SpiritChargeLimit(stats.GetValueOrDefault(Stat.MaximumSpiritCharges),
            stats.GetValueOrDefault(Stat.MaximumSpiritChargesPerAbyssJewelEquipped),
            stats.GetValueOrDefault(Stat.NumberOfAbyssJewels));

    static void ChangeSpiritCharges(Player player, SpiritChargeState state, int held, double at)
    {
        state.Held = held;
        if (held == 0) player.Area.Buffs.Clear(player.Id, SpiritChargeBuffRow, player.Id);
        else player.Area.Buffs.Set(player.Id, new(SpiritChargeBuffRow, player.Id,
            Stacks: (ushort)held, U68: Buff.Seconds(state.Until - at)));
        MarkPlayerStats(player, PlayerStatStep.SpiritCharges);
    }

    static void SpiritChargeOnKill(Player player, double at)
    {
        var stats = StatSourceValues(player);
        int cap = SpiritChargeLimit(stats);
        if (cap == 0 || !RandomRolls.Chance(stats.GetValueOrDefault(Stat.GainSpiritChargeOnKillPctChance), 100, AreaRng(player))) return;
        var state = SpiritCharges.GetOrCreateValue(player);
        int held = at < state.Until ? state.Held : 0;
        state.Until = at + SpiritChargeSeconds;
        ChangeSpiritCharges(player, state, Math.Min(cap, held + 1), at);
    }

    static void SpiritBurstOnSkillUse(Player player, (int X, int Y) aim, double at)
    {
        if (!SpiritCharges.TryGetValue(player, out var state) || state.Held == 0 || !CurrentGameWork
            || player.Closed || player.LeavingArea || player.PendingTransfer is not null
            || player.Paused || player.PlayerLife == 0 || InTown(player)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        int held = at < state.Until ? Math.Min(state.Held, SpiritChargeLimit(StatSourceValues(player))) : 0;
        if (held != state.Held) ChangeSpiritCharges(player, state, held, at);
        if (held == 0) return;

        int effect = SkillsFor(player).EffectRow(SpiritBurstEffect);
        var skill = GemSkillEffects(player).FirstOrDefault(gem => gem.Effect == effect);
        if (skill.Name is null || GemSkillInstanceDisabled(player, effect, skill.Id, triggered: true) is not null) return;
        var gem = SupportedSkill(player, skill.Id);
        if (!RandomRolls.Chance(gem.Stats.GetValueOrDefault(Stat.TriggerOnSkillUsePctIfYouHaveASpiritCharge), 100, AreaRng(player))) return;
        var session = ProjectilesFor(player);
        int epoch = player.ProjectileEpoch;
        var rules = CombatLifetime;
        bool Current() => session.Active && CurrentGameWork && player.ProjectileActive(epoch)
            && ReferenceEquals(rules, CombatLifetime) && !player.Paused && player.PlayerLife > 0
            && player.PendingTransfer is null && SameTriggerSource(gem, SupportedSkill(player, skill.Id));
        var release = PrepareTriggeredPayload(player, session, skill.Id, aim, Current);
        if (release is null || !PayMana(player, skill.Id, out uint mana, out uint life)) return;
        ChangeSpiritCharges(player, state, held - 1, at);
        release();
        if (mana > 0 || life > 0) session.Enqueue(() => SendSkillCost(player, life, "Spirit Burst cost"));
    }

    static readonly uint[] SpiritChargeGainStats =
    [
        Stat.PhysicalDamagePctToAddAsFire,
        Stat.PhysicalDamagePctToAddAsCold,
        Stat.PhysicalDamagePctToAddAsLightning,
    ];

    static void ApplySpiritChargeStats(Player player, StatMap stats)
    {
        int cap = SpiritChargeLimit(stats);
        if (cap > 0) stats[Stat.MaximumSpiritCharges] = cap;
        if (!SpiritCharges.TryGetValue(player, out var state) || state.Held == 0) return;
        double now = AreaTime(player);
        int held = player.PlayerLife == 0 || now >= state.Until ? 0 : Math.Min(state.Held, cap);
        if (held != state.Held) ChangeSpiritCharges(player, state, held, now);
        if (held == 0) return;
        StatsChangeIn(state.Until - now);
        stats[Stat.CurrentSpiritCharges] = held;
        long gain = (long)held * stats.GetValueOrDefault(Stat.PhysicalDamagePctToAddAsEachElementPerSpiritCharge);
        if (gain == 0) return;
        foreach (uint type in SpiritChargeGainStats)
            stats[type] = ActorScale.Saturate(stats.GetValueOrDefault(type) + gain);
    }
}
