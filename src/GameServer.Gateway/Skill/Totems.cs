using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// The player's summoned totems (Agents/docs/protocol/11-skills.md section 10g): team-0 monster objects of the skill's
/// SkillTotemVariations variety, kept here rather than in <see cref="ClientState.Monsters"/>, so area hits, monster AI,
/// loot, experience and the instance save never see them. A totem dies when its Life reaches 0, its duration ends, a new
/// one replaces it past the limit, or the player dies (wiki Totem, Soul Mantle); it then plays its death (0x1b1 at 0) and
/// is removed (0x2bb) <see cref="RemoveSeconds"/> later. An area change starts a new connection without them. Guarded by
/// the Monsters lock; times are the connection's game seconds.
/// </summary>
sealed class TotemState
{
    /// <summary>The death animation's time before the object goes.</summary>
    public const double RemoveSeconds = 1;

    /// <summary>A totem's aura on the player within <paramref name="Radius"/> cells: a BuffDefinitions row and its stats from
    /// the totem's skill, before aura effect.</summary>
    public sealed record Aura(ushort Row, int Radius, IReadOnlyList<(uint Stat, int Value)> Stats);

    public sealed class Totem(uint id, ushort skillId, (int X, int Y) at, uint maxLife, double until, Aura? aura)
    {
        public uint Id => id;
        public ushort SkillId => skillId;
        public (int X, int Y) At => at;
        public uint MaxLife { get; } = maxLife;
        public double Until { get; set; } = until;
        public Aura? Aura => aura;
        public uint Life = maxLife;
        /// <summary>When it died; null while it lives.</summary>
        public double? DiedAt;
        public uint SentLife = maxLife;
        public bool SentDeath;
        public bool Alive(double now) => DiedAt is null && Life > 0 && now < Until;
    }

    readonly List<Totem> _totems = [];
    /// <summary>The aura buff the client was last sent: its source totem and instance (Program.TotemPresentation).</summary>
    public uint SentAuraTotem;
    public ushort SentAuraInstance;

    public IReadOnlyList<Totem> All => _totems;
    public IEnumerable<Totem> Living(double now) => _totems.Where(t => t.Alive(now));

    /// <summary>Adds a totem; the oldest living ones die until at most <paramref name="limit"/> live. The ones it replaced.</summary>
    public List<Totem> Place(Totem totem, int limit, double now)
    {
        var replaced = new List<Totem>();
        foreach (var old in Living(now).ToList())
            if (Living(now).Count() >= limit) { old.DiedAt = now; replaced.Add(old); }
        _totems.Add(totem);
        return replaced;
    }

    /// <summary>The living totem nearest to a point (the earliest placed of equally near ones).</summary>
    public Totem? Nearest((int X, int Y) at, double now) => Living(now)
        .OrderBy(t => (long)(t.At.X - at.X) * (t.At.X - at.X) + (long)(t.At.Y - at.Y) * (t.At.Y - at.Y)).FirstOrDefault();

    /// <summary>The strongest living totem aura covering a point.</summary>
    public Totem? AuraAt((int X, int Y) at, double now) => Living(now)
        .Where(t => t.Aura is { } a && (long)(t.At.X - at.X) * (t.At.X - at.X) + (long)(t.At.Y - at.Y) * (t.At.Y - at.Y) <= (long)a.Radius * a.Radius)
        .OrderByDescending(t => t.Aura!.Stats.Sum(s => (long)s.Value)).FirstOrDefault();

    /// <summary>The totems whose duration ended and that had not died yet: they die now.</summary>
    public List<Totem> Expire(double now)
    {
        var expired = _totems.Where(t => t.DiedAt is null && now >= t.Until).ToList();
        foreach (var t in expired) t.DiedAt = now;
        return expired;
    }

    /// <summary>Every living totem dies (the player died).</summary>
    public void KillAll(double now)
    {
        foreach (var t in _totems.Where(t => t.DiedAt is null)) t.DiedAt = now;
    }

    /// <summary>The dead totems whose death animation is over leave.</summary>
    public List<Totem> Remove(double now)
    {
        var gone = _totems.Where(t => t.DiedAt is double died && now - died >= RemoveSeconds).ToList();
        _totems.RemoveAll(gone.Contains);
        return gone;
    }

    /// <summary>"X% of Damage from Hits is taken from your nearest Totem's Life before you" for one part of a hit (PoB
    /// reducePoolsByDamage: the share, capped by the totem's Life): what the player still takes and the totem's Life left.
    /// The totem's share is rounded down.</summary>
    public static (int Taken, uint Life) Redirect(int damage, int pct, uint life)
    {
        int share = (int)Math.Min(life, Math.Max(0, (long)damage * Math.Clamp(pct, 0, 100) / 100));
        return (damage - share, life - (uint)share);
    }
}
