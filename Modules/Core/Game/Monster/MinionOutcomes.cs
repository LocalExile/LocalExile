using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Progression = GameServer.Core.World.Progression;

public static partial class Game
{
    // Call in the admitted contact transaction, before any network await. There is no deferred Game kill closure.
    static void CommitMinionHitLocked(Player player, uint id, SpawnedMonster monster, MonsterHit hit, int damage,
        double now, string by, StunHit? stun = null, SpawnedMonster? source = null, IReadOnlyDictionary<uint, int>? skill = null)
    {
        OwnedKillLocked(monster, hit.Life);
        CommitAcceptedHitLocked(player, id, monster, hit, damage, now, by, playerKill: false, stun: stun);
        if (hit.Life == 0 && source is not null) ResolveCursedMinionKillLocked(player, id, monster, source, skill, now);
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
        HitSummonsOnDeathLocked(player, victimId, monster, now);
        QueueEssenceDeathSpawnLocked(player, victimId, monster);
        var state = player.Minions;
        if (!state.OutputQueue.Any(o => o.Kind == PlayerMinions.OutputKind.Vaal))
            state.OutputQueue.Add(new([], "Vaal souls after minion kill", Kind: PlayerMinions.OutputKind.Vaal));
        if (!state.Rewarded.Add(monster) || HardcoreProgressBlocked(player) || monster.Variety is not { } variety || !monster.Hostile) return;
        var at = monster.Walk.Position(now);
        var partyShares = PartyExperienceShares(player, at);
        ShareKill(player, victimId, monster, variety, at, partyShares);
        SoulEaterSoul(player, victimId, at);
        var killer = PlayerOf(player); var modMonster = ModMonster(monster, at);
        // An admitted player kill retains its mod notification and charge event; owned kills do not acquire either.
        if (playerKill)
        {
            QueueMadnessExplosionLocked(player, victimId, monster);
            player.RecentEvents.RecordAmount(StatConditions.KilledEnemy, now);
            state.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.PlayerKill, Kill: modMonster));
            var charge = new PlayerMinions.AcceptedCharges(true, false, monster.Rarity, null, null, false, now,
                Frozen: monster.Ailments.FrozenAtDeath(now));
            if (chargeBatch is not null) chargeBatch.Add(charge);
            else CommitAcceptedChargeBatchLocked(player, [charge], CharacterPlayerStats(player));
        }
        var packets = new List<(byte[] Packet, string? Log)>();
        if (CommitQuestFlags(player, QuestsOf(player).OnMonsterKilled(player.Layout.Id, variety.Id)) is { } quest)
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
        uint xp = 0, gemXp = 0;
        if (!monster.NoRewards && player.PlayerLife > 0 && Dist(at, player.Walk.Position(now)) <= ExperienceRange
            && Monsters.BaseExperience(variety, monster.Level) is int baseXp)
            (xp, gemXp) = KillExperience(killer, modMonster, baseXp, variety.ExperienceMultiplier, monster.Rarity,
                player.Pools.Level, monster.Level, PartyExperienceShareOf(partyShares, player));
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
                CommitExperienceLevel(player, level);
                player.PlayerLife = LifeCap(player); player.PlayerMana = (uint)UnreservedMana(player);
                player.EnergyShield.SetMaximum(player.Pools.EnergyShield, true); TrimLeechLocked(player);
                state.StatRevision++; state.AccountRevision++;
                state.OutputQueue.Add(new([], "0x0033 current passive skill tree (accepted level)", Kind: PlayerMinions.OutputKind.Passives));
            }
        }
        state.FlaskRewards.Add(new(monster.Rarity));
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        if (monster.NoRewards || BossMods.GivesNoItems(variety) || variety.Id == TutorialObjects.Zombie) return;
        if (!GameEvents.AllowsLoot(player)) return;
        var balance = LeagueFor(player).Rules;
        var modded = GameEvents.RunDrops(PlayerOf(killer), modMonster);
        var drops = new List<ItemDrops.Drop>();
        if (modded is not { Cancel: true })
        {
            int mapTier = MapRunOf(player)?.Area.Tier ?? 0;
            drops.AddRange(DropsFor(player).Roll(monster.Level, monster.Rarity, AreaRng(player), IncreasedItemRarity(player, monster),
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * ItemQuantityFactor(player, monster),
                balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1), mapTier, MapModsOf(player)));
            drops.AddRange(PartyCurrency(player, drops, AreaRng(player)));
            if (DropsFor(player).Map(monster.Level, monster.Rarity, mapTier, AreaRng(player), chanceFactor: modded?.MapRelated ?? 1) is { } map) drops.Add(map);
            if (DropsFor(player).Gold(monster.Level, monster.Rarity, AreaRng(player),
                balance.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier * (modded?.Gold ?? 1)) is { } gold) drops.Add(gold);
        }
        if (modded is not { Cancel: true }) drops.AddRange(Sirus.Rewards(player, monster));
        drops = ModdedDrops(player, modded, drops, monster.Level, monster.Rarity, IncreasedItemRarity(player, monster),
            balance.EquipmentRarityMultiplier, AreaRng(player));
        drops.AddRange(MadeDrops(player, modded));
        modded?.ApplyTransforms(drops);
        if (MapBeyond.CorruptsDrops(player, variety?.Id)) CorruptDrops(drops);
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            double angle = AreaRng(player).NextDouble() * Math.Tau, radius = 3 + AreaRng(player).NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * radius), y = at.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (player.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
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
                await SendWorldRemoval(player, output.WorldId ?? output.TargetId!.Value, output.Log, atGate: Project);
            else if (output.Kind is PlayerMinions.OutputKind.GolemAction or PlayerMinions.OutputKind.EnergyShield)
                await SendWorldRecords(player, output.TargetId!.Value,
                    () => Project() is null ? null : actionRecords, output.Log);
            else if (worldId is uint targetId)
                await SendWorldRecord(player, targetId, [], output.Log, atGate: Project);
            else await Send(player, [], output.Log, atGate: Project);

        }
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
                var changed = new List<InventoryItem>();
                foreach (var flask in items.Items(PlayerInventories.Flask))
                {
                    if (!CanGainFlaskCharges(player, flask) || ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type
                        || ItemsFor(player).FlaskChargesOf(type) is not { } basis) continue;
                    var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods); var charges = FlaskCharges.Of(basis, local);
                    if (flask.Item.Charges >= charges.MaxCharges) continue;
                    double gain = reward.CritGains is { } gains ? gains.GetValueOrDefault(flask.Id) : FlaskCharges.FullShortcut ? int.MaxValue : FlaskCharges.KillCharges(reward.Rarity, ItemsFor(player).Mods);
                    int whole = player.FlaskChargeCarry.Take(flask.Id, gain * FlaskCharges.GainMultiplier(local, Get));
                    if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, item => Flasks.Gain(item, charges, whole)) is { } done) changed.Add(done);
                }
                return ([.. changed.Select(i => i.Id)], changed);
            }, _ => "0x00bf flask charges (accepted minion kill)", retainOutput: true);
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
        await SynchronizeAccountLevel(player);
        if (Current() && !player.Closed && !player.LeavingArea) state.PresentedAccountRevision = Math.Max(state.PresentedAccountRevision, account);
    }
}
