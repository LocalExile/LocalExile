/// <summary>
/// The source-owned Unhinge toggle. The equipped Doppelgänger Guise grant supplies the source identity; this state owns
/// the player-controlled on/off flag, its native buff and the sane-state hit recovery. Installing or retiring a source
/// always returns to sane.
/// </summary>
sealed class UnhingedState
{
    internal object? Owner;
    internal WornGrantedSkills.Grant? Source;
    internal bool Enabled;
    internal ushort Buff;
    internal int Version;
    internal readonly TimedLifeRecovery HitRecovery = new();

    internal bool Exists => Source is not null;
    internal bool Active => Exists && Enabled;

    internal void Install(object owner, WornGrantedSkills.Grant source)
    {
        Owner = owner;
        Source = source;
        Enabled = false;
        Version++;
        HitRecovery.Clear();
    }

    internal bool Toggle()
    {
        if (!Exists) return false;
        Enabled = !Enabled;
        Version++;
        if (!Enabled) HitRecovery.Clear();
        return Enabled;
    }

    /// <summary>Returns the native buff instance the client must remove, if one was presented.</summary>
    internal ushort Retire()
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
