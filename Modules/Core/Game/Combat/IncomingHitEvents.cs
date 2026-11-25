public static partial class Game
{
    const string TakenProjectileDamage = "have_taken_projectile_damage_recently";
    const string EnemyAttackHit = "have_been_hit_by_an_attack_recently";

    static double WindDancerHitFactor(Player player, bool attack, double now, IReadOnlyDictionary<uint, int> stats)
        => !attack || stats.GetValueOrDefault(Stat.KeystoneWindDancer) == 0 ? 1
            : player.RecentEvents.Happened(EnemyAttackHit, now) ? 1.2 : .8;

    // Every accepted enemy hit records the same events, whether it came from a monster action or a testing tool.
    // Call in the area's frame after applying damage. Presentation must not create gameplay events a second time.
    static void RecordIncomingHitLocked(Player player, double now, bool spell, bool blocked, bool suppressed,
        long lifeDamage, bool damaged, bool critical = false, bool enemy = true, bool projectile = false, bool attack = false)
    {
        if (enemy)
        {
            player.RecentEnemyHits.Record(now);
            if (attack) player.RecentEvents.Record(EnemyAttackHit, now);
            if (damaged)
            {
                player.RecentEvents.Record(DamagedByHitEvent, now);
                if (projectile) player.RecentEvents.Record(TakenProjectileDamage, now);
            }
        }
        player.RecentEvents.Record(StatConditions.BeenHit, now);
        if (critical) player.LastCritTakenAt = now;
        if (blocked)
        {
            player.RecentEvents.Record(StatConditions.Blocked, now);
            player.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now);
            QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Block);
        }
        if (suppressed) player.RecentEvents.Record(StatConditions.SuppressedSpell, now);
        if (lifeDamage > 0 && lifeDamage * 100 >= (long)player.PlayerMaxLife * 15)
        {
            player.RecentEvents.Record(StatConditions.TakenSavageHit, now);
            player.RecentEvents.Record(VengefulCryOpportunity, now);
            QueueFlaskTrigger(player, FlaskEnchantments.Trigger.SavageHit);
        }
        ChargesWhenHit(player, blocked, spell, critical);
    }
}
