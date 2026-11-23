using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The original action guards release only. Once created, an arrow or wand bolt owns the area's flight lifetime.
    // A frozen player (PlayerElementalAilments) or hit recovery cancels an unreleased shot, as a newer action does.
    public static bool OrdinaryRangedReleaseCurrent(Player player, int action, int epoch)
    {
        double now = AreaTime(player);
        return player.ProjectileActive(epoch) && !player.RageRetired && player.PlayerLife > 0 && player.ActionCount == action
            && player.ElementalAilments.ActionFactor(now) > 0 && !player.HitRecovery.Active(now);
    }

    // A pause can win the send gate after an action was accepted. Keep its echo pending rather than releasing an
    // unconfirmed arrow after resume; a later action, death or transfer still cancels it with its original token.
    public static async Task<bool> SendOrdinaryRangedAction(Player player, byte[][] bytes, string? log, int action, int epoch)
    {
        bool sent = false;
        while (!sent && OrdinaryRangedReleaseCurrent(player, action, epoch))
        {
            await SendWorldRecords(player, player.Id, () => bytes, log, still: () =>
            {
                return sent = OrdinaryRangedReleaseCurrent(player, action, epoch) && !player.Paused;
            });
            if (!sent) await AreaDelay(player, 0.016);
        }
        return sent;
    }

    // The runtime schedules the release first, anchored to the accepted input rather than to the send gate; the echo
    // follows. The release guard rechecks the action token, so a cancelled or replaced action creates no projectile.
    public static async Task ConfirmAndLaunchOrdinaryRanged(Player player, byte[][] actionBytes, string actionLog, (int X, int Y) aim, double attackSeconds, int action, int epoch,
        string projectileId, ushort sequence, SkillCriticalRoll criticalRoll)
    {
        await LaunchOrdinaryRanged(player, aim, attackSeconds, action, epoch, projectileId, sequence, criticalRoll);
        await SendOrdinaryRangedAction(player, actionBytes, actionLog, action, epoch);
    }

    // A bow or wand releases at the contact of the clip the class ACT's stance pool gives this use: a wand's is the one
    // the native selector picks for the input's sequence (AnimationVariantSelector), a bow's attack1_bow (the item's own).
    public static double OrdinaryRangedContactDelay(Player player, double attackSeconds, string projectileId,
        ushort sequence) => OrdinaryRangedContact(player, attackSeconds, projectileId, sequence).Delay;

    /// <summary>When and from where an ordinary bow or wand shot leaves: the delay to the releasing clip's contact
    /// keyframe, and that keyframe's position in the model's space (the Ranger's attack1_bow: 59.8 units in front at
    /// 127.5 up; the ACT-selected wand clip's tip), or null without rig data for the class or weapon. With
    /// <paramref name="clientClock"/> (a lockstep client), the delay is the client's clip clock's
    /// (<see cref="DefaultAttackClip.ReleaseSeconds"/>), which <c>LockstepShotAt</c> puts on its frame tick.</summary>
    public static (double Delay, ContactPoint? Point) OrdinaryRangedContact(Player player, double attackSeconds,
        string projectileId, ushort sequence, bool clientClock = false)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? Skills.ClassFor(player.Character.ClassRow).AnimatedObject;
        var profile = AnimationWeaponProfileFor(player);
        if (Animations.TryDefaultAttackClip(ao, profile, sequence, attackSeconds, out var selected))
        {
            double delay = Math.Max(0, clientClock ? selected.ReleaseSeconds(attackSeconds) : selected.ContactSeconds(attackSeconds));
            if (projectileId == OrdinaryRangedAttack.Wand)
                Console.WriteLine($"[gateway] wand release sequence 0x{sequence:x4}: {selected.Stance}/{selected.Animation} " +
                    $"(clip {selected.Index}, contact {selected.ContactMs}/{selected.LengthMs} ms), " +
                    $"attack {attackSeconds:0.000000} s, release after {delay:0.000000} s");
            return (delay, selected.Point);
        }
        if (projectileId == OrdinaryRangedAttack.Wand)
            Console.WriteLine($"[gateway] WARNING: missing native wand variation metadata for {ao}; using approximate attack1 release");
        return (AttackContactDelay(player, attackSeconds, approach: false), Animations.AttackContactPoint(ao, profile));
    }

    // Ranged release has no walk-in offset or melee early-hit clamp: the shot is scheduled on the projectile runtime
    // at the weapon's contact and resolved there (Projectiles/Program.ProjectileRanged.cs). The use's critical roll was
    // captured at acceptance and travels with the shot. The contact counts from swingAt when the swing began before now
    // (a lockstep walk-up's goal frame), else from now.
    public static Task LaunchOrdinaryRanged(Player player,
        (int X, int Y) aim, double attackSeconds, int action, int epoch, string projectileId, ushort sequence,
        SkillCriticalRoll criticalRoll, double? swingAt = null) =>
        ScheduleOrdinaryRanged(player, aim, attackSeconds, action, epoch, projectileId, sequence, criticalRoll,
            swingAt);
}
