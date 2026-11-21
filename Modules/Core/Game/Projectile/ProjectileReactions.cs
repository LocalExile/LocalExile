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
            player.RecentEvents.RecordHit(now, critical);
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
            session.World(id, () => TakeHitPacket(player, id, target, until, now));
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
            if (attacker == player.Id) order = HitOrder(player, player.Id);
            session.World(attacker, InstanceProtocol.ActionHit(attacker, shot.ActionSequence ?? 0xffff, id, flags: flags, kind: 0x03,
                code: HitCode(shot.Hit), angle: HitAngle((int)Math.Round(direction.X * 1000), (int)Math.Round(direction.Y * 1000)),
                orderKey: order.Key, checkpoint: order.Hit));
        }
        Note($"projectile {shot.Label} {contact.Stage} t={now:0.000000} use={contact.Projectile.UseId} entity={contact.Projectile.Id}"
            + $" phase={contact.Phase} form={contact.Projectile.Form} at={contact.Position}"
            + $" target=0x{id:x} damage={damage} life={hit.Life}/{target.MaxLife}");
        if (critical && shot.ActualCaster is null) CommitProjectileCritFlasks(session, now, random);
        if (hit.Life == 0) CommitProjectileKill(session, id, target, contact.At, contact.Projectile.RewardRandomSource, playerKill: shot.ActualCaster is null);
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
        monster.Walk.Order(pos.X, pos.Y, now); monster.LastTarget = null;
        monster.BusyUntil = recoveryAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageEffectAt = effectAt / (double)ProjectileRuntime.QuantaPerSecond;
        monster.StageActionStarted = true;
        session.World(id, InstanceProtocol.ActionStarted(id, pos.X, pos.Y, pos.X, pos.Y, StageSkillId, 0xffff, session.Player.MonsterOrderFlags),
            $"monster 0x{id:x} starts stage t={now:0.000000}; effect={monster.StageEffectAt:0.000000}, recovery={monster.BusyUntil:0.000000}");
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
        bool playerKill = true)
    {
        var player = session.Player;
        // The projectile's lethal hit froze this decision before the output queue can publish it.
        bool destroyCorpse;
        destroyCorpse = playerKill && TakeUnhingedHitKillLocked(player, monster);
        if (playerKill) QueueMadnessExplosionLocked(player, id, monster);
        StopCorpseBlocking(player, id, monster);
        session.World(player.Id, () => VaalSoulPresentation(player), "Vaal souls after projectile kill");
        double now = at / (double)ProjectileRuntime.QuantaPerSecond;
        var position = monster.Walk.Position(now);
        // Match ordinary kills: corpses stay until area teardown, and impales retain their own expiry/hit state.
        if (HardcoreProgressBlocked(player) || monster.Variety is not { } variety || !monster.Hostile) return;
        // Devour consumes the killing blow's corpse: its object leaves before the kill's loot and quest work.
        bool devoured;
        devoured = ConsumeDevouredCorpseLocked(player, id, monster, now);
        if (devoured)
            session.Enqueue(() => SendWorldRemoval(player, id,
                $"0x02bd corpse 0x{id:x} ({monster.Label}) devoured by the supported skill"));
        // The mods (Program.Mods.cs) as in MonsterKilled: their modifiers here, inside the commit; the kill after it.
        var killer = PlayerOf(player);
        var modMonster = ModMonster(monster, position);
        var partyShares = PartyExperienceShares(player, position);
        ShareKill(player, id, monster, variety, position, partyShares);
        if (playerKill)
        {
            // The kill's bookkeeping goes behind the presentation queued with it (EnqueueLater): queued in line, it ended
            // the projectile step's world group, and the first kill's life update went a frame before the others.
            session.EnqueueLater(() => GameEvents.RunMonsterKilled(new(PlayerOf(killer), modMonster)));
            if (OnslaughtOnKill(player)) session.EnqueueLater(() => SendKillOnslaught(player));
            ChargesOnKill(player, monster);
            RecoverOnKill(player, monster);
            GainPhasingOnKill(player);
            session.EnqueueLater(async () => await ExplodeOnKill(player, id, monster, position));
        }
        SoulEaterSoul(player, id, position);
        if (CommitQuestFlags(player, QuestsOf(player).OnMonsterKilled(player.Layout.Id, variety.Id)) is { } quest)
            session.EnqueueLater(() => PublishQuestFlags(player, quest));
        var kill = new KillContext(player, monster, variety.Id, position) { PlayerKill = playerKill };
        foreach (var run in GameEvents.Kill.For(player)) run(kill);
        foreach (var send in kill.Afterwards) session.EnqueueLater(send);
        uint xp = 0, gemXp = 0;
        if (!monster.NoRewards && player.PlayerLife > 0 && Dist(position, player.Walk.Position(at / (double)ProjectileRuntime.QuantaPerSecond)) <= ExperienceRange
            && Monsters.BaseExperience(variety, monster.Level) is int baseXp)
            (xp, gemXp) = KillExperience(killer, modMonster, baseXp, variety.ExperienceMultiplier, monster.Rarity,
                player.Pools.Level, monster.Level, PartyExperienceShareOf(partyShares, player));
        var (gemPackets, leveled) = CommitGemExperience(player, gemXp);
        foreach (var packet in gemPackets) session.Packet(packet);
        if (leveled) session.Enqueue(() => SendSkills(player));
        if (QuestsOf(player).LevelAfterKill(player.Layout.Id, variety.Id) is int required and > 1 && required <= CharacterStats.LevelStarts.Count)
            xp = (uint)Math.Max(xp, Math.Max(0L, (long)CharacterStats.LevelStarts[required - 1] - player.Character.Experience));
        if (xp > 0 && RecordExperienceGain(player, xp) is { } change)
        {
            int level = Progression.LevelFor(change.After, CharacterStats.LevelStarts);
            session.PlayerExperience(change.After);
            if (level != player.Pools.Level)
            {
                CommitExperienceLevel(player, level);
                player.PlayerLife = LifeCap(player); player.PlayerMana = (uint)UnreservedMana(player);
                player.EnergyShield.SetMaximum(player.Pools.EnergyShield, true);
                TrimLeechLocked(player);
                session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Build));
                session.Enqueue(() => SendPlayerMana(player, "projectile kill level"));
                session.Packet(PassivesMessage(player).Bytes);
                // Account-level persistence is a projection of the committed XP ledger.
                session.Enqueue(async () => { await SynchronizeAccountLevel(player); });
            }
        }
        CommitProjectileFlaskGain(session, (_, _) => FlaskCharges.FullShortcut ? int.MaxValue
            : FlaskCharges.KillCharges(monster.Rarity, ItemsFor(player).Mods));
        // The map overlay's "X monsters remain" (S→C 0x1a0): a projectile's kill is committed here, not through
        // Game.MonsterKilled (MonsterRemaining.cs).
        session.EnqueueLater(() => SendRemainingMonsters(player));
        // Loot rolling is catalogue work (ItemDrops.Roll); doing it under the projectile runtime's Monsters lock held
        // that lock 100-200 ms on a pack kill, and the stat sends and input handlers wait for the same lock. The session's
        // output task runs after it, behind the hits queued with it (EnqueueLater).
        session.EnqueueLater(() =>
        {
            CommitProjectileLoot(session, monster, position);
            return Task.CompletedTask;
        });
        session.Enqueue(() => PublishUnhingedCorpseDestruction(player, id, monster, destroyCorpse));
    }

    static void CommitProjectileLoot(ProjectileSession session, SpawnedMonster monster, (int X, int Y) position)
    {
        var player = session.Player;
        if (!session.Active || Environment.GetEnvironmentVariable("Game_DROPS") == "0" || monster.NoRewards
            || !GameEvents.AllowsLoot(player)) return;
        var random = AreaRng(player);
        var balance = LeagueFor(player).Rules;
        var modded = GameEvents.RunDrops(player, ModMonster(monster, position));
        List<ItemDrops.Drop> drops = [];
        if (modded is not { Cancel: true })
        {
            int mapTier = MapRunOf(player)?.Area.Tier ?? 0;
            drops.AddRange(DropsFor(player).Roll(monster.Level, monster.Rarity, random, IncreasedItemRarity(player, monster),
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * ItemQuantityFactor(player, monster), balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1), mapTier));
            if (DropsFor(player).Gold(monster.Level, monster.Rarity, random, balance.GoldMultiplier * (modded?.Gold ?? 1)) is { } gold) drops.Add(gold);
            drops.AddRange(ExtraDropsFromMap(player, monster.Level, monster.Rarity, random));
        }
        drops = ModdedDrops(player, modded, drops, monster.Level, monster.Rarity, IncreasedItemRarity(player, monster),
            balance.EquipmentRarityMultiplier, random);
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            double angle = random.NextDouble() * Math.Tau, radius = 3 + random.NextDouble() * 3;
            int x = position.X + (int)Math.Round(Math.Cos(angle) * radius), y = position.Y + (int)Math.Round(Math.Sin(angle) * radius);
            if (player.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = position;
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

    public static void CommitProjectileFlaskGain(ProjectileSession session,
        Func<InventoryItem, IReadOnlyDictionary<uint, int>, double> gain)
    {
        var player = session.Player;
        if (HardcoreProgressBlocked(player)) return;
        // Built only for a flask that can still gain: a full flask bar (the common case in a pack fight) needs no stats,
        // and every kill of an explosion asked for the full snapshot under the lock.
        Dictionary<uint, int>? stats = null;
        int Get(uint key) => (stats ??= new Dictionary<uint, int>(CharacterPlayerStats(player))).GetValueOrDefault(key);
        var items = ItemsOf(player);
        var changed = new List<InventoryItem>();
        foreach (var flask in items.Items(PlayerInventories.Flask))
        {
            if (!CanGainFlaskCharges(player, flask)) continue;
            if (ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(player).FlaskChargesOf(type) is not { } basis) continue;
            var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods); var charges = FlaskCharges.Of(basis, local);
            if (flask.Item.Charges >= charges.MaxCharges) continue;
            int whole = player.FlaskChargeCarry.Take(flask.Id, gain(flask, local) * FlaskCharges.GainMultiplier(local, Get));
            if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, value => Flasks.Gain(value, charges, whole)) is { } item)
                changed.Add(item);
        }
        if (changed.Count > 0)
            session.Packet(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [.. changed.Select(i => i.Id)], changed));
    }

    public static void CommitProjectileCritFlasks(ProjectileSession session, double at, Random random)
    {
        var player = session.Player;
        if (!player.FlaskChargeCarry.CritReady(at)) return;
        var bar = ItemsOf(player).Items(PlayerInventories.Flask).Select(f => (f.Id,
            Chance: ItemModStats.Local(f.Item, ItemsFor(player).Mods).GetValueOrDefault(Stat.LocalRechargeOnCritPct),
            Full: ItemsFor(player).ByHash(f.Item.TypeHash) is not { } type || ItemsFor(player).FlaskChargesOf(type) is not { } basis
                || f.Item.Charges >= FlaskCharges.Of(basis, ItemModStats.Local(f.Item, ItemsFor(player).Mods)).MaxCharges)).ToList();
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        bool precision = AffectingAuras(player).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), player.FlaskChargeCarry, at, random.Next);
        if (gains.Count > 0) CommitProjectileFlaskGain(session, (f, _) => gains.GetValueOrDefault(f.Id));
    }
}
