using GameServer.Modding;

// What some GameEvents hand their subscribers (GameEvents.cs). `Player` is the player entity; `ModPlayer` is the same player as GameServer.Modding sees it.

/// <summary>A monster: its <c>MonsterVarieties.dat</c> id, name, level, rarity and where it is (cells).</summary>
public sealed record MonsterInfo(string VarietyId, string Name, int Level, GameServer.Core.Protocol.Instance.MonsterRarity Rarity, int X, int Y)
{
    /// <summary>Retained world identity; zero for callers without a registered object.</summary>
    public uint ObjectId { get; init; }
}

/// <summary>A monster the player killed, after its kill was committed (<see cref="GameEvents.MonsterKilled"/>).</summary>
public sealed record MonsterKill(Player Player, MonsterInfo Monster)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
}

/// <summary>An option picked in an NPC's dialogue (an <c>NPCTalk</c> row).</summary>
/// <param name="Player">Who picked it.</param>
/// <param name="Npc">The NPC's name.</param>
/// <param name="Category">The row's category: Gossip, Quests, QuestReward, BuyItems, SellItems …</param>
/// <param name="Option">Its text.</param>
/// <param name="Script">Its script.</param>
/// <param name="HasText">It has text and audio to play.</param>
/// <param name="Index">Its index among the NPC's options.</param>
/// <param name="Row">Its <c>NPCTalk</c> row.</param>
/// <param name="OpensGuildHideouts">A SelectHideout row's choice of list.</param>
/// <param name="RewardOffer">A QuestReward row's offer.</param>
public sealed record NpcOption(Player Player, string Npc, string Category, string Option, string Script, bool HasText, int Index, int Row,
    bool OpensGuildHideouts, int? RewardOffer)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
}

/// <summary>A new instance's monsters are placed (<see cref="GameEvents.AreaPopulated"/>): the monsters' level, the
/// population's seed (for an encounter's own rolls) and the cells the packs and bosses took, to keep an encounter
/// apart from them.</summary>
public sealed record AreaPopulated(Player Player, byte Level, int Seed, IReadOnlyList<(int X, int Y)> Taken)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
}

/// <summary>A gamble window as it opens (<see cref="GameEvents.Gambles"/>).</summary>
public sealed class GambleWindow
{
    public GambleWindow(Player player, string npc, int characterLevel) : this(player, npc, characterLevel, characterLevel) { }

    public GambleWindow(Player player, string npc, int characterLevel, int priceLevel)
    {
        Player = player;
        Npc = npc;
        CharacterLevel = characterLevel;
        PriceLevel = Math.Max(characterLevel, priceLevel);
    }

    public Player Player { get; }

    public IPlayer ModPlayer => Program.PlayerOf(Player);

    /// <summary>The NPC's <c>NPCs</c> id.</summary>
    public string Npc { get; }

    /// <summary>The character's level, which rolled item levels and offer unlock levels go by.</summary>
    public int CharacterLevel { get; }

    /// <summary>The level prices go by: at least <see cref="CharacterLevel"/>, raised to the account's league floor for a lower-level character.</summary>
    public int PriceLevel { get; }

    /// <summary>What the window offers, in order.</summary>
    public List<GambleOffer> Offers { get; } = [];
}

/// <summary>One offer of a gamble window: the item it shows (a <c>HiddenItem</c> row such as "Random Ring"), its price in gold,
/// what a purchase gives (null: nothing is bought) and the tab it is in.</summary>
public sealed record GambleOffer(ItemCatalog.BaseType Shown, uint Gold, Func<Random, GameServer.Core.Protocol.Instance.Item?> Roll, string Tab = "Gamble");

/// <summary>An item about to be placed into an inventory row (<see cref="GameEvents.InventoryPlacementRefusal"/>):
/// <paramref name="Replaced"/> is the single item that will be swapped onto the cursor, or null when none is displaced.</summary>
public sealed record InventoryPlacement(Player Player, byte Row, GameServer.Core.Protocol.Instance.Item Item,
    GameServer.Core.Protocol.Instance.InventoryItem? Replaced)
{
    /// <summary>Target cell X within the inventory row (0 for single-item rows or callers without grid coordinates).</summary>
    public byte X { get; init; }
    /// <summary>Target cell Y within the inventory row.</summary>
    public byte Y { get; init; }
    public IPlayer ModPlayer => Program.PlayerOf(Player);
}

/// <summary>An optional area encounter evaluating whether and at what level to spawn (<see cref="GameEvents.EncounterSpawn"/>).</summary>
public sealed class EncounterSpawnRequest(Player player, string kind, byte level, long chance, bool allowed)
{
    public Player Player { get; } = player;
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    /// <summary>Lowercase encounter kind identifier (e.g. <c>"mercenary"</c>).</summary>
    public string Kind { get; } = kind;
    /// <summary>Encounter level to spawn at (defaults to area level).</summary>
    public byte Level { get; set; } = level;
    /// <summary>Spawn chance percentage (0..100+).</summary>
    public long Chance { get; set; } = chance;
    /// <summary>Whether the current area is eligible to spawn this encounter.</summary>
    public bool Allowed { get; set; } = allowed;
    /// <summary>When true, guarantees spawning without a random roll.</summary>
    public bool Force { get; set; }
}

/// <summary>Evaluates companion follower eligibility and equipment rule overrides (<see cref="GameEvents.CompanionRules"/>).</summary>
public sealed class CompanionRuleCheck(Player player, bool canHavePermanentFollower)
{
    public Player Player { get; } = player;
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    /// <summary>Whether the player may hire and bring a permanent companion follower.</summary>
    public bool CanHavePermanentFollower { get; set; } = canHavePermanentFollower;
    /// <summary>When true, permanent companions ignore per-slot unique item restrictions (retaining disabled-unique,
    /// level, armour tag, weapon class, and socket checks).</summary>
    public bool AllowAllUniqueSlots { get; set; }
}

/// <summary>Evaluates whether a companion may equip an item in an equipment slot (<see cref="GameEvents.CompanionEquip"/>).</summary>
public sealed class CompanionEquipCheck(Player player, byte equipmentRow, GameServer.Core.Protocol.Instance.Item item,
    int companionLevel, bool isPermanent)
{
    public Player Player { get; } = player;
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    /// <summary>Equipment slot row (<see cref="PlayerInventories.BodyArmour"/>..<see cref="PlayerInventories.Belt"/>).</summary>
    public byte EquipmentRow { get; } = equipmentRow;
    public GameServer.Core.Protocol.Instance.Item Item { get; } = item;
    public int CompanionLevel { get; } = companionLevel;
    public bool IsPermanent { get; } = isPermanent;
    /// <summary>When true, waives per-slot unique item restrictions for this placement.</summary>
    public bool AllowUnique { get; set; }
    /// <summary>Optional refusal reason; non-null blocks equipping.</summary>
    public string? Refusal { get; set; }
}

/// <summary>What an area's first message carries for a module: terrain data, achievement sets.</summary>
public sealed record AreaHeader(byte[]? Terrain, IReadOnlyList<byte>? AchievementSets)
{
    public byte? Level { get; init; }
}

/// <summary>A checked device recipe before payment: content may resolve an abstract destination or refuse it.</summary>
public sealed class MapOpening(Player player, IReadOnlyList<MapDevice.Slot> slots, bool campaign,
    MapDevice.Opening? opening, string reason)
{
    public Player Player { get; } = player;
    public IReadOnlyList<MapDevice.Slot> Slots { get; } = slots;
    public bool Campaign { get; } = campaign;
    public ushort Target { get; init; }
    /// <summary>Input indices used by the selected recipe; null retains the checked device's complete input.</summary>
    public IReadOnlyList<int>? ConsumeSlots { get; set; }
    public MapDevice.Opening? Opening { get; set; } = opening;
    public string Reason { get; set; } = reason;
    public bool ReturnToSource { get; set; }
    /// <summary>Module-defined content keys handled by this opening's enabled providers.</summary>
    public HashSet<string> HandledContent { get; } = new(StringComparer.Ordinal);
    /// <summary>Content diagnostics, reported after every provider has checked the opening.</summary>
    public List<(string Content, string Message)> ContentWarnings { get; } = [];
    public IEnumerable<string> UnhandledContent => ContentWarnings
        .Where(w => !HandledContent.Contains(w.Content)).Select(w => w.Message);
}

/// <summary>A killed monster's drops, before they are rolled. The multipliers of several subscribers multiply.</summary>
public sealed class DropRoll(Player player, MonsterInfo monster)
{
    /// <summary>Who killed it.</summary>
    public Player Player { get; } = player;

    public IPlayer ModPlayer => Program.PlayerOf(Player);

    /// <summary>What died.</summary>
    public MonsterInfo Monster { get; } = monster;

    /// <summary>Scales how many items drop (1: the server's own).</summary>
    public double Quantity { get; set; } = 1;

    /// <summary>Scales how often equipment drops magic or rare (1: the server's own).</summary>
    public double Rarity { get; set; } = 1;

    /// <summary>Scales the gold pile (1: the server's own).</summary>
    public double Gold { get; set; } = 1;

    /// <summary>Scales the chance of a map-related drop (a map; 1: the server's own), as the monster stat
    /// <c>map_related_item_drop_chance_+%_final_from_league</c> does.</summary>
    public double MapRelated { get; set; } = 1;

    /// <summary>Factors on what the server's own roll picks: the kinds (currency, gems, flasks, equipment), a map's base
    /// card and scarab chances, and single currency, card and scarab types (1: the server's own).</summary>
    public DropWeights Weights { get; } = new();

    /// <summary>True: nothing of the server's own drops (the <see cref="Items"/> still do).</summary>
    public bool Cancel { get; set; }

    /// <summary>Stackable items to drop as well, each a <c>BaseItemTypes</c> id or name and its count, split at the item's
    /// stack size (the <see cref="Items"/> still drop besides).</summary>
    public List<(string Item, int Count)> Stacks { get; } = [];

    /// <summary>Items to drop as well, each as /item takes it: a <c>BaseItemTypes</c> id or name, or a unique's name.</summary>
    public List<string> Items { get; } = [];

    /// <summary>Base types (a <c>BaseItemTypes</c> id or name) to drop as the kill's own items roll: its rarity chances
    /// (with <see cref="Rarity"/>) and the item level its rarity gives, so one may drop magic, rare or unique.</summary>
    public List<string> RolledItems { get; } = [];

    /// <summary>Items to drop as well, as they are: already made and rolled.</summary>
    public List<GameServer.Core.Protocol.Instance.Item> Made { get; } = [];

    /// <summary>Changes to every item the kill drops (the server's own, <see cref="Items"/> and <see cref="RolledItems"/>),
    /// in order: each takes a drop and returns what drops in its place (the drop itself, changed, or several of them).
    /// A change tells the drops it leaves alone by their type.</summary>
    public List<Func<ItemDrops.Drop, Random, IEnumerable<ItemDrops.Drop>>> Changes { get; } = [];

    /// <summary>The drops after every change.</summary>
    public IEnumerable<ItemDrops.Drop> Changed(IEnumerable<ItemDrops.Drop> drops, Random rng)
    {
        foreach (var change in Changes) drops = drops.SelectMany(d => change(d, rng)).ToList();
        return drops;
    }

    Action<List<ItemDrops.Drop>>? _transforms;

    /// <summary>True when at least one post-roll transform was registered.</summary>
    public bool HasTransforms => _transforms is not null;

    /// <summary>Registers a post-roll callback that inspects or mutates the kill's rolled drops before they are
    /// stacked and placed on the ground. Multiple transforms run in registration order.</summary>
    public void AddTransform(Action<List<ItemDrops.Drop>> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _transforms += transform;
    }

    /// <summary>Runs every registered post-roll transform on <paramref name="drops"/>, in registration order.
    /// A transform that throws is logged and skipped so later transforms and drop placement still run.</summary>
    public void ApplyTransforms(List<ItemDrops.Drop> drops)
    {
        if (_transforms is null) return;
        foreach (var transform in Delegate.EnumerateInvocationList(_transforms))
            GameEventOwners.Guard(nameof(DropRoll), nameof(ApplyTransforms), () => transform(drops));
    }
}

/// <summary>A kill's experience, before the character and its gems gain it.</summary>
public sealed class ExperienceGain(Player player, MonsterInfo monster, uint experience)
{
    /// <summary>Who gains it.</summary>
    public Player Player { get; } = player;

    public IPlayer ModPlayer => Program.PlayerOf(Player);

    /// <summary>What died.</summary>
    public MonsterInfo Monster { get; } = monster;

    /// <summary>The experience, after the server's level penalty; set it to change what is gained.</summary>
    public uint Experience { get; set; } = experience;
}

/// <summary>Stats added to the character, like /god's: the pools (life, mana) are not rebuilt from them. Values of the same stat add up.</summary>
public sealed class StatSheet(Player player)
{
    readonly Dictionary<uint, int> _added = new();

    /// <summary>Whose stats these are.</summary>
    public Player Player { get; } = player;

    public IPlayer ModPlayer => Program.PlayerOf(Player);

    /// <summary>Adds <paramref name="value"/> to the stat with this <c>Stats.dat</c> id (an unknown id is ignored).</summary>
    public void Add(uint stat, int value) =>
        _added[stat] = (int)Math.Clamp((long)_added.GetValueOrDefault(stat) + value, int.MinValue, int.MaxValue);

    /// <summary>What was added so far.</summary>
    public IReadOnlyDictionary<uint, int> Added => _added;
}

/// <summary>The official league whose Currency Exchange prices are traded at.</summary>
public enum ExchangeRatesLeague
{
    /// <summary>The current challenge league.</summary>
    Current,
    /// <summary>Standard.</summary>
    Standard,
}

/// <summary>How the Currency Exchange trades; these are its values without a mod (<see cref="ExchangeRateStore.Use(string, ExchangeOptions)"/>).</summary>
/// <param name="StandardPrices">The official league whose prices the Standard league here trades at.</param>
/// <param name="LeaguePrices">The official league whose prices every other league here trades at.</param>
/// <param name="OrderSlots">How many orders a player can have.</param>
/// <param name="SpreadScale">Multiplies how far from the price Faustus trades: 1% for the busiest items, up to 10% for the rarest.</param>
/// <param name="VolumeShare">How much of an item's official hourly trade an hour offers; 0: no limit.</param>
/// <param name="PatientRange">How much of the way from the market's price to the other side's an order fills with time; 1 as a live market, about 0.5 no profit
/// from the spread, 0 none.</param>
/// <param name="PatientSpeed">Divides how long such an order waits.</param>
/// <param name="CatchUpHours">How many hours away an order catches up on; 0: only the current hour.</param>
/// <param name="GoldFee">Multiplies the gold an order pays.</param>
/// <param name="MaxRateAgeDays">Older rates are not traded at.</param>
/// <param name="FaustusSpeaks">He speaks when an order is placed or filled.</param>
public sealed record ExchangeOptions(ExchangeRatesLeague StandardPrices = ExchangeRatesLeague.Standard, ExchangeRatesLeague LeaguePrices = ExchangeRatesLeague.Current,
    int OrderSlots = 20, double SpreadScale = 1, double VolumeShare = .1, double PatientRange = 1, double PatientSpeed = 1, int CatchUpHours = 168, double GoldFee = 1,
    double MaxRateAgeDays = 7, bool FaustusSpeaks = true);

/// <summary>A chat message or slash command sent by the client (<c>C->S 0x0015</c>, <see cref="GameEvents.ClientMessage"/>).</summary>
public sealed record ClientMessage(Player Player, string Text, IReadOnlyList<uint> Links)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    public bool IsCommand => Text.StartsWith('/');
}

/// <summary>A cosmetic slot or skill-gem cosmetic was changed by the player (<see cref="GameEvents.CosmeticChanged"/>).</summary>
public sealed record CosmeticChange(Player Player, byte Slot, uint RecordId, uint MtxTypeHash, byte Option = 0, uint GemHash = 0)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    public bool IsSkillCosmetic => GemHash != 0;
}

/// <summary>A brand-new character was committed in Auth and has not yet entered its first area (<see cref="GameEvents.CharacterCreated"/>).
/// Subscribers may inspect <see cref="CreationOptions"/> and set <see cref="StartingAreaId"/>, <see cref="StartingLevel"/>,
/// or <see cref="SuppressStartKit"/> before Auth sends S→C 0x15 / 0x13 and assigns the first area.</summary>
public sealed class CharacterCreatedContext(
    string account,
    Guid characterId,
    string name,
    GameServer.Core.Accounts.CharacterClass @class,
    string league,
    bool hardcore,
    bool ssf,
    string? leagueForm,
    IReadOnlyDictionary<string, string> creationOptions)
{
    public string Account { get; } = account;
    public Guid CharacterId { get; } = characterId;
    public string Name { get; } = name;
    public GameServer.Core.Accounts.CharacterClass Class { get; } = @class;
    public string League { get; } = league;
    public bool Hardcore { get; } = hardcore;
    public bool Ssf { get; } = ssf;
    public string? LeagueForm { get; } = leagueForm;
    public IReadOnlyDictionary<string, string> CreationOptions { get; } = creationOptions;

    /// <summary>Optional town <c>WorldAreas</c> Id (e.g. <c>"2_11_endgame_town"</c>) to assign as <c>LastTown</c> before first login.</summary>
    public string? StartingAreaId { get; set; }

    /// <summary>Optional starting character level (<c>1..100</c>) to apply before S→C 0x15 / 0x13.</summary>
    public int? StartingLevel { get; set; }

    /// <summary>When true, pre-seeds an empty save (<c>StartWeapon = null</c>, <c>Tutorial = null</c>) so the first login
    /// skips the starter weapon, starter flasks/scrolls, and opening tutorial.</summary>
    public bool SuppressStartKit { get; set; }
}

/// <summary>Result of evaluating a vendor sale quote (<see cref="GameEvents.VendorQuote"/>).</summary>
public sealed record VendorQuoteResult(
    IReadOnlyList<GameServer.Core.Protocol.Instance.Item> Rewards,
    IReadOnlyList<string> Recipes,
    string? Error = null);

/// <summary>The player died in an area (<see cref="GameEvents.PlayerDeath"/>), after death state and death screen presentation.</summary>
public sealed record PlayerDiedContext(Player Player, byte DeathKind, int DeathCount, bool Hardcore, uint ExperienceLost = 0)
{
    public IPlayer ModPlayer => Program.PlayerOf(Player);
}

/// <summary>
/// Offered vendor sale items evaluated by <see cref="GameEvents.VendorRecipe"/> subscribers before built-in vendor
/// recipes and basic sale pricing run. Consumed counts reduce what later subscribers and built-in recipes see.
/// </summary>
public sealed class VendorRecipeContext
{
    readonly List<GameServer.Core.Protocol.Instance.InventoryItem> _offered;
    readonly Dictionary<uint, int> _remaining;
    readonly List<GameServer.Core.Protocol.Instance.Item> _rewards = [];
    readonly List<string> _recipes = [];

    public VendorRecipeContext(
        Player player,
        IReadOnlyList<GameServer.Core.Protocol.Instance.InventoryItem> offered,
        ItemCatalog catalog,
        Random random)
    {
        Player = player;
        Catalog = catalog;
        Random = random;
        _offered = [.. offered];
        _remaining = new Dictionary<uint, int>(_offered.Count);
        foreach (var item in _offered)
            _remaining[item.Id] = item.Item.StackSize;
    }

    public Player Player { get; }
    public IPlayer ModPlayer => Program.PlayerOf(Player);
    public ItemCatalog Catalog { get; }
    public Random Random { get; }

    /// <summary>All items originally offered in the sale window (check <see cref="Remaining"/> for unconsumed stacks).</summary>
    public IReadOnlyList<GameServer.Core.Protocol.Instance.InventoryItem> Offered => _offered;

    /// <summary>Rewards added by custom vendor recipes so far.</summary>
    public IReadOnlyList<GameServer.Core.Protocol.Instance.Item> Rewards => _rewards;

    /// <summary>Recipe names matched so far.</summary>
    public IReadOnlyList<string> Recipes => _recipes;

    /// <summary>Remaining unconsumed stack count for the offered item <paramref name="itemId"/>.</summary>
    public int Remaining(uint itemId) => _remaining.GetValueOrDefault(itemId);

    /// <summary>Consumes <paramref name="count"/> items from offered stack <paramref name="itemId"/>; false if insufficient.</summary>
    public bool TryConsume(uint itemId, int count = 1)
    {
        if (count <= 0) return false;
        if (_remaining.GetValueOrDefault(itemId) < count) return false;
        _remaining[itemId] -= count;
        return true;
    }

    /// <summary>Atomically consumes all requested <c>(ItemId, Count)</c> entries or consumes none and returns false.</summary>
    public bool TryConsumeAll(IEnumerable<(uint ItemId, int Count)> items)
    {
        var needed = new Dictionary<uint, int>();
        foreach (var (id, count) in items)
        {
            if (count <= 0) return false;
            needed[id] = needed.GetValueOrDefault(id) + count;
        }
        if (needed.Count == 0) return false;
        foreach (var (id, count) in needed)
            if (_remaining.GetValueOrDefault(id) < count) return false;
        foreach (var (id, count) in needed)
            _remaining[id] -= count;
        return true;
    }

    /// <summary>Adds a single reward item and records the matched recipe name.</summary>
    public void AddReward(GameServer.Core.Protocol.Instance.Item item, string recipeName)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.StackSize == 0)
            throw new ArgumentOutOfRangeException(nameof(item), "Vendor recipe reward StackSize must be positive.");
        _rewards.Add(item);
        if (!string.IsNullOrWhiteSpace(recipeName)) _recipes.Add(recipeName);
    }

    /// <summary>Adds multiple reward items and records the matched recipe name once.</summary>
    public void AddRewards(IEnumerable<GameServer.Core.Protocol.Instance.Item> items, string recipeName)
    {
        ArgumentNullException.ThrowIfNull(items);
        bool any = false;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.StackSize == 0)
                throw new ArgumentOutOfRangeException(nameof(items), "Vendor recipe reward StackSize must be positive.");
            _rewards.Add(item);
            any = true;
        }
        if (any && !string.IsNullOrWhiteSpace(recipeName)) _recipes.Add(recipeName);
    }
}
