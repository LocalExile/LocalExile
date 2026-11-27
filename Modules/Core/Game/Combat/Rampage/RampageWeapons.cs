using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static void ReleaseRampageProjectiles(Player player, RampageWorld.Job job)
    {
        var own = job.Tier.Skill.AtLevel(job.Level)!;
        var metadata = SkillsFor(player).ProjectileFor(job.Tier.Skill.Row);
        if (metadata is null) throw new InvalidOperationException($"No projectile metadata for {job.Tier.Skill.Id}");
        var profile = new ProjectileProfile(metadata.Value.Id)
        {
            Count = 12, CircularSpread = true, SpreadRadians = Math.PI * 2,
            Speed = ProjectileSpeed.CellsPerSecond(metadata.Value.Speed, _ => 0, _ => 0), Range = 120,
            Pierce = own.Get(Stat.AlwaysPierce) != 0 ? int.MaxValue : 0,
            Chain = Math.Max(0, own.Get(Stat.NumberOfChains)),
        };
        var session = ProjectilesFor(player);
        ProjectileActorKey emitter;
        if (!RampageEffectCurrent(player, job.Epoch)) return;
        if (!player.Area.Monsters.TryGetValue(job.Actor, out var daemon)) return;
        emitter = session.ActorKey(job.Actor, daemon);
        var packet = RampageSpellPacket(player, job.Tier.Skill.Row, job.Level);
        session.Schedule(ProjectileRuntime.Time(job.At), () => RampageEffectCurrent(player, job.Epoch), () =>
        {
            var caster = CharacterPlayerStats(player);
            return new ProjectileUse(profile, new(player.Id, player.ProjectileEpoch), emitter, 1,
                new(job.Origin.X, job.Origin.Y), new(job.Origin.X + 100, job.Origin.Y))
            { Payload = new ProjectileOffense(packet, true, 10000, own.SpellCriticalChance, 150, job.Tier.Skill.Id)
                { RampageEpoch = job.Epoch, RampageSkill = own, Effect = job.Tier.Skill.Row, Level = job.Level,
                    TargetCritical = TargetCriticalBonuses.From(caster, null), NoExtraCriticalDamage = CriticalHitsDealNoExtraDamage(caster) } };
        });
    }

    static async Task AdvanceRampageWeapons(Player player)
    {
        List<(uint Id, SpawnedMonster Actor, RampageWorld.Actor Plan, uint TargetId, SpawnedMonster Target)> swings = [];
        List<(uint Id, (int X, int Y) From, (int X, int Y) To)> moves = [];
        if (!CurrentRampageWork) return;
        double now = ProjectileWorldTime(player);
        foreach (var plan in player.RampageWorld.Actors.Values.Where(a => a.Weapon is not null).ToArray())
        {
            if (!RampageEffectCurrent(player, plan.Epoch) || plan.NextActionAt > now
                || !player.Area.Monsters.TryGetValue(plan.Id, out var actor) || actor.Dead || now < actor.BusyUntil) continue;
            var from = actor.Walk.Position(now);
            var target = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.OwnerId is null)
                .OrderBy(p => Dist(from, p.Value.Walk.Position(now))).FirstOrDefault();
            var to = target.Value?.Walk.Position(now) ?? player.Walk.Position(now);
            double range = plan.Weapon == RampageCatalog.WeaponKind.Bow ? 80
                : plan.Weapon == RampageCatalog.WeaponKind.TwoHandedSword ? 20 : 10;
            if (target.Value is not null && Dist(from, to) <= range)
            {
                var sources = StatSourceValues(player);
                int speed = Stat.MinionAttackSpeedPlusPct is var k ? sources.GetValueOrDefault(k) : 0;
                double seconds = (actor.Variety?.AttackMs ?? 1500) / 1000.0 / Math.Max(.01, 1 + (40 + speed) / 100.0);
                player.RampageWorld.Actors[plan.Id] = plan with { NextActionAt = now + seconds };
                actor.Walk.Order(from.X, from.Y, now); actor.LastTarget = null;
                swings.Add((plan.Id, actor, plan, target.Key, target.Value));
            }
            else if (Dist(from, to) > 8 && (actor.LastTarget is not { } last || Dist(last, to) > 3))
            {
                actor.Walk.Order(to.X, to.Y, now); actor.LastTarget = to;
                moves.Add((plan.Id, from, to));
            }
        }
        foreach (var (id, from, to) in moves)
            await SendWorldRecord(player, id, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y,
                0x2909, 0xffff, player.MonsterOrderFlags), "Rampage weapon moves", still: () =>
                player.OwnedCombatState is RampageWorld current && current.Actors.TryGetValue(id, out var actor)
                    && RampageEffectCurrent(player, actor.Epoch));
        foreach (var swing in swings)
        {
            if (!CurrentRampageWork) return;
            var from = swing.Actor.Walk.Position(ProjectileWorldTime(player));
            var to = swing.Target.Walk.Position(ProjectileWorldTime(player));
            await SendWorldRecord(player, swing.Id, InstanceProtocol.ActionStartedNow(swing.Id, from.X, from.Y, to.X, to.Y,
                SkillCatalog.FirstSkillId, 0xffff, player.MonsterOrderFlags, swing.TargetId), "Rampage weapon attacks",
                still: () => RampageEffectCurrent(player, swing.Plan.Epoch));
            var variety = swing.Actor.Variety!;
            var baseDamage = Monsters.BaseDamage(variety, (byte)swing.Plan.Level) ?? (1, 1);
            var sources = StatSourceValues(player);
            int inc = Stat.MinionDamagePlusPct is var key ? sources.GetValueOrDefault(key) : 0;
            var packet = DamagePacket.Of(DamageType.Physical, baseDamage.Item1, baseDamage.Item2)
                .Scale(Math.Max(0, 100L + inc) / 100.0);
            int accuracyInc = Stat.MinionAccuracyRatingPlusPct is var ak ? sources.GetValueOrDefault(ak) : 0;
            int accuracy = (int)Math.Clamp((long)(Monsters.BaseAccuracy((byte)swing.Plan.Level) ?? 0) * Math.Max(0L, 100L + accuracyInc) / 100, 0, int.MaxValue);
            if (swing.Plan.Weapon == RampageCatalog.WeaponKind.Bow)
            {
                var session = ProjectilesFor(player);
                var arrow = SkillsFor(player).Projectile("Metadata/Projectiles/DefaultArrow");
                var profile = new ProjectileProfile(arrow?.Id ?? "Metadata/Projectiles/DefaultArrow")
                { Speed = ProjectileSpeed.CellsPerSecond(arrow?.Speed ?? 3260, _ => 0, _ => 0), Range = 120 };
                var emitter = session.ActorKey(swing.Id, swing.Actor);
                session.Schedule(ProjectileRuntime.Time(ProjectileWorldTime(player) + .25),
                    () => RampageEffectCurrent(player, swing.Plan.Epoch) && !swing.Actor.Dead
                        && ProjectileWorldTime(player) < swing.Plan.ExpiresAt
                        && ReferenceEquals(player.Area.Monsters.GetValueOrDefault(swing.Id), swing.Actor), () =>
                    new ProjectileUse(profile, new(player.Id, player.ProjectileEpoch), emitter, 1,
                        new(from.X, from.Y), new(to.X, to.Y))
                    { Payload = new ProjectileOffense(packet, false, accuracy, 500, 150, "Rampage bow")
                        { RampageEpoch = swing.Plan.Epoch,
                            NoExtraCriticalDamage = CriticalHitsDealNoExtraDamage(CharacterPlayerStats(player)) } });
            }
            else
            {
                if (RampageEffectCurrent(player, swing.Plan.Epoch))
                    player.RampageWorld.Strikes.Add(new(ProjectileWorldTime(player) + .25, swing.Plan.Epoch,
                        swing.Id, swing.Actor, swing.TargetId, swing.Target, packet,
                        swing.Plan.Weapon == RampageCatalog.WeaponKind.TwoHandedSword, accuracy));
            }
        }
        RampageWorld.Strike[] contacts;
        if (!CurrentRampageWork) return;
        now = ProjectileWorldTime(player);
        contacts = player.RampageWorld.Strikes.Where(s => s.At <= now).ToArray();
        foreach (var contact in contacts) player.RampageWorld.Strikes.Remove(contact);
        foreach (var strike in contacts)
        {
            KeyValuePair<uint, SpawnedMonster>[] targets;
            if (!RampageEffectCurrent(player, strike.Epoch) || strike.Actor.Dead
                || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(strike.ActorId), strike.Actor)) continue;
            now = ProjectileWorldTime(player);
            var at = strike.Actor.Walk.Position(now);
            targets = player.Area.MonstersNear(at, (strike.Area ? 20 : 12)).Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.OwnerId is null
                && (strike.Area || p.Key == strike.TargetId && ReferenceEquals(p.Value, strike.Target))
                && Dist(p.Value.Walk.Position(now), at) <= (strike.Area ? 20 : 12)).ToArray();
            foreach (var target in targets)
                await RampageOwnedHit(player, strike.Epoch,
                    target.Key, target.Value, strike.Damage, null, "Rampage weapon", attack: true, accuracy: strike.Accuracy);
        }
    }
}
