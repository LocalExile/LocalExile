/// <summary>Global options registered by a user mod; disposing its registration restores the previous options.</summary>
public sealed record CurrencyPickupRules(int Radius = 0, int MaximumItemsPerTick = 8, bool RequirePet = true);
