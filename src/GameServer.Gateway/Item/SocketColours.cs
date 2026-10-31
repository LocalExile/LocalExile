using GameServer.Core.Protocol.Instance;

/// <summary>
/// 3.29 socket colours. Item sockets are white by default; each generated socket has a chance to roll red, green or blue
/// instead, in proportion to the item's attribute requirements, and that chance rises with the item level (and, for
/// Chromatic Orbs and the non-white bench crafts, with the item's quality). New sockets added by currency or the bench
/// stay white. GGG has not published the curve, so <see cref="NonWhiteChance"/> is an approximation
/// (13-items.md, "Socket colours (3.29)").
/// </summary>
static class SocketColours
{
    /// <summary>The weight every colour gets on top of its attribute requirement, so a base can roll all three colours.
    /// The client's exact weights are unknown.</summary>
    public const int AttributeWeight = 12;

    /// <summary>The chance that a generated socket is one of the three colours, 5% at item level 1 and 0.25 points per
    /// level (30% at level 100). <paramref name="chromatic"/> adds 0.25 points per 1% item quality (5 points at 20%).
    /// Approximate: the official patch notes give no numbers.</summary>
    public static double NonWhiteChance(int itemLevel, int quality = 0, bool chromatic = false) =>
        Math.Clamp(0.05 + 0.0025 * Math.Clamp(itemLevel, 0, 100)
            + (chromatic ? 0.0025 * Math.Clamp(quality, 0, 100) : 0), 0.05, 0.6);

    /// <summary>One freshly generated socket: white, or one of the three colours at the item's chance.</summary>
    public static SocketColour Roll((int Str, int Dex, int Int) requirements, int itemLevel, int quality, Random random,
        bool chromatic = false) =>
        random.NextDouble() < NonWhiteChance(itemLevel, quality, chromatic)
            ? Colour(requirements, random) : SocketColour.White;

    /// <summary>A forced colour: red, green or blue, weighted by the base's strength, dexterity and intelligence
    /// requirements plus <see cref="AttributeWeight"/>.</summary>
    public static SocketColour Colour((int Str, int Dex, int Int) requirements, Random random)
    {
        int r = requirements.Str + AttributeWeight, g = requirements.Dex + AttributeWeight, b = requirements.Int + AttributeWeight;
        int value = random.Next(r + g + b);
        return value < r ? SocketColour.Red : value < r + g ? SocketColour.Green : SocketColour.Blue;
    }

    /// <summary>Red, green or blue: a socket that gets the +10% matching-gem-quality bonus.</summary>
    public static bool IsColoured(SocketColour colour) =>
        colour is SocketColour.Red or SocketColour.Green or SocketColour.Blue;
}
