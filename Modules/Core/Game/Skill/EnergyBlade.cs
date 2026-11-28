using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;

public static partial class Game
{
    const string EnergyBladeEffect = "EnergyBlade";
    const string OneHandEnergyBlade = "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand";
    const string TwoHandEnergyBlade = "Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/StormBladeTwoHand";
    const string EnergyBladeImplicit = "StormBladeImplicitLocalLightningDamage_1";
    static ushort EnergyBladeBuffRow = 1942;

    sealed record EnergyBladeState(ushort Skill, GemSourceLifetimes.SourceLifetime Source,
        EnergyBladeHand Main, EnergyBladeHand? Off, ushort Implicit, int ShieldMore, int AttackSpeed,
        int MinimumPercent, int MaximumPercent, int MinimumFlat, int MaximumFlat, int TwoHandMore)
    {
        public uint PublishedShield;
        public volatile BladeEquipment? Equipment;
    }

    sealed record BladeEquipment(IReadOnlyList<(byte Row, InventoryItem Item)> Original, uint Shield,
        IReadOnlyList<(byte Row, InventoryItem Item)> Projected);

    sealed record BladeProjection(Item Original, uint Shield, InventoryItem Item)
    {
        public readonly IReadOnlyList<InventoryItem> Row = [Item];
    }

    sealed class EnergyBladeHand(InventoryItem original, ItemCatalog.BaseType type)
    {
        public ItemCatalog.BaseType Type => type;
        public bool Matches(InventoryItem item) => original.Id == item.Id && original.Item.TypeHash == item.Item.TypeHash;
        public volatile BladeProjection? Projection;
    }

    static EnergyBladeState? CurrentEnergyBlade(Player player) =>
        SelfBuffs.TryGetValue(player, out var set) ? set.EnergyBlade : null;

    static bool EnergyBladeSword(ItemCatalog.BaseType? type) =>
        type?.Class is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword";

    static bool CanUseEnergyBlade(Player player)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        if (items.Items(PlayerInventories.Weapon1) is not [var main, ..] || IsInactive(player, main.Id)
            || !EnergyBladeSword(catalog.ByHash(main.Item.TypeHash))) return false;
        return items.Items(PlayerInventories.Offhand1) is not [var off, ..]
            || catalog.ByHash(off.Item.TypeHash) is { } type
                && (type.Class == "Shield" || EnergyBladeSword(type) && !IsInactive(player, off.Id));
    }

    static bool ScheduleEnergyBlade(Player player, ushort skill, int effect, int castMs, int recovery,
        double? startedAt, Func<bool> current)
    {
        using var mutation = ProjectileMutation(player);
        if (!current() || !CanUseEnergyBlade(player)
            || CaptureGemSourceLifetimeLocked(player, skill, effect) is not { } source) return false;
        double release = (startedAt ?? AreaTime(player)) + castMs / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        ProjectilesFor(player).ScheduleContact(ProjectileRuntime.Time(release),
            () => current() && player.ActionInterruptionGeneration == recovery
                && GemSourceLifetimeCurrentLocked(player, skill, effect, source),
            () => ReleaseEnergyBlade(player, skill, source));
        return true;
    }

    static void ReleaseEnergyBlade(Player player, ushort skill, GemSourceLifetimes.SourceLifetime source)
    {
        if (!CanUseEnergyBlade(player)) return;
        if (ClearEnergyBlade(player))
        {
            EnergyBladeChanged(player);
            _ = PublishEnergyBlade(player);
            return;
        }
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        var gem = SupportedSkill(player, skill);
        if (catalog.Mods.ById(EnergyBladeImplicit) is not { } implicitMod) return;
        EnergyBladeHand? Hand(byte row)
        {
            if (items.Items(row) is not [var original, ..]) return null;
            var type = catalog.ByHash(original.Item.TypeHash);
            return EnergyBladeSword(type)
                && catalog.ById(type!.Class == "Two Hand Sword" ? TwoHandEnergyBlade : OneHandEnergyBlade) is { } blade
                ? new(original, blade) : null;
        }
        if (Hand(PlayerInventories.Weapon1) is not { } main) return;
        int Read(uint stat) => gem.Stats.GetValueOrDefault(stat);
        var state = new EnergyBladeState(skill, source, main, Hand(PlayerInventories.Offhand1), implicitMod.Hash,
            Read(Stat.StormBladeEnergyShieldPlusPctFinal), Read(Stat.StormBladeGlobalAttackSpeedPlusPct),
            Read(Stat.StormBladeMinimumLightningDamageFromEsPct), Read(Stat.StormBladeMaximumLightningDamageFromEsPct),
            Read(Stat.StormBladeMinimumLightningDamage), Read(Stat.StormBladeMaximumLightningDamage),
            Read(Stat.StormBladeDamagePlusPctFinalWithTwoHandWeapon));
        var set = SelfBuffs.GetOrCreateValue(player);
        set.EnergyBlade = state;
        MarkPlayerStats(player, PlayerStatStep.Sources);
        player.Area.Buffs.Set(player.Id, new(EnergyBladeBuffRow, player.Id, skill));
        EnergyBladeChanged(player);
        state.PublishedShield = player.Pools.EnergyShield;
        _ = PublishEnergyBlade(player);
        EveryFrameAsPlayer(player, () => ReferenceEquals(set.EnergyBlade, state), async _ =>
        {
            using (ProjectileMutation(player, advance: false))
            {
                if (!ReferenceEquals(set.EnergyBlade, state)) return;
                if (ReconcileEnergyBlade(player)) EnergyBladeChanged(player);
                state.PublishedShield = player.Pools.EnergyShield;
            }
            await PublishEnergyBlade(player);
        }, needsWork: () => state.PublishedShield != player.Pools.EnergyShield || !EnergyBladeCurrent(player, state));
    }

    static bool EnergyBladeCurrent(Player player, EnergyBladeState state)
    {
        var items = ItemsOf(player);
        return player.PlayerLife > 0 && player.GemSourceLifetimes.IsCurrent(state.Source)
            && items.Items(PlayerInventories.Weapon1) is [var main, ..] && state.Main.Matches(main) && !IsInactive(player, main.Id)
            && (state.Off is { } off
                ? items.Items(PlayerInventories.Offhand1) is [var actual, ..] && off.Matches(actual) && !IsInactive(player, actual.Id)
                : items.Items(PlayerInventories.Offhand1) is not [var other, ..]
                    || ItemsFor(player).ByHash(other.Item.TypeHash)?.Class == "Shield");
    }

    static bool ReconcileEnergyBlade(Player player) => CurrentEnergyBlade(player) is { } state
        && !EnergyBladeCurrent(player, state) && ClearEnergyBlade(player);

    static bool ClearEnergyBlade(Player player)
    {
        if (!SelfBuffs.TryGetValue(player, out var set) || set.EnergyBlade is null) return false;
        set.EnergyBlade = null;
        player.Area.Buffs.Clear(player.Id, EnergyBladeBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        return true;
    }

    static void EnergyBladeChanged(Player player)
    {
        player.PlayerSkillsCache = null;
        ActiveSkillSets.Remove(player);
        RecomputePoolsAndSpeed(player);
        player.Frames.Area?.Life.Resend(player);
    }

    static async Task PublishEnergyBlade(Player player)
    {
        await SendEnergyBladeInventories(player);
        await SendModelIfHandsChanged(player);
        await SendCharacterStats(player, PlayerStatChange.Build);
    }

    static Task SendEnergyBladeInventories(Player player)
    {
        var items = ItemsOf(player);
        return Send(player, [], "0x00bf Energy Blade equipment", atGate: () =>
        {
            if (player.Closed || player.LeavingArea) return null;
            var bytes = new List<byte>();
            foreach (byte row in new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1 })
            {
                if (items.Items(row) is not [var original, ..]) continue;
                bytes.AddRange(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row),
                    [original.Id], [EnergyBladeItem(player, row, original)]));
            }
            return bytes.ToArray();
        });
    }

    static EnergyBladeHand? TransformedHand(EnergyBladeState state, byte row) => row switch
    {
        PlayerInventories.Weapon1 => state.Main,
        PlayerInventories.Offhand1 => state.Off,
        _ => null,
    };

    static InventoryItem EnergyBladeItem(Player player, byte row, InventoryItem original) =>
        ProjectEnergyBlade(player, row, original)?.Item ?? original;

    static BladeProjection? ProjectEnergyBlade(Player player, byte row, InventoryItem original)
    {
        if (row is not (PlayerInventories.Weapon1 or PlayerInventories.Offhand1)
            || CurrentEnergyBlade(player) is not { } state || TransformedHand(state, row) is not { } hand
            || !hand.Matches(original)) return null;
        uint shield = player.Pools.EnergyShield;
        if (hand.Projection is { } cached && cached.Shield == shield && ReferenceEquals(cached.Original, original.Item)) return cached;
        long more = hand.Type.Class == "Two Hand Sword" ? 100L + state.TwoHandMore : 100L;
        int Damage(int percent, int flat) =>
            (int)Math.Clamp(((long)shield * percent / 100 + flat) * more / 100, 0, int.MaxValue);
        var blade = new Item(hand.Type.Hash, hand.Type.Components, hand.Type.VisualIdentity)
        {
            OriginalItem = original.Item,
            ItemLevel = original.Item.ItemLevel,
            Sockets = original.Item.Sockets,
            Links = original.Item.Links,
            Mods = [new(ModSection.Implicit, state.Implicit,
                [Damage(state.MinimumPercent, state.MinimumFlat), Damage(state.MaximumPercent, state.MaximumFlat)])],
        };
        return hand.Projection = new(original.Item, shield, original with { Item = blade });
    }

    static IReadOnlyList<(byte Row, InventoryItem Item)> EnergyBladeEquipment(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment)
    {
        if (CurrentEnergyBlade(player) is not { } state) return equipment;
        uint shield = player.Pools.EnergyShield;
        if (state.Equipment is { } cache && cache.Shield == shield && ReferenceEquals(cache.Original, equipment))
            return cache.Projected;
        // These are effective items for item stats and item-granted skills. The original inventory remains the gem host.
        var projected = new (byte Row, InventoryItem Item)[equipment.Count];
        for (int i = 0; i < equipment.Count; i++)
        {
            var (row, item) = equipment[i];
            projected[i] = (row, EnergyBladeItem(player, row, item));
        }
        state.Equipment = new(equipment, shield, projected);
        return projected;
    }

    static IReadOnlyList<InventoryItem> EnergyBladeRow(Player player, byte row, IReadOnlyList<InventoryItem> items)
    {
        if (items is not [var original, ..]) return items;
        return ProjectEnergyBlade(player, row, original)?.Row ?? items;
    }
}
