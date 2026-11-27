using GameServer.Core.World;

public static partial class Game
{
    /// <summary>Damage a character returns to the monster that hit it: what the effect rules' "damage" action deals
    /// ("Reflects # Damage to Melee Attackers", "... to Attackers on Block", "#% of Melee Physical Damage taken
    /// reflected to Attacker"). The monster mitigates it with its own resistances and armour; nothing of the
    /// character's scales it, and it is not a hit of the character's (no ailments, no leech, no on-hit effects).</summary>
    internal static void DamageAttackerLocked(Player player, uint id, SpawnedMonster m, DamagePacket packet, double now)
    {
        if (m.Dead || m.Life == 0 || !m.Hostile || m.Invulnerable || packet.MinTotal <= 0) return;
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
