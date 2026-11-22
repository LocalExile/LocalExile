using GameServer.Modding;

// What some GameEvents hand their subscribers (GameEvents.cs). `Player` is the same connection as GameServer.Modding sees it.

/// <summary>A monster: its <c>MonsterVarieties.dat</c> id, name, level, rarity and where it is (cells).</summary>
public sealed record MonsterInfo(string VarietyId, string Name, int Level, GameServer.Core.Protocol.Instance.MonsterRarity Rarity, int X, int Y)
{
    /// <summary>Retained world identity; zero for callers without a registered object.</summary>
    public uint ObjectId { get; init; }
}

/// <summary>A monster the player killed, after its kill was committed (<see cref="GameEvents.MonsterKilled"/>).</summary>
public sealed record MonsterKill(ClientState Conn, MonsterInfo Monster)
{
    public IPlayer Player => Program.PlayerOf(Conn);
}

/// <summary>An option picked in an NPC's dialogue (an <c>NPCTalk</c> row).</summary>
/// <param name="Conn">Who picked it.</param>
/// <param name="Npc">The NPC's name.</param>
/// <param name="Category">The row's category: Gossip, Quests, QuestReward, BuyItems, SellItems …</param>
/// <param name="Option">Its text.</param>
/// <param name="Script">Its script.</param>
/// <param name="HasText">It has text and audio to play.</param>
/// <param name="Index">Its index among the NPC's options.</param>
/// <param name="Row">Its <c>NPCTalk</c> row.</param>
/// <param name="OpensGuildHideouts">A SelectHideout row's choice of list.</param>
/// <param name="RewardOffer">A QuestReward row's offer.</param>
public sealed record NpcOption(ClientState Conn, string Npc, string Category, string Option, string Script, bool HasText, int Index, int Row,
    bool OpensGuildHideouts, int? RewardOffer)
{
    public IPlayer Player => Program.PlayerOf(Conn);
}

/// <summary>A gamble window as it opens (<see cref="GameEvents.Gambles"/>).</summary>
public sealed class GambleWindow
{
    public GambleWindow(ClientState conn, string npc, int characterLevel) : this(conn, npc, characterLevel, characterLevel) { }

    public GambleWindow(ClientState conn, string npc, int characterLevel, int priceLevel)
    {
        Conn = conn;
        Npc = npc;
        CharacterLevel = characterLevel;
        PriceLevel = Math.Max(characterLevel, priceLevel);
    }

    public ClientState Conn { get; }

    public IPlayer Player => Program.PlayerOf(Conn);

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
public sealed record InventoryPlacement(ClientState Conn, byte Row, GameServer.Core.Protocol.Instance.Item Item,
    GameServer.Core.Protocol.Instance.InventoryItem? Replaced)
{
    public IPlayer Player => Program.PlayerOf(Conn);
}

/// <summary>What an area's first message carries for a module: terrain data, achievement sets.</summary>
public sealed record AreaHeader(byte[]? Terrain, IReadOnlyList<byte>? AchievementSets)
{
    public byte? Level { get; init; }
}

/// <summary>A checked device recipe before payment: content may resolve an abstract destination or refuse it.</summary>
public sealed class MapOpening(ClientState conn, IReadOnlyList<MapDevice.Slot> slots, bool campaign,
    MapDevice.Opening? opening, string reason)
{
    public ClientState Conn { get; } = conn;
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
public sealed class DropRoll(ClientState conn, MonsterInfo monster)
{
    /// <summary>Who killed it.</summary>
    public ClientState Conn { get; } = conn;

    public IPlayer Player => Program.PlayerOf(Conn);

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

    /// <summary>True: nothing of the server's own drops (the <see cref="Items"/> still do).</summary>
    public bool Cancel { get; set; }

    /// <summary>Stackable items to drop as well, each a <c>BaseItemTypes</c> id or name and its count, split at the item's
    /// stack size (the <see cref="Items"/> still drop besides).</summary>
    public List<(string Item, int Count)> Stacks { get; } = [];

    /// <summary>Items to drop as well, each as /item takes it: a <c>BaseItemTypes</c> id or name, or a unique's name.</summary>
    public List<string> Items { get; } = [];

    /// <summary>Items to drop as well, as they are: already made and rolled.</summary>
    public List<GameServer.Core.Protocol.Instance.Item> Made { get; } = [];

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
public sealed class ExperienceGain(ClientState conn, MonsterInfo monster, uint experience)
{
    /// <summary>Who gains it.</summary>
    public ClientState Conn { get; } = conn;

    public IPlayer Player => Program.PlayerOf(Conn);

    /// <summary>What died.</summary>
    public MonsterInfo Monster { get; } = monster;

    /// <summary>The experience, after the server's level penalty; set it to change what is gained.</summary>
    public uint Experience { get; set; } = experience;
}

/// <summary>Stats added to the character, like /god's: the pools (life, mana) are not rebuilt from them. Values of the same stat add up.</summary>
public sealed class StatSheet(ClientState conn)
{
    readonly Dictionary<string, int> _added = new(StringComparer.Ordinal);

    /// <summary>Whose stats these are.</summary>
    public ClientState Conn { get; } = conn;

    public IPlayer Player => Program.PlayerOf(Conn);

    /// <summary>Adds <paramref name="value"/> to the stat with this <c>Stats.dat</c> id (an unknown id is ignored).</summary>
    public void Add(string stat, int value) =>
        _added[stat] = (int)Math.Clamp((long)_added.GetValueOrDefault(stat) + value, int.MinValue, int.MaxValue);

    /// <summary>What was added so far.</summary>
    public IReadOnlyDictionary<string, int> Added => _added;
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
