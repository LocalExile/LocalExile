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

    /// <summary>The league's name in the client's league list. "Ruthless" takes every Ruthless form of every league
    /// (Hardcore Ruthless, SSF Ruthless …). Other names take the league of that name with its Hardcore and SSF forms.</summary>
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
    /// <summary>The Ruthless ("HardMode") tables and columns: items, gems, passives, drops, rewards, vendors.</summary>
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
    /// <summary>Vendor recipes use the Ruthless table scaled by this; null: the ordinary table.</summary>
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
    /// <summary>A utility flask gains charges while its effect runs (false: only with the hard-mode exception stat).</summary>
    public bool UtilityFlaskChargesWhileActive { get; init; } = true;
    /// <summary>Gem vendors other than Siosa and Lilly refill life and mana when spoken to.</summary>
    public bool VendorsRefillVitals { get; init; }
    /// <summary>Gem base type ids (the last part of the path) that do not work; each also takes its "Alt" variants.</summary>
    public IReadOnlyList<string> UnavailableGemBases { get; init; } = [];
    /// <summary>Skill effect ids (without "HardMode") that do not work; each also takes its "Alt" variants.</summary>
    public IReadOnlyList<string> UnavailableGemEffects { get; init; } = [];

    /// <summary>What experience is multiplied by at this level.</summary>
    public double ExperienceMultiplier(int level) => ExperiencePenaltyStartLevel is not int start || level < start ? 1
        : Math.Pow(ExperienceMultiplierPerLevel, Math.Max(1, level - start + 1));

    /// <summary>An amount scaled, rounded down.</summary>
    public static uint Scale(uint amount, double multiplier) => (uint)Math.Clamp(Math.Floor(amount * multiplier), 0, uint.MaxValue);

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
