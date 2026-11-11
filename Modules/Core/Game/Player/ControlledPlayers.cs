using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static async Task InitializeControlledPlayer(ClientState conn, ClientState leader)
    {
        RegisterVitalsAdmission(conn);
        PrepareCharacterForArea(conn.Wire.Remote, conn, conn.Pools.Level);
        InitializeCharacterVitals(conn);
        conn.ControlledPrepared = true;
        var at = ControlledFollowerPoint(conn, leader, arrival: true);
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

    public static async Task StepControlledPlayer(ClientState conn, ClientState leader)
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
        bool following = KeepControlledFollowerNear(conn, leader, now, ControlledExternalMovement(conn, leader, now));
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
        if (await ExecuteControlledIntent(conn, leader, now, following)) return;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => SkillHitPlan.ForSkill(SkillsFor(conn), g.Effect, g.Level, g.Quality).CanHit
            && GemSkillInstanceDisabled(conn, g.Effect, g.Id) is null);
        bool onTheMove = following || leader.Walk.Moving(now);
        double seconds = gem.Name is null ? .35 : SkillsFor(conn).IsAttack(gem.Effect)
            ? SupportedAttackSeconds(conn, gem.Id) : (CastMs(conn, gem.Effect, gem.Id) ?? 750) / 1000.0;
        double gap = Dist(conn.Walk.Position(now), leader.Walk.Position(now));
        double ahead = ControlledAhead(conn.Walk.Position(now), leader.Walk.Position(now), leader.Walk.Destination);
        bool mayAttack = !onTheMove || ControlledFollowAttackFits(gap, leader.Walk.Moving(now) ? leader.Walk.Speed : 0, seconds, ahead);
        (uint Id, SpawnedMonster Monster)? target;
        var here = conn.Walk.Position(now);
        var plan = FollowerPlans.GetOrCreateValue(conn);
        double reach = gem.Name is null ? 0 : ControlledInPlaceReach(conn, gem.Id, gem.Effect);
        lock (conn.Monsters)
            target = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && mayAttack
                    && !conn.AsleepMonsters.Contains(p.Key)
                    && Dist(p.Value.Walk.Position(now), leader.Walk.Position(now)) < 60
                    && Dist(p.Value.Walk.Position(now), here) < 65
                    && (!onTheMove || conn.Walk.InReachOf(here, p.Value.Walk.Position(now), (int)reach, p.Value.StopRange - 2)))
                .OrderBy(p => p.Key == plan.Target ? 0 : 1).ThenBy(p => Dist(p.Value.Walk.Position(now), here))
                .Select(p => ((uint Id, SpawnedMonster Monster)?)(p.Key, p.Value)).FirstOrDefault();
        if (!InTown(conn) && target is { } enemy && gem.Name is not null)
        {
            HoldControlledFollowerAttack(conn, now, seconds, enemy.Id);
            var bytes = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(enemy.Id)
                .U16(gem.Id).U16(0xffff).U16(onTheMove ? (ushort)1 : (ushort)0).ToArray();
            await OnClientMessages(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                [(InstanceTransfer.UseSkillOnObjectId, bytes)], [], conn);
        }
        else if (!following) IdleControlledFollower(conn, leader, now);
    }

    static double ControlledInPlaceReach(ClientState conn, ushort skill, int effect)
    {
        if (SkillsFor(conn).ActionHash(effect) == GroundSlamWave.Action)
            return GroundSlamWave.Length(SkillRadius(conn, SupportedSkill(conn, skill).Stats, spell: false, effect));
        if (SkillsFor(conn).IsMelee(effect)) return WeaponFor(conn).Range;
        if (SkillsFor(conn).IsSelfTargeted(effect))
            return SkillRadius(conn, SupportedSkill(conn, skill).Stats, SkillsFor(conn).IsSpell(effect), effect);
        return 65;
    }
}
