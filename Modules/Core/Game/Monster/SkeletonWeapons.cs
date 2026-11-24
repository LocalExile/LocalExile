using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed record SkeletonWeaponSlot(Item? MainHand, Item? Equipped, SkillCatalog.Unarmed? Weapon, Dictionary<uint, int> Gear);
    sealed class SkeletonWeaponHit { public double At = double.NegativeInfinity; }
    static readonly RuleTable<Player, SkeletonWeaponSlot> SkeletonWeaponSlots = new();
    static readonly RuleTable<Player, System.Runtime.CompilerServices.ConditionalWeakTable<Item, SkeletonWeaponHit>> SkeletonWeaponHits = new();

    static void CopySkeletonWeapon(Player player, GolemState.Golem fighter, MonsterCatalog.Variety variety)
    {
        bool warrior = fighter.Definition.Id == "SummonSkeletons"
            || fighter.Definition.Kind == "VaalSkeleton" && variety.Id.Contains("Melee", StringComparison.Ordinal);
        if (!warrior) return;
        var hand = ActiveMainHand(player);
        var item = hand?.Item;
        if (!SkeletonWeaponSlots.TryGetValue(player, out var slot) || !ReferenceEquals(slot.MainHand, item))
        {
            var catalog = ItemsFor(player);
            bool qualifies = item is not null
                && ItemModStats.Local(item, catalog.Mods).GetValueOrDefault(Stat.SummonedSkeletonWarriorsGetWeaponStatsInMainHand) != 0;
            var weapon = qualifies ? WeaponIn(player, hand) : null;
            slot = weapon is not null && item is not null
                ? new(item, item, weapon, ItemModStats.Sum([item], catalog.Mods))
                : new(item, null, null, []);
            SkeletonWeaponSlots.AddOrUpdate(player, slot);
        }
        fighter.SkeletonWeapon = slot.Equipped;
        fighter.Weapon = slot.Weapon;
        fighter.Gear = slot.Gear;
    }

    static Item? SkeletonWeaponContactSource(Player player, AttackHand hand, IReadOnlyDictionary<uint, int>? local) =>
        (local?.GetValueOrDefault(Stat.LocalGrantSkeletonWarriorsTripleDamageOnHit) > 0 || local?.GetValueOrDefault(Stat.LocalUseSkillOnCritPct) > 0)
            ? (hand == AttackHand.Off ? ActiveOffHand(player) : ActiveMainHand(player))?.Item : null;

    static void RecordWeaponContactLocked(Player player, double now, Item? source, Item? second = null)
    {
        player.RecentEvents.Record(StatConditions.HitEnemyWithWeapon, now);
        if (source is not null) SkeletonWeaponHits.GetOrCreateValue(player).GetValue(source, static _ => new()).At = now;
        if (second is not null) SkeletonWeaponHits.GetOrCreateValue(player).GetValue(second, static _ => new()).At = now;
    }

    static bool SkeletonWeaponTripleDamage(Player player, GolemState.Golem fighter, GolemState.Ability attack, double now) =>
        !attack.Attack.Spell && !attack.Attack.Secondary && fighter.SkeletonWeapon is { } weapon
        && SkeletonWeaponHits.TryGetValue(player, out var history) && history.TryGetValue(weapon, out var contact)
        && now >= contact.At && now < contact.At + 4;
}
