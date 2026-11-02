namespace GameServer.Core.World;

/// <summary>
/// What the player asks of a walk in lockstep (Agents/docs/protocol/07-latency-visibility-movement.md §6). A lockstep
/// client starts nothing itself: it sends 0x18e on the click, 0x192 every 69 ms while the button is down and 0x195 on
/// release. GGG's server keeps the newest target and starts the Move from its own tick, so a walk follows the cursor at
/// the pace of 0x192 and a blocked one is tried again every frame from the same cell (471 Moves, a median 33 ms apart).
/// 0x195 is not answered and does not cancel a target not yet started.
/// </summary>
public sealed class LockstepHold
{
    private (int X, int Y)? _target;
    private bool _pending, _held;

    /// <summary>The action flags of the click (0x18e), repeated on each Move it leads to.</summary>
    public ushort Flags { get; private set; }

    /// <summary>The Move skill of the click (0x18e).</summary>
    public ushort Skill { get; private set; }

    /// <summary>C→S 0x18e: a click, with the target as a cell.</summary>
    public void Press((int X, int Y) target, ushort skill, ushort flags)
    {
        (_target, Skill, Flags) = (target, skill, flags);
        _pending = _held = true;
    }

    /// <summary>C→S 0x192: the held button's newest target. Only a held Move has one.</summary>
    public void Update((int X, int Y) target)
    {
        if (!_held || _target == target) return;
        _target = target;
        _pending = true;
    }

    /// <summary>C→S 0x195.</summary>
    public void Release()
    {
        _held = false;
        if (!_pending) _target = null;
    }

    /// <summary>Another action took over: nothing is held or waiting any more.</summary>
    public void Clear()
    {
        _target = null;
        _pending = _held = false;
    }

    /// <summary>The target a Move should start for in this frame, if any: a new one, or the held one when the walk has
    /// stopped short of it. <paramref name="walking"/> and <paramref name="here"/> are the server's walk at this frame.</summary>
    public (int X, int Y)? Due(bool walking, (int X, int Y) here)
    {
        if (_target is not { } target) return null;
        if (!_pending && (walking || here == target)) return null;
        _pending = false;
        if (!_held) _target = null;
        return target;
    }
}
