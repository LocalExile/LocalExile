using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // Dark Bargain (DarkPact; Dark Pact before 3.29): at the cast's contact it takes the player's skeleton nearest the aimed
    // point (else nearest the player), sacrifices skeletal_chains_aoe_%_health_dealt_as_chaos_damage of its maximum life
    // (never its last point: not damage, so it cannot kill) and explodes there, adding the sacrificed life as base chaos
    // damage. It then chains number_of_chains times, each to the nearest unvisited skeleton, which pays and explodes the
    // same way. With no skeleton in reach it sacrifices that share of the player's maximum life instead, explodes around
    // the player and deals skeletal_chains_no_minions_damage_+%_final more damage (even if no life could be paid).
    const string DarkBargainSkin = "default";
    const string DarkBargainSkeletonAo = "Metadata/Effects/Spells/skeletal_chains/minion_explode.ao";
    const string DarkBargainPlayerAo = "Metadata/Effects/Spells/skeletal_chains/explosion.ao";
    // ESTIMATE: no table carries how far the skill looks for a skeleton or how far a chain jumps.
    public const int DarkBargainSeekRadius = 40;
    public const int DarkBargainChainRadius = 40;
    const double DarkBargainChainSeconds = 0.1;
    const int DarkBargainArtMs = 1500;

    public static bool IsDarkBargain(ClientState conn, int effect) =>
        SkillsFor(conn).EffectId(effect) is "DarkPact" or "DarkPactRoyale";

    static async Task DarkBargainCast(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) to,
        int repeat, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll? criticalRoll, SacrificeUse sacrifice,
        double sealDelay = 0, double reoccurrenceMore = 1)
    {
        if (CastMs(conn, gem.Effect, skillId) is not int castMs) return;
        bool Current() => !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
            && (cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken));
        double delay;
        lock (conn.Monsters)
            delay = sealDelay + castMs / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
        await WhilePaused(conn);
        if (!Current())
        {
            Console.WriteLine($"[gateway] {remote} dark bargain: cast replaced before its contact, nothing sacrificed");
            return;
        }
        var skillStats = SupportedSkill(conn, skillId).Stats;
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        int percent = Math.Max(0, Stat("skeletal_chains_aoe_%_health_dealt_as_chaos_damage"));
        int chains = Math.Max(0, Stat("number_of_chains"));
        var blasts = new List<((int X, int Y) At, int Sacrificed, uint Skeleton)>();
        bool self = false;
        int epoch = conn.ProjectileEpoch;
        using (ProjectileMutation(conn))
        {
            if (!Current() || !sacrifice.Current) return;
            double now = ProjectileWorldTime(conn);
            var player = conn.Walk.Position(now);
            var skeletons = conn.Summons.Fighters.Values
                .Where(g => g.Definition.Kind == "Skeleton" && GolemCurrent(conn, g) && !g.Monster.Dead && g.Monster.Life > 0)
                .ToDictionary(g => g.Id);
            foreach (var blast in DarkBargainChain(
                [.. skeletons.Values.Select(g => (g.Id, g.Monster.Walk.Position(now), g.Monster.Life, g.Monster.MaxLife))],
                to, player, percent, chains))
            {
                var m = skeletons[blast.Skeleton].Monster;
                if (blast.Sacrificed > 0)
                {
                    m.Life -= (uint)blast.Sacrificed;
                    conn.Minions.OutputQueue.Add(new([], null, blast.Skeleton, m, PlayerMinions.OutputKind.Life));
                }
                blasts.Add(blast);
            }
            if (blasts.Count == 0)
            {
                self = true;
                int paid = DarkBargainLifePaid(conn.PlayerLife, conn.PlayerMaxLife, percent);
                conn.PlayerLife -= (uint)paid;
                blasts.Add((player, paid, 0));
            }
        }
        if (self && blasts[0].Sacrificed > 0)
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                lock (conn.Monsters) return InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife);
            });
        double more = self ? Math.Max(0, 1 + Stat("skeletal_chains_no_minions_damage_+%_final") / 100.0) : 1;
        int radius = SkillRadius(conn, skillStats, true, gem.Effect);
        string ao = self ? DarkBargainPlayerAo : DarkBargainSkeletonAo;
        Console.WriteLine($"[gateway] {remote} dark bargain: {(self ? "own life" : $"{blasts.Count} skeleton(s)")}, "
            + $"sacrificed [{string.Join(", ", blasts.Select(b => b.Sacrificed))}] ({percent}%), chains {chains}, radius {radius}, more x{more:0.##}");
        // The art is drawn at MiscAnimated __col4 world units at scale 1 (SkeletalChainsExplodePlayer 200,
        // SkeletalChainsExplode 120): scale it to the blast's radius.
        float scale = (float)(radius * (250.0 / 23) / (Art.ArtRadius(ao) ?? (self ? 200 : 120)));
        Console.WriteLine($"[gateway] {remote} dark bargain skin {DarkBargainSkin}: art {ao}");
        var art = new List<uint>();
        for (int i = 0; i < blasts.Count; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(DarkBargainChainSeconds));
            if (conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch)) break;
            var (at, paid, skeleton) = blasts[i];
            uint effect;
            lock (conn.Monsters) effect = conn.TakeObjectId();
            art.Add(effect);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, ao) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()),
                $"0x02bb create effect 0x{effect:x} {ao} at ({at.X}, {at.Y}) scale {scale:0.##}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(effect), null);
            int addedChaos = paid;
            Console.WriteLine($"[gateway] {remote} dark bargain blast {i + 1}/{blasts.Count} at ({at.X}, {at.Y})"
                + $"{(skeleton != 0 ? $" skeleton 0x{skeleton:x}" : "")}: paid {paid}, added chaos {addedChaos}");
            await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at, immediate: true,
                criticalRoll: criticalRoll, sacrifice: sacrifice, baseChaos: paid, damageMore: more * reoccurrenceMore);
        }
        if (art.Count > 0)
            _ = Task.Run(async () =>
            {
                await Task.Delay(DarkBargainArtMs);
                foreach (uint effect in art)
                    if (conn.ProjectileActive(epoch)) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(effect), null);
            });
    }

    // The life a blast pays: the percent of the maximum, never the last point (a sacrifice, not damage: it cannot kill).
    public static int DarkBargainLifePaid(uint life, uint maxLife, int percent) =>
        (int)Math.Max(0, Math.Min((long)life - 1, (long)maxLife * Math.Max(0, percent) / 100));

    // The skeletons a cast explodes, in order: the one nearest the aim within the seek radius (else nearest the player),
    // then number_of_chains jumps, each to the nearest skeleton not yet used within the chain radius. Empty: none in reach.
    public static List<((int X, int Y) At, int Sacrificed, uint Skeleton)> DarkBargainChain(
        IReadOnlyList<(uint Id, (int X, int Y) At, uint Life, uint MaxLife)> skeletons, (int X, int Y) aim,
        (int X, int Y) player, int percent, int chains)
    {
        var used = new HashSet<uint>();
        (uint Id, (int X, int Y) At, uint Life, uint MaxLife)? Nearest((int X, int Y) point, int reach) =>
            skeletons.Where(s => !used.Contains(s.Id) && Dist(s.At, point) <= reach).OrderBy(s => Dist(s.At, point))
                .Select(s => ((uint, (int, int), uint, uint)?)s).FirstOrDefault();
        var next = Nearest(aim, DarkBargainSeekRadius) ?? Nearest(player, DarkBargainSeekRadius);
        var blasts = new List<((int X, int Y) At, int Sacrificed, uint Skeleton)>();
        for (int link = 0; link <= Math.Max(0, chains) && next is { } skeleton; link++)
        {
            used.Add(skeleton.Id);
            blasts.Add((skeleton.At, DarkBargainLifePaid(skeleton.Life, skeleton.MaxLife, percent), skeleton.Id));
            next = Nearest(skeleton.At, DarkBargainChainRadius);
        }
        return blasts;
    }

}
