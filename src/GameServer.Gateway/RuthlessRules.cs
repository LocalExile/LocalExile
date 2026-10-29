using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>LocalExile tuning for balance numbers the retail server does not publish.
/// Known Ruthless mechanics and native table overrides are enforced independently of these multipliers.</summary>
sealed record RuthlessRules
{
    public double ItemDropMultiplier { get; init; } = 0.20;
    public double GoldMultiplier { get; init; } = 0.25;
    public double EquipmentRarityMultiplier { get; init; } = 0.25;
    public double GemExperienceMultiplier { get; init; } = 0.25;
    public double VendorRewardMultiplier { get; init; } = 0.20;
    public int ExperiencePenaltyStartLevel { get; init; } = 68;
    public double ExperienceMultiplierPerLevel { get; init; } = 0.90;
    public double CampaignBossLifeRecoveryFraction { get; init; } = 0.25;

    public void Validate()
    {
        foreach (var (name, value) in new[]
        {
            (nameof(ItemDropMultiplier), ItemDropMultiplier), (nameof(GoldMultiplier), GoldMultiplier),
            (nameof(EquipmentRarityMultiplier), EquipmentRarityMultiplier), (nameof(GemExperienceMultiplier), GemExperienceMultiplier),
            (nameof(VendorRewardMultiplier), VendorRewardMultiplier), (nameof(ExperienceMultiplierPerLevel), ExperienceMultiplierPerLevel),
            (nameof(CampaignBossLifeRecoveryFraction), CampaignBossLifeRecoveryFraction),
        })
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentException($"{name} must be between 0 and 1 inclusive");
        if (ExperiencePenaltyStartLevel is < 1 or > 100)
            throw new ArgumentException("ExperiencePenaltyStartLevel must be between 1 and 100");
    }

    public double ExperienceMultiplier(int level) => level < ExperiencePenaltyStartLevel ? 1
        : Math.Pow(ExperienceMultiplierPerLevel, Math.Max(1, level - ExperiencePenaltyStartLevel + 1));

    public static uint Scale(uint amount, double multiplier) => (uint)Math.Clamp(Math.Floor(amount * multiplier), 0, uint.MaxValue);
}

/// <summary>Reads at most once a second, publishes complete validated snapshots, and keeps the last good snapshot
/// through partial writes, invalid edits or temporary file failures. No server restart is required.</summary>
sealed class RuthlessRulesFile(string path, Action<string>? log = null, TimeProvider? time = null)
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    readonly Lock gate = new();
    readonly TimeProvider clock = time ?? TimeProvider.System;
    RuthlessRules current = new();
    DateTimeOffset nextRead = DateTimeOffset.MinValue;
    (DateTime Modified, long Length)? seen;
    string? lastError;

    public RuthlessRules Current
    {
        get
        {
            lock (gate)
            {
                var now = clock.GetUtcNow();
                if (now < nextRead) return current;
                nextRead = now.AddSeconds(1);
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists) throw new IOException($"settings file does not exist: {path}");
                    var stamp = (file.LastWriteTimeUtc, file.Length);
                    if (stamp == seen) return current;
                    // A failed read is retried next poll even if an editor preserves the file's timestamp.
                    var candidate = JsonSerializer.Deserialize<RuthlessRules>(File.ReadAllText(path), Json)
                        ?? throw new JsonException("settings must be a JSON object");
                    candidate.Validate();
                    current = candidate;
                    seen = stamp;
                    lastError = null;
                    log?.Invoke($"[ruthless] loaded settings from {Path.GetFullPath(path)}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    string error = e.Message;
                    if (error != lastError) log?.Invoke($"[ruthless] keeping previous settings: {error}");
                    lastError = error;
                }
                return current;
            }
        }
    }
}
