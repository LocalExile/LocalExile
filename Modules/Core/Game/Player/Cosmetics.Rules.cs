using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    const string CosmeticDisplayFlagsKey = "cosmetics.displayFlags";
    const string ExtraCosmeticsKey = "cosmetics.extraSlots";
    const string MannequinsKey = "cosmetics.mannequins.v1";

    public sealed record SavedExtraCosmetic(uint Key, uint Record, byte Option = 0, uint? Hash = null);

    public sealed record SavedMannequin(Dictionary<byte, CharacterSaves.SavedCosmetic>? Worn = null,
        List<SavedExtraCosmetic>? Extras = null, byte DisplayFlags = InstanceMicrotransactions.DefaultFlags,
        uint MainWeaponClass = 0, uint OffWeaponClass = 0);

    static string MannequinStorageKey(Player player, uint targetId) =>
        player.HideoutEditor.TryGet(targetId, out var entry)
            ? $"{player.Layout.Id}:{entry.Key}"
            : $"{player.Layout.Id}:obj:{targetId}";

    static Dictionary<string, SavedMannequin> ParseMannequinMap(string? json)
    {
        if (json is not { Length: > 0 }) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, SavedMannequin>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    static Dictionary<string, SavedMannequin> ReadMannequinMap(Player player)
    {
        string? account = player.Character.Account;
        string? json = account is not null
            ? AccountStash.For(account, ItemsFor(player)).ModuleData(MannequinsKey)
            : CharacterSaves.ModuleData(CharacterName(player), MannequinsKey);
        return ParseMannequinMap(json);
    }

    public static SavedMannequin LoadMannequin(Player player, uint targetId) =>
        ReadMannequinMap(player).GetValueOrDefault(MannequinStorageKey(player, targetId)) ?? new SavedMannequin();

    public static SavedMannequin UpdateMannequin(Player player, uint targetId, Func<SavedMannequin, SavedMannequin> change)
    {
        string key = MannequinStorageKey(player, targetId);
        SavedMannequin updated = new();
        string? account = player.Character.Account;
        string Transform(string? text)
        {
            var map = ParseMannequinMap(text);
            updated = change(map.GetValueOrDefault(key) ?? new SavedMannequin());
            map[key] = updated;
            return JsonSerializer.Serialize(map);
        }
        if (account is not null) AccountStash.For(account, ItemsFor(player)).ChangeModuleData(MannequinsKey, Transform);
        else CharacterSaves.ChangeModuleData(CharacterName(player), MannequinsKey, Transform);
        return updated;
    }

    public static (List<InstanceMicrotransactions.Equipped> Worn, List<InstanceMicrotransactions.ExtraSlot> Extras)
        ResolveMannequinSlots(SavedMannequin mannequin)
    {
        var worn = mannequin.Worn is { Count: > 0 } ? Mtx.Equipped(mannequin.Worn) : [];
        var extras = new List<InstanceMicrotransactions.ExtraSlot>();
        foreach (var saved in mannequin.Extras ?? [])
            if (Mtx.Resolve(new CharacterSaves.SavedCosmetic(saved.Record, saved.Option, saved.Hash)) is { } record)
                extras.Add(new InstanceMicrotransactions.ExtraSlot(saved.Key, record.Id, record.MtxTypeHash, record.Qword, saved.Option));
        return (worn, extras);
    }

    public static byte CosmeticDisplayFlags(Player player) =>
        byte.TryParse(CharacterSaves.ModuleData(CharacterName(player), CosmeticDisplayFlagsKey), out byte flags)
            ? (byte)(flags | InstanceMicrotransactions.DefaultFlags)
            : InstanceMicrotransactions.DefaultFlags;

    public static byte SetCosmeticDisplayFlag(Player player, byte bitMask, bool enabled)
    {
        byte updated = InstanceMicrotransactions.DefaultFlags;
        CharacterSaves.ChangeModuleData(CharacterName(player), CosmeticDisplayFlagsKey, text =>
        {
            byte current = byte.TryParse(text, out byte f) ? f : InstanceMicrotransactions.DefaultFlags;
            updated = enabled ? (byte)(current | bitMask) : (byte)(current & ~bitMask);
            updated |= InstanceMicrotransactions.DefaultFlags;
            return updated.ToString();
        });
        return updated;
    }

    static List<SavedExtraCosmetic> ParseExtraCosmetics(string? json)
    {
        if (json is not { Length: > 0 }) return [];
        try { return JsonSerializer.Deserialize<List<SavedExtraCosmetic>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static List<SavedExtraCosmetic> SavedExtraCosmetics(Player player) =>
        ParseExtraCosmetics(CharacterSaves.ModuleData(CharacterName(player), ExtraCosmeticsKey));

    public static List<InstanceMicrotransactions.ExtraSlot> ExtraCosmetics(Player player)
    {
        var list = new List<InstanceMicrotransactions.ExtraSlot>();
        foreach (var saved in SavedExtraCosmetics(player))
            if (Mtx.Resolve(new CharacterSaves.SavedCosmetic(saved.Record, saved.Option, saved.Hash)) is { } record)
                list.Add(new InstanceMicrotransactions.ExtraSlot(saved.Key, record.Id, record.MtxTypeHash, record.Qword, saved.Option));
        return list;
    }

    public static T UpdateExtraCosmetics<T>(Player player, Func<List<SavedExtraCosmetic>, T> change)
    {
        T result = default!;
        CharacterSaves.ChangeModuleData(CharacterName(player), ExtraCosmeticsKey, text =>
        {
            var list = ParseExtraCosmetics(text);
            result = change(list);
            return JsonSerializer.Serialize(list);
        });
        return result;
    }

    // The Cosmetics tab (K) wears what S→C 0x1f3 lists, all slots at once (23-microtransactions.md). Saved picks first; a
    // portal effect set with /cosmetic and no Portal slot saved shows in that slot too.
    // Read from the save once per connection (the stats ask for it on every hit); a pick clears it.
    public static List<InstanceMicrotransactions.Equipped> WornCosmetics(Player player)
    {
        if (player.Character.WornCosmetics is { } cached) return cached;
        var worn = new Dictionary<byte, CharacterSaves.SavedCosmetic>(CharacterSaves.Cosmetics(CharacterName(player)));
        if (!worn.ContainsKey(InstanceMicrotransactions.PortalSlot) && player.Character.CosmeticPortalVariation != 0
            && Cosmetics.Portals.FirstOrDefault(p => p.Index == player.Character.CosmeticPortalVariation) is { Hash: not 0 } portal
            && Mtx.ByHash(portal.Hash) is { } record)
            worn[InstanceMicrotransactions.PortalSlot] = new CharacterSaves.SavedCosmetic(record.Id, Hash: record.MtxTypeHash);
        return player.Character.WornCosmetics = Mtx.Equipped(worn);
    }

    public static async Task SendWornCosmetics(Player player, string why)
    {
        await Send(player, [], null, atGate: () =>
        {
            var worn = WornCosmetics(player);
            var skills = SkillSlots(player);
            var extras = ExtraCosmetics(player);
            byte flags = CosmeticDisplayFlags(player);
            Console.WriteLine($"[gateway] {player.Connection.Remote} => 0x01f3 worn cosmetics ({why}): " +
                $"{string.Join(", ", worn.Select(w => $"slot {w.Slot} = record {w.Id}"))}; " +
                $"skills: {string.Join(", ", skills.Select(s => $"key {s.Key} gem 0x{s.GemHash:x8} inventory {s.Inventory} socket {s.Socket} type {s.SlotType} record {s.RecordId}"))}; " +
                $"extras: {extras.Count}, flags: 0x{flags:x2}");
            return InstanceMicrotransactions.Purchased([], worn, skills, extras, flags);
        });
    }

    // The Cosmetics tab's skill slots: one per active gem type socketed in the equipment (a gem that grants a skill) and
    // effect slot type it takes (SkillGems MtxSlotTypes: the skill's effect, a totem skin), naming the first socket that
    // holds it (the panel finds the gem item there). Keys start at 50, past the fixed slots, and identify target lifetimes,
    // not positions in this list. Refresh only at the send gate: queued work must not publish an obsolete binding.
    public static List<(byte Row, byte Socket, uint Gem, byte SlotType)> SkillCosmeticPlaces(Player player) =>
        PlayerInventories.For(CharacterName(player), ItemsFor(player)).SocketedGemPlaces()
            .Where(g => ItemsFor(player).GemEffects(g.Gem).Any()).DistinctBy(g => g.Gem.TypeHash)
            .SelectMany(g => (ItemsFor(player).ByHash(g.Gem.TypeHash) is { } t && ItemsFor(player).Gem(t) is { MtxSlotTypes.Count: > 0 } gem ? gem.MtxSlotTypes : [0])
                .Select(slotType => (g.Row, (byte)g.Socket, Gem: g.Gem.TypeHash, SlotType: (byte)slotType)))
            .Take(byte.MaxValue).ToList();

    public static List<InstanceMicrotransactions.SkillSlot> SkillSlots(Player player)
    {
        string character = CharacterName(player);
        var places = SkillCosmeticPlaces(player);
        var worn = CharacterSaves.SkillCosmetics(character);
        var slots = new List<InstanceMicrotransactions.SkillSlot>();
        var previous = player.Character.SkillSlots.Values.ToDictionary(s => (s.Inventory, s.Socket, s.GemHash, s.SlotType), s => s.Key);
        foreach (var place in places)
        {
            uint key = previous.TryGetValue(place, out uint existing) ? existing : checked(player.Character.NextSkillCosmeticKey++);
            var slot = new InstanceMicrotransactions.SkillSlot(key, place.Gem, place.Row, place.Socket, place.SlotType);
            slots.Add(worn.TryGetValue(CharacterSaves.SkillCosmeticKey(place.Gem, place.SlotType), out var w) && Mtx.Resolve(w) is { } record
                ? slot with { RecordId = record.Id, MtxTypeHash = record.MtxTypeHash } : slot);
        }
        player.Character.SkillSlots = slots.ToDictionary(s => s.Key);
        return slots;
    }

    // Ends a burning Righteous Fire and lights it again from its gem (only from the gem of type `gem`, when given), so a
    // change of art or ring size shows at once. `expectedRelight` is the exact owner seen by the producer before its own
    // awaits; null means no RF was present and never late-binds to one that appeared later.
    public static async Task<bool> RelightRighteousFire(Player player, string why, RighteousFireState? expectedRelight, uint? gem = null)
    {
        PlayerSkillSource source;
        {
            if (!CurrentGameWork || expectedRelight is null || player.Closed || player.LeavingArea
                || !ReferenceEquals(player.RighteousFire, expectedRelight)
                || !RighteousFireOwnerCurrentLocked(player, expectedRelight)
                || expectedRelight.SourceLifetime is not { } lifetime)
                return false;
            using var scope = new PlayerStatScope(player, fresh: true);
            var supported = SupportedSkill(player, expectedRelight.Skill);
            source = CurrentPlayerSkillSourceLocked(player, expectedRelight.Skill)!;
            if (!supported.RequirementsMet || supported.Effect < 0
                || !IsRighteousFireEffect(SkillsFor(player).EffectId(supported.Effect))
                || source is not { Active: true } || source.Effect != supported.Effect
                || source.Identity != lifetime.Key || (gem is uint type && source.Gem != type))
                return false;
        }
        if (!await EndRighteousFire(player, why, expectedRelight, source,
                expectedRelight.Instance))
            return false;
        await ActivateRighteousFire(player, source.Id, source.Effect, source.Level,
            source.Quality, source.Gem, expectedRelight, source);
        return true;
    }

    /// <summary>A creature the character is drawn as: the client's morph number (the value of
    /// mtx_virtual_morph_art_variation_index), its animated object and its animation set.</summary>
    public sealed record Morph(int Index, string Model, string Actor);

    // The morphs the server knows the files of, by the client's morph number. No client table names them: the number
    // only tells the client that the character is morphed (it then hides weapons, flasks, footprints and stat-driven
    // visuals and takes the "default" stance), and the model and animation set arrive as paths (23-microtransactions.md
    // §3). The files are the client's own transformation rigs, found by name; the Goblin Shaman Skin's two and the
    // Skeleton Skin's were seen working (live 2026-11-12), the Lunar set's and the Wuzhiqi Potion's were not looked at.
    static readonly Dictionary<int, Morph> Morphs = new()
    {
        [18] = new(18, "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessTransformationHeld.ao",
            "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessPolymorph.act"),
        [19] = new(19, "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessTransformationCloakHeld.ao",
            "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessPolymorphCloak.act"),
        [20] = new(20, "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessTransformationWingsHeld.ao",
            "Metadata/Items/Armours/BodyArmours/TencentLunarGoddess/Polymorph/TencentLunarGoddessPolymorphWings.act"),
        [27] = new(27, "Metadata/Items/Armours/BodyArmours/WuzhiquArmour/Transform/WuzhiquTransformationHeld.ao",
            "Metadata/Items/Armours/BodyArmours/WuzhiquArmour/Transform/WuzhiqiTransform.act"),
        [30] = new(30, "Metadata/Pet/SkeletonBodyPaint/SkeletonBodyPaint.ao", "Metadata/Pet/SkeletonBodyPaint/SkeletonBodyPaintTransform.act"),
        [28] = new(28, "Metadata/Pet/Goblins/GoblinShamanPet/GoblinShamanPet_01.ao", "Metadata/Pet/Goblins/GoblinShamanPet/GoblinShamanTransform1.act"),
        [29] = new(29, "Metadata/Pet/Goblins/GoblinShamanPet/GoblinShamanPet_02.ao", "Metadata/Pet/Goblins/GoblinShamanPet/GoblinShamanTransform2.act"),
    };

    /// <summary>
    /// What the worn cosmetics turn the character into here, or null. The client's own calculator of
    /// mtx_virtual_morph_art_variation_index (0x1428d9cce is inside it) gives 0 outside towns, hideouts and the Rogue
    /// Harbour, then the first of: a Lunar set (mtx_tencent_lunar_set_polymorph 4 → 18, 19 or 20 by
    /// mtx_tencent_lunar_back_attachment), tencent_wuzhiqi_set_polymorph 10 → 27, mtx_goblin_morph 1 → 28 and 2 → 29,
    /// mtx_skeleton_body_paint 1 → 30, mtx_marionettes_revenge_skin_variation 1 to 4 → 31 to 34. The client does not run
    /// it for a player the server describes, so the server does and sends the result as that stat. A number whose files
    /// are not known (Morphs) morphs nothing.
    /// </summary>
    public static Morph? WornMorph(Player player)
    {
        // is_in_town_or_hideout_or_heist_hub: the Rogue Harbour (HeistHub, HeistHubEndless) is neither a town nor a
        // hideout in WorldAreas. Nowhere else: the transformation sets have an idle, a run and Emerge, and map a hit,
        // death and casts to the idle clip; they have no attack.
        if (!InTown(player) && HideoutCatalog.ByArea(player.Layout.Hash) is null
            && !player.Layout.Id.StartsWith("HeistHub", StringComparison.Ordinal)) return null;
        var worn = new Dictionary<uint, int>();
        foreach (var w in WornCosmetics(player).Where(w => w.Slot != 49))
            foreach (var (stat, value) in MtxEffects.Of(w.MtxTypeHash)?.Stats ?? [])
                worn[stat] = worn.GetValueOrDefault(stat) + value;
        int Of(uint key) => worn.GetValueOrDefault(key);
        int index = Of(Stat.MtxTencentLunarSetPolymorph) == 4
                ? Of(Stat.MtxTencentLunarBackAttachment) switch { 1 => 19, 2 => 20, _ => 18 }
            : Of(Stat.TencentWuzhiqiSetPolymorph) == 10 ? 27
            : Of(Stat.MtxGoblinMorph) is 1 or 2 ? 27 + Of(Stat.MtxGoblinMorph)
            : Of(Stat.MtxSkeletonBodyPaint) == 1 ? 30
            : Of(Stat.MtxMarionettesRevengeSkinVariation) is >= 1 and <= 4 ? 30 + Of(Stat.MtxMarionettesRevengeSkinVariation)
            : 0;
        return Morphs.GetValueOrDefault(index);
    }

    // A charge's art: the derived stat the client's charge visuals read (SkillArtVariations names it; the numbers are
    // BuffVisualsArtVariations' variants of endurance_charge, frenzy_charge and power_charge), and its inputs in the
    // order the client's calculators test them (3.29.3c 0x1428ed890, 0x1428edd70, 0x1428ee4e0). The first is the
    // Alternate Charge Skin's own stat, whose value is the variant; each of the others gives a fixed one.
    static readonly (uint Output, uint Skin, (uint Input, int Variant)[] Sources)[] ChargeArt =
    [
        (Stat.VirtualEnduranceChargeArtVariation, Stat.MtxEnduranceChargeArtVariation,
            [(Stat.ChargeElderArtVariation, 1000), (Stat.ChargePredatorFarrulsArtVariation, 1001),
             (Stat.EnduranceChargeFlameChargedHelmet, 1002), (Stat.MtxDruidicGlovesAltEnduranceCharge, 1003)]),
        (Stat.VirtualFrenzyChargeArtVariation, Stat.MtxFrenzyChargeArtVariation,
            [(Stat.FrenzyChargeFrenzyScalingHelmetArtVariation, 1000), (Stat.ChargeElderArtVariation, 1001),
             (Stat.FrenzyChargeBloodDanceArtVariation, 1002), (Stat.ChargePredatorFarrulsArtVariation, 1003),
             (Stat.FrenzyChargeFlameChargedHelmet, 1004), (Stat.MtxDruidicGlovesAltFrenzyCharge, 1006),
             (Stat.MtxDarkrayVectorsAltFrenzyCharge, 1007)]),
        (Stat.VirtualPowerChargeArtVariation, Stat.MtxPowerChargeArtVariation,
            [(Stat.PowerChargeEldritchVoidBatteryArtVariation, 1000), (Stat.ChargeElderArtVariation, 1001),
             (Stat.PowerChargeArtVariationAylardex, 1002), (Stat.ChargePredatorFarrulsArtVariation, 1003),
             (Stat.PowerChargeFlameChargedHelmet, 1004), (Stat.MtxDruidicGlovesAltPowerCharge, 1005)]),
    ];

    // Derived stats that are a choice of art: the first input that is not 0 gives its number, else the fallback stat's
    // own value is the result. The rules are the client's calculators'; inputs that are not a cosmetic's are left out
    // (a unique item's alternate art).
    static readonly (uint Output, (uint Input, int Value)[] Sources, uint? Fallback)[] DerivedArt =
    [
        (Stat.MtxHasChestEffect, [(Stat.MtxAngryChestBees, 1), (Stat.MtxChestFish, 1), (Stat.MtxVerdantChestWisps, 1)], null),
        (Stat.LevelUpSoundDisabled, [(Stat.MtxRingBlackStarLevelUpEffect, 1), (Stat.MtxSkeletonLevelUpEffect, 1)], null),
        (Stat.VirtualHasItemDropVisualReplacementStat, [(Stat.MtxHellfireReplaceCurrencyWithFireSpirits, 1),
            (Stat.MtxChaosOrbArmourEntity, 1), (Stat.MtxCardOracleArmourReplaceDivinationCardDropVisuals, 1)], null),
        (Stat.IsFloating, [], Stat.MtxCosmicArmourFlyingPlayer),
        (Stat.VirtualOnslaughtArtVariation, [(Stat.MtxThrillsteelAltOnslaught, 2), (Stat.MtxVoidEmperorOnslaughtRing, 1)], null),
        (Stat.VirtualArcaneSurgeArtVariation, [(Stat.MtxMadcapArcaneSurgeRing, 1)], null),
        (Stat.VirtualFortifyArtVariation, [(Stat.MtxMyrmidonFortifyRing, 1)], null),
        (Stat.VirtualMtxBreachArtVariation, [(Stat.MtxRitualBreachArtVariation, 1)], null),
        (Stat.VirtualCoveredInAshArtVariation, [(Stat.MtxPolaricDevastationAlternateCoveredInAshAndFrost, 1)], null),
        (Stat.VirtualIgniteVisualOverride, [(Stat.MtxAlternateAilmentsGlovesDemonicIgniteScorch, 28)], Stat.IgniteArtVariation),
        (Stat.VirtualShockArtVariation, [(Stat.MtxAlternateAilmentsGlovesVoidShockSap, 11), (Stat.MtxVoidforgeAlternateShockAilment, 14),
            (Stat.MtxStarforgeAlternateShockAilment, 15)], Stat.ShockArtVariation),
        (Stat.VirtualChillArtVariation, [(Stat.MtxHyrrisIreAlternateArrowVisuals, 2),
            (Stat.MtxAlternateAilmentsGlovesCrystallineChillFreezeBrittle, 4), (Stat.MtxSpiritChainsGlovesFreeze, 6),
            (Stat.MtxSpiritChainsGlovesFreezeVines, 7), (Stat.MtxSpiritChainsGlovesFreezeTentacles, 8)], Stat.ChillArtVariation),
        (Stat.VirtualFreezeArtVariation, [(Stat.MtxAlternateAilmentsGlovesCrystallineChillFreezeBrittle, 3),
            (Stat.MtxSpiritChainsGlovesFreeze, 5), (Stat.MtxSpiritChainsGlovesFreezeVines, 6),
            (Stat.MtxSpiritChainsGlovesFreezeTentacles, 7)], Stat.FreezeArtVariation),
        (Stat.VirtualPoisonArtVariation, [(Stat.MtxAlternateAilmentsGlovesBlackTarPoison, 2), (Stat.MtxIllusionistCobraLashPoison, 3)],
            Stat.PoisonArtVariation),
        (Stat.VirtualBleedArtVariation, [(Stat.MtxAlternateAilmentsGlovesVoodooBleed, 3)], Stat.BleedArtVariation),
        (Stat.VirtualMineArtVariation, [(Stat.CorruptedEmperorMineArtVariation, 1)], Stat.MineArtVariation),
        (Stat.VirtualAncestralCallArtVariation, [], Stat.AncestralCallArtVariation),
        (Stat.VirtualTriggeredSummonSpiderVariation, [(Stat.AlternateArakaaliSpidersDemonic, 1)], null),
        (Stat.VirtualSkillSurgeTypeOverride, [], Stat.SkillSurgeTypeOverride),
        (Stat.VirtualDamageHitEffectIndex, [], Stat.DamageHitEffectIndex),
        (Stat.VirtualMainHandDamageHitEffectIndex, [(Stat.MainHandMtxColdIronPointHitEffectVariation, 66),
            (Stat.MtxCordycepBowHitEffectVariation, 82)], Stat.DamageHitEffectIndex),
        (Stat.VirtualOffHandDamageHitEffectIndex, [(Stat.OffHandMtxColdIronPointHitEffectVariation, 66)], Stat.DamageHitEffectIndex),
    ];

    static readonly (uint Input, int Value)[] MinionSurges = [(Stat.MtxDarkAssemblySummonMinionEffect, 39),
        (Stat.MtxSummoningRingRitual, 108), (Stat.MtxSummoningRingStygian, 109), (Stat.MtxSummoningRingNecrotic, 110),
        (Stat.MtxSummoningRingDivine, 111)];

    // The outputs of DerivedArt that belong to the character alone: a skill does not carry them.
    static readonly HashSet<uint> CharacterArt = [Stat.MtxHasChestEffect, Stat.LevelUpSoundDisabled,
        Stat.VirtualHasItemDropVisualReplacementStat, Stat.IsFloating, Stat.VirtualOnslaughtArtVariation,
        Stat.VirtualArcaneSurgeArtVariation, Stat.VirtualFortifyArtVariation, Stat.VirtualMtxBreachArtVariation,
        Stat.VirtualCoveredInAshArtVariation];

    static IEnumerable<(uint Stat, int Value)> DerivedArtStats(Func<uint, int> of, bool skill = false)
    {
        foreach (var (output, sources, fallback) in DerivedArt)
        {
            if (skill && CharacterArt.Contains(output)) continue;
            int value = sources.FirstOrDefault(s => of(s.Input) != 0).Value;
            // A skill that creates minions takes Dark Assembly's and the summoning rings' surge before its own.
            if (output == Stat.VirtualSkillSurgeTypeOverride && of(Stat.SkillCreatesMinions) != 0)
                value = MinionSurges.FirstOrDefault(s => of(s.Input) != 0).Value;
            if (value == 0 && fallback is uint f) value = of(f);
            if (value != 0 && output is var key) yield return (key, value);
        }
    }

    /// <summary>
    /// The derived art stats of one skill (DerivedArt), from the skill's own stats, which hold its worn Skill Gem
    /// Effect's (a hit effect, a surge, an ailment's or a mine's art), and the character's worn cosmetics: the
    /// client's calculators run in the skill's context, which sees both.
    /// </summary>
    public static IEnumerable<(uint Stat, int Value)> SkillCosmeticDerivedStats(Player player, IReadOnlyDictionary<uint, int> skill)
    {
        var worn = new Dictionary<uint, int>();
        foreach (var w in WornCosmetics(player).Where(w => w.Slot != 49))
            foreach (var (stat, value) in MtxEffects.Of(w.MtxTypeHash)?.Stats ?? [])
                worn[stat] = worn.GetValueOrDefault(stat) + value;
        return DerivedArtStats(name => !(name is var key) ? 0 : skill.TryGetValue(key, out int own) ? own : worn.GetValueOrDefault(key),
            skill: true);
    }

    /// <summary>
    /// The derived stats of the worn cosmetics' own (<paramref name="worn"/>: stat key → value). The client keeps
    /// calculators for them but runs none for a player the server describes: it shows what it is sent. A cosmetic whose
    /// stat only such a calculator reads does nothing until the server sends the result: the morph (WornMorph) and the
    /// charges' art (ChargeArt) and the choices of art (DerivedArt). 23-microtransactions.md §4 lists what is left.
    /// </summary>
    public static IEnumerable<(uint Stat, int Value)> CosmeticDerivedStats(Player player, IReadOnlyDictionary<uint, int> worn)
    {
        int Of(uint key) => worn.GetValueOrDefault(key);
        if (WornMorph(player) is { } morph && Stat.MtxVirtualMorphArtVariationIndex is var morphKey)
            yield return (morphKey, morph.Index);
        foreach (var (output, skin, sources) in ChargeArt)
        {
            int variant = Of(skin) != 0 ? Of(skin) : sources.FirstOrDefault(s => Of(s.Input) != 0).Variant;
            if (variant != 0 && output is var key) yield return (key, variant);
        }
        foreach (var derived in DerivedArtStats(Of)) yield return derived;
    }

    // The BuffVisuals row a charge's buff shows: BuffVisualsArtVariations' row of the charge's art variation (ChargeArt:
    // a worn Alternate Charge Skin), null for the buff's own. The server names it, as for every buff whose art follows a
    // variation (0x1c1's BuffVisuals override, 11-skills.md §12); the derived stat alone changes nothing.
    public static ushort? ChargeArtVisual(Player player, ChargeType type)
    {
        string id = type switch { ChargeType.Endurance => "endurance_charge", ChargeType.Frenzy => "frenzy_charge", _ => "power_charge" };
        if (Stats.Key($"virtual_{id}_art_variation") is not uint key) return null;
        int variation = CosmeticStats(player).FirstOrDefault(s => s.Stat == key).Value;
        return variation == 0 ? null : Art.BuffVisual(id, variation);
    }

    // Animation.dat's row of Emerge, and how long the morph's Emerge holds the character (our own number).
    const byte EmergeAnimation = 43;
    const float MorphEmergeSeconds = 1f;

    // The BuffVisuals row one of the player's own buffs shows when a worn cosmetic restyles it: BuffVisualsArtVariations'
    // row ("onslaught_buff", "fortify_buff", "arcane_surge") at the derived stat's value (the rings), null for its own.
    public static ushort? BuffArtVisual(Player player, string buff, uint stat) =>
        stat is var key && CosmeticStats(player).FirstOrDefault(s => s.Stat == key).Value is int variation and not 0
            ? Art.BuffVisual(buff, variation) : null;

    // The morph to the client again after a pick changed it: the stat goes with the player's stats (CosmeticStats); the
    // model is the Animated component's (0x2bc) and the animation set the Actor's (0x1b6), back to the class's own when
    // the morph ended.
    public static async Task SendMorph(Player player, Morph? before)
    {
        var now = WornMorph(player);
        if (now == before) return;
        var cls = SkillsFor(player).ClassFor(player.Character.ClassRow);
        string model = now?.Model ?? Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject;
        string actor = now?.Actor ?? System.IO.Path.ChangeExtension(model, ".act").Replace('\\', '/');
        await SendWorldRecord(player, player.Id, InstanceEntities.AnimatedModel(player.Id, PlayerObject.AnimatedIndex, model),
            $"0x02bc player model {model} (morph {now?.Index ?? 0})");
        await SendWorldRecord(player, player.Id, InstanceEntities.ActorFile(player.Id, actor),
            $"0x01b6 player animation set {actor}");
        // The client loads the new set but leaves the animation that was playing (0x1b6 only stores it), so the new
        // model stands frozen in its bind pose until its next action (seen live 2026-11-12). The transformation sets
        // define Emerge for this: their idle with the pets' summon effect, after which the Actor idles on the new set.
        if (now is not null)
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActorAnimation(player.Id, MorphEmergeSeconds, EmergeAnimation),
                $"0x01d1 player animation Emerge (morph {now.Index})");
        // The model was built anew: what it wears again.
        await SendModel(player, Looks(player, ItemsOf(player)), "morph");
    }

    // The worn portrait's variation (the Player component's +0x1aa), 0 for the class's own.
    public static ushort WornPortrait(Player player) =>
        WornCosmetics(player).FirstOrDefault(w => w.Slot == PortraitSlot) is { } worn ? MtxEffects.Of(worn.MtxTypeHash)?.Portrait ?? 0 : (ushort)0;

    // The portrait frame in the Social Frame slot, 0 for the default. What the client draws is the one it logged in with
    // (Player.PortraitFrame).
    public static ushort WornFrame(Player player) =>
        WornCosmetics(player).FirstOrDefault(w => w.Slot == SocialFrameSlot) is { } worn ? MtxEffects.Of(worn.MtxTypeHash)?.Frame ?? 0 : (ushort)0;
}
