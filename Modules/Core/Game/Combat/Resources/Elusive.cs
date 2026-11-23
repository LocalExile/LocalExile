using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public sealed class ElusiveState
{
    public object? Rules;
    public int Epoch;
    public string Source = "";
    public int? NightbladeMultiplier;
    public double Started, CarriedAge, Initial, Rate = 20, Minimum, RisingSeconds;
    public ushort Buff;
    public ElusiveGrant Grant;
    public readonly HashSet<ushort> Retired = [];
    public double Age(double now) => CarriedAge + Math.Max(0, now - Started);
    public double Effect(double now)
    {
        if ((Source.Length == 0 && NightbladeMultiplier is null) || Initial <= 0) return 0;
        double age = Age(now), rise = Math.Min(age, RisingSeconds);
        double effect = Initial + Rate * rise - Rate * Math.Max(0, age - RisingSeconds);
        return effect > Minimum ? effect : 0;
    }
    public double Remaining(double now) => Math.Max(0, RisingSeconds * 2 + (Initial - Minimum) / Rate - Age(now));
    public void Clear()
    {
        if (Buff != 0) Retired.Add(Buff);
        Buff = 0; Source = ""; NightbladeMultiplier = null;
        Started = CarriedAge = Initial = Minimum = RisingSeconds = 0; Rate = 20;
    }
}

public readonly record struct ElusiveGrant(int Critical, int Kill, int Effect, int PowerEffect, int LosePower, int LossRate, int Minimum, int RisingMs);

public static partial class Game
{
    public const uint ElusiveOnCritical = Stat.GainElusiveOnCritPctChance, ElusiveOnKill = Stat.GainElusiveOnKillChancePct;
    public const ushort ElusiveBuffRow = 1374;
    static readonly Store<Player, ElusiveState> Elusives = new();
    public static ElusiveState ElusiveOf(Player player) => Elusives.GetOrCreateValue(player);
    public sealed record CarriedElusive(string Source, double Age, double Initial, double Rate, double Minimum, double RisingSeconds)
    {
        public int? NightbladeMultiplier { get; init; }
    }

    static IReadOnlyDictionary<string, bool> RequalifyElusive(Player player, IReadOnlyDictionary<string, bool> flags,
        IReadOnlyDictionary<uint, int> raw, IEnumerable<Item>? activeItems = null)
    {
        if (player.LeavingArea) return WithFlags(flags, ("_while_affected_by_elusive", false), ("_while_elusive", false), ("_while_you_have_elusive", false));
        var state = ElusiveOf(player);
        bool alive = CurrentGameWork && !player.Closed && !player.RageRetired && player.PlayerLife > 0;
        int Get(uint n) => GolemStat(raw, n);
        string identity = "";
        if (alive && (Get(ElusiveOnCritical) > 0 || Get(ElusiveOnKill) > 0))
        {
            var active = activeItems?.ToArray();
            var sources = ItemsOf(player).EquipmentSnapshot()
                .Where(e => active is null ? !IsInactive(player, e.Item.Id) : active.Any(i => ReferenceEquals(i, e.Item.Item)))
                .Where(e => PlainItemSum(e.Item.Item, ItemsFor(player).Mods) is var item
                    && (item.GetValueOrDefault(ElusiveOnCritical) > 0 || item.GetValueOrDefault(ElusiveOnKill) > 0))
                .Select(e => e.Item.Id).Order().ToArray();
            identity = string.Join(',', sources) + ":" + Get(ElusiveOnCritical) + ":" + Get(ElusiveOnKill)
                + ":" + string.Join(',', player.Character.Passives.Where(n => PassivesFor(player).Find(n)?.Stats.Any(s =>
                    s.Key is ElusiveOnCritical or ElusiveOnKill) == true).Order());
            ResourceEffectsOf(player);
        }
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch || !alive
            || (state.NightbladeMultiplier is null && state.Source != identity))
        {
            state.Clear(); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch;
        }
        state.Source = identity;
        int Input(uint key) => Get(key);
        state.Grant = new(Input(ElusiveOnCritical), Input(ElusiveOnKill),
            Input(Stat.ElusiveEffectPlusPct) + Input(Stat.ElusiveEffectOnSelfPlusPct) + Input(Stat.BuffEffectOnSelfPlusPct),
            Input(Stat.ElusiveEffectOnSelfPlusPctPerPowerCharge), Input(Stat.LoseAPowerChargeWhenYouGainElusivePctChance),
            Input(Stat.ElusiveLossRatePlusPct), Input(Stat.ElusiveMinimumEffectPct), Input(Stat.ElusiveTicksUpInsteadForXMs));
        double now = ResourceEffectsTime(player);
        bool running = alive && state.Effect(now) > 0;
        player.ElusiveUntil = running ? now + state.Remaining(now) : 0;
        return WithFlags(flags, ("_while_affected_by_elusive", running), ("_while_elusive", running), ("_while_you_have_elusive", running));
    }

    public static bool TryGainElusiveLocked(Player player, bool critical, bool kill, double now, Random? random = null) =>
        TryGainElusiveLocked(player, critical, kill, now, random, null);

    static bool TryGainElusiveLocked(Player player, bool critical, bool kill, double now, Random? random,
        IReadOnlyDictionary<uint, int>? skillStats)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return false;
        var state = ElusiveOf(player);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch) return false;
        var input = state.Grant;
        if (state.Effect(now) > 0) return false; // An existing Elusive cannot be refreshed.
        var rng = random ?? AreaRng(player);
        int Skill(uint key) => skillStats?.GetValueOrDefault(key) ?? 0;
        bool nightblade = critical && Skill(Stat.SupportedByNightblade) != 0
            && GameServer.Core.World.RandomRolls.Chance(Skill(ElusiveOnCritical), 100, rng);
        bool fromCharacter = !nightblade && state.Source.Length != 0
            && ((critical && GameServer.Core.World.RandomRolls.Chance(input.Critical, 100, rng))
                || (kill && GameServer.Core.World.RandomRolls.Chance(input.Kill, 100, rng)));
        if (!nightblade && !fromCharacter) return false;
        if (nightblade) ResourceEffectsOf(player);
        var charges = ChargesOf(player);
        if (!charges.Converted(ChargeType.Power) && GameServer.Core.World.RandomRolls.Chance(
            input.LosePower, 100, rng)) charges.Spend(ChargeType.Power);
        int power = charges.Converted(ChargeType.Power) ? 0 : charges.Effective(ChargeType.Power);
        state.Started = now; state.CarriedAge = 0;
        state.NightbladeMultiplier = nightblade ? Skill(Stat.NightbladeElusiveGrantsCriticalStrikeMultiplierPlusToSupportedSkills) : null;
        state.Initial = Math.Max(0, 100d + input.Effect + (double)power * input.PowerEffect
            + (nightblade ? Skill(Stat.ElusiveEffectPlusPct) : 0));
        state.Rate = 20 / Math.Max(.01, 1 + input.LossRate / 100d);
        state.Minimum = Math.Max(0, input.Minimum);
        state.RisingSeconds = Math.Max(0, input.RisingMs) / 1000d;
        player.ElusiveUntil = now + state.Remaining(now);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        return state.Effect(now) > 0;
    }

    static int NightbladeCriticalMultiplier(Player player, IReadOnlyDictionary<uint, int> skill) =>
        skill.GetValueOrDefault(Stat.SupportedByNightblade) != 0 && Elusives.TryGetValue(player, out var state)
            && state.NightbladeMultiplier is int modifier
        ? (int)Math.Floor(modifier * ElusiveFactor(player)) : 0;

    static (int Movement, int Avoid) ElusivePanelState(Player player)
    {
        if (!Elusives.TryGetValue(player, out var state)) return default;
        double now = ResourceEffectsTime(player), effect = state.Effect(now);
        if (effect > 0 && state.Rate > 0)
        {
            // The effect rises, then falls, linearly: the next whole point of movement speed (30% at full effect), or
            // the end, whichever is first.
            int movement = (int)Math.Floor(30 * effect / 100 + 1e-9);
            double age = state.Age(now), seconds = age < state.RisingSeconds
                ? Math.Min(state.RisingSeconds - age, ((movement + 1) * 100 / 30.0 - effect) / state.Rate)
                : Math.Min((effect - movement * 100 / 30.0) / state.Rate, (effect - state.Minimum) / state.Rate);
            StatsChangeAt(now + Math.Max(seconds, 0) + 1e-6, () => ResourceEffectsTime(player));
        }
        return ((int)Math.Clamp(Math.Floor(30 * effect / 100 + 1e-9), 0, int.MaxValue),
            (int)Math.Clamp(Math.Floor(15 * effect / 100 + 1e-9), 0, int.MaxValue));
    }
    static Dictionary<uint, int> WithElusive(Player player, Dictionary<uint, int> sources)
    {
        var (movement, avoid) = ElusivePanelState(player);
        void Add(uint n, int value)
        { if (n is var key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value); }
        if (player.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_affected_by_elusive") == true)
        {
            Add(Stat.AffectedByElusive, 1); Add(Stat.BaseMovementVelocityPlusPct, movement);
        }
        return sources;
    }
    public static CarriedElusive? CaptureElusive(Player player, double at) =>
        Elusives.TryGetValue(player, out var state) && state.Effect(at) > 0
            ? new(state.Source, state.Age(at), state.Initial, state.Rate, state.Minimum, state.RisingSeconds)
            { NightbladeMultiplier = state.NightbladeMultiplier } : null;
    public static void RestoreElusive(Player player, CarriedElusive value, double at)
    {
        var state = ElusiveOf(player); state.Clear(); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch;
        state.Source = value.Source; state.Started = at; state.CarriedAge = value.Age;
        state.NightbladeMultiplier = value.NightbladeMultiplier;
        state.Initial = value.Initial; state.Rate = value.Rate; state.Minimum = value.Minimum; state.RisingSeconds = value.RisingSeconds;
    }
    public static void RetireElusive(Player player)
    {
        player.ElusiveUntil = 0;
        if (!Elusives.TryGetValue(player, out var state)) return;
        state.Clear(); foreach (ushort b in state.Retired) player.RetiredResourceBuffs.Add((player.Id, b)); state.Retired.Clear();
    }
    public static Task SendElusive(Player player) =>
        SendWorldRecords(player, player.Id, () => ElusivePresentation(player));
    public static byte[][]? ElusivePresentation(Player player)
    {
        var state = ElusiveOf(player);
        state.Retired.Clear();
        double now = ResourceEffectsTime(player);
        if (ElusiveFactor(player) <= 0) player.Area.Buffs.Clear(player.Id, ElusiveBuffRow, player.Id);
        else player.Area.Buffs.Keep(player.Id, new(ElusiveBuffRow, player.Id, U68: Buff.Seconds(state.Remaining(now)),
            U82: unchecked((ushort)(ElusiveDisplayPercent(player) - 100))));
        return null;
    }
}
