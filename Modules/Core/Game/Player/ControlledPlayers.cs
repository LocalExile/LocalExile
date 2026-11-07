using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static async Task InitializeControlledPlayer(ClientState conn, ClientState leader)
    {
        RegisterVitalsAdmission(conn);
        PrepareCharacterForArea(conn.Wire.Remote, conn, conn.Pools.Level);
        InitializeCharacterVitals(conn);
        conn.ControlledPrepared = true;
        var at = conn.SpawnNear(leader.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        conn.Walk.Teleport(at.X, at.Y, conn.Clock.Elapsed.TotalSeconds);
        conn.PeerType = InstanceEntities.ObjectTypeHash(SkillsFor(conn).ClassFor(conn.ClassRow).ObjectType);
        conn.PeerTemplate = PlayerForCreation(conn) with { GridX = at.X, GridY = at.Y };
        conn.ClientSkillIds = [.. conn.PeerTemplate.Skills!.Select(s => s.Id)];
        conn.PlayerSent = conn.PlayerObjectCreated = true;
        Program.InGame[conn] = 0;
        global::HotReload.Register(conn);
        await PopulateArea(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        if (conn.Closed) return;
        PublishPlayerPose(conn);
        EnsurePlayerUpdates(conn);
    }

    internal static async Task StepControlledPlayer(ClientState conn, ClientState leader)
    {
        if (conn.Closed || leader.Closed || conn.LeavingArea || conn.Paused || !leader.PlayerSent) return;
        EnsurePlayerUpdates(conn);
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (conn.PlayerLife == 0)
        {
            if (double.IsNaN(conn.ControlledDeadSince)) conn.ControlledDeadSince = now;
            if (now - conn.ControlledDeadSince >= 3)
            {
                await Resurrect(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, inTown: false);
                conn.ControlledDeadSince = double.NaN;
            }
            return;
        }
        if (now < conn.ControlledNextAction) return;
        conn.ControlledNextAction = now + .35;
        if (conn.Passives.Count < conn.Pools.Level - 1)
        {
            var cls = (GameServer.Core.Accounts.CharacterClass)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId;
            var nodes = BuildControlledPlayer(cls, conn.Pools.Level).Passives!;
            lock (conn.Monsters)
            {
                CharacterSaves.SetMainAscendancy(CharacterName(conn), conn.MainAscendancy, nodes, conn.MasteryEffects,
                    expected: conn.SavedPassiveProgress);
                conn.Passives = [.. nodes];
                conn.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(CharacterSaves.Load(CharacterName(conn))!);
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            await SendCharacterStats(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, "bot allocates earned passive points");
        }
        (uint Id, SpawnedMonster Monster)? target;
        var here = conn.Walk.Position(now);
        lock (conn.Monsters)
            target = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && !conn.AsleepMonsters.Contains(p.Key)
                    && Dist(p.Value.Walk.Position(now), leader.Walk.Position(now)) < 85
                    && Dist(p.Value.Walk.Position(now), here) < 90)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), here))
                .Select(p => ((uint Id, SpawnedMonster Monster)?)(p.Key, p.Value)).FirstOrDefault();
        var gem = GemSkillEffects(conn).FirstOrDefault(g => SkillHitPlan.ForSkill(SkillsFor(conn), g.Effect, g.Level, g.Quality).CanHit
            && GemSkillInstanceDisabled(conn, g.Effect, g.Id) is null);
        if (!InTown(conn) && target is { } enemy && gem.Name is not null)
        {
            var bytes = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(enemy.Id)
                .U16(gem.Id).U16(0xffff).U16(0).ToArray();
            await OnClientMessages(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                [(InstanceTransfer.UseSkillOnObjectId, bytes)], [], conn);
        }
        else
        {
            var follow = leader.Walk.Position(now);
            if (Dist(here, follow) > 22)
            {
                var at = conn.SpawnNear(follow);
                conn.Walk.Order(at.X, at.Y, now);
                PublishPlayerPose(conn);
            }
        }
    }
}
