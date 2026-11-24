using GameServer.Core.World;

public static partial class Game
{
    public static bool ChaosDamageBypassesEnergyShield(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        return Get(Stat.ChaosDamageDoesNotBypassEnergyShield) == 0
            && Get(Stat.VirtualChaosDamageDoesNotBypassEnergyShield) == 0
            && !(Get(Stat.ChaosDamageDoesNotBypassEnergyShieldWhileNotLowLife) != 0
                && (ulong)player.PlayerLife * 100 > (ulong)player.PlayerMaxLife * (uint)LowLifeThresholdPct(stats))
            && !(Get(Stat.ChaosDamageDoesNotBypassEnergyShieldWhileNotLowMana) != 0
                && (player.Pools.Mana == 0 || (ulong)player.PlayerMana * 2 > player.Pools.Mana));
    }

    // Accepted, mitigated hit components: Ward once, typed ES, then defensive Mana (PlayerDamageTaken). types: the hit's
    // damage by type (only its proportions count).
    public static int DamagePlayerHitTyped(Player player, int physical, int other, int chaos, double at,
        IReadOnlyDictionary<uint, int> playerStats, GameServer.Core.World.DamagePacket types)
    {
        if (player.Immune) return 0;
        // "You cannot be Damaged or Killed": base_cannot_be_damaged (BuffDefinitions 261 shrine_godmode, the Divine
        // shrine's 20 s). It is the buff's own stat rather than a shrine-specific flag, so anything else that grants it
        // protects the player the same way.
        if (Stat.BaseCannotBeDamaged is var cannotBeDamaged
            && playerStats.GetValueOrDefault(cannotBeDamaged) > 0) return 0;
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state) || player.LeavingArea) return 0;
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        // Redirect the combined non-chaos hit before Ward, retaining the physical share.
        int nonChaos = (int)Math.Min(int.MaxValue, (long)physical + other);
        var kept = TotemsTakeHitLocked(player, nonChaos, chaos, at, playerStats);
        physical = nonChaos == 0 ? 0 : (int)((long)physical * kept.NonChaos / nonChaos);
        other = kept.NonChaos - physical;
        chaos = kept.Chaos;
        int less = IncomingResourceDefences.LessTakenPct(player.Pools, Get);
        physical = IncomingResourceDefences.Taken(physical, Get, less);
        other = IncomingResourceDefences.Taken(other, Get, less);
        chaos = IncomingResourceDefences.Taken(chaos, Get, less);
        // Doppelgänger Guise's sane conditional final is component-specific and follows ordinary increased Damage Taken.
        // Apply it before every resource pool so it is not repeated between mitigation and allocation.
        physical = ApplyUnhingedTaken(physical, DamageType.Physical, Get);
        chaos = ApplyUnhingedTaken(chaos, DamageType.Chaos, Get);
        (physical, other, chaos) = MitigateTriggeredGuardHit(player, physical, other, chaos, at);
        // Recoup counts the mitigated hit damage before Aegis, guard absorption, Ward and Energy Shield absorb any of it.
        int recoupable = (int)Math.Min(int.MaxValue, (long)physical + other + chaos);
        // Physical Aegis follows mitigation and totem redirection. Path of Exile's damage order puts an Aegis before
        // Ward; only the Aegis overflow reaches Ward, ES, Mana and Life.
        physical = PhysicalAegisDamage(player, physical, at);
        (physical, other, chaos) = AbsorbTriggeredGuardHit(player, physical, other, chaos, at);
        RecordDamageTakenTriggers(player, (int)Math.Min(int.MaxValue, (long)physical + other + chaos), at);
        (physical, other, chaos) = AbsorbPlayerWard(player, physical, other, chaos, at, playerStats);
        CrabPhysicalHitLocked(player, physical, at, playerStats);
        bool protectsMana = Get(Stat.VirtualEnergyShieldProtectsMana) != 0 || Get(Stat.KeystoneEldritchBattery) != 0;
        int bypass = Get(IncomingResourceDefences.BypassStat);
        int physicalLife = protectsMana ? player.EnergyShield.Damage(physical, true, at)
            : IncomingResourceDefences.Damage(player.EnergyShield, physical, bypass, at);
        int otherLife = protectsMana ? player.EnergyShield.Damage(other, true, at)
            : IncomingResourceDefences.Damage(player.EnergyShield, other, bypass, at);
        int nonChaosLife = (int)Math.Min(int.MaxValue, (long)physicalLife + otherLife);
        // Chaos bypasses Energy Shield by default; "Chaos Damage taken does not bypass Energy Shield" and its conditional
        // forms opt out of that, which only Energy Shield that still protects Life can honour.
        int chaosLife = player.EnergyShield.Damage(chaos, protectsMana || ChaosDamageBypassesEnergyShield(player, playerStats), at);
        var manaTypes = RemainingManaDamageTypes(types, physicalLife, otherLife);
        int manaPool = protectsMana ? (int)Math.Min(int.MaxValue, (long)player.PlayerMana + player.EnergyShield.Current) : (int)player.PlayerMana;
        int fromMana = PlayerDamageTaken.FromMana(nonChaosLife, chaosLife, manaTypes, manaPool, Get);
        int toMana = protectsMana ? player.EnergyShield.Damage(fromMana, false, at) : fromMana;
        player.PlayerMana -= (uint)Math.Min(toMana, player.PlayerMana);
        if (ResourceEffectsCurrent(player, state))
            state.Mana.Hit(at, recoupable, Get(Stat.DamageTakenGoesToManaPct), ManaRecoupSeconds(Get));
        // Petrified Blood sees the life loss after Ward, Energy Shield and Mana: it protects Life, not the other pools.
        // Vaal Immortal Call's "you cannot die" holds that Life at 1 (wiki); the hit and its effects still land.
        int lifeDamage = VaalImmortalCallHitLife(player,
            PetrifiedBlood.PreventHitLifeLoss(player, (int)Math.Min(int.MaxValue, (long)nonChaosLife + chaosLife - fromMana), at), at);
        RecordRechargeLifeDamage(player, lifeDamage, at, playerStats);
        return lifeDamage;
    }

    /// <summary>Takes already-mitigated Chaos damage over time through Energy Shield (when not bypassed) and then from
    /// Mana before Life (<c>chaos_damage_%_taken_from_mana_before_life</c> and shared Mana-before-Life), returning
    /// the remaining Life loss.</summary>
    public static int DamagePlayerChaosOverTime(Player player, int chaosDamage, double at, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (chaosDamage <= 0) return 0;
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        bool protectsMana = Get(Stat.VirtualEnergyShieldProtectsMana) != 0 || Get(Stat.KeystoneEldritchBattery) != 0;
        bool bypass = ChaosDamageBypassesEnergyShield(player, playerStats);
        int remaining = IncomingResourceDefences.DamageOverTime(player.EnergyShield, chaosDamage,
            (protectsMana || bypass) ? 100 : 0, at, ref player.ChaosDotBypassCarry);
        if (remaining <= 0) return 0;
        int manaPct = PlayerDamageTaken.ManaBeforeLifePct(GameServer.Core.World.DamageType.Chaos, Get);
        if (manaPct <= 0)
        {
            player.ChaosDotManaCarry = 0;
            return remaining;
        }
        bool esProtectsManaFromChaos = protectsMana && !bypass;
        int manaPool = esProtectsManaFromChaos
            ? (int)Math.Min(int.MaxValue, (long)player.PlayerMana + player.EnergyShield.Current)
            : (int)player.PlayerMana;
        int fromMana;
        if (manaPct >= 100)
        {
            player.ChaosDotManaCarry = 0;
            fromMana = Math.Min(manaPool, remaining);
        }
        else
        {
            double share = remaining * (manaPct / 100.0) + player.ChaosDotManaCarry;
            int desired = (int)Math.Floor(share + 1e-9);
            player.ChaosDotManaCarry = share - desired;
            fromMana = Math.Clamp(desired, 0, manaPool);
        }
        if (fromMana > 0)
        {
            int toMana = esProtectsManaFromChaos ? player.EnergyShield.Damage(fromMana, false, at) : fromMana;
            player.PlayerMana -= (uint)Math.Min(toMana, player.PlayerMana);
        }
        return remaining - fromMana;
    }

    // Mana-before-Life uses the remaining typed weights after Aegis, Ward and ES. In particular, physical damage
    // removed by an Aegis must not keep weighting a typed Mana share for another damage type.
    static GameServer.Core.World.DamagePacket RemainingManaDamageTypes(
        GameServer.Core.World.DamagePacket original, int physicalLife, int otherLife)
    {
        var result = GameServer.Core.World.DamagePacket.Empty;
        if (physicalLife > 0)
            result = result.Add(GameServer.Core.World.DamageType.Physical,
                new GameServer.Core.World.DamageRange(physicalLife, physicalLife));
        var components = original.Components.Where(c => c.Type is not (GameServer.Core.World.DamageType.Physical
            or GameServer.Core.World.DamageType.Chaos) && c.Range.Min > 0).ToArray();
        double total = components.Sum(c => c.Range.Min);
        if (otherLife <= 0 || total <= 0) return result;
        foreach (var (type, range) in components)
        {
            double amount = range.Min * otherLife / total;
            result = result.Add(type, new GameServer.Core.World.DamageRange(amount, amount));
        }
        return result;
    }

    public static (int Physical, int Other, int Chaos) AbsorbPlayerWard(Player player,
        int physical, int other, int chaos, double now, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        int nonChaos = (int)Math.Min(int.MaxValue, (long)physical + other);
        int total = (int)Math.Min(int.MaxValue, (long)nonChaos + chaos);
        player.Ward.Advance(now);
        bool wasBroken = player.Ward.Broken;
        int remaining = player.Ward.Damage(total, now, Get);
        int absorbed = total - remaining;
        // Ordered absorption keeps chaos bypassing ES. The supported shared Ward bypass is applied per component.
        int bypass = Math.Clamp(Get(Stat.BaseDamageBypassWardPct), 0, 100);
        int nonChaosEligible = nonChaos - (int)((long)nonChaos * bypass / 100);
        int nonChaosAbsorbed = Math.Min(nonChaosEligible, absorbed);
        int physicalEligible = physical - (int)((long)physical * bypass / 100);
        int physicalAbsorbed = Math.Min(physicalEligible, nonChaosAbsorbed);
        if (!wasBroken && player.Ward.Broken
            && player.Antiquarian.GrantAdrenaline(now, Get(AntiquarianEffects.AdrenalineSource)))
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
        return (physical - physicalAbsorbed, Math.Max(0, other - (nonChaosAbsorbed - physicalAbsorbed)),
            Math.Max(0, chaos - (absorbed - nonChaosAbsorbed)));
    }

}
