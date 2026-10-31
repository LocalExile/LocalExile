/// <summary>
/// One of a variety's TakeHit clips: its index in the rig's animation list (S→C 0x1b2 kind 0) and its length in
/// seconds (data/monster-takehit.json "indexes" and "lengths").
/// </summary>
readonly record struct HitAnimation(int Index, double Seconds);
