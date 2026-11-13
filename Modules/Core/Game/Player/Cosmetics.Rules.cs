using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The Cosmetics tab (K) wears what S→C 0x1f3 lists, all slots at once (23-microtransactions.md). Saved picks first; a
    // portal effect set with /cosmetic and no Portal slot saved shows in that slot too.
    // Read from the save once per connection (the stats ask for it on every hit); a pick clears it.
    public static List<InstanceMicrotransactions.Equipped> WornCosmetics(ClientState conn)
    {
        if (conn.WornCosmetics is { } cached) return cached;
        var worn = new Dictionary<byte, CharacterSaves.SavedCosmetic>(CharacterSaves.Cosmetics(CharacterName(conn)));
        if (!worn.ContainsKey(InstanceMicrotransactions.PortalSlot) && conn.CosmeticPortalVariation != 0
            && Cosmetics.Portals.FirstOrDefault(p => p.Index == conn.CosmeticPortalVariation) is { Hash: not 0 } portal
            && Mtx.ByHash(portal.Hash) is { } record)
            worn[InstanceMicrotransactions.PortalSlot] = new CharacterSaves.SavedCosmetic(record.Id, Hash: record.MtxTypeHash);
        return conn.WornCosmetics = Mtx.Equipped(worn);
    }

    public static async Task SendWornCosmetics(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            var worn = WornCosmetics(conn);
            var skills = SkillSlots(conn);
            Console.WriteLine($"[gateway] {remote} => 0x01f3 worn cosmetics ({why}): " +
                $"{string.Join(", ", worn.Select(w => $"slot {w.Slot} = record {w.Id}"))}; " +
                $"skills: {string.Join(", ", skills.Select(s => $"key {s.Key} gem 0x{s.GemHash:x8} inventory {s.Inventory} socket {s.Socket} type {s.SlotType} record {s.RecordId}"))}");
            return InstanceMicrotransactions.Purchased([], worn, skills);
        });
    }

    // The Cosmetics tab's skill slots: one per active gem type socketed in the equipment (a gem that grants a skill) and
    // effect slot type it takes (SkillGems MtxSlotTypes: the skill's effect, a totem skin), naming the first socket that
    // holds it (the panel finds the gem item there). Keys start at 50, past the fixed slots, and identify target lifetimes,
    // not positions in this list. Refresh only at the send gate: queued work must not publish an obsolete binding.
    public static List<(byte Row, byte Socket, uint Gem, byte SlotType)> SkillCosmeticPlaces(ClientState conn) =>
        PlayerInventories.For(CharacterName(conn), ItemsFor(conn)).SocketedGemPlaces()
            .Where(g => ItemsFor(conn).GemEffects(g.Gem).Any()).DistinctBy(g => g.Gem.TypeHash)
            .SelectMany(g => (ItemsFor(conn).ByHash(g.Gem.TypeHash) is { } t && ItemsFor(conn).Gem(t) is { MtxSlotTypes.Count: > 0 } gem ? gem.MtxSlotTypes : [0])
                .Select(slotType => (g.Row, (byte)g.Socket, Gem: g.Gem.TypeHash, SlotType: (byte)slotType)))
            .Take(byte.MaxValue).ToList();

    public static List<InstanceMicrotransactions.SkillSlot> SkillSlots(ClientState conn)
    {
        string character = CharacterName(conn);
        var places = SkillCosmeticPlaces(conn);
        var worn = CharacterSaves.SkillCosmetics(character);
        var slots = new List<InstanceMicrotransactions.SkillSlot>();
        var previous = conn.SkillSlots.Values.ToDictionary(s => (s.Inventory, s.Socket, s.GemHash, s.SlotType), s => s.Key);
        foreach (var place in places)
        {
            uint key = previous.TryGetValue(place, out uint existing) ? existing : checked(conn.NextSkillCosmeticKey++);
            var slot = new InstanceMicrotransactions.SkillSlot(key, place.Gem, place.Row, place.Socket, place.SlotType);
            slots.Add(worn.TryGetValue(CharacterSaves.SkillCosmeticKey(place.Gem, place.SlotType), out var w) && Mtx.Resolve(w) is { } record
                ? slot with { RecordId = record.Id, MtxTypeHash = record.MtxTypeHash } : slot);
        }
        conn.SkillSlots = slots.ToDictionary(s => s.Key);
        return slots;
    }

    // Ends a burning Righteous Fire and lights it again from its gem (only from the gem of type `gem`, when given), so a
    // change of art or ring size shows at once. False when none burns.
    public static async Task<bool> RelightRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why, uint? gem = null)
    {
        if (conn.RighteousFire is not { } rf || GemSkillEffects(conn).FirstOrDefault(g => g.Id == rf.Skill) is not { Name: not null } skill
            || (gem is uint type && skill.Gem != type)) return false;
        await EndRighteousFire(remote, stream, ciphers, conn, why);
        await ActivateRighteousFire(remote, stream, ciphers, conn, skill.Id, skill.Effect, skill.Level, skill.Quality, skill.Gem);
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
    public static Morph? WornMorph(ClientState conn)
    {
        if (!InTown(conn) && HideoutCatalog.ByArea(conn.Layout.Hash) is null) return null;
        var worn = new Dictionary<uint, int>();
        foreach (var w in WornCosmetics(conn).Where(w => w.Slot != 49))
            foreach (var (stat, value) in MtxEffects.Of(w.MtxTypeHash)?.Stats ?? [])
                worn[stat] = worn.GetValueOrDefault(stat) + value;
        int Of(string name) => Stats.Key(name) is uint key ? worn.GetValueOrDefault(key) : 0;
        int index = Of("mtx_tencent_lunar_set_polymorph") == 4
                ? Of("mtx_tencent_lunar_back_attachment") switch { 1 => 19, 2 => 20, _ => 18 }
            : Of("tencent_wuzhiqi_set_polymorph") == 10 ? 27
            : Of("mtx_goblin_morph") is 1 or 2 ? 27 + Of("mtx_goblin_morph")
            : Of("mtx_skeleton_body_paint") == 1 ? 30
            : Of("mtx_marionettes_revenge_skin_variation") is >= 1 and <= 4 ? 30 + Of("mtx_marionettes_revenge_skin_variation")
            : 0;
        return Morphs.GetValueOrDefault(index);
    }

    // A charge's art: the derived stat the client's charge visuals read (SkillArtVariations names it; the numbers are
    // BuffVisualsArtVariations' variants of endurance_charge, frenzy_charge and power_charge), and its inputs in the
    // order the client's calculators test them (3.29.3c 0x1428ed890, 0x1428edd70, 0x1428ee4e0). The first is the
    // Alternate Charge Skin's own stat, whose value is the variant; each of the others gives a fixed one.
    static readonly (string Output, string Skin, (string Input, int Variant)[] Sources)[] ChargeArt =
    [
        ("virtual_endurance_charge_art_variation", "mtx_endurance_charge_art_variation",
            [("charge_elder_art_variation", 1000), ("charge_predator_farruls_art_variation", 1001),
             ("endurance_charge_flame_charged_helmet", 1002), ("mtx_druidic_gloves_alt_endurance_charge", 1003)]),
        ("virtual_frenzy_charge_art_variation", "mtx_frenzy_charge_art_variation",
            [("frenzy_charge_frenzy_scaling_helmet_art_variation", 1000), ("charge_elder_art_variation", 1001),
             ("frenzy_charge_blood_dance_art_variation", 1002), ("charge_predator_farruls_art_variation", 1003),
             ("frenzy_charge_flame_charged_helmet", 1004), ("mtx_druidic_gloves_alt_frenzy_charge", 1006),
             ("mtx_darkray_vectors_alt_frenzy_charge", 1007)]),
        ("virtual_power_charge_art_variation", "mtx_power_charge_art_variation",
            [("power_charge_eldritch_void_battery_art_variation", 1000), ("charge_elder_art_variation", 1001),
             ("power_charge_art_variation_aylardex", 1002), ("charge_predator_farruls_art_variation", 1003),
             ("power_charge_flame_charged_helmet", 1004), ("mtx_druidic_gloves_alt_power_charge", 1005)]),
    ];

    // Derived stats that are a choice of art: the first input that is not 0 gives its number, else the fallback stat's
    // own value is the result. The rules are the client's calculators'; inputs that are not a cosmetic's are left out
    // (a unique item's alternate art, the summoning rings' surges, which also need the skill to create minions).
    static readonly (string Output, (string Input, int Value)[] Sources, string? Fallback)[] DerivedArt =
    [
        ("mtx_has_chest_effect", [("mtx_angry_chest_bees", 1), ("mtx_chest_fish", 1), ("mtx_verdant_chest_wisps", 1)], null),
        ("level_up_sound_disabled", [("mtx_ring_black_star_level_up_effect", 1), ("mtx_skeleton_level_up_effect", 1)], null),
        ("virtual_has_item_drop_visual_replacement_stat", [("mtx_hellfire_replace_currency_with_fire_spirits", 1),
            ("mtx_chaos_orb_armour_entity", 1), ("mtx_card_oracle_armour_replace_divination_card_drop_visuals", 1)], null),
        ("is_floating", [], "mtx_cosmic_armour_flying_player"),
        ("virtual_onslaught_art_variation", [("mtx_thrillsteel_alt_onslaught", 2), ("mtx_void_emperor_onslaught_ring", 1)], null),
        ("virtual_arcane_surge_art_variation", [("mtx_madcap_arcane_surge_ring", 1)], null),
        ("virtual_fortify_art_variation", [("mtx_myrmidon_fortify_ring", 1)], null),
        ("virtual_mtx_breach_art_variation", [("mtx_ritual_breach_art_variation", 1)], null),
        ("virtual_covered_in_ash_art_variation", [("mtx_polaric_devastation_alternate_covered_in_ash_and_frost", 1)], null),
        ("virtual_ignite_visual_override", [("mtx_alternate_ailments_gloves_demonic_ignite_scorch", 28)], "ignite_art_variation"),
        ("virtual_shock_art_variation", [("mtx_alternate_ailments_gloves_void_shock_sap", 11), ("mtx_voidforge_alternate_shock_ailment", 14),
            ("mtx_starforge_alternate_shock_ailment", 15)], "shock_art_variation"),
        ("virtual_chill_art_variation", [("mtx_hyrris_ire_alternate_arrow_visuals", 2),
            ("mtx_alternate_ailments_gloves_crystalline_chill_freeze_brittle", 4), ("mtx_spirit_chains_gloves_freeze", 6),
            ("mtx_spirit_chains_gloves_freeze_vines", 7), ("mtx_spirit_chains_gloves_freeze_tentacles", 8)], "chill_art_variation"),
        ("virtual_freeze_art_variation", [("mtx_alternate_ailments_gloves_crystalline_chill_freeze_brittle", 3),
            ("mtx_spirit_chains_gloves_freeze", 5), ("mtx_spirit_chains_gloves_freeze_vines", 6),
            ("mtx_spirit_chains_gloves_freeze_tentacles", 7)], "freeze_art_variation"),
        ("virtual_poison_art_variation", [("mtx_alternate_ailments_gloves_black_tar_poison", 2), ("mtx_illusionist_cobra_lash_poison", 3)],
            "poison_art_variation"),
        ("virtual_bleed_art_variation", [("mtx_alternate_ailments_gloves_voodoo_bleed", 3)], "bleed_art_variation"),
        ("virtual_mine_art_variation", [("corrupted_emperor_mine_art_variation", 1)], "mine_art_variation"),
        ("virtual_ancestral_call_art_variation", [], "ancestral_call_art_variation"),
        ("virtual_triggered_summon_spider_variation", [("alternate_arakaali_spiders_demonic", 1)], null),
        ("virtual_skill_surge_type_override", [], "skill_surge_type_override"),
        ("virtual_damage_hit_effect_index", [], "damage_hit_effect_index"),
        ("virtual_main_hand_damage_hit_effect_index", [("main_hand_mtx_cold_iron_point_hit_effect_variation", 66),
            ("mtx_cordycep_bow_hit_effect_variation", 82)], "damage_hit_effect_index"),
        ("virtual_off_hand_damage_hit_effect_index", [("off_hand_mtx_cold_iron_point_hit_effect_variation", 66)], "damage_hit_effect_index"),
    ];

    // The outputs of DerivedArt that belong to the character alone: a skill does not carry them.
    static readonly HashSet<string> CharacterArt = ["mtx_has_chest_effect", "level_up_sound_disabled",
        "virtual_has_item_drop_visual_replacement_stat", "is_floating", "virtual_onslaught_art_variation",
        "virtual_arcane_surge_art_variation", "virtual_fortify_art_variation", "virtual_mtx_breach_art_variation",
        "virtual_covered_in_ash_art_variation"];

    static IEnumerable<(uint Stat, int Value)> DerivedArtStats(Func<string, int> of, bool skill = false)
    {
        foreach (var (output, sources, fallback) in DerivedArt)
        {
            if (skill && CharacterArt.Contains(output)) continue;
            int value = sources.FirstOrDefault(s => of(s.Input) != 0).Value;
            if (value == 0 && fallback is not null) value = of(fallback);
            if (value != 0 && Stats.Key(output) is uint key) yield return (key, value);
        }
    }

    /// <summary>
    /// The derived art stats of one skill (DerivedArt), from the skill's own stats, which hold its worn Skill Gem
    /// Effect's (a hit effect, a surge, an ailment's or a mine's art), and the character's worn cosmetics: the
    /// client's calculators run in the skill's context, which sees both.
    /// </summary>
    public static IEnumerable<(uint Stat, int Value)> SkillCosmeticDerivedStats(ClientState conn, IReadOnlyDictionary<uint, int> skill)
    {
        var worn = new Dictionary<uint, int>();
        foreach (var w in WornCosmetics(conn).Where(w => w.Slot != 49))
            foreach (var (stat, value) in MtxEffects.Of(w.MtxTypeHash)?.Stats ?? [])
                worn[stat] = worn.GetValueOrDefault(stat) + value;
        return DerivedArtStats(name => Stats.Key(name) is not uint key ? 0 : skill.TryGetValue(key, out int own) ? own : worn.GetValueOrDefault(key),
            skill: true);
    }

    /// <summary>
    /// The derived stats of the worn cosmetics' own (<paramref name="worn"/>: stat key → value). The client keeps
    /// calculators for them but runs none for a player the server describes: it shows what it is sent. A cosmetic whose
    /// stat only such a calculator reads does nothing until the server sends the result: the morph (WornMorph) and the
    /// charges' art (ChargeArt) and the choices of art (DerivedArt). 23-microtransactions.md §4 lists what is left.
    /// </summary>
    public static IEnumerable<(uint Stat, int Value)> CosmeticDerivedStats(ClientState conn, IReadOnlyDictionary<uint, int> worn)
    {
        int Of(string name) => Stats.Key(name) is uint key ? worn.GetValueOrDefault(key) : 0;
        if (WornMorph(conn) is { } morph && Stats.Key("mtx_virtual_morph_art_variation_index") is uint morphKey)
            yield return (morphKey, morph.Index);
        foreach (var (output, skin, sources) in ChargeArt)
        {
            int variant = Of(skin) != 0 ? Of(skin) : sources.FirstOrDefault(s => Of(s.Input) != 0).Variant;
            if (variant != 0 && Stats.Key(output) is uint key) yield return (key, variant);
        }
        foreach (var derived in DerivedArtStats(Of)) yield return derived;
    }

    // The BuffVisuals row a charge's buff shows: BuffVisualsArtVariations' row of the charge's art variation (ChargeArt:
    // a worn Alternate Charge Skin), null for the buff's own. The server names it, as for every buff whose art follows a
    // variation (0x1c1's BuffVisuals override, 11-skills.md §12); the derived stat alone changes nothing.
    public static ushort? ChargeArtVisual(ClientState conn, ChargeType type)
    {
        string id = type switch { ChargeType.Endurance => "endurance_charge", ChargeType.Frenzy => "frenzy_charge", _ => "power_charge" };
        if (Stats.Key($"virtual_{id}_art_variation") is not uint key) return null;
        int variation = CosmeticStats(conn).FirstOrDefault(s => s.Stat == key).Value;
        return variation == 0 ? null : Art.BuffVisual(id, variation);
    }

    // Animation.dat's row of Emerge, and how long the morph's Emerge holds the character (our own number).
    const byte EmergeAnimation = 43;
    const float MorphEmergeSeconds = 1f;

    // The BuffVisuals row one of the player's own buffs shows when a worn cosmetic restyles it: BuffVisualsArtVariations'
    // row ("onslaught_buff", "fortify_buff", "arcane_surge") at the derived stat's value (the rings), null for its own.
    public static ushort? BuffArtVisual(ClientState conn, string buff, string stat) =>
        Stats.Key(stat) is uint key && CosmeticStats(conn).FirstOrDefault(s => s.Stat == key).Value is int variation and not 0
            ? Art.BuffVisual(buff, variation) : null;

    // The morph to the client again after a pick changed it: the stat goes with the player's stats (CosmeticStats); the
    // model is the Animated component's (0x2bc) and the animation set the Actor's (0x1b6), back to the class's own when
    // the morph ended.
    public static async Task SendMorph(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Morph? before)
    {
        var now = WornMorph(conn);
        if (now == before) return;
        var cls = SkillsFor(conn).ClassFor(conn.ClassRow);
        string model = now?.Model ?? Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject;
        string actor = now?.Actor ?? System.IO.Path.ChangeExtension(model, ".act").Replace('\\', '/');
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AnimatedModel(PlayerId, PlayerObject.AnimatedIndex, model),
            $"0x02bc player model {model} (morph {now?.Index ?? 0})");
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.ActorFile(PlayerId, actor),
            $"0x01b6 player animation set {actor}");
        // The client loads the new set but leaves the animation that was playing (0x1b6 only stores it), so the new
        // model stands frozen in its bind pose until its next action (seen live 2026-11-12). The transformation sets
        // define Emerge for this: their idle with the pets' summon effect, after which the Actor idles on the new set.
        if (now is not null)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActorAnimation(PlayerId, MorphEmergeSeconds, EmergeAnimation),
                $"0x01d1 player animation Emerge (morph {now.Index})");
        // The model was built anew: what it wears again.
        await SendModel(remote, stream, ciphers, conn, Looks(conn, ItemsOf(conn)), "morph");
    }

    // The worn portrait's variation (the Player component's +0x1aa), 0 for the class's own.
    public static ushort WornPortrait(ClientState conn) =>
        WornCosmetics(conn).FirstOrDefault(w => w.Slot == PortraitSlot) is { } worn ? MtxEffects.Of(worn.MtxTypeHash)?.Portrait ?? 0 : (ushort)0;

    // The portrait frame in the Social Frame slot, 0 for the default. What the client draws is the one it logged in with
    // (ClientState.PortraitFrame).
    public static ushort WornFrame(ClientState conn) =>
        WornCosmetics(conn).FirstOrDefault(w => w.Slot == SocialFrameSlot) is { } worn ? MtxEffects.Of(worn.MtxTypeHash)?.Frame ?? 0 : (ushort)0;
}
