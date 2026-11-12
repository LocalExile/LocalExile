using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static void CommitBossStage(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster monster, double at)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        CommitProjectileBossStage(session, id, monster, Math.Max(session.Runtime.Now, ProjectileRuntime.Time(at)));
    }

    // Called synchronously under XP -> combat lock. The outbox contains notifications of committed results.
    public static void CommitProjectileHit(ProjectileSession session, ProjectileDamage contact, SpawnedMonster target,
        MonsterHit hit, int damage, ProjectileOffense shot, bool critical, StunHit stun)
    {
        var conn = session.Conn;
        double now = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
        if (shot.ActualCaster is null)
        {
            conn.LastEnemyHitAt = now;
            // The "recently" stats (issue #504): a projectile hit is an enemy hit, and a critical one is a crit.
            conn.RecentEvents.RecordHit(now, critical);
        }
        uint id = contact.Target.Id;
        var random = contact.Projectile.ReactionRandomSource;
        if (hit.Life == 0)
            session.World(id, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]));
        // Native Actor TakeHit recovery (0x1d1), as the melee path sends: a rig-only clip (0x1b2) leaves the client
        // monster walking while the server's estimate has stopped (11-skills.md "Hit reaction").
        // Built in the send gate, which places a lockstep client's recovery in its frames (TakeHitPacket).
        if (HitReaction.TryFlinch(target, stun, now, random))
        {
            double until = target.FlinchUntil;
            session.World(id, () => TakeHitPacket(conn, id, target, until, now));
        }
        // The life update follows its hit message in the attacker's turn (HitOrder); one with no hit message stays.
        (uint Key, ushort Hit, ushort Life) order = default;
        if (shot.HitPacket)
        {
            // S→C 0x1af before the life update, as the melee default attack sends it: an immediate package (kind 3) for the
            // shot's action with blood (0x0001), the generic particles (0x4000) of its largest damage type, the critical
            // bit when it crit, and the main-hand bit (0x0200: the weapon's own effect and stat selection), which GGG's
            // server sets on every bow hit (live capture 2026-10-29, Agents/docs/protocol/11-skills.md §17).
            // Live also sends skill index 2 and a filled object header; their meanings are unknown, so they stay zero.
            var direction = contact.Projectile.Direction;
            ushort flags = (ushort)(0x4201 | (critical ? 0x0020 : 0));
            // The emitter is the attacker: the player, or his totem. Only the player's own hit goes into his turn of the frame
            // (HitOrder); a totem's stays at the frame's start, as monsters' and minions' hits do.
            uint attacker = contact.Projectile.Use.Emitter.Id;
            if (attacker == PlayerId) order = HitOrder(conn, PlayerId);
            session.World(attacker, InstanceProtocol.ActionHit(attacker, shot.ActionSequence ?? 0xffff, id, flags: flags, kind: 0x03,
                code: HitCode(shot.Hit), angle: HitAngle((int)Math.Round(direction.X * 1000), (int)Math.Round(direction.Y * 1000)),
                orderKey: order.Key, checkpoint: order.Hit));
        }
        session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life, orderKey: order.Key, checkpoint: order.Life),
            $"projectile {shot.Label} {contact.Stage} t={now:0.000000} use={contact.Projectile.UseId} entity={contact.Projectile.Id}"
            + $" phase={contact.Phase} form={contact.Projectile.Form} at={contact.Position}"
            + $" target=0x{id:x} damage={damage} life={hit.Life}/{target.MaxLife}");
        if (critical && shot.ActualCaster is null) CommitProjectileCritFlasks(session, now, random);
        if (hit.Life == 0) CommitProjectileKill(session, id, target, contact.At, contact.Projectile.RewardRandomSource, playerKill: shot.ActualCaster is null);
        if (hit.StageStarts) CommitProjectileBossStage(session, id, target, contact.At);
        // Charges affect the next chronological hit/use, rather than waiting for the presentation ticker.
        var charges = ChargesOf(conn);
        if (charges.Pending)
        {
            var final = CharacterPlayerStats(conn);
            int Get(string name) => Stats.Key(name) is uint key ? final.GetValueOrDefault(key) : 0;
            var step = charges.Step(now, Get, () => WeaponProfileFor(conn), conn.PlayerLife > 0,
                conn.Walk.Moving(now), InTown(conn), random);
            if (ChargePresentationPending(charges)) session.PresentCharges();
            if (step.CountsChanged)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed();
                session.Enqueue(() => SendChargeStats(session.remote, session.stream, session.ciphers, conn, "projectile hit"));
            }
        }
    }

    public static void CommitProjectileBossStage(ProjectileSession session, uint id, SpawnedMonster monster, long at)
    {
        if (monster.StageActionStarted) return;
        var stage = monster.Stage!;
        double now = at / (double)ProjectileRuntime.QuantaPerSecond;
        var pos = monster.Walk.Position(now);
        long recoveryAt = at + ProjectileRuntime.Time(stage.CastSeconds);
        long effectAt = at + ProjectileRuntime.Time(stage.EffectSeconds(monster.Variety is { } variety
            ? SkillsFor(session.Conn).MonsterContact(variety.Id, "Throw", 0xffff) : null));
        monster.Walk.Order(pos.X, pos.Y, now); monster.LastTarget = null;
        monster.BusyUntil = recoveryAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageEffectAt = effectAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageActionStarted = true;
        session.World(id, InstanceProtocol.ActionStarted(id, pos.X, pos.Y, pos.X, pos.Y, StageSkillId, 0xffff, session.Conn.MonsterOrderFlags),
            $"monster 0x{id:x} starts stage t={now:0.000000}; effect={monster.StageEffectAt:0.000000}, recovery={monster.BusyUntil:0.000000}");
        session.Runtime.Schedule(effectAt, Publish);
        session.Runtime.Schedule(recoveryAt, Publish);
        void Publish()
        {
            double time = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            if (AdvanceMonsterStageRecords(session.Conn, id, monster, time) is { } bytes)
                session.WorldRecords(id, () => bytes, $"monster 0x{id:x} stage {(monster.StageRunning ? "effect" : "complete")} t={time:0.000000}; life={monster.Life}");
        }
    }

    public static void CommitProjectileKill(ProjectileSession session, uint id, SpawnedMonster monster, long at, Random random,
        bool playerKill = true)
    {
        var conn = session.Conn;
        // The projectile's lethal hit froze this decision before the output queue can publish it.
        bool destroyCorpse;
        lock (conn.Monsters)
        {
            destroyCorpse = playerKill && TakeUnhingedHitKillLocked(conn, monster);
            if (playerKill) QueueMadnessExplosionLocked(conn, id, monster);
        }
        StopCorpseBlocking(session.remote, session.stream, session.ciphers, conn, id, monster);
        session.World(PlayerId, () => VaalSoulPresentation(conn), "Vaal souls after projectile kill");
        var position = monster.Walk.Position(at / (double)ProjectileRuntime.QuantaPerSecond);
        // Match ordinary kills: corpses stay until area teardown, and impales retain their own expiry/hit state.
        if (HardcoreProgressBlocked(conn) || monster.Variety is not { } variety || !monster.Hostile) return;
        // The mods (Program.Mods.cs) as in MonsterKilled: their modifiers here, inside the commit; the kill after it.
        var killer = PlayerOf(conn);
        var modMonster = ModMonster(monster, position);
        var partyShares = PartyExperienceShares(conn, position);
        ShareKill(conn, id, monster, variety, position, partyShares);
        if (playerKill)
        {
            session.Enqueue(() => GameEvents.RunMonsterKilled(new(ConnOf(killer), modMonster)));
            if (OnslaughtOnKill(conn)) session.Enqueue(() => SendKillOnslaught(session.remote, session.stream, session.ciphers, conn));
            ChargesOnKill(conn, monster);
            RecoverOnKill(conn, monster);
            session.Enqueue(async () => await ExplodeOnKill(session.remote, session.stream, session.ciphers, conn, id, monster, position));
        }
        SoulEaterSoul(conn, id, position);
        if (CommitQuestFlags(conn, QuestsOf(conn).OnMonsterKilled(conn.Layout.Id, variety.Id)) is { } quest)
            session.Enqueue(() => PublishQuestFlags(session.remote, session.stream, session.ciphers, conn, quest));
        var kill = new KillContext(conn, monster, variety.Id, position) { PlayerKill = playerKill };
        foreach (var run in GameEvents.Kill.For(conn)) run(kill);
        foreach (var send in kill.Afterwards) session.Enqueue(send);
        uint xp = 0, gemXp = 0;
        if (!monster.NoRewards && conn.PlayerLife > 0 && Dist(position, conn.Walk.Position(at / (double)ProjectileRuntime.QuantaPerSecond)) <= ExperienceRange
            && Monsters.BaseExperience(variety, monster.Level) is int baseXp)
            (xp, gemXp) = KillExperience(killer, modMonster, baseXp, variety.ExperienceMultiplier, monster.Rarity,
                conn.Pools.Level, monster.Level, PartyExperienceShareOf(partyShares, conn));
        var (gemPackets, leveled) = CommitGemExperience(conn, gemXp);
        foreach (var packet in gemPackets) session.Packet(packet);
        if (leveled) session.Enqueue(() => SendSkills(session.remote, session.stream, session.ciphers, conn));
        if (QuestsOf(conn).LevelAfterKill(conn.Layout.Id, variety.Id) is int required and > 1 && required <= CharacterStats.LevelStarts.Count)
            xp = (uint)Math.Max(xp, Math.Max(0L, (long)CharacterStats.LevelStarts[required - 1] - conn.Experience));
        if (xp > 0 && RecordExperienceGain(conn, xp) is { } change)
        {
            int level = Progression.LevelFor(change.After, CharacterStats.LevelStarts);
            session.World(PlayerId, InstanceEntities.PlayerExperienceUpdate(PlayerId, change.After));
            if (level != conn.Pools.Level)
            {
                CommitExperienceLevel(conn, level);
                conn.PlayerLife = LifeCap(conn); conn.PlayerMana = (uint)UnreservedMana(conn);
                conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, true);
                TrimLeechLocked(conn);
                session.Enqueue(() => SendCharacterStats(session.remote, session.stream, session.ciphers, conn, "projectile kill level"));
                session.World(PlayerId, PlayerLifeUpdate(conn));
                session.Enqueue(() => SendPlayerMana(session.remote, session.stream, session.ciphers, conn, "projectile kill level"));
                session.Packet(PassivesMessage(conn).Bytes);
                // Account-level persistence is a projection of the committed XP ledger.
                session.Enqueue(async () => { await SynchronizeAccountLevel(conn); });
            }
        }
        CommitProjectileFlaskGain(session, (_, _) => FlaskCharges.FullShortcut ? int.MaxValue
            : FlaskCharges.KillCharges(monster.Rarity, ItemsFor(conn).Mods));
        // Loot rolling is catalogue work (ItemDrops.Roll); doing it under the projectile runtime's Monsters lock held
        // that lock 100-200 ms on a pack kill, and the stat sends and input handlers wait for the same lock. The session's
        // output task runs after it.
        session.Enqueue(() =>
        {
            CommitProjectileLoot(session, monster, position);
            return Task.CompletedTask;
        });
        session.Enqueue(() => PublishUnhingedCorpseDestruction(session.remote, session.stream, session.ciphers,
            conn, id, monster, destroyCorpse));
    }

    static void CommitProjectileLoot(ProjectileSession session, SpawnedMonster monster, (int X, int Y) position)
    {
        var conn = session.Conn;
        if (!session.Active || Environment.GetEnvironmentVariable("Game_DROPS") == "0" || monster.NoRewards
            || !GameEvents.AllowsLoot(conn)) return;
        var random = Random.Shared;
        var balance = LeagueFor(conn).Rules;
        var modded = GameEvents.RunDrops(conn, ModMonster(monster, position));
        List<ItemDrops.Drop> drops = [];
        if (modded is not { Cancel: true })
        {
            int mapTier = MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash
                ? openMap.Area.Tier : 0;
            drops.AddRange(DropsFor(conn).Roll(monster.Level, monster.Rarity, random, IncreasedItemRarity(conn, monster),
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * ItemQuantityFactor(conn, monster), balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1), mapTier));
            if (DropsFor(conn).Gold(monster.Level, monster.Rarity, random, balance.GoldMultiplier * (modded?.Gold ?? 1)) is { } gold) drops.Add(gold);
            drops.AddRange(ExtraDropsFromMap(conn, monster.Level, monster.Rarity, random));
        }
        foreach (string query in modded?.Items ?? [])
            if (ModDrop(conn, query, monster.Level) is { } extra) drops.Add(extra);
            else Console.WriteLine($"[mods] a mod's drop \"{query}\" is no item type or unique the server can create");
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            double angle = random.NextDouble() * Math.Tau, radius = 3 + random.NextDouble() * 3;
            int x = position.X + (int)Math.Round(Math.Cos(angle) * radius), y = position.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = position;
            AddProjectileGroundItem(session, new(x, y, drop.Item));
        }
    }

    public static void AddProjectileGroundItem(ProjectileSession session, WorldItemObject ground) =>
        PublishProjectileGroundItem(session, session.Conn.Instance.Ground.Add(ground, session.Conn), ground);

    public static void PublishProjectileGroundItem(ProjectileSession session, uint id, WorldItemObject ground)
    {
        session.Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob(justDropped: true)));
        session.World(id, InstanceProtocol.EnterWakeZone(id));
    }

    public static void CommitProjectileFlaskGain(ProjectileSession session,
        Func<InventoryItem, IReadOnlyDictionary<string, int>, double> gain)
    {
        var conn = session.Conn;
        if (HardcoreProgressBlocked(conn)) return;
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var items = ItemsOf(conn);
        var changed = new List<InventoryItem>();
        foreach (var flask in items.Items(PlayerInventories.Flask))
        {
            if (!CanGainFlaskCharges(conn, flask)) continue;
            if (ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(conn).FlaskChargesOf(type) is not { } basis) continue;
            var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods); var charges = FlaskCharges.Of(basis, local);
            if (flask.Item.Charges >= charges.MaxCharges) continue;
            int whole = conn.FlaskChargeCarry.Take(flask.Id, gain(flask, local) * FlaskCharges.GainMultiplier(local, Get));
            if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, value => Flasks.Gain(value, charges, whole)) is { } item)
                changed.Add(item);
        }
        if (changed.Count > 0)
            session.Packet(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [.. changed.Select(i => i.Id)], changed));
    }

    public static void CommitProjectileCritFlasks(ProjectileSession session, double at, Random random)
    {
        var conn = session.Conn;
        if (!conn.FlaskChargeCarry.CritReady(at)) return;
        var bar = ItemsOf(conn).Items(PlayerInventories.Flask).Select(f => (f.Id,
            Chance: ItemModStats.Local(f.Item, ItemsFor(conn).Mods).GetValueOrDefault("local_recharge_on_crit_%"),
            Full: ItemsFor(conn).ByHash(f.Item.TypeHash) is not { } type || ItemsFor(conn).FlaskChargesOf(type) is not { } basis
                || f.Item.Charges >= FlaskCharges.Of(basis, ItemModStats.Local(f.Item, ItemsFor(conn).Mods)).MaxCharges)).ToList();
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        bool precision = AffectingAuras(conn).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), conn.FlaskChargeCarry, at, random.Next);
        if (gains.Count > 0) CommitProjectileFlaskGain(session, (f, _) => gains.GetValueOrDefault(f.Id));
    }
}
