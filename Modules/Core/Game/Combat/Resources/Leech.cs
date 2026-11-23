using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static readonly Store<Player, LeechSession> LeechSessions = new();
    public static LeechSession LeechOf(Player player) => LeechSessions.GetValue(player, _ => new());

    // Invisible BuffDefinitions rows for the leech overlay on the life, mana and energy-shield orbs.
    const ushort LifeLeechBuffRow = 39, ManaLeechBuffRow = 38, EnergyShieldLeechBuffRow = 40;

    public static (bool Life, bool Mana, bool EnergyShield, bool FullEnergyShield) LeechContext(Player player)
    {
        TrimLeechLocked(player);
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return default;
        double now = ProjectileWorldTime(player);
        var session = LeechOf(player);
        var active = session.Active(now);
        // A step that reads it runs again when a leech stops on its own.
        if (session.NextChangeAt(now) is double stops) StatsChangeAt(stops, () => ProjectileWorldTime(player));
        return (active.Life, active.Mana, active.EnergyShield, player.EnergyShield.Full);
    }

    public static void WithLeechConditions(Player player, StatMap stats)
    {
        var state = LeechContext(player);
        foreach (var (name, active) in new[] { (Stat.IsLifeLeeching, state.Life), (Stat.IsManaLeeching, state.Mana),
            (Stat.IsEsLeeching, state.EnergyShield), (Stat.IsLeeching, state.Life || state.Mana || state.EnergyShield),
            (Stat.OnFullEnergyShield, state.FullEnergyShield) })
            if (name is var key) stats[key] = active ? 1 : 0;
    }

    // "X% increased Attack Speed while Leeching" (Endless Hunger): the client's increased attack speed sum (0x142704be0)
    // adds it while is_leeching. The server sends the final main-hand value, so it joins there.
    public static int LeechingAttackSpeed(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        var state = LeechContext(player);
        return (state.Life || state.Mana || state.EnergyShield) && Stat.AttackSpeedPlusPctWhileLeeching is var key
            ? stats.GetValueOrDefault(key) : 0;
    }

    // Endless Hunger's two "while Leeching" defences, on the final flags the server sends and reads (client 0x1427e17d0:
    // cannot_be_stunned_while_leeching and is_leeching; 0x142896810 the same for virtual_unaffected_by_bleeding).
    public static void WithLeechingDefences(StatMap stats)
    {
        int Get(uint k) => stats.GetValueOrDefault(k);
        bool leeching = Get(Stat.IsLeeching) != 0;
        if (Stat.CannotBeStunned is var stun
            && leeching && Get(Stat.CannotBeStunnedWhileLeeching) != 0) stats[stun] = 1;
        if (Stat.VirtualUnaffectedByBleeding is var bleed)
            stats[bleed] = Get(Stat.BaseUnaffectedByBleeding) != 0 || leeching && Get(Stat.UnaffectedByBleedingWhileLeeching) != 0
                ? 1 : 0;
    }

    public static void ConfigureLeech(Player player, Func<uint, int> get, bool onLowLife,
        IReadOnlyDictionary<uint, int>? replacements = null)
    {
        int? Replacement(uint key) => replacements is not null && replacements.TryGetValue(key, out int value) ? value : null;
        int claws = (WeaponIdentity.CountsAs(ActiveMainHand(player), ItemsFor(player).Mods, "Claw") ? 1 : 0)
            + (WeaponIdentity.CountsAs(ActiveOffHand(player), ItemsFor(player).Mods, "Claw") ? 1 : 0);
        LeechOf(player).Configure(ProjectileWorldTime(player),
            LeechLimits.Resolve(get, player.Pools.Life, player.Pools.Mana, player.Pools.EnergyShield, Replacement, onLowLife),
            get, Replacement, claws);
        LeechOf(player).OnKill = KillRecovery.From(get, player.Pools.Level);
        TrimLeechLocked(player);
    }

    // Pool commits (including instant flasks/block gains) cannot leave ordinary leech alive until the next tick.
    public static void TrimLeechLocked(Player player)
    {
        double now = ProjectileWorldTime(player);
        if (player.PlayerLife == 0) LeechOf(player).Clear(now);
        else
        {
            LeechOf(player).ClearFull(now, player.PlayerLife >= LifeCap(player),
                player.PlayerMana >= UnreservedMana(player), player.EnergyShield.Current >= player.EnergyShield.Maximum);
            SettleFullManaLocked(player, ResourceEffectsTime(player));
        }
    }

    public static LeechSources HitLeechSources(IReadOnlyDictionary<uint, int> player, bool attack,
        IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<uint, int>? local = null, bool melee = false)
    {
        int Get(uint key) => (int)Math.Clamp((long)player.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0), int.MinValue, int.MaxValue);
        return LeechSources.From(Get, attack, name => local?.GetValueOrDefault(name) ?? 0) with { Melee = melee };
    }

    // Invoke only inside an accepted direct-hit transaction, after evasion/block and before impale reflection.
    public static void LeechOnHitLocked(Player player, SpawnedMonster target, DamagePacket taken,
        int directDamage, LeechSources sources)
    {
        if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
        if (sources.Attack)
        {
            DamagePacket Extra(string pool) => DamageTypes.ConversionOrder.Aggregate(DamagePacket.Empty,
                (p, type) => p.Add(DamagePacket.Of(type, target.Curses[Stat.Of(pool + "_leech_on_any_damage_when_hit_by_attack_permyriad")],
                    target.Curses[Stat.Of(pool + "_leech_on_any_damage_when_hit_by_attack_permyriad")])));
            sources = sources with { Life = sources.Life.Add(Extra("life")), Mana = sources.Mana.Add(Extra("mana")),
                GainOnHit = (sources.GainOnHit.Life + target.Curses[Stat.LifeGrantedWhenHitByAttacks],
                    sources.GainOnHit.Mana + target.Curses[Stat.ManaGrantedWhenHitByAttacks], sources.GainOnHit.EnergyShield) };
        }
        var amounts = sources.Amounts(taken, directDamage);
        // Called before the hit's damage is applied, so the target's Life is what the hit has to overkill.
        amounts.Life += sources.OverkillLife(directDamage, target.Life);
        var session = LeechOf(player);
        if (target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0) amounts = default;
        else
        {
            if (target.Effects[Stat.CannotHaveLifeLeechedFrom] != 0) amounts.Life = 0;
            if (target.Effects[Stat.CannotHaveEnergyShieldLeechedFrom] != 0)
            {
                amounts.EnergyShield = 0;
                if (session.ConvertsLifeToEnergyShield) amounts.Life = 0;
            }
        }
        double now = ProjectileWorldTime(player);
        var instant = session.Hit(now, amounts, player.PlayerLife >= LifeCap(player),
            player.PlayerMana >= UnreservedMana(player), player.EnergyShield.Current >= player.EnergyShield.Maximum, sources.Attack, sources.Melee);
        var gain = sources.GainOnHit;
        uint life = player.PlayerLife, mana = player.PlayerMana;
        // Instant portions and the gain per enemy hit resolve in this hit transaction, so a later enemy hit sees the
        // recovered resource. Keep sub-point recovery between hits, and discard overflow at a full pool. Petrified
        // Blood caps the actual recovery like regeneration/Recoup; the leech instance itself remains until full Life.
        // Vaal Pact blocks the life gain on hit (it is not leech); the leeched instant life still recovers.
        player.PlayerLife = PetrifiedBlood.RecoverLife(player, life,
            instant.Life + (session.RecoversLife && !session.LifeGainOnHitBlocked ? gain.Life : 0), ref session.LifeInstantCarry, minimum: 1);
        player.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(player), instant.Mana + gain.Mana,
            ref session.ManaInstantCarry);
        player.EnergyShield.Gain(instant.EnergyShield + (session.RecoversEnergyShield ? gain.EnergyShield : 0));
        session.InstantLifeChanged |= life != player.PlayerLife;
        session.InstantManaChanged |= mana != player.PlayerMana;
        TrimLeechLocked(player);
    }

    /// <summary>
    /// The invisible life/mana/energy-shield leech recovery buffs the client draws on the resource orbs. Each buff
    /// carries the full current recovery per minute and its is-*-leeching flag. The rate is deliberately not reduced
    /// by Petrified Blood's Low Life recovery cap: the orb shows the leech's full potential while the server caps the
    /// actual pool update.
    /// </summary>
    public static byte[]? LeechBuffPresentation(Player player)
    {
        double now = ProjectileWorldTime(player);
        var display = LeechOf(player).Display(now);
        var state = player.LeechDisplay;
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
                player.Area.Buffs.Clear(player.Id, row, player.Id);
                buff = 0;
                perMinute = 0;
                expiry = 0;
            }
            if (!show || buff != 0) return;
            buff = 1;
            player.Area.Buffs.Set(player.Id, new(row, player.Id, Values: [(uint)Math.Min(value.PerMinute, int.MaxValue), 1u],
                U68: Buff.Seconds(value.Expiry - now)));
            perMinute = value.PerMinute;
            expiry = value.Expiry;
        }
    }

    // A stat presentation: on a granted or removed instance (DisplayChanged), and when the next one ends.
    public static Task SendLeechBuffs(Player player)
    {
        double now = ProjectileWorldTime(player);
        if (LeechOf(player).NextExpiry(now) is double expires) PresentIn(player, expires - now);
        return Send(player, [], null, atGate: () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired
                || (!player.Connection.PlayerObjectCreated && !player.Connection.PlayerSent)) return null;
            return LeechBuffPresentation(player);
        });
    }
}
