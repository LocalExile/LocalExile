using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static int EqualisingPercent(Player player, SpawnedMonster source) =>
        source.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
            ? Math.Clamp(MapModsOf(player)?.Get(Stat.MapRareUniqueMonstersRemovePctLifeManaEsOnHit) ?? 0, 0, 100)
            : 0;

    static uint HitResourceLoss(uint current, uint maximum, int percent) =>
        (uint)Math.Min(current, (long)maximum * percent / 100);

    static void ApplyEqualisingHitLocked(Player player, SpawnedMonster source)
    {
        int percent = EqualisingPercent(player, source);
        if (percent == 0 || player.PlayerLife == 0) return;
        player.PlayerLife -= HitResourceLoss(player.PlayerLife, player.PlayerMaxLife, percent);
        player.PlayerMana -= HitResourceLoss(player.PlayerMana, player.Pools.Mana, percent);
        player.EnergyShield.Spend(HitResourceLoss(player.EnergyShield.Current, player.EnergyShield.Maximum, percent));
    }

    static int NearbyAllyDamageTaken(Player player, (int X, int Y) here, double now)
    {
        int perAlly = MapModsOf(player)?.Get(Stat.MapDamageTakenPlusPctPerNearbyAlly) ?? 0;
        if (perAlly == 0) return 0;
        const int radius = 40; // Local nearby reach for the legacy map stat; the stat provides only the percentage.
        int allies = 0;
        if (player.Instance is { } instance)
            foreach (var ally in instance.PlayersExcept(player.Id))
                if (!ally.Closed && !ally.LeavingArea && ally.PlayerLife > 0
                    && ReferenceEquals(player.Area, ally.Area) && Dist(ally.Walk.Position(now), here) <= radius)
                    allies++;
        foreach (var (_, ally) in player.Area.MonstersNear(here, radius))
            if (ally is { Hostile: false, Dead: false, Life: > 0, OwnerId: not null, WorldHidden: false }
                && Dist(ally.Walk.Position(now), here) <= radius)
                allies++;
        return ActorScale.Saturate((long)allies * perAlly);
    }

    static void QueueMonsterFlaskChargeLossLocked(Player player, SpawnedMonster source)
    {
        int chance = source.Effects[Stat.MapMonstersRemoveEnemyFlaskChargeOnHitPctChance];
        if (chance <= 0 || !RandomRolls.Chance(chance, 100, player.IncomingHitRandom)) return;
        int choice = player.IncomingHitRandom.Next(int.MaxValue);
        var session = ProjectilesFor(player);
        session.Enqueue(() => ChangeFlaskBar(player, inventory =>
        {
            if (!session.Active || player.Closed || player.LeavingArea) return null;
            var catalogue = ItemsFor(player);
            var charged = inventory.Items(PlayerInventories.Flask).Where(item => item.Item.Charges > 0
                && catalogue.ByHash(item.Item.TypeHash) is { } type && catalogue.FlaskChargesOf(type) is not null)
                .OrderBy(item => item.X).ToArray();
            if (charged.Length == 0) return null;
            uint id = charged[choice % charged.Length].Id;
            var result = inventory.ChangeUnsaved(PlayerInventories.Flask, id, item => item with { Charges = Math.Max(0, item.Charges - 1) });
            return result is { } changed ? ([id], [changed]) : null;
        }, changed => $"0x00c6 monster hit removed a charge from flask {changed[0].X}"));
    }
}
