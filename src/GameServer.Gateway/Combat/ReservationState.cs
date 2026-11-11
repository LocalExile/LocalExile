// Mutable values only: reservation combat survives replacement of the Game assembly.
// All access is under the owning connection's Monsters lock and uses area game time.
sealed class ReservationState
{
    internal sealed record Echo(double At, int X, int Y, uint Life, uint Mana, double Shield);
    internal sealed record Proc(ushort Skill, (int X, int Y) At, uint Target = 0);
    internal sealed record Ice((int X, int Y) At, double Until, uint? Object = null);
    internal enum CatPhase { None, Stealth, Agility }
    internal readonly record struct CatAspectOwner(ushort Skill, ushort RootBuff);
    internal enum AvianPhase { None, Might, Flight }
    public CatAspectOwner? AvianOwner;
    public AvianPhase Avian;
    public double AvianStartedAt, AvianDeadline;
    public ushort AvianBuff, PresentedAvianBuff;
    public ushort PresentedAvianRow;
    public CatAspectOwner? CrabOwner;
    public double NextCrabBarrier, CrabLastLoss = double.NegativeInfinity;
    public int CrabBarriers, CrabMaximum;
    public ushort CrabBuff, PresentedCrabBuff;
    public long AspectRevision;
    public bool CrabPoolsDirty;
    public readonly Queue<Echo> Echoes = new();
    public readonly Queue<Proc> Procs = new();
    public readonly List<Ice> Trail = [];
    public readonly Dictionary<uint, GolemState.Golem> Fighters = [];
    public readonly Dictionary<uint, double> Until = [];
    public double NextEcho, NextIce, ThunderUntil, NextThunder, NextVirulence, RefreshFightersAt;
    public int Virulence;
    public ushort VirulenceBuff, StationaryBuff;
    public int ShownVirulence;
    public bool Secondary;
    public CatAspectOwner? CatOwner;
    public CatPhase Cat = CatPhase.None;
    public double CatPhaseStartedAt, CatPhaseDeadline;
    public double CatLastStealthStartedAt = double.NegativeInfinity;
    public double CatObservedStealthEntryAt = double.NegativeInfinity;
    public double CatLastAgilityStartedAt = double.NegativeInfinity;
    public double CatObservedAgilityEntryAt = double.NegativeInfinity;
    public double CatStealthGrantQualifiedSince = double.PositiveInfinity;
    public double CatAgilityGrantQualifiedSince = double.PositiveInfinity;
    public bool CatStealthGrantQualified, CatAgilityGrantQualified, CatOnslaughtQualified, CatPhasing;
    public ushort CatPhaseBuff, PresentedCatPhaseBuff, CatOnslaughtBuff, PresentedCatOnslaughtBuff;
    public ushort PresentedCatOnslaughtPhaseBuff;
    public ushort PresentedCatPhaseRow;
    public int PresentedCatValue1, PresentedCatValue2, PresentedCatValue3;
    public long CatRevision, PresentedCatRevision;
}

sealed partial class ClientState
{
    public readonly ReservationState Reservations = new();
}
