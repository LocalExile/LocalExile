using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    sealed record AnimatedGuardianGear(Dictionary<byte, Item> Reference, Dictionary<byte, Item> Equipment);

    sealed record AnimatedSource(uint Id, WorldItemObject? Ground, Dictionary<byte, Item> Equipment,
        AnimatedGuardianGear? GuardianGear = null, BladefallTrarthusState? BladeSource = null);

    static string AnimatedGuardianDeathKey(Player player) =>
        "Game.AnimatedGuardianDeath:" + CharacterName(player).ToUpperInvariant();

    static void RecordAnimatedGuardianDeath(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null)
            player.Instance?.ContentState.TryAdd(AnimatedGuardianDeathKey(player), "1");
    }

    static bool AnimatedGuardianUnavailable(Player player) =>
        HideoutCatalog.ByArea(player.Layout.Hash) is null
        && player.Instance?.ContentState.ContainsKey(AnimatedGuardianDeathKey(player)) == true;

    static AnimatedGuardianGear? CaptureAnimatedGuardianGearLocked(Player player, SummonCatalog.Definition definition) =>
        definition.Family == "AnimatedGuardian"
            ? new(player.Summons.GuardianEquipment, new(player.Summons.GuardianEquipment))
            : null;

    static bool AnimatedGuardianGearCurrentLocked(Player player, AnimatedGuardianGear gear)
    {
        var current = player.Summons.GuardianEquipment;
        return ReferenceEquals(current, gear.Reference) && current.Count == gear.Equipment.Count
            && current.All(pair => gear.Equipment.TryGetValue(pair.Key, out var item) && ReferenceEquals(item, pair.Value));
    }

    static AnimatedSource? AnimatedItems(Player player, SummonCatalog.Definition definition, SupportedGem gem,
        (int X, int Y) aim, uint target, Dictionary<byte, Item>? restored, double radius = 15)
    {
        var guardian = CaptureAnimatedGuardianGearLocked(player, definition);
        if (restored is not null) return new(0, null, new(restored), guardian);
        if (definition.Id == "AnimateWeaponAltX")
        {
            if (ActiveMainHand(player) is not { Type: { } type } hand
                || WeaponIn(player, hand) is not { Melee: true }
                || ItemModStats.LevelRequirement(hand.Item, type.DropLevel, ItemsFor(player).Mods)
                    > GolemStat(gem.Stats, Stat.AnimateItemMaximumLevelRequirement)) return null;
            return new(0, null, new() { [2] = hand.Item }, guardian);
        }
        if (definition.Family == "Clone")
        {
            if (ActiveMainHand(player) is not { Type.Class: "Bow" } bow) return null;
            var equipment = ItemsOf(player).EquipmentSnapshot().ToDictionary(p => p.Row, p => p.Item.Item);
            return new(0, null, equipment);
        }
        byte Slot(Item item) => AnimatedItemSlot(player, definition, gem, item);
        bool Visible(uint id)
        {
            var origin = player.Instance.Ground.Origin(id);
            return origin.Shared || ReferenceEquals(origin.By, player);
        }
        if (target != 0 && !Visible(target)) return null;
        var ground = target != 0 ? player.Instance.Ground.Get(target) : null;
        uint id = target;
        if (ground is null)
        {
            var found = player.Instance.Ground.Within(aim.X, aim.Y, radius, p => Slot(p.Item) != 0)
                .Where(p => Visible(p.Id)).OrderBy(p => Dist((p.Item.GridX, p.Item.GridY), aim)).FirstOrDefault();
            id = found.Id; ground = found.Item;
        }
        var gear = guardian is null ? [] : new Dictionary<byte, Item>(guardian.Equipment);
        if (ground is null) return gear.Count > 0 ? new(0, null, gear, guardian) : null;
        byte slot = Slot(ground.Item);
        if (slot == 0) return null;
        gear[slot] = ground.Item;
        return new(id, ground, gear, guardian);
    }

    public static byte AnimatedItemSlot(Player player, SummonCatalog.Definition definition, SupportedGem gem, Item item)
    {
        var catalog = ItemsFor(player);
        if (!item.Identified || catalog.ByHash(item.TypeHash) is not { } type
            || ItemModStats.LevelRequirement(item, type.DropLevel, catalog.Mods)
                > GolemStat(gem.Stats, Stat.AnimateItemMaximumLevelRequirement)) return 0;
        if (WeaponIn(player, (item, type)) is { } weapon)
        {
            if (definition.Family == "AnimatedGuardian") return weapon.Melee ? (byte)2 : (byte)0;
            if (definition.Family is not ("AnimatedWeapon" or "VaalAnimatedWeapon") || item.Sockets.Count >= 6) return 0;
            bool allowed = definition.Id == "AnimateWeaponAltY" ? !weapon.Melee : weapon.Melee;
            return allowed ? (byte)2 : (byte)0;
        }
        if (definition.Family != "AnimatedGuardian") return 0;
        return type.Class switch { "Body Armour" => 1, "Shield" => 3, "Helmet" => 4, "Gloves" => 8, "Boots" => 9, _ => (byte)0 };
    }

    static IReadOnlyList<VisibleItem> SummonLooks(GolemState.Golem fighter) => fighter.Equipment
        .Where(p => SummonVisibleSlots.ContainsKey(p.Key)).Select(p => (Slot: SummonVisibleSlots[p.Key], Look: LookOf(p.Value)))
        .Where(p => p.Look is not null).Select(p => p.Look! with { Slot = p.Slot }).ToArray();
    static readonly IReadOnlyDictionary<byte, byte> SummonVisibleSlots = new Dictionary<byte, byte>
        { [1] = 2, [2] = 0, [3] = 1, [4] = 4, [5] = 11, [6] = 8, [7] = 9, [8] = 5, [9] = 6, [10] = 10 };

    static void AnimatedGuardianSmiteLocked(Player player, GolemState.Golem guardian, GolemState.Action action, double now)
    {
        var ability = action.Ability;
        var origin = guardian.Monster.Walk.Position(now);
        int range = GolemStat(ability.Stats, Stat.SmiteLightningTargetRange);
        int count = GolemStat(ability.Stats, Stat.BaseSmiteNumberOfTargets);
        var enemies = player.Area.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0,
                Invulnerable: false, Asleep: false } && !MonsterAsleep(player, p.Key))
            .Select(p => (Id: p.Key, Monster: p.Value, At: p.Value.Walk.Position(now))).ToArray();
        var centres = enemies.Where(e => Dist(origin, e.At) <= range + LivingCollision.Size(e.Monster))
            .OrderByDescending(e => e.Id == action.Target).ThenBy(e => Dist(e.At, action.Aim)).ThenBy(e => e.Id)
            .Take(count).Select(e => e.At).ToArray();
        foreach (var enemy in enemies)
            if (centres.Any(centre => Dist(centre, enemy.At) <= ability.Radius + LivingCollision.Size(enemy.Monster)))
                GolemHitLocked(player, guardian, ability, enemy.Id, enemy.Monster, now);
    }

    static async Task<bool> AnimateGroundItem(Player player,
        WorldItemObject ground, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == use.SkillId);
        if (skill.Name is null || SummonData.Find(SkillsFor(player).EffectId(skill.Effect)) is not { Family: "AnimatedWeapon" or "VaalAnimatedWeapon" or "AnimatedGuardian" }) return false;
        var here = player.Walk.Position(AreaTime(player)); var aim = (ground.GridX, ground.GridY);
        if (!TryBeginSkillCooldownUse(player, use.SkillId, skill.Effect, CooldownForSkill(player, use.SkillId), false,
            () => { Interlocked.Increment(ref player.ActionCount); player.Walk.Order(here.X, here.Y, AreaTime(player)); }, out var accepted, requestedAt: requestedAt)) return true;
        bool acknowledged = false;
        await SendWorldRecords(player, player.Id, () =>
            [InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, aim.GridX, aim.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                InstanceProtocol.ActionEnded(player.Id)], "animate ground equipment",
            still: () => acknowledged = SkillAcknowledgementAllowed(player, skill.Effect, use.SkillId, () => accepted?.CommitNativeUse(player, use.SkillId) ?? true));
        if (acknowledged)
        {
            await SendNativeCooldownRefresh(player);
            _ = CastGemSkill(player, use.SkillId, here, aim, use.TargetId,
                sequence: use.Sequence, actionToken: accepted?.ActionToken, cooldownUse: accepted);
        }
        return true;
    }
}
