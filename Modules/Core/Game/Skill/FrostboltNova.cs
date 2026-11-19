using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    const ushort FrostboltAction = 0x5e92;
    const int FrostboltNovaTargetRange = 100;

    // The IceNova action compares squared distances between grid cells. It retains the closest FrostBolt actions
    // on the caster, up to the gem's limit. A later candidate tied with the last one in a full list is dropped.
    internal static ProjectileEntity[] FrostboltNovaTargets(ClientState conn, (int X, int Y) aim, int limit)
    {
        if (limit == 0 || conn.ProjectileRuntime is not { Active: true } session) return [];
        var caster = new ProjectileActorKey(PlayerId, conn.ProjectileEpoch);
        var targets = new List<(ProjectileEntity Bolt, double DistanceSquared)>();
        foreach (var bolt in session.Runtime.Projectiles)
        {
            if (bolt.Use.Owner != caster || bolt.Use.Emitter != caster || bolt.Use.Payload is not ProjectileOffense { Effect: >= 0 } shot
                || SkillsFor(conn).ActionHash(shot.Effect) != FrostboltAction) continue;
            var cell = FrostboltCell(bolt);
            double dx = (double)cell.X - aim.X, dy = (double)cell.Y - aim.Y;
            double squared = dx * dx + dy * dy;
            if (squared > FrostboltNovaTargetRange * FrostboltNovaTargetRange || targets.Count == limit && squared >= targets[^1].DistanceSquared) continue;
            int index = targets.FindIndex(target => squared <= target.DistanceSquared);
            targets.Insert(index < 0 ? targets.Count : index, (bolt, squared));
            if (targets.Count > limit) targets.RemoveAt(limit);
        }
        return [.. targets.Select(t => t.Bolt)];
    }

    static (int X, int Y) FrostboltCell(ProjectileEntity bolt) =>
        ((int)Math.Round(bolt.Position.X), (int)Math.Round(bolt.Position.Y));

    // Projectiles after the first start 100 ms apart. A bolt expands again 300 ms after its ring reaches full size.
    internal static double FrostboltNovaDelay(int bolt, int expansion, int artVariation) =>
        bolt * 0.1 + expansion * (IceNovaRing.Seconds(artVariation) + 0.3);

    static async Task CastFrostboltNova(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll critical, SacrificeUse sacrifice,
        IReadOnlyDictionary<uint, int> own, int repeats, int seals, double sealMore)
    {
        if (CastMs(conn, gem.Effect, skillId) is not int castMs) return;
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        int limit = GolemStat(own, "ice_nova_number_of_frost_bolts_to_cast_on");
        double boltMore = 1 + GolemStat(own, "ice_nova_damage_when_cast_on_frostbolt_+%_final") / 100d;
        double cast = castMs / 1000d;
        int variation = SkillArtVariation(conn, gem.Gem);
        bool redirected = false;
        var releases = new List<Task>();
        using (ProjectileMutation(conn))
        {
            long first = session.Runtime.Now + ProjectileRuntime.Time(cast
                * Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
            for (int release = 0; release <= repeats + seals; release++)
            {
                int repeat = Math.Max(0, release - seals);
                bool seal = release > 0 && release <= seals;
                long at = first + ProjectileRuntime.Time(Math.Min(release, seals) * cast / 10 + repeat * cast);
                releases.Add(At(at, () => Release(repeat, seal)));
            }
        }
        await Task.WhenAll(releases);

        // Selection and every later position sample run at the flight simulator's exact instant, even if one
        // pump tick covers both a ring start and the end of the projectile. Only the released hit awaits output.
        async Task At(long at, Func<Task> start)
        {
            var result = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Runtime.Schedule(at, () =>
            {
                try { result.SetResult(start()); }
                catch (Exception error) { result.SetException(error); }
            });
            await Task.WhenAny(result.Task, session.OutputCompleted);
            if (result.Task.IsCompleted) await result.Task.Unwrap();
        }

        async Task Release(int repeat, bool seal)
        {
            if (redirected && repeat > 0 || !session.Active || conn.PlayerLife == 0 || !sacrifice.Current
                || !(cooldownUse?.Current(conn) ?? PlayerActionCurrent(conn, action))) return;
            var bolts = FrostboltNovaTargets(conn, aim, limit);
            double more = seal ? sealMore : 1;
            if (bolts.Length == 0)
            {
                var centre = SpellCentre(conn, gem.Effect, conn.Walk.Position(ProjectileWorldTime(conn)), aim);
                await Hit(centre, repeat, repeat > 0 && repeat == repeats, more);
                return;
            }
            if (!seal)
            {
                redirected = true;
                ReleaseFrostboltNovaRepeats(conn, sequence, repeats, repeat);
            }
            var rings = new List<Task>();
            long contact = session.Runtime.Now;
            for (int i = 0; i < bolts.Length; i++)
            {
                var bolt = bolts[i];
                // Each new ring starts its own expansion counter at zero.
                for (int expansion = 0; expansion <= (seal ? 0 : repeats); expansion++)
                {
                    long at = contact + ProjectileRuntime.Time(FrostboltNovaDelay(i, expansion, variation));
                    rings.Add(At(at, () => session.Active && conn.PlayerLife > 0 && sacrifice.Current
                        && session.Runtime.Projectiles.Contains(bolt) ? Hit(FrostboltCell(bolt), 0, false, more * boltMore)
                        : Task.CompletedTask));
                }
            }
            await Task.WhenAll(rings);
        }

        Task Hit((int X, int Y) centre, int repeat, bool final, double more) =>
            AreaSkillHitForUse(null, remote, stream, ciphers, conn, skillId, gem, centre, centre, immediate: true,
                sequence: sequence, criticalRoll: critical, sacrifice: sacrifice, damageMore: more, repeat: repeat,
                finalSpellRepeat: final, novaStartedAt: ProjectileWorldTime(conn),
                triggeredCurrent: () => session.Active && conn.PlayerLife > 0);
    }
}
