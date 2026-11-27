using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string PhysicalAegisEffect = "PhysicalAegis";
    public const uint PhysicalAegisBlindStat =
        Stat.NearbyEnemiesAreBlindedWhileYouHaveActivePhysicalAegis;
    // The Ghastly Theatre's "nearby" range is estimated at 5 m (50 cells at the project's 10 cells per metre).
    // It is an item condition, not an aura skill: aura area modifiers do not scale it.
    public const int PhysicalAegisNearbyRadius = 50;
    public const ushort PhysicalAegisBuffRow = 960;
    public const ushort PhysicalAegisDepletedBuffRow = 965;
    public const ushort PhysicalAegisPersistBuffRow = 970;

    /// <summary>
    /// Resolve the current real grant and reconcile the connection-owned state with it. This never advances recharge: a
    /// stat/panel query can install the initial active state, but only an owner tick or an accepted physical hit restores it.
    /// </summary>
    static PhysicalAegisState PhysicalAegisAt(Player player, double now, bool advance)
    {
        var state = player.PhysicalAegis;
        var owner = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, owner))
        {
            RetirePhysicalAegis(player);
            return state;
        }

        var items = ItemsOf(player);
        long invMutations = items.Mutations;
        var statSources = player.StatSourcesCache;
        if (ReferenceEquals(state.CheckedOwner, owner) && ReferenceEquals(state.CheckedStatSources, statSources)
            && state.CheckedInventoryMutations == invMutations && !state.CheckedHasGrant)
            return state;

        var equipment = items.EquipmentSnapshot();
        var grant = AllWornGrantedSkillsOf(player, equipment)
            .FirstOrDefault(g => g.EffectId == PhysicalAegisEffect && !g.Support && !g.ManuallyCast
                && !IsInactive(player, g.Host)
                && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId));
        state.CheckedOwner = owner;
        state.CheckedStatSources = statSources;
        state.CheckedInventoryMutations = invMutations;
        if (statSources?.Sources.GetValueOrDefault(Stat.YourAegisSkillsExceptPrimalAreDisabled) is int and not 0
            || grant.Host == 0 || grant.EffectId != PhysicalAegisEffect || PhysicalAegisGeometry(player, grant) is not { } geometry)
        {
            state.CheckedHasGrant = false;
            RetirePhysicalAegis(player);
            return state;
        }
        state.CheckedHasGrant = true;

        if (!ReferenceEquals(state.Owner, owner) || state.Source != grant
            || state.Maximum != geometry.Maximum || state.RechargeDelay != geometry.Seconds
            || state.Variation != geometry.Variation)
        {
            RetirePhysicalAegis(player);
            state.Install(owner, grant, geometry.Maximum, geometry.Seconds, geometry.Variation);
        }
        if (advance) state.Advance(now);
        return state;
    }

    static void RetirePhysicalAegis(Player player)
    {
        foreach (ushort buff in player.PhysicalAegis.Retire()) player.RetiredPhysicalAegisBuffs.Add(buff);
    }

    /// <summary>The condition/state view. It installs a newly equipped source but never performs recharge.</summary>
    public static PhysicalAegisState PhysicalAegisStatus(Player player, double now) =>
        PhysicalAegisAt(player, now, advance: false);

    /// <summary>The player tick's state advance. The source is revalidated before the recharge commit.</summary>
    public static bool AdvancePhysicalAegis(Player player, double now)
    {
        var state = PhysicalAegisAt(player, now, advance: false);
        return state.Exists && state.Advance(now);
    }

    static int PhysicalAegisDamage(Player player, int physical, double now)
    {
        if (physical <= 0) return Math.Max(0, physical);
        var state = PhysicalAegisAt(player, now, advance: false);
        return state.Exists ? state.Absorb(physical, now) : physical;
    }

    static (int Maximum, double Seconds, int Variation)? PhysicalAegisGeometry(
        Player player, WornGrantedSkills.Grant grant)
    {
        var stats = SkillsFor(player).EffectStats(grant.Effect, grant.Level)
            .ToDictionary(row => row.Item1, row => row.Item2);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int maximum = Get(Stat.AegisUniqueShieldMaxValue);
        int delayMs = Get(Stat.AegisRechargeDelayMs);
        if (maximum <= 0 || delayMs <= 0) return null;
        return (maximum, delayMs / 1000.0, Get(Stat.ActiveSkillDisplayAegisVariation));
    }

    public static int PhysicalAegisRadius(Player player, PhysicalAegisState state)
    {
        if (state.Source is not { } source) return 0;
        return source.EffectId == PhysicalAegisEffect ? PhysicalAegisNearbyRadius : 0;
    }

    static bool PhysicalAegisHasBlind(Player player, PhysicalAegisState state)
    {
        if (state.Source is not { } source || IsInactive(player, source.Host)) return false;
        var host = ItemsOf(player).EquipmentSnapshot().FirstOrDefault(e => e.Item.Id == source.Host);
        if (host.Item is null) return false;
        var mods = ItemsFor(player).Mods;
        return host.Item.Item.Mods.Any(applied => mods.ByHash(applied.Hash) is { } mod
            && applied.Values.Any(value => value != 0)
            && mod.Stats.Any(stat => stat.Id == PhysicalAegisBlindStat && stat.Min != 0));
    }

    /// <summary>
    /// The final Aegis Blind predicate shared by accuracy/evasion, critical and damage-taken consumers. Presentation
    /// membership is only a receipt; this revalidates the live grant, modifier and radius at the consuming commit.
    /// </summary>
    public static bool PhysicalAegisBlinds(Player player, uint monster)
    {
        if (!player.Area.Monsters.TryGetValue(monster, out var target)) return false;
        double now = ResourceEffectsTime(player);
        var state = PhysicalAegisAt(player, now, advance: false);
        if (!state.Active || player.PlayerLife == 0 || player.Paused || player.LeavingArea
            || !PhysicalAegisHasBlind(player, state)
            || target is not { Hostile: true, Dead: false, Life: > 0 }
            || target.Mods.Effects[Stat.CannotBeBlinded] != 0) return false;
        return Dist(target.Walk.Position(now), player.Walk.Position(now)) <= PhysicalAegisRadius(player, state);
    }

    public static Task SendPhysicalAegis(Player player) =>
        SendWorldRecords(player, player.Id, () => PhysicalAegisPresentationRecords(player));

    public static byte[]? PhysicalAegisPresentation(Player player) =>
        PhysicalAegisPresentationRecords(player) is { } records ? [.. records.SelectMany(p => p)] : null;

    // Physical Aegis: its persistent buff while it exists, and its stage buff (the shield's stacks, or the depleted one with
    // the countdown to its restore).
    static byte[][]? PhysicalAegisPresentationRecords(Player player)
    {
        player.RetiredPhysicalAegisBuffs.Clear();
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return null;
        double now = ResourceEffectsTime(player);
        var state = PhysicalAegisAt(player, now, advance: true);
        player.RetiredPhysicalAegisBuffs.Clear();
        var buffs = player.Area.Buffs;
        if (state.Exists && double.IsFinite(state.RestoresAt)) PresentIn(player, state.RestoresAt - now);
        ushort wanted = !state.Exists ? (ushort)0 : state.Depleted ? PhysicalAegisDepletedBuffRow : PhysicalAegisBuffRow;
        foreach (ushort row in (ushort[])[PhysicalAegisBuffRow, PhysicalAegisDepletedBuffRow])
            if (row != wanted) buffs.Clear(player.Id, row, player.Id);
        if (!state.Exists) { buffs.Clear(player.Id, PhysicalAegisPersistBuffRow, player.Id); return null; }
        buffs.Keep(player.Id, new(PhysicalAegisPersistBuffRow, player.Id));
        buffs.Keep(player.Id, state.Depleted
            ? new(wanted, player.Id, U68: Buff.Seconds(state.RestoresAt - now))
            : new(wanted, player.Id, Values: [(uint)Math.Max(0, state.Variation)], Stacks: (ushort)Math.Clamp(state.Current, 0, ushort.MaxValue)));
        return null;
    }
}
