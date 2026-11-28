using GameServer.Core.Protocol.Instance;


/// <summary>What a module can do with a player's world (<see cref="Program.WorldOf(IPlayer)"/>): objects, walking, windows, crafting
/// and saved data. Messages go through <see cref="IPlayer.Send"/>, built with GameServer.Core's protocol, which modules may use.</summary>
public interface IPlayerWorld
{
    /// <summary>The area's <c>WorldAreas</c> HASH16.</summary>
    ushort AreaHash { get; }

    /// <summary>Where a player arrives in this area (cells).</summary>
    (int X, int Y) Spawn { get; }

    /// <summary>The player's own object exists on the client.</summary>
    bool PlayerReady { get; }

    /// <summary>A free object id in this area.</summary>
    uint NewObjectId();

    /// <summary>A square of cells around an object blocks the server's walking.</summary>
    void SetBlocker(uint objectId, int x, int y, int size);

    /// <summary>Removes <see cref="SetBlocker"/>'s square.</summary>
    void RemoveBlocker(uint objectId);

    /// <summary>Moves the player out of a blocker that was placed on them.</summary>
    Task PushOutOfCollision();

    /// <summary>Places a piece of hideout furniture in one of the scene's reserved spots, or where the character has moved it
    /// (<paramref name="editorKey"/>), with its collision, movable in the hideout editor. Null outside a hideout, or when the
    /// object type cannot be written.</summary>
    Task<uint?> SpawnHideoutStation(HideoutSlot slot, string metadataPath, string editorKey, string name);

    /// <summary>Where <see cref="SpawnHideoutStation"/>'s object stands now.</summary>
    (int X, int Y)? StationAt(uint objectId);

    /// <summary>Walks to within reach of a cell. True when the player arrived and has not started anything else.</summary>
    Task<bool> WalkUpTo(int x, int y, int reach, double requestedAt);

    /// <summary>As <see cref="WalkUpTo"/>, to a station at its interaction size.</summary>
    Task<bool> WalkUpToStation(uint objectId, double requestedAt);

    /// <summary>Opens a native window as this module's: any other window closing it clears <see cref="OpenWindowOwner"/>.</summary>
    Task OpenWindow(string owner, byte ui, string log);

    /// <summary>Who opened the window that is open now, if a module did.</summary>
    string? OpenWindowOwner { get; }

    /// <summary>Rebuilds the character's stats and sends them.</summary>
    Task RefreshStats();

    /// <summary>Moves the player to an area by its <c>WorldAreas</c> id; false when it has no known way in.</summary>
    Task<bool> TravelTo(string areaId, string reason);

    /// <summary>Moves the player using an explicit <see cref="PendingTransfer"/> (supporting target instance id,
    /// new-instance creation, custom arrival coordinates, and transfer callbacks).</summary>
    Task<bool> TravelToAsync(PendingTransfer transfer, string reason);

    /// <summary>Spawns a clickable standalone portal at <c>(x, y)</c> leading to <paramref name="targetAreaId"/>
    /// (with optional target instance, use count, and automatic return portal back to the parent instance).
    /// Returns the portal object id, or null if the target area is unknown or the connection is closed.</summary>
    Task<uint?> SpawnPortalAsync(int x, int y, string targetAreaId, PortalOptions? options = null);

    /// <summary>Removes a standalone custom portal spawned via <see cref="SpawnPortalAsync"/>.</summary>
    Task<bool> RemovePortalAsync(uint objectId);

    /// <summary>Finds a walkable cell near <paramref name="near"/> (defaulting to the player's current position)
    /// between <paramref name="minRadius"/> and <paramref name="maxRadius"/> cells, respecting optional avoidance points.</summary>
    (int X, int Y)? FindOpenSpot((int X, int Y)? near = null, int minRadius = 0, int maxRadius = 40,
        IReadOnlyList<(int X, int Y)>? avoid = null, int minAvoidDistance = 12);

    /// <summary>Spawns an interactable or decorative world object (<see cref="QuestObject"/>) for a stock or
    /// runtime-overlaid <c>.ot</c> type at <c>(x, y)</c>, wakes it, and optionally registers collision.
    /// Returns the created object id, or null when the type is unknown or cannot be serialized.</summary>
    Task<uint?> SpawnWorldObjectAsync(string metadataPath, int x, int y, WorldObjectOptions? options = null);

    /// <summary>Removes a world object and any blocker registered for it.</summary>
    Task<bool> RemoveWorldObjectAsync(uint objectId, string? reason = null);

    /// <summary>Spawns a monster matching <paramref name="varietyQuery"/> (row, id, or name; including runtime
    /// <see cref="MonsterCatalog"/> overlays) at <c>(x, y)</c>. Returns the monster object id, or null if unknown.</summary>
    Task<uint?> SpawnMonsterAsync(string varietyQuery, int x, int y, MonsterSpawnOptions? options = null);

    /// <summary>Applies a timed visual buff (<c>BuffDefinitions</c> row, including runtime <see cref="BuffRows"/>
    /// overlays) and optional stat modifiers to a player or monster entity for <paramref name="seconds"/>,
    /// automatically clearing both the icon and stats when it expires.</summary>
    Task<bool> ApplyTimedBuffAsync(uint targetObjectId, ushort buffRow, IReadOnlyDictionary<uint, int>? stats,
        double seconds, uint sourceObjectId = 0);

    /// <summary>What this module keeps per account and league, as text under keys it chooses.</summary>
    ISavedData AccountData { get; }

    /// <summary>What this module keeps per character.</summary>
    ISavedData CharacterData { get; }

    /// <summary>What this module keeps shared across all accounts and characters in this player's league.</summary>
    ISavedData LeagueData { get; }

    /// <summary>The item rules of the player's data set.</summary>
    IItemCrafting Crafting { get; }

    /// <summary>The NPC dialogue plays this option's text and audio (its index among the NPC's options, its <c>NPCTalk</c> row and text,
    /// for the log); Continue returns to the options.</summary>
    Task NpcSay(string npc, string option, int index, int row);

    /// <summary>Ends the dialogue state without closing the window: another screen takes over.</summary>
    void NpcEndTalk();

    /// <summary>Closes the NPC dialogue.</summary>
    Task NpcClose(string log);

    /// <summary>Identifies every unidentified item in the main inventory, free.</summary>
    Task NpcIdentifyItems();

    /// <summary>Opens a quest's reward window. False when the offer has nothing for this character, or was taken.</summary>
    Task<bool> NpcOfferReward(int offer);

    /// <summary>The id of the one item in an inventory row, 0 when there is none.</summary>
    uint ItemIdIn(byte row);

    /// <summary>Crafts the one item in an inventory row: the new item and all its costs, paid from the inventory then the stash,
    /// commit together and reach the client. Null, changing nothing, when the slot, the craft or the payment fails.</summary>
    Task<InventoryItem?> CraftInSlot(byte row, Func<Item, CraftResult?> craft, string log);

    /// <summary>Pays a currency (from the inventory, then the stash) and gold; <paramref name="commit"/> saves what they buy in the same
    /// transaction. False, changing nothing, when the player holds too little.</summary>
    Task<bool> PayCurrency(uint currency, int amount, uint gold, Func<bool> commit, string log);

    /// <summary>Puts as much of a currency as fits into the main inventory; <paramref name="commit"/> records how much in the same transaction.
    /// Returns how much went in.</summary>
    Task<int> ReceiveCurrency(uint currency, int amount, Func<int, bool> commit, string log);

    /// <summary>The account's current gold in this player's league.</summary>
    uint Gold { get; }

    /// <summary>Adds (or subtracts, clamped at 0) gold in this player's league stash and syncs <c>S→C 0x02aa</c>.</summary>
    Task<uint> AddGoldAsync(long delta, string? reason = null);

    /// <summary>Sets the character's level (<c>1..100</c>), persists experience, syncs <c>0x02bc</c>/<c>0x01b3</c>/<c>0x0033</c>,
    /// notifies Auth (<c>set_level</c>), and fires <see cref="GameEvents.LevelUp"/> when raising level.</summary>
    Task<bool> SetLevelAsync(int level, string? reason = null);

    /// <summary>Whether the character holds <paramref name="flag"/>; with <paramref name="earnedOnly"/>, excludes
    /// login-granted unearned flags (<c>UnearnedQuestFlags</c>).</summary>
    bool HasQuestFlag(string flag, bool earnedOnly = false);

    /// <summary>Sets or clears character quest flags (clearing <c>UnearnedQuestFlags</c> when setting so Kitava penalties,
    /// Pantheon unlocks, and endgame gates apply), saves, and syncs the client.</summary>
    Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true);

    /// <summary>Sets or clears account-wide league quest flags in this player's league and syncs the client.</summary>
    Task SetLeagueQuestFlagsAsync(IEnumerable<int> flags, bool on = true);

    /// <summary>Activates waypoints for the given <c>WorldAreas</c> Ids, saves, and syncs <c>0x02bc</c> + <c>0x004c</c>.
    /// Returns how many new waypoints were added.</summary>
    Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null);

    /// <summary>The character's current passive refund points.</summary>
    ushort RefundPoints { get; }

    /// <summary>Sets the character's passive refund points, persists, updates optimistic passive snapshot, and syncs <c>0x0033</c>.</summary>
    Task<ushort> SetRefundPointsAsync(ushort points);

    /// <summary>Awards Labyrinth ascendancy point tiers (<c>AscendancyRules.AwardKeys</c>: normal, cruel, merciless, eternal),
    /// persists, and syncs <c>0x0033</c>. Returns total earned ascendancy points.</summary>
    Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys);

    /// <summary>Allocates ordinary/ascendancy passive nodes (with optional mastery effect) using the server's normal
    /// connectivity and budget validation. Returns null on success or an error reason.</summary>
    Task<string?> AllocatePassivesAsync(IReadOnlyList<ushort> nodes, ushort? masteryEffect = null, string? reason = null);

    /// <summary>Lists characters belonging to this player's account in the player's current league, respecting exact
    /// league/SSF isolation and Hardcore death state.</summary>
    IReadOnlyList<CharacterSaves.LeagueCharacterInfo> GetLeagueCharacters(bool includeDead = false);
}

/// <summary>A reserved spot on every hideout's floor: there whether or not a module uses it, so the rest of the layout does not
/// change with the modules.</summary>
public enum HideoutSlot
{
    /// <summary>For something about 8 cells around.</summary>
    Large,
    /// <summary>For something about 4 cells around.</summary>
    Small,
    /// <summary>A second reserved spot for something about 4 cells around, separate from <see cref="Small"/>.</summary>
    Small2,
}

/// <summary>What a craft makes and what it costs: currency type hash and amount.</summary>
public sealed record CraftResult(Item Item, IReadOnlyList<(uint Currency, int Amount)> Costs);

/// <summary>Text a module saves. The key is the module's own; use its id in it.</summary>
public interface ISavedData
{
    /// <summary>The text under a key, null when there is none.</summary>
    string? Get(string key);

    /// <summary>Changes a key's text atomically; a null result changes nothing and returns false.</summary>
    bool Change(string key, Func<string?, string?> change);

    /// <summary>Removes a key atomically; true when the key existed and was removed.</summary>
    bool Remove(string key) => false;

    /// <summary>Lists saved keys (optionally filtered by ordinal <paramref name="prefix"/>), sorted ordinally.</summary>
    IReadOnlyList<string> Keys(string? prefix = null) => [];
}

/// <summary>Options for <see cref="IPlayerWorld.SpawnPortalAsync"/>.</summary>
public sealed record PortalOptions(
    ulong TargetInstance = 0,
    bool NewInstance = true,
    (int X, int Y)? ArriveAt = null,
    int? UsesLeft = null,
    bool SpawnReturnPortal = false,
    string? Owner = null,
    ushort? Variation = null);

/// <summary>Options for <see cref="IPlayerWorld.SpawnWorldObjectAsync"/>.</summary>
public sealed record WorldObjectOptions(
    float Rotation = 0,
    float? Height = null,
    string? Model = null,
    byte TransitionState = 1,
    bool Opened = false,
    bool Blocked = false,
    int BlockerSize = 0,
    bool Awake = true,
    bool HideMinimapIcon = false,
    StateMachineData? StateMachine = null,
    IReadOnlyList<(uint Key, int Value)>? Stats = null,
    IReadOnlyList<EntityVariable>? Variables = null);

/// <summary>Options for <see cref="IPlayerWorld.SpawnMonsterAsync"/>.</summary>
public sealed record MonsterSpawnOptions(
    byte Level = 0,
    MonsterRarity Rarity = MonsterRarity.Normal,
    string? Name = null,
    bool Friendly = false,
    float Rotation = 0,
    int? LifePercent = null,
    MonsterModSet? Mods = null,
    MonsterModSet? ExtraMods = null,
    IReadOnlyList<(uint Key, int Value)>? ExtraStats = null,
    bool NonBlocking = false,
    bool InstanceOwned = true);

/// <summary>The item rules a crafting module builds on, for one data set.</summary>
public interface IItemCrafting
{
    /// <summary>A <c>BaseItemTypes</c> Id's hash, null when unknown.</summary>
    uint? TypeHash(string id);

    /// <summary>Every <c>ModType</c> name of the mods.</summary>
    IReadOnlySet<string> ModTypes { get; }

    /// <summary>Whether the item accepts affix changes (not corrupted, mirrored, vestigial or of an unmodifiable base).</summary>
    bool Modifiable(Item item);

    /// <summary>Whether an explicit mod of the item has one of these stats.</summary>
    bool HasExplicitStat(Item item, IReadOnlyCollection<string> stats);

    /// <summary>A reroll that must include a mod with this <c>Tags.dat</c> tag; null when none can.</summary>
    Item? RollRequiredTag(Item item, ItemRarity rarity, Random rng, string tag);

    /// <summary>A reroll where mod types the item has are this percent as likely.</summary>
    Item? RollAffinity(Item item, ItemRarity rarity, Random rng, int percent);

    /// <summary>Removes one random explicit mod.</summary>
    Item? Annul(Item item, Random rng);

    /// <summary>How many prefixes and suffixes a rare of this base holds.</summary>
    (int Prefix, int Suffix) RareMaxAffixes(Item item);

    /// <summary>Adds a mod with this tag, within the limits.</summary>
    Item? AddTaggedAffix(Item item, (int Prefix, int Suffix) max, Random rng, string tag);

    /// <summary>Turns one random explicit mod whose <c>ModType</c> is a key of the map into the nearest-level mod of the value's type.</summary>
    Item? ConvertMod(Item item, IReadOnlyDictionary<string, string> map, Random rng);
}
