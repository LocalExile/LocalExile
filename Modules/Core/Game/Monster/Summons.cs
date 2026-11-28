using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SummonCatalog SummonData = null!;
    const string SummonSaveKey = "Game.SummonedMinions";
    const string GuardianSaveKey = "Game.AnimatedGuardianEquipment";
    static Dictionary<int, int> SpectreOverrides = [];

    internal static void LoadOwnedSummonsData()
    {
        SpectreOverrides = ClientTables.TableOrEmpty("SpectreOverrides")
            .ToDictionary(row => row.Int("Monster"), row => row.Int("Spectre"));
        LoadItemisedCorpseData();
        LoadDivineSentinelAuraData();
        LoadGuardianItemAuraRadii();
        LoadSpectreChildrenData();
    }

    const string SpectreBankSaveKey = "Game.SpectreBank";
    const int MaxSpectreBankCount = 16;

    // BuffDefinitions "spectre_buff": BuffVisuals raise_specter_02/monster.epk (translucent spectral model) + is_raised_spectre.
    static ushort SpectreBuffRow = 61;
    static IReadOnlyDictionary<string, ushort> SpectreVisualRows = new Dictionary<string, ushort>(StringComparer.Ordinal);

    internal static void LoadSummonsData()
    {
        ushort buffRow = 61;
        if (GameServer.Core.ClientTables.Has("BuffDefinitions"))
            foreach (var row in GameServer.Core.ClientTables.Table("BuffDefinitions"))
                if (row.Text("Id") == "spectre_buff") { buffRow = (ushort)row.Index; break; }
        SpectreBuffRow = buffRow;

        var map = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (GameServer.Core.ClientTables.Has("BuffVisuals"))
            foreach (var row in GameServer.Core.ClientTables.Table("BuffVisuals"))
            {
                string? rowId = row.Text("Id");
                if (rowId is "spectre_alternate" or "black_spectres" or "cultist_spectres" or "celestial_spectres"
                    or "arcane_spectres" or "stygian_spectres" or "darkwood_spectres" or "oblivion_spectres"
                    or "onyx_oblivion_spectres")
                    map[rowId] = (ushort)row.Index;
            }
        SpectreVisualRows = map;
    }

    static ushort SpectreBuffRowValue() => SpectreBuffRow;

    const uint AlternateRaiseSpectreMtxHash = 3158447165u;
    static ushort? SpectreVisualRowById(string id) =>
        SpectreVisualRows.TryGetValue(id, out ushort v) ? v : null;

    static (bool HasCosmetic, ushort? Visual) SpectreBuffVisual(Player player, ushort source)
    {
        uint gemHash = GemSkillEffects(player).FirstOrDefault(g => g.Id == source).Gem;
        if (gemHash == 0) return (false, null);
        string character = CharacterName(player);
        var wornMap = CharacterSaves.SkillCosmetics(string.IsNullOrEmpty(character) ? "Exile" : character);
        foreach (var (key, worn) in wornMap)
        {
            if (CharacterSaves.SkillCosmeticGem(key) != gemHash || Mtx.Resolve(worn) is not { } record) continue;
            if (record.MtxTypeHash == AlternateRaiseSpectreMtxHash)
                return (true, SpectreVisualRowById("spectre_alternate"));
            if (MtxEffects.Of(record.MtxTypeHash) is { } fx)
            {
                int art = fx.Stats.FirstOrDefault(s => s.Stat == Stat.SkillArtVariation).Value;
                string? visualId = art switch
                {
                    1 => "black_spectres",
                    2 => "cultist_spectres",
                    4 => "celestial_spectres",
                    5 => "arcane_spectres",
                    6 => "stygian_spectres",
                    7 => "darkwood_spectres",
                    8 => "oblivion_spectres",
                    9 => "onyx_oblivion_spectres",
                    _ => null,
                };
                return (true, visualId is null ? null : SpectreVisualRowById(visualId));
            }
            return (true, null);
        }
        return (false, null);
    }

    static void ApplySpectreBuffVisualLocked(Player player, uint id, GolemState.Golem live,
        (bool HasCosmetic, ushort? Visual)? precomputed = null)
    {
        var (hasCosmetic, visual) = precomputed ?? SpectreBuffVisual(player, live.Source);
        if (!hasCosmetic || visual is not null)
        {
            live.SpectreBuff = 1;
            player.Area.Buffs.Set(id, new(SpectreBuffRowValue(), id, Visual: visual));
        }
        else
        {
            if (live.SpectreBuff != 0)
            {
                player.Area.Buffs.Clear(id, SpectreBuffRowValue(), id);
                live.SpectreBuff = 0;
            }
        }
    }

    public static void RefreshSpectreCosmetics(Player player, uint gemHash)
    {
        var matchingSources = GemSkillEffects(player).Where(g => g.Gem == gemHash).Select(g => g.Id).ToHashSet();
        if (matchingSources.Count == 0) return;
        using (ProjectileMutation(player, advance: false, phase: "spectre cosmetic refresh"))
        {
            foreach (var (id, live) in player.Summons.Fighters)
            {
                if (live.Monster.Dead || live.Monster.Life == 0 || !matchingSources.Contains(live.Source)) continue;
                if (player.Summons.Definitions.GetValueOrDefault(id)?.Family != "Spectre") continue;
                ApplySpectreBuffVisualLocked(player, id, live);
            }
        }
    }

    public static void SaveSummons(Player player)
    {
        lock (MinionSaveLocks.GetOrCreateValue(player))
        {
            string text, guardian, character;
            if (!player.Summons.Restored || player.Summons.LostPersistentOnDeath || InTown(player)) return;
            double now = AreaTime(player);
            text = JsonSerializer.Serialize(player.Summons.Fighters.Values.Where(g => !g.Monster.Dead && g.Monster.Life > 0
                    && player.Summons.Definitions.GetValueOrDefault(g.Id)?.Persistent == true
                    && player.Summons.Until.TryGetValue(g.Id, out double until) && double.IsPositiveInfinity(until))
                .Select(g => SavedSummon(player, g, now)).ToArray());
            guardian = JsonSerializer.Serialize(player.Summons.GuardianEquipment);
            character = CharacterName(player);
            Saves.Atomically(() =>
            {
                CharacterSaves.ChangeModuleData(character, SummonSaveKey, _ => text);
                CharacterSaves.ChangeModuleData(character, GuardianSaveKey, _ => guardian);
            });
        }
    }

    static SummonedMinions.Saved SavedSummon(Player player, GolemState.Golem fighter, double now)
    {
        var gem = SupportedSkill(player, fighter.Source);
        object? sourceId = gem.SourceGem is not null ? PlayerInventories.GemIdentity(gem.SourceGem) : null;
        var place = sourceId is null ? default : ItemsOf(player).SocketedGemPlaces().FirstOrDefault(p =>
            ReferenceEquals(PlayerInventories.GemIdentity(p.Gem), sourceId));
        return new(fighter.Definition.Id, fighter.Source, fighter.Monster.Variety!.Id,
            SavedLifeFraction(fighter.Monster),
            fighter.Equipment.Count > 0 ? fighter.Equipment : null,
            CaptureResources(fighter, now),
            Host: place.ItemId == 0 ? null : place.ItemId, Socket: place.ItemId == 0 ? null : place.Socket);
    }

    internal static ushort RestoredSummonSource(Player player, SummonedMinions.Saved saved) =>
        RestoredMinionSource(player, saved.Effect, saved.Source, saved.Host, saved.Socket);

    static ushort RestoredMinionSource(Player player, string effect, ushort source, uint? host, int? socket)
    {
        var candidates = GemSkillEffects(player).Where(g => SkillsFor(player).EffectId(g.Effect) == effect).ToArray();
        if (host is uint item && socket is int index)
        {
            if (index < 0)
                return candidates.FirstOrDefault(g => SupportedSkill(player, g.Id).WornGrant is { } grant
                    && grant.Host == item && grant.Socket == index).Id;
            var place = ItemsOf(player).SocketedGemPlaces().FirstOrDefault(p => p.ItemId == item && p.Socket == index);
            if (place.Gem is null) return 0;
            object placeId = PlayerInventories.GemIdentity(place.Gem);
            return candidates.FirstOrDefault(g => SupportedSkill(player, g.Id).SourceGem is { } sg
                && ReferenceEquals(PlayerInventories.GemIdentity(sg), placeId)).Id;
        }
        var exact = candidates.FirstOrDefault(g => g.Id == source);
        return exact.Name is not null ? exact.Id : candidates.FirstOrDefault().Id;
    }

    /// <summary>How many minions one use brings: Summon Skeletons its melee, archer and mage counts together (at least
    /// one), every other summon one.</summary>
    public static int SummonCount(SummonCatalog.Definition definition, Func<uint, int> stat) =>
        definition.Family == "Skeleton" ? Math.Max(1, new[] { Stat.NumberOfMeleeSkeletonsToSummon,
            Stat.NumberOfArcherSkeletonsToSummon, Stat.NumberOfMageSkeletonsToSummon }.Sum(stat)) : 1;

    internal static string? DarkMonarchMinionFamily(int value) => value switch
    {
        1 => "AnimatedWeapon",
        2 => "Golem",
        3 => "RagingSpirit",
        4 => "Spectre",
        5 => "Spider",
        6 => "Zombie",
        7 => "Reaper",
        8 => "Absolution",
        9 => "Dominating",
        10 => "Purity",
        11 => "HolyRelic",
        12 => "Phantasm",
        13 => "Skeleton",
        14 => "SpectralWolf",
        15 => "LivingLightning",
        16 => "HolyArmament",
        _ => null,
    };

    /// <summary>The summon families whose official create (MinionType row, as in SentinelOwnership.MinionNativeType) is flag 0x02
    /// with Owner, [Source], Self exactly: zombie (7), Herald of Agony spiders (39), raging spirit (20), animated guardian (18),
    /// Vaal Domination sentinel (4). Spectres (0), animated weapons (19), Raise Spiders and the rest are not written that way.</summary>
    internal static bool OfficialOwnedCreate(string family, string id) => family switch
    {
        "Zombie" => id != "RaiseZombieAltY",
        "Agony" or "RagingSpirit" or "AnimatedGuardian" or "VaalDomination" => true,
        _ => false,
    };

    static string CanonicalMinionFamily(string family) => family switch
    {
        "VaalAbsolution" => "Absolution",
        "VaalDomination" => "Dominating",
        "VaalSkeleton" => "Skeleton",
        "VaalAnimatedWeapon" => "AnimatedWeapon",
        "SpectreSkeleton" or "SpectreConstruct" or "SpectreEnfeebleGround" or "SpectreVulnerabilityGround"
            or "SpectreTemporalChainsGround" => "Spectre",
        _ when family.StartsWith("Dominating", StringComparison.Ordinal) => "Dominating",
        _ => family,
    };

    public static bool MinionFamilyAllowed(Func<uint, int> playerStat, string family)
    {
        string canon = CanonicalMinionFamily(family);
        if (playerStat(Stat.YouCannotHaveNonSpectreMinions) != 0 && canon != "Spectre") return false;
        if (playerStat(Stat.YouCannotHaveNonAnimatedMinions) != 0 && canon is not ("AnimatedWeapon" or "AnimatedGuardian")) return false;
        if (playerStat(Stat.YouCannotHaveNonGolemMinions) != 0 && canon != "Golem") return false;
        int monarch = playerStat(Stat.MaximumNumberOfXMinionTypeDoubled);
        return monarch == 0 || DarkMonarchMinionFamily(monarch) == canon;
    }

    public static int ApplyDarkMonarchLimit(int limit, Func<uint, int> playerStat, string family) =>
        !MinionFamilyAllowed(playerStat, family) ? 0
        : playerStat(Stat.MaximumNumberOfXMinionTypeDoubled) is int monarch and > 0
            && DarkMonarchMinionFamily(monarch) == CanonicalMinionFamily(family)
            ? Math.Clamp(limit * 2, 0, 200)
            : limit;

    public static int SummonLimit(Player player, SummonCatalog.Definition definition, SupportedGem gem)
        => SummonLimit(definition, gem.Stats, CharacterPlayerStats(player));

    static int SummonLimit(SummonCatalog.Definition definition, IReadOnlyDictionary<uint, int> skillStats,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        if (!MinionFamilyAllowed(key => GolemStat(playerStats, key), definition.Family)) return 0;
        if (definition.LimitStat == 0) return definition.LimitWithoutStat;
        uint final = definition.FinalLimitStat;
        long value = (long)GolemStat(skillStats, definition.LimitStat) + GolemStat(playerStats, definition.LimitStat)
            + GolemStat(playerStats, final);
        if (definition.Family == "Zombie")
        {
            int PerAttribute(uint attribute, uint threshold) => GolemStat(playerStats, threshold) is int step and > 0
                ? Math.Max(0, GolemStat(playerStats, attribute)) / step : 0;
            value += (long)PerAttribute(Stat.Strength, Stat.NumberOfZombiesAllowedPlus1PerXStrength)
                + PerAttribute(Stat.Intelligence, Stat.NumberOfZombiesAllowedPlus1PerXIntelligenceFromFoulbornBaron);
            value = (long)Math.Clamp(value * Math.Max(0, 100L + GolemStat(playerStats, Stat.NumberOfZombiesAllowedPlusPct)) / 100, 0, 200);
        }
        if (definition.Family == "Skeleton")
            value += GolemStat(playerStats, Stat.NumberOfSkeletonsAllowedPer2Old) / 2;
        if (definition.Family == "RagingSpirit")
            value = (long)Math.Clamp(value * (1 + GolemStat(playerStats, Stat.PctNumberOfRagingSpiritsAllowed) / 100d), 0, 200);
        // Clones have no ordinary cap; duration is their lifetime constraint.
        if (value == 0 && definition.Family == "Clone") return 100;
        return SupportedMinionLimit(DarkMonarchLimit(definition.Family, value, playerStats), skillStats);
    }

    static int SupportedMinionLimit(long limit, IReadOnlyDictionary<uint, int> skillStats) =>
        (int)Math.Clamp(limit * (100L + GolemStat(skillStats, Stat.SupportUndeadArmyMinionMaximumCountPlusPctFinal)) / 100, 0, 200);

    static void ApplySocketedGhastlyEyeBonuses(IEnumerable<Item> active, ItemCatalog catalog, Dictionary<uint, int> sources)
    {
        long spectres = 0, impale = 0;
        foreach (var item in active)
        {
            int socketed = item.Sockets.Count(socket => socket is { Colour: SocketColour.Abyss, Gem: { } jewel }
                && catalog.ByHash(jewel.TypeHash)?.Id == "Metadata/Items/Jewels/JewelAbyssSummoner");
            if (socketed == 0) continue;
            var itemStats = PlainItemSum(item, catalog.Mods);
            spectres += (long)socketed * GolemStat(itemStats, Stat.AdditionalSpectresPerGhastlyEyeJewel);
            impale += (long)socketed * GolemStat(itemStats, Stat.MinionChanceToImpaleOnAttackHitPctPerSocketedGhastlyJewel);
        }
        if (spectres != 0)
            sources[Stat.BaseNumberOfSpectresAllowed] = ActorScale.Saturate(
                (long)sources.GetValueOrDefault(Stat.BaseNumberOfSpectresAllowed) + spectres);
        if (impale != 0)
            sources[Stat.MinionChanceToImpaleOnAttackHitPct] = ActorScale.Saturate(
                (long)sources.GetValueOrDefault(Stat.MinionChanceToImpaleOnAttackHitPct) + impale);
    }

    static bool PermanentSkeletonWarriors(SummonCatalog.Definition definition, IReadOnlyDictionary<uint, int> owner) =>
        definition.Id == "SummonSkeletons" && GolemStat(owner, Stat.SkeletonsArePermanentMinions) != 0;

    internal static void EnsureSpectreBankLoaded(Player player)
    {
        if (player.Summons.SpectreBankLoaded) return;
        player.Summons.SpectreBankLoaded = true;
        var saved = CharacterSaves.Load(CharacterName(player))?.ModuleData?.GetValueOrDefault(SpectreBankSaveKey);
        if (saved is null) return;
        try
        {
            foreach (string id in JsonSerializer.Deserialize<string[]>(saved) ?? [])
                if (!string.IsNullOrWhiteSpace(id) && !player.Summons.SpectreBank.Contains(id, StringComparer.Ordinal))
                    player.Summons.SpectreBank.Add(id);
        }
        catch (JsonException) { }
    }

    internal static void RememberSpectreVariety(Player player, string varietyId)
    {
        if (string.IsNullOrWhiteSpace(varietyId)) return;
        EnsureSpectreBankLoaded(player);
        var bank = player.Summons.SpectreBank;
        int idx = bank.FindIndex(id => id.Equals(varietyId, StringComparison.Ordinal));
        if (idx == 0) return;
        if (idx > 0) bank.RemoveAt(idx);
        bank.Insert(0, varietyId);
        while (bank.Count > MaxSpectreBankCount) bank.RemoveAt(bank.Count - 1);
        string text = JsonSerializer.Serialize(bank);
        string character = CharacterName(player);
        Saves.Atomically(() => CharacterSaves.ChangeModuleData(character, SpectreBankSaveKey, _ => text));
    }

    static double SummonDuration(Player player, SummonCatalog.Definition definition, SupportedGem gem)
    {
        IReadOnlyDictionary<uint, int>? playerStats = null;
        int spectreDuration = 0;
        if (definition.Family == "Spectre")
        {
            playerStats = CharacterPlayerStats(player);
            spectreDuration = Math.Max(GolemStat(playerStats, Stat.SpectresHaveBaseDurationMs),
                GolemStat(gem.Stats, Stat.SpectresHaveBaseDurationMs));
        }
        if (spectreDuration <= 0 && (definition.Persistent || definition.Family == ItemHarbingerFamily
            || GolemStat(gem.Stats, Stat.InfiniteMinionDuration) != 0)) return double.PositiveInfinity;
        bool secondary = definition.Family == "Absolution" || definition.Family.StartsWith("Dominating", StringComparison.Ordinal);
        int ms = definition.Family == "Spectre" ? spectreDuration
            : GolemStat(gem.Stats, secondary ? Stat.BaseSecondarySkillEffectDuration : Stat.BaseSkillEffectDuration);
        playerStats ??= CharacterPlayerStats(player);
        uint familyDuration = definition.Family switch
        {
            "Skeleton" or "VaalSkeleton" => Stat.SkeletonDurationPlusPct,
            "RagingSpirit" => Stat.SummonedRagingSpiritDurationPlusPct,
            _ => 0,
        };
        int Read(uint key) => key == SkillDurationModifiers.IncreasedDuration && familyDuration != 0
            ? ActorScale.Saturate((long)GolemStat(playerStats, key) + GolemStat(playerStats, familyDuration))
            : GolemStat(playerStats, key);
        double seconds = SkillDurationModifiers.Seconds(ms, Read, n => GolemStat(gem.Stats, n),
            DurationKinds(player, gem.Effect) | (secondary ? SkillDurationModifiers.Kinds.Secondary : 0));
        if (definition.Family is "AnimatedWeapon" or "VaalAnimatedWeapon")
            seconds *= Math.Max(0, 100d + GolemStat(playerStats, Stat.JorrhastsBlacksteelAnimateWeaponDurationPlusPctFinal)) / 100d;
        return seconds;
    }

    static bool SummonNeedsCorpse(SummonCatalog.Definition definition, IReadOnlyDictionary<uint, int> playerStats) =>
        definition.Corpse && (definition.Family != "Zombie" || GolemStat(playerStats, Stat.RaiseZombieDoesNotUseCorpses) == 0);

    static SpawnedMonster? SummonCorpse(Player player, (int X, int Y) aim, uint target, double radius = 15,
        bool cannotUseSpectreCorpses = false, SpawnedMonster? preferred = null)
    {
        bool Allowed(SpawnedMonster m) => m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered
            && CorpseTypeAllowsUse(m, minionSkill: true, cannotUseSpectreCorpses) && m.Rarity != MonsterRarity.Unique;
        // The corpse the cast was planned on, while it is still present and usable; otherwise the lookup runs as it always did.
        if (preferred is not null && Allowed(preferred)
            && (preferred.ObjectId != 0
                ? player.Area.Monsters.TryGetValue(preferred.ObjectId, out var live) && ReferenceEquals(live, preferred)
                : player.Area.Monsters.Values.Any(m => ReferenceEquals(m, preferred))))
            return preferred;
        if (target != 0 && player.Area.Monsters.GetValueOrDefault(target) is { } selected && Allowed(selected)) return selected;
        return player.Area.Monsters.Values.Where(Allowed).Where(m => Dist(m.Walk.Position(AreaTime(player)), aim) <= radius)
            .MinBy(m => Dist(m.Walk.Position(AreaTime(player)), aim));
    }

    // A raise that needs a corpse (Raise Zombie, Raise Spectre) with none in reach is refused when the cast is planned,
    // before any cost is paid. It asks what SummonFighterCore asks, so the two cannot disagree.
    // False when the raise is refused. The corpse it found is handed on to the release, which prefers it.
    static bool TryPlanSummonCorpse(Player player, ushort skillId, int effect, (int X, int Y) aim, uint target,
        out SpawnedMonster? corpse)
    {
        corpse = null;
        if (SummonData.Find(SkillsFor(player).EffectId(effect)) is not { Corpse: true } definition
            || IsTotemSkill(player, skillId)) return true;
        using var scope = new PlayerStatScope(player, fresh: true);
        var gem = SupportedSkill(player, skillId);
        if (!SummonNeedsCorpse(definition, new Dictionary<uint, int>(CharacterPlayerStats(player)))) return true;
        corpse = SummonCorpse(player, aim, target, cannotUseSpectreCorpses: GolemStat(gem.Stats, Stat.CannotUseSpectreCorpses) != 0);
        return corpse is not null;
    }

    // The living actor uses the raised body's row, including the old saves that recorded the ordinary monster's row.
    static MonsterCatalog.Variety RaisedSpectreVariety(MonsterCatalog.Variety corpse)
    {
        int row = SpectreOverrides.GetValueOrDefault(corpse.Row, corpse.Row);
        return (uint)row < (uint)Monsters.All.Count ? Monsters.All[row] : corpse;
    }

    internal static int SummonCount(SummonCatalog.Definition definition, SupportedGem gem, Random random)
    {
        if (definition.Family == "Skeleton")
            return Math.Max(1, new[] { Stat.NumberOfMeleeSkeletonsToSummon, Stat.NumberOfArcherSkeletonsToSummon,
                Stat.NumberOfMageSkeletonsToSummon }.Sum(n => GolemStat(gem.Stats, n)));
        if (definition.Family == "RagingSpirit")
            return random.Next(100) < Math.Clamp(GolemStat(gem.Stats, Stat.SummonedRagingSpiritChanceToSpawnAdditionalMinionPct), 0, 100) ? 2 : 1;
        if (definition.Family == "Hiveborn")
            return Math.Max(1, GolemStat(gem.Stats, Stat.GraftSkillTulSummonNumberOfDemonsToSummon));
        return Math.Max(1, GolemStat(gem.Stats, Stat.NumberOfMonstersToSummon));
    }

    internal static Task<bool> CastSummon(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target, int recovery, Func<bool> current,
        bool triggered = false, bool playerConsumesCorpse = true, SpawnedMonster? plannedCorpse = null) =>
        RepeatSummonCast(player, source, recovery, current, triggered, async () =>
        {
            var gem = SupportedSkill(player, source);
            int count = SummonCount(definition, gem, AreaRng(player));
            if (definition.Family == "Skeleton" && GolemStat(gem.Stats, Stat.NumberOfMeleeSkeletonsToSummon) > 0)
            {
                var owner = CharacterPlayerStats(player);
                if (PermanentSkeletonWarriors(definition, owner))
                    count = 1;
                else if (GolemStat(owner, Stat.SummonSkeletonsNumAdditionalWarriorSkeletons) is int extra and not 0)
                    count = Math.Min(Math.Max(1, ActorScale.Add(Stat.NumberOfMeleeSkeletonsToSummon, count, extra)), SummonLimit(player, definition, gem));
            }
            bool released = false;
            for (int i = 0; i < count && current(); i++)
            {
                if (definition.Family == "Reaper" && definition.Id != "SummonReaperAltX")
                    if (player.Summons.Fighters.Values.FirstOrDefault(g => g.Source == source && g.Definition.Id == definition.Id && !g.Monster.Dead) is { } reaper
                        && reaper.Abilities.FirstOrDefault(a => a.Effect == 10584) is { } ultimate)
                    {
                        GolemReserveLocked(player, reaper, ultimate, target, player.Area.Monsters.GetValueOrDefault(target), aim, AreaTime(player));
                        return true;
                    }
                if (definition.Id is "BlinkArrow" or "BlinkArrowAltX" or "BlinkArrowAltY" or "BlinkArrowRoyale")
                {
                    var origin = player.Walk.Position(AreaTime(player));
                    var landing = MovementLanding(player, gem.Effect, origin, aim);
                    bool AllowClone()
                    {
                        var stats = StatSourceValues(player);
                        return MinionFamilyAllowed(stats.GetValueOrDefault, definition.Family);
                    }
                    uint clone = 0;
                    if (AllowClone())
                    {
                        clone = await SummonFighterCore(player, source, definition,
                            origin, target, current: current, triggered: triggered, playerConsumesCorpse: playerConsumesCorpse);
                        if (clone == 0 && AllowClone()) return false;
                    }
                    if (await CommitTravelLanding(player, landing,
                            () => current() && (clone == 0 || player.Summons.Fighters.ContainsKey(clone))) is null) return false;
                    released = true;
                }
                else
                {
                    uint summoned = await SummonFighterCore(player, source, definition, aim, target,
                        current: current, triggered: triggered, playerConsumesCorpse: playerConsumesCorpse, plannedCorpse: plannedCorpse);
                    if (summoned == 0) continue;
                    released = true;
                    if (definition.Family == "AnimatedWeapon" && current()
                        && player.Summons.Fighters.TryGetValue(summoned, out var original) && original.Equipment.Count > 0)
                    {
                        int copies = Math.Min(Math.Max(0, SummonLimit(player, definition, gem) - 1), Math.Max(0,
                            PlayerStat(player, Stat.AnimateWeaponNumberOfAdditionalCopies)));
                        if (copies == 0) continue;
                        var copiedWeapon = new AnimatedSource(0, null, new(original.Equipment));
                        for (int copy = 0; copy < copies && current(); copy++)
                            if (await SummonFighterCore(player, source, definition, aim,
                                current: current, triggered: triggered, playerConsumesCorpse: false, preparedEquipment: copiedWeapon) == 0)
                                break;
                    }
                }
            }
            if (released) SaveSummons(player);
            return released;
        }, firstReleaseDelay: !triggered && definition.Family == "Clone" ? CloneArrowLandingDelay(player, source) : null);

    public static Task<uint> SummonFighter(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target = 0,
        string? restoredVariety = null, double life = 1, Func<bool>? current = null,
        Dictionary<byte, Item>? restoredEquipment = null, double? remaining = null,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null, bool triggered = false, MonsterModSet? inheritedMods = null,
        MonsterRarity rarity = MonsterRarity.Normal, bool arrival = false) =>
        SummonFighterCore(player, source, definition, aim, target, restoredVariety, life,
            current, restoredEquipment, remaining, resources, legacyArrival, replacingOld, triggered, inheritedMods: inheritedMods, rarity: rarity, arrival: arrival);

    static async Task<uint> SummonFighterCore(Player player,
        ushort source, SummonCatalog.Definition definition, (int X, int Y) aim, uint target = 0,
        string? restoredVariety = null, double life = 1, Func<bool>? current = null,
        Dictionary<byte, Item>? restoredEquipment = null, double? remaining = null,
        GolemState.MinionResourceSnapshot? resources = null, bool legacyArrival = false,
        GolemState.Golem? replacingOld = null, bool triggered = false, SpawnedMonster? consumedCorpse = null, bool playerConsumesCorpse = true, MonsterModSet? inheritedMods = null,
        MonsterRarity rarity = MonsterRarity.Normal, IReadOnlyDictionary<uint, int>? retainedStats = null,
        AnimatedSource? preparedEquipment = null, GolemState.Golem? reformingSpectre = null,
        SpectreChildSummon? spectreChild = null, bool arrival = false, SpawnedMonster? plannedCorpse = null)
    {
        if (replacingOld is null)
        {
            string? prePath = restoredVariety ?? (definition.Variety.Length > 0 && definition.Id != "SummonSkeletonsAltY"
                && definition.Id != "SummonHolyRelicAltX" && definition.Family != "Clone" ? definition.Variety : null);
            if (prePath is not null && Monsters.Find(prePath) is { Supported: true } preVariety)
            {
                var preGem = SupportedSkill(player, source);
                if (preGem.RequirementsMet && SkillsFor(player).EffectId(preGem.Effect) == definition.Id)
                {
                    if (definition.Family == "Phantasm") preGem = PhantasmGem(player, preGem);
                    if (definition.Family == LivingLightningFamily && LivingLightningDefinition(player, preGem) is not null)
                        preGem = LivingLightningGem(player, preGem);
                    if (retainedStats is not null) preGem = preGem with { Stats = MergeSummonStats(preGem.Stats, retainedStats) };
                    int preActorLevel = GolemStat(preGem.Stats, definition.Family == "Spectre" ? Stat.RaisedSpectreLevel : Stat.BaseDisplayMinionActorLevel);
                    if (preActorLevel <= 0 && (definition.Family.StartsWith("Dominating", StringComparison.Ordinal) || definition.Family == "VaalDomination"))
                        preActorLevel = SkillsFor(player).PlayerLevelReq(preGem.Effect, preGem.Level);
                    byte preLevel = spectreChild?.Parent.Monster.Level ?? (byte)Math.Clamp(preActorLevel, 1, 100);
                    await EnsureMonsterTypeSent(player, preVariety, preLevel);
                    if (!CurrentGameWork || player.Closed || player.LeavingArea) return 0;
                }
            }
        }
        SupportedGem gem;
        Dictionary<uint, int> playerStats;
        FreshSummonAdmission? fresh;
        SummonReplacementAdmission? replacement;
        GemSourceLifetimes.SourceLifetime? gemSource;
        using (ProjectileMutation(player, advance: false, phase: "summon replacement preparation"))
        using (new PlayerStatScope(player, fresh: true))
        {
            gem = SupportedSkill(player, source);
            playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            gemSource = gem.RequirementsMet
                ? CaptureGemSourceLifetimeLocked(player, source, gem.Effect) : null;
            fresh = replacingOld is null
                ? PrepareFreshSummonLocked(player, source, gem, playerStats, gemSource) : null;
            replacement = replacingOld is null ? null
                : PrepareReplacementLocked(player, SummonReplacementFamily.Summon, replacingOld, source,
                    gem.Effect, gem, playerStats, AreaTime(player));
            if (replacingOld is not null && replacement is null) return 0;
        }
        if (!gem.RequirementsMet || gemSource is null || replacingOld is null && fresh is null
            || SkillsFor(player).EffectId(gem.Effect) != definition.Id
            || !CanHaveMinion(playerStats, SummonFamily(definition.Family)) || !DarkMonarchAllows(definition.Family, playerStats)) return 0;
        if (PermanentSkeletonWarriors(definition, playerStats))
            definition = definition with { Persistent = true, Follows = true };
        if (definition.Family == "Phantasm") gem = PhantasmGem(player, gem);
        if (definition.Family == LivingLightningFamily)
        {
            if (LivingLightningDefinition(player, gem) is null) return 0;
            gem = LivingLightningGem(player, gem);
        }
        inheritedMods ??= replacingOld?.InheritedMods;
        if (replacingOld is not null) rarity = replacingOld.Monster.Rarity;
        // A replacement keeps the stats its body was given at summon (a Vaal ascension's upgrade).
        retainedStats ??= replacingOld?.RetainedSkillStats;
        if (retainedStats is not null) gem = gem with { Stats = MergeSummonStats(gem.Stats, retainedStats) };
        int limit = spectreChild?.Plan.Limit ?? SummonLimit(definition, gem.Stats, playerStats);
        if (limit < 1) return 0;
        if (resources is { Life: 0 } || legacyArrival && (!double.IsFinite(life) || life <= 0)) return 0;
        SpawnedMonster? corpse = consumedCorpse;
        MonsterCatalog.Variety? corpseVariety = null;
        AnimatedSource? animated = null;
        string path = restoredVariety ?? definition.Variety;
        if (restoredVariety is null && SummonNeedsCorpse(definition, playerStats))
        {
            corpse = SummonCorpse(player, aim, target, cannotUseSpectreCorpses: GolemStat(gem.Stats, Stat.CannotUseSpectreCorpses) != 0,
                preferred: plannedCorpse);
            if (corpse is null) return 0;
            if (definition.Family == "Spectre") corpseVariety = corpse.Variety;
            aim = corpse.Walk.Position(AreaTime(player));
        }
        if (definition.Family is "AnimatedWeapon" or "VaalAnimatedWeapon" or "AnimatedGuardian" or "Clone" or "HolyArmament")
        {
            animated = preparedEquipment ?? AnimatedItems(player, definition, gem, aim, target, restoredEquipment);
            if (animated is null) return 0;
            if (animated.Ground is { } item) aim = (item.GridX, item.GridY);
        }
        if (definition.Id == "SummonSkeletonsAltY") path = $"Metadata/Monsters/RaisedSkeletons/RaisedSkeletonSpellcaster{AreaRng(player).Next(1, 4)}Jewel";
        if (definition.Id == "SummonHolyRelicAltX") path = "Metadata/Monsters/Relic/HolyRelicSkull";
        if (definition.Family == "Clone" && restoredVariety is null)
        {
            string variant = definition.Id.EndsWith("AltX", StringComparison.Ordinal) ? "RainOfArrows"
                : definition.Id.EndsWith("AltY", StringComparison.Ordinal) ? "ElementalShot" : "";
            // The Duelist base row is DuelistCloneImmobile_ (MonsterVarieties 3964, trailing DAT disambiguation
            // underscore); the other classes' base rows have no suffix.
            path = "Metadata/Monsters/Clone/" + (player.Character.ClassRow switch { 0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar" })
                + "CloneImmobile" + variant + (player.Character.ClassRow switch { 2 when variant.Length > 0 => "__", 4 when variant.Length == 0 => "_", _ => "" });
        }
        var variety = corpseVariety ?? Monsters.Find(path);
        if (definition.Family == "Spectre" && variety is not null)
        {
            variety = RaisedSpectreVariety(variety);
            path = variety.Id;
        }
        if (variety is not { Supported: true }) return 0;
        int actorLevel = GolemStat(gem.Stats, definition.Family == "Spectre" ? Stat.RaisedSpectreLevel : Stat.BaseDisplayMinionActorLevel);
        if (actorLevel <= 0 && (definition.Family.StartsWith("Dominating", StringComparison.Ordinal)
            || definition.Family is "VaalDomination" or "HolyArmament" or "Spider" or SpectralSpiritFamily || definition.Id == GuardianWeaponEffect))
            actorLevel = SkillsFor(player).PlayerLevelReq(gem.Effect, gem.Level);
        byte level = spectreChild?.Parent.Monster.Level ?? (byte)Math.Clamp(actorLevel, 1, 100);
        if (inheritedMods is null && (definition.Family is "RagingSpirit" or "Spider"
            || definition.Family == "Spectre" && ItemisedCorpses.Bodies.Contains(variety.Row) || spectreChild is not null))
            inheritedMods = MonsterModRolls.Create(MonsterModRolls.Species(variety.SpeciesModsOrEmpty, AreaRng(player)),
                [], MonsterRarity.Normal, level);
        var native = new GolemCatalog.Definition(gem.Effect, definition.Id, definition.Family, path, 0, [], false, false, false);
        double freshMeat = remaining is null && replacement is null && restoredVariety is null && resources is null && !legacyArrival
            && GolemStat(gem.Stats, FreshMeatDurationPercent) > 0
            ? FreshMeatSeconds(gem.Stats, SummonDuration(player, definition, gem)) : 0;
        var fighter = new GolemState.Golem(0, null!, native, source,
            definition.Family == "Spectre" ? gemSource : null)
        { GemLevel = gem.Level, Quality = gem.Quality, Own = new(gem.Stats), Player = new(playerStats), Limit = limit,
            SummonedAt = replacement?.SummonedAt ?? reformingSpectre?.SummonedAt ?? AreaTime(player),
            RestoredInArea = replacement?.Old.RestoredInArea ?? reformingSpectre?.RestoredInArea ?? arrival,
            FreshMeatUntil = replacingOld?.FreshMeatUntil ?? (freshMeat > 0 ? AreaTime(player) + freshMeat : 0),
            InheritedMods = inheritedMods ?? MonsterModSet.None, RetainedSkillStats = retainedStats is null ? [] : new(retainedStats),
            SourceIdentity = gem.SourceGem is null ? null : PlayerInventories.GemIdentity(gem.SourceGem) };
        if (animated is not null)
        {
            fighter.Equipment = animated.Equipment;
            var items = definition.Family == "Clone" ? fighter.Equipment.Where(p => p.Key is 2 or 3).Select(p => p.Value) : fighter.Equipment.Values;
            fighter.Gear = ItemModStats.Sum(items, ItemsFor(player).Mods);
            if (fighter.Equipment.GetValueOrDefault((byte)2) is { } item) fighter.Weapon = WeaponIn(player, (item, ItemsFor(player).ByHash(item.TypeHash)));
        }
        CopySkeletonWeapon(player, fighter, variety);
        fighter.Utility = MinionOfferingStats(player, AreaTime(player));
        var mods = GolemModifiers(player, fighter, playerStats);
        var pools = GolemPools.Calculate(Monsters, variety, level, fighter, playerStats, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint wireLife = pools.Fatal ? 1 : replacement is { } liveReplacement
            ? Math.Min(liveReplacement.Resources.Life, pools.MaximumLife)
            : resources is { } saved
            ? Math.Min(saved.Life, pools.MaximumLife)
            : (uint)Math.Clamp(pools.MaximumLife *
                (double.IsFinite(life) ? Math.Clamp(life, 0, 1) : 0), 0, pools.MaximumLife);
        uint energyShield = replacement is { } liveShield
            ? Math.Min(liveShield.Resources.EnergyShield, maximumEnergyShield)
            : resources is { } savedShield
            ? Math.Min(savedShield.EnergyShield.Current, maximumEnergyShield)
            : legacyArrival ? 0 : maximumEnergyShield;
        if (!pools.Fatal)
        {
            fighter.Abilities = GolemAbilities(player, variety, level, fighter, playerStats);
            fighter.Skills = GolemNativeSkills(player, level, fighter.Abilities);
        }
        int bodySize = Math.Clamp(variety.ObjectSize - 1, 0, 127);
        if (!player.Layout.WalkableFor(aim.X, aim.Y, bodySize) || player.Barrier.Contains(aim)) aim = NextToPlayer(player, bodySize);
        var consumed = corpse;
        bool SameLimit(GolemState.Golem g) => g.Definition.Kind == definition.Family
            && (!g.Monster.Dead || double.IsFinite(g.ReviveAt))
            && (spectreChild is null || g.Monster.SummonerId == spectreChild.Parent.Id)
            && (definition.Family != "Clone" || player.Summons.Definitions[g.Id].LimitStat == definition.LimitStat);
        bool damageImmune = definition.Family is "HolyArmament" or "Spider" or LivingLightningFamily or SpectralSpiritFamily;
        // Official creates of these families carry flag 0x02 and base data: owner, the corpse it rose from (if any), itself.
        // The others (spectres, animated weapons, ...) are sent differently and keep the plain create.
        Func<uint, IReadOnlyList<(uint Key, byte Kind, uint Value)>>? baseData = !OfficialOwnedCreate(definition.Family, definition.Id) ? null
            : self => consumed is { ObjectId: not 0 }
                ? [(MonsterObject.OwnerKey, 0, player.Id), (MonsterObject.SourceKey, 0, consumed.ObjectId), (MonsterObject.SelfKey, 1, self)]
                : [(MonsterObject.OwnerKey, 0, player.Id), (MonsterObject.SelfKey, 1, self)];
        uint id = await SpawnMonster(player, variety, level, rarity, aim.X, aim.Y,
            friendly: true, mods: mods, unrisen: false,
            extraStats: damageImmune ? [.. MinionStats, (Stat.BaseCannotBeDamaged, 1)]
                : LivingZombieCorpse(fighter, playerStats) ? [.. MinionStats, (Stat.IsUsableAsCorpseWhenAlive, 1)] : MinionStats,
            ownedSkills: fighter.Skills, ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield,
            damagedLife: wireLife,
            ownedInitialDeath: pools.Fatal,
            ownedLooks: SummonLooks(fighter),
            ownedBaseData: baseData,
            ownedAdmission: () => (replacement is null || SummonReplacementCurrentLocked(player, replacement))
                && (animated?.GuardianGear is not { } guardian || AnimatedGuardianGearCurrentLocked(player, guardian))
                && ConsumeAnimatedSourceLocked(player, animated),
            ownedCurrent: () => player.PlayerLife > 0 && (current?.Invoke() ?? true) && (consumed is null || !consumed.CorpseConsumed)
                && (reformingSpectre is null || CanReformSpectreLocked(player, reformingSpectre, AreaTime(player)))
                && (replacement is { } admission
                    ? SummonReplacementCurrentLocked(player, admission)
                    : fresh is { } captured && FreshSummonCurrentLocked(player, captured, arrival: arrival)
                        && GemSkillInstanceDisabled(player, gem.Effect, source, triggered: triggered || restoredVariety is not null) is null),
            owned: (id, monster) =>
            {
                if (consumed is not null)
                {
                    ConsumeSummonCorpseLocked(player, consumed, playerConsumesCorpse);
                    if (definition == ItemSpider)
                        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(consumed.ObjectId),
                            "Raise Spiders consumes a corpse", WorldId: consumed.ObjectId, RemovesObject: true));
                    // Official: any other raised corpse leaves in the create's frame, after the create. Desecrate/created
                    // corpses also require 0x02bd because AreaDelta does not poll-remove consumed corpses automatically.
                    else if (consumed.ObjectId != 0)
                        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(consumed.ObjectId),
                            $"0x02bd raised corpse 0x{consumed.ObjectId:x} removed", WorldId: consumed.ObjectId, RemovesObject: true));
                }
                if (animated?.Ground is not null)
                    player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(animated.Id), "animated item consumed", WorldId: animated.Id, RemovesObject: true));
                if (animated?.BladeSource is not null)
                {
                    bool projectileBlade = player.UntrackProjectileObject(animated.Id);
                    player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(animated.Id), "lingering blade animated",
                        WorldId: projectileBlade ? animated.Id : null, RemovesObject: projectileBlade));
                }
                if (definition.Family == "AnimatedGuardian") player.Summons.GuardianEquipment = new(fighter.Equipment);
                double committed = AreaTime(player);
                double? reformUntil = null;
                if (reformingSpectre is not null)
                {
                    reformUntil = player.Summons.Until[reformingSpectre.Id];
                    RemoveSummonLocked(player, reformingSpectre, committed, reconcileBlessing: false);
                }
                while (replacement is null && reformingSpectre is null && player.Summons.Fighters.Values.Count(SameLimit) >= limit)
                {
                    var oldest = player.Summons.Fighters.Values.Where(SameLimit).MinBy(g => g.SummonedAt)!;
                    if (!oldest.Monster.Dead && oldest.Monster.Life > 0) OwnedMinionDeathEffectsLocked(player, oldest, committed);
                    RemoveSummonLocked(player, oldest, committed, reconcileBlessing: false, dies: true);
                }
                SummonReplacementResources? committedResources = replacement is null
                    ? null : CaptureReplacementResources(replacement.Old, committed);
                var retainedAura = replacement is null ? null
                    : player.Minions.All.FirstOrDefault(minion => minion.Id == replacement.Old.Id)?.OwnerAura;
                fighter.Id = id; fighter.Monster = monster;
                monster.DamageImmune = damageImmune;
                if (replacement is not null)
                    TransferIncomingReplacementStateLocked(replacement.Old, fighter, committed);
                if (replacement is not null) RetireReplacementLocked(player, replacement, committed);
                fighter.SummonedAt = replacement?.SummonedAt ?? reformingSpectre?.SummonedAt ?? committed;
                if (freshMeat > 0) fighter.FreshMeatUntil = committed + freshMeat;
                fighter.LastStep = replacement?.LastStep ?? committed;
                fighter.ShieldPool.SetMaximum(maximumEnergyShield,
                    refill: committedResources is null && resources is null && !legacyArrival);
                if (committedResources is { } liveResources)
                {
                    fighter.Regeneration = SavedLifeRegeneration(liveResources.LifeRegeneration);
                    fighter.ShieldPool.Restore(liveResources.EnergyShieldCapture, committed);
                }
                else if (resources is { } savedResources)
                {
                    fighter.Regeneration = SavedLifeRegeneration(savedResources.LifeRegeneration);
                    fighter.ShieldPool.Restore(savedResources.EnergyShield, committed);
                }
                else if (legacyArrival) fighter.ShieldPool.Interrupt(committed);
                monster.Mods = mods;
                if (spectreChild is not null)
                {
                    monster.SummonerId = spectreChild.Parent.Id;
                    SpectreChildParents.AddOrUpdate(fighter, spectreChild.Parent);
                }
                player.Summons.Fighters.Add(id, fighter); player.Summons.Definitions[id] = definition;
                player.Summons.Until[id] = remaining is double duration ? committed + duration
                    : replacement?.Until ?? reformUntil ?? committed + SummonDuration(player, definition, gem);
                player.Minions.Add(new(id, monster, definition.Id, definition.Family)
                { Until = player.Summons.Until[id], OwnerAura = RollDivineSentinelAura(player, fighter, retainedAura) });
                PresentPhantasmalMightLocked(player, fighter, playerStats, committed);
                monster.NoRewards = true; monster.Aggro = true;
                if (definition.Family == ItemHarbingerFamily || SpectreGroundHex(definition.Family) is not null)
                    monster.DamageImmune = true;
                if (definition.Id == "RaiseZombieAltY" && fighter.Abilities.FirstOrDefault(a => a.Effect == 7133) is { } fall)
                    GolemReserveLocked(player, fighter, fall, 0, null, aim, AreaTime(player));
                player.Minions.StatRevision++; MarkPlayerStats(player, PlayerStatStep.Golems);
                if (!pools.Fatal) PresentTransferredGolemDotBuffsLocked(player, fighter, committed);
                if (pools.Fatal) GolemDieLocked(player, fighter, AreaTime(player), emitLife: false, canReform: false);
                else
                {
                    ReconcileGuardianBlessingLocked(player);
                    if (definition.Family == "Spectre") ApplySpectreBuffVisualLocked(player, id, fighter);
                }
            });
        if (id != 0 && definition.Family == "Spectre" && !pools.Fatal
            && !ItemisedCorpses.Bodies.Contains(variety.Row)
            && (corpseVariety is null || !ItemisedCorpses.Bodies.Contains(corpseVariety.Row)))
            RememberSpectreVariety(player, corpseVariety?.Id ?? variety.Id);
        if (id != 0 && !arrival && replacingOld is null && spectreChild is null && !pools.Fatal)
            await UseParentFlasksOnSummon(player, fighter);
        return id;
    }

    // Creating and refreshing a summon share one receipt. A proxy's consumption cannot recharge its owner's flask.
    static void ConsumeSummonCorpseLocked(Player player, SpawnedMonster corpse, bool playerConsumesCorpse = true)
    {
        if (corpse.CorpseConsumed) return;
        corpse.CorpseConsumed = true;
        if (playerConsumesCorpse) NoteCorpseConsumedLocked(player, corpse);
    }

    /// <summary>How long the body of a summon that died stays before it is removed: 21 frames.</summary>
    public const double SummonBodySeconds = 21 * Program.TickSeconds;

    /// <summary>A summon that ended in play (its time is up, it was killed, its owner died, a new one replaced it past the
    /// limit) dies where it stands: its life goes to 0 (not again when it was killed, which showed it already), it is not a
    /// corpse anyone can use, and RemoveMinionBodiesLocked takes the body away later, once. Under the Monsters lock.</summary>
    static void SummonBodyDiesLocked(Player player, GolemState.Golem golem, bool shown, double now)
    {
        var m = golem.Monster;
        m.CorpseConsumed = true;
        SetStats(player, golem.Id, [(Stat.HideMiniLifeBar, 1)]);
        if (!shown)
            player.Minions.OutputQueue.Add(new([], "summon life 0", golem.Id, m, PlayerMinions.OutputKind.Life));
        if (!player.Minions.Bodies.Exists(b => b.Id == golem.Id))
            player.Minions.Bodies.Add((golem.Id, m, now + SummonBodySeconds));
    }

    // dies: see SummonBodyDiesLocked. Not for one already dead and waiting to be re-formed (a Spectre with a finite ReviveAt).
    static void RemoveSummonLocked(Player player, GolemState.Golem fighter, double now, bool reconcileBlessing = true, bool dies = false)
    {
        bool diesHere = dies && !double.IsFinite(fighter.ReviveAt);   // each summon by its own ReviveAt; its children by theirs
        if (fighter.Definition.Kind == "Phantasm")
            player.Area.Buffs.Clear(player.Id, PhantasmalMightBuff, fighter.Id);
        if (fighter.Definition.Kind == "Spectre")
        {
            ClearTurtleBoonLocked(player, fighter);
            ClearSpectreTimedPayloadsLocked(player, fighter);
            foreach (var child in player.Summons.Fighters.Values.Where(child => child.Monster.SummonerId == fighter.Id).ToArray())
                RemoveSummonLocked(player, child, now, reconcileBlessing: false, dies);
        }
        ClearEffigyHexesLocked(fighter);
        if (fighter.Definition.Kind == ItemHarbingerFamily) ClearItemHarbingerLocked(player, fighter);
        SpectreChildParents.Remove(fighter);
        player.Summons.Fighters.Remove(fighter.Id); player.Summons.Until.Remove(fighter.Id);
        player.Summons.Definitions.Remove(fighter.Id); player.Summons.Weapons.Remove(fighter.Id);
        RemoveGolemBodyLocked(player, fighter, now, reconcileBlessing, diesHere);
        player.Minions.StatRevision++;
    }

    static bool CanReformSpectreLocked(Player player, GolemState.Golem spectre, double now)
    {
        if (!GolemCurrent(player, spectre) || !spectre.Monster.Dead || now < spectre.ReviveAt
            || now >= player.Summons.Until[spectre.Id]
            || !GemSourceLifetimeCurrentLocked(player, spectre.Source, spectre.Definition.Effect, spectre.GemSource)) return false;
        var gem = SupportedSkill(player, spectre.Source);
        return gem.RequirementsMet && SummonLimit(player, player.Summons.Definitions[spectre.Id], gem) > 0
            && GemSkillInstanceDisabled(player, gem.Effect, spectre.Source, triggered: true) is null;
    }

    static bool RefreshSpectreLimitLocked(Player player, IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        var state = player.Summons;
        var spectres = state.Fighters.Values.Where(g => g.Definition.Kind == "Spectre"
            && (!g.Monster.Dead || double.IsFinite(g.ReviveAt)) && now < state.Until[g.Id]).ToArray();
        if (spectres.Length == 0) return false;
        foreach (var spectre in spectres)
            spectre.Limit = SummonLimit(state.Definitions[spectre.Id], spectre.Own, playerStats);
        int capacity = spectres.Max(g => g.Limit);
        if (spectres.Length <= capacity) return false;
        foreach (var excess in spectres.OrderBy(g => g.SummonedAt).Take(spectres.Length - capacity))
            RemoveSummonLocked(player, excess, now);
        return true;
    }

    static bool RefreshSupportedSummonLimitsLocked(Player player, IReadOnlyDictionary<uint, int> stats, double now)
    {
        var state = player.Summons;
        var current = DarkMonarchSelection(stats);
        bool HadSupportedLimit(GolemState.Golem fighter) => GolemStat(fighter.Own, Stat.SupportUndeadArmyMinionMaximumCountPlusPctFinal) != 0;
        if (current is null && !state.Fighters.Values.Any(fighter => HadSupportedLimit(fighter)
            || DarkMonarchSelection(fighter.Player) is not null))
            return false;
        var families = state.Fighters.Values.Where(fighter => !fighter.Monster.Dead
                && now < state.Until[fighter.Id] && fighter.Definition.Kind != "Spectre")
            .GroupBy(fighter => fighter.Definition.Kind);
        bool changed = false;
        foreach (var family in families)
        {
            string type = CanonicalMinionFamily(family.Key);
            bool doubledFamily = type == current || family.Any(fighter => type == DarkMonarchSelection(fighter.Player));
            if (!doubledFamily && !family.Any(HadSupportedLimit)) continue;
            foreach (var fighter in family)
            {
                var gem = SupportedSkill(player, fighter.Source);
                if (fighter.Definition.Kind == "Phantasm") gem = PhantasmGem(player, gem);
                if (fighter.Definition.Kind == LivingLightningFamily && LivingLightningDefinition(player, gem) is not null)
                    gem = LivingLightningGem(player, gem);
                fighter.Limit = SummonLimit(state.Definitions[fighter.Id], gem.Stats, stats);
            }
            int surplus = family.Count() - family.Max(fighter => fighter.Limit);
            foreach (var fighter in family.OrderBy(fighter => fighter.SummonedAt).Take(surplus))
            {
                RemoveSummonLocked(player, fighter, now);
                changed = true;
            }
        }
        return changed;
    }

    public static async Task SummonsTick(Player player, double now)
    {
        if (!player.Connection.PlayerSent || !CurrentGameWork || player.Closed || player.LeavingArea
            || player.Frames.WaitingFirstFrame(player)) return;
        var state = player.Summons;
        EnsureSpectreBankLoaded(player);
        // Persistent summons are suppressed in Town; keep Restored = false so SaveSummons on leaving Town never wipes them.
        if (InTown(player)) return;
        await HitSummonsTick(player, now);
        await GuardianWeaponsTick(player);
        await SpectreChildrenTick(player);
        if (player.PlayerLife > 0 && state.LostPersistentOnDeath)
        {
            state.LostPersistentOnDeath = false;
            state.Restored = false;
            var resetGuard = SummonRestoreGuards.GetOrCreateValue(state);
            resetGuard.NextIndex = 0;
            resetGuard.Retries = 0;
        }
        if (!state.Restored && player.PlayerLife > 0)
        {
            var guard = SummonRestoreGuards.GetOrCreateValue(state);
            if (guard.Restoring) return;
            guard.Restoring = true;
            try
            {
                if (guard.NextIndex == 0)
                {
                    var guardian = CharacterSaves.Load(CharacterName(player))?.ModuleData?.GetValueOrDefault(GuardianSaveKey);
                    if (guardian is not null)
                        try { state.GuardianEquipment = JsonSerializer.Deserialize<Dictionary<byte, Item>>(guardian) ?? []; }
                        catch (JsonException) { state.GuardianEquipment = []; }
                }
                var saved = CharacterSaves.Load(CharacterName(player))?.ModuleData?.GetValueOrDefault(SummonSaveKey);
                SummonedMinions.Saved[] restore = [];
                if (saved is not null)
                {
                    try { restore = JsonSerializer.Deserialize<SummonedMinions.Saved[]>(saved) ?? []; }
                    catch (JsonException) { restore = []; }
                }
                bool transientAbort = false;
                for (int i = guard.NextIndex; i < restore.Length; i++)
                {
                    if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0)
                    { transientAbort = true; break; }
                    var old = restore[i];
                    if (SummonData.Find(old.Effect) is not { } definition || !definition.Persistent
                        && (definition.Id != "SummonSkeletons" || !PermanentSkeletonWarriors(definition, CharacterPlayerStats(player))))
                    {
                        guard.NextIndex = i + 1;
                        continue;
                    }
                    ushort source = RestoredSummonSource(player, old);
                    if (source == 0) { guard.NextIndex = i + 1; continue; }
                    int bodySize = Monsters.Find(old.Variety) is { } v ? Math.Clamp(v.ObjectSize - 1, 0, 127) : 1;
                    uint restoredId = await SummonFighter(player, source, definition,
                        NextToPlayer(player, bodySize), restoredVariety: old.Variety, life: old.Life, restoredEquipment: old.Equipment,
                        resources: old.Resources, legacyArrival: old.Resources is null, arrival: true);
                    if (restoredId == 0)
                    {
                        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0)
                        {
                            transientAbort = true;
                            break;
                        }
                        var check = SupportedSkill(player, source);
                        bool nonTransientDead = old.Resources is { Life: 0 }
                            || old.Resources is null && (!double.IsFinite(old.Life) || old.Life <= 0);
                        if (!nonTransientDead
                            && check.RequirementsMet && SkillsFor(player).EffectId(check.Effect) == definition.Id
                            && Monsters.Find(old.Variety) is { Supported: true }
                            && SummonLimit(player, definition, check) >= 1
                            && (definition.Family != "AnimatedGuardian" || old.Equipment is { Count: > 0 } || state.GuardianEquipment.Count > 0)
                            && GemSkillInstanceDisabled(player, check.Effect, source, triggered: true) is null
                            && ++guard.Retries < MaxMinionRestoreRetries)
                        {
                            transientAbort = true;
                            break;
                        }
                    }
                    guard.NextIndex = i + 1;
                    guard.Retries = 0;
                }
                if (!transientAbort && CurrentGameWork && !player.Closed && !player.LeavingArea)
                    state.Restored = true;
            }
            finally { guard.Restoring = false; }
            if (!state.Restored) return;
        }
        if (state.Fighters.Count == 0 && state.HolyBuff == 0) return;
        bool changed = false;
        var rescaled = new List<(GolemState.Golem Old, SummonCatalog.Definition Definition,
            Dictionary<byte, Item>? Equipment, string Variety, (int X, int Y) Aim)>();
        var reforming = new List<(GolemState.Golem Old, SummonCatalog.Definition Definition)>();
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            if (state.HolyBuff != 0 && now >= state.HolyRegenerationUntil)
            {
                player.Area.Buffs.ClearRow(player.Id, 1181);
                state.HolyBuff = 0; state.HolyRegenerationPerMinute = 0; player.Minions.StatRevision++;
            }
            var playerStats = player.PlayerLife > 0 && now >= state.RefreshAt && state.Fighters.Count > 0
                ? CharacterPlayerStats(player) : null;
            if (playerStats is not null)
            {
                state.RefreshAt = now + .5;
                List<GolemState.Golem>? zombies = null;
                foreach (var fighter in state.Fighters.Values)
                    if (fighter.Definition.Kind == "Zombie" && !fighter.Monster.Dead && fighter.Monster.Life > 0)
                        (zombies ??= []).Add(fighter);
                if (zombies is { Count: > 0 })
                {
                    zombies.Sort(static (a, b) => b.SummonedAt.CompareTo(a.SummonedAt));
                    var newest = zombies[0];
                    if (SupportedSkill(player, newest.Source) is { RequirementsMet: true } skill)
                    {
                        int limit = SummonLimit(state.Definitions[newest.Id], skill.Stats, playerStats);
                        for (int i = limit; i < zombies.Count; i++)
                        {
                            RemoveSummonLocked(player, zombies[i], now);
                            changed = true;
                        }
                    }
                }
                changed |= RefreshSpectreLimitLocked(player, playerStats, now);
                changed |= RefreshSupportedSummonLimitsLocked(player, playerStats, now);
            }
            foreach (var fighter in state.Fighters.Values.ToArray())
            {
                if (!state.Until.TryGetValue(fighter.Id, out double until)) continue;
                if (player.PlayerLife == 0)
                {
                    if (state.Definitions.TryGetValue(fighter.Id, out var def) && def.Persistent
                        && double.IsPositiveInfinity(until))
                        state.LostPersistentOnDeath = true;
                    RemoveSummonLocked(player, fighter, now, dies: true);
                    changed = true;
                    continue;
                }
                if (now >= until || !SpectreChildStillOwned(player, fighter))
                {
                    if (now >= until && !fighter.Monster.Dead && fighter.Monster.Life > 0)
                    {
                        if (fighter.Definition.Kind == VolatileAnomalyFamily)
                            ReleaseVolatileAnomalyLocked(player, fighter, now);
                        else OwnedMinionDeathEffectsLocked(player, fighter, now);
                    }
                    // A Spectre child whose parent is gone dies where it stands too; only a Volatile Anomaly is removed at once.
                    RemoveSummonLocked(player, fighter, now, dies: fighter.Definition.Kind != VolatileAnomalyFamily); changed = true; continue;
                }
                if (fighter.Monster.Dead || fighter.Monster.Life == 0)
                {
                    if (double.IsFinite(fighter.ReviveAt))
                    {
                        if (now < fighter.ReviveAt) continue;
                        if (CanReformSpectreLocked(player, fighter, now))
                        {
                            reforming.Add((fighter, state.Definitions[fighter.Id]));
                            continue;
                        }
                    }
                    RemoveSummonLocked(player, fighter, now, dies: true); changed = true; continue;
                }
                if (playerStats is null) { GolemStepLocked(player, fighter, now); continue; }
                if (state.Definitions[fighter.Id] is { Id: "SummonSkeletons", Persistent: true } definition
                    && !PermanentSkeletonWarriors(definition, playerStats))
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                var gem = SupportedSkill(player, fighter.Source);
                if (fighter.Definition.Kind == "Phantasm") gem = PhantasmGem(player, gem);
                if (fighter.Definition.Kind == LivingLightningFamily)
                {
                    if (LivingLightningDefinition(player, gem) is null)
                    { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                    gem = LivingLightningGem(player, gem);
                }
                if (fighter.Definition.Kind == "Phantasm" && GolemStat(gem.Stats, Stat.BaseNumberOfSupportGhostsAllowed) <= 0)
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                if (fighter.SourceIdentity is not null && (fighter.Definition.Kind == "Phantasm" || SentinelNativeType(fighter.Definition.Kind) != 0)
                    && (gem.SourceGem is null || !ReferenceEquals(fighter.SourceIdentity, PlayerInventories.GemIdentity(gem.SourceGem)))
                    || SkillsFor(player).EffectId(gem.Effect) != fighter.Definition.Id || !gem.RequirementsMet)
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                gem = gem with { Stats = MergeSummonStats(gem.Stats, fighter.RetainedSkillStats) };
                int liveLimit = SummonLimit(player, state.Definitions[fighter.Id], gem);
                if (liveLimit <= 0 || state.Fighters.Values.Count(g => g.Definition.Kind == fighter.Definition.Kind && !g.Monster.Dead) > liveLimit)
                { RemoveSummonLocked(player, fighter, now); changed = true; continue; }
                int actorLevel = GolemStat(gem.Stats, fighter.Definition.Kind == "Spectre" ? Stat.RaisedSpectreLevel : Stat.BaseDisplayMinionActorLevel);
                if (actorLevel <= 0 && (fighter.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) || fighter.Definition.Kind == "VaalDomination"))
                    actorLevel = SkillsFor(player).PlayerLevelReq(gem.Effect, gem.Level);
                if (actorLevel > 0 && actorLevel != fighter.Monster.Level)
                {
                    rescaled.Add((fighter, state.Definitions[fighter.Id],
                        fighter.Equipment.Count > 0 ? fighter.Equipment : null, fighter.Monster.Variety!.Id,
                        fighter.Monster.Walk.Position(now)));
                    continue;
                }
                fighter.Own = new(gem.Stats); fighter.GemLevel = gem.Level; fighter.Quality = gem.Quality;
                RefreshGolemLocked(player, fighter, playerStats, now);
                PresentPhantasmalMightLocked(player, fighter, playerStats, now);
                GolemStepLocked(player, fighter, now);
            }
        }
        foreach (var (old, definition) in reforming)
            if (await SummonFighterCore(player, old.Source, definition, old.Monster.Walk.Position(now),
                restoredVariety: old.Monster.Variety!.Id, inheritedMods: old.InheritedMods, reformingSpectre: old) != 0)
                changed = true;
        foreach (var (old, definition, equipment, variety, aim) in rescaled)
            if (await SummonFighter(player, old.Source, definition, aim,
                restoredVariety: variety, restoredEquipment: equipment, replacingOld: old) != 0)
                changed = true;
        if (changed) SaveSummons(player);
    }

    static int LivingSummons(Player player, string family, double now)
    {
        int count = 0;
        foreach (var minion in player.Summons.Fighters.Values)
            if (minion.Definition.Kind == family && !minion.Monster.Dead && minion.Monster.Life > 0
                && player.Summons.Until.TryGetValue(minion.Id, out double until) && now < until)
                count++;
        return count;
    }

    static void ApplyZombieDamageBonus(Player player, Dictionary<uint, int> stats, double now)
    {
        if (GolemStat(stats, Stat.DamagePlusPctPerRaisedZombie) is not (int amount and not 0)) return;
        int live = LivingSummons(player, "Zombie", now);
        if (live > 0)
            stats[Stat.DamagePlusPct] = ActorScale.Saturate((long)GolemStat(stats, Stat.DamagePlusPct) + (long)amount * live);
    }

    static void AddSummonCounts(Player player, StatMap stats)
    {
        foreach (var (rarity, count) in new[] { (MonsterRarity.Normal, Stat.NumberOfDominatedNormalMonsters),
            (MonsterRarity.Magic, Stat.NumberOfDominatedMagicMonsters), (MonsterRarity.Rare, Stat.NumberOfDominatedRareMonsters) })
            stats[count] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == "Dominating" + rarity && !g.Monster.Dead);
        if (Stat.NumberOfDominatedVaalMonsters is var ascended)
            stats[ascended] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == "VaalDomination" && !g.Monster.Dead);
        if (Stat.NumberOfDominatedMonsters is var dominated)
            stats[dominated] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind.StartsWith("Dominating", StringComparison.Ordinal) && !g.Monster.Dead);
        foreach (var (kind, key) in SummonCountNames)
            stats[key] = player.Summons.Fighters.Values.Count(g => g.Definition.Kind == kind && !g.Monster.Dead && g.Monster.Life > 0);
        // The living minions the client's minion roster draws from (SentinelOwnershipPresentation), each source once: a
        // summon, a golem, a reservation minion, an elemental relic. A permanent one has no duration.
        double now = AreaTime(player);
        int minions = 0, permanent = 0;
        foreach (var (id, fighter) in player.Summons.Fighters)
            if (!fighter.Monster.Dead && fighter.Monster.Life > 0 && player.Summons.Until.TryGetValue(id, out double until) && now < until)
            { minions++; if (double.IsPositiveInfinity(until)) permanent++; }
        foreach (var (id, fighter) in player.Reservations.Fighters)
            if (!fighter.Monster.Dead && fighter.Monster.Life > 0 && player.Reservations.Until.TryGetValue(id, out double until) && now < until)
            { minions++; if (double.IsPositiveInfinity(until)) permanent++; }
        foreach (var golem in player.Golems.All.Values)
            if (!golem.Monster.Dead && golem.Monster.Life > 0) { minions++; permanent++; }
        foreach (var relic in player.Minions.All)
            if (ElementalRelicNativeType(relic.Kind) is not null && !relic.Monster.Dead && relic.Monster.Life > 0 && now < relic.Until
                && !player.Summons.Fighters.ContainsKey(relic.Id) && !player.Golems.All.ContainsKey(relic.Id)
                && !player.Reservations.Fighters.ContainsKey(relic.Id))
            { minions++; if (double.IsPositiveInfinity(relic.Until)) permanent++; }
        stats[Stat.NumberOfActiveMinions] = minions;
        stats[Stat.NumberOfActivePermanentMinions] = permanent;
    }
    static readonly IReadOnlyDictionary<string, uint> SummonCountNames = new Dictionary<string, uint>
    {
        ["Absolution"] = Stat.NumberOfActiveAbsolutionSentinels, ["VaalAbsolution"] = Stat.NumberOfActiveVaalAbsolutionSentinels,
        ["Phantasm"] = Stat.NumberOfActivePhantasms, ["Zombie"] = Stat.NumberOfActiveZombies, ["Spectre"] = Stat.NumberOfActiveSpectres,
        ["Skeleton"] = Stat.NumberOfActiveSkeletons, ["RagingSpirit"] = Stat.NumberOfActiveRagingSpirits, ["HolyRelic"] = Stat.NumberOfActiveRelics,
        ["Reaper"] = Stat.NumberOfActiveReapers, ["VaalAnimatedWeapon"] = Stat.NumberOfActiveVaalAnimatedWeapons,
        ["AnimatedWeapon"] = Stat.NumberOfActiveAnimatedWeapons, ["AnimatedGuardian"] = Stat.NumberOfActiveAnimatedArmour,
        ["HolyArmament"] = Stat.NumberOfActiveHolyStrikeAnimatedWeapons,
    };
}
