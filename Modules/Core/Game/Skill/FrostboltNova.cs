using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    const ushort FrostboltAction = 0x5e92;
    const int FrostboltNovaTargetRange = 100;

    // Both nova actions compare squared cell distances and retain the closest FrostBolt actions
    // on the caster, up to the gem's limit. A later candidate tied with the last one in a full list is dropped.
    internal static ProjectileEntity[] FrostboltNovaTargets(ClientState conn, (int X, int Y) aim, int limit, bool forVortex = false)
    {
        if (limit == 0 || conn.ProjectileRuntime is not { Active: true } session) return [];
        var caster = new ProjectileActorKey(PlayerId, conn.ProjectileEpoch);
        var targets = new List<(ProjectileEntity Bolt, double DistanceSquared)>();
        foreach (var bolt in session.Runtime.Projectiles)
        {
            if (bolt.Use.Owner != caster || bolt.Use.Emitter != caster || forVortex && session.ReservedVortexBolts.Contains(bolt.Id)
                || bolt.Use.Payload is not ProjectileOffense { Effect: >= 0 } shot
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

    static bool ManualMarkedNova(ClientState conn, ushort id, SupportedGem gem) =>
        (SkillsFor(conn).ActionHash(gem.Effect) == IceNovaRing.Action || SkillsFor(conn).EffectId(gem.Effect) is "Vortex" or "VortexAltX")
            && !IsTotemSkill(conn, id) && PlacedKind(gem) is { Trap: false, Mine: false } && TriggerOnlyReason(gem) is null;

    // Both native actions resolve the owner's mark at release, after looking for Frostbolts.
    static (int X, int Y)? MarkedNovaCentreLocked(ClientState conn, ushort id, SupportedGem gem, double now) =>
        MarkedMonsterLocked(conn) is { } target && SkillsFor(conn).IsNova(gem.Effect) && SkillsFor(conn).IsSpell(gem.Effect)
            && GolemStat(CharacterPlayerStats(conn), "nova_spells_cast_at_marked_target") != 0 && ManualMarkedNova(conn, id, gem)
            ? target.Monster.Walk.Position(now) : null;

    static async Task CastFrostboltNova(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll critical, SacrificeUse sacrifice,
        SupportedGem gemStats, int repeats, int seals, double sealMore, bool vortex)
    {
        var own = gemStats.Stats;
        if (CastMs(conn, gem.Effect, skillId) is not int castMs) return;
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        int limit = GolemStat(own, vortex ? "frost_bolt_nova_number_of_frost_bolts_to_detonate" : "ice_nova_number_of_frost_bolts_to_cast_on");
        double boltMore = 1 + GolemStat(own, vortex ? "active_skill_if_used_through_frostbolt_damage_+%_final"
            : "ice_nova_damage_when_cast_on_frostbolt_+%_final") / 100d;
        double cast = castMs / 1000d;
        int variation = vortex ? SkillCosmeticStat(conn, gem.Gem, "ground_maelstrom_art_variation") : SkillArtVariation(conn, gem.Gem);
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
            var bolts = FrostboltNovaTargets(conn, aim, limit, forVortex: vortex);
            double more = seal ? sealMore : 1;
            if (bolts.Length == 0)
            {
                (int X, int Y) centre;
                lock (conn.Monsters)
                {
                    double now = ProjectileWorldTime(conn);
                    centre = MarkedNovaCentreLocked(conn, skillId, gemStats, now) ?? SpellCentre(conn, gem.Effect, conn.Walk.Position(now), aim);
                }
                await Hit(centre, repeat, repeat > 0 && repeat == repeats, more);
                return;
            }
            if (vortex)
            {
                var detonations = new List<Task>(bolts.Length);
                long released = session.Runtime.Now;
                for (int i = 0; i < bolts.Length; i++)
                {
                    var bolt = bolts[i];
                    if (i == 0) detonations.Add(Detonate(bolt));
                    else
                    {
                        session.ReservedVortexBolts.Add(bolt.Id);
                        detonations.Add(At(released + i * ProjectileRuntime.QuantaPerSecond / 2, () =>
                        {
                            session.ReservedVortexBolts.Remove(bolt.Id);
                            return Detonate(bolt);
                        }));
                    }
                }
                await Task.WhenAll(detonations);
                return;

                Task Detonate(ProjectileEntity bolt)
                {
                    if (!session.Active || conn.PlayerLife == 0 || !sacrifice.Current || !session.Runtime.TryConsume(bolt.Id))
                        return Task.CompletedTask;
                    return Hit(FrostboltCell(bolt), repeat, repeat > 0 && repeat == repeats, more * boltMore, throughBolt: true);
                }
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

        Task Hit((int X, int Y) centre, int repeat, bool final, double more, bool throughBolt = false)
        {
            int? radius = null;
            if (vortex)
            {
                session.SettleVortexGround();
                radius = SkillRadius(conn, own, true, gem.Effect, repeat, throughBolt);
            }
            var hit = AreaSkillHitForUse(null, remote, stream, ciphers, conn, skillId, gem, centre, centre, immediate: true,
                sequence: sequence, criticalRoll: critical, sacrifice: sacrifice, damageMore: more, repeat: repeat,
                radiusOverride: radius, finalSpellRepeat: final, novaStartedAt: vortex ? null : ProjectileWorldTime(conn),
                scheduledSession: vortex ? session : null, triggeredCurrent: () => session.Active && conn.PlayerLife > 0);
            if (vortex) PlaceVortex(session, centre, gem.Effect, own, repeat, more, throughBolt, variation);
            return hit;
        }
    }
}
