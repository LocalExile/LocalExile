/// <summary>
/// The source-owned Unhinge toggle. The equipped Doppelgänger Guise grant supplies the source identity; this state owns
/// the player-controlled on/off flag, its native buff and the sane-state hit recovery. Installing or retiring a source
/// always returns to sane.
/// </summary>
public sealed class UnhingedState
{
    public object? Owner;
    public WornGrantedSkills.Grant? Source;
    public bool Enabled;
    public ushort Buff;
    public int Version;
    public readonly TimedLifeRecovery HitRecovery = new();

    public bool Exists => Source is not null;
    public bool Active => Exists && Enabled;

    public void Install(object owner, WornGrantedSkills.Grant source)
    {
        Owner = owner;
        Source = source;
        Enabled = false;
        Version++;
        HitRecovery.Clear();
    }

    public bool Toggle()
    {
        if (!Exists) return false;
        Enabled = !Enabled;
        Version++;
        if (!Enabled) HitRecovery.Clear();
        return Enabled;
    }

    /// <summary>Returns the native buff instance the client must remove, if one was presented.</summary>
    public ushort Retire()
    {
        ushort buff = Buff;
        Owner = null;
        Source = null;
        Enabled = false;
        Buff = 0;
        Version++;
        HitRecovery.Clear();
        return buff;
    }
}
