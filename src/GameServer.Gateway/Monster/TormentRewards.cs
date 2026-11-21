using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>Torment marks and unclaimed payouts kept by the live instance across Game reloads.</summary>
public sealed class TormentRewards
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public readonly Dictionary<uint, Mark> Marks = [];
    public readonly ConcurrentQueue<Payout> Payouts = new();
    public int Version;
    public bool MarksChanged;

    /// <summary>A touched or possessed monster: what every client is shown of it, and what its death drops. Kept with
    /// the instance (a module reload keeps it).</summary>
    public sealed class Mark
    {
        public bool Grip { get; set; }
        public bool Paid { get; set; }
        public int Version { get; set; }
        public byte[] Magic { get; set; } = [];
        public byte[]? Skills { get; set; }
        public List<int> Spirits { get; set; } = [];
        public Dictionary<string, int> DropStats { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>A kill's payout, claimed in the kill (every kill: the player's, a minion's, a totem's, a trap's) and paid
    /// on the next tick, so the kill's own drop roll still finds its mark.</summary>
    public sealed record Payout(ClientState Killer, uint Id, int SpiritRow, Mark? Mark, MonsterRarity Rarity, int Level, (int X, int Y) At, string Name);
}
