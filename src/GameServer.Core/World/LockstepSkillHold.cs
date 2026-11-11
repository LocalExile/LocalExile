namespace GameServer.Core.World;

/// <summary>
/// The skill a player in lockstep asks for (Agents/docs/protocol/07-latency-visibility-movement.md §6). GGG's server
/// keeps the newest request and starts it when the player is free, so a click during a swing waits and is not cancelled
/// by its own 0x195. While the button stays down it repeats the skill by itself, once per action, aimed at the newest
/// 0x192 (one 0x18e gave 30 attacks 462 ms apart), and an attack on an object that dies goes on at the ground.
/// A channelled skill starts once and runs until the release.
/// </summary>
public sealed class LockstepSkillHold
{
    private bool _pending, _held, _repeats;
    private ushort? _finishedChannel;

    public ushort Skill { get; private set; }

    /// <summary>The click's action flags, repeated on each use it leads to.</summary>
    public ushort Flags { get; private set; }

    /// <summary>The object it is aimed at, while there is one.</summary>
    public uint? Target { get; private set; }

    /// <summary>The cell it is aimed at.</summary>
    public (int X, int Y) Aim { get; private set; }

    /// <summary>The sequence of the channel running, if one is.</summary>
    public ushort? Channel { get; private set; }

    /// <summary>Whether anything is asked for or running.</summary>
    public bool Active => _pending || _held || Channel is not null;

    /// <summary>A new click waiting to start, rather than the automatic repeat of a held skill.</summary>
    public bool Pending => _pending;

    /// <summary>C→S 0x18e or 0x18f.</summary>
    public void Press((int X, int Y) aim, uint? target, ushort skill, ushort flags, bool repeats)
    {
        if (Channel is { } channel) _finishedChannel = channel;
        Channel = null;
        (Aim, Target, Skill, Flags, _repeats) = (aim, target, skill, flags, repeats);
        _pending = _held = true;
    }

    /// <summary>C→S 0x192: the held skill's newest aim.</summary>
    public void Update((int X, int Y) aim)
    {
        if (_held) Aim = aim;
    }

    /// <summary>C→S 0x195, or 0x196 for this skill. A request not yet started still starts.</summary>
    public void Release() => _held = false;

    /// <summary>Clear input; when another action takes over, retain the old channel's release for the next frame.</summary>
    public void Clear(bool finishChannel = false)
    {
        _finishedChannel = finishChannel ? Channel ?? _finishedChannel : null;
        _pending = _held = false;
        Channel = null;
    }

    /// <summary>The object it was aimed at is gone: it goes on at the ground.</summary>
    public void LoseTarget() => Target = null;

    /// <summary>Whether a use should start in this frame, when the player is <paramref name="free"/>. A new click does;
    /// the held skill's next use only once the use before it is over as well (<paramref name="repeat"/>): an attack
    /// still walking up holds its repeat, but not a click, which only a swing holds.</summary>
    public bool Due(bool free, bool repeat = true)
    {
        if (!free || !(_pending || repeat && _held && _repeats && Channel is null)) return false;
        _pending = false;
        return true;
    }

    /// <summary>The use just started is a channel, under this sequence.</summary>
    public void Channelling(ushort sequence) => Channel = sequence;

    /// <summary>A replaced channel, or the running channel whose button was released: finish it before a new start.</summary>
    public ushort? ChannelReleased()
    {
        if (_finishedChannel is { } finished)
        {
            _finishedChannel = null;
            return finished;
        }
        if (_held || Channel is not { } sequence) return null;
        Channel = null;
        return sequence;
    }
}
