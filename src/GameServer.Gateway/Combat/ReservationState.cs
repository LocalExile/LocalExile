// Mutable values only: reservation combat survives replacement of the Game assembly.
// All access is under the owning connection's Monsters lock and uses area game time.
sealed class ReservationState
{
    internal sealed record Echo(double At, int X, int Y, uint Life, uint Mana, double Shield);
    internal sealed record Proc(ushort Skill, (int X, int Y) At, uint Target = 0);
    internal sealed record Ice((int X, int Y) At, double Until, uint? Object = null);
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
}

sealed partial class ClientState
{
    public readonly ReservationState Reservations = new();
}
