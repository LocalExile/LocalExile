using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static readonly RuleTable<ClientState, LeechSession> LeechSessions = new();
    public static LeechSession LeechOf(ClientState conn) => LeechSessions.GetValue(conn, _ => new());

    // Invisible BuffDefinitions rows for the leech overlay on the life, mana and energy-shield orbs.
    const ushort LifeLeechBuffRow = 39, ManaLeechBuffRow = 38, EnergyShieldLeechBuffRow = 40;

    public static (bool Life, bool Mana, bool EnergyShield, bool FullEnergyShield) LeechContext(ClientState conn)
    {
        lock (conn.Monsters)
        {
            TrimLeechLocked(conn);
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return default;
            var active = LeechOf(conn).Active(ProjectileWorldTime(conn));
            return (active.Life, active.Mana, active.EnergyShield, conn.EnergyShield.Full);
        }
    }

    public static void WithLeechConditions(ClientState conn, Dictionary<uint, int> stats)
    {
        var state = LeechContext(conn);
        foreach (var (name, active) in new[] { ("is_life_leeching", state.Life), ("is_mana_leeching", state.Mana),
            ("is_es_leeching", state.EnergyShield), ("is_leeching", state.Life || state.Mana || state.EnergyShield),
            ("on_full_energy_shield", state.FullEnergyShield) })
            if (Stats.Key(name) is uint key) stats[key] = active ? 1 : 0;
    }

    // "X% increased Attack Speed while Leeching" (Endless Hunger): the client's increased attack speed sum (0x142704be0)
    // adds it while is_leeching. The server sends the final main-hand value, so it joins there.
    public static int LeechingAttackSpeed(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        var state = LeechContext(conn);
        return (state.Life || state.Mana || state.EnergyShield) && Stats.Key("attack_speed_+%_while_leeching") is uint key
            ? stats.GetValueOrDefault(key) : 0;
    }

    // Endless Hunger's two "while Leeching" defences, on the final flags the server sends and reads (client 0x1427e17d0:
    // cannot_be_stunned_while_leeching and is_leeching; 0x142896810 the same for virtual_unaffected_by_bleeding).
    // A /set_stat value of either flag keeps its value.
    public static void WithLeechingDefences(Dictionary<uint, int> stats, IReadOnlySet<uint> devSet)
    {
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        bool leeching = Get("is_leeching") != 0;
        if (Stats.Key("cannot_be_stunned") is uint stun && !devSet.Contains(stun)
            && leeching && Get("cannot_be_stunned_while_leeching") != 0) stats[stun] = 1;
        if (Stats.Key("virtual_unaffected_by_bleeding") is uint bleed && !devSet.Contains(bleed))
            stats[bleed] = Get("base_unaffected_by_bleeding") != 0 || leeching && Get("unaffected_by_bleeding_while_leeching") != 0
                ? 1 : 0;
    }

    public static void ConfigureLeech(ClientState conn, Func<string, int> get, IReadOnlyDictionary<uint, int> replacements,
        bool onLowLife)
    {
        int? Replacement(string name) => Stats.Key(name) is uint key && replacements.TryGetValue(key, out int value) ? value : null;
        lock (conn.Monsters)
        {
            int claws = (ActiveMainHand(conn)?.Type?.Class == "Claw" ? 1 : 0)
                + (ActiveOffHand(conn)?.Type?.Class == "Claw" ? 1 : 0);
            LeechOf(conn).Configure(ProjectileWorldTime(conn),
                LeechLimits.Resolve(get, conn.Pools.Life, conn.Pools.Mana, conn.Pools.EnergyShield, Replacement, onLowLife),
                get, Replacement, claws);
            LeechOf(conn).OnKill = KillRecovery.From(get, conn.Pools.Level);
            TrimLeechLocked(conn);
        }
    }

    // Pool commits (including instant flasks/block gains) cannot leave ordinary leech alive until the next tick.
    public static void TrimLeechLocked(ClientState conn)
    {
        double now = ProjectileWorldTime(conn);
        if (conn.PlayerLife == 0) LeechOf(conn).Clear(now);
        else
        {
            LeechOf(conn).ClearFull(now, conn.PlayerLife >= conn.PlayerUnreservedLife,
                conn.PlayerMana >= UnreservedMana(conn), conn.EnergyShield.Current >= conn.EnergyShield.Maximum);
            SettleFullManaLocked(conn, ResourceEffectsTime(conn));
        }
    }

    public static LeechSources HitLeechSources(IReadOnlyDictionary<uint, int> player, bool attack,
        IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<string, int>? local = null)
    {
        int Get(string name) => Stats.Key(name) is uint key
            ? (int)Math.Clamp((long)player.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0), int.MinValue, int.MaxValue) : 0;
        return LeechSources.From(Get, attack, name => local?.GetValueOrDefault(name) ?? 0);
    }

    // Invoke only inside an accepted direct-hit transaction, after evasion/block and before impale reflection.
    public static void LeechOnHitLocked(ClientState conn, SpawnedMonster target, DamagePacket taken,
        int directDamage, LeechSources sources)
    {
        if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return;
        if (sources.Attack)
        {
            DamagePacket Extra(string pool) => DamageTypes.ConversionOrder.Aggregate(DamagePacket.Empty,
                (p, type) => p.Add(DamagePacket.Of(type, target.Curses[pool + "_leech_on_any_damage_when_hit_by_attack_permyriad"],
                    target.Curses[pool + "_leech_on_any_damage_when_hit_by_attack_permyriad"])));
            sources = sources with { Life = sources.Life.Add(Extra("life")), Mana = sources.Mana.Add(Extra("mana")),
                GainOnHit = (sources.GainOnHit.Life + target.Curses["life_granted_when_hit_by_attacks"],
                    sources.GainOnHit.Mana + target.Curses["mana_granted_when_hit_by_attacks"], sources.GainOnHit.EnergyShield) };
        }
        var amounts = sources.Amounts(taken, directDamage);
        // Called before the hit's damage is applied, so the target's Life is what the hit has to overkill.
        amounts.Life += sources.OverkillLife(directDamage, target.Life);
        var session = LeechOf(conn);
        if (target.Effects["cannot_have_life_leeched_from"] != 0) amounts.Life = 0;
        if (target.Effects["cannot_have_energy_shield_leeched_from"] != 0)
        {
            amounts.EnergyShield = 0;
            if (session.ConvertsLifeToEnergyShield) amounts.Life = 0;
        }
        double now = ProjectileWorldTime(conn);
        var instant = session.Hit(now, amounts, conn.PlayerLife >= conn.PlayerUnreservedLife,
            conn.PlayerMana >= UnreservedMana(conn), conn.EnergyShield.Current >= conn.EnergyShield.Maximum, sources.Attack);
        var gain = sources.GainOnHit;
        uint life = conn.PlayerLife, mana = conn.PlayerMana;
        // Instant portions and the gain per enemy hit resolve in this hit transaction, so a later enemy hit sees the
        // recovered resource. Keep sub-point recovery between hits, and discard overflow at a full pool. Petrified
        // Blood caps the actual recovery like regeneration/Recoup; the leech instance itself remains until full Life.
        conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, life,
            instant.Life + (session.RecoversLife ? gain.Life : 0), ref session.LifeInstantCarry, minimum: 1);
        conn.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(conn), instant.Mana + gain.Mana,
            ref session.ManaInstantCarry);
        conn.EnergyShield.Gain(instant.EnergyShield + (session.RecoversEnergyShield ? gain.EnergyShield : 0));
        session.InstantLifeChanged |= life != conn.PlayerLife;
        session.InstantManaChanged |= mana != conn.PlayerMana;
        TrimLeechLocked(conn);
    }

    /// <summary>
    /// The invisible life/mana/energy-shield leech recovery buffs the client draws on the resource orbs. Each buff
    /// carries the full current recovery per minute and its is-*-leeching flag. The rate is deliberately not reduced
    /// by Petrified Blood's Low Life recovery cap: the orb shows the leech's full potential while the server caps the
    /// actual pool update.
    /// </summary>
    public static byte[]? LeechBuffPresentation(ClientState conn)
    {
        double now = ProjectileWorldTime(conn);
        var display = LeechOf(conn).Display(now);
        var state = conn.LeechDisplay;
        var packets = new List<byte[]>();
        Sync(display.Life, LifeLeechBuffRow, ref state.LifeBuff, ref state.LifePerMinute, ref state.LifeExpiry);
        Sync(display.Mana, ManaLeechBuffRow, ref state.ManaBuff, ref state.ManaPerMinute, ref state.ManaExpiry);
        Sync(display.EnergyShield, EnergyShieldLeechBuffRow, ref state.EnergyShieldBuff,
            ref state.EnergyShieldPerMinute, ref state.EnergyShieldExpiry);
        return packets.Count == 0 ? null : [.. packets.SelectMany(packet => packet)];

        void Sync(LeechRecoveryState.DisplayState value, ushort row, ref ushort buff, ref int perMinute,
            ref double expiry)
        {
            bool show = value.PerMinute > 0 && value.Expiry > now;
            if (buff != 0 && (!show || perMinute != value.PerMinute || expiry != value.Expiry))
            {
                packets.Add(InstanceEntities.RemoveBuff(PlayerId, buff));
                buff = 0;
                perMinute = 0;
                expiry = 0;
            }
            if (!show || buff != 0) return;
            buff = conn.NextBuff++;
            packets.Add(InstanceEntities.AddBuff(PlayerId, buff, row, PlayerId,
                values: [(uint)Math.Min(value.PerMinute, int.MaxValue), 1u],
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, value.Expiry - now))));
            perMinute = value.PerMinute;
            expiry = value.Expiry;
        }
    }

    public static Task SendLeechBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.RageRetired
                    || (!conn.PlayerObjectCreated && !conn.PlayerSent)) return null;
                return LeechBuffPresentation(conn);
            }
        });
}
