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
        double now = ProjectileWorldTime(player);
        var session = LeechOf(player);
        long lifeLeechCap = 0;
        bool redirects = false;
        foreach (var link in player.Links.All)
        {
            if (VampiricMinionAt(player, link, now) is null) continue;
            redirects = true;
            lifeLeechCap += ScaledLinkStat(link, Stat.RemoraLinkGrantsMaximumLifeLeechRatePctPerMinute,
                Math.Max(0, 100L + get(Stat.LinkSkillBuffEffectPlusPct)));
        }
        int WithVampiricCap(uint key) => key != Stat.MaximumLifeLeechRatePctPerMinute
            ? get(key) : ActorScale.Saturate(get(key) + lifeLeechCap);
        session.SetLifeRecoveryRedirected(now, redirects);
        session.Configure(now,
            LeechLimits.Resolve(WithVampiricCap, player.Pools.Life, player.Pools.Mana, player.Pools.EnergyShield, Replacement, onLowLife),
            get, Replacement, claws);
        session.OnKill = KillRecovery.From(get, player.Pools.Level);
        TrimLeechLocked(player);
    }

    // Pool commits (including instant flasks/block gains) cannot leave ordinary leech alive until the next tick.
    public static void TrimLeechLocked(Player player)
    {
        double now = ProjectileWorldTime(player);
        if (player.PlayerLife == 0) LeechOf(player).Clear(now);
        else
        {
            var session = LeechOf(player);
            session.SetLifeRecoveryRedirected(now, HasVampiricMinion(player, now));
            session.ClearFull(now, player.PlayerLife >= LifeCap(player),
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
        int directDamage, LeechSources sources) => LeechOnHitTargetLocked(player, target, taken, directDamage, sources, targetId: 0);

    static void LeechOnHitTargetLocked(Player player, SpawnedMonster target, DamagePacket taken,
        int directDamage, LeechSources sources, uint targetId)
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
        double now = ProjectileWorldTime(player);
        if (sources.LifeLeechVsBleeding is > 0 and var leechVsBleeding && target.Bleeds.IgnitedAt(now))
            sources = sources with { Life = sources.Life.Add(DamagePacket.Empty.Select((_, _) => new(leechVsBleeding, leechVsBleeding))) };
        var amounts = sources.Amounts(taken, directDamage);
        // Called before the hit's damage is applied, so the target's Life is what the hit has to overkill.
        amounts.Life += sources.OverkillLife(directDamage, target.Life);
        var session = LeechOf(player);
        if (target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0) amounts = default;
        else
        {
            if (target.Effects[Stat.CannotHaveLifeLeechedFrom] != 0) amounts.Life = 0;
            if (target.Effects[Stat.CannotHaveManaLeechedFrom] != 0) amounts.Mana = 0;
            if (target.Effects[Stat.CannotHaveEnergyShieldLeechedFrom] != 0)
            {
                amounts.EnergyShield = 0;
                if (session.ConvertsLifeToEnergyShield) amounts.Life = 0;
            }
        }
        session.SetLifeRecoveryRedirected(now, HasVampiricMinion(player, now));
        double powerInstant = sources.InstantLeechPerEnemyPower == 0 ? 0
            : (double)sources.InstantLeechPerEnemyPower * WarcryPower(target.Rarity);
        var instant = session.HitWithWeapon(now, amounts, player.PlayerLife >= LifeCap(player),
            player.PlayerMana >= UnreservedMana(player), player.EnergyShield.Current >= player.EnergyShield.Maximum,
            sources.Attack, sources.Melee, sources.WeaponLifeLeechInstant, powerInstant);
        if (instant.Life > 0 && RecoverVampiricMinions(player, instant.Life, now)) instant.Life = 0;
        var gain = sources.GainOnHit;
        var cursedGain = sources.GainOnCursedAttackHit;
        if (sources.Attack && (cursedGain.Life > 0 || cursedGain.Mana > 0) && target.HasCurses
            && target.Curses.All.Values.Any(curse => curse.Until > now))
        {
            gain.Life = ActorScale.Saturate((long)gain.Life + cursedGain.Life);
            gain.Mana = ActorScale.Saturate((long)gain.Mana + cursedGain.Mana);
        }
        if (sources.LifeOnBleedingHit > 0 && target.Bleeds.IgnitedAt(now))
            gain.Life = ActorScale.Saturate((long)gain.Life + sources.LifeOnBleedingHit);
        if (sources.LifeOnIgnitedHit > 0 && target.Ignites.IgnitedAt(now))
            gain.Life = ActorScale.Saturate((long)gain.Life + sources.LifeOnIgnitedHit);
        if (sources.LifeOnBlindedHit > 0)
        {
            if (targetId == 0) targetId = player.Area.Monsters.FirstOrDefault(entry => ReferenceEquals(entry.Value, target)).Key;
            if (targetId != 0 && Blinded(player, targetId, now))
                gain.Life = ActorScale.Saturate((long)gain.Life + sources.LifeOnBlindedHit);
        }
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

    internal static (double Life, double Mana, double EnergyShield) TakeLeechRecovery(Player player, double now,
        bool lifeFull, bool manaFull, bool shieldFull)
    {
        var session = LeechOf(player);
        session.SetLifeRecoveryRedirected(now, HasVampiricMinion(player, now));
        session.ClearFull(now, lifeFull, manaFull, shieldFull);
        var recovery = session.TakeRecovery(now);
        if (recovery.Life > 0 && RecoverVampiricMinions(player, recovery.Life, now, overTime: true)) recovery.Life = 0;
        return recovery;
    }

    static void LeechFromMinionDamageLocked(Player player, SpawnedMonster target, int damage, double now,
        IReadOnlyDictionary<uint, int>? ownerStats)
    {
        if (damage <= 0 || player.PlayerLife == 0 || target.Effects[Stat.CannotHaveLifeLeechedFrom] != 0
            || target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0) return;
        ownerStats ??= CharacterPlayerStats(player);
        int rate = ownerStats.GetValueOrDefault(Stat.LifeLeechFromMinionDamagePct);
        LeechFromOwnedDamageLocked(player, target, damage * (rate / 100d), now);
    }

    static void LeechFromTotemHitLocked(Player player, SpawnedMonster target, int damage, double now,
        IReadOnlyDictionary<uint, int> ownerStats)
    {
        int rate = ownerStats.GetValueOrDefault(Stat.TotemDamageLeechedAsLifeToYouPermyriad);
        if (rate <= 0 || damage <= 0 || !target.Hostile || target.Invulnerable || player.PlayerLife == 0
            || target.Effects[Stat.CannotHaveLifeLeechedFrom] != 0 || target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0)
            return;
        LeechFromOwnedDamageLocked(player, target, damage * (rate / 10000d), now);
    }

    static void LeechFromOwnedDamageLocked(Player player, SpawnedMonster target, double amount, double now)
    {
        if (amount <= 0) return;
        var session = LeechOf(player);
        if (session.ConvertsLifeToEnergyShield && target.Effects[Stat.CannotHaveEnergyShieldLeechedFrom] != 0) return;
        session.SetLifeRecoveryRedirected(now, HasVampiricMinion(player, now));
        var instant = session.Hit(now, (amount, 0, 0),
            player.PlayerLife >= LifeCap(player), player.PlayerMana >= UnreservedMana(player),
            player.EnergyShield.Current >= player.EnergyShield.Maximum);
        if (instant.Life > 0 && !RecoverVampiricMinions(player, instant.Life, now))
        {
            uint before = player.PlayerLife;
            player.PlayerLife = PetrifiedBlood.RecoverLife(player, before, instant.Life, ref session.LifeInstantCarry, minimum: 1);
            session.InstantLifeChanged |= player.PlayerLife != before;
        }
        player.EnergyShield.Gain(instant.EnergyShield);
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
