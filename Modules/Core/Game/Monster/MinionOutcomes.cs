using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using Progression = GameServer.Core.World.Progression;

public static partial class Game
{
    static readonly ConditionalWeakTable<Player, ConcurrentQueue<Func<Task>>> MinionKillAfterwards = new();

    // Call in the admitted contact transaction, before any network await. There is no deferred Game kill closure.
    static void CommitMinionHitLocked(Player player, uint id, SpawnedMonster monster, MonsterHit hit, int damage,
        double now, string by, StunHit? stun = null, SpawnedMonster? source = null, IReadOnlyDictionary<uint, int>? skill = null,
        GolemState.Golem? fighter = null, bool damageOverTime = false)
    {
        OwnedKillLocked(monster, hit.Life);
        if (monster.Hostile && fighter is not null && ReferenceEquals(source, fighter.Monster))
        {
            if (!damageOverTime) CurseCharges(player, monster, death: false, now, fighter);
            if (hit.Life == 0)
            {
                CurseCharges(player, monster, death: true, now, fighter);
                MinionOnslaughtOnKillLocked(player, fighter, now);
                MinionRecoversFromPoisonedKillLocked(player, fighter, monster, now);
                QueueGuardianWeaponOnKillLocked(player, fighter, monster.Walk.Position(now), now);
            }
        }
        if (!damageOverTime && monster.Hostile && source is not null)
            LeechFromMinionDamageLocked(player, monster, damage, now, fighter?.Player);
        CommitAcceptedHitLocked(player, id, monster, hit, damage, now, by, playerKill: false, stun: stun);
        if (!damageOverTime && monster.Hostile && fighter is not null && ReferenceEquals(source, fighter.Monster))
            ReleaseIntuitiveLinkLocked(player, fighter, monster.Walk.Position(now), now);
        if (hit.Life == 0 && source is not null) ResolveCursedMinionKillLocked(player, id, monster, source, skill, now);
        if (hit.Life == 0 && !damageOverTime && monster.Hostile && fighter is not null && ReferenceEquals(source, fighter.Monster)
            && skill is not null)
            MinionDevourKillingBlowLocked(player, fighter, id, monster, skill, now);
    }

    static void MinionRecoversFromPoisonedKillLocked(Player player, GolemState.Golem fighter, SpawnedMonster victim, double now)
    {
        var body = fighter.Monster;
        int percent = GolemStat(fighter.Player, Stat.MinionsRecoverPctMaximumLifeOnKillingPoisonedEnemy)
            + body.Effects[Stat.RecoverPctMaximumLifeOnKillingPoisonedEnemy];
        if (percent > 0 && victim.Poisons.Stacks(now) > 0)
            RecoverMinionLifeLocked(player, fighter, (long)body.MaxLife * percent / 100);
    }

    static void RecoverMinionLifeLocked(Player player, GolemState.Golem fighter, long life)
    {
        var body = fighter.Monster;
        if (life <= 0 || body.Dead || body.Life == 0 || body.Life >= body.MaxLife || !GolemCurrent(player, fighter)
            || LifeRecovery.NonLeechBlocked(key => body.Effects[key])) return;
        body.Life = (uint)Math.Min(body.MaxLife, body.Life + life);
        if (body.Life == body.MaxLife) MinionLifeLeeches.Remove(fighter);
        player.Minions.OutputQueue.Add(new([], null, fighter.Id, body, PlayerMinions.OutputKind.Life));
    }

    static void MinionOnslaughtOnKillLocked(Player player, GolemState.Golem fighter, double now)
    {
        int chance = fighter.Monster.Effects[Stat.ChanceToGainOnslaughtOnKillFor4SecondsPct];
        if (chance <= 0 || fighter.Monster.Dead || !GolemCurrent(player, fighter)
            || !GameServer.Core.World.RandomRolls.Chance(chance, 100, AreaRng(player))) return;
        GainMinionOnslaughtLocked(player, fighter, now, 4);
    }

    static void GainMinionOnslaughtLocked(Player player, GolemState.Golem fighter, double now, double seconds)
    {
        if (fighter.OnslaughtUntil >= now + seconds) return;
        fighter.OnslaughtUntil = now + seconds;
        if (ItemsFor(player).Onslaught is { } onslaught)
            player.Area.Buffs.Set(fighter.Id, new(onslaught.BuffRow, fighter.Id, U68: Buff.Seconds(seconds)));
    }

    static void CommitAcceptedHitLocked(Player player, uint id, SpawnedMonster monster, MonsterHit hit, int damage,
        double now, string by, bool playerKill, bool deferKill = false, StunHit? stun = null, bool damageOverTime = false)
    {
        var state = player.Minions;
        if (hit.Life == 0) SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        if (!damageOverTime && HitReaction.TryFlinch(monster, stun ?? new(damage), now, AreaRng(player)))
            state.OutputQueue.Add(new([], $"0x01d1 monster 0x{id:x} TakeHit recovery", id, monster,
                PlayerMinions.OutputKind.Flinch, monster.FlinchUntil, monster.ActionGeneration));
        state.OutputQueue.Add(new([], $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {hit.Life}/{monster.MaxLife}{(hit.Life == 0 ? " (dies)" : "")}",
            id, monster, PlayerMinions.OutputKind.Life));
        if (hit.Life == 0 && !deferKill) CommitAcceptedKillLocked(player, id, monster, now, playerKill);
    }

    public static void CommitAcceptedKillLocked(Player player, uint victimId, SpawnedMonster monster, double now, bool playerKill, List<PlayerMinions.AcceptedCharges>? chargeBatch = null)
    {
        if (monster.ShockedAtDeath is null) CaptureLootAilmentsAtDeath(monster, now);
        SpreadPestilentStrike(player, victimId, monster, now);
        RememberCursedCorpse(player, victimId, monster);
        QueueInfernalBlowDeath(monster, now);
        QueueInfernalCryDeath(victimId, monster, now);
        TransferMarksOnDeathLocked(player, monster, now);
        HitSummonsOnDeathLocked(player, victimId, monster, now);
        QueueEssenceDeathSpawnLocked(player, victimId, monster);
        var state = player.Minions;
        QueueVaalSoulPresentationLocked(player, "Vaal souls after minion kill");
        if (!state.Rewarded.Add(monster) || HardcoreProgressBlocked(player) || monster.Variety is not { } variety || !monster.Hostile) return;
        var at = monster.Walk.Position(now);
        var partyShares = PartyExperienceShares(player, at);
        ShareKill(player, victimId, monster, variety, at, partyShares);
        SoulEaterSoul(player, victimId, at);
        MonsterKits.GrantKinDeathChargesLocked(player.Area, victimId, monster, now);
        ProjectilesFor(player).EnqueueLater(() => MonsterKits.ExecuteDeathSkills(player, victimId, monster, at));
        var killer = PlayerOf(player); var modMonster = ModMonster(monster, at);
        // An admitted player kill retains its mod notification and charge event; owned kills do not acquire either.
        if (playerKill)
        {
            WitchbaneOnKill(player, monster, now);
            QueueMadnessExplosionLocked(player, victimId, monster);
            RecordPlayerMonsterKill(player, monster, now);
            SpiritChargeOnKill(player, now);
            state.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.PlayerKill, Kill: modMonster));
            var charge = new PlayerMinions.AcceptedCharges(true, false, monster.Rarity, null, null, false, now,
                Frozen: monster.Ailments.FrozenAtDeath(now));
            if (chargeBatch is not null) chargeBatch.Add(charge);
            else CommitAcceptedChargeBatchLocked(player, [charge], CharacterPlayerStats(player));
            if (OnslaughtOnKill(player, now, monster.TauntedAtDeath))
                ProjectilesFor(player).EnqueueLater(() => SendKillOnslaught(player));
            if (RecoverOnKill(player, monster, tincture: null) is int deathEpoch)
                ProjectilesFor(player).EnqueueLater(() => PlayerDiedAtEpoch(player, "death from life lost on kill", deathEpoch));
            var explosionEquipment = CaptureKillEquipment(player, monster, at, now);
            ProjectilesFor(player).EnqueueLater(async () => await ExplodeOnKill(player, victimId, monster, at, explosionEquipment));
        }
        var packets = new List<(byte[] Packet, string? Log)>();
        bool firstBossKill = HasUnsetKillQuestFlag(player, variety.Id);
        if (CommitQuestFlags(player, QuestFlagsOnKill(player, variety.Id)) is { } quest)
        {
            if (player.Connection.PlayerSent) state.OutputQueue.Add(new([], "0x02bc player quest flags (accepted kill)", Kind: PlayerMinions.OutputKind.QuestFlags));
            foreach (var (id, transition) in player.Layout.AreaTransitions)
            {
                if (TransitionFlag(player, transition) is not { } flag || !quest.Effective.Contains(flag)) continue;
                state.OutputQueue.Add(new([], "quest area transition (current eligibility)", TargetId: id,
                    Kind: PlayerMinions.OutputKind.QuestTransition, Transition: transition));
            }
            foreach (uint id in quest.Removed) state.OutputQueue.Add(new([], "quest room object removed", TargetId: id, Kind: PlayerMinions.OutputKind.QuestRoomRemove));
            state.StatRevision++;
        }
        var kill = new KillContext(player, monster, variety.Id, at) { PlayerKill = playerKill, CommittedOutput = packets };
        foreach (var run in GameEvents.Kill.For(player)) run(kill);
        foreach (var (packet, log) in packets) state.OutputQueue.Add(new(packet, log));
        if (kill.Afterwards.Count > 0)
        {
            var afterwards = MinionKillAfterwards.GetOrCreateValue(player);
            foreach (var send in kill.Afterwards) afterwards.Enqueue(send);
        }
        uint xp = 0, gemXp = 0;
        if (!MonsterGivesNoRewards(monster) && player.PlayerLife > 0 && Dist(at, player.Walk.Position(now)) <= ExperienceRange)
            (xp, gemXp) = ComputeKillExperience(player, monster, variety, modMonster, PartyExperienceShareOf(partyShares, player));
        var gemRows = new List<(byte Row, uint[] Items)>();
        var (_, leveled) = CommitGemExperience(player, gemXp, gemRows);
        foreach (var (row, ids) in gemRows)
            state.OutputQueue.Add(new([], "gem experience (current)", Kind: PlayerMinions.OutputKind.GemInventory, InventoryRow: row, ItemIds: ids));
        if (leveled) state.StatRevision++;
        if (QuestsOf(player).LevelAfterKill(player.Layout.Id, variety.Id) is int required and > 1 && required <= CharacterStats.LevelStarts.Count)
            xp = (uint)Math.Max(xp, Math.Max(0L, (long)CharacterStats.LevelStarts[required - 1] - player.Character.Experience));
        if (xp > 0 && RecordExperienceGain(player, xp) is { } change)
        {
            state.OutputQueue.Add(new([], $"0x02bc current player experience ({monster.Rarity} {monster.Label} 0x{victimId:x})", Kind: PlayerMinions.OutputKind.Experience));
            int level = Progression.LevelFor(change.After, CharacterStats.LevelStarts);
            if (level != player.Pools.Level)
            {
                int before = CommitExperienceLevel(player, level);
                state.PendingLevelBefore ??= before;
                player.PlayerLife = LifeCap(player); player.PlayerMana = (uint)UnreservedMana(player);
                RefillPlayerEnergyShield(player); TrimLeechLocked(player);
                state.StatRevision++; state.AccountRevision++;
                state.OutputQueue.Add(new([], "0x0033 current passive skill tree (accepted level)", Kind: PlayerMinions.OutputKind.Passives));
            }
        }
        if (MonsterGrantsKillFlaskCharges(monster))
            state.FlaskRewards.Add(new(monster.Rarity)
            {
                KillChargesPct = BleedingKillFlaskChargesPct(player, monster.BleedSourcesAtDeath?.Contains(player.Id) == true),
            });
        var rng = AreaRng(player);
        var stacked = RollKillLoot(player, monster, at, rng, firstBossKill);
        if (stacked.Count == 0) return;
        foreach (var drop in stacked)
        {
            var (x, y) = ScatterDropPosition(player, at, rng);
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            state.OutputQueue.Add(new([], $"world item 0x{id:x} {drop.Type.Name} dropped by {monster.Label} at ({x}, {y})",
                TargetId: id, Kind: PlayerMinions.OutputKind.GroundItem));
        }
    }

    // Only the existing current minion role/caller drains. Selection, validation and consumption share the real send gate.
    public static Task PublishMinionOutcomes(Player player) =>
        PublishMinionOutcomesFor(player, CombatLifetime);

    static async Task PublishMinionOutcomesFor(Player player,
        IModuleLifetime? rules)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        if (Current()) await RunMinionDeathHits(player);
        var state = player.Minions;
        while (true)
        {
            PlayerMinions.Output output;
            if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0) break;
            output = state.OutputQueue[0];
            if (output.Kind == PlayerMinions.OutputKind.QuestKill)
            {
                if (player.Connection.LockstepClient)
                    await SendWorldBatch(player, [], admission =>
                    {
                        using var mutation = ProjectileMutation(player);
                        if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0
                            || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                        var packets = CommitQuestKillOperation(player, output, admission);
                        state.OutputQueue.RemoveAt(0);
                        return packets;
                    }, quest: true);
                else
                {
                    await player.Character.QuestGate.WaitAsync();
                    try
                    {
                        await Send(player, [], "accepted quest kill effects", atGate: () =>
                        {
                            using var mutation = ProjectileMutation(player);
                            if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0
                                || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                            var admission = new WorldAdmission(player.Connection.WorldOutput, []);
                            var packets = CommitQuestKillOperation(player, output, admission);
                            admission.Finish(true);
                            state.OutputQueue.RemoveAt(0);
                            return [.. packets.Records.SelectMany(r => r.CopyBytes())];
                        });
                    }
                    finally { player.Character.QuestGate.Release(); }
                }
                continue;
            }
            if (output.Kind == PlayerMinions.OutputKind.PlayerKill)
            {
                Task notification;
                if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0
                    || !ReferenceEquals(state.OutputQueue[0], output)) return;
                state.OutputQueue.RemoveAt(0);
                // Start exactly once while claimed by the current owner. No Game callback is stored in the receipt.
                notification = GameEvents.RunMonsterKilled(new(player, output.Kill!));
                await notification;
                // A minion's kill is committed here, not through Game.MonsterKilled: the overlay's count (S→C 0x1a0) is one
                // monster smaller too.
                await SendRemainingMonsters(player);
                continue;
            }
            if (output.Kind == PlayerMinions.OutputKind.GroundItem)
            {
                uint groundId = output.TargetId!.Value;
                await SendWorldCreate(player, groundId, () =>
                {
                    if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0
                        || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                    state.OutputQueue.RemoveAt(0);
                    return GroundItemPresentation(player, groundId, player.Instance.Ground.IsNewFor(player, groundId), includeWake: false);
                }, output.Log, wake: true);
                continue;
            }
            byte[][]? actionRecords = null;
            byte[]? Project()
            {
                if (!Current() || player.Closed || player.LeavingArea || state.OutputQueue.Count == 0
                    || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                state.OutputQueue.RemoveAt(0);
                if (output.RemovesObject)
                    return InstanceProtocol.RemoveObject(output.WorldId!.Value);
                if (output.Kind == PlayerMinions.OutputKind.Remove)
                    return output.TargetId is uint gone && !player.Area.Monsters.ContainsKey(gone) ? output.Packet : null;
                if (output.Kind == PlayerMinions.OutputKind.MonsterType)
                {
                    var type = output.MonsterType!.Value;
                    return player.MonsterTypes.Add((type.Row, type.Level)) ? InstanceEntities.MonsterTypes([type]) : null;
                }
                if (output.Kind == PlayerMinions.OutputKind.QuestFlags) return QuestFlagPresentation(player);
                if (output.Kind == PlayerMinions.OutputKind.QuestTransition)
                    return []; // The declared transition records are built in the same admission below.
                if (output.Kind == PlayerMinions.OutputKind.QuestRoomRemove)
                    return player.QuestRoomObjects.ContainsKey(output.TargetId!.Value) ? null : InstanceProtocol.RemoveObject(output.TargetId.Value);
                if (output.Kind == PlayerMinions.OutputKind.Passives) return PassivesMessage(player).Bytes;
                if (output.Kind == PlayerMinions.OutputKind.GemInventory)
                {
                    var items = ItemsOf(player); byte row = output.InventoryRow!.Value;
                    return InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), output.ItemIds!,
                        [.. items.Items(row).Where(item => output.ItemIds!.Contains(item.Id))]);
                }
                if (output.Kind == PlayerMinions.OutputKind.Charges) return AcceptedChargePresentationLocked(player, player.Connection.Remote);
                if (output.Kind == PlayerMinions.OutputKind.RighteousFireRing)
                    return RighteousFireRingCurrentLocked(player, output.RighteousFireRing!) ? output.Packet : null;
                if (output.Target is not null && output.TargetId is uint id && !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), output.Target)) return null;
                if (output.Kind == PlayerMinions.OutputKind.GolemAction)
                {
                    actionRecords = GolemActionRecords(player, output);
                    return actionRecords is null ? null : [.. actionRecords.SelectMany(p => p)];
                }
                if (output.Kind == PlayerMinions.OutputKind.GolemIncoming) return GolemIncomingPresentation(player, output);
                if (output.Kind == PlayerMinions.OutputKind.EnergyShield)
                {
                    actionRecords = GolemEnergyShieldRecords(player, output);
                    return actionRecords is null ? null : [.. actionRecords.SelectMany(p => p)];
                }
                if (output.Action != 0 && output.Target?.ActionGeneration != output.Action) return null;
                return output.Kind switch
                {
                    PlayerMinions.OutputKind.Life => output.Target!.Life == 0 && output.DeathKind != InstanceProtocol.DeathKindNormal
                        ? InstanceProtocol.LifeUpdate(output.TargetId!.Value, 0, u2c: output.DeathKind) : [],
                    PlayerMinions.OutputKind.Vaal => VaalSoulPresentation(player),
                    PlayerMinions.OutputKind.Experience => InstanceEntities.PlayerExperienceUpdate(player.Id, CurrentExperience(player)),
                    PlayerMinions.OutputKind.Flinch => output.Target is { Dead: false } m && m.ActionGeneration == output.Action
                        && output.Until > AreaTime(player) ? TakeHitPacket(player, output.TargetId!.Value, m, output.Until,
                            AreaTime(player)) : null,
                    PlayerMinions.OutputKind.Packet when output.Packet.Length == 0 && output.At is { } moveTo
                        && output.Target is { } mover && output.TargetId is uint moverId
                        => GolemMoveAtGate(player, moverId, mover, moveTo),
                    _ => output.Packet,
                };
            }
            uint? worldId = output.WorldId ?? (output.Kind switch
            {
                PlayerMinions.OutputKind.Life or PlayerMinions.OutputKind.Flinch or PlayerMinions.OutputKind.GolemIncoming => output.TargetId,
                PlayerMinions.OutputKind.Experience or PlayerMinions.OutputKind.Vaal or PlayerMinions.OutputKind.QuestFlags => player.Id,
                _ => null,
            });
            if (output.Kind == PlayerMinions.OutputKind.QuestTransition)
                await SendWorldBatch(player, [], admission => Project() is null ? null
                    : QuestTransitionRecords(player, admission, output.TargetId!.Value, output.Transition!));
            else if (output.CreatesObject)
                await SendWorldCreate(player, output.WorldId!.Value, Project, output.Log);
            else if (output.RemovesObject || output.Kind is PlayerMinions.OutputKind.Remove or PlayerMinions.OutputKind.QuestRoomRemove)
            {
                uint id = output.WorldId ?? output.TargetId!.Value;
                await SendWorldRemoval(player, id, output.Log, atGate: () =>
                {
                    var records = Project();
                    if (records is not null) player.UntrackProjectileObject(id);
                    return records;
                });
            }
            else if (output.Kind is PlayerMinions.OutputKind.GolemAction or PlayerMinions.OutputKind.EnergyShield)
                await SendWorldRecords(player, output.TargetId!.Value,
                    () => Project() is null ? null : actionRecords, output.Log);
            else if (worldId is uint targetId)
                await SendWorldRecord(player, targetId, [], output.Log, atGate: Project);
            else await Send(player, [], output.Log, atGate: Project);

        }
        while (Current() && !player.Closed && !player.LeavingArea
            && MinionKillAfterwards.TryGetValue(player, out var afterwards)
            && afterwards.TryDequeue(out var send))
            await send();
        while (true)
        {
            PlayerMinions.FlaskReward reward;
            if (!Current() || player.Closed || player.LeavingArea || state.FlaskRewards.Count == 0) break;
            if (HardcoreProgressBlocked(player)) { state.FlaskRewards.Clear(); break; }
            reward = state.FlaskRewards[0];
            await ChangeFlaskBar(player, items =>
            {
                if (player.Closed || player.LeavingArea || state.FlaskRewards.Count == 0 || !ReferenceEquals(state.FlaskRewards[0], reward)) return null;
                state.FlaskRewards.RemoveAt(0);
                var playerStats = CharacterPlayerStats(player);
                int Get(uint key) => playerStats.GetValueOrDefault(key);
                int areaIncrease = MapFlaskChargeGain(player);
                var changed = new List<InventoryItem>();
                foreach (var flask in items.Items(PlayerInventories.Flask))
                {
                    if (!CanGainFlaskCharges(player, flask) || ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type
                        || ItemsFor(player).FlaskChargesOf(type) is not { } basis) continue;
                    var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods); var charges = FlaskCharges.Of(basis, local);
                    if (flask.Item.Charges >= charges.MaxCharges) continue;
                    double gain = reward.CritGains is { } gains ? gains.GetValueOrDefault(flask.Id) : FlaskCharges.FullShortcut ? int.MaxValue : FlaskCharges.KillCharges(reward.Rarity, ItemsFor(player).Mods, reward.KillChargesPct);
                    int whole = player.FlaskChargeCarry.Take(flask.Id,
                        gain * FlaskGainMultiplier(player, flask.Id, local, Get, areaIncrease));
                    if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, item => Flasks.Gain(item, charges, whole)) is { } done) changed.Add(done);
                }
                return ([.. changed.Select(i => i.Id)], changed);
            }, _ => "0x00c6 flask charges (accepted minion kill)", retainOutput: true);
            if (!Current() || state.FlaskRewards.Count > 0 && ReferenceEquals(state.FlaskRewards[0], reward)) return;
        }
        long revision, account;
        if (!Current() || player.Closed || player.LeavingArea) return;
        revision = state.StatRevision; account = state.AccountRevision;
        if (revision != state.PresentedStatRevision)
        {
            await SendCharacterStats(player, PlayerStatChange.Minions);
            if (!Current()) return;
            await SendPlayerMana(player); await SendPlayerEnergyShield(player);
            if (Current() && !player.Closed && !player.LeavingArea) state.PresentedStatRevision = Math.Max(state.PresentedStatRevision, revision);
        }
        await SendSentinelOwnership(player, Current);
        if (!Current() || account == state.PresentedAccountRevision) return;
        if (!HardcoreProgressBlocked(player))
        {
            await BroadcastLevelChange(player, player.Pools.Level);
            if (state.PendingLevelBefore is int prev)
            {
                state.PendingLevelBefore = null;
                await GameEvents.RunLevelUp(player, prev, player.Pools.Level);
            }
        }
        if (!Current()) return;
        await SynchronizeAccountLevel(player);
        if (Current() && !player.Closed && !player.LeavingArea) state.PresentedAccountRevision = Math.Max(state.PresentedAccountRevision, account);
    }
}
