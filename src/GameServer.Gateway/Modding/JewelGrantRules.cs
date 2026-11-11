/// <summary>Global options registered by a user mod; disposing its registration restores the previous options.</summary>
public sealed record JewelGrantRules(bool Enabled = false, bool CrossClass = false, int MaximumPairs = 1);
