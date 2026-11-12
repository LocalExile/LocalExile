public static partial class Game
{
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
        (physical, other, chaos) = TriggeredGuardHit(conn, physical, other, chaos, at);
        RecordDamageTakenTriggers(conn, (int)Math.Min(int.MaxValue, (long)physical + other + chaos), at);
        (physical, other, chaos) = AbsorbPlayerWard(conn, physical, other, chaos, at, player);
        int bypass = Get(IncomingResourceDefences.BypassStat);
        int nonChaosLife = (int)Math.Min(int.MaxValue, (long)IncomingResourceDefences.Damage(conn.EnergyShield, physical, bypass, at)
            + IncomingResourceDefences.Damage(conn.EnergyShield, other, bypass, at));
        int chaosLife = conn.EnergyShield.Damage(chaos, true, at);
        int fromMana = PlayerDamageTaken.FromMana(nonChaosLife, chaosLife, types, (int)conn.PlayerMana, Get);
        conn.PlayerMana -= (uint)fromMana;
        if (ResourceEffectsCurrent(conn, state)) state.Mana.Hit(at,
            (int)Math.Min(int.MaxValue, (long)physical + other + chaos), Get("damage_taken_goes_to_mana_%"));
        // Petrified Blood sees the life loss after Ward, Energy Shield and Mana: it protects Life, not the other pools.
        return PetrifiedBlood.PreventHitLifeLoss(conn, (int)Math.Min(int.MaxValue, (long)nonChaosLife + chaosLife - fromMana), at);
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
