using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static IReadOnlyDictionary<string, bool> ItemConditionFlags(ClientState conn, double now)
    {
        var ailments = conn.ElementalAilments.Snapshot(now);
        var leech = LeechOf(conn).Active(now);
        bool moving = conn.Walk.Moving(now), alive = conn.PlayerLife > 0;
        var lowLifeSources = conn.StatSourcesCache is { } cached ? new Dictionary<uint, int>(cached.Sources) : [];
        foreach (var (key, value) in conn.PlayerStats) lowLifeSources[key] = value;
        var flags = new Dictionary<string, bool>
        {
            ["_while_on_full_life"] = alive && conn.PlayerLife >= conn.PlayerMaxLife,
            ["_while_on_low_life"] = alive && CriticalOnLowLife(conn, lowLifeSources),
            ["_while_on_full_mana"] = alive && conn.PlayerMana >= conn.Pools.Mana,
            ["_while_on_low_mana"] = alive && (long)conn.PlayerMana * 2 <= conn.Pools.Mana,
            ["_while_on_full_energy_shield"] = alive && conn.EnergyShield.Full,
            ["_while_no_energy_shield"] = conn.EnergyShield.Current == 0,
            ["_while_moving"] = moving, ["_while_stationary"] = !moving,
            ["_while_phasing"] = conn.Phasing,
            ["_while_ignited"] = ailments.Ignite, ["_while_chilled"] = ailments.Chill > 0,
            ["_while_frozen"] = ailments.Freeze, ["_while_shocked"] = ailments.Shock > 0,
            ["_while_fortified"] = FortificationOf(conn).Read(now).Current > 0,
            ["_while_life_leeching"] = alive && leech.Life,
            ["_while_mana_leeching"] = alive && leech.Mana,
            ["_while_energy_shield_leeching"] = alive && leech.EnergyShield,
            ["_while_leeching"] = alive && (leech.Life || leech.Mana || leech.EnergyShield),
        };
        flags["_while_you_have_fortify"] = flags["_while_fortified"];
        // The uniques spell the life/mana states with "when"; the same state answers both keys.
        foreach (string state in new[] { "_on_full_life", "_on_low_life", "_on_full_mana", "_on_low_mana" })
            flags["_when" + state] = flags["_while" + state];
        var weapons = WeaponProfileFor(conn);
        flags["_while_unarmed"] = weapons.Unarmed;
        flags["_while_wielding_staff"] = weapons.Staff;
        flags["_while_wielding_sword"] = weapons.Sword;
        var charges = ChargesOf(conn);
        foreach (var type in Charges.Types)
        {
            int maximum = charges.Limits(type).Maximum;
            flags["_while_at_maximum_" + type.ToString().ToLowerInvariant() + "_charges"] =
                !charges.Converted(type) && maximum > 0 && charges.Effective(type) >= maximum;
        }
        return flags;
    }

    // A named event is answerable only when a production commit records it. Other mechanics retain their dedicated
    // stat id until their owning subsystem supplies the event; an absent unsupported event is not evidence of false.
    internal static readonly IReadOnlySet<string> TrackedItemRecentEvents = new HashSet<string>
    {
        StatConditions.HitEnemy, StatConditions.KilledEnemy, StatConditions.Crit, StatConditions.BeenHit,
        StatConditions.Blocked, StatConditions.BlockedAttack, StatConditions.BlockedSpell,
        StatConditions.SuppressedSpell, StatConditions.TakenSavageHit, StatConditions.BeenStunned,
        StatConditions.BeenFrozen,
        StatConditions.FrozeEnemy, StatConditions.ShockedEnemy, StatConditions.IgnitedEnemy,
        StatConditions.ShatteredEnemy, StatConditions.UsedWarcry, StatConditions.ConsumedCorpse,
    };
    sealed class ConditionalItemPoolState
    {
        internal StatSourcesResult? Applied;
        internal bool Building;
        internal long Revision, Presented;
    }

    static readonly ConditionalWeakTable<ClientState, ConditionalItemPoolState> ConditionalItemPools = new();

    // A changed condition must reach the final pools as well as the raw stat dictionary. Compare effective sources:
    // another hit can extend the same four-second window without requiring another pool build.
    internal static bool EnsureConditionalItemPools(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var state = ConditionalItemPools.GetOrCreateValue(conn);
            if (state.Building || conn.Closed || conn.LeavingArea) return false;
            EquipmentStatSources(conn, ItemsOf(conn).EquipmentSnapshot());
            var current = conn.StatSourcesCache;
            var before = state.Applied;
            if (current is null || ReferenceEquals(before, current)) return false;
            state.Applied = current;
            // After a Game-module reload the source marker is new but the connection's old final pools survive.
            // Rebuild conditional equipment on first observation instead of accepting an already-expired old bonus.
            bool conditionalEquipment = before is null && current.Worn.Any(w => w.Item.Mods.Any(m =>
                ItemsFor(conn).Mods.ByHash(m.Hash)?.Stats.Any(s => !s.Local
                    && (s.Id.Contains("_if_", StringComparison.Ordinal) || s.Id.Contains("_while_", StringComparison.Ordinal))) == true));
            if (before is null && !conditionalEquipment || before is not null && before.Attributes == current.Attributes
                && before.Inactive.SetEquals(current.Inactive)
                && before.Sources.Count == current.Sources.Count
                && before.Sources.All(p => current.Sources.TryGetValue(p.Key, out int value) && value == p.Value)) return false;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
            conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            state.Revision++;
            return true;
        }
    }

    static async Task SendConditionalItemStats(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        long revision;
        ConditionalItemPoolState state;
        lock (conn.Monsters)
        {
            EnsureConditionalItemPools(conn);
            state = ConditionalItemPools.GetOrCreateValue(conn);
            revision = state.Revision;
            if (revision == state.Presented) return;
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "conditional item stats changed");
        await SendPlayerMana(remote, stream, ciphers, conn);
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea ? null
                    : GameServer.Core.Protocol.Instance.InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife);
        });
        // Remember the revision sampled before the await; a later change still needs its own publication.
        lock (conn.Monsters) state.Presented = revision;
    }
}
