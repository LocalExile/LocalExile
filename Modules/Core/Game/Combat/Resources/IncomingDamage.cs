using GameServer.Core.World;

public static partial class Game
{
    const string CorruptedSoul = "keystone_corrupted_defences";

    static void ProjectCorruptedSoul(Dictionary<uint, int> sources, IReadOnlyDictionary<uint, int> replacements)
    {
        if (Stats.Key(CorruptedSoul) is uint flag && replacements.GetValueOrDefault(flag, sources.GetValueOrDefault(flag)) != 0
            && Stats.Key(IncomingResourceDefences.BypassStat) is uint bypass)
            sources[bypass] = ActorScale.Saturate((long)sources.GetValueOrDefault(bypass) + 50);
    }

    public static bool ChaosDamageBypassesEnergyShield(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return Get("chaos_damage_does_not_bypass_energy_shield") == 0
            && Get("virtual_chaos_damage_does_not_bypass_energy_shield") == 0
            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_life") != 0
                && (ulong)conn.PlayerLife * 100 > (ulong)conn.PlayerMaxLife * (uint)LowLifeThresholdPct(stats))
            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_mana") != 0
                && (ulong)conn.PlayerMana * 2 > conn.Pools.Mana);
    }

    // Accepted, mitigated hit components: Ward once, typed ES, then defensive Mana (PlayerDamageTaken). types: the hit's
    // damage by type (only its proportions count).
    public static int DamagePlayerHitTyped(ClientState conn, int physical, int other, int chaos, double at,
        IReadOnlyDictionary<uint, int> player, GameServer.Core.World.DamagePacket types)
    {
        if (conn.Immune) return 0;
        // "You cannot be Damaged or Killed": base_cannot_be_damaged (BuffDefinitions 261 shrine_godmode, the Divine
        // shrine's 20 s). It is the buff's own stat rather than a shrine-specific flag, so anything else that grants it
        // protects the player the same way.
        if (Stats.Key("base_cannot_be_damaged") is uint cannotBeDamaged
            && player.GetValueOrDefault(cannotBeDamaged) > 0) return 0;
        var state = ResourceEffectsOf(conn);
        if (!ResourceEffectsCurrent(conn, state) || conn.LeavingArea) return 0;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        // Redirect the combined non-chaos hit before Ward, retaining the physical share.
        int nonChaos = (int)Math.Min(int.MaxValue, (long)physical + other);
        var kept = TotemsTakeHitLocked(conn, nonChaos, chaos, at, player);
        physical = nonChaos == 0 ? 0 : (int)((long)physical * kept.NonChaos / nonChaos);
        other = kept.NonChaos - physical;
        chaos = kept.Chaos;
        int less = IncomingResourceDefences.LessTakenPct(conn.Pools, Get);
        physical = IncomingResourceDefences.Taken(physical, Get, less);
        other = IncomingResourceDefences.Taken(other, Get, less);
        chaos = IncomingResourceDefences.Taken(chaos, Get, less);
        // Doppelgänger Guise's sane conditional final is component-specific and follows ordinary increased Damage Taken.
        // Apply it before every resource pool so it is not repeated between mitigation and allocation.
        physical = ApplyUnhingedTaken(physical, DamageType.Physical, Get);
        chaos = ApplyUnhingedTaken(chaos, DamageType.Chaos, Get);
        (physical, other, chaos) = MitigateTriggeredGuardHit(conn, physical, other, chaos, at);
        // Physical Aegis follows mitigation and totem redirection. Path of Exile's damage order puts an Aegis before
        // Ward; only the Aegis overflow reaches Ward, ES, Mana and Life.
        physical = PhysicalAegisDamage(conn, physical, at);
        (physical, other, chaos) = AbsorbTriggeredGuardHit(conn, physical, other, chaos, at);
        RecordDamageTakenTriggers(conn, (int)Math.Min(int.MaxValue, (long)physical + other + chaos), at);
        (physical, other, chaos) = AbsorbPlayerWard(conn, physical, other, chaos, at, player);
        CrabPhysicalHitLocked(conn, physical, at, player);
        bool protectsMana = Get("virtual_energy_shield_protects_mana") != 0 || Get("keystone_eldritch_battery") != 0;
        int bypass = Get(IncomingResourceDefences.BypassStat);
        int physicalLife = protectsMana ? conn.EnergyShield.Damage(physical, true, at)
            : IncomingResourceDefences.Damage(conn.EnergyShield, physical, bypass, at);
        int otherLife = protectsMana ? conn.EnergyShield.Damage(other, true, at)
            : IncomingResourceDefences.Damage(conn.EnergyShield, other, bypass, at);
        int nonChaosLife = (int)Math.Min(int.MaxValue, (long)physicalLife + otherLife);
        int chaosLife = conn.EnergyShield.Damage(chaos, true, at);
        var manaTypes = RemainingManaDamageTypes(types, physicalLife, otherLife);
        int manaPool = protectsMana ? (int)Math.Min(int.MaxValue, (long)conn.PlayerMana + conn.EnergyShield.Current) : (int)conn.PlayerMana;
        int fromMana = PlayerDamageTaken.FromMana(nonChaosLife, chaosLife, manaTypes, manaPool, Get);
        int toMana = protectsMana ? conn.EnergyShield.Damage(fromMana, false, at) : fromMana;
        conn.PlayerMana -= (uint)Math.Min(toMana, conn.PlayerMana);
        if (ResourceEffectsCurrent(conn, state)) state.Mana.Hit(at,
            (int)Math.Min(int.MaxValue, (long)physical + other + chaos), Get("damage_taken_goes_to_mana_%"));
        // Petrified Blood sees the life loss after Ward, Energy Shield and Mana: it protects Life, not the other pools.
        // Vaal Immortal Call's "you cannot die" holds that Life at 1 (wiki); the hit and its effects still land.
        return VaalImmortalCallHitLife(conn,
            PetrifiedBlood.PreventHitLifeLoss(conn, (int)Math.Min(int.MaxValue, (long)nonChaosLife + chaosLife - fromMana), at), at);
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

    public static (int Physical, int Other, int Chaos) AbsorbPlayerWard(ClientState conn,
        int physical, int other, int chaos, double now, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int nonChaos = (int)Math.Min(int.MaxValue, (long)physical + other);
        int total = (int)Math.Min(int.MaxValue, (long)nonChaos + chaos);
        conn.Ward.Advance(now);
        bool wasBroken = conn.Ward.Broken;
        int remaining = conn.Ward.Damage(total, now, Get);
        int absorbed = total - remaining;
        // Ordered absorption keeps chaos bypassing ES. The supported shared Ward bypass is applied per component.
        int bypass = Math.Clamp(Get("base_damage_bypass_ward_%"), 0, 100);
        int nonChaosEligible = nonChaos - (int)((long)nonChaos * bypass / 100);
        int nonChaosAbsorbed = Math.Min(nonChaosEligible, absorbed);
        int physicalEligible = physical - (int)((long)physical * bypass / 100);
        int physicalAbsorbed = Math.Min(physicalEligible, nonChaosAbsorbed);
        if (!wasBroken && conn.Ward.Broken
            && conn.Antiquarian.GrantAdrenaline(now, Get(AntiquarianEffects.AdrenalineSource)))
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        return (physical - physicalAbsorbed, Math.Max(0, other - (nonChaosAbsorbed - physicalAbsorbed)),
            Math.Max(0, chaos - (absorbed - nonChaosAbsorbed)));
    }

}
