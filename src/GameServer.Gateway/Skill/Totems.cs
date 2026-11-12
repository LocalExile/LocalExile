using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// The player's summoned totems (Agents/docs/protocol/11-skills.md section 10g): team-0 monster objects of the skill's
/// SkillTotemVariations variety, kept here rather than in <see cref="ClientState.Monsters"/>, so area hits, monster AI,
/// loot, experience and the instance save never see them. A totem dies when its Life reaches 0, its duration ends, a new
/// one replaces it past the limit, or the player dies (wiki Totem, Soul Mantle); it then plays its death (0x1b1 at 0) and
/// is removed (0x2bd) <see cref="RemoveSeconds"/> later. An area change starts a new connection without them. Guarded by
/// the Monsters lock; times are the connection's game seconds.
/// </summary>
public sealed class TotemState
{
    /// <summary>The death animation's time before the object goes.</summary>
    public const double RemoveSeconds = 1;

    /// <summary>A totem's aura on the player within <paramref name="Radius"/> cells: a BuffDefinitions row and its stats from
    /// the totem's skill, before aura effect.</summary>
    public sealed record Aura(ushort Row, int Radius, IReadOnlyList<(uint Stat, int Value)> Stats);

    /// <summary>What a totem that places its own skill does with it: the granted id its object carries for the action
    /// the client plays (a placed trap's proxy id), the hit that use deals, the cells around the aimed point it covers,
    /// the cells it looks for a target in, the seconds one use takes and where in it the hit lands. The tick keeps the
    /// use in flight; a totem whose skill carries no hit (Rejuvenation Totem, Decoy Totem) has none, and an adopted
    /// projectile spell totem has none either because it casts through SpellTotems.cs (flies) rather than contacting.</summary>
    public sealed class Strike(ushort grantedId, DamagePacket hit, int radius, int range, bool spell, double interval,
        double contact)
    {
        public ushort GrantedId { get; } = grantedId;
        public DamagePacket Hit { get; } = hit;
        public int Radius { get; } = radius;
        public int Range { get; } = range;
        /// <summary>Whether the skill is a spell (its mitigation and ailments) rather than an attack.</summary>
        public bool Spell { get; } = spell;
        public double Interval { get; } = interval;
        public double Contact { get; } = contact;
        /// <summary>The skill's stats as the totem was summoned with, for the hit's ailments.</summary>
        public IReadOnlyDictionary<uint, int> Sources { get; init; } = new Dictionary<uint, int>();
        /// <summary>The earliest time of the next use.</summary>
        public double NextUse;
        /// <summary>When the use in flight lands its hit; null between uses.</summary>
        public double? ContactAt;
        /// <summary>The point the use in flight was aimed at.</summary>
        public (int X, int Y) Aim;
        /// <summary>What the use in flight was aimed at, or 0.</summary>
        public uint Target;
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
        Strike? strike = null, ushort grantedId = 0, Devour? devour = null, bool taunt = false, uint ground = 0,
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
        public (int X, int Y) At => at;
        public uint MaxLife { get; } = maxLife;
        public double Until { get; set; } = until;
        public Aura? Aura => aura;
        /// <summary>The ground effect object drawn for its aura (0 when it has none); removed with the totem.</summary>
        public uint Ground => ground;
        /// <summary>What it does with its skill; null for a totem that only carries an aura or another effect, and for
        /// an adopted projectile spell totem, which casts through SpellTotems.cs instead of contacting.</summary>
        public Strike? Strike => strike;
        /// <summary>The granted-skill id its object carries, so the client can play any action it takes (Rejuvenation,
        /// Decoy and Devouring Totems deal no damage but still act). 0 when the skill could not be built.</summary>
        public ushort GrantedId => grantedId;
        /// <summary>Its corpse meal; null unless it is a Devouring Totem.</summary>
        public Devour? Devour => devour;
        /// <summary>Whether the monsters near it are drawn to it (Decoy Totem).</summary>
        public bool Taunt => taunt;
        public int Effect = -1;
        public uint SourceGem;
        public uint SourceHost;
        public Item? SourceItem;
        public bool Ballista;
        public int BonusSlots;
        public double NextCast;
        public ushort Sequence;
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
    public List<Totem> Place(Totem totem, int limit, double now, int? ordinaryLimit = null)
    {
        var replaced = new List<Totem>();
        void Retire(Totem old) { old.DiedAt = now; replaced.Add(old); }
        if (ordinaryLimit is int ordinary)
        {
            // Ballista-only bonus slots do not let ordinary totems exceed their own cap. Placing one ordinary
            // totem beside two ballistas must not remove both ballistas as the old all-totem limit of one did.
            if (!totem.Ballista)
                foreach (var old in Living(now).Where(t => !t.Ballista).ToArray())
                    if (Living(now).Count(t => !t.Ballista) >= limit) Retire(old);
            int bonus = Math.Max(totem.BonusSlots, Living(now).Where(t => t.Ballista).Select(t => t.BonusSlots).DefaultIfEmpty(0).Max());
            int total = Math.Max(limit, (int)Math.Min(int.MaxValue, (long)ordinary + bonus));
            while (Living(now).Count() >= total && Living(now).FirstOrDefault() is { } oldest) Retire(oldest);
        }
        else
            foreach (var old in Living(now).ToArray())
                if (Living(now).Count() >= limit) Retire(old);
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
