using GameServer.Core.Protocol.Instance;

/// <summary>Host-owned, callback-free golem state survives Game replacement; guarded by the area's Monsters lock.</summary>
sealed class GolemState
{
    internal sealed record MinionResourceSnapshot(uint Life, double LifeRegeneration, EnergyShield.Snapshot EnergyShield);
    internal sealed record Saved(string Effect, ushort Skill, double Life = 1, double ReviveSeconds = 0,
        double SummonedSecondsRemaining = 0, MinionResourceSnapshot? Resources = null);
    internal sealed record Awaiting(Saved Saved, double At, GemSourceLifetimes.SourceLifetime? GemSource = null);
    internal sealed class Golem(uint id, SpawnedMonster monster, GolemCatalog.Definition definition, ushort source,
        GemSourceLifetimes.SourceLifetime? gemSource = null)
    {
        public uint Id = id;
        public SpawnedMonster Monster = monster;
        public GolemCatalog.Definition Definition = definition;
        public ushort Source = source;
        public readonly GemSourceLifetimes.SourceLifetime? GemSource = gemSource;
        public int GemLevel, Quality, Limit;
        public double SummonedAt, NextMove, LastStep, ReviveAt = double.PositiveInfinity;
        public double Regeneration;
        public readonly EnergyShield ShieldPool = new();
        public uint Shield => ShieldPool.Current;
        public uint MaximumShield => ShieldPool.Maximum;
        public int Combo;
        public uint Target;
        public readonly Dictionary<uint, double> Taunts = [];
        public bool CorpseEmpowered;
        public bool DeathBurstDone;
        public double WrathUntil, NextOrb;
        public int InputHash;
        public Dictionary<uint, int> Own = [], OwnerBuff = [], Player = [];
        public Dictionary<string, int> Auras = [];
        public Dictionary<byte, Item> Equipment = [];
        public Dictionary<string, int> Gear = [];
        public SkillCatalog.Unarmed? Weapon;
        public double HolyRegenerationUntil, HolyRegenerationPerSecond;
        public readonly Dictionary<int, Queue<double>> Charges = [];
        public IReadOnlyList<GrantedSkill> Skills = [];
        public IReadOnlyList<Ability> Abilities = [];
        public Action? Pending;
        public readonly List<Hazard> Hazards = [];
        public readonly List<Pulse> Pulses = [];
    }
    internal sealed record Ability(int Effect, ushort Skill, string Id, MonsterAttack Attack,
        IReadOnlyDictionary<uint, int> Stats, double Radius, double Cooldown, int Uses, double Contact,
        SkillCatalog.MonsterProjectileInfo? Projectile = null, int Crit = 0);
    internal sealed record Action(Ability Ability, uint Target, SpawnedMonster? Victim, (int X, int Y) Aim, double ContactAt, int Generation)
    {
        public bool Started;
        public double At = ContactAt;
    }
    internal sealed record Hazard(int Effect, (int X, int Y) At, double Until, double Radius, double DamagePerSecond,
        GameServer.Core.World.DamageType Type, bool Follows = false, bool Wither = false,
        double UnitDamagePerSecond = 0, int NormalDamageIncrease = 0)
    {
        public double LastAt;
        public uint? Object;
        public readonly Dictionary<uint, double> Carry = [];
        public readonly Dictionary<uint, double> DamageCarry = [];
    }
    internal sealed record Incoming(uint Enemy, SpawnedMonster Monster, Golem Target, MonsterAttack Attack, int Generation)
    {
        public bool Started;
        public double ContactAt;
    }

    internal sealed record Pulse(Ability Ability, (int X, int Y) At, double Until, double Interval, bool Follows = false, bool Once = false)
    {
        public double Next;
        public readonly HashSet<uint> Hit = [];
    }

    public readonly Dictionary<uint, Golem> All = [];
    public readonly List<Awaiting> Waiting = [];
    public readonly List<Incoming> IncomingHits = [];
    public readonly Dictionary<ushort, (uint Source, ushort Instance)> Buffs = [];
    public readonly Dictionary<uint, int> OwnerStats = [];
    public bool Restored;
    public double LastSummoned = double.NegativeInfinity;
    public long Version;
    public double RefreshAt;

    internal Golem? Replacement(string kind, int limit) => All.Values.Count(g => !g.Monster.Dead) < limit ? null
        : All.Values.Where(g => !g.Monster.Dead).OrderBy(g => g.Definition.Kind == kind ? 0 : 1).ThenBy(g => g.SummonedAt).FirstOrDefault();
}

sealed partial class ClientState
{
    GolemState? golems;
    public GolemState Golems => System.Threading.LazyInitializer.EnsureInitialized(ref golems);
}
