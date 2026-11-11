using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public sealed class HerEmbraceState
{
    public object? Rules;
    public int Epoch;
    public uint[] Hosts = [];
    public double Until, At, PerSecond, Pending, TakenCarry, BypassCarry, ShownUntil;
    public ushort Buff;
    public readonly HashSet<ushort> Retired = [];
    public void Settle(double now)
    {
        double end = Math.Min(now, Until);
        if (end > At) Pending += (end - At) * PerSecond;
        At = Math.Max(At, now);
    }
    public void Clear()
    {
        if (Buff != 0) Retired.Add(Buff);
        Buff = 0; Hosts = []; Until = At = PerSecond = Pending = TakenCarry = BypassCarry = ShownUntil = 0;
    }
}

public static partial class Game
{
    public const string HerEmbraceGain = "gain_her_embrace_for_x_ms_on_enemy_ignited";
    public const string HerEmbraceDegen = "%_maximum_es_and_life_taken_as_fire_damage_per_minute_per_level_while_in_her_embrace";
    public const ushort HerEmbraceBuffRow = 979;
    static readonly ConditionalWeakTable<ClientState, HerEmbraceState> HerEmbraces = new();
    public static HerEmbraceState HerEmbraceOf(ClientState conn) => HerEmbraces.GetOrCreateValue(conn);
    public sealed record CarriedHerEmbrace(uint[] Hosts, double Seconds, double Pending, double TakenCarry, double BypassCarry);

    static Dictionary<string, bool> RequalifyHerEmbrace(ClientState conn, IReadOnlyDictionary<string, bool> flags,
        IEnumerable<Item>? activeItems = null)
    {
        if (conn.LeavingArea) return new(flags) { ["_while_in_her_embrace"] = false };
        var state = HerEmbraceOf(conn);
        var active = activeItems?.ToArray();
        bool alive = CurrentGameWork && !conn.Closed && !conn.RageRetired && conn.PlayerLife > 0;
        var hosts = alive ? ItemsOf(conn).EquipmentSnapshot()
            .Where(e => active is null ? !IsInactive(conn, e.Item.Id) : active.Any(i => ReferenceEquals(i, e.Item.Item)))
            .Where(e => ItemModStats.Sum([e.Item.Item], ItemsFor(conn).Mods).GetValueOrDefault(HerEmbraceGain) > 0)
            .Select(e => e.Item.Id).Order().ToArray() : [];
        double now = ResourceEffectsTime(conn);
        if (hosts.Length > 0) ResourceEffectsOf(conn);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch || !state.Hosts.SequenceEqual(hosts))
        {
            state.Clear(); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.Hosts = hosts; state.At = now;
        }
        state.Settle(now);
        return new(flags) { ["_while_in_her_embrace"] = alive && state.Hosts.Length > 0 && now < state.Until };
    }

    public static void HerEmbraceOnIgniteLocked(ClientState conn, double now)
    {
        if (conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 || !CurrentGameWork) return;
        var stats = CharacterStatSources(conn).Stats;
        int Get(string name) => GolemStat(stats, name);
        var state = HerEmbraceOf(conn);
        int duration = Get(HerEmbraceGain);
        if (duration <= 0 || state.Hosts.Length == 0) return;
        state.Settle(now);
        state.Until = Math.Max(state.Until, now + duration / 1000d);
        state.PerSecond = Math.Max(0, Get(HerEmbraceDegen)) * (double)conn.Pools.Level
            * ((double)conn.PlayerMaxLife + conn.Pools.EnergyShield) / 6000d;
    }

    static Dictionary<uint, int> WithHerEmbrace(ClientState conn, Dictionary<uint, int> sources)
    {
        var state = HerEmbraceOf(conn);
        if (conn.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") != true) return sources;
        void Add(string name, int value)
        {
            if (Stats.Key(name) is uint key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }
        Add("has_her_embrace", 1); Add("base_cannot_be_stunned", 1);
        Add("sword_physical_damage_%_to_add_as_fire", 123);
        if (WeaponProfileFor(conn, activeSet: true).Sword) Add("attack_physical_damage_%_to_add_as_fire", 123);
        Add("burning_ground_effect_on_self_+%", -100);
        foreach (string ailment in new[] { "freeze", "chill", "ignite" }) Add($"base_immune_to_{ailment}", 1);
        Add("attack_speed_+%", 20); Add("base_cast_speed_+%", 20); Add("base_movement_velocity_+%", 20);
        int coefficient = GolemStat(sources, HerEmbraceDegen);
        state.PerSecond = Math.Max(0, coefficient) * (double)conn.Pools.Level
            * ((double)conn.PlayerMaxLife + conn.Pools.EnergyShield) / 6000d;
        return sources;
    }

    public static int AdvanceHerEmbraceDamageLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        RequalifyHerEmbrace(conn, new Dictionary<string, bool>());
        var state = HerEmbraceOf(conn);
        double raw = state.Pending; state.Pending = 0;
        if (raw <= 0 || InGracePeriod(conn) || InTown(conn) || conn.Immune || HardcoreProgressBlocked(conn)) return 0;
        int Get(string name) => GolemStat(stats, name);
        if (DamageImmunities.From(Get).Contains(DamageType.Fire)) return 0;
        double resistance = Math.Clamp(Resistance(conn, DamageType.Fire), -200, MaxResistance(conn, DamageType.Fire));
        int damage = IncomingResourceDefences.TakenOverTime(raw * (1 - resistance / 100d), Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get),
            typedIncreasedPct: ActorScale.Saturate((long)Get("fire_damage_taken_+%") + Get("elemental_damage_taken_+%")));
        int life = IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), now, ref state.BypassCarry);
        conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - life);
        if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); RecordHardcoreDeath(conn); }
        return life;
    }

    public static CarriedHerEmbrace? CaptureHerEmbrace(ClientState conn, double at)
    {
        var state = HerEmbraceOf(conn); state.Settle(at);
        return state.Hosts.Length > 0 && (state.Until > at || state.Pending > 0)
            ? new([.. state.Hosts], Math.Max(0, state.Until - at), state.Pending, state.TakenCarry, state.BypassCarry) : null;
    }
    public static void RestoreHerEmbrace(ClientState conn, CarriedHerEmbrace value, double at)
    {
        var state = HerEmbraceOf(conn); state.Clear();
        state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.Hosts = [.. value.Hosts];
        state.At = at; state.Until = at + value.Seconds; state.Pending = value.Pending;
        state.TakenCarry = value.TakenCarry; state.BypassCarry = value.BypassCarry;
    }
    public static void RetireHerEmbrace(ClientState conn)
    {
        if (!HerEmbraces.TryGetValue(conn, out var state)) return;
        state.Clear();
        foreach (ushort buff in state.Retired) conn.RetiredResourceBuffs.Add((PlayerId, buff));
        state.Retired.Clear();
    }
    public static Task SendHerEmbrace(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => HerEmbracePresentation(conn));
    public static byte[][]? HerEmbracePresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            CharacterStatSources(conn);
            var state = HerEmbraceOf(conn);
            var packets = state.Retired.Select(b => InstanceEntities.RemoveBuff(PlayerId, b)).ToList(); state.Retired.Clear();
            double now = ResourceEffectsTime(conn);
            bool active = now < state.Until && state.Hosts.Length > 0 && !conn.LeavingArea;
            if (state.Buff != 0 && (!active || state.ShownUntil != state.Until))
            { packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.Buff)); state.Buff = 0; }
            if (active && state.Buff == 0)
            {
                state.ShownUntil = state.Until;
                packets.Add(InstanceEntities.AddBuff(PlayerId, state.Buff = conn.NextBuff++, HerEmbraceBuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Until - now))));
            }
            return packets.Count == 0 ? null : [.. packets];
        }
    }
}
