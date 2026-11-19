using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Ship Upgrades (<c>DeepwaterUpgrades#0–8</c>), the 5 upgrade pieces (<c>BaseItemTypes#5426–5430</c>,
/// <c>QuestItems#40–44</c>), Vesper turn-ins (<c>NPCTalk#3920–3924</c>), final-lantern Adrenaline
/// (<c>BuffDefinitions#1090</c>), and the below-deck <c>GhostflameCraftingBenchProps_Stage1..6</c> visual &amp;
/// tutorial crafting props on The Sovereign.</summary>
public static class DeepwaterUpgrades
{
    /// <summary>Number of ship upgrade pieces (<c>DeepwaterUpgradePiece1..5</c>) and cumulative ship upgrade rows
    /// (<c>DeepwaterUpgrades#0..4</c>).</summary>
    public const int PieceCount = 5;

    /// <summary>Per-piece turn-in league quest flags (<c>TurnInDeepwaterUpgradeItem1..5</c>, LQF 1824–1828).</summary>
    public static readonly int[] TurnInLeagueFlags = [1824, 1825, 1826, 1827, 1828];

    /// <summary>Cumulative count league quest flags (<c>DeepwaterTotalUpgradeItemTurnedIn1..5</c>, LQF 1829–1833),
    /// gating <c>DeepwaterUpgrades#0..4</c> and <c>GhostflameCraftingBenchProps_Stage2..6</c>.</summary>
    public static readonly int[] TotalTurnedInLeagueFlags = [1829, 1830, 1831, 1832, 1833];

    /// <summary>Quest flags granted while carrying each upgrade piece (<c>QF 3563–3567</c>).</summary>
    public static readonly string[] HeldQuestFlags =
    [
        "HaveDeepwaterUpgradeItem1",
        "HaveDeepwaterUpgradeItem2",
        "HaveDeepwaterUpgradeItem3",
        "HaveDeepwaterUpgradeItem4",
        "HaveDeepwaterUpgradeItem5",
    ];

    /// <summary>Vesper (<c>NPCs#795</c>) turn-in <c>NPCTalk</c> rows for pieces 1..5.</summary>
    public static readonly int[] VesperTurnInTalkRows = [3920, 3921, 3922, 3923, 3924];

    // Crafting copy-cap & endgame unlocks applied on upgrade turn-in (3.29.0b: 1 piece -> 3 copies, 3 pieces -> 4 copies + endgame/Ducats).
    public const int ThreeOutputCraftLeagueFlag = 1876;       // Deepwater3OutputCraft
    public const int FourOutputCraftLeagueFlag = 1877;        // Deepwater4OutputCraft
    public const int EndgameCraftLeagueFlag = 1878;           // DeepwaterEndgameCraft
    public const int SpecialCraftsUnlockedLeagueFlag = 1818;  // DeepwaterSpecialCraftsUnlocked
    public const int VesperFlameGivenLeagueFlag = 1859;       // DeepwaterValVesperFlameGiven (gates Stage1 bench disable_targetable)

    // Exact Stats.dat Ids used by DeepwaterUpgrades#0..5 (client.db verified).
    public const string MaxLanternsStat = "map_deepwater_max_lanterns";
    public const string ResourceFoundStat = "map_deepwater_league_resource_found_+%";
    public const string ItemQuantityStat = "map_item_drop_quantity_+%";
    public const string ScatterObjectsStat = "map_deepwater_upgrade_enable_scatter_objects";
    public const string RareStrongboxesStat = "map_strongboxes_at_least_rare";
    public const string HigherEssenceTierStat = "map_essences_are_1_tier_higher_chance_%";
    public const string FinalLanternAdrenalineStat = "map_deepwater_players_gain_adrenaline_for_x_ms_on_final_lantern_placement";
    public const string ItemRarityStat = "map_item_drop_rarity_+%";
    public const string VoyageMaxLanternsStat = "map_deepwater_max_lanterns_in_voyages_+";

    /// <summary><c>BuffDefinitions#1090</c> (<c>adrenaline</c>) granted for 10 s on final lantern placement once
    /// upgrade row 4 (<c>LQF 1833</c>) is unlocked.</summary>
    public const ushort AdrenalineBuff = AntiquarianEffects.AdrenalineDefinition;
    public const int DefaultFinalLanternAdrenalineMs = 10_000;

    // BaseItemTypes#5426..5430: the 5 ship upgrade quest items (2x2).
    public const string DeadcallerBellowsId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece1";
    public const string SulphurPurifierId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece2";
    public const string SpectralInfuserId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece3";
    public const string AlchemicalExchangeId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece4";
    public const string ForbiddenScriptureId = "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece5";

    static readonly string[] PieceItemIds =
    [
        DeadcallerBellowsId,
        SulphurPurifierId,
        SpectralInfuserId,
        AlchemicalExchangeId,
        ForbiddenScriptureId,
    ];

    /// <summary>Below-deck bench stage object path prefix (<c>GhostflameCraftingBenchProps_Stage1..6</c>).</summary>
    public const string BenchPropPrefix = "Metadata/Terrain/Leagues/Deepwater/Objects/GhostflameCraftingBenchProps_Stage";
    public const string Stage1BenchType = BenchPropPrefix + "1";
    public static readonly string[] BenchStageTypes =
    [
        BenchPropPrefix + "1",
        BenchPropPrefix + "2",
        BenchPropPrefix + "3",
        BenchPropPrefix + "4",
        BenchPropPrefix + "5",
        BenchPropPrefix + "6",
    ];

    // Drop eligibility thresholds & monster variety filters (wiki / research-mechanics.md §1.2).
    public const int Piece3MinAreaLevel = 55;
    public const int Piece4MinAreaLevel = 73;
    const string WrigglingHorrorBase = "Metadata/Monsters/DeepwaterLeague/OctopusParasite";
    const string WrigglingHorrorBathysphere = "Metadata/Monsters/DeepwaterLeague/OctopusParasiteBathysphere";

    // G: world.db system_settings keys for tunable upgrade piece drops & scatter object bonus.
    public const string Piece1DropChanceSetting = "deepwater_upgrade_piece1_drop_chance";
    public const string Piece3DropChanceSetting = "deepwater_upgrade_piece3_drop_chance";
    public const string Piece4DropChanceSetting = "deepwater_upgrade_piece4_drop_chance";
    public const string ScatterUpgradeExtraMinSetting = "deepwater_scatter_upgrade_extra_min";
    public const string ScatterUpgradeExtraMaxSetting = "deepwater_scatter_upgrade_extra_max";

    // Exact .ot deserialized component order (verified via ot_components.py against game-files.zip; Stage1..6 are placed
    // by DeepwaterHubBoatInterior.tdt and are not present in client.db's object_types table).
    static readonly string[] Stage1FallbackComponents = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "StateMachine"];
    static readonly string[] Stage2To6FallbackComponents = ["Positioned", "BaseEvents", "Animated", "TriggerableBlockage", "StateMachine"];

    // DeepwaterHubBoatInterior.tdt placements (allqo.txt:6420-6425; research-protocol.md §2.2).
    static readonly (int X, int Y)[] DefaultBenchPositions =
    [
        (322, 138), // Stage1 (globals.vesperbench_stage1)
        (276, 138), // Stage2 (LQF 1829)
        (276, 161), // Stage3 (LQF 1830)
        (276, 115), // Stage4 (LQF 1831)
        (299, 138), // Stage5 (LQF 1832)
        (253, 138), // Stage6 (LQF 1833)
    ];

    /// <summary>One row of <c>DeepwaterUpgrades</c> (rows 0..8), with 1-based client stat keys (<c>Stats._row + 1</c>).</summary>
    public sealed record UpgradeRow(int Row, string Id, int LeagueFlag, IReadOnlyList<(uint Key, int Value)> Stats);

    /// <summary>One of the 5 ship upgrade quest items (<c>BaseItemTypes#5426–5430</c>, <c>QuestItems#40–44</c>, Vesper
    /// <c>NPCTalk#3920–3924</c>).</summary>
    public sealed record UpgradePiece(
        int Index,
        int BaseRow,
        string ItemId,
        string Name,
        uint TypeHash,
        string QuestFlag,
        int TurnInLeagueFlag,
        int VesperTalkRow);

    sealed record Catalog(
        UpgradeRow[] Rows,
        UpgradeRow[] ShipRows,
        UpgradePiece[] Pieces,
        Dictionary<uint, UpgradePiece> PieceByHash,
        Dictionary<int, UpgradePiece> PieceByTalkRow);

    static readonly Lazy<Catalog> Data = new(LoadCatalog);

    static Catalog LoadCatalog()
    {
        var upgradesTable = ClientTables.TableOrEmpty("DeepwaterUpgrades");
        var rows = new List<UpgradeRow>(upgradesTable.Count);
        foreach (var r in upgradesTable)
        {
            string id = r.TextOrNull("Id") ?? "";
            int lqf = r.IntOrNull("QuestState") ?? 0;
            int[] statRows = r.IsNull("Stats") ? [] : r.Ints("Stats");
            int[] values = r.IsNull("StatsValues") ? [] : r.Ints("StatsValues");
            var pairs = new (uint Key, int Value)[statRows.Length];
            for (int i = 0; i < statRows.Length; i++)
                pairs[i] = ((uint)statRows[i] + 1, i < values.Length ? values[i] : 0);
            rows.Add(new UpgradeRow(r.Index, id, lqf, pairs));
        }

        var shipRows = rows.Where(r => r.Row < PieceCount).ToArray();

        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        var questFlags = ClientTables.TableOrEmpty("QuestFlags");
        var baseRowById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < bases.Count; i++)
            if (bases[i].TextOrNull("Id") is { Length: > 0 } bId)
                baseRowById.TryAdd(bId, i);

        var pieceBaseRows = new HashSet<int>();
        foreach (string itemId in PieceItemIds)
            if (baseRowById.TryGetValue(itemId, out int bRow)) pieceBaseRows.Add(bRow);

        var qfByBaseRow = new Dictionary<int, string>();
        var removeLqfByBaseRow = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("QuestItems"))
        {
            if (r.IntOrNull("Item") is not int itemRow || !pieceBaseRows.Contains(itemRow)) continue;
            if (r.IntOrNull("TriggeredQuestFlag") is int qfRow && qfRow >= 0 && qfRow < questFlags.Count
                && questFlags[qfRow].TextOrNull("Id") is { Length: > 0 } qfId)
                qfByBaseRow[itemRow] = qfId;
            if (r.Has("__col3") && r.IntOrNull("__col3") is int removeLqf && removeLqf > 0 && removeLqf <= Quests.League.Count)
                removeLqfByBaseRow[itemRow] = removeLqf;
        }

        var talkRowByLqf = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("NPCTalk"))
        {
            if (r.IntOrNull("NPCKey") != DeepwaterCrafting.VesperNpc) continue;
            if (!r.Has("__col15") || r.IsNull("__col15")) continue;
            foreach (int setLqf in r.Ints("__col15"))
                if (Array.IndexOf(TurnInLeagueFlags, setLqf) >= 0)
                    talkRowByLqf.TryAdd(setLqf, r.Index);
        }

        var pieces = new List<UpgradePiece>(PieceCount);
        var byHash = new Dictionary<uint, UpgradePiece>(PieceCount);
        var byTalkRow = new Dictionary<int, UpgradePiece>(PieceCount);
        for (int i = 0; i < PieceItemIds.Length; i++)
        {
            string itemId = PieceItemIds[i];
            int baseRow = baseRowById.GetValueOrDefault(itemId, -1);
            string name = baseRow >= 0 && baseRow < bases.Count ? bases[baseRow].TextOrNull("Name") ?? itemId : itemId;
            uint hash = baseRow >= 0 && baseRow < bases.Count ? bases[baseRow].UInt("HASH32") : InstanceEntities.ObjectTypeHash(itemId);
            string qf = baseRow >= 0 && qfByBaseRow.TryGetValue(baseRow, out var foundQf) ? foundQf : HeldQuestFlags[i];
            int turnInLqf = baseRow >= 0 && removeLqfByBaseRow.TryGetValue(baseRow, out int foundLqf) ? foundLqf : TurnInLeagueFlags[i];
            int talkRow = talkRowByLqf.TryGetValue(turnInLqf, out int foundTalk) ? foundTalk : VesperTurnInTalkRows[i];
            var piece = new UpgradePiece(i, baseRow, itemId, name, hash, qf, turnInLqf, talkRow);
            pieces.Add(piece);
            byHash[hash] = piece;
            byTalkRow[talkRow] = piece;
        }

        return new Catalog([.. rows], shipRows, [.. pieces], byHash, byTalkRow);
    }

    /// <summary>All 9 rows of <c>DeepwaterUpgrades</c> (0..4 ship upgrades, 5 Voyage3x3, 6..8 DucatCount1..3).</summary>
    public static IReadOnlyList<UpgradeRow> Rows => Data.Value.Rows;

    /// <summary>Rows 0..4 of <c>DeepwaterUpgrades</c> (gated by <c>LQF 1829..1833</c>).</summary>
    public static IReadOnlyList<UpgradeRow> ShipUpgradeRows => Data.Value.ShipRows;

    /// <summary>All 5 ship upgrade quest pieces (<c>Deadcaller Bellows</c> .. <c>Forbidden Scripture</c>).</summary>
    public static IReadOnlyList<UpgradePiece> Pieces => Data.Value.Pieces;

    public static UpgradePiece? PieceByIndex(int index) =>
        (uint)index < (uint)Data.Value.Pieces.Length ? Data.Value.Pieces[index] : null;

    public static UpgradePiece? PieceByHash(uint typeHash) => Data.Value.PieceByHash.GetValueOrDefault(typeHash);

    public static UpgradePiece? PieceByTalkRow(int talkRow) => Data.Value.PieceByTalkRow.GetValueOrDefault(talkRow);

    public static bool IsUpgradePiece(uint typeHash) => Data.Value.PieceByHash.ContainsKey(typeHash);

    public static bool IsUpgradePiece(Item item) => IsUpgradePiece(item.TypeHash);

    // ---- Stat injection (Charts & Voyages) & run query helpers ----

    /// <summary>Injects all active ship upgrade stats (<c>DeepwaterUpgrades#0..4</c>, gated by <c>LQF 1829..1833</c>)
    /// into <paramref name="global"/> for both Charts and Voyages. Returns the number of active ship upgrades applied (0..5).</summary>
    public static int InjectPlanStats(ClientState conn, Dictionary<uint, int> global)
    {
        HashSet<int> snapshot;
        lock (conn.Monsters) snapshot = [.. conn.LeagueQuestFlags];
        return InjectStats(snapshot.Contains, global);
    }

    /// <summary>Pure stat-injection core for <see cref="InjectPlanStats"/> (callable directly in Gateway tests).</summary>
    public static int InjectStats(Func<int, bool> hasLeagueFlag, Dictionary<uint, int> global)
    {
        int applied = 0;
        var rows = ShipUpgradeRows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.LeagueFlag <= 0 || !hasLeagueFlag(row.LeagueFlag)) continue;
            applied++;
            for (int s = 0; s < row.Stats.Count; s++)
            {
                var (key, value) = row.Stats[s];
                if (value != 0) global[key] = global.GetValueOrDefault(key) + value;
            }
        }
        return applied;
    }

    /// <summary>Convenience overload of <see cref="InjectStats(Func{int, bool}, Dictionary{uint, int})"/>.</summary>
    public static int InjectStats(IReadOnlySet<int> leagueFlags, Dictionary<uint, int> global) =>
        InjectStats(leagueFlags.Contains, global);

    /// <summary>Extra lanterns granted to both Charts and Voyages by <c>DeepwaterUpgrades</c> (<c>23019</c>
    /// <c>map_deepwater_max_lanterns</c>, +1 once <c>LQF 1829</c> is set).</summary>
    public static int ExtraLanterns(ClientState conn)
    {
        HashSet<int> snapshot;
        lock (conn.Monsters) snapshot = [.. conn.LeagueQuestFlags];
        return ExtraLanterns(snapshot.Contains);
    }

    /// <summary>Pure helper for <see cref="ExtraLanterns(ClientState)"/>.</summary>
    public static int ExtraLanterns(Func<int, bool> hasLeagueFlag)
    {
        if (Program.Stats.Key(MaxLanternsStat) is not uint key) return 0;
        int total = 0;
        var rows = ShipUpgradeRows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.LeagueFlag <= 0 || !hasLeagueFlag(row.LeagueFlag)) continue;
            for (int s = 0; s < row.Stats.Count; s++)
                if (row.Stats[s].Key == key) total += row.Stats[s].Value;
        }
        return Math.Max(0, total);
    }

    /// <summary>Reads <c>map_deepwater_max_lanterns</c> (<c>23019</c>) directly from a plan/area stat dictionary.</summary>
    public static int ExtraLanternsFromStats(IReadOnlyDictionary<uint, int>? stats) =>
        stats is not null && Program.Stats.Key(MaxLanternsStat) is uint key
            ? Math.Max(0, stats.GetValueOrDefault(key)) : 0;

    /// <summary>Whether ship upgrade row 3 (<c>LQF 1832</c>, <c>23022 map_deepwater_upgrade_enable_scatter_objects</c>)
    /// is active for this run (checks <c>KeptAreaStats</c> first, falling back to <c>LQF 1832</c>).</summary>
    public static bool ScatterObjectsEnabled(ClientState conn)
    {
        if (Program.Stats.Key(ScatterObjectsStat) is uint key
            && conn.Instance?.KeptAreaStats?.GetValueOrDefault(key) is > 0)
            return true;
        return HasLeagueFlag(conn, TotalTurnedInLeagueFlags[3]);
    }

    /// <summary>Extra scattered objects to place per cell when <see cref="ScatterObjectsEnabled"/> is true
    /// (G: 1–2 extra objects per cell, tunable via <c>deepwater_scatter_upgrade_extra_min/max</c>).</summary>
    public static int ExtraScatteredObjects(ClientState conn, Random random)
    {
        if (!ScatterObjectsEnabled(conn)) return 0;
        int min = Setting(ScatterUpgradeExtraMinSetting, 1, 0, 10);
        int max = Math.Max(min, Setting(ScatterUpgradeExtraMaxSetting, 2, 0, 10));
        return max <= 0 ? 0 : random.Next(min, max + 1);
    }

    /// <summary>Duration in ms of the final-lantern Adrenaline buff (<c>23021</c>, 10000 ms once <c>LQF 1833</c> is set),
    /// or 0 when not unlocked.</summary>
    public static int FinalLanternAdrenalineMs(ClientState conn)
    {
        if (Program.Stats.Key(FinalLanternAdrenalineStat) is uint key
            && conn.Instance?.KeptAreaStats?.GetValueOrDefault(key) is int ms and > 0)
            return ms;
        return HasLeagueFlag(conn, TotalTurnedInLeagueFlags[4]) ? DefaultFinalLanternAdrenalineMs : 0;
    }

    // ---- Final-lantern 10 s Adrenaline (BuffDefinitions#1090) ----

    sealed class AdrenalineState
    {
        public ushort BuffId;
        public double Until;
    }

    static readonly ConditionalWeakTable<ClientState, AdrenalineState> AdrenalineHolders = new();
    static int _adrenalineHeld;

    /// <summary>Grants 10 s Adrenaline (<c>BuffDefinitions#1090</c>) to every alive member in the run when the final
    /// lantern is placed (<c>placed &gt;= total</c>) and upgrade row 4 (<c>23021</c> / <c>LQF 1833</c>) is active.
    /// Does not refresh if already active (PoE Adrenaline rule).</summary>
    public static async Task GrantFinalLanternAdrenaline(IModuleContext module, ClientState conn)
    {
        int durationMs = FinalLanternAdrenalineMs(conn);
        if (durationMs <= 0) return;
        double durationSeconds = durationMs / 1000.0;

        foreach (var member in DeepwaterLoot.Members(conn))
        {
            if (member.Closed || member.LeavingArea || member.PlayerLife == 0) continue;
            double now = member.Clock.Elapsed.TotalSeconds;
            bool antiquarianActive;
            lock (member.Monsters) antiquarianActive = member.Antiquarian.Adrenaline(now);
            if (antiquarianActive) continue;

            var state = AdrenalineHolders.GetValue(member, _ => new AdrenalineState());
            ushort buffId;
            ushort expiredOld = 0;
            lock (state)
            {
                if (state.BuffId != 0 && now < state.Until) continue; // already active; Adrenaline cannot be extended
                if (state.BuffId != 0)
                {
                    expiredOld = state.BuffId;
                    Interlocked.Decrement(ref _adrenalineHeld);
                }
                buffId = member.NextBuff++;
                state.BuffId = buffId;
                state.Until = now + durationSeconds;
                Interlocked.Increment(ref _adrenalineHeld);
            }

            var player = PlayerOf(member);
            if (expiredOld != 0)
                await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.RemoveBuff(PlayerId, expiredOld),
                    "0x01c2 Allflame Adrenaline expired before refresh");
            await global::Game.SendWorldRecord(player, PlayerId,
                InstanceEntities.AddBuff(PlayerId, buffId, AdrenalineBuff, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)durationSeconds)),
                $"0x01c1 Allflame final-lantern Adrenaline ({durationSeconds:0.#}s)");
            await SendAdrenalineStats(member, "final lantern placed");
            module.Log($"{CharacterName(member)}: final-lantern Adrenaline granted for {durationSeconds:0.#}s");
        }
    }

    /// <summary><c>GameEvents.PlayerStatSources</c>: merges <see cref="AntiquarianEffects.AdrenalineStats"/> while
    /// final-lantern Adrenaline is active (0-alloc O(1) counter check when inactive).</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> sources)
    {
        if (Volatile.Read(ref _adrenalineHeld) == 0 || !AdrenalineHolders.TryGetValue(conn, out var state)) return sources;
        double now = conn.Clock.Elapsed.TotalSeconds;
        bool active;
        lock (state) active = state.BuffId != 0 && now < state.Until;
        if (!active || conn.PlayerLife == 0) return sources;

        lock (conn.Monsters)
            if (conn.Antiquarian.Adrenaline(now)) return sources;

        foreach (var (key, value) in AntiquarianEffects.AdrenalineStats)
            sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return sources;
    }

    /// <summary><c>GameEvents.Tick</c>: expires final-lantern Adrenaline at death or after its duration (0-alloc no-op
    /// while nobody holds it).</summary>
    public static async Task TickAdrenaline(IModuleContext module, ClientState conn)
    {
        if (Volatile.Read(ref _adrenalineHeld) == 0 || !AdrenalineHolders.TryGetValue(conn, out var state)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        ushort ended = 0;
        string why = "expired";
        lock (state)
        {
            if (state.BuffId == 0) return;
            if (conn.PlayerLife == 0) why = "died";
            else if (now < state.Until) return;
            ended = state.BuffId;
            state.BuffId = 0;
            state.Until = double.NegativeInfinity;
            Interlocked.Decrement(ref _adrenalineHeld);
        }
        AdrenalineHolders.Remove(conn);
        if (conn.Closed || conn.LeavingArea) return;
        await global::Game.SendWorldRecord(PlayerOf(conn), PlayerId, InstanceEntities.RemoveBuff(PlayerId, ended),
            $"0x01c2 Allflame final-lantern Adrenaline ends ({why})");
        await SendAdrenalineStats(conn, why);
        module.Log($"{CharacterName(conn)}: final-lantern Adrenaline ended ({why})");
    }

    /// <summary><c>GameEvents.PlayerLeaving</c>: clears tracked final-lantern Adrenaline when leaving the run.</summary>
    public static void ForgetAdrenaline(ClientState conn)
    {
        if (Volatile.Read(ref _adrenalineHeld) == 0 || !AdrenalineHolders.TryGetValue(conn, out var state)) return;
        bool had;
        lock (state)
        {
            had = state.BuffId != 0;
            state.BuffId = 0;
            state.Until = double.NegativeInfinity;
        }
        if (had) Interlocked.Decrement(ref _adrenalineHeld);
        AdrenalineHolders.Remove(conn);
    }

    static async Task SendAdrenalineStats(ClientState conn, string why)
    {
        lock (conn.Monsters)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        var w = conn.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Allflame Adrenaline: {why})");
    }

    // ---- 5 Upgrade Pieces: eligibility, drops & QF 3563–3567 sync ----

    /// <summary>Fast O(1) eligibility check: true when the player has neither turned in piece
    /// <paramref name="pieceIndex"/> (<c>LQF 1824..1828</c>) nor carries its held quest flag (<c>QF 3563..3567</c>).</summary>
    public static bool NeedsPiece(ClientState conn, int pieceIndex)
    {
        if (PieceByIndex(pieceIndex) is not { } piece) return false;
        if (HasLeagueFlag(conn, piece.TurnInLeagueFlag)) return false;
        return !conn.HasQuestFlag(piece.QuestFlag);
    }

    /// <summary>Full drop check: <see cref="NeedsPiece"/>, not currently carried in any inventory, and not already
    /// lying on the ground in the current instance.</summary>
    public static bool CanDropPiece(ClientState conn, int pieceIndex)
    {
        if (PieceByIndex(pieceIndex) is not { } piece || !NeedsPiece(conn, pieceIndex)) return false;
        if (CarriesPiece(conn, piece)) return false;
        return !IsOnGround(conn, piece.TypeHash);
    }

    /// <summary>Per-kill <c>GameEvents.Drops</c> hook inside an Allflame Chart or Voyage for upgrade pieces 1, 3, and 4
    /// (zero allocations on normal/magic kills or when pieces are already owned/turned in):
    /// <list type="bullet">
    /// <item>Piece 1 (<c>Deadcaller Bellows</c>): special Chart room rare/unique kill if not yet received (G: 35%).</item>
    /// <item>Piece 3 (<c>Spectral Infuser</c>): Captainsbane (<c>#10589</c>, 100% G) or Chart/Voyage rare/unique at
    /// level ≥ 55 (G: 25%).</item>
    /// <item>Piece 4 (<c>Alchemical Exchange</c>): rare/unique Wriggling Horrors in <c>Infested Bathyspheres</c>
    /// (<c>#21–22</c>, 100% G) or Voyage rare/unique at level ≥ 73 (G: 25%).</item>
    /// </list>
    /// Quest items drop without Dredge flags.</summary>
    public static void Drops(IModuleContext module, DropRoll roll, int runLevel, bool voyage, int roomId, bool specialRoom)
    {
        if (roll.Monster.Rarity < MonsterRarity.Rare) return;
        var conn = roll.Conn;

        // Piece 1 (Deadcaller Bellows): special Chart room fallback if not yet obtained on first Chart completion.
        if (!voyage && specialRoom && NeedsPiece(conn, 0) && CanDropPiece(conn, 0))
        {
            // G: 35% chance from special Chart room rare/unique kills.
            int chance = Setting(Piece1DropChanceSetting, 35, 0, 100);
            if (chance > 0 && (chance >= 100 || Random.Shared.Next(100) < chance))
            {
                roll.Items.Add(DeadcallerBellowsId);
                module.Log($"{CharacterName(conn)}: {roll.Monster.Name} drops Deadcaller Bellows (special Chart room {roomId})");
            }
        }

        // Piece 3 (Spectral Infuser): Captainsbane (#10589) or Chart/Voyage rare/unique at area level >= 55.
        if (NeedsPiece(conn, 2) && CanDropPiece(conn, 2))
        {
            bool captainsbane = roll.Monster.VarietyId.Equals(DeepwaterCursedDucats.CaptainsbaneVariety, StringComparison.Ordinal);
            if (captainsbane || runLevel >= Piece3MinAreaLevel)
            {
                // G: guaranteed from Captainsbane; 25% chance on eligible level >= 55 rare/unique kills.
                int chance = captainsbane ? 100 : Setting(Piece3DropChanceSetting, 25, 0, 100);
                if (chance > 0 && (chance >= 100 || Random.Shared.Next(100) < chance))
                {
                    roll.Items.Add(SpectralInfuserId);
                    module.Log($"{CharacterName(conn)}: {roll.Monster.Name} drops Spectral Infuser"
                        + (captainsbane ? " (Captainsbane)" : $" (level {runLevel})"));
                }
            }
        }

        // Piece 4 (Alchemical Exchange): rare Wriggling Horrors in Infested Bathyspheres (#21-22) or Voyage rare/unique at level >= 73.
        if (NeedsPiece(conn, 3) && CanDropPiece(conn, 3))
        {
            bool bathysphereHorror = (roomId is 21 or 22) && IsWrigglingHorror(roll.Monster.VarietyId);
            if (bathysphereHorror || (voyage && runLevel >= Piece4MinAreaLevel))
            {
                // G: guaranteed from rare Wriggling Horrors in Infested Bathyspheres; 25% on level >= 73 Voyage rare/unique kills.
                int chance = bathysphereHorror ? 100 : Setting(Piece4DropChanceSetting, 25, 0, 100);
                if (chance > 0 && (chance >= 100 || Random.Shared.Next(100) < chance))
                {
                    roll.Items.Add(AlchemicalExchangeId);
                    module.Log($"{CharacterName(conn)}: {roll.Monster.Name} drops Alchemical Exchange"
                        + (bathysphereHorror ? $" (Infested Bathyspheres room {roomId})" : $" (Voyage level {runLevel})"));
                }
            }
        }
    }

    /// <summary>Special Chart room chest helper for Piece 1 (<c>Deadcaller Bellows</c>, G: 35% chance when still needed).</summary>
    public static ItemDrops.Drop? TryChestPiece1(IModuleContext module, ClientState conn, bool voyage, bool specialRoom, int level)
    {
        if (voyage || !specialRoom || !CanDropPiece(conn, 0)) return null;
        int chance = Setting(Piece1DropChanceSetting, 35, 0, 100);
        if (chance <= 0 || (chance < 100 && Random.Shared.Next(100) >= chance)) return null;
        if (ItemsFor(conn).Find(DeadcallerBellowsId) is not { Supported: true } type) return null;
        module.Log($"{CharacterName(conn)}: special Chart room chest drops {type.Name}");
        return new ItemDrops.Drop(ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, type);
    }

    /// <summary>Piece 1 (<c>Deadcaller Bellows</c>) on Chart completion and Piece 2 (<c>Sulphur Purifier</c>) on Voyage
    /// completion (<c>DeepwaterStart.Finish</c>). Places the piece into <c>MainInventory</c>, <c>ExpandedInventory</c>
    /// (if granted), or the deck <c>CollectionRow (114)</c>, and sets its held quest flag (<c>QF 3563</c>/<c>3564</c>).
    /// If bags and collection are full, Piece 2 remains reclaimable via Valerie's fallback <c>Offer#138</c>.</summary>
    public static Task TryGrantRunCompletionPiece(IModuleContext module, ClientState conn, bool voyage, bool live) =>
        TryGrantPieceToInventory(module, conn, voyage ? 1 : 0, live, voyage ? "Voyage completion" : "Chart completion");

    /// <summary>Hub fallback for Piece 2 (<c>Sulphur Purifier</c>) on The Sovereign after Voyage completion
    /// (<c>LQF 1823</c>) and Spirit Flame (<c>LQF 1859</c>) if not yet held or turned in; complements Valerie's
    /// <c>Offer#138</c> (<c>NPCTalk#3954/#3982</c>).</summary>
    public static Task TryGrantHubSulphurPurifier(IModuleContext module, ClientState conn)
    {
        if (!HasLeagueFlag(conn, DeepwaterConstants.FirstVoyageLeagueFlag)
            || !HasLeagueFlag(conn, VesperFlameGivenLeagueFlag))
            return Task.CompletedTask;
        return TryGrantPieceToInventory(module, conn, 1, live: !conn.Closed && !conn.LeavingArea, "post-Voyage hub grant");
    }

    /// <summary>Velka first-kill helper for Piece 5 (<c>Forbidden Scripture</c>, <c>BaseItemTypes#5430</c>, NOT Dredge),
    /// called by <c>DeepwaterVelka</c> on defeat.</summary>
    public static ItemDrops.Drop? VelkaDropPiece(IModuleContext module, ClientState conn, int level)
    {
        if (!CanDropPiece(conn, 4)) return null;
        if (ItemsFor(conn).Find(ForbiddenScriptureId) is not { Supported: true } type) return null;
        module.Log($"{CharacterName(conn)}: Velka drops {type.Name} (first-kill upgrade piece)");
        return new ItemDrops.Drop(ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, type);
    }

    static async Task TryGrantPieceToInventory(IModuleContext module, ClientState conn, int pieceIndex, bool live, string reason)
    {
        if (PieceByIndex(pieceIndex) is not { } piece || !NeedsPiece(conn, pieceIndex) || CarriesPiece(conn, piece))
            return;
        lock (conn.Monsters) if (HardcoreProgressBlocked(conn)) return;

        if (ItemsFor(conn).Find(piece.ItemId) is not { Supported: true } type) return;
        var items = ItemsOf(conn);
        var created = ItemCatalog.Create(type);
        var shape = ShapeOf(created) ?? new(type.Width, type.Height, type.Class);

        byte destRow = PlayerInventories.MainInventory;
        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            if (CarriesPiece(conn, piece)) return;
            var cell = items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf);
            if (cell is null && ExpandedInventoryGranted(conn))
            {
                destRow = PlayerInventories.ExpandedInventory;
                cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
            }
            if (cell is null)
            {
                destRow = DeepwaterExtraction.CollectionRow;
                cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
            }
            if (cell is { } at)
                placed = items.Put(destRow, new InventoryItem(items.NewItemId(), at.X, at.Y, created));
        });

        if (placed is null)
        {
            module.Log($"{CharacterName(conn)}: could not grant {piece.Name} ({reason}): inventories full");
            return;
        }

        if (live && !conn.Closed && !conn.LeavingArea)
        {
            uint invId = PlayerInventories.InventoryId(destRow);
            await PlayerOf(conn).Send(
                InstanceItems.InventoryChange(invId, items.NextRevision(destRow), [], [placed]),
                $"0x00bf inventory {invId}: granted {piece.Name} ({reason})");
            var (remote, stream, ciphers) = conn.Wire;
            List<string> flagsToSet = destRow == DeepwaterExtraction.CollectionRow
                ? [piece.QuestFlag, DeepwaterConstants.StoredItems]
                : [piece.QuestFlag];
            await SetQuestFlags(remote, stream, ciphers, conn, flagsToSet, on: true);
        }
        module.Log($"{CharacterName(conn)}: granted {piece.Name} into row {destRow} ({placed.X}, {placed.Y}) ({reason})");
    }

    static bool IsWrigglingHorror(string varietyId) =>
        varietyId.Equals(WrigglingHorrorBase, StringComparison.Ordinal)
        || varietyId.Equals(WrigglingHorrorBathysphere, StringComparison.Ordinal);

    /// <summary>Syncs <c>QF 3563–3567</c> (<c>HaveDeepwaterUpgradeItem1..5</c>) to the upgrade pieces currently carried
    /// across the player's inventories. Sends nothing when all flags already match.</summary>
    public static async Task SyncHeldQuestFlags(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return;
        var pieces = Pieces;
        if (pieces.Count == 0) return;

        uint carriedMask = 0;
        var items = ItemsOf(conn);
        foreach (var (_, it) in QuestItems.Carried(items))
            if (PieceByHash(it.Item.TypeHash) is { Index: >= 0 and < 32 } piece)
                carriedMask |= 1u << piece.Index;

        List<string>? qfSet = null, qfClear = null;
        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            bool want = (carriedMask & (1u << piece.Index)) != 0;
            bool has = conn.HasQuestFlag(piece.QuestFlag);
            if (want && !has) (qfSet ??= []).Add(piece.QuestFlag);
            else if (!want && has) (qfClear ??= []).Add(piece.QuestFlag);
        }

        if (qfSet is null && qfClear is null) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (qfSet is not null) await SetQuestFlags(remote, stream, ciphers, conn, qfSet, on: true);
        if (qfClear is not null) await SetQuestFlags(remote, stream, ciphers, conn, qfClear, on: false);
    }

    /// <summary><c>GameEvents.MessagesHandled</c>: syncs <c>QF 3563–3567</c> only on item-moving/reward/command opcodes
    /// (0-alloc fast-exit on ordinary ticks/combat).</summary>
    public static Task OnMessagesHandled(ClientState conn, IReadOnlyList<ushort> ids)
    {
        for (int i = 0; i < ids.Count; i++)
            if (MayChangeUpgradePieces(ids[i])) return SyncHeldQuestFlags(conn);
        return Task.CompletedTask;
    }

    static bool MayChangeUpgradePieces(ushort id) => id is
        InstanceItems.PickUpItemId or
        InstanceItems.PlaceItemId or
        InstanceItems.DropItemId or
        InstanceItems.ItemActionId or
        InstanceItems.CollectionTakeId or
        InstanceItems.CommandId or
        InstanceNpcs.BuyId or
        InstanceNpcs.CtrlBuyId or
        InstanceNpcs.TakeAllRewardsId or
        InstanceChat.ChatId
        || QuestItems.MovesItems(id);

    // ---- Vesper turn-in (NPCTalk#3920–3924) ----

    /// <summary>Computes the new league quest flags to set when turning in upgrade piece <paramref name="pieceIndex"/>
    /// (<c>0..4</c>): its piece flag (<c>1824..1828</c>), cumulative total flags (<c>1829..1833</c>), and crafting copy-cap
    /// unlocks (<c>count &gt;= 1 → 1876</c>; <c>count &gt;= 3 → 1877, 1878, 1818</c>). Pure helper for tests.</summary>
    public static (int TotalTurnedIn, List<int> FlagsToSet) ComputeTurnInFlags(IReadOnlySet<int> currentLeagueFlags, int pieceIndex)
    {
        if (PieceByIndex(pieceIndex) is not { } piece) return (0, []);
        var next = new HashSet<int>(currentLeagueFlags) { piece.TurnInLeagueFlag };

        int total = 0;
        for (int i = 0; i < TurnInLeagueFlags.Length; i++)
            if (next.Contains(TurnInLeagueFlags[i])) total++;

        var toSet = new List<int>(8);
        void Add(int flag)
        {
            if (flag > 0 && !currentLeagueFlags.Contains(flag) && !toSet.Contains(flag))
                toSet.Add(flag);
        }

        Add(piece.TurnInLeagueFlag);
        for (int i = 0; i < total && i < TotalTurnedInLeagueFlags.Length; i++)
            Add(TotalTurnedInLeagueFlags[i]);
        if (total >= 1) Add(ThreeOutputCraftLeagueFlag);
        if (total >= 3)
        {
            Add(FourOutputCraftLeagueFlag);
            Add(EndgameCraftLeagueFlag);
            Add(SpecialCraftsUnlockedLeagueFlag);
        }
        return (total, toSet);
    }

    /// <summary><c>GameEvents.NpcOptionPicked</c> handler for Vesper's 5 ship upgrade turn-ins (<c>NPCTalk#3920–3924</c>).
    /// Returns <c>true</c> for unrelated options or after completing a valid turn-in (so Vesper's turn-in dialogue/audio
    /// still plays); returns <c>false</c> to refuse when the piece is missing or already turned in.</summary>
    public static async Task<bool> TryTurnIn(IModuleContext module, ClientState conn, NpcCatalog.Talk talk)
    {
        if (talk.Npc != DeepwaterCrafting.VesperNpc || PieceByTalkRow(talk.Row) is not { } piece)
            return true;

        var player = PlayerOf(conn);
        if (HasLeagueFlag(conn, piece.TurnInLeagueFlag))
        {
            module.Log($"{player.Character} <= Vesper NPCTalk {talk.Row} \"{talk.Option}\": refused (LQF {piece.TurnInLeagueFlag} already set)");
            return false;
        }

        var items = ItemsOf(conn);
        (byte Row, InventoryItem Item)? carried = null;
        foreach (var entry in QuestItems.Carried(items))
        {
            if (entry.Item.Item.TypeHash == piece.TypeHash) { carried = entry; break; }
        }
        if (carried is not { } held)
        {
            module.Log($"{player.Character} <= Vesper NPCTalk {talk.Row} \"{talk.Option}\": refused ({piece.Name} not carried)");
            await SyncHeldQuestFlags(conn);
            return false;
        }

        bool removed = false;
        Saves.Atomically(() =>
        {
            removed = items.Remove(held.Row, held.Item.Id) is not null;
        });
        if (!removed)
        {
            module.Log($"{player.Character} <= Vesper NPCTalk {talk.Row} \"{talk.Option}\": refused ({piece.Name} disappeared)");
            return false;
        }

        uint invId = PlayerInventories.InventoryId(held.Row);
        await player.Send(
            InstanceItems.InventoryChange(invId, items.NextRevision(held.Row), [held.Item.Id], []),
            $"0x00bf inventory {invId}: handed in {piece.Name} (item {held.Item.Id}) to Vesper");

        HashSet<int> currentFlags;
        lock (conn.Monsters) currentFlags = [.. conn.LeagueQuestFlags];
        var (totalTurnedIn, flagsToSet) = ComputeTurnInFlags(currentFlags, piece.Index);

        var (remote, stream, ciphers) = conn.Wire;
        if (!CarriesPiece(conn, piece) && conn.HasQuestFlag(piece.QuestFlag))
            await SetQuestFlags(remote, stream, ciphers, conn, [piece.QuestFlag], on: false);
        if (flagsToSet.Count > 0)
            await SetLeagueQuestFlags(remote, stream, ciphers, conn, flagsToSet, on: true);

        // Reveal & animate newly unlocked below-deck bench stage prop(s) immediately (Stage2..Stage6).
        for (int stage = 2; stage <= totalTurnedIn + 1 && stage <= BenchStageTypes.Length; stage++)
        {
            int lqf = TotalTurnedInLeagueFlags[stage - 2];
            bool newlyUnlocked = flagsToSet.Contains(lqf);
            await UpdateUpgradeBenchStage(conn, stage, visible: true, animate: newlyUnlocked);
        }

        module.Log($"{player.Character} <= Vesper turn-in {piece.Name} (NPCTalk#{talk.Row}): {totalTurnedIn}/{PieceCount} upgrades turned in, LQF [{string.Join(", ", flagsToSet)}]");
        return true;
    }

    // ---- Below-deck GhostflameCraftingBenchProps_Stage1..6 ----

    sealed class BenchProp
    {
        public required int Stage;
        public required string Type;
        public required uint Id;
        public required int X, Y, Reach;
        public required float Rotation;
        public float? Z;
        public string? Model;
    }

    sealed class BenchPropGroup
    {
        public readonly Lock Gate = new();
        public readonly List<BenchProp> List = [];
    }

    static readonly ConditionalWeakTable<InstanceState, BenchPropGroup> BenchProps = new();

    /// <summary>Deserialized components for <c>GhostflameCraftingBenchProps_Stage1..6</c> (reads <c>client.db</c>
    /// <c>object_types</c> if present, falling back to the exact <c>.ot</c> component order).</summary>
    public static IReadOnlyList<string> BenchStageComponents(int stage)
    {
        if (stage is < 1 or > 6) return [];
        string type = BenchStageTypes[stage - 1];
        if (QuestObjectTypes.Find(type) is { Components.Count: > 0 } info && QuestObject.CanSerialize(info.Components, true))
            return info.Components;
        return stage == 1 ? Stage1FallbackComponents : Stage2To6FallbackComponents;
    }

    /// <summary>Default <see cref="StateMachineData"/> for <c>GhostflameCraftingBenchProps_Stage1..6</c> (Stage 1:
    /// <c>disable_targetable</c>; Stage 2..6: <c>visible, animate</c>).</summary>
    public static StateMachineData BenchStageStateMachine(int stage)
    {
        if (stage is < 1 or > 6) return new StateMachineData([], 0);
        string type = BenchStageTypes[stage - 1];
        if (ObjectTypes.All().Where(t => t.Type == type)
            .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates()))
            .FirstOrDefault() is { } loaded)
            return loaded;
        return stage == 1
            ? new StateMachineData([0], 0, ["disable_targetable"])
            : new StateMachineData([0, 0], 0, ["visible", "animate"]);
    }

    static string BenchStageAnimatedModel(int stage) =>
        $"Metadata/Terrain/Leagues/Deepwater/Hub/DeepwaterCraftingBench_Stage{stage}.ao";

    /// <summary>Spawns <c>GhostflameCraftingBenchProps_Stage1..6</c> below deck on The Sovereign (called from
    /// <c>SpawnDeck</c>). Stage 1 (<c>globals.vesperbench_stage1</c>) is clickable for tutorial crafting while
    /// <c>LQF 1859</c> is clear; Stages 2..6 reflect <c>LQF 1829..1833</c>.</summary>
    public static async Task SpawnBenchProps(IModuleContext module, ClientState conn)
    {
        var player = PlayerOf(conn);
        if (player.AreaId != DeepwaterModule.HubId || conn.Instance is not { } instance) return;

        var group = BenchProps.GetOrCreateValue(instance);
        BenchProp[] snapshot;
        lock (group.Gate)
        {
            if (group.List.Count == 0)
            {
                var tiles = conn.Layout.Level?.TileQuestObjects ?? [];
                var rooms = conn.Layout.Level?.RoomObjects ?? [];
                for (int stage = 1; stage <= BenchStageTypes.Length; stage++)
                {
                    string type = BenchStageTypes[stage - 1];
                    var placed = FindPlacement(tiles, type) ?? FindPlacement(rooms, type);
                    var (defX, defY) = DefaultBenchPositions[stage - 1];
                    int size = QuestObjectTypes.Find(type)?.Size ?? 1;
                    string defaultModel = BenchStageAnimatedModel(stage);
                    string? modelOverride = placed is not null && !string.IsNullOrEmpty(placed.Model)
                        && !placed.Model.Equals(defaultModel, StringComparison.OrdinalIgnoreCase)
                        ? placed.Model : null;
                    group.List.Add(new BenchProp
                    {
                        Stage = stage,
                        Type = type,
                        Id = conn.TakeObjectId(),
                        X = placed?.X ?? defX,
                        Y = placed?.Y ?? defY,
                        Reach = size + 3,
                        Rotation = placed?.Rotation ?? 0f,
                        Z = placed?.Z,
                        Model = modelOverride,
                    });
                }
            }
            snapshot = [.. group.List];
        }

        bool vesperAwake = HasLeagueFlag(conn, VesperFlameGivenLeagueFlag);
        foreach (var prop in snapshot)
        {
            var components = BenchStageComponents(prop.Stage);
            var states = BenchStageStateMachine(prop.Stage);
            if (!QuestObject.CanSerialize(components, true))
            {
                module.Log($"{player.Character}: {prop.Type} cannot be serialized ({string.Join(", ", components)})");
                continue;
            }
            StateMachineData initial = prop.Stage == 1
                ? states.Set("disable_targetable", vesperAwake ? 1 : 0)
                : states.Set("visible", HasLeagueFlag(conn, TotalTurnedInLeagueFlags[prop.Stage - 2]) ? 1 : 0).Set("animate", 0);
            var blob = new QuestObject(prop.X, prop.Y, components, prop.Rotation, prop.Z, prop.Model, Blocked: false)
            {
                StateMachine = initial,
            }.Blob();
            string stateLabel = prop.Stage == 1
                ? $"disable_targetable={(vesperAwake ? 1 : 0)}"
                : $"visible={(HasLeagueFlag(conn, TotalTurnedInLeagueFlags[prop.Stage - 2]) ? 1 : 0)}, animate=0";
            if (!await global::Game.SendWorldCreate(player, prop.Id,
                InstanceEntities.CreateObject(prop.Id, InstanceEntities.ObjectTypeHash(prop.Type), blob),
                $"0x02bb GhostflameCraftingBenchProps_Stage{prop.Stage} 0x{prop.Id:x} at ({prop.X}, {prop.Y}), {stateLabel}"))
                continue;
            await global::Game.SendWorldRecord(player, prop.Id, InstanceProtocol.EnterWakeZone(prop.Id));
        }
    }

    /// <summary>Updates <c>GhostflameCraftingBenchProps_Stage1</c>'s <c>disable_targetable</c> state live via
    /// <c>0x02bc</c> (called when <c>LQF 1859 DeepwaterValVesperFlameGiven</c> is set during <c>"The Allflame's Power"</c>
    /// so the client disables targeting and plays <c>flame_craft_01</c>).</summary>
    public static async Task UpdateStage1Bench(ClientState conn, bool disableTargetable)
    {
        if (conn.Instance is not { } instance || !BenchProps.TryGetValue(instance, out var group)) return;
        BenchProp? prop;
        lock (group.Gate) prop = group.List.FirstOrDefault(p => p.Stage == 1);
        if (prop is null) return;

        var components = BenchStageComponents(1);
        int smIndex = IndexOfComponent(components, "StateMachine");
        if (smIndex < 0) return;
        var update = BenchStageStateMachine(1).Set("disable_targetable", disableTargetable ? 1 : 0);
        foreach (var member in DeepwaterLoot.Members(conn))
            await global::Game.SendWorldRecord(PlayerOf(member), prop.Id,
                InstanceEntities.StateMachineUpdate(prop.Id, (byte)smIndex, update),
                $"0x02bc GhostflameCraftingBenchProps_Stage1 0x{prop.Id:x} disable_targetable={(disableTargetable ? 1 : 0)}");
    }

    /// <summary>Updates <c>GhostflameCraftingBenchProps_Stage2..6</c>'s <c>visible</c>/<c>animate</c> state live via
    /// <c>0x02bc</c> for all members on The Sovereign.</summary>
    public static async Task UpdateUpgradeBenchStage(ClientState conn, int stage, bool visible, bool animate)
    {
        if (stage is < 2 or > 6 || conn.Instance is not { } instance || !BenchProps.TryGetValue(instance, out var group)) return;
        BenchProp? prop;
        lock (group.Gate) prop = group.List.FirstOrDefault(p => p.Stage == stage);
        if (prop is null) return;

        var components = BenchStageComponents(stage);
        int smIndex = IndexOfComponent(components, "StateMachine");
        if (smIndex < 0) return;
        var update = BenchStageStateMachine(stage)
            .Set("visible", visible ? 1 : 0)
            .Set("animate", animate ? 1 : 0);
        foreach (var member in DeepwaterLoot.Members(conn))
            await global::Game.SendWorldRecord(PlayerOf(member), prop.Id,
                InstanceEntities.StateMachineUpdate(prop.Id, (byte)smIndex, update),
                $"0x02bc GhostflameCraftingBenchProps_Stage{stage} 0x{prop.Id:x} visible={(visible ? 1 : 0)}, animate={(animate ? 1 : 0)}");
    }

    /// <summary>Syncs all 6 below-deck bench stage props live to <paramref name="conn"/>'s current league flags.</summary>
    public static async Task SyncBenchProps(ClientState conn)
    {
        if (PlayerOf(conn).AreaId != DeepwaterModule.HubId) return;
        await UpdateStage1Bench(conn, HasLeagueFlag(conn, VesperFlameGivenLeagueFlag));
        for (int stage = 2; stage <= BenchStageTypes.Length; stage++)
            await UpdateUpgradeBenchStage(conn, stage, visible: HasLeagueFlag(conn, TotalTurnedInLeagueFlags[stage - 2]), animate: false);
    }

    /// <summary><c>C→S 0x0024</c> (<c>Interact</c>) handler for <c>GhostflameCraftingBenchProps_Stage1</c>: opens
    /// <c>DeepwaterCraftingWindow</c> (<c>0x69</c>) during the pre-Fairgraves crafting tutorial (while
    /// <c>LQF 1859</c> is clear and no craft is in progress). Returns <c>false</c> for any other object.</summary>
    public static async Task<bool> UseStage1Bench(IModuleContext module, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, double at, bool isHubOwner)
    {
        var player = PlayerOf(conn);
        if (player.AreaId != DeepwaterModule.HubId || conn.Instance is not { } instance
            || !BenchProps.TryGetValue(instance, out var group))
            return false;

        BenchProp? stage1;
        lock (group.Gate) stage1 = group.List.FirstOrDefault(p => p.Stage == 1 && p.Id == use.TargetId);
        if (stage1 is null) return false;

        if (!await WorldOf(conn).WalkUpTo(stage1.X, stage1.Y, stage1.Reach, at)) return true;

        string? refusal = !isHubOwner ? "not the hub owner"
            : HasLeagueFlag(conn, VesperFlameGivenLeagueFlag) ? "Vesper is already awake (LQF 1859)"
            : ItemsOf(conn).Items(DeepwaterCrafting.OutputRow).Count > 0 || conn.HasQuestFlag(DeepwaterConstants.CraftInProgress)
                ? "an Allflame craft is already in progress"
            : null;

        if (refusal is not null)
        {
            module.Log($"{player.Character} <= 0x0024 GhostflameCraftingBenchProps_Stage1 0x{stage1.Id:x}: refused ({refusal})");
            await DeepwaterLoot.EndAction(conn);
            return true;
        }

        await player.Send(InstanceWaypoints.OpenUi(DeepwaterCrafting.CraftingUi),
            $"0x007b open DeepwaterCraftingWindow (kind 0x{DeepwaterCrafting.CraftingUi:x2}) from GhostflameCraftingBenchProps_Stage1");
        await DeepwaterLoot.EndAction(conn);
        return true;
    }

    // ---- Internal helpers ----

    static PlacedTileObject? FindPlacement(IReadOnlyList<PlacedTileObject> list, string type)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Type.Equals(type, StringComparison.OrdinalIgnoreCase)) return list[i];
        return null;
    }

    static int IndexOfComponent(IReadOnlyList<string> components, string name)
    {
        for (int i = 0; i < components.Count; i++)
            if (components[i].Equals(name, StringComparison.Ordinal)) return i;
        return -1;
    }

    static bool HasLeagueFlag(ClientState conn, int flag)
    {
        if (flag <= 0) return false;
        lock (conn.Monsters) return conn.LeagueQuestFlags.Contains(flag);
    }

    static bool CarriesPiece(ClientState conn, UpgradePiece piece)
    {
        foreach (var (_, it) in QuestItems.Carried(ItemsOf(conn)))
            if (it.Item.TypeHash == piece.TypeHash) return true;
        return false;
    }

    static bool IsOnGround(ClientState conn, uint typeHash) =>
        conn.Instance is { } inst && inst.Ground.All().Any(e => e.Item.Item.TypeHash == typeHash);

    static int Setting(string key, int fallback, int min, int max) =>
        SystemSettings.Default.Values.TryGetValue(key, out var v) && double.IsFinite(v)
            ? (int)Math.Clamp(v, min, max) : fallback;
}
