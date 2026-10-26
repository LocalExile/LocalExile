/// <summary>
/// One of a variety's TakeHit clips: its index in the rig's animation list (S→C 0x1b2 kind 0) and its length in
/// seconds (data/monster-takehit.json "indexes" and "lengths").
/// </summary>
readonly record struct HitAnimation(int Index, double Seconds);

/// <summary>
/// The gates on a monster's hit reaction (S→C 0x1b2, sent by <c>SendFlinch</c> in Program.cs).
/// A hit must take at least a tenth of the monster's max life — the low end of PoE's stun range — so scratches do not
/// flinch. And a monster does not flinch again until its clip has played (at least 0.4 s): otherwise a fast attack, a
/// volley or an area hit restarts the clip and its SoundEvent and keeps cutting the monster's own attack and walk
/// animations, while the server goes on moving and attacking it on its own timeline. The clips run at speed 1, so the
/// clip's length is how long it plays (flinch_large is at least 0.45 s by its own .act condition).
/// </summary>
static class HitReaction
{
    public const double DamageFraction = 0.1;
    public const double CooldownSeconds = 0.4;

    /// <summary>
    /// The clip this hit plays, or null when it does not flinch the monster. A flinch claims the monster until the clip
    /// has played, so call it under the Monsters lock.
    /// </summary>
    public static HitAnimation? TryFlinch(SpawnedMonster m, int damage, double now, Random random)
    {
        if (m.Life == 0 || m.Dead || m.HitAnimations is not { Count: > 0 } clips) return null;
        if (m.Mods.Effects.CannotBeStunned) return null;   // Unwavering: the flinch is the hit's stun
        if (damage < m.MaxLife * DamageFraction) return null;
        if (now < m.FlinchUntil) return null;
        var clip = clips[random.Next(clips.Count)];
        m.FlinchUntil = now + Math.Max(CooldownSeconds, clip.Seconds);
        return clip;
    }
}
