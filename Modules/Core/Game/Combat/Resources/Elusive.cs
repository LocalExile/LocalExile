using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public sealed class ElusiveState
{
    public object? Rules;
    public int Epoch;
    public string Source = "";
    public double Started, CarriedAge, Initial, Rate = 20, Minimum, RisingSeconds;
    public ushort Buff;
    public int PresentedPercent = -1;
    public ElusiveGrant Grant;
    public readonly HashSet<ushort> Retired = [];
    public double Age(double now) => CarriedAge + Math.Max(0, now - Started);
    public double Effect(double now)
    {
        if (Source.Length == 0 || Initial <= 0) return 0;
        double age = Age(now), rise = Math.Min(age, RisingSeconds);
        double effect = Initial + Rate * rise - Rate * Math.Max(0, age - RisingSeconds);
        return effect > Minimum ? effect : 0;
    }
    public double Remaining(double now) => Math.Max(0, RisingSeconds * 2 + (Initial - Minimum) / Rate - Age(now));
    public void Clear()
    {
        if (Buff != 0) Retired.Add(Buff);
        Buff = 0; PresentedPercent = -1; Source = ""; Started = CarriedAge = Initial = Minimum = RisingSeconds = 0; Rate = 20;
    }
}

public readonly record struct ElusiveGrant(int Critical, int Kill, int Effect, int PowerEffect, int LosePower, int LossRate, int Minimum, int RisingMs);

public static partial class Game
{
    public const string ElusiveOnCritical = "gain_elusive_on_crit_%_chance", ElusiveOnKill = "gain_elusive_on_kill_chance_%";
    public const ushort ElusiveBuffRow = 1374;
    static readonly RuleTable<ClientState, ElusiveState> Elusives = new();
    public static ElusiveState ElusiveOf(ClientState conn) => Elusives.GetOrCreateValue(conn);
    public sealed record CarriedElusive(string Source, double Age, double Initial, double Rate, double Minimum, double RisingSeconds);

    static Dictionary<string, bool> RequalifyElusive(ClientState conn, IReadOnlyDictionary<string, bool> flags,
        IReadOnlyDictionary<uint, int> raw, IEnumerable<Item>? activeItems = null)
    {
        var copy = new Dictionary<string, bool>(flags);
        ApplyElusive(conn, copy, raw, activeItems, equipment: null);
        return copy;
    }

    internal static void ApplyElusive(ClientState conn, Dictionary<string, bool> flags,
        IReadOnlyDictionary<uint, int> raw, IEnumerable<Item>? activeItems,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment)
    {
        if (conn.LeavingArea)
        {
            flags["_while_affected_by_elusive"] = false;
            flags["_while_elusive"] = false;
            flags["_while_you_have_elusive"] = false;
            return;
        }
        var state = ElusiveOf(conn);
        bool alive = CurrentGameWork && !conn.Closed && !conn.RageRetired && conn.PlayerLife > 0;
        int Get(string n) => GolemStat(raw, n);
        string identity = "";
        if (alive && (Get(ElusiveOnCritical) > 0 || Get(ElusiveOnKill) > 0))
        {
            var active = activeItems is Item[] arr ? arr : activeItems?.ToArray();
            var sources = (equipment ?? ItemsOf(conn).EquipmentSnapshot())
                .Where(e => active is null ? !IsInactive(conn, e.Item.Id) : active.Any(i => ReferenceEquals(i, e.Item.Item)))
                .Where(e => PlainItemSum(e.Item.Item, ItemsFor(conn).Mods) is var item
                    && (item.GetValueOrDefault(ElusiveOnCritical) > 0 || item.GetValueOrDefault(ElusiveOnKill) > 0))
                .Select(e => e.Item.Id).Order().ToArray();
            identity = string.Join(',', sources) + ":" + Get(ElusiveOnCritical) + ":" + Get(ElusiveOnKill)
                + ":" + string.Join(',', conn.Passives.Where(n => PassivesFor(conn).Find(n)?.Stats.Any(s =>
                    Stats.Name(s.Key) is ElusiveOnCritical or ElusiveOnKill) == true).Order());
            ResourceEffectsOf(conn);
        }
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch || state.Source != identity)
        {
            state.Clear(); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.Source = identity;
        }
        int Input(string n) => Stats.Key(n) is uint key ? conn.PlayerStats.GetValueOrDefault(key, Get(n)) : 0;
        state.Grant = new(Input(ElusiveOnCritical), Input(ElusiveOnKill),
            Input("elusive_effect_+%") + Input("elusive_effect_on_self_+%") + Input("buff_effect_on_self_+%"),
            Input("elusive_effect_on_self_+%_per_power_charge"), Input("lose_a_power_charge_when_you_gain_elusive_%_chance"),
            Input("elusive_loss_rate_+%"), Input("elusive_minimum_effect_%"), Input("elusive_ticks_up_instead_for_x_ms"));
        double now = ResourceEffectsTime(conn);
        bool running = alive && state.Effect(now) > 0;
        conn.ElusiveUntil = running ? now + state.Remaining(now) : 0;
        flags["_while_affected_by_elusive"] = running;
        flags["_while_elusive"] = running;
        flags["_while_you_have_elusive"] = running;
    }

    public static bool TryGainElusiveLocked(ClientState conn, bool critical, bool kill, double now, Random? random = null)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
        var state = ElusiveOf(conn);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch) return false;
        var input = state.Grant;
        if (state.Source.Length == 0 || state.Effect(now) > 0) return false; // An existing Elusive cannot be refreshed.
        var rng = random ?? Random.Shared;
        bool gained = critical && GameServer.Core.World.RandomRolls.Chance(input.Critical, 100, rng);
        if (!gained && kill) gained = GameServer.Core.World.RandomRolls.Chance(input.Kill, 100, rng);
        if (!gained) return false;
        var charges = ChargesOf(conn);
        if (!charges.Converted(ChargeType.Power) && GameServer.Core.World.RandomRolls.Chance(
            input.LosePower, 100, rng)) charges.Spend(ChargeType.Power);
        int power = charges.Converted(ChargeType.Power) ? 0 : charges.Effective(ChargeType.Power);
        state.Started = now; state.CarriedAge = 0;
        state.Initial = Math.Max(0, 100d + input.Effect + (double)power * input.PowerEffect);
        state.Rate = 20 / Math.Max(.01, 1 + input.LossRate / 100d);
        state.Minimum = Math.Max(0, input.Minimum);
        state.RisingSeconds = Math.Max(0, input.RisingMs) / 1000d;
        conn.ElusiveUntil = now + state.Remaining(now);
        return state.Effect(now) > 0;
    }

    static (int Movement, int Avoid) ElusivePanelState(ClientState conn)
    {
        if (!Elusives.TryGetValue(conn, out var state)) return default;
        double effect = state.Effect(ResourceEffectsTime(conn));
        return ((int)Math.Clamp(Math.Floor(30 * effect / 100 + 1e-9), 0, int.MaxValue),
            (int)Math.Clamp(Math.Floor(15 * effect / 100 + 1e-9), 0, int.MaxValue));
    }
    static Dictionary<uint, int> WithElusive(ClientState conn, Dictionary<uint, int> sources)
    {
        var (movement, avoid) = ElusivePanelState(conn);
        void Add(string n, int value)
        { if (Stats.Key(n) is uint key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value); }
        if (conn.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_affected_by_elusive") == true)
        {
            Add("affected_by_elusive", 1); Add("base_movement_velocity_+%", movement);
        }
        return sources;
    }
    public static CarriedElusive? CaptureElusive(ClientState conn, double at) =>
        Elusives.TryGetValue(conn, out var state) && state.Effect(at) > 0
            ? new(state.Source, state.Age(at), state.Initial, state.Rate, state.Minimum, state.RisingSeconds) : null;
    public static void RestoreElusive(ClientState conn, CarriedElusive value, double at)
    {
        var state = ElusiveOf(conn); state.Clear(); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch;
        state.Source = value.Source; state.Started = at; state.CarriedAge = value.Age;
        state.Initial = value.Initial; state.Rate = value.Rate; state.Minimum = value.Minimum; state.RisingSeconds = value.RisingSeconds;
    }
    public static void RetireElusive(ClientState conn)
    {
        conn.ElusiveUntil = 0;
        if (!Elusives.TryGetValue(conn, out var state)) return;
        state.Clear(); foreach (ushort b in state.Retired) conn.RetiredResourceBuffs.Add((PlayerId, b)); state.Retired.Clear();
    }
    public static Task SendElusive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => ElusivePresentation(conn));
    public static byte[][]? ElusivePresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var state = ElusiveOf(conn);
            var packets = state.Retired.Select(b => InstanceEntities.RemoveBuff(PlayerId, b)).ToList(); state.Retired.Clear();
            double now = ResourceEffectsTime(conn); bool active = ElusiveFactor(conn) > 0;
            if (!active && state.Buff != 0) { packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.Buff)); state.Buff = 0; }
            int percent = ElusiveDisplayPercent(conn);
            if (active && (state.Buff == 0 || state.PresentedPercent != percent))
            {
                if (state.Buff == 0) state.Buff = conn.NextBuff++;
                state.PresentedPercent = percent;
                packets.Add(InstanceEntities.AddBuff(PlayerId, state.Buff, ElusiveBuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)state.Remaining(now)),
                    u82: unchecked((ushort)(percent - 100))));
            }
            return packets.Count == 0 ? null : [.. packets];
        }
    }
}
