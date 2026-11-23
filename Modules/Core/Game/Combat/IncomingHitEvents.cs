public static partial class Game
{
    // Every accepted enemy hit records the same events, whether it came from a monster action or a testing tool.
    // Call in the area's frame after applying damage. Presentation must not create gameplay events a second time.
    static void RecordIncomingHitLocked(Player player, double now, bool spell, bool blocked, bool suppressed,
        long lifeDamage, bool critical = false, SpawnedMonster? by = null)
    {
        player.RecentEnemyHits.Record(now);
        player.RecentEvents.Record(StatConditions.BeenHit, now);
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
            QueueFlaskTrigger(player, FlaskEnchantments.Trigger.SavageHit);
        }
        ChargesWhenHit(player, blocked, spell, critical, by);
    }
}
