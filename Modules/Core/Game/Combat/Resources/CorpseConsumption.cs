using GameServer.Core.World;

/// <summary>Killing-blow corpse consumption: the Kitava Heart Eater tattoo's "Killing Blows have X% chance to Consume
/// Corpses to recover 10% of Maximum Life" (the roll and the life share) and Devour Support's corpse (whose recovery
/// happens when the hit lands). Consuming the corpse also takes it from summoning, explosions and warcries
/// (SpawnedMonster.CorpseConsumed), and records the consumed-corpse recently event the conditional items read.</summary>
static partial class Game
{
    internal const uint CorpseKillChance = Stat.KillingBlowConsumesCorpseRestore10PctLifeChancePct;
    internal const int CorpseKillLifePct = 10;

    /// <summary>A corpse the player consumed (a warcry, Devour, a raise or offering): an ignited one counts for
    /// Cinderswallow Urn's "Recharges X Charges when you Consume an Ignited corpse", which the flask-charge loop pays
    /// (RunFlaskChargeRegen). Only ignites still present at consumption count. In the area's frame.</summary>
    internal static void NoteCorpseConsumedLocked(Player player, SpawnedMonster corpse)
    {
        if (corpse.Ignites.IgnitedAt(ProjectileWorldTime(player))) { Interlocked.Increment(ref player.IgnitedCorpsesConsumed); PresentStats(player); }
    }

    /// <summary>The killing blow's corpse consumption: true when the corpse was consumed (the caller removes the corpse
    /// object). A corpse the engine cannot use (Warcries.IsCorpse: a shattered, already consumed or corpse-unusable
    /// monster) is not consumed, and the roll happens before the corpse is taken. Call in the area's frame: the
    /// corpse leaves conn.Monsters and the life change uses that lock.</summary>
    internal static bool ConsumeCorpseOnKillLocked(Player player, uint id, SpawnedMonster m, double now)
    {
        if (player.PlayerLife == 0 || m.Ailments.Shattered || !IsCorpse(m)) return false;
        var playerStats = CharacterPlayerStats(player);
        int chance = CorpseKillChance is var key ? playerStats.GetValueOrDefault(key) : 0;
        if (!RandomRolls.Chance(chance, 100, AreaRng(player))) return false;
        m.CorpseConsumed = true;
        NoteCorpseConsumedLocked(player, m);
        player.Area.Monsters.Remove(id);
        player.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
        if (LifeRecovery.NonLeechBlocked(key => playerStats.GetValueOrDefault(key))) return true;
        var session = LeechOf(player);
        uint before = player.PlayerLife;
        player.PlayerLife = (uint)Math.Min(player.PlayerMaxLife,
            (long)player.PlayerLife + player.PlayerMaxLife * (long)CorpseKillLifePct / 100);
        session.InstantLifeChanged |= before != player.PlayerLife;
        return true;
    }

    /// <summary>The Devour-supported killing blow's corpse: true when it was consumed (the caller removes the corpse
    /// object). The support's life/mana recovery already happened when the hit landed; this takes the corpse from
    /// summoning, explosions and warcries and records the consumed-corpse recently event. A shattered or already
    /// consumed corpse is not consumed again. Call in the area's frame.</summary>
    internal static bool ConsumeDevouredCorpseLocked(Player player, uint id, SpawnedMonster m, double now)
    {
        if (!m.DevourCorpse) return false;
        m.DevourCorpse = false;
        if (m.Ailments.Shattered || !IsCorpse(m)) return false;
        m.CorpseConsumed = true;
        NoteCorpseConsumedLocked(player, m);
        player.Area.Monsters.Remove(id);
        player.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
        return true;
    }
}
