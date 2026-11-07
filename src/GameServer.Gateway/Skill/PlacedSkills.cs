using GameServer.Core.World;

sealed class PlacedSkills
{
    internal sealed record Placed(uint Id, ushort Skill, int Effect, (int X, int Y) At, bool Mine,
        double Armed, double Until, DamagePacket Damage, IReadOnlyDictionary<uint, int> Own,
        int Flat, int Percent, bool ReservesLife, int Efficiency)
    {
        public double DetonateAt = double.PositiveInfinity;
        public bool SkitterTriggered;
        public int Chain;
    }
    public readonly Dictionary<uint, Placed> All = [];
    public double NextSkitter;
    public double NextChain;
    public int Chain;
    public bool Dirty;
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
