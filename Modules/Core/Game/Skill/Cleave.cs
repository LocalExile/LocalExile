using GameServer.Core.World;

public static partial class Game
{
    internal const int CleaveBaseRadius = 20;
    const int CleaveNearbyRadius = 20;
    const int CleaveNearbyBonusLimit = 10;

    internal static bool CleaveAttack(string? effect) =>
        effect is "Cleave" or "CleaveAltX" or "CleaveAltY" or "CleaveRoyale" or "VaalCleave";

    static bool CleaveHasAxeAndSword(Player player)
    {
        if (ActiveMainHand(player) is not { Type: { } mainType } main
            || ActiveOffHand(player) is not { Type: { } offType } off
            || !WeaponIdentity.OneHandMelee(mainType.Class) || !WeaponIdentity.OneHandMelee(offType.Class))
            return false;

        var mods = ItemsFor(player).Mods;
        bool mainAll = WeaponIdentity.AllOneHandMelee(main.Item, mods);
        bool offAll = WeaponIdentity.AllOneHandMelee(off.Item, mods);
        string? mainFamily = WeaponTypeDamage.FamilyOf(mainType.Class);
        string? offFamily = WeaponTypeDamage.FamilyOf(offType.Class);
        return (mainAll || mainFamily == "axe") && (offAll || offFamily == "sword")
            || (mainAll || mainFamily == "sword") && (offAll || offFamily == "axe");
    }

    // The Cleave action scans its forward semicircle and its separate nearby-enemy radius from the actor.
    // A body's size extends the radial test, while a centre behind the player still misses.
    internal readonly record struct CleaveArc((int X, int Y) Origin, (double X, double Y) Direction, int Radius)
    {
        internal bool Covers((int X, int Y) target, double distance, int body) =>
            Radius > 0 && distance <= Radius + Math.Max(0, body)
            && (target.X - Origin.X) * Direction.X + (target.Y - Origin.Y) * Direction.Y >= 0;
    }

    static bool MeleeArcSight(Player player, (int X, int Y) origin, (int X, int Y) target) =>
        player.Layout?.Sight is not { } sight || !ClientPathfinder.LineBelow(target, origin, sight, 1);

    static int CleaveRadius(Player player, int effect, IReadOnlyDictionary<uint, int> own,
        (int X, int Y) origin, double now)
    {
        var merged = SkillAreaStats(player, own, effect);
        int perEnemy = merged.GetValueOrDefault(Stat.CleavePlus1BaseRadiusPerNearbyEnemyUpTo10);
        int nearby = 0;
        if (perEnemy > 0)
        {
            foreach (var (_, monster) in player.Area.MonstersNear(origin, CleaveNearbyRadius))
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }) continue;
                var position = monster.Walk.Position(now);
                if (Dist(origin, position) > CleaveNearbyRadius + Math.Max(0, LivingCollision.Size(monster))
                    || !MeleeArcSight(player, origin, position)) continue;
                nearby = (int)Math.Min(CleaveNearbyBonusLimit, (long)nearby + perEnemy);
                if (nearby == CleaveNearbyBonusLimit) break;
            }
        }

        int divisor = merged.GetValueOrDefault(Stat.ChainStrikeConeRadiusPlusPerXRage);
        int rageBonus = divisor > 0 ? Math.Max(0, merged.GetValueOrDefault(Stat.CurrentRage)) / divisor : 0;
        return SkillArea.Radius(CleaveBaseRadius + nearby + rageBonus, merged.GetValueOrDefault, spell: false);
    }
}
