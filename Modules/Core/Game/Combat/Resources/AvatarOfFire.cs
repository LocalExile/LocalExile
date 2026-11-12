using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>The source-owned eight-second interval, with a four-second grant after each interval.</summary>
public sealed class AvatarOfFireRotation
{
    public object? Rules;
    public int Epoch;
    public uint[] Hosts = [];
    public double Started, CarriedAge;
    public ushort Buff;
    public long PresentedCycle = -1;
    public readonly HashSet<ushort> Retired = [];

    public void Reconcile(object? rules, int epoch, uint[] hosts, double now)
    {
        if (ReferenceEquals(Rules, rules) && Epoch == epoch && Hosts.SequenceEqual(hosts)) return;
        Clear(); Rules = rules; Epoch = epoch; Hosts = hosts; Started = now;
    }

    public (bool Active, double Until, long Cycle) Read(double now)
    {
        double age = Elapsed(now);
        if (Hosts.Length == 0 || age < 8) return (false, 0, -1);
        long cycle = (long)Math.Floor(age / 8);
        double within = age - cycle * 8;
        return (within < 4, now + Math.Max(0, 4 - within), cycle);
    }

    public double Elapsed(double now) => CarriedAge + Math.Max(0, now - Started);

    public void Clear()
    {
        if (Buff != 0) Retired.Add(Buff);
        Buff = 0; PresentedCycle = -1; Hosts = []; Started = CarriedAge = 0;
    }
}

public static partial class Game
{
    public const string AvatarRotationStat = "avatar_of_fire_rotation_active";
    public const string AvatarKeystone = "keystone_avatar_of_fire";
    public const ushort AvatarRotationBuffRow = 1012;
    static readonly RuleTable<ClientState, AvatarOfFireRotation> AvatarRotations = new();
    public sealed record CarriedAvatar(uint[] Hosts, double Age);

    public static AvatarOfFireRotation AvatarRotationOf(ClientState conn) => AvatarRotations.GetOrCreateValue(conn);

    static Dictionary<string, bool> RequalifyAvatarOfFire(ClientState conn, IReadOnlyDictionary<string, bool> flags,
        IReadOnlyDictionary<uint, int> raw, IReadOnlyDictionary<uint, int> overrides, IEnumerable<Item>? activeItems = null)
    {
        int Get(string name) => Stats.Key(name) is uint key
            ? overrides.TryGetValue(key, out int value) ? value : raw.GetValueOrDefault(key) : 0;
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        var active = activeItems?.ToArray();
        if (conn.LeavingArea) return new(flags)
        {
            ["_while_you_have_avatar_of_fire"] = false,
            ["_while_you_do_not_have_avatar_of_fire"] = false,
        }; // The transfer owner captures the frozen interval before retiring it.
        bool alive = CurrentGameWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0;
        uint[] hosts = alive && Get(AvatarRotationStat) > 0 ? [.. equipment
            .Where(e => active is null ? !IsInactive(conn, e.Item.Id)
                : active.Any(item => ReferenceEquals(item, e.Item.Item)))
            .Where(e => ItemModStats.Sum([e.Item.Item], ItemsFor(conn).Mods).GetValueOrDefault(AvatarRotationStat) > 0)
            .Select(e => e.Item.Id).Order()] : [];
        var state = AvatarRotationOf(conn);
        double now = ResourceEffectsTime(conn);
        if (hosts.Length > 0) ResourceEffectsOf(conn); // Bind/retire the previous owner before installing this interval.
        state.Reconcile(CombatLifetime, conn.ProjectileEpoch, hosts, now);
        bool avatar = alive && (Get(AvatarKeystone) > 0 || state.Read(now).Active);
        if (Stats.Key(AvatarKeystone) is uint key && overrides.TryGetValue(key, out int replacement)) avatar = alive && replacement > 0;
        return new(flags)
        {
            ["_while_you_have_avatar_of_fire"] = avatar,
            ["_while_you_do_not_have_avatar_of_fire"] = alive && !avatar,
        };
    }

    static Dictionary<uint, int> WithAvatarOfFire(ClientState conn, Dictionary<uint, int> sources)
    {
        bool active = conn.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_you_have_avatar_of_fire") == true;
        if (!active) return sources;
        void Add(string name, int value)
        {
            if (Stats.Key(name) is uint key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }
        if (Stats.Key(AvatarKeystone) is uint avatar) sources[avatar] = Math.Max(1, sources.GetValueOrDefault(avatar));
        foreach (string type in new[] { "physical", "lightning", "cold" }) Add($"base_{type}_damage_%_to_convert_to_fire", 50);
        // Use the existing final-type prohibitions: converted Fire is kept, while unconverted non-Fire is removed.
        foreach (string type in new[] { "physical", "lightning", "cold", "chaos" }) Add($"base_deal_no_{type}_damage", 1);
        return sources;
    }

    public static CarriedAvatar? CaptureAvatarOfFire(ClientState conn, double at) =>
        AvatarRotations.TryGetValue(conn, out var state) && state.Hosts.Length > 0
            ? new([.. state.Hosts], state.Elapsed(at)) : null;

    public static void RestoreAvatarOfFire(ClientState conn, CarriedAvatar value, double at)
    {
        var state = AvatarRotationOf(conn);
        state.Reconcile(CombatLifetime, conn.ProjectileEpoch, [.. value.Hosts], at);
        state.Started = at; state.CarriedAge = Math.Max(0, value.Age);
    }

    public static void RetireAvatarOfFire(ClientState conn)
    {
        if (!AvatarRotations.TryGetValue(conn, out var state)) return;
        state.Clear();
        foreach (ushort buff in state.Retired) conn.RetiredResourceBuffs.Add((PlayerId, buff));
        state.Retired.Clear();
    }

    public static Task SendAvatarOfFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => AvatarOfFirePresentation(conn));

    public static byte[][]? AvatarOfFirePresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            CharacterStatSources(conn);
            var state = AvatarRotationOf(conn);
            var result = state.Retired.Select(buff => InstanceEntities.RemoveBuff(PlayerId, buff)).ToList();
            state.Retired.Clear();
            double now = ResourceEffectsTime(conn);
            var phase = state.Read(now);
            if (state.Buff != 0 && (!phase.Active || state.PresentedCycle != phase.Cycle))
            {
                result.Add(InstanceEntities.RemoveBuff(PlayerId, state.Buff)); state.Buff = 0;
            }
            if (phase.Active && state.Buff == 0)
            {
                state.PresentedCycle = phase.Cycle;
                result.Add(InstanceEntities.AddBuff(PlayerId, state.Buff = conn.NextBuff++, AvatarRotationBuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, phase.Until - now))));
            }
            return result.Count == 0 ? null : [.. result];
        }
    }
}
