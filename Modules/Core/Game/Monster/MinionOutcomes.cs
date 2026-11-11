using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Progression = GameServer.Core.World.Progression;

static partial class Game
{
    // Call in the admitted contact transaction, before any network await. There is no deferred Game kill closure.
    static void CommitMinionHitLocked(ClientState conn, uint id, SpawnedMonster monster, MonsterHit hit, int damage,
        double now, string by) => CommitAcceptedHitLocked(conn, id, monster, hit, damage, now, by, playerKill: false);

    static void CommitAcceptedHitLocked(ClientState conn, uint id, SpawnedMonster monster, MonsterHit hit, int damage,
        double now, string by, bool playerKill, bool deferKill = false, StunHit? stun = null)
    {
        var state = conn.Minions;
        if (hit.Life == 0) state.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
            $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1", id, monster));
        if (HitReaction.TryFlinch(monster, stun ?? new(damage), now, Random.Shared))
            state.OutputQueue.Add(new([], $"0x01d1 monster 0x{id:x} TakeHit recovery", id, monster,
                PlayerMinions.OutputKind.Flinch, monster.FlinchUntil, monster.ActionGeneration));
        state.OutputQueue.Add(new([], $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {hit.Life}/{monster.MaxLife}{(hit.Life == 0 ? " (dies)" : "")}",
            id, monster, PlayerMinions.OutputKind.Life));
        if (hit.Life == 0 && !deferKill) CommitAcceptedKillLocked(conn, id, monster, now, playerKill);
    }

    static void CommitAcceptedKillLocked(ClientState conn, uint victimId, SpawnedMonster monster, double now, bool playerKill, List<PlayerMinions.AcceptedCharges>? chargeBatch = null)
    {
        var state = conn.Minions;
        if (!state.OutputQueue.Any(o => o.Kind == PlayerMinions.OutputKind.Vaal))
            state.OutputQueue.Add(new([], "Vaal souls after minion kill", Kind: PlayerMinions.OutputKind.Vaal));
        if (!state.Rewarded.Add(monster) || HardcoreProgressBlocked(conn) || monster.Variety is not { } variety || !monster.Hostile) return;
        var at = monster.Walk.Position(now);
        SoulEaterSoul(conn, victimId, at);
        var killer = PlayerOf(conn); var modMonster = ModMonster(monster, at);
        // An admitted player kill retains its mod notification and charge event; owned kills do not acquire either.
        if (playerKill)
        {
            conn.RecentEvents.RecordAmount(StatConditions.KilledEnemy, now);
            state.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.PlayerKill, Kill: modMonster));
            var charge = new PlayerMinions.AcceptedCharges(true, false, monster.Rarity, null, null, false, now,
                Frozen: monster.Ailments.FrozenAtDeath(now));
            if (chargeBatch is not null) chargeBatch.Add(charge);
            else CommitAcceptedChargeBatchLocked(conn, [charge], CharacterPlayerStats(conn));
        }
        var packets = new List<(byte[] Packet, string? Log)>();
        if (CommitQuestFlags(conn, QuestsOf(conn).OnMonsterKilled(conn.Layout.Id, variety.Id)) is { } quest)
        {
            if (conn.PlayerSent) state.OutputQueue.Add(new([], "0x02ba player quest flags (accepted kill)", Kind: PlayerMinions.OutputKind.QuestFlags));
            foreach (var (id, transition) in conn.Layout.AreaTransitions)
            {
                if (TransitionFlag(conn, transition) is not { } flag || !quest.Effective.Contains(flag)) continue;
                state.OutputQueue.Add(new([], "quest area transition (current eligibility)", TargetId: id,
                    Kind: PlayerMinions.OutputKind.QuestTransition, Transition: transition));
            }
            foreach (uint id in quest.Removed) state.OutputQueue.Add(new([], "quest room object removed", TargetId: id, Kind: PlayerMinions.OutputKind.QuestRoomRemove));
            state.StatRevision++;
        }
        var kill = new KillContext(conn, monster, variety.Id, at) { PlayerKill = playerKill, CommittedOutput = packets };
        foreach (var run in GameEvents.Kill.For(conn)) run(kill);
        foreach (var (packet, log) in packets) state.OutputQueue.Add(new(packet, log));
        uint xp = 0;
        if (conn.PlayerLife > 0 && Dist(at, conn.Walk.Position(now)) <= ExperienceRange
            && Monsters.BaseExperience(variety, monster.Level) is int baseXp)
            xp = Program.ServerHooks.Experience(killer, modMonster,
                Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, monster.Rarity, conn.Pools.Level, monster.Level));
        var gemRows = new List<(byte Row, uint[] Items)>();
        var (_, leveled) = CommitGemExperience(conn, xp, gemRows);
        foreach (var (row, ids) in gemRows)
            state.OutputQueue.Add(new([], "gem experience (current)", Kind: PlayerMinions.OutputKind.GemInventory, InventoryRow: row, ItemIds: ids));
        if (leveled) state.StatRevision++;
        if (QuestsOf(conn).LevelAfterKill(conn.Layout.Id, variety.Id) is int required and > 1 && required <= CharacterStats.LevelStarts.Count)
            xp = (uint)Math.Max(xp, Math.Max(0L, (long)CharacterStats.LevelStarts[required - 1] - conn.Experience));
        if (xp > 0 && RecordExperienceGain(conn, xp) is { } change)
        {
            state.OutputQueue.Add(new([], $"0x02ba current player experience ({monster.Rarity} {monster.Label} 0x{victimId:x})", Kind: PlayerMinions.OutputKind.Experience));
            int level = Progression.LevelFor(change.After, CharacterStats.LevelStarts);
            if (level != conn.Pools.Level)
            {
                CommitExperienceLevel(conn, level);
                conn.PlayerLife = LifeCap(conn); conn.PlayerMana = (uint)UnreservedMana(conn);
                conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, true); TrimLeechLocked(conn);
                state.StatRevision++; state.AccountRevision++;
                state.OutputQueue.Add(new([], "0x0033 current passive skill tree (accepted level)", Kind: PlayerMinions.OutputKind.Passives));
            }
        }
        state.FlaskRewards.Add(new(monster.Rarity));
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var balance = LeagueFor(conn).Rules;
        var modded = Program.ServerHooks.Drops(killer, modMonster);
        var drops = new List<ItemDrops.Drop>();
        if (modded is not { Cancel: true })
        {
            drops.AddRange(DropsFor(conn).Roll(monster.Level, monster.Rarity, Random.Shared, IncreasedItemRarity(conn),
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * ItemQuantityFactor(conn, monster),
                balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1)));
            drops.AddRange(PartyCurrency(conn, drops, Random.Shared));
            int mapTier = MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash ? openMap.Area.Tier : 0;
            if (DropsFor(conn).Map(monster.Level, monster.Rarity, mapTier, Random.Shared) is { } map) drops.Add(map);
            if (DropsFor(conn).Gold(monster.Level, monster.Rarity, Random.Shared,
                balance.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier * (modded?.Gold ?? 1)) is { } gold) drops.Add(gold);
        }
        foreach (string query in modded?.Items ?? [])
            if (ModDrop(conn, query, monster.Level) is { } extra) drops.Add(extra);
            else Console.WriteLine($"[mods] a mod's drop \"{query}\" is no item type or unique the server can create");
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            double angle = Random.Shared.NextDouble() * Math.Tau, radius = 3 + Random.Shared.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * radius), y = at.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            state.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
                $"0x02b9 create world item 0x{id:x} {drop.Type.Name} dropped by {monster.Label} at ({x}, {y})"));
            state.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null));
        }
    }

    // Only the existing current minion role/caller drains. Selection, validation and consumption share the real send gate.
    internal static Task PublishMinionOutcomes(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        PublishMinionOutcomesFor(remote, stream, ciphers, conn, CombatLifetime);

    static async Task PublishMinionOutcomesFor(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IModuleLifetime? rules)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        var state = conn.Minions;
        while (true)
        {
            PlayerMinions.Output output;
            lock (conn.Monsters)
            {
                if (!Current() || conn.Closed || conn.LeavingArea || state.OutputQueue.Count == 0) break;
                output = state.OutputQueue[0];
            }
            if (output.Kind == PlayerMinions.OutputKind.QuestKill)
            {
                await conn.QuestGate.WaitAsync();
                try
                {
                    await Send(remote, stream, ciphers, [], "accepted quest kill effects", atGate: () =>
                    {
                        using var mutation = ProjectileMutation(conn);
                        if (!Current() || conn.Closed || conn.LeavingArea || state.OutputQueue.Count == 0
                            || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                        var packets = CommitQuestKillOperation(remote, stream, ciphers, conn, output);
                        state.OutputQueue.RemoveAt(0);
                        return packets;
                    });
                }
                finally { conn.QuestGate.Release(); }
                continue;
            }
            if (output.Kind == PlayerMinions.OutputKind.PlayerKill)
            {
                Task notification;
                lock (conn.Monsters)
                {
                    if (!Current() || conn.Closed || conn.LeavingArea || state.OutputQueue.Count == 0
                        || !ReferenceEquals(state.OutputQueue[0], output)) return;
                    state.OutputQueue.RemoveAt(0);
                    // Start exactly once while claimed by the current owner. No Game callback is stored in the receipt.
                    notification = Program.ServerHooks.MonsterKilled(new(PlayerOf(conn), output.Kill!));
                }
                await notification;
                continue;
            }
            await Send(remote, stream, ciphers, [], output.Log, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (!Current() || conn.Closed || conn.LeavingArea || state.OutputQueue.Count == 0
                        || !ReferenceEquals(state.OutputQueue[0], output)) return null;
                    state.OutputQueue.RemoveAt(0);
                    if (output.Kind == PlayerMinions.OutputKind.Remove)
                        return output.TargetId is uint gone && !conn.Monsters.ContainsKey(gone) ? output.Packet : null;
                    if (output.Kind == PlayerMinions.OutputKind.MonsterType)
                    {
                        var type = output.MonsterType!.Value;
                        return conn.MonsterTypes.Add((type.Row, type.Level)) ? InstanceEntities.MonsterTypes([type]) : null;
                    }
                    if (output.Kind == PlayerMinions.OutputKind.QuestFlags) return QuestFlagPresentation(conn);
                    if (output.Kind == PlayerMinions.OutputKind.QuestTransition)
                        return QuestTransitionPresentation(conn, output.TargetId!.Value, output.Transition!);
                    if (output.Kind == PlayerMinions.OutputKind.QuestRoomRemove)
                        return conn.QuestRoomObjects.ContainsKey(output.TargetId!.Value) ? null : InstanceProtocol.RemoveObject(output.TargetId.Value);
                    if (output.Kind == PlayerMinions.OutputKind.Passives) return PassivesMessage(conn).Bytes;
                    if (output.Kind == PlayerMinions.OutputKind.GemInventory)
                    {
                        var items = ItemsOf(conn); byte row = output.InventoryRow!.Value;
                        return InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), output.ItemIds!,
                            [.. items.Items(row).Where(item => output.ItemIds!.Contains(item.Id))]);
                    }
                    if (output.Kind == PlayerMinions.OutputKind.Charges) return AcceptedChargePresentationLocked(conn, remote);
                    if (output.TargetId is uint id && !ReferenceEquals(conn.Monsters.GetValueOrDefault(id), output.Target)) return null;
                    if (output.Kind == PlayerMinions.OutputKind.GolemAction) return GolemActionPresentation(conn, output);
                    if (output.Kind == PlayerMinions.OutputKind.GolemIncoming) return GolemIncomingPresentation(conn, output);
                    if (output.Kind == PlayerMinions.OutputKind.EnergyShield) return GolemEnergyShieldPresentation(conn, output);
                    if (output.Action != 0 && output.Target?.ActionGeneration != output.Action) return null;
                    return output.Kind switch
                    {
                        PlayerMinions.OutputKind.Life => InstanceProtocol.LifeUpdate(output.TargetId!.Value, output.Target!.Life),
                        PlayerMinions.OutputKind.Vaal => VaalSoulPresentation(conn),
                        PlayerMinions.OutputKind.Experience => InstanceEntities.PlayerExperienceUpdate(PlayerId, CurrentExperience(conn)),
                        PlayerMinions.OutputKind.Flinch => output.Target is { Dead: false } m && m.ActionGeneration == output.Action
                            && output.Until > AreaTime(conn) ? TakeHitPacket(conn, output.TargetId!.Value, m, output.Until,
                                AreaTime(conn)) : null,
                        _ => output.Packet,
                    };
                }
            });
        }
        while (true)
        {
            PlayerMinions.FlaskReward reward;
            lock (conn.Monsters)
            {
                if (!Current() || conn.Closed || conn.LeavingArea || state.FlaskRewards.Count == 0) break;
                if (HardcoreProgressBlocked(conn)) { state.FlaskRewards.Clear(); break; }
                reward = state.FlaskRewards[0];
            }
            await ChangeFlaskBar(remote, stream, ciphers, conn, items =>
            {
                if (conn.Closed || conn.LeavingArea || state.FlaskRewards.Count == 0 || !ReferenceEquals(state.FlaskRewards[0], reward)) return null;
                state.FlaskRewards.RemoveAt(0);
                var player = CharacterPlayerStats(conn);
                int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
                var changed = new List<InventoryItem>();
                foreach (var flask in items.Items(PlayerInventories.Flask))
                {
                    if (!CanGainFlaskCharges(conn, flask) || ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type
                        || ItemsFor(conn).FlaskChargesOf(type) is not { } basis) continue;
                    var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods); var charges = FlaskCharges.Of(basis, local);
                    if (flask.Item.Charges >= charges.MaxCharges) continue;
                    double gain = reward.CritGains is { } gains ? gains.GetValueOrDefault(flask.Id) : FlaskCharges.FullShortcut ? int.MaxValue : FlaskCharges.KillCharges(reward.Rarity, ItemsFor(conn).Mods);
                    int whole = conn.FlaskChargeCarry.Take(flask.Id, gain * FlaskCharges.GainMultiplier(local, Get));
                    if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, item => Flasks.Gain(item, charges, whole)) is { } done) changed.Add(done);
                }
                return ([.. changed.Select(i => i.Id)], changed);
            }, _ => "0x00bf flask charges (accepted minion kill)", retainOutput: true);
            lock (conn.Monsters)
                if (!Current() || state.FlaskRewards.Count > 0 && ReferenceEquals(state.FlaskRewards[0], reward)) return;
        }
        long revision, account;
        lock (conn.Monsters)
        {
            if (!Current() || conn.Closed || conn.LeavingArea) return;
            revision = state.StatRevision; account = state.AccountRevision;
        }
        if (revision != state.PresentedStatRevision)
        {
            await SendCharacterStats(remote, stream, ciphers, conn, "accepted minion outcomes");
            if (!Current()) return;
            await SendPlayerMana(remote, stream, ciphers, conn); await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            await Send(remote, stream, ciphers, [], "player life after minion outcomes", atGate: () =>
            { lock (conn.Monsters) return Current() && !conn.Closed && !conn.LeavingArea ? PlayerLifeUpdate(conn) : null; });
            lock (conn.Monsters) if (Current() && !conn.Closed && !conn.LeavingArea) state.PresentedStatRevision = Math.Max(state.PresentedStatRevision, revision);
        }
        if (!Current() || account == state.PresentedAccountRevision) return;
        await SynchronizeAccountLevel(conn);
        lock (conn.Monsters) if (Current() && !conn.Closed && !conn.LeavingArea) state.PresentedAccountRevision = Math.Max(state.PresentedAccountRevision, account);
    }
}
