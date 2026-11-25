using GameServer.Core.World;

public static partial class Game
{
    // Damage a character returns to what hit it: "Reflects # (to #) <type> Damage to Melee Attackers", "Reflects #
    // <type> Damage to Attackers on Block", and "#% of Melee Physical Damage taken reflected to Attacker" (Bramblejack,
    // Wall of Brambles; a map's players can have it too). The returned damage is the monster's to mitigate with its own
    // resistances and armour; nothing of the character's scales it, and it is not a hit of the character's (no
    // ailments, no leech, no on-hit effects).
    static readonly (DamageType Type, uint Flat, uint Minimum, uint Maximum)[] ReturnToMeleeAttacker =
    [
        (DamageType.Physical, Stat.PhysicalDamageToReturnToMeleeAttacker, Stat.MinimumPhysicalDamageToReturnToMeleeAttacker, Stat.MaximumPhysicalDamageToReturnToMeleeAttacker),
        (DamageType.Fire, Stat.FireDamageToReturnToMeleeAttacker, Stat.MinimumFireDamageToReturnToMeleeAttacker, Stat.MaximumFireDamageToReturnToMeleeAttacker),
        (DamageType.Cold, Stat.ColdDamageToReturnToMeleeAttacker, Stat.MinimumColdDamageToReturnToMeleeAttacker, Stat.MaximumColdDamageToReturnToMeleeAttacker),
        (DamageType.Lightning, Stat.LightningDamageToReturnToMeleeAttacker, Stat.MinimumLightningDamageToReturnToMeleeAttacker, Stat.MaximumLightningDamageToReturnToMeleeAttacker),
        (DamageType.Chaos, Stat.ChaosDamageToReturnToMeleeAttacker, Stat.MinimumChaosDamageToReturnToMeleeAttacker, Stat.MaximumChaosDamageToReturnToMeleeAttacker),
    ];

    static readonly (DamageType Type, uint Flat, uint Minimum, uint Maximum)[] ReturnOnBlock =
    [
        (DamageType.Physical, 0, Stat.MinimumPhysicalDamageToReturnOnBlock, Stat.MaximumPhysicalDamageToReturnOnBlock),
        (DamageType.Lightning, 0, Stat.MinimumLightningDamageToReturnOnBlock, Stat.MaximumLightningDamageToReturnOnBlock),
        (DamageType.Fire, Stat.FireDamageToReturnOnBlock, 0, 0),
    ];

    /// <summary>After a monster's hit on the player was taken or blocked: what the player's lines return to it.
    /// <paramref name="melee"/>: an attack that is not a projectile. <paramref name="physicalTaken"/>: the physical
    /// damage the hit dealt after mitigation (0 for a blocked hit that dealt none).</summary>
    static void ReflectToAttackerLocked(Player player, uint id, SpawnedMonster m, IReadOnlyDictionary<uint, int> stats,
        bool melee, bool attack, bool blocked, int physicalTaken, double now)
    {
        if (m.Dead || m.Life == 0 || !m.Hostile || m.Invulnerable) return;
        var packet = DamagePacket.Empty;
        var random = AreaRng(player);
        void Add((DamageType Type, uint Flat, uint Minimum, uint Maximum) row)
        {
            int flat = row.Flat != 0 ? stats.GetValueOrDefault(row.Flat) : 0;
            int low = row.Minimum != 0 ? stats.GetValueOrDefault(row.Minimum) : 0, high = row.Maximum != 0 ? stats.GetValueOrDefault(row.Maximum) : 0;
            double amount = flat + (high > 0 ? low + random.NextDouble() * Math.Max(0, high - low) : 0);
            if (amount > 0) packet = packet.Add(DamagePacket.Of(row.Type, amount, amount));
        }
        if (melee)
        {
            foreach (var row in ReturnToMeleeAttacker) Add(row);
            long share = (long)stats.GetValueOrDefault(Stat.MeleePhysicalDamageTakenPctToDealToAttacker)
                + (MapModsOf(player)?.Get(Stat.MapMeleePhysicalDamageTakenPctToDealToAttacker) ?? 0);
            if (share > 0 && physicalTaken > 0)
            {
                double returned = physicalTaken * (double)share / 100;
                packet = packet.Add(DamagePacket.Of(DamageType.Physical, returned, returned));
            }
        }
        if (blocked && attack)
            foreach (var row in ReturnOnBlock) Add(row);
        if (packet.MinTotal <= 0) return;
        int damage = MonsterTakesRolled(m, packet, spell: false, now: now);
        var at = player.Walk.Position(now);
        if (damage <= 0 || ApplyTypedMonsterHitLocked(player, m, damage, (int)Math.Min(damage, packet[DamageType.Chaos].Min), allyAt: at) is not { } hit) return;
        if (hit.StageStarts)
        {
            CommitBossStage(player, id, m, now);
            hit = hit with { StageStarts = false };
        }
        CommitMinionHitLocked(player, id, m, hit, damage, now, "damage returned to the attacker");
    }
}
