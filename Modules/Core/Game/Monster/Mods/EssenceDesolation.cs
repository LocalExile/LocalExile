using GameServer.Core;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Desolation's Flesh Boulder (<c>GTFaridunEssenceDaemonBoulder</c> → <c>GSFaridunEssenceDaemonBoulderImpact</c>):
/// drops a flesh boulder (<c>MiscAnimated</c> 7957 <c>fleshboulderEssence.ao</c>) on the target that explodes after 1 s
/// (<c>MiscAnimated</c> 7958 <c>fleshboulder_explode.ao</c>) for physical spell damage with 100% chance to poison.</summary>
public static partial class MapEssences
{
    const string DesolationBoulderArt = "Metadata/Effects/Spells/monsters_effects/League_Faridun/FaridunEssence/fleshboulderEssence.ao";
    const string DesolationBoulderExplodeArt = "Metadata/Effects/Spells/monsters_effects/League_Faridun/FleshHulk/fleshboulder_explode.ao";

    static async Task CastDesolationBoulder(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
    {
        var catalog = SkillsFor(conn);
        int impactEffect = catalog.EffectRow("GSFaridunEssenceDaemonBoulderImpact");
        var damage = EssenceSkillDamage(conn, impactEffect, monster.Level);
        if (damage.IsZero) return;
        var spot = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
        uint meteor = SpawnEffect(conn, spot, DesolationBoulderArt);
        await Task.Delay(TimeSpan.FromSeconds(1.0));
        RemoveEffect(conn, meteor);
        if (conn.Closed || conn.LeavingArea) return;
        uint boom = SpawnEffect(conn, spot, DesolationBoulderExplodeArt);
        _ = RemovePulseLater(conn, boom, 1.0);
        await EssenceSpellHit(conn, monster, impactEffect, damage, radius: 14, spot, poisonChance: 100);
        Console.WriteLine($"[map-content] essence desolation boulder by 0x{monster.ObjectId:x} at ({spot.X}, {spot.Y})");
    }
}
