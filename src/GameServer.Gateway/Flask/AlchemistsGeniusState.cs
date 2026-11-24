/// <summary>
/// The Alchemist's Genius buff on one player: its BuffDefinitions row and when it ends (area clock seconds, which stop
/// while the game is paused). Plain state; the rule (what grants it, its values, its duration) is in the Game module.
/// </summary>
public sealed class AlchemistsGeniusState
{
    public bool Active { get; private set; }
    public ushort Row { get; private set; }
    public double Ends { get; private set; }

    public void Start(ushort row, double now, double seconds)
    {
        Row = row;
        Ends = now + seconds;
        Active = true;
    }

    public void Clear() => Active = false;

    public bool Due(double now) => Active && now >= Ends;
}
