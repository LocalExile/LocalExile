using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal const string PhysicalAegisEffect = "PhysicalAegis";
    internal const string PhysicalAegisBlindStat =
        "nearby_enemies_are_blinded_while_you_have_active_physical_aegis";
    // The Ghastly Theatre's "nearby" range is estimated at 5 m (50 cells at the project's 10 cells per metre).
    // It is an item condition, not an aura skill: aura area modifiers do not scale it.
    internal const int PhysicalAegisNearbyRadius = 50;
    internal const ushort PhysicalAegisBuffRow = 960;
    internal const ushort PhysicalAegisDepletedBuffRow = 965;
    internal const ushort PhysicalAegisPersistBuffRow = 970;

    /// <summary>
    /// Resolve the current real grant and reconcile the connection-owned state with it. This never advances recharge: a
    /// stat/panel query can install the initial active state, but only an owner tick or an accepted physical hit restores it.
    /// </summary>
    static PhysicalAegisState PhysicalAegisAt(ClientState conn, double now, bool advance)
    {
        lock (conn.Monsters)
        {
            var state = conn.PhysicalAegis;
            var owner = ResourceEffectsOf(conn);
            if (!ResourceEffectsCurrent(conn, owner))
            {
                RetirePhysicalAegis(conn);
                return state;
            }

            var equipment = ItemsOf(conn).EquipmentSnapshot();
            var grant = AllWornGrantedSkillsOf(conn, equipment)
                .FirstOrDefault(g => g.EffectId == PhysicalAegisEffect && !g.Support && !g.ManuallyCast
                    && !IsInactive(conn, g.Host)
                    && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId));
            if (grant.Effect < 0 || PhysicalAegisGeometry(conn, grant) is not { } geometry)
            {
                RetirePhysicalAegis(conn);
                return state;
            }

            if (!ReferenceEquals(state.Owner, owner) || state.Source != grant
                || state.Maximum != geometry.Maximum || state.RechargeDelay != geometry.Seconds
                || state.Variation != geometry.Variation)
            {
                RetirePhysicalAegis(conn);
                state.Install(owner, grant, geometry.Maximum, geometry.Seconds, geometry.Variation);
            }
            if (advance) state.Advance(now);
            return state;
        }
    }

    static void RetirePhysicalAegis(ClientState conn)
    {
        foreach (ushort buff in conn.PhysicalAegis.Retire()) conn.RetiredPhysicalAegisBuffs.Add(buff);
    }

    /// <summary>The condition/state view. It installs a newly equipped source but never performs recharge.</summary>
    internal static PhysicalAegisState PhysicalAegisStatus(ClientState conn, double now) =>
        PhysicalAegisAt(conn, now, advance: false);

    /// <summary>The player tick's state advance. The source is revalidated before the recharge commit.</summary>
    internal static bool AdvancePhysicalAegis(ClientState conn, double now)
    {
        lock (conn.Monsters)
        {
            var state = PhysicalAegisAt(conn, now, advance: false);
            return state.Exists && state.Advance(now);
        }
    }

    static int PhysicalAegisDamage(ClientState conn, int physical, double now)
    {
        if (physical <= 0) return Math.Max(0, physical);
        lock (conn.Monsters)
        {
            var state = PhysicalAegisAt(conn, now, advance: false);
            return state.Exists ? state.Absorb(physical, now) : physical;
        }
    }

    static (int Maximum, double Seconds, int Variation)? PhysicalAegisGeometry(
        ClientState conn, WornGrantedSkills.Grant grant)
    {
        var stats = SkillsFor(conn).EffectStats(grant.Effect, grant.Level)
            .ToDictionary(row => row.Item1, row => row.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int maximum = Get("aegis_unique_shield_max_value");
        int delayMs = Get("aegis_recharge_delay_ms");
        if (maximum <= 0 || delayMs <= 0) return null;
        return (maximum, delayMs / 1000.0, Get("active_skill_display_aegis_variation"));
    }

    internal static int PhysicalAegisRadius(ClientState conn, PhysicalAegisState state)
    {
        if (state.Source is not { } source) return 0;
        return source.EffectId == PhysicalAegisEffect ? PhysicalAegisNearbyRadius : 0;
    }

    static bool PhysicalAegisHasBlind(ClientState conn, PhysicalAegisState state)
    {
        if (state.Source is not { } source || IsInactive(conn, source.Host)) return false;
        var host = ItemsOf(conn).EquipmentSnapshot().FirstOrDefault(e => e.Item.Id == source.Host);
        if (host.Item is null) return false;
        var mods = ItemsFor(conn).Mods;
        return host.Item.Item.Mods.Any(applied => mods.ByHash(applied.Hash) is { } mod
            && applied.Values.Any(value => value != 0)
            && mod.Stats.Any(stat => stat.Id == PhysicalAegisBlindStat && stat.Min != 0));
    }

    /// <summary>
    /// The final Aegis Blind predicate shared by accuracy/evasion, critical and damage-taken consumers. Presentation
    /// membership is only a receipt; this revalidates the live grant, modifier and radius at the consuming commit.
    /// </summary>
    internal static bool PhysicalAegisBlinds(ClientState conn, uint monster)
    {
        if (!conn.Monsters.TryGetValue(monster, out var target)) return false;
        double now = ResourceEffectsTime(conn);
        lock (conn.Monsters)
        {
            var state = PhysicalAegisAt(conn, now, advance: false);
            if (!state.Active || conn.PlayerLife == 0 || conn.Paused || conn.LeavingArea
                || !PhysicalAegisHasBlind(conn, state)
                || target is not { Hostile: true, Dead: false, Life: > 0 }
                || target.Mods.Effects["cannot_be_blinded"] != 0) return false;
            return Dist(target.Walk.Position(now), conn.Walk.Position(now)) <= PhysicalAegisRadius(conn, state);
        }
    }

    internal static Task SendPhysicalAegis(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () => PhysicalAegisPresentation(conn));

    internal static byte[]? PhysicalAegisPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var bytes = new List<byte>();
            foreach (ushort buff in conn.RetiredPhysicalAegisBuffs)
                bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, buff));
            conn.RetiredPhysicalAegisBuffs.Clear();

            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired)
                return bytes.Count == 0 ? null : [.. bytes];

            double now = ResourceEffectsTime(conn);
            var state = PhysicalAegisAt(conn, now, advance: true);
            // Reading an invalid source retires the old state and puts its instances in the cleanup queue above.
            foreach (ushort buff in conn.RetiredPhysicalAegisBuffs)
                bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, buff));
            conn.RetiredPhysicalAegisBuffs.Clear();
            if (!state.Exists) return bytes.Count == 0 ? null : [.. bytes];

            if (state.PersistBuff == 0)
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, state.PersistBuff = conn.NextBuff++,
                    PhysicalAegisPersistBuffRow, PlayerId));

            ushort wantedRow = state.Depleted ? PhysicalAegisDepletedBuffRow : PhysicalAegisBuffRow;
            bool changed = state.StageBuff == 0 || state.StageRow != wantedRow || state.PresentedCurrent != state.Current;
            if (!changed) return bytes.Count == 0 ? null : [.. bytes];

            if (state.StageBuff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, state.StageBuff));
            state.StageBuff = conn.NextBuff++;
            state.StageRow = wantedRow;
            state.PresentedCurrent = state.Current;
            bytes.AddRange(state.Depleted
                ? InstanceEntities.AddBuff(PlayerId, state.StageBuff, wantedRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.RestoresAt - now)))
                : InstanceEntities.AddBuff(PlayerId, state.StageBuff, wantedRow, PlayerId,
                    values: [(uint)Math.Max(0, state.Variation)],
                    stacks: (ushort)Math.Clamp(state.Current, 0, ushort.MaxValue)));
            return [.. bytes];
        }
    }
}
