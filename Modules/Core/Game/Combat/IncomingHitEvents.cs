public static partial class Game
{
    // Every accepted enemy hit records the same events, whether it came from a monster action or a testing tool.
    // Call under Monsters after applying damage. Presentation must not create gameplay events a second time.
    static void RecordIncomingHitLocked(Player conn, double now, bool spell, bool blocked, bool suppressed,
        long lifeDamage)
    {
        conn.RecentEnemyHits.Record(now);
        conn.RecentEvents.Record(StatConditions.BeenHit, now);
        if (blocked)
        {
            conn.RecentEvents.Record(StatConditions.Blocked, now);
            conn.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now);
            QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Block);
        }
        if (suppressed) conn.RecentEvents.Record(StatConditions.SuppressedSpell, now);
        if (lifeDamage > 0 && lifeDamage * 100 >= (long)conn.PlayerMaxLife * 15)
        {
            conn.RecentEvents.Record(StatConditions.TakenSavageHit, now);
            QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.SavageHit);
        }
        ChargesWhenHit(conn, blocked, spell);
    }
}
