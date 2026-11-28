using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static void CommitBossStage(Player player, uint id, SpawnedMonster monster, double at)
    {
        var session = ProjectilesFor(player);
        CommitProjectileBossStage(session, id, monster, Math.Max(session.Runtime.Now, ProjectileRuntime.Time(at)));
    }

    // Called synchronously under XP -> combat lock. The outbox contains notifications of committed results.
    public static void CommitProjectileHit(ProjectileSession session, ProjectileDamage contact, SpawnedMonster target,
        MonsterHit hit, int damage, ProjectileOffense shot, bool critical, StunHit stun)
    {
        var player = session.Player;
        double now = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
        if (shot.ActualCaster is null)
        {
            player.LastEnemyHitAt = now;
            // The "recently" stats (issue #504): a projectile hit is an enemy hit, and a critical one is a crit.
            player.RecentEvents.RecordHit(now, critical, attack: !shot.Spell && !shot.FromTrapOrMine && !shot.Mirage
                && (shot.Effect < 0 || SkillsFor(player).IsAttack(shot.Effect)));
        }
        uint id = contact.Target.Id;
        var random = contact.Projectile.ReactionRandomSource;
        if (hit.Life == 0)
            SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        // Native Actor TakeHit recovery (0x1d1), as the melee path sends: a rig-only clip (0x1b2) leaves the client
        // monster walking while the server's estimate has stopped (11-skills.md "Hit reaction").
        // Built in the send gate, which places a lockstep client's recovery in its frames (TakeHitPacket).
        if (HitReaction.TryFlinch(target, stun, now, random))
        {
            double until = target.FlinchUntil;
            float stunSeconds = MathF.Max(0.05f, (float)(until - now));
            session.World(id, () =>
            {
                double gateNow = AreaTime(player);
                if (target.Walk.OnFrames(gateNow)) target.Walk.Cancel(gateNow);
                if (player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame && target.FlinchUntil == until)
                    target.FlinchUntil = LockstepRecoveryEnds(frame, stunSeconds);
                return InstanceProtocol.ActorAnimation(id, stunSeconds, PlayerHitRecovery.TakeHitAnimation);
            }, GameServer.Core.Diagnostics.ServerTrace.Combat || GameServer.Core.Diagnostics.ServerTrace.Projectiles
                ? $"0x01d1 monster 0x{id:x} TakeHit recovery {stunSeconds:0.000} s (projectile {shot.Label})" : null);
        }
        // The life update follows its hit message in the attacker's turn (HitOrder); one with no hit message stays.
        (uint Key, ushort Hit, ushort Life) order = default;
        if (shot.HitPacket)
        {
            // S→C 0x1af before the life update, as the melee default attack sends it: an immediate package (kind 3) for the
            // shot's action with blood (0x0001), the generic particles (0x4000) of its largest damage type, the critical
            // bit when it crit, and the main-hand bit (0x0200: the weapon's own effect and stat selection), which GGG's
            // server sets on every bow hit (live capture 2026-10-29, Agents/docs/protocol/11-skills.md §17).
            // Derived byte 2 matches the retained projectile action in Actor +0x1e8..+0x1f0 after the root attack ends.
            var direction = contact.Projectile.Direction;
            byte code = HitCode(shot.Hit, damage, target.MaxLife, critical || hit.Life == 0);
            ushort flags = (ushort)(0x4000 | HitTypeFlag(code) | (shot.Spell ? 0 : 0x0200) | (critical ? 0x0020 : 0));
            byte kind = 0x03, angle = HitAngle((int)Math.Round(direction.X * 1000), (int)Math.Round(direction.Y * 1000));
            ConsumeNativeKnockbackLocked(target, ref flags, ref kind, ref angle, out ushort u3c, out ushort u3e);
            // The emitter is the attacker: the player, or his totem. Only the player's own hit goes into his turn of the frame
            // (HitOrder); a totem's stays at the frame's start, as monsters' and minions' hits do.
            uint attacker = contact.Projectile.Use.Emitter.Id;
            if (attacker == player.Id) order = HitOrder(player, player.Id);
            byte derived = shot.ActionSequence is not null && !shot.Spell ? (byte)2 : (byte)0;
            session.World(attacker, InstanceProtocol.ActionHit(attacker, shot.ActionSequence ?? 0xffff, id, skillIndex: derived,
                flags: flags, kind: kind, code: code, angle: angle, u3c: u3c, u3e: u3e,
                orderKey: order.Key, checkpoint: order.Hit));
        }
        if (GameServer.Core.Diagnostics.ServerTrace.Projectiles)
            Note($"projectile {shot.Label} {contact.Stage} t={now:0.000000} use={contact.Projectile.UseId} entity={contact.Projectile.Id}"
                + $" phase={contact.Phase} form={contact.Projectile.Form} at={contact.Position}"
                + $" target=0x{id:x} damage={damage} life={hit.Life}/{target.MaxLife}");
        if (critical && shot.ActualCaster is null) CommitProjectileCritFlasks(session, now, random);
        if (hit.Life == 0 && shot.FromTotem) OwnedKillLocked(target, 0);
        if (hit.Life == 0) CommitProjectileKill(session, id, target, contact.At, contact.Projectile.RewardRandomSource, playerKill: shot.ActualCaster is null, skillStats: shot.SkillStats);
        if (hit.StageStarts) CommitProjectileBossStage(session, id, target, contact.At);
        // Charges affect the next chronological hit/use, rather than waiting for the presentation ticker.
        var charges = ChargesOf(player);
        if (charges.Pending)
        {
            var final = CharacterPlayerStats(player);
            int Get(uint key) => final.GetValueOrDefault(key);
            var step = charges.Step(now, Get, () => WeaponProfileFor(player), player.PlayerLife > 0,
                player.Walk.Moving(now), InTown(player), random);
            if (ChargePresentationPending(charges)) session.PresentCharges();
            if (step.CountsChanged)
            {
                player.Pools = PoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed();
                session.Enqueue(() => SendChargeStats(player, "projectile hit"));
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
            ? SkillsFor(session.Player).MonsterContact(variety.Id, "Throw", 0xffff) : null));
        monster.LastTarget = null;
        monster.BusyUntil = recoveryAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageEffectAt = effectAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageActionStarted = true;
        if (!session.Player.Connection.LockstepClient) monster.Walk.Order(pos.X, pos.Y, now);
        ushort stageSeq = monster.NextSequence();
        session.World(id, () =>
        {
            double gateAt = session.Player.Connection.LockstepClient && session.Player.Connection.LockstepFrame is { } frame
                ? frame.Start : AreaTime(session.Player);
            if (session.Player.Connection.LockstepClient)
            {
                StepWalkers(session.Player, gateAt);
                pos = monster.Walk.Position(gateAt);
                monster.Walk.Cancel(gateAt);
            }
            return [.. InstanceProtocol.ActionStarted(id, pos.X, pos.Y, pos.X, pos.Y, StageSkillId, stageSeq, session.Player.MonsterOrderFlags), .. InstanceProtocol.ActionEnded(id)];
        }, $"monster 0x{id:x} starts stage t={now:0.000000}; effect={monster.StageEffectAt:0.000000}, recovery={monster.BusyUntil:0.000000}");
        session.Runtime.Schedule(effectAt, Publish);
        session.Runtime.Schedule(recoveryAt, Publish);
        void Publish()
        {
            double time = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            if (AdvanceMonsterStageRecords(session.Player, id, monster, time) is { } bytes)
                session.WorldRecords(id, () => bytes, $"monster 0x{id:x} stage {(monster.StageRunning ? "effect" : "complete")} t={time:0.000000}; life={monster.Life}");
        }
    }

    public static void CommitProjectileKill(ProjectileSession session, uint id, SpawnedMonster monster, long at, Random random,
        bool playerKill = true, IReadOnlyDictionary<uint, int>? skillStats = null)
    {
        var player = session.Player;
        if (player.Area.Owned) player.Area.Delta.DiscardActions(id);
        // The projectile's lethal hit froze this decision before the output queue can publish it.
        TinctureKillBonus? tinctureKill;
        bool destroyCorpse;
        tinctureKill = playerKill ? TakeTinctureKill(player, monster) : null;
        destroyCorpse = playerKill && TakeUnhingedHitKillLocked(player, monster);
        if (playerKill) QueueMadnessExplosionLocked(player, id, monster);
        if (!destroyCorpse || HardcoreProgressBlocked(player) || monster.Variety is null || !monster.Hostile)
            StopCorpseBlocking(player, id, monster);
        DetonateExplosiveFusesOnDeath(monster, session);
        QueueInfernalBlowDeath(monster, at / (double)ProjectileRuntime.QuantaPerSecond, session);
        QueueInfernalCryDeath(id, monster, at / (double)ProjectileRuntime.QuantaPerSecond, session);
        SpreadPestilentStrike(player, id, monster, at / (double)ProjectileRuntime.QuantaPerSecond);
        // A soul only moves on some kills: an unchanged balance must not take a gate turn through the presentation.
        if (VaalSoulsUnpresented(player))
            session.World(player.Id, () => VaalSoulPresentation(player), "Vaal souls after projectile kill");
        double now = at / (double)ProjectileRuntime.QuantaPerSecond;
        if (playerKill && monster.Hostile)
        {
            IndexExistingHitMarksLocked(player);
            if (HitMarkedMonsters.TryGetValue(monster, out _))
                HitSummonsOnDeathLocked(player, id, monster, now);
        }
        TransferMarksOnDeathLocked(player, monster, now);
        var position = monster.Walk.Position(now);
        // Match ordinary kills: corpses stay until area teardown, and impales retain their own expiry/hit state.
        if (HardcoreProgressBlocked(player) || monster.Variety is not { } variety || !monster.Hostile) return;
        // Hit-kill corpse destruction (Axe Mastery, Kitava's Feast, Unhinged, Pyre, Cremator): remove the corpse before
        // death skills (e.g. Porcupine spines), corpse consumption, or targeting can observe it.
        bool corpseDestroyed = destroyCorpse && DestroyHitKillCorpseLocked(player, id, monster);
        if (corpseDestroyed)
            session.Enqueue(() => SendWorldRemoval(player, id,
                $"0x02bd corpse 0x{id:x} ({monster.Label}) destroyed on hit kill"));
        // Devour consumes the killing blow's corpse: its object leaves before the kill's loot and quest work.
        bool devoured = !corpseDestroyed && ConsumeDevouredCorpseLocked(player, id, monster, now);
        if (devoured)
            session.Enqueue(() => SendWorldRemoval(player, id,
                $"0x02bd corpse 0x{id:x} ({monster.Label}) devoured by the supported skill"));
        else if (!corpseDestroyed && ConsumeVoidSphereCorpseLocked(player, id, monster, now))
            session.WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
        // The mods (Program.Mods.cs) as in MonsterKilled: their modifiers here, inside the commit; the kill after it.
        var killer = PlayerOf(player);
        var modMonster = ModMonster(monster, position);
        var partyShares = PartyExperienceShares(player, position);
        ShareKill(player, id, monster, variety, position, partyShares);
        if (playerKill)
        {
            WitchbaneOnKill(player, monster, now);
            if (MapDeathMarkDuration(player, monster) is > 0 and var markMs)
                session.EnqueueLater(() => ApplyMapDeathMarkOnKill(player, markMs));
            // The kill's bookkeeping goes behind the presentation queued with it (EnqueueLater): queued in line, it ended
            // the projectile step's world group, and the first kill's life update went a frame before the others.
            if (GameEvents.MonsterKilled.Any)
                session.EnqueueLater(() => GameEvents.RunMonsterKilled(new(PlayerOf(killer), modMonster)));
            if (OnslaughtOnKill(player, now, monster.TauntedAtDeath))
                session.EnqueueLater(() => SendKillOnslaught(player));
            ChargesOnKill(player, monster, skillStats ?? TakeAcceptedKillSkill(player));
            if (RecoverOnKill(player, monster, tinctureKill) is int deathEpoch)
                session.EnqueueLater(() => PlayerDiedAtEpoch(player, "death from life lost on kill", deathEpoch));
            else if (player.PlayerLife > 0)
                GainPhasingOnKill(player);
            var explosionStats = PlayerStatValues(player);
            var explosionEquipment = CaptureKillEquipment(player, monster, position, now, explosionStats);
            if (MayExplodeOnKill(player, monster, explosionEquipment, explosionStats))
                session.EnqueueLater(async () => await ExplodeOnKill(player, id, monster, position, explosionEquipment));
        }
        SoulEaterSoul(player, id, position);
        MonsterKits.GrantKinDeathChargesLocked(player.Area, id, monster, now);
        session.EnqueueLater(() => MonsterKits.ExecuteDeathSkills(player, id, monster, position));
        bool firstBossKill = HasUnsetKillQuestFlag(player, variety.Id);
        if (CommitQuestFlags(player, QuestFlagsOnKill(player, variety.Id)) is { } quest)
            session.EnqueueLater(() => PublishQuestFlags(player, quest));
        var kill = new KillContext(player, monster, variety.Id, position) { PlayerKill = playerKill };
        foreach (var run in GameEvents.Kill.For(player)) run(kill);
        foreach (var send in kill.Afterwards) session.EnqueueLater(send);
        uint xp = 0, gemXp = 0;
        if (!MonsterGivesNoRewards(monster) && player.PlayerLife > 0 && Dist(position, player.Walk.Position(now)) <= ExperienceRange)
            (xp, gemXp) = ComputeKillExperience(player, monster, variety, modMonster, PartyExperienceShareOf(partyShares, player));
        session.AddGemExperience(gemXp);
        if (player.PlayerLife > 0 && QuestsOf(player).LevelAfterKill(player.Layout.Id, variety.Id) is int required and > 1
            && required <= CharacterStats.LevelStarts.Count)
            xp = (uint)Math.Max(xp, Math.Max(0L, (long)CharacterStats.LevelStarts[required - 1] - player.Character.Experience));
        if (xp > 0 && RecordExperienceGain(player, xp) is { } change)
        {
            int level = Progression.LevelFor(change.After, CharacterStats.LevelStarts);
            session.PlayerExperience(change.After);
            if (level != player.Pools.Level)
            {
                int before = CommitExperienceLevel(player, level);
                player.PlayerLife = LifeCap(player); player.PlayerMana = (uint)UnreservedMana(player);
                RefillPlayerEnergyShield(player);
                TrimLeechLocked(player);
                session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Build));
                session.Enqueue(() => SendPlayerMana(player, "projectile kill level"));
                session.Packet(PassivesMessage(player).Bytes);
                session.Enqueue(() => BroadcastLevelChange(player, level));
                session.Enqueue(() => GameEvents.RunLevelUp(player, before, level));
                // Account-level persistence is a projection of the committed XP ledger.
                session.Enqueue(async () => { await SynchronizeAccountLevel(player); });
            }
        }
        if (MonsterGrantsKillFlaskCharges(monster))
        {
            int flaskChargesPct = ActorScale.Saturate((long)(tinctureKill?.FlaskChargesPct ?? 0)
                + BleedingKillFlaskChargesPct(player, monster.BleedSourcesAtDeath?.Contains(player.Id) == true));
            CommitProjectileFlaskGain(session, (_, _) => FlaskCharges.FullShortcut ? int.MaxValue
                : FlaskCharges.KillCharges(monster.Rarity, ItemsFor(player).Mods, flaskChargesPct));
        }
        // The map overlay's "X monsters remain" (S→C 0x1a0): coalesced once per batch instead of once per kill.
        session.QueueRemainingMonsters();
        // Loot rolling is catalogue work (ItemDrops.Roll); doing it under the projectile runtime's Monsters lock held
        // that lock 100-200 ms on a pack kill, and the stat sends and input handlers wait for the same lock. The session's
        // output task runs after it, behind the hits queued with it (EnqueueLater).
        session.EnqueueLater(() =>
        {
            CommitProjectileLoot(session, monster, position, firstBossKill);
            return Task.CompletedTask;
        });
    }

    static void CommitProjectileLoot(ProjectileSession session, SpawnedMonster monster, (int X, int Y) position, bool firstBossKill = false)
    {
        var player = session.Player;
        if (!session.Active) return;
        var random = AreaRng(player);
        var stacked = RollKillLoot(player, monster, position, random, firstBossKill);
        if (stacked.Count == 0) return;
        foreach (var drop in stacked)
        {
            var (x, y) = ScatterDropPosition(player, position, random);
            AddProjectileGroundItem(session, new(x, y, drop.Item));
        }
    }

    public static void AddProjectileGroundItem(ProjectileSession session, WorldItemObject ground) =>
        PublishProjectileGroundItem(session, session.Player.Instance.Ground.Add(ground, session.Player), ground);

    // One admitted create and wake, projected at the gate from the item as it lies there. A pickup that wins the race
    // leaves no item, so the create is suppressed instead of a removal reaching an object the client never saw; the
    // claim on SeenGround keeps the 100 ms ground sync from removing that unseen ID and from creating the drop again.
    public static void PublishProjectileGroundItem(ProjectileSession session, uint id, WorldItemObject ground) =>
        session.Create(id, () => GroundItemPresentation(session.Player, id, justDropped: true, expected: ground, includeWake: false),
            wake: true);

    sealed class FlaskLocalEntry(ModCatalog mods, IReadOnlyDictionary<uint, int> local)
    {
        public readonly ModCatalog Mods = mods;
        public readonly IReadOnlyDictionary<uint, int> Local = local;
    }

    static readonly IReadOnlyDictionary<uint, int> EmptyFlaskLocalStats = new Dictionary<uint, int>();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<ItemMod>, FlaskLocalEntry> FlaskLocalCache = new();

    static IReadOnlyDictionary<uint, int> FlaskLocalStats(Player player, Item item)
    {
        if (item.Mods is not { Count: > 0 } itemMods) return EmptyFlaskLocalStats;
        var catalog = ItemsFor(player).Mods;
        if (FlaskLocalCache.TryGetValue(itemMods, out var cached) && ReferenceEquals(cached.Mods, catalog))
            return cached.Local;
        var local = ItemModStats.Local(item, catalog);
        FlaskLocalCache.AddOrUpdate(itemMods, new(catalog, local));
        return local;
    }

    public static void CommitProjectileFlaskGain(ProjectileSession session,
        Func<InventoryItem, IReadOnlyDictionary<uint, int>, double> gain)
    {
        var player = session.Player;
        if (HardcoreProgressBlocked(player)) return;
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        List<(InventoryItem Flask, IReadOnlyDictionary<uint, int> Local, Flasks.Flask Charges, double Gain)>? gaining = null;
        foreach (var flask in items.Items(PlayerInventories.Flask))
        {
            if (catalog.ByHash(flask.Item.TypeHash) is not { } type || catalog.FlaskChargesOf(type) is not { } basis) continue;
            var local = FlaskLocalStats(player, flask.Item);
            var charges = FlaskCharges.Of(basis, local);
            if (flask.Item.Charges >= charges.MaxCharges) continue;
            double amount = gain(flask, local);
            if (amount > 0) (gaining ??= []).Add((flask, local, charges, amount));
        }
        if (gaining is null) return;
        int areaIncrease = MapFlaskChargeGain(player);
        var stats = PlayerStatValues(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        var changed = new List<InventoryItem>(gaining.Count);
        foreach (var (flask, local, charges, amount) in gaining)
        {
            if (!CanGainFlaskCharges(player, flask, local, stats)) continue;
            int whole = player.FlaskChargeCarry.Take(flask.Id, amount * FlaskGainMultiplier(player, flask.Id, local, Get, areaIncrease));
            if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, value => Flasks.Gain(value, charges, whole)) is { } item)
                changed.Add(item);
        }
        session.FlaskBarChanged(changed);
    }

    public static void CommitProjectileCritFlasks(ProjectileSession session, double at, Random random)
    {
        var player = session.Player;
        if (!player.FlaskChargeCarry.CritReady(at)) return;
        var catalog = ItemsFor(player);
        var flasks = ItemsOf(player).Items(PlayerInventories.Flask);
        var bar = new List<(uint Id, int Chance, bool Full)>(flasks.Count);
        bool anyNotFull = false;
        foreach (var f in flasks)
        {
            var local = FlaskLocalStats(player, f.Item);
            bool full = catalog.ByHash(f.Item.TypeHash) is not { } type
                || catalog.FlaskChargesOf(type) is not { } basis
                || f.Item.Charges >= FlaskCharges.Of(basis, local).MaxCharges;
            if (!full) anyNotFull = true;
            bar.Add((f.Id, local.GetValueOrDefault(Stat.LocalRechargeOnCritPct), full));
        }
        if (!anyNotFull) return;
        var stats = PlayerStatValues(player);
        bool precision = AffectingAuras(player).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, PlayerFlaskCritSources(player, stats, precision), player.FlaskChargeCarry, at, random.Next);
        if (gains.Count > 0) CommitProjectileFlaskGain(session, (f, _) => gains.GetValueOrDefault(f.Id));
    }
}
