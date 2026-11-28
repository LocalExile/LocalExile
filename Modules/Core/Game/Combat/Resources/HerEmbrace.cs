using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public sealed class HerEmbraceState
{
    public object? Rules;
    public int Epoch;
    public int GainMilliseconds, Degeneration;
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
    public const uint HerEmbraceGain = Stat.GainHerEmbraceForXMsOnEnemyIgnited;
    public const uint HerEmbraceDegen = Stat.PctMaximumEsAndLifeTakenAsFireDamagePerMinutePerLevelWhileInHerEmbrace;
    public const ushort HerEmbraceBuffRow = 979;
    static readonly Store<Player, HerEmbraceState> HerEmbraces = new();
    public static HerEmbraceState HerEmbraceOf(Player player) => HerEmbraces.GetOrCreateValue(player);
    public sealed record CarriedHerEmbrace(uint[] Hosts, double Seconds, double Pending, double TakenCarry, double BypassCarry);

    static IReadOnlyDictionary<string, bool> RequalifyHerEmbrace(Player player, IReadOnlyDictionary<string, bool> flags,
        IEnumerable<Item>? activeItems = null, IReadOnlyDictionary<uint, int>? raw = null)
    {
        if (player.LeavingArea) return WithFlags(flags, ("_while_in_her_embrace", false));
        var state = HerEmbraceOf(player);
        var active = activeItems?.ToArray();
        bool alive = CurrentGameWork && !player.Closed && !player.RageRetired && player.PlayerLife > 0;
        var hosts = alive ? ItemsOf(player).EquipmentSnapshot()
            .Where(e => active is null ? !IsInactive(player, e.Item.Id) : active.Any(i => ReferenceEquals(i, e.Item.Item)))
            .Where(e => PlainItemSum(e.Item.Item, ItemsFor(player).Mods).GetValueOrDefault(HerEmbraceGain) > 0)
            .Select(e => e.Item.Id).Order().ToArray() : [];
        double now = ResourceEffectsTime(player);
        if (hosts.Length > 0) ResourceEffectsOf(player);
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch || !state.Hosts.SequenceEqual(hosts))
        {
            state.Clear(); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; state.Hosts = hosts; state.At = now;
        }
        raw ??= player.StatSourcesCache?.Sources;
        state.GainMilliseconds = raw is null ? 0 : GolemStat(raw, HerEmbraceGain);
        state.Degeneration = raw is null ? 0 : GolemStat(raw, HerEmbraceDegen);
        state.Settle(now);
        bool embraced = alive && state.Hosts.Length > 0 && now < state.Until;
        if (embraced) StatsChangeAt(state.Until, () => ResourceEffectsTime(player));
        return WithFlags(flags, ("_while_in_her_embrace", embraced));
    }

    public static void HerEmbraceOnIgniteLocked(Player player, double now)
    {
        if (player.LeavingArea || player.Paused || player.PlayerLife == 0 || !CurrentGameWork) return;
        var state = HerEmbraceOf(player);
        int duration = state.GainMilliseconds;
        if (duration <= 0 || state.Hosts.Length == 0) return;
        state.Settle(now);
        state.Until = Math.Max(state.Until, now + duration / 1000d);
        player.Area.Buffs.Set(player.Id, HerEmbraceBuff(player, state, now));
        state.PerSecond = Math.Max(0, state.Degeneration) * (double)player.Pools.Level
            * ((double)player.PlayerMaxLife + player.Pools.EnergyShield) / 6000d;
    }

    static Dictionary<uint, int> WithHerEmbrace(Player player, Dictionary<uint, int> sources)
    {
        var state = HerEmbraceOf(player);
        if (player.StatSourcesCache?.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") != true) return sources;
        void Add(uint name, int value)
        {
            if (name is var key) sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }
        Add(Stat.HasHerEmbrace, 1); Add(Stat.BaseCannotBeStunned, 1);
        Add(Stat.SwordPhysicalDamagePctToAddAsFire, 123);
        if (WeaponProfileFor(player, activeSet: true).Sword) Add(Stat.AttackPhysicalDamagePctToAddAsFire, 123);
        Add(Stat.BurningGroundEffectOnSelfPlusPct, -100);
        foreach (string ailment in new[] { "freeze", "chill", "ignite" }) Add(Stat.Of($"base_immune_to_{ailment}"), 1);
        Add(Stat.AttackSpeedPlusPct, 20); Add(Stat.BaseCastSpeedPlusPct, 20); Add(Stat.BaseMovementVelocityPlusPct, 20);
        int coefficient = GolemStat(sources, HerEmbraceDegen);
        state.Degeneration = coefficient;
        state.PerSecond = Math.Max(0, coefficient) * (double)player.Pools.Level
            * ((double)player.PlayerMaxLife + player.Pools.EnergyShield) / 6000d;
        return sources;
    }

    public static int AdvanceHerEmbraceDamageLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (!HerEmbraces.TryGetValue(player, out var state) || state.Hosts.Length == 0
            || state.Until <= 0 && state.Pending <= 0) return 0;
        RequalifyHerEmbrace(player, new Dictionary<string, bool>());
        double raw = state.Pending; state.Pending = 0;
        if (raw <= 0 || InGracePeriod(player) || InTown(player) || player.Immune || HardcoreProgressBlocked(player)) return 0;
        int Get(uint name) => GolemStat(stats, name);
        if (DamageImmunities.From(Get).Contains(DamageType.Fire)) return 0;
        double resistance = Math.Clamp(Resistance(player, DamageType.Fire), -200, MaxResistance(player, DamageType.Fire));
        int damage = IncomingResourceDefences.TakenOverTime(raw * (1 - resistance / 100d), Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get),
            typedIncreasedPct: ActorScale.Saturate((long)Get(Stat.FireDamageTakenPlusPct) + Get(Stat.ElementalDamageTakenPlusPct)));
        int life = DamagePlayerNonChaosOverTime(player, DamageType.Fire, damage, now, Get, ref state.BypassCarry);
        player.PlayerLife = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - life), now);
        if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); RecordHardcoreDeath(player); }
        return life;
    }

    public static CarriedHerEmbrace? CaptureHerEmbrace(Player player, double at)
    {
        var state = HerEmbraceOf(player); state.Settle(at);
        return state.Hosts.Length > 0 && (state.Until > at || state.Pending > 0)
            ? new([.. state.Hosts], Math.Max(0, state.Until - at), state.Pending, state.TakenCarry, state.BypassCarry) : null;
    }
    public static void RestoreHerEmbrace(Player player, CarriedHerEmbrace value, double at)
    {
        var state = HerEmbraceOf(player); state.Clear();
        state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; state.Hosts = [.. value.Hosts];
        state.At = at; state.Until = at + value.Seconds; state.Pending = value.Pending;
        state.TakenCarry = value.TakenCarry; state.BypassCarry = value.BypassCarry;
    }
    public static void RetireHerEmbrace(Player player)
    {
        if (!HerEmbraces.TryGetValue(player, out var state)) return;
        state.Clear();
        foreach (ushort buff in state.Retired) player.RetiredResourceBuffs.Add((player.Id, buff));
        state.Retired.Clear();
    }
    public static Task SendHerEmbrace(Player player) =>
        SendWorldRecords(player, player.Id, () => HerEmbracePresentation(player));
    public static byte[][]? HerEmbracePresentation(Player player)
    {
        CharacterStatSources(player);
        var state = HerEmbraceOf(player);
        state.Retired.Clear();
        double now = ResourceEffectsTime(player);
        bool active = now < state.Until && state.Hosts.Length > 0 && !player.LeavingArea;
        if (!active) player.Area.Buffs.Clear(player.Id, HerEmbraceBuffRow, player.Id);
        else player.Area.Buffs.Keep(player.Id, HerEmbraceBuff(player, state, now));
        return null;
    }

    static Buff HerEmbraceBuff(Player player, HerEmbraceState state, double now) =>
        new(HerEmbraceBuffRow, player.Id, U68: Buff.Seconds(state.Until - now));
}
