using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// The player's summoned totems (Agents/docs/protocol/11-skills.md section 10g): team-0 monster objects of the skill's
/// SkillTotemVariations variety, kept here rather than in <see cref="Player.Monsters"/>, so area hits, monster AI,
/// loot, experience and the instance save never see them. A totem dies when its Life reaches 0, its duration ends, a new
/// one replaces it past the limit, or the player dies (wiki Totem, Soul Mantle); it then plays its death (0x1b1 at 0) and
/// is removed (0x2bd) <see cref="RemoveSeconds"/> later. An area change starts a new connection without them. Guarded by
/// the area's frame; times are the connection's game seconds.
/// </summary>
public sealed class TotemState
{
    /// <summary>The death animation's time before the object goes.</summary>
    public const double RemoveSeconds = 1;

    /// <summary>A totem's aura on the player within <paramref name="Radius"/> cells: a BuffDefinitions row and its stats from
    /// the totem's skill, before aura effect.</summary>
    public sealed record Aura(ushort Row, int Radius, IReadOnlyList<(uint Stat, int Value)> Stats)
    {
        public int HitRedirectPercent { get; init; }
    }

    /// <summary>What a nova totem (Shockwave Totem) does with its skill: the granted id its object carries for the
    /// action the client plays and the cells around the totem it hits; the hit itself is worked out at each contact.</summary>
    public sealed class Strike(ushort grantedId, int radius)
    {
        public ushort GrantedId { get; } = grantedId;
        public int Radius { get; } = radius;
        /// <summary>When the cast in flight lands its hit (scheduled on the projectile runtime) and the next may start.</summary>
        public double NextCast;
    }

    /// <summary>Devouring Totem's meal: what one consumed corpse gives the player, and when the next one is due. The
    /// amounts are the gem's own per-level `corpse_consumption_life_to_gain` / `_mana_to_gain`.</summary>
    public sealed class Devour(int life, int mana)
    {
        public int Life { get; } = life;
        public int Mana { get; } = mana;
        /// <summary>The earliest time of the next meal.</summary>
        public double NextUse;
    }

    public sealed class Totem(uint id, ushort skillId, (int X, int Y) at, uint maxLife, double until, Aura? aura,
        Strike? strike = null, ushort grantedId = 0, Devour? devour = null,
        int row = 0, string varietyId = "", int level = 0, int effectRow = -1)
    {
        public uint Id => id;
        public ushort SkillId => skillId;
        /// <summary>The GrantedEffects row the totem was summoned from, the variety it was summoned as, and the level
        /// its Life was computed at (the gem's <c>PlayerLevelReq</c>). Derivation facts for the lab (the /totems and
        /// /state listings and the ledger label), which have no gem at hand: the connection's gem list is the player's
        /// and a totem outlives the socket it came from. This is deliberately NOT <see cref="Effect"/>, which is the
        /// narrower upstream fact "this totem executes that effect itself" and is -1 for every other totem.</summary>
        public int EffectRow => effectRow;
        public int Row => row;
        public string VarietyId => varietyId;
        public int Level => level;
        public (int X, int Y) At { get; private set; } = at;
        public void Relocate((int X, int Y) destination) => At = destination;
        public uint MaxLife { get; } = maxLife;
        public int PhysicalDamageReductionPct { get; init; }
        public double Until { get; set; } = until;
        public Aura? Aura => aura;
        /// <summary>Its nova; null for every other totem (an aura, Devouring Totem, an adopted projectile spell).</summary>
        public Strike? Strike => strike;
        /// <summary>The granted-skill id its object carries, so the client can play any action it takes (Devouring Totem
        /// deals no damage but still acts). 0 when the skill could not be built.</summary>
        public ushort GrantedId => grantedId;
        /// <summary>Its corpse meal; null unless it is a Devouring Totem.</summary>
        public Devour? Devour => devour;
        public double DecoyRadius { get; init; }
        public int BodySize { get; init; }
        public double? TauntAt;
        /// <summary>A Decoy Totem's chasers: the cell each was last sent to and where it stood then, and the cells an
        /// order never moved a chaser toward (walled in by other bodies), which no chaser is given again.</summary>
        public readonly Dictionary<uint, ((int X, int Y) Slot, (int X, int Y) From)> DecoyOrders = [];
        public readonly HashSet<(int X, int Y)> DecoyUnreachable = [];
        public int Effect = -1;
        public uint SourceGem;
        public uint SourceHost;
        public Item? SourceItem;
        public bool Ballista;
        public int BonusSlots;
        public int PlacementGroup { get; init; }
        public double NextCast;
        public SingleUseCooldownState? Cooldown;
        public ushort Sequence;
        public uint Life = maxLife;
        /// <summary>When it died; null while it lives.</summary>
        public double? DiedAt;
        /// <summary>Whether its removal from the owner's minion roster was written.</summary>
        public bool RosterRemovalSent;
        public bool Alive(double now) => DiedAt is null && Life > 0 && now < Until;
    }

    readonly List<Totem> _totems = [];
    /// <summary>The aura buff the client was last sent: its source totem and instance (Program.TotemPresentation).</summary>
    public uint SentAuraTotem;
    public ushort SentAuraInstance;

    /// <summary>A totem was placed or died before its time (the stat steps that read it run again).</summary>
    public Action? Changed;

    public IReadOnlyList<Totem> All => _totems;
    public HashSet<uint> PendingPlacements { get; } = [];
    public IEnumerable<Totem> Living(double now) => _totems.Where(t => t.Alive(now));
    public void Clear()
    {
        _totems.Clear();
        PendingPlacements.Clear();
    }

    public static int SharedLimit(int limit, int? ordinaryLimit, int bonusSlots, IEnumerable<Totem> living)
    {
        if (ordinaryLimit is not int ordinary) return limit;
        int bonus = Math.Max(bonusSlots, living.Select(t => t.BonusSlots).DefaultIfEmpty(0).Max());
        return Math.Max(limit, (int)Math.Min(int.MaxValue, (long)ordinary + bonus));
    }

    /// <summary>The bodies a placement replaces, in order. Each group's bonus raises the shared cap while that
    /// group is present and the largest live bonus applies. The incoming group still uses its own placement limit.</summary>
    public List<Totem> Replacements(int limit, double now, int? ordinaryLimit, bool ballista, int group, int bonusSlots)
    {
        var living = Living(now).ToList();
        var replaced = new List<Totem>();
        if (ordinaryLimit is not null && !ballista)
        {
            bool SameGroup(Totem totem) => !totem.Ballista && totem.PlacementGroup == group;
            int groupCount = living.Count(SameGroup);
            for (int i = 0; i < living.Count && groupCount >= limit; i++)
                if (SameGroup(living[i]))
                {
                    replaced.Add(living[i]);
                    living.RemoveAt(i--);
                    groupCount--;
                }
        }
        int total = SharedLimit(limit, ordinaryLimit, bonusSlots, living);
        while (living.Count >= total && living.Count > 0)
        {
            replaced.Add(living[0]);
            living.RemoveAt(0);
        }
        return replaced;
    }

    /// <summary>Adds a totem and retires the bodies the placement replaces.</summary>
    public List<Totem> Place(Totem totem, int limit, double now, int? ordinaryLimit = null)
    {
        var replaced = Replacements(limit, now, ordinaryLimit, totem.Ballista, totem.PlacementGroup, totem.BonusSlots);
        foreach (var old in replaced) old.DiedAt = now;
        _totems.Add(totem);
        Changed?.Invoke();
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
        bool any = false;
        foreach (var t in _totems.Where(t => t.DiedAt is null)) { t.DiedAt = now; any = true; }
        if (any) Changed?.Invoke();
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
