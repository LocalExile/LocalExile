using GameServer.Core.World;

sealed class PlacedSkills
{
    internal sealed record DamageProfile(DamagePacket Hit, MonsterIgnites.Settings Ignite,
        MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);
    internal sealed record DamageSnapshot(DamageProfile Normal, DamageProfile Blinded,
        IReadOnlyDictionary<uint, int> Caster, bool Spell);

    internal sealed record Placed(uint Id, ushort Skill, int Effect, (int X, int Y) At, bool Mine,
        double Armed, double Until, DamagePacket Damage, IReadOnlyDictionary<uint, int> Own,
        int Flat, int Percent, bool ReservesLife, int Efficiency)
    {
        public DamageSnapshot? Offense { get; init; }
        public double DetonateAt = double.PositiveInfinity;
        public bool SkitterTriggered;
        public int Chain;
        public double LandAt;
        public bool Landed = true;
    }
    public readonly Dictionary<uint, Placed> All = [];
    internal sealed record Ground(uint Object, ushort Source, (int X, int Y) At, double Radius,
        double DamagePerSecond, double Until, double Created)
    {
        public double LastAt = Created;
        public readonly Dictionary<uint, double> Carry = [];
    }
    public readonly List<Ground> Grounds = [];
    public double NextSkitter;
    public double NextChain;
    public int Chain;
    public bool Dirty;
    public bool DetonationPublished;
    public static int Reserved(CharacterStats.Pools pools, IEnumerable<Placed> all, bool life) =>
        AuraCatalog.Required(life ? pools.Life : pools.Mana, life ? pools.LifeReservationEfficiencyPct : pools.ManaReservationEfficiencyPct,
            all.Where(p => p.ReservesLife == life).Select(p => new AuraCatalog.Aura(p.Skill, p.Effect, "", "", 0, 0,
                p.Flat, p.Percent, ReservationEfficiencyPct: p.Efficiency)));
}

sealed partial class ClientState
{
    PlacedSkills? placedSkills;
    public PlacedSkills Placed => System.Threading.LazyInitializer.EnsureInitialized(ref placedSkills);
}
