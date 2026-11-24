using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

public static partial class Game
{
    /// <summary>ModuleData key storing completed <c>AchievementItems.Id</c> values (account-wide in <see cref="AccountStash"/>, with character fallback when no account is bound).</summary>
    public const string AchievementsSaveKey = "Game.Achievements";

    public sealed record AchievementEntry(ushort Row, string Id, byte SetId, int MinCompletedItems,
        bool SoftcoreOnly, bool HardcoreOnly, ushort[] ItemRows);

    public sealed record AchievementItemEntry(ushort Row, string Id, int BitWidth, int BitOffset,
        int CompletionsRequired, ushort AchievementRow);

    public sealed record AchievementSetLayout(byte SetId, int ByteLength, AchievementItemEntry[] Items);

    sealed class AchievementCatalog
    {
        public static readonly AchievementCatalog Empty = new();

        public Dictionary<string, AchievementItemEntry> ItemsById { get; } = new(StringComparer.Ordinal);
        public Dictionary<ushort, AchievementItemEntry> ItemsByRow { get; } = [];
        public Dictionary<ushort, AchievementEntry> AchievementsByRow { get; } = [];
        public Dictionary<byte, AchievementSetLayout> Sets { get; } = [];
        public Dictionary<string, ushort[]> KillItemsByVariety { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort[]> FullClearItemsByArea { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, ushort> EquipItemByTypeHash { get; } = [];

        public bool IsLoaded => Sets.Count > 0;

        static readonly (string VarietyId, string ItemId)[] CampaignAndEndgameBossKills =
        [
            // Act 1-3 campaign bosses
            ("Metadata/Monsters/Brute/BossBrute", "KillBrutus"),
            ("Metadata/Monsters/Brute/BossBruteStandalone", "KillBrutus"),
            ("Metadata/Monsters/Brute/BossBruteGAUNTLET", "KillBrutus"),
            ("Metadata/Monsters/Seawitch/BossMerveil2", "KillMerveil"),
            ("Metadata/Monsters/Seawitch/BossMerveil2Standalone", "KillMerveil"),
            ("Metadata/Monsters/Seawitch/BossMerveil2Gauntlet", "KillMerveil"),
            ("Metadata/Monsters/IncaShadowBoss/BossIncaShadow", "KillVaal"),
            ("Metadata/Monsters/IncaShadowBoss/BossIncaShadowStandalone", "KillVaal"),
            ("Metadata/Monsters/IncaShadowBoss/BossIncaShadowGauntlet__", "KillVaal"),
            ("Metadata/Monsters/Axis/PietyBoss", "KillPiety"),
            ("Metadata/Monsters/Axis/PietyBossStandalone", "KillPiety"),
            ("Metadata/Monsters/Axis/PietyBossGauntlet", "KillPiety"),
            ("Metadata/Monsters/Dominusdemon/Dominusdemon", "KillDominus"),
            ("Metadata/Monsters/Dominusdemon/DominusdemonStandalone", "KillDominus"),
            ("Metadata/Monsters/Dominusdemon/DominusdemonGAUNTLET_", "KillDominus"),
            // Act 4-10 campaign bosses
            ("Metadata/Monsters/Malachai/MalachaiBoss2", "KillMalachai"),
            ("Metadata/Monsters/Malachai/MalachaiBoss2Standalone", "KillMalachai"),
            ("Metadata/Monsters/Malachai/MalachaiBoss2Gauntlet", "KillMalachai"),
            ("Metadata/Monsters/BrineKing/BrineKing", "KillBrineKing"),
            ("Metadata/Monsters/BrineKing/BrineKingStandalone", "KillBrineKing"),
            ("Metadata/Monsters/BrineKing/BrineKingGauntlet", "KillBrineKing"),
            ("Metadata/Monsters/VaalSpiderGod/Arakaali", "KillArakaali"),
            ("Metadata/Monsters/VaalSpiderGod/ArakaaliStandalone", "KillArakaali"),
            ("Metadata/Monsters/VaalSpiderGod/ArakaaliGauntlet_", "KillArakaali"),
            ("Metadata/Monsters/UnholyTrio/UnholyTrio", "KillUnholyTrio"),
            ("Metadata/Monsters/UnholyTrio/UnholyTrioStandalone", "KillUnholyTrio"),
            ("Metadata/Monsters/UnholyTrio/UnholyTrioGauntlet", "KillUnholyTrio"),
            ("Metadata/Monsters/KitavaBoss/KitavaFinal", "CompleteAct10"),
            // Endgame pinnacle bosses
            ("Metadata/Monsters/AtlasBosses/TheShaperBoss", "KillTheShaper"),
            ("Metadata/Monsters/AtlasBosses/TheShaperBossStandalone", "KillTheShaper"),
            ("Metadata/Monsters/AtlasBosses/TheShaperBossGauntlet", "KillTheShaper"),
            ("Metadata/Monsters/AtlasBosses/TheShaperBossStandaloneMirage", "KillTheShaper"),
            ("Metadata/Monsters/MavenBoss/TheMaven", "DefeatTheMaven"),
            ("Metadata/Monsters/MavenBoss/TheMavenEnraged_", "DefeatTheMaven"),
            ("Metadata/Monsters/MavenBoss/TheMavenEnragedGauntlet", "DefeatTheMaven"),
            ("Metadata/Monsters/MavenBoss/MavenBrainBoss", "DefeatTheMaven"),
            ("Metadata/Monsters/MavenBoss/MavenBrainBossGauntlet_", "DefeatTheMaven"),
            ("Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss", "DefeatScarsOfTheAtlasBoss1"),
            ("Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_", "DefeatScarsOfTheAtlasBoss2"),
            ("Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss", "DefeatScarsOfTheAtlasBoss3"),
            ("Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBossGauntlet", "DefeatScarsOfTheAtlasBoss3"),
            ("Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss", "DefeatScarsOfTheAtlasBoss4"),
            ("Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBossGauntlet", "DefeatScarsOfTheAtlasBoss4"),
        ];

        public static AchievementCatalog Build()
        {
            if (!ClientTables.Has("Achievements") || !ClientTables.Has("AchievementItems"))
                return Empty;

            var catalog = new AchievementCatalog();
            var itemsByAchievement = new Dictionary<ushort, List<ushort>>();
            var itemsBySet = new Dictionary<byte, List<AchievementItemEntry>>();
            var achTable = ClientTables.Table("Achievements");
            var itemTable = ClientTables.Table("AchievementItems");

            foreach (var row in achTable)
            {
                if (row.Index > ushort.MaxValue) continue;
                int setId = row.Int("SetId");
                if (setId is < 0 or > byte.MaxValue) continue;
                ushort achRow = (ushort)row.Index;
                string id = row.Text("Id") ?? "";
                int minItems = row.Int("MinCompletedItems");
                bool scOnly = row.Table.HasColumn("SoftcoreOnly") && row.Bool("SoftcoreOnly");
                bool hcOnly = row.Table.HasColumn("HardcoreOnly") && row.Bool("HardcoreOnly");
                catalog.AchievementsByRow[achRow] = new(achRow, id, (byte)setId, minItems, scOnly, hcOnly, []);
                itemsByAchievement[achRow] = [];
            }

            foreach (var row in itemTable)
            {
                if (row.Index > ushort.MaxValue) continue;
                if (row.IntOrNull("AchievementsKey") is not int achKey || achKey < 0 || achKey > ushort.MaxValue) continue;
                ushort achRow = (ushort)achKey;
                if (!catalog.AchievementsByRow.TryGetValue(achRow, out var parent)) continue;
                ushort itemRow = (ushort)row.Index;
                string id = row.Text("Id") ?? "";
                if (id.Length == 0) continue;
                int bitWidth = row.Int("__col1");
                int bitOffset = row.Int("__col2");
                int required = row.Int("CompletionsRequired");
                var entry = new AchievementItemEntry(itemRow, id, bitWidth, bitOffset, required, achRow);
                catalog.ItemsById.TryAdd(id, entry);
                catalog.ItemsByRow[itemRow] = entry;
                itemsByAchievement[achRow].Add(itemRow);
                if (!itemsBySet.TryGetValue(parent.SetId, out var setList))
                    itemsBySet[parent.SetId] = setList = [];
                setList.Add(entry);
            }

            foreach (var (achRow, list) in itemsByAchievement)
                if (list.Count > 0)
                    catalog.AchievementsByRow[achRow] = catalog.AchievementsByRow[achRow] with { ItemRows = [.. list] };

            foreach (var (setId, list) in itemsBySet)
            {
                int maxEndBit = 0;
                foreach (var item in list)
                    if (item.BitOffset >= 0 && item.BitWidth > 0)
                        maxEndBit = Math.Max(maxEndBit, item.BitOffset + item.BitWidth);
                int byteLen = Math.Min(InstanceAchievements.MaxBitsetBytes, (maxEndBit + 7) >> 3);
                catalog.Sets[setId] = new(setId, byteLen, [.. list]);
            }

            bool IsTrackablePermanent(ushort itemRow) =>
                catalog.ItemsByRow.TryGetValue(itemRow, out var it)
                && catalog.AchievementsByRow.TryGetValue(it.AchievementRow, out var ach)
                && ach.SetId == InstanceAchievements.PermanentAchievementSetId
                && !string.Equals(ach.Id, "FillerSpace", StringComparison.Ordinal);

            var killMap = new Dictionary<string, List<ushort>>(StringComparer.Ordinal);
            void AddKill(string varietyId, ushort itemRow)
            {
                if (!killMap.TryGetValue(varietyId, out var rows)) killMap[varietyId] = rows = [];
                if (!rows.Contains(itemRow)) rows.Add(itemRow);
            }

            if (ClientTables.Has("MonsterVarieties"))
            {
                var mvTable = ClientTables.Table("MonsterVarieties");
                if (mvTable.HasColumn("KillSpecificMonsterCount_AchievementItemsKeys"))
                {
                    foreach (var row in mvTable)
                    {
                        if (row.Text("Id") is not { Length: > 0 } mvId) continue;
                        foreach (int key in row.Ints("KillSpecificMonsterCount_AchievementItemsKeys"))
                            if (key >= 0 && key <= ushort.MaxValue && IsTrackablePermanent((ushort)key))
                                AddKill(mvId, (ushort)key);
                    }
                }
            }
            foreach (var (varietyId, itemId) in CampaignAndEndgameBossKills)
                if (catalog.ItemsById.TryGetValue(itemId, out var item))
                    AddKill(varietyId, item.Row);
            foreach (var (varietyId, rows) in killMap)
                catalog.KillItemsByVariety[varietyId] = [.. rows];

            if (ClientTables.Has("WorldAreas"))
            {
                var waTable = ClientTables.Table("WorldAreas");
                if (waTable.HasColumn("FullClear_AchievementItemsKeys"))
                {
                    foreach (var row in waTable)
                    {
                        if (row.Text("Id") is not { Length: > 0 } areaId) continue;
                        List<ushort>? matched = null;
                        foreach (int key in row.Ints("FullClear_AchievementItemsKeys"))
                            if (key >= 0 && key <= ushort.MaxValue && IsTrackablePermanent((ushort)key))
                                (matched ??= []).Add((ushort)key);
                        if (matched is { Count: > 0 })
                            catalog.FullClearItemsByArea[areaId] = [.. matched];
                    }
                }
            }

            if (ClientTables.Has("BaseItemTypes"))
            {
                var bitTable = ClientTables.Table("BaseItemTypes");
                if (bitTable.HasColumn("Equip_AchievementItemsKey"))
                {
                    foreach (var row in bitTable)
                    {
                        if (row.IntOrNull("Equip_AchievementItemsKey") is not int key || key < 0 || key > ushort.MaxValue) continue;
                        if (!IsTrackablePermanent((ushort)key)) continue;
                        if (row.Text("Id") is { Length: > 0 } baseId)
                            catalog.EquipItemByTypeHash[InstanceEntities.ObjectTypeHash(baseId)] = (ushort)key;
                    }
                }
            }

            return catalog;
        }
    }

    static readonly Lock AchievementCatalogGate = new();
    static object? AchievementCatalogRevision;
    static AchievementCatalog AchievementCatalogCache = AchievementCatalog.Empty;

    /// <summary>Builds the permanent achievements catalog from <see cref="ClientTables"/> at module startup.</summary>
    public static void LoadAchievementsData()
    {
        lock (AchievementCatalogGate)
        {
            AchievementCatalogCache = AchievementCatalog.Build();
            AchievementCatalogRevision = ClientTables.CacheKey;
        }
    }

    static AchievementCatalog AchievementsCatalog()
    {
        var current = ClientTables.CacheKey;
        if (ReferenceEquals(AchievementCatalogRevision, current)) return AchievementCatalogCache;
        lock (AchievementCatalogGate)
        {
            if (ReferenceEquals(AchievementCatalogRevision, current)) return AchievementCatalogCache;
            AchievementCatalogCache = AchievementCatalog.Build();
            AchievementCatalogRevision = current;
            return AchievementCatalogCache;
        }
    }

    sealed record ParsedAchievementSet(string? AccountRaw, string? CharacterRaw, HashSet<string> Items);
    static readonly ConcurrentDictionary<string, ParsedAchievementSet> AchievementParseCache = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetAchievementCache() => AchievementParseCache.Clear();

    static readonly HashSet<string> DominusVarieties = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/Dominusdemon/Dominusdemon",
        "Metadata/Monsters/Dominusdemon/DominusdemonStandalone",
        "Metadata/Monsters/Dominusdemon/DominusdemonGAUNTLET_",
    };

    static readonly HashSet<string> VaalOversoulVarieties = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/IncaShadowBoss/BossIncaShadow",
        "Metadata/Monsters/IncaShadowBoss/BossIncaShadowStandalone",
        "Metadata/Monsters/IncaShadowBoss/BossIncaShadowGauntlet__",
    };

    static readonly Dictionary<string, string> QuestFlagAchievementItems = new(StringComparer.Ordinal)
    {
        ["a5q6-CompletedBoss"] = "CompleteAct5",
        ["a8q23-KilledBosses"] = "KillLunarisSolaris",
        [Quests.EndGameAvailable] = "CompleteAct10",
        ["ScionUnlocked"] = "UnlockScion",
        ["a2b-IntAlly"] = "SideWithAlira",
        ["a2b-DexAlly"] = "SideWithKraityn",
        ["a2b-StrAlly"] = "SideWithOak",
    };

    // PlayerInventories.SwapWeaponSets physically swaps (Weapon1, Weapon2) and (Offhand1, Offhand2),
    // so Weapon1 and Offhand1 always hold the active weapon set while Weapon2/Offhand2 hold the inactive set.
    static readonly byte[] EquipmentAchievementSlots =
    [
        PlayerInventories.BodyArmour, PlayerInventories.Weapon1, PlayerInventories.Offhand1,
        PlayerInventories.Helmet, PlayerInventories.Amulet, PlayerInventories.Ring1,
        PlayerInventories.Ring2, PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Belt,
    ];

    static readonly byte[] RequiredRareSlots =
    [
        PlayerInventories.BodyArmour, PlayerInventories.Weapon1, PlayerInventories.Helmet,
        PlayerInventories.Amulet, PlayerInventories.Ring1, PlayerInventories.Ring2,
        PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Belt,
    ];

    // Always bind the account-wide Standard stash to Program.Items so a Ruthless character running first
    // never caches Standard's AccountStash with HardModeItems.
    static AccountStash? AccountAchievementStore(Player player) =>
        player.Character.Account is { Length: > 0 } account && Program.Items is { } catalog
            ? AccountStash.For(account, catalog) : null;

    static void ParseCompletedInto(string? raw, HashSet<string> target)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string trimmed = raw.Trim();
        if (trimmed[0] == '[')
        {
            try
            {
                if (JsonSerializer.Deserialize<string[]>(trimmed) is { } arr)
                    foreach (string s in arr)
                        if (!string.IsNullOrWhiteSpace(s)) target.Add(s.Trim());
                return;
            }
            catch (JsonException) { }
        }
        foreach (string part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(part);
    }

    static string FormatCompleted(IEnumerable<string> items) =>
        string.Join(',', items.OrderBy(s => s, StringComparer.Ordinal));

    static HashSet<string> ReadCompletedItems(Player player)
    {
        var stash = AccountAchievementStore(player);
        string? accountRaw = stash?.ModuleData(AchievementsSaveKey);
        string? charRaw = CharacterSaves.ModuleData(CharacterName(player), AchievementsSaveKey);
        string key = stash is not null ? $"a:{player.Character.Account}|c:{CharacterName(player)}" : $"c:{CharacterName(player)}";
        if (AchievementParseCache.TryGetValue(key, out var cached)
            && ReferenceEquals(cached.AccountRaw, accountRaw)
            && ReferenceEquals(cached.CharacterRaw, charRaw))
            return cached.Items;

        var parsed = new HashSet<string>(StringComparer.Ordinal);
        ParseCompletedInto(accountRaw, parsed);
        ParseCompletedInto(charRaw, parsed);
        AchievementParseCache[key] = new(accountRaw, charRaw, parsed);
        return parsed;
    }

    /// <summary>Snapshot of completed <c>AchievementItems.Id</c> strings for <paramref name="player"/>.</summary>
    public static IReadOnlySet<string> CompletedAchievementItems(Player player) =>
        new HashSet<string>(ReadCompletedItems(player), StringComparer.Ordinal);

    static bool IsAchievementComplete(AchievementEntry achievement, AchievementCatalog catalog, IReadOnlySet<string> completed)
    {
        if (achievement.ItemRows.Length == 0) return false;
        int done = 0;
        foreach (ushort row in achievement.ItemRows)
            if (catalog.ItemsByRow.TryGetValue(row, out var item) && completed.Contains(item.Id))
                done++;
        // Matches 3.29.3c 0x14220fba0 / 0x14220fe50: MinCompletedItems > 0 requires count >= MinCompletedItems; 0 requires all items.
        return achievement.MinCompletedItems > 0 ? done >= achievement.MinCompletedItems : done == achievement.ItemRows.Length;
    }

    /// <summary>
    /// Builds the packed progress bitset for <paramref name="setId"/> matching the exact LSB-first bit layout decoded by
    /// <c>ChallengesPanel</c> (3.29.3c <c>0x1407fa940</c>) and <c>0x14220fb20</c>.
    /// </summary>
    public static byte[] BuildAchievementProgressBitset(Player player, byte setId = InstanceAchievements.PermanentAchievementSetId) =>
        BuildAchievementProgressBitset(AchievementsCatalog(), ReadCompletedItems(player), setId);

    static byte[] BuildAchievementProgressBitset(AchievementCatalog catalog, IReadOnlySet<string> completed, byte setId)
    {
        if (!catalog.Sets.TryGetValue(setId, out var layout) || layout.ByteLength == 0) return [];
        var bitset = new byte[layout.ByteLength];
        foreach (var item in layout.Items)
        {
            if (item.BitOffset < 0 || item.BitWidth <= 0) continue;
            if (((item.BitOffset + item.BitWidth - 1) >> 3) >= bitset.Length) continue;
            if (!completed.Contains(item.Id)) continue;
            uint value = (uint)Math.Max(1, item.CompletionsRequired);
            InstanceAchievements.WriteBitField(bitset, item.BitOffset, item.BitWidth, value);
        }
        return bitset;
    }

    /// <summary>Initial <c>S->C 0x126</c> permanent achievement progress message sent on area entry (<see cref="GameEvents.EnterMessages"/>).</summary>
    public static IReadOnlyList<(string Log, byte[] Bytes)> AchievementEnterMessages(Player player)
    {
        if (player.Controlled) return [];
        var catalog = AchievementsCatalog();
        if (!catalog.Sets.ContainsKey(InstanceAchievements.PermanentAchievementSetId)) return [];
        byte[] bitset = BuildAchievementProgressBitset(catalog, ReadCompletedItems(player), InstanceAchievements.PermanentAchievementSetId);
        return [($"0x0126 achievement progress (set {InstanceAchievements.PermanentAchievementSetId}, {bitset.Length} bytes)",
            InstanceAchievements.AchievementProgress(InstanceAchievements.PermanentAchievementSetId, bitset))];
    }

    static Task SendAchievementPacket(Player player, byte[] bytes, string log)
    {
        if (player.Connection.Outbox is { } outbox) return outbox(bytes, log);
        return player.Connection.Connected ? Send(player, bytes, log) : Task.CompletedTask;
    }

    /// <summary>Sends <c>S->C 0x126</c> achievement progress bitset for <paramref name="setId"/>.</summary>
    public static Task SendAchievementProgress(Player player, byte setId = InstanceAchievements.PermanentAchievementSetId)
    {
        if (player.Controlled || player.Closed || player.LeavingArea) return Task.CompletedTask;
        var catalog = AchievementsCatalog();
        if (!catalog.Sets.ContainsKey(setId)) return Task.CompletedTask;
        byte[] bitset = BuildAchievementProgressBitset(catalog, ReadCompletedItems(player), setId);
        byte[] packet = InstanceAchievements.AchievementProgress(setId, bitset);
        string log = $"0x0126 achievement progress (set {setId}, {bitset.Length} bytes)";
        return SendAchievementPacket(player, packet, log);
    }

    /// <summary>
    /// Awards an <c>AchievementItems</c> entry by <see cref="AchievementItemEntry.Id"/> for the first time: persists it,
    /// sends <c>S->C 0x125</c> (item complete), sends updated <c>S->C 0x126</c> (progress bitset), and sends
    /// <c>S->C 0x124</c> (achievement complete) if the parent achievement just reached completion.
    /// Returns <see langword="true"/> only on a new completion.
    /// </summary>
    public static Task<bool> AwardAchievementItem(Player player, string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return Task.FromResult(false);
        var catalog = AchievementsCatalog();
        return catalog.ItemsById.TryGetValue(itemId, out var item)
            ? AwardAchievementItemCore(player, catalog, item)
            : Task.FromResult(false);
    }

    /// <summary>Awards an <c>AchievementItems</c> entry by 0-based table row index.</summary>
    public static Task<bool> AwardAchievementItem(Player player, ushort itemRow)
    {
        var catalog = AchievementsCatalog();
        return catalog.ItemsByRow.TryGetValue(itemRow, out var item)
            ? AwardAchievementItemCore(player, catalog, item)
            : Task.FromResult(false);
    }

    static async Task<bool> AwardAchievementItemCore(Player player, AchievementCatalog catalog, AchievementItemEntry item)
    {
        if (player.Controlled || HardcoreProgressBlocked(player)) return false;
        if (!catalog.AchievementsByRow.TryGetValue(item.AchievementRow, out var parent)) return false;
        if (player.Character.Hardcore && parent.SoftcoreOnly) return false;
        if (!player.Character.Hardcore && parent.HardcoreOnly) return false;

        // Fast zero-alloc check before entering persistence transaction.
        if (ReadCompletedItems(player).Contains(item.Id)) return false;

        bool added = false;
        bool parentCompletedNow = false;
        HashSet<string>? afterSet = null;

        string? MergeAndAdd(string? existing, string? extraRaw)
        {
            var merged = new HashSet<string>(StringComparer.Ordinal);
            ParseCompletedInto(existing, merged);
            ParseCompletedInto(extraRaw, merged);
            if (merged.Contains(item.Id)) return null;
            bool wasComplete = IsAchievementComplete(parent, catalog, merged);
            merged.Add(item.Id);
            added = true;
            parentCompletedNow = !wasComplete && IsAchievementComplete(parent, catalog, merged);
            afterSet = merged;
            return FormatCompleted(merged);
        }

        if (AccountAchievementStore(player) is { } stash)
        {
            string? charRaw = CharacterSaves.ModuleData(CharacterName(player), AchievementsSaveKey);
            stash.ChangeModuleData(AchievementsSaveKey, existing => MergeAndAdd(existing, charRaw));
        }
        else
        {
            CharacterSaves.ChangeModuleData(CharacterName(player), AchievementsSaveKey, existing => MergeAndAdd(existing, null));
        }

        if (!added || afterSet is null) return false;

        if (player.Connection.PlayerSent && !player.Closed && !player.LeavingArea)
        {
            await SendAchievementPacket(player, InstanceAchievements.AchievementItemComplete(item.Row),
                $"0x0125 achievement item complete: {item.Id} (row {item.Row})");
            byte[] bitset = BuildAchievementProgressBitset(catalog, afterSet, parent.SetId);
            await SendAchievementPacket(player, InstanceAchievements.AchievementProgress(parent.SetId, bitset),
                $"0x0126 achievement progress (set {parent.SetId}, {bitset.Length} bytes)");
            if (parentCompletedNow)
                await SendAchievementPacket(player, InstanceAchievements.AchievementComplete(parent.Row),
                    $"0x0124 achievement complete: {parent.Id} (row {parent.Row})");
        }
        return true;
    }

    /// <summary>Notifies the client via <c>S->C 0x127</c> that an achievement/challenge item failed.</summary>
    public static Task FailAchievementItem(Player player, string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return Task.CompletedTask;
        var catalog = AchievementsCatalog();
        return catalog.ItemsById.TryGetValue(itemId, out var item)
            ? FailAchievementItem(player, item.Row)
            : Task.CompletedTask;
    }

    /// <summary>Notifies the client via <c>S->C 0x127</c> that an achievement/challenge item failed (0-based row).</summary>
    public static Task FailAchievementItem(Player player, ushort itemRow)
    {
        if (player.Controlled || !player.Connection.PlayerSent || player.Closed || player.LeavingArea)
            return Task.CompletedTask;
        return SendAchievementPacket(player, InstanceAchievements.AchievementItemFailed(itemRow),
            $"0x0127 achievement item failed (row {itemRow})");
    }

    // ---- Gameplay Triggers ----

    /// <summary><see cref="GameEvents.PlayerEntered"/>: awards <c>OpenAndEnterAMap</c> when entering a Map Device map.</summary>
    public static async Task CheckAreaEntryAchievements(Player player)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || !AchievementsCatalog().IsLoaded) return;
        if (MapRunOf(player) is not null && MapDevices.IsMapArea(player.Layout.Hash))
            await AwardAchievementItem(player, "OpenAndEnterAMap");
    }

    /// <summary><see cref="GameEvents.Kill"/>: O(1) lookup by monster variety for boss/warband/endgame achievements.</summary>
    public static void OnMonsterKillAchievements(KillContext kill)
    {
        var player = kill.Player;
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        var catalog = AchievementsCatalog();
        if (!catalog.KillItemsByVariety.TryGetValue(kill.VarietyId, out var rows) || rows.Length == 0) return;

        bool isDominus = DominusVarieties.Contains(kill.VarietyId);
        bool isVaal = VaalOversoulVarieties.Contains(kill.VarietyId);
        kill.Later(() => AwardKillAchievements(player, rows, isDominus, isVaal));
    }

    static async Task AwardKillAchievements(Player player, ushort[] rows, bool isDominus, bool isVaal)
    {
        Player[] recipients = [player, .. (player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
            .Where(p => !ReferenceEquals(p, player))];
        foreach (var recipient in recipients)
        {
            if (recipient.Controlled || recipient.Closed || recipient.LeavingArea || !recipient.Connection.PlayerSent || recipient.PlayerLife == 0)
                continue;
            foreach (ushort itemRow in rows)
                await AwardAchievementItem(recipient, itemRow);
            if (isDominus && DominusClassAchievement(recipient) is { } classItem)
                await AwardAchievementItem(recipient, classItem);
            if (isVaal && FortificationOf(recipient).Read(AreaTime(recipient)).Current > 0)
                await AwardAchievementItem(recipient, "VaalOversoulKilledWithFortify");
        }
    }

    static string? DominusClassAchievement(Player player)
    {
        if (SkillsFor(player) is not { } skills) return null;
        int classId = skills.ClassFor(player.Character.ClassRow).IntegerId;
        return classId switch
        {
            0 => "KillDominusStrDexInt", // Scion
            1 => "KillDominusStr",       // Marauder
            2 => "KillDominusDex",       // Ranger
            3 => "KillDominusInt",       // Witch
            4 => "KillDominusStrDex",    // Duelist
            5 => "KillDominusStrInt",    // Templar
            6 => "KillDominusDexInt",    // Shadow
            _ => null,
        };
    }

    /// <summary>Called when character quest flags are published (<c>Quests.cs</c>).</summary>
    public static async Task CheckQuestFlagAchievements(Player player, IReadOnlyList<string> flags)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || flags.Count == 0 || !AchievementsCatalog().IsLoaded) return;
        foreach (string flag in flags)
        {
            if (QuestFlagAchievementItems.TryGetValue(flag, out var itemId))
                await AwardAchievementItem(player, itemId);
            else if (LabyrinthFlagAchievement(player, flag) is { } labItem)
                await AwardAchievementItem(player, labItem);
        }
    }

    static string? LabyrinthFlagAchievement(Player player, string flag) => flag switch
    {
        "LabyrinthComplete1" => player.Character.Hardcore ? "CompleteLabyrinth1HC" : "CompleteLabyrinth1",
        "LabyrinthComplete2" => player.Character.Hardcore ? "CompleteLabyrinth2HC" : "CompleteLabyrinth2",
        "LabyrinthComplete3" => player.Character.Hardcore ? "CompleteLabyrinth3HC" : "CompleteLabyrinth3",
        "LabyrinthCompleteMaps" => player.Character.Hardcore ? "CompleteLabyrinth4HC" : "CompleteLabyrinth4",
        _ => null,
    };

    /// <summary>Called when Labyrinth ascendancy points are awarded (<c>Progression.cs</c>).</summary>
    public static async Task CheckAscendancyAwardAchievements(Player player, IEnumerable<string> awards)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || !AchievementsCatalog().IsLoaded) return;
        foreach (string award in awards)
        {
            string? itemId = award switch
            {
                "normal" => player.Character.Hardcore ? "CompleteLabyrinth1HC" : "CompleteLabyrinth1",
                "cruel" => player.Character.Hardcore ? "CompleteLabyrinth2HC" : "CompleteLabyrinth2",
                "merciless" => player.Character.Hardcore ? "CompleteLabyrinth3HC" : "CompleteLabyrinth3",
                "eternal" => player.Character.Hardcore ? "CompleteLabyrinth4HC" : "CompleteLabyrinth4",
                _ => null,
            };
            if (itemId is not null) await AwardAchievementItem(player, itemId);
        }
    }

    /// <summary>Called on level-up (<c>Skills.cs</c>).</summary>
    public static async Task CheckLevelAchievements(Player player, int level)
    {
        if (player.Controlled || level < 60 || !AchievementsCatalog().IsLoaded) return;
        bool hc = player.Character.Hardcore;
        if (level >= 60) await AwardAchievementItem(player, hc ? "Level60HC" : "Level60");
        if (level >= 70) await AwardAchievementItem(player, hc ? "Level70HC" : "Level70");
        if (level >= 80) await AwardAchievementItem(player, hc ? "Level80HC" : "Level80");
        if (level >= 90) await AwardAchievementItem(player, hc ? "Level90HC" : "Level90");
    }

    /// <summary>Called when worn equipment changes (<c>Equipment.cs</c>).</summary>
    public static async Task CheckEquipmentAchievements(Player player, PlayerInventories items)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        var catalog = AchievementsCatalog();
        if (!catalog.IsLoaded) return;
        var itemCatalog = ItemsFor(player);

        bool anyUnique = false;
        bool duplicateUnique = false;
        List<IReadOnlyList<uint>>? uniqueNames = null;
        List<ushort>? marakethRows = null;

        foreach (byte slot in EquipmentAchievementSlots)
        {
            var worn = items.Items(slot);
            if (worn.Count == 0 || IsInactive(player, worn[0].Id)) continue;
            var item = worn[0].Item;
            if (item.Rarity == ItemRarity.Unique)
            {
                anyUnique = true;
                if (item.NameWords.Count > 0)
                {
                    if (uniqueNames is not null)
                    {
                        foreach (var prior in uniqueNames)
                            if (prior.Count == item.NameWords.Count && prior.SequenceEqual(item.NameWords))
                                duplicateUnique = true;
                    }
                    (uniqueNames ??= []).Add(item.NameWords);
                }
            }
            if (catalog.EquipItemByTypeHash.TryGetValue(item.TypeHash, out ushort equipItemRow))
                (marakethRows ??= []).Add(equipItemRow);
        }

        if (anyUnique) await AwardAchievementItem(player, "EquipUnique");
        if (duplicateUnique) await AwardAchievementItem(player, "EquipTwoOfTheSameUnique");
        if (marakethRows is not null)
            foreach (ushort row in marakethRows)
                await AwardAchievementItem(player, row);

        // EquipRareEachSlot ("Geared Up"): Rare or Unique in every active non-Flask slot (two-handed non-bow omits offhand).
        bool allRareOrUnique = true;
        foreach (byte slot in RequiredRareSlots)
        {
            var worn = items.Items(slot);
            if (worn.Count == 0 || IsInactive(player, worn[0].Id)
                || worn[0].Item.Rarity is not (ItemRarity.Rare or ItemRarity.Unique))
            {
                allRareOrUnique = false;
                break;
            }
        }
        if (allRareOrUnique)
        {
            var mainHand = items.Items(PlayerInventories.Weapon1)[0].Item;
            string mainClass = itemCatalog.ByHash(mainHand.TypeHash)?.Class ?? "";
            bool twoHandNoOffhand = mainClass is "Staff" or "Warstaff" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" or "FishingRod";
            if (!twoHandNoOffhand)
            {
                var offhand = items.Items(PlayerInventories.Offhand1);
                if (offhand.Count == 0 || IsInactive(player, offhand[0].Id)
                    || offhand[0].Item.Rarity is not (ItemRarity.Rare or ItemRarity.Unique))
                    allRareOrUnique = false;
            }
        }
        if (allRareOrUnique) await AwardAchievementItem(player, "EquipRareEachSlot");
    }

    /// <summary>Called after passive nodes are allocated (<c>Progression.cs</c>).</summary>
    public static async Task CheckPassiveAchievements(Player player, IReadOnlyList<ushort> allocatedNodes)
    {
        if (player.Controlled || allocatedNodes.Count == 0 || !AchievementsCatalog().IsLoaded) return;
        var tree = PassivesFor(player);
        int keystones = 0;
        for (int i = 0; i < allocatedNodes.Count; i++)
            if (tree.Find(allocatedNodes[i]) is { IsKeystone: true })
                keystones++;
        if (keystones >= 1) await AwardAchievementItem(player, "AllocateKeystone");
        if (keystones >= 4) await AwardAchievementItem(player, "Allocate4Keystones");
    }

    /// <summary>Called when a skill gem levels up (<c>Items.cs</c>).</summary>
    public static async Task CheckGemLevelAchievements(Player player, Item gem, int newLevel)
    {
        if (player.Controlled || newLevel < 20 || !AchievementsCatalog().IsLoaded) return;
        await AwardAchievementItem(player, "Level20Gem");
        if (IsVaalSkillGem(ItemsFor(player), gem))
            await AwardAchievementItem(player, "Level20VaalGem");
    }

    static bool IsVaalSkillGem(ItemCatalog catalog, Item gem) =>
        catalog.ByHash(gem.TypeHash) is { } type
        && catalog.Gem(type) is { IsVaalOrAwakened: true } info
        && info.Variant(gem.GemVariant).Name.StartsWith("Vaal ", StringComparison.Ordinal);

    /// <summary>Called when an area's living hostile monster count drops from &gt;0 to 0 (<c>MonsterRemaining.cs</c>).</summary>
    public static async Task CheckFullClearAchievements(Player player)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        var catalog = AchievementsCatalog();
        if (!catalog.IsLoaded) return;
        if (MapDevices.IsMapArea(player.Layout.Hash))
            await AwardAchievementItem(player, "FullClearMap");
        if (catalog.FullClearItemsByArea.TryGetValue(player.Layout.Id, out var areaItems))
            foreach (ushort row in areaItems)
                await AwardAchievementItem(player, row);
    }

    /// <summary>Called after currency crafting succeeds on an item (<c>Items.cs</c>).</summary>
    public static async Task CheckCraftingAchievements(Player player, string currencyId, Item before, Item after)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || !AchievementsCatalog().IsLoaded) return;
        if (currencyId == CurrencyEffects.OrbOfChance)
        {
            if (after.Rarity == ItemRarity.Unique)
                await AwardAchievementItem(player, "ChanceUnique");
        }
        else if (currencyId == CurrencyEffects.OrbOfFusing)
        {
            if (HasSixLink(after))
                await AwardAchievementItem(player, "RollSixLink");
        }
        else if (currencyId == CurrencyEffects.OrbOfAlchemy)
        {
            if (ItemsFor(player).ByHash(after.TypeHash)?.Class is "Jewel" or "AbyssJewel"
                && after.Mods.Count(m => m.Section == ModSection.Explicit) >= 4)
                await AwardAchievementItem(player, "MakeFourModJewelWithAlchemy");
        }
        else if (currencyId == CurrencyEffects.VaalOrb)
        {
            if (after.Sockets.Any(s => s.Colour == SocketColour.White) && !before.Sockets.Any(s => s.Colour == SocketColour.White))
                await AwardAchievementItem(player, "CorruptItemWhiteSocket");
            if (ImplicitModsChanged(before, after))
                await AwardAchievementItem(player, "CorruptItemNewImplicit");
            if (before.Components.Contains("SkillGem") && after.TypeHash != before.TypeHash && IsVaalSkillGem(ItemsFor(player), after))
                await AwardAchievementItem(player, "CorruptGemVaalGem");
            if (ItemsFor(player).ByHash(after.TypeHash) is { } targetType
                && CurrencyEffects.IsMap(after, targetType)
                && after.Mods.Count(m => m.Section == ModSection.Explicit) >= 8)
                await AwardAchievementItem(player, "CorruptMap8Mods");
        }
    }

    static bool HasSixLink(Item item)
    {
        if (item.Links is not { Count: > 0 } links) return false;
        for (int i = 0; i < links.Count; i++)
            if (links[i] >= 6) return true;
        return false;
    }

    static bool ImplicitModsChanged(Item before, Item after)
    {
        int beforeCount = 0, afterCount = 0;
        for (int i = 0; i < before.Mods.Count; i++) if (before.Mods[i].Section == ModSection.Implicit) beforeCount++;
        for (int i = 0; i < after.Mods.Count; i++) if (after.Mods[i].Section == ModSection.Implicit) afterCount++;
        if (afterCount == 0) return false;
        if (beforeCount != afterCount) return true;
        int bi = 0;
        for (int ai = 0; ai < after.Mods.Count; ai++)
        {
            if (after.Mods[ai].Section != ModSection.Implicit) continue;
            while (bi < before.Mods.Count && before.Mods[bi].Section != ModSection.Implicit) bi++;
            if (before.Mods[bi].Hash != after.Mods[ai].Hash) return true;
            bi++;
        }
        return false;
    }
}
