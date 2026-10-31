namespace GameServer.Modding;

/// <summary>
/// A league as a module, registered with <see cref="IModContext.AddLeague"/>. Standard is built into the server: a character
/// whose league has no loaded module, or whose module needs a feature that is not loaded, plays by Standard's rules, keeping
/// its league and saves, and returns to its own rules when the module is back. A connection keeps the module it started with
/// until the next area.
/// </summary>
public interface ILeague
{
    /// <summary>The module's id, lower case: what <see cref="IPlayer.LeagueId"/> says.</summary>
    string Id { get; }

    /// <summary>The league's name in the client's league list: it takes the league of that name with its Hardcore and SSF
    /// variants. A league form (<c>mod.json</c> "league"."form") is found by <see cref="Id"/> instead: the mod's id.</summary>
    string ClientLeagueName { get; }

    /// <summary>The rules now. Asked often, so return a kept object; a new value applies at once (a tunables file).</summary>
    LeagueRules Rules { get; }

    /// <summary>Mods that must be loaded for this league's characters to use it.</summary>
    IReadOnlyList<string> RequiredFeatures => [];

    /// <summary>Which of the game files' data the league's characters use.</summary>
    LeagueCatalogs Catalogs => LeagueCatalogs.Standard;

    /// <summary>The league's hooks. <paramref name="league"/> is the mod's context, but its hooks run only for this league's
    /// players; they are removed with the mod.</summary>
    void Load(IModContext league) { }
}

/// <summary>The sets of catalog data the game files have.</summary>
public enum LeagueCatalogs
{
    /// <summary>The ordinary tables and columns.</summary>
    Standard,
    /// <summary>The game files' HardMode tables and columns: items, gems, passives, drops, rewards, vendor prices.</summary>
    HardMode,
}

/// <summary>What the server asks of a league. The defaults are Standard's.</summary>
public sealed record LeagueRules
{
    /// <summary>Standard's rules.</summary>
    public static LeagueRules Standard { get; } = new();

    /// <summary>Scales how many items drop.</summary>
    public double ItemDropMultiplier { get; init; } = 1;
    /// <summary>Scales gold piles.</summary>
    public double GoldMultiplier { get; init; } = 1;
    /// <summary>Scales how often equipment drops magic or rare.</summary>
    public double EquipmentRarityMultiplier { get; init; } = 1;
    /// <summary>Gems gain this share of a tenth of a kill's experience, keeping the fraction; null: a whole tenth.</summary>
    public double? GemExperienceMultiplier { get; init; }
    /// <summary>Vendors pay a basic sale (no recipes) scaled by this; null: the ordinary recipes.</summary>
    public double? VendorRewardMultiplier { get; init; }
    /// <summary>From this level the character's experience shrinks; null: it never does.</summary>
    public int? ExperiencePenaltyStartLevel { get; init; }
    /// <summary>What each level from <see cref="ExperiencePenaltyStartLevel"/> on multiplies experience by.</summary>
    public double ExperienceMultiplierPerLevel { get; init; } = 1;
    /// <summary>The share of its life a nearby boss recovers when the player dies, in <see cref="BossRecoveryAreaPrefixes"/>.</summary>
    public double BossLifeRecoveryFraction { get; init; }
    /// <summary>The <c>WorldAreas</c> id prefixes where bosses recover.</summary>
    public IReadOnlyList<string> BossRecoveryAreaPrefixes { get; init; } = [];
    /// <summary>Life and mana are saved and come back on the next connection, instead of filling at every area.</summary>
    public bool PersistsVitals { get; init; }
    /// <summary>Towns and hideouts refill the flasks.</summary>
    public bool TownRefillsFlasks { get; init; } = true;
    /// <summary>The crafting bench is in the hideout and works.</summary>
    public bool CraftingBench { get; init; } = true;
    /// <summary>The tutorial's chest and support gem exist.</summary>
    public bool TutorialGifts { get; init; } = true;
    /// <summary>A utility flask gains charges while its effect runs (false: only with <see cref="UtilityFlaskExceptionStat"/>).</summary>
    public bool UtilityFlaskChargesWhileActive { get; init; } = true;
    /// <summary>The <c>Stats.dat</c> id that lifts <see cref="UtilityFlaskChargesWhileActive"/>.</summary>
    public string? UtilityFlaskExceptionStat { get; init; }
    /// <summary>NPC ids that refill life, mana and flasks when spoken to; an entry starting with * matches inside an id.</summary>
    public IReadOnlyList<string> VitalRefillNpcs { get; init; } = [];
    /// <summary>Nessa's map stock is for sale.</summary>
    public bool SellsMaps { get; init; } = true;
    /// <summary>The development assortment of every gem at the vendors that have it.</summary>
    public bool FullGemStock { get; init; } = true;
    /// <summary>The only NPC id endings whose gem tabs exist; null: every gem vendor's.</summary>
    public IReadOnlyList<string>? GemVendors { get; init; }
    /// <summary>Support gems are in the gem tabs.</summary>
    public bool SellsSupportGems { get; init; } = true;
    /// <summary>Gems in the ordinary stock are for sale.</summary>
    public bool SellsGemsInGoods { get; init; } = true;
    /// <summary>Item classes left out of every shop's ordinary stock.</summary>
    public IReadOnlyList<string> ShopExcludedClasses { get; init; } = [];
    /// <summary>Quests offer support gems.</summary>
    public bool SupportGemRewards { get; init; } = true;
    /// <summary>Gem base type ids (the last part of the path) that do not work; each also takes its "Alt" variants.</summary>
    public IReadOnlyList<string> UnavailableGemBases { get; init; } = [];
    /// <summary>Skill effect ids (without the data set's "HardMode") that do not work; each also takes its "Alt" variants.</summary>
    public IReadOnlyList<string> UnavailableGemEffects { get; init; } = [];

    /// <summary>What experience is multiplied by at this level.</summary>
    public double ExperienceMultiplier(int level) => ExperiencePenaltyStartLevel is not int start || level < start ? 1
        : Math.Pow(ExperienceMultiplierPerLevel, Math.Max(1, level - start + 1));

    /// <summary>An amount scaled, rounded down.</summary>
    public static uint Scale(uint amount, double multiplier) => (uint)Math.Clamp(Math.Floor(amount * multiplier), 0, uint.MaxValue);

    /// <summary>Whether speaking to this NPC refills life, mana and flasks.</summary>
    public bool RefillsVitalsAt(string npcId) => VitalRefillNpcs.Any(entry => entry.StartsWith('*')
        ? npcId.Contains(entry[1..], StringComparison.Ordinal) : npcId.Equals(entry, StringComparison.Ordinal));

    /// <summary>Whether this gem vendor has gem tabs.</summary>
    public bool SellsGemsAt(string npcId) => GemVendors is null || GemVendors.Any(end => npcId.EndsWith(end, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a gem of this base type works.</summary>
    public bool AllowsGemBase(string baseId)
    {
        string leaf = baseId[(baseId.LastIndexOf('/') + 1)..];
        return !UnavailableGemBases.Any(id => leaf.Equals(id, StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith(id + "Alt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a skill effect works.</summary>
    public bool AllowsGemEffect(string? effectId)
    {
        string name = effectId?.Replace("HardMode", "", StringComparison.OrdinalIgnoreCase) ?? "";
        return !UnavailableGemEffects.Any(id => name.Equals(id, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(id + "Alt", StringComparison.OrdinalIgnoreCase));
    }
}
