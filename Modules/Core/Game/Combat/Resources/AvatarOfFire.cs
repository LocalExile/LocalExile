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
        Buff = 0; Hosts = []; Started = CarriedAge = 0;
    }
}

public static partial class Game
{
    public const uint AvatarRotationStat = Stat.AvatarOfFireRotationActive;
    public const uint AvatarKeystone = Stat.KeystoneAvatarOfFire;
    public const ushort AvatarRotationBuffRow = 1012;
    static readonly Store<Player, AvatarOfFireRotation> AvatarRotations = new();
    public sealed record CarriedAvatar(uint[] Hosts, double Age);

    public static AvatarOfFireRotation AvatarRotationOf(Player player) => AvatarRotations.GetOrCreateValue(player);

    static IReadOnlyDictionary<string, bool> RequalifyAvatarOfFire(Player player, IReadOnlyDictionary<string, bool> flags,
        IReadOnlyDictionary<uint, int> raw, IEnumerable<Item>? activeItems = null)
    {
        int Get(uint key) => raw.GetValueOrDefault(key);
        var equipment = ItemsOf(player).EquipmentSnapshot();
        var active = activeItems?.ToArray();
        // The transfer owner captures the frozen interval before retiring it.
        if (player.LeavingArea) return WithFlags(flags, ("_while_you_have_avatar_of_fire", false), ("_while_you_do_not_have_avatar_of_fire", false));
        bool alive = CurrentGameWork && !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0;
        uint[] hosts = alive && Get(AvatarRotationStat) > 0 ? [.. equipment
            .Where(e => active is null ? !IsInactive(player, e.Item.Id)
                : active.Any(item => ReferenceEquals(item, e.Item.Item)))
            .Where(e => PlainItemSum(e.Item.Item, ItemsFor(player).Mods).GetValueOrDefault(AvatarRotationStat) > 0)
            .Select(e => e.Item.Id).Order()] : [];
        var state = AvatarRotationOf(player);
        double now = ResourceEffectsTime(player);
        if (hosts.Length > 0) ResourceEffectsOf(player); // Bind/retire the previous owner before installing this interval.
        state.Reconcile(CombatLifetime, player.ProjectileEpoch, hosts, now);
        var phase = state.Read(now);
        if (alive && hosts.Length > 0 && phase.Until > now) StatsChangeAt(phase.Until, () => ResourceEffectsTime(player));
        bool avatar = alive && (Get(AvatarKeystone) > 0 || phase.Active);
        return WithFlags(flags, ("_while_you_have_avatar_of_fire", avatar), ("_while_you_do_not_have_avatar_of_fire", alive && !avatar));
    }

    static Dictionary<uint, int> WithAvatarOfFire(Player player, Dictionary<uint, int> sources)
    {
        bool active = player.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_you_have_avatar_of_fire") == true;
        if (!active) return sources;
        void Add(uint name, int value)
        {
            if (name is var key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }
        if (AvatarKeystone is var avatar) sources[avatar] = Math.Max(1, sources.GetValueOrDefault(avatar));
        foreach (string type in new[] { "physical", "lightning", "cold" }) Add(Stat.Of($"base_{type}_damage_%_to_convert_to_fire"), 50);
        // Use the existing final-type prohibitions: converted Fire is kept, while unconverted non-Fire is removed.
        foreach (string type in new[] { "physical", "lightning", "cold", "chaos" }) Add(Stat.Of($"base_deal_no_{type}_damage"), 1);
        return sources;
    }

    public static CarriedAvatar? CaptureAvatarOfFire(Player player, double at) =>
        AvatarRotations.TryGetValue(player, out var state) && state.Hosts.Length > 0
            ? new([.. state.Hosts], state.Elapsed(at)) : null;

    public static void RestoreAvatarOfFire(Player player, CarriedAvatar value, double at)
    {
        var state = AvatarRotationOf(player);
        state.Reconcile(CombatLifetime, player.ProjectileEpoch, [.. value.Hosts], at);
        state.Started = at; state.CarriedAge = Math.Max(0, value.Age);
    }

    public static void RetireAvatarOfFire(Player player)
    {
        if (!AvatarRotations.TryGetValue(player, out var state)) return;
        state.Clear();
        foreach (ushort buff in state.Retired) player.RetiredResourceBuffs.Add((player.Id, buff));
        state.Retired.Clear();
    }

    public static Task SendAvatarOfFire(Player player) =>
        SendWorldRecords(player, player.Id, () => AvatarOfFirePresentation(player));

    public static byte[][]? AvatarOfFirePresentation(Player player)
    {
        CharacterStatSources(player);
        var state = AvatarRotationOf(player);
        state.Retired.Clear();
        double now = ResourceEffectsTime(player);
        var phase = state.Read(now);
        // Each rotation cycle is a buff of its own (its tag), with its countdown.
        player.Area.Buffs.Replace(player.Id, b => b.Row == AvatarRotationBuffRow && b.Source == player.Id, phase.Active
            ? [new(AvatarRotationBuffRow, player.Id, U68: Buff.Seconds(phase.Until - now), Tag: unchecked((ushort)phase.Cycle))] : []);
        return null;
    }
}
