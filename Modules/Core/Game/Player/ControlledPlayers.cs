using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static async Task InitializeControlledPlayer(Player player, Player leader)
    {
        RegisterVitalsAdmission(player);
        PrepareCharacterForArea(player, player.Pools.Level);
        InitializeCharacterVitals(player);
        player.ControlledPrepared = true;
        var at = ControlledFollowerPoint(player, leader, arrival: true);
        player.Walk.Teleport(at.X, at.Y, AreaTime(player));
        player.PeerType = InstanceEntities.ObjectTypeHash(SkillsFor(player).ClassFor(player.Character.ClassRow).ObjectType);
        player.PeerTemplate = PlayerForCreation(player).FitSkillList().Fitted with { GridX = at.X, GridY = at.Y };
        player.ClientSkillIds = [.. player.PeerTemplate.Skills!.Select(s => s.Id)];
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        Program.InGame[player] = 0;
        global::HotReload.Register(player);
        await PopulateArea(player);
        if (player.Closed) return;
        PublishPlayerPose(player);
        EnsurePlayerUpdates(player);
    }

    public static async Task StepControlledPlayer(Player player, Player leader)
    {
        if (player.Closed || leader.Closed || player.LeavingArea || player.Paused || !leader.Connection.PlayerSent) return;
        EnsurePlayerUpdates(player);
        double now = AreaTime(player);
        if (player.PlayerLife == 0)
        {
            if (double.IsNaN(player.ControlledDeadSince)) player.ControlledDeadSince = now;
            if (now - player.ControlledDeadSince >= 3)
            {
                await Resurrect(player, inTown: false);
                player.ControlledDeadSince = double.NaN;
            }
            return;
        }
        bool following = KeepControlledFollowerNear(player, leader, now, ControlledExternalMovement(player, leader, now));
        if (now < player.ControlledNextAction) return;
        player.ControlledNextAction = now + .35;
        if (player.Character.Passives.Count < player.Pools.Level - 1)
        {
            var cls = (GameServer.Core.Accounts.CharacterClass)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId;
            var nodes = BuildControlledPlayer(cls, player.Pools.Level).Passives!;
            CharacterSaves.SetMainAscendancy(CharacterName(player), player.Character.MainAscendancy, nodes, player.Character.MasteryEffects,
                expected: player.Character.SavedPassiveProgress);
            player.Character.Passives = [.. nodes];
            player.Character.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(CharacterSaves.Load(CharacterName(player))!);
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            await SendCharacterStats(player, PlayerStatChange.Build);
        }
        if (await ExecuteControlledIntent(player, leader, now, following)) return;
        // Reactive reservation hits do not make their toggles usable as automatic attacks.
        var gem = GemSkillEffects(player).FirstOrDefault(g => SkillHitPlan.ForSkill(SkillsFor(player), g.Effect, g.Level, g.Quality).CanHit
            && ToggledReservation(player, g.Id) is null
            && GemSkillInstanceDisabled(player, g.Effect, g.Id) is null);
        bool onTheMove = following || leader.Walk.Moving(now);
        double seconds = gem.Name is null ? .35 : SkillsFor(player).IsAttack(gem.Effect)
            ? SupportedAttackSeconds(player, gem.Id) : (CastMs(player, gem.Effect, gem.Id) ?? 750) / 1000.0;
        double gap = Dist(player.Walk.Position(now), leader.Walk.Position(now));
        double ahead = ControlledAhead(player.Walk.Position(now), leader.Walk.Position(now), leader.Walk.Destination);
        bool mayAttack = !onTheMove || ControlledFollowAttackFits(gap, leader.Walk.Moving(now) ? leader.Walk.Speed : 0, seconds, ahead);
        (uint Id, SpawnedMonster Monster)? target;
        var here = player.Walk.Position(now);
        var plan = FollowerPlans.GetOrCreateValue(player);
        double reach = gem.Name is null ? 0 : ControlledInPlaceReach(player, gem.Id, gem.Effect);
        target = player.Area.MonstersNear(leader.Walk.Position(now), 60).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                && mayAttack
                && !MonsterAsleep(player, p.Key)
                && Dist(p.Value.Walk.Position(now), leader.Walk.Position(now)) < 60
                && Dist(p.Value.Walk.Position(now), here) < 65
                && (!onTheMove || player.Walk.InReachOf(here, p.Value.Walk.Position(now), (int)reach, p.Value.StopRange - 2)))
            .OrderBy(p => p.Key == plan.Target ? 0 : 1).ThenBy(p => Dist(p.Value.Walk.Position(now), here))
            .Select(p => ((uint Id, SpawnedMonster Monster)?)(p.Key, p.Value)).FirstOrDefault();
        if (!InTown(player) && target is { } enemy && gem.Name is not null)
        {
            HoldControlledFollowerAttack(player, now, seconds, enemy.Id);
            var bytes = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(enemy.Id)
                .U16(gem.Id).U16(0xffff).U16(onTheMove ? (ushort)1 : (ushort)0).ToArray();
            await OnClientMessages([(InstanceTransfer.UseSkillOnObjectId, bytes)], [], player);
        }
        else if (!following) IdleControlledFollower(player, leader, now);
    }

    static double ControlledInPlaceReach(Player player, ushort skill, int effect)
    {
        if (SkillsFor(player).ActionHash(effect) == GroundSlamWave.Action)
            return GroundSlamWave.Length(SkillRadius(player, SupportedSkill(player, skill).Stats, spell: false, effect));
        if (SkillsFor(player).IsMelee(effect)) return WeaponFor(player).Range;
        if (SkillsFor(player).IsSelfTargeted(effect))
            return SkillRadius(player, SupportedSkill(player, skill).Stats, SkillsFor(player).IsSpell(effect), effect);
        return 65;
    }
}
