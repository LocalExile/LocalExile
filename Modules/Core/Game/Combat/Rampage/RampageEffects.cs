using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public sealed record RampageStunToken(uint Id, SpawnedMonster Target, int Stun, int Action,
        int IncomingEpoch, int ProjectileEpoch, long OwnedEpoch, double Until);

    public static RampageCatalog RampageData = null!;

    static bool RampageEffectCurrent(Player player, long epoch) => CurrentRampageWork
        && player.OwnedCombatState is RampageWorld world && world.Current(epoch)
        && (world.SourceOwner?.IsActive ?? true)
        && ReferenceEquals(world.SourceOwner, AscendancyPolicies.SourceOwner(player))
        && !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0 && RampageSource(player);

    static DamagePacket RampageSpellPacket(Player player, int effect, int level)
    {
        var owner = StatSourceValues(player);
        int increased = Stat.MinionDamagePlusPct is var key ? owner.GetValueOrDefault(key) : 0;
        // The daemon is a separate caster. Rampager's spell-skills-deal-no-damage and owner Rage do not apply.
        return SkillsFor(player).HitDamage(effect, level).Scale(Math.Max(0, 100L + increased) / 100.0);
    }

    static async Task<uint?> CreateRampageActor(Player player, RampageCatalog.VarietyPlan plan, int level, double lifetime,
        RampageCatalog.WeaponKind? weapon, (int X, int Y) at, long epoch)
    {
        if (Monsters.Find(plan.Row.ToString()) is not { Supported: true } variety) return null;
        uint id;
        id = player.TakeObjectId();
        byte actorLevel = (byte)Math.Clamp(level, 1, 100);
        uint life = (uint)Math.Max(1, Monsters.BaseLife(variety, actorLevel) ?? 1);
        int effect = plan.Skills[0].Row;
        var own = SkillsFor(player).EffectStats(effect, level).ToDictionary(s => s.Item1, s => s.Item2);
        var skill = SkillsFor(player).FindByRow(SkillCatalog.FirstSkillId, effect, level, own.Select(s => (s.Key, s.Value)).ToArray());
        if (skill is null) return null;
        var blob = new MonsterObject(at.X, at.Y, life, variety.ComponentsOrBase, StateMachine: variety.StateMachine,
            Team: 0, Skills: [skill], Stats: [(Stat.MaximumLife, (int)life)]) { LifeData = variety.LifeData, Chain = variety.Chain };
        await SendWorldBatch(player, [], admission =>
        {
            if (!RampageEffectCurrent(player, epoch)) return null;
            double now = ProjectileWorldTime(player);
            player.Area.Monsters[id] = new(variety.Name, player.NewMonsterWalk(id, at.X, at.Y, variety.MovementSpeed,
                Math.Clamp(variety.ObjectSize - 1, -1, 127), false), variety.AggroRange, variety.AttackRange, life)
            { OwnerId = player.Id, Hostile = false, Variety = variety, Level = actorLevel,
                HitAnimations = Monsters.TakeHit(variety.Id) };
            player.RampageWorld.Actors[id] = new(id, epoch, weapon, actorLevel, now + lifetime, now, effect);
            var bytes = new WorldRecordBuilder(player, admission);
            if (player.MonsterTypes.Add((variety.Row, actorLevel)))
                bytes.Immediate(InstanceEntities.MonsterTypes([((ushort)variety.Row, actorLevel)]));
            uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, actorLevel));
            bytes.Create(id, InstanceEntities.CreateObject(id, type, blob.Blob()));
            bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
            return bytes.Build();
        });
        return id;
    }

    static async Task StartRampageTier(Player player, RampageCatalog.Tier tier)
    {
        long epoch; int level; (int X, int Y) origin;
        epoch = player.RampageWorld.Epoch; level = player.Pools.Level;
        origin = player.Walk.Position(ProjectileWorldTime(player));
        if (!RampageEffectCurrent(player, epoch)) return;
        var own = tier.Skill.AtLevel(level)!;
        double cast = tier.Skill.CastTimeMs / 1000.0 / Math.Max(.01, 1 + own.Get(Stat.BaseCastSpeedPlusPct) / 100.0);
        var steps = RampageExecution.Steps(tier, level);
        double end = cast + steps.Max(s => s.Delay) + .5;
        uint? actor = await CreateRampageActor(player, tier.Daemon, level, end, null, origin, epoch);
        if (actor is null) throw new InvalidOperationException($"Rampage tier {tier.Threshold} cannot create its actor");
        await SendWorldRecord(player, actor.Value, InstanceProtocol.ActionStarted(actor.Value, origin.X, origin.Y,
            origin.X, origin.Y, SkillCatalog.FirstSkillId, 0xffff, player.MonsterOrderFlags), $"Rampage tier {tier.Threshold}",
            still: () => RampageEffectCurrent(player, epoch));
        if (RampageEffectCurrent(player, epoch))
            foreach (var step in steps)
                player.RampageWorld.Jobs.Add(new(ProjectileWorldTime(player) + cast + step.Delay,
                    epoch, actor.Value, tier, step, origin, level));
    }

    static async Task AdvanceRampageEffects(Player player)
    {
        int[] tiers; List<RampageWorld.Job> due; uint[] removals;
        if (!CurrentRampageWork || player.Paused) return;
        CharacterPlayerStats(player);
        var world = player.RampageWorld;
        double now = ProjectileWorldTime(player);
        tiers = world.Enabled ? player.Rampage.TakeTiers() : [];
        due = world.Jobs.Where(j => j.At <= now).OrderBy(j => j.At).ToList();
        foreach (var job in due) world.Jobs.Remove(job);
        foreach (var actor in world.Actors.Values.ToArray())
            if (actor.ExpiresAt <= now || !RampageEffectCurrent(player, actor.Epoch)
                || !player.Area.Monsters.TryGetValue(actor.Id, out var state) || state.Dead)
            { world.PendingRemoval.Add(actor.Id); world.Actors.Remove(actor.Id); }
        removals = world.PendingRemoval.ToArray();
        foreach (uint id in removals)
            if (player.Area.Monsters.Remove(id, out var dead)) dead.Dead = true;
        foreach (uint id in removals)
            await SendRampageRemoval(player, id);
        foreach (int threshold in tiers)
        {
            var tier = RampageData.Tiers.SingleOrDefault(t => t.Threshold == threshold)
                ?? throw new InvalidOperationException(RampageData.Error ?? "Missing Rampage tier");
            await StartRampageTier(player, tier);
        }
        foreach (var job in due)
        {
            if (!RampageEffectCurrent(player, job.Epoch)) continue;
            if (job.Step.Kind == RampageExecution.StepKind.Weapons)
            {
                foreach (var summon in job.Tier.Summons)
                    for (int i = 0; i < summon.Count; i++)
                        await CreateRampageActor(player, summon.Weapon.Variety, job.Level,
                            summon.DurationMs / 1000.0, summon.Weapon.Kind, job.Origin, job.Epoch);
            }
            else if (job.Step.Kind == RampageExecution.StepKind.Projectiles)
                ReleaseRampageProjectiles(player, job);
            else await RampagePulse(player, job);
        }
        await AdvanceRampageWeapons(player);
        await ExpireRampageBlinds(player);
    }

    static async Task RampagePulse(Player player, RampageWorld.Job job)
    {
        var own = job.Tier.Skill.AtLevel(job.Level)!;
        var origins = new List<((int X, int Y) Position, DamagePacket Damage)>();
        var corpses = new List<uint>();
        if (!RampageEffectCurrent(player, job.Epoch)) return;
        if (job.Step.Kind == RampageExecution.StepKind.Corpses)
        {
            foreach (var (corpseId, target) in player.Area.Monsters.Where(p => p.Value.Dead && p.Value.Hostile && p.Value.OwnerId is null)
                .OrderBy(p => Dist(p.Value.Walk.Position(job.At), job.Origin)))
                if (Dist(target.Walk.Position(job.At), job.Origin) <= 120
                    && player.RampageWorld.ConsumedCorpses.Add(target))
                {
                    double amount = target.MaxLife * own.Get(Stat.CorpseExplosionMonsterLifePct) / 100.0;
                    origins.Add((target.Walk.Position(job.At), DamagePacket.Of(DamageType.Fire, amount, amount)));
                    corpses.Add(corpseId);
                }
        }
        else origins.Add((job.Origin, RampageSpellPacket(player, job.Tier.Skill.Row, job.Level)));
        foreach (uint id in corpses)
        {
            if (!RampageEffectCurrent(player, job.Epoch)) return;
            player.Area.Monsters.Remove(id);
            player.RampageWorld.PendingRemoval.Add(id);
            await SendRampageRemoval(player, id);
        }
        foreach (var (origin, packet) in origins)
        {
            double radius = job.Step.Kind == RampageExecution.StepKind.Corpses ? RampageExecution.CorpseRadius
                : Math.Max(0, own.Get(Stat.ActiveSkillBaseAreaOfEffectRadius) is > 0 and var r ? r : RampageExecution.NearbyRadius)
                    * Math.Sqrt(Math.Max(0, 100 + own.Get(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal)) / 100.0)
                    * job.Step.RadiusPercent / 100;
            (uint Id, SpawnedMonster Monster)[] targets;
            targets = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead
                && p.Value.OwnerId is null && Dist(p.Value.Walk.Position(ProjectileWorldTime(player)), origin) <= radius)
                .Select(p => (p.Key, p.Value)).ToArray();
            foreach (var (id, target) in targets)
                await RampageOwnedHit(player, job.Epoch, id, target, packet, own,
                    job.Step.Kind == RampageExecution.StepKind.Corpses ? "Rampage corpse explosion" : "Rampage nova");
        }
    }

    static async Task<bool> RampageOwnedHit(Player player,
        long epoch, uint id, SpawnedMonster target, DamagePacket packet, RampageCatalog.SkillLevel? own, string label,
        bool attack = false, int accuracy = 0)
    {
        MonsterHit? hit; int damage; RampageStunToken? stun;
        using (ProjectileMutation(player))
        {
            if (!RampageEffectCurrent(player, epoch) || player.Paused || target.Dead || !target.Hostile
                || target.OwnerId is not null || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), target)) return false;
            if (attack && !RampageAttackHits(player, id, target, accuracy, ProjectileWorldTime(player), AreaRng(player))) return false;
            if (MonsterBlocks(target, AreaRng(player), !attack)) return false;
            bool critical = AreaRng(player).Next(10000) < (attack ? 500 : own?.SpellCriticalChance ?? 0)
                + BrittleCriticalChance(target, ProjectileWorldTime(player));
            (damage, _) = MonsterDamage(target, packet.Roll(AreaRng(player)), critical, attack, default);
            if (critical) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(target, 150, CharacterPlayerStats(player)));
            hit = ApplyMonsterHitLocked(player, target, damage, rampageSource: true); // Proven by this contact's effect guard.
            if (hit is null) return false;
            player.RecentEvents.RecordCriticalOutcome(ProjectileWorldTime(player), critical);
            if (hit.Value.StageStarts) CommitBossStage(player, id, target, ProjectileWorldTime(player));
            stun = RampageStatusLocked(player, id, target, own, ProjectileWorldTime(player));
        }
        Note($"{label} hit 0x{id:x} for {damage}, life {hit.Value.Life}/{target.MaxLife}");
        if (!CurrentRampageWork) return hit.Value.Life == 0;
        if (hit.Value.Life == 0)
        {
            await MonsterKilledBy(player, id, target, playerKill: false);
            if (own?.Get(Stat.ExplodeOnKillPctFireDamageToDeal) is int percent and > 0)
                await RampageKillExplosion(player, epoch, id, target, percent);
        }
        else if (stun is not null)
            await SendRampageStun(player, stun);
        return hit.Value.Life == 0;
    }

    /// <summary>The chance a Rampage attack has to hit this target, the value its hit roll is taken against. The
    /// critical strike's confirmation roll is taken against the same chance (Path of Building's
    /// <c>Modules/CalcOffence.lua:3058</c>).</summary>
    public static int RampageAttackHitChance(Player player, uint id, SpawnedMonster target, int accuracy, double now,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int evasion = target.Mods.Effects.Evasion(Monsters.BaseEvasion(target.Level) ?? 0);
        evasion = MonsterBlindRating(player, id, target, evasion, now, playerStats);
        return DamageModel.AccuracyHitChance(evasion, accuracy);
    }

    public static bool RampageAttackHits(Player player, uint id, SpawnedMonster target, int accuracy, double now, Random random,
        IReadOnlyDictionary<uint, int>? playerStats = null) =>
        DamageModel.Hits(RampageAttackHitChance(player, id, target, accuracy, now, playerStats), random.Next(100));

    static RampageStunToken? RampageStatusLocked(Player player, uint id, SpawnedMonster target,
        RampageCatalog.SkillLevel? own, double now)
    {
        if (own is null || target.Dead) return null;
        RampageStunToken? stun = null;
        if (own.Get(Stat.AlwaysStun) != 0 && !target.Mods.Effects.CannotBeStunned)
        {
            target.StunSerial++;
            target.ActionGeneration++;
            target.SwingGeneration = null;
            target.FlinchUntil = Math.Max(target.FlinchUntil,
                now + .35 * (1 + own.Get(Stat.BaseStunDurationPlusPct) / 100.0));
            target.BusyUntil = Math.Max(target.BusyUntil, target.FlinchUntil);
            if (!target.Walk.OnFrames(now)) target.Walk.Cancel(now);   // on frames with the 0x1d1 (TakeHitPacket)
            target.LastTarget = null;
            // Capture the accepted mutation before any LifeUpdate await or deferred projectile output.
            stun = new(id, target, target.StunSerial, target.ActionGeneration, player.IncomingHitEpoch,
                player.ProjectileEpoch, player.RampageWorld.Epoch, target.FlinchUntil);
        }
        if (own.Get(Stat.GlobalChanceToBlindOnHitPct) > 0 && target.Mods.Effects[Stat.CannotBeBlinded] == 0)
        {
            target.RampageBlindUntil = now + 4 * (1 + own.Get(Stat.BlindDurationPlusPct) / 100.0);
            ushort buff = player.RampageWorld.Blinds.TryGetValue(id, out var old) ? old.Buff : player.NextBuff++;
            player.RampageWorld.Blinds[id] = (target.RampageBlindUntil, buff);
        }
        return stun;
    }

    static async Task SendRampageStun(Player player,
        RampageStunToken token)
    {
        var target = token.Target;
        double Now() => Math.Max(ProjectileWorldTime(player), AreaTime(player));
        bool Current() => token.Stun > 0 && target.StunSerial == token.Stun && target.ActionGeneration == token.Action
            && player.IncomingHitEpoch == token.IncomingEpoch && player.ProjectileEpoch == token.ProjectileEpoch
            && target.FlinchUntil == token.Until && token.Until > Now() && !target.StageRunning
            && !target.Mods.Effects.CannotBeStunned && MonsterPresent(player, token.Id, target)
            && RampageEffectCurrent(player, token.OwnedEpoch);
        // Recovery was committed at the hit. Queued output may only present that same interval, never restart it.
        // Like ordinary flinches, pause waits outside the send gate; identity and generations are rechecked there.
        while (true)
        {
            if (!Current()) return;
            bool completed = false;
            await SendWorldRecord(player, token.Id, [], null, atGate: () =>
            {
                if (!Current()) { completed = true; return null; }
                if (player.Paused) return null;
                completed = true;
                return RampageStunPacket(player, token, Now());
            });
            if (completed) return;
            await AreaDelay(player, 0.016);
        }
    }

    // A current stun's 0x1d1 at its send gate, for the recovery left of it at `now`, under conn.Monsters. It is a monster
    // TakeHit like any other (TakeHitPacket), so a lockstep client's recovery is held in its frames, and the stun's own
    // hold on the AI (BusyUntil, RampageStatusLocked) moves with it.
    public static byte[]? RampageStunPacket(Player player, RampageStunToken token, double now)
    {
        var target = token.Target;
        if ((float)(token.Until - now) <= 0) return null;
        var packet = TakeHitPacket(player, token.Id, target, token.Until, now);
        if (target.BusyUntil == token.Until) target.BusyUntil = target.FlinchUntil;
        return packet;
    }

    static Task SendRampageRemoval(Player player, uint id) =>
        SendWorldRemoval(player, id, "Rampage owned object ends", atGate: () =>
        {
            if (!CurrentRampageWork || player.OwnedCombatState is not RampageWorld world) return null;
            world.PendingRemoval.Remove(id);
            return InstanceProtocol.RemoveObject(id);
        });

    // A blind that expired, changed or ended needs a send; otherwise the 32 ms loop offered an empty packet every step.
    static bool RampageBlindWork(Player player)
    {
        if (!CurrentRampageWork || player.OwnedCombatState is not RampageWorld world) return false;
        double now = ProjectileWorldTime(player);
        foreach (var (id, entry) in world.Blinds)
            if (RampageBlindEndsLocked(player, world, id, entry.Until, now)
                || world.SentBlinds.GetValueOrDefault(id) != entry.Until) return true;
        return false;
    }

    // The blind is over: the streak's effects are off, its time is up, or its monster is gone, dead or cannot be blinded.
    // Under conn.Monsters.
    static bool RampageBlindEndsLocked(Player player, RampageWorld world, uint id, double until, double now) =>
        !world.Enabled || until <= now || !player.Area.Monsters.TryGetValue(id, out var target) || target.Dead || target.Life == 0
        || target.Mods.Effects[Stat.CannotBeBlinded] != 0;

    static Task ExpireRampageBlinds(Player player) =>
        !RampageBlindWork(player) ? Task.CompletedTask :
        Send(player, [], null, atGate: () =>
        {
            if (!CurrentRampageWork || player.OwnedCombatState is not RampageWorld world) return null;
            var playerStats = world.Blinds.Count > 0 ? CharacterPlayerStats(player) : null;
            if (!CurrentRampageWork || !ReferenceEquals(player.OwnedCombatState, world)) return null;
            List<byte> bytes = [];
            double now = ProjectileWorldTime(player);
            foreach (var (id, entry) in world.Blinds.ToArray())
                if (RampageBlindEndsLocked(player, world, id, entry.Until, now))
                {
                    player.Area.Buffs.Clear(id, 179, player.Id, tag: (ushort)BuffTag.RampageBlind);
                    if (player.Area.Monsters.TryGetValue(id, out var old)) old.RampageBlindUntil = 0;
                    world.Blinds.Remove(id);
                    world.SentBlinds.Remove(id);
                }
                else if (world.SentBlinds.GetValueOrDefault(id) != entry.Until)
                {
                    player.Area.Buffs.Set(id, new(179, player.Id, U68: Buff.Seconds(entry.Until - now), Tag: (ushort)BuffTag.RampageBlind));
                    world.SentBlinds[id] = entry.Until;
                }
            if (bytes.Count > 0 && BlindPresentation(player, playerStats) is { } blind) bytes.AddRange(blind);
            return bytes.Count > 0 ? bytes.ToArray() : null;
        });

    static async Task RampageKillExplosion(Player player, long epoch, uint corpseId, SpawnedMonster corpse, int percent)
    {
        var pending = new Queue<(uint Id, SpawnedMonster Corpse)>();
        pending.Enqueue((corpseId, corpse));
        while (pending.TryDequeue(out var current))
        {
            (uint Id, SpawnedMonster Target)[] targets;
            if (!RampageEffectCurrent(player, epoch)) return;
            if (!player.RampageWorld.Exploded.Add(current.Id)) continue;
            var at = current.Corpse.Walk.Position(ProjectileWorldTime(player));
            targets = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.OwnerId is null
                && Dist(p.Value.Walk.Position(ProjectileWorldTime(player)), at) <= RampageExecution.CorpseRadius)
                .Select(p => (p.Key, p.Value)).ToArray();
            double fire = current.Corpse.MaxLife * percent / 100.0;
            foreach (var (id, target) in targets)
                if (await RampageOwnedHit(player, epoch, id, target,
                    DamagePacket.Of(DamageType.Fire, fire, fire), null, "Rampage kill explosion"))
                    pending.Enqueue((id, target));
        }
    }
}
