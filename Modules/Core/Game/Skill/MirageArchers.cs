using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static double MirageDamageFactor(IReadOnlyDictionary<uint, int> stats) =>
        Stats.Key("support_mirage_archer_damage_+%_final") is uint k ? Math.Max(0, 1 + stats.GetValueOrDefault(k) / 100d) : 1;

    static ProjectileOffense MirageOffense(ClientState conn, ProjectileOffense shot)
    {
        double factor = MirageDamageFactor(shot.SkillStats);
        var caster = new Dictionary<uint, int>(CharacterPlayerStats(conn));
        // These restrictions belong to the player using the skill himself, not to the mirage.
        foreach (string name in new[] { "keystone_ancestral_bond", "deal_no_damage_yourself" })
            if (Stats.Key(name) is uint key) caster.Remove(key);
        return shot with
        {
            MirageSource = null, CriticalTrigger = null, ClientPredicted = false,
            ActualCaster = caster, Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
            Hit = shot.Hit.Scale(factor), AreaHit = shot.AreaHit?.Scale(factor),
            Ignite = shot.Ignite with { Min = shot.Ignite.Min * factor, Max = shot.Ignite.Max * factor,
                CriticalMin = shot.Ignite.CriticalMin * factor, CriticalMax = shot.Ignite.CriticalMax * factor },
            Poison = shot.Poison with { Min = shot.Poison.Min * factor, Max = shot.Poison.Max * factor,
                CriticalMin = shot.Poison.CriticalMin * factor, CriticalMax = shot.Poison.CriticalMax * factor },
            Bleed = shot.Bleed with { Min = shot.Bleed.Min * factor, Max = shot.Bleed.Max * factor,
                CriticalMin = shot.Bleed.CriticalMin * factor, CriticalMax = shot.Bleed.CriticalMax * factor }
        };
    }

    public sealed partial class ProjectileSession
    {
        sealed class Archer(ushort skill, SupportedGem source)
        {
            public readonly ushort Skill = skill;
            public readonly SupportedGem Source = source;
            public long Expires, NextAttack;
            public uint Actor;
            public ushort Buff;
            public bool BuffShown;
        }
        Archer? archer;

        static byte[] MirageDisplay(uint actor) => InstanceEntities.AddBuff(actor, 1, 1748,
            visual: 802, list: [0, 0, 0]);

        internal byte[]? MiragePeerDisplay(uint actor, uint recipient) =>
            archer is { Actor: > 0 } current && current.Actor == actor ? MirageDisplay(recipient) : null;

        void StartMirage(Archer current)
        {
            string name = Conn.ClassRow switch
            { 0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar" };
            if (Monsters.Find($"Metadata/Monsters/Mirage/{name}Mirage") is not { Supported: true } variety) return;
            current.Buff = Conn.NextBuff++;
            Enqueue(async () =>
            {
                bool Current() => Active && ReferenceEquals(archer, current);
                if (!Current()) return;
                var at = Conn.Walk.Position(Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
                var stats = new Dictionary<uint, int>(current.Source.Stats);
                if (Stats.Key("skill_used_by_mirage_archer") is uint mirage) stats[mirage] = 1;
                var skill = SkillsFor(Conn).FindByRow(current.Skill, current.Source.Effect, current.Source.Level,
                    [.. stats.Select(p => (p.Key, p.Value))]);
                await SpawnMonster(remote, stream, ciphers, Conn, variety, (byte)Math.Clamp(Conn.Pools.Level, 1, 100),
                    MonsterRarity.Normal, at.X, at.Y, friendly: true, log: false,
                    ownedLooks: Looks(Conn, ItemsOf(Conn)), ownedSkills: skill is null ? [] : [skill],
                    attachTo: (PlayerId, "Head_Attachment", 0, 3), ownedCurrent: Current,
                    owned: (id, actor) =>
                    {
                        current.Actor = id;
                        actor.NoRewards = true; actor.EntranceGrace = true; actor.Attack = null;
                    });
                if (current.Actor == 0) return;
                await SendWorldRecord(remote, stream, ciphers, Conn, current.Actor, MirageDisplay(current.Actor), null, still: Current);
                await SendWorldRecords(remote, stream, ciphers, Conn, PlayerId, () =>
                {
                    lock (Conn.Monsters)
                    {
                        if (!Current()) return null;
                        current.BuffShown = true;
                        // Native MirageArcherVisualBuff reads list[0] in its constructor and destructor.
                        return [InstanceEntities.AddBuff(PlayerId, current.Buff, 1017, PlayerId, list: [0])];
                    }
                });
            });
        }

        void EndMirage(Archer current, bool retire = false)
        {
            if (ReferenceEquals(archer, current)) archer = null;
            if (current.Actor != 0)
            {
                Conn.Monsters.Remove(current.Actor);
                Conn.MinionOrigins.TryRemove(current.Actor, out _);
            }
            if (retire)
            {
                if (current.Actor != 0) Conn.RetiredCombatObjects.Add(current.Actor);
                if (current.BuffShown) Conn.RetiredCombatBuffs.Add((PlayerId, current.Buff));
                return;
            }
            Enqueue(async () =>
            {
                if (current.BuffShown)
                    await SendWorldRecord(remote, stream, ciphers, Conn, PlayerId,
                        InstanceEntities.RemoveBuff(PlayerId, current.Buff), null);
                if (current.Actor != 0)
                    await SendWorldRemoval(remote, stream, ciphers, Conn, current.Actor);
            });
        }

        void MirageOnArrowHit(ProjectileOffense shot)
        {
            if (shot.MirageSource is not { } source || !WeaponProfileFor(Conn, activeSet: true).Bow
                || !SameTriggerSource(source, SupportedSkill(Conn, shot.SourceSkill))) return;
            int Own(string name) => Stats.Key(name) is uint key ? source.Stats.GetValueOrDefault(key) : 0;
            var player = CharacterPlayerStats(Conn);
            int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
            double seconds = SkillDurationModifiers.Seconds(Own("support_mirage_archer_base_duration"), Player, Own)
                * Math.Max(0, 1 + (Player("mirage_archer_duration_+%") + Own("mirage_archer_duration_+%")) / 100d);
            if (seconds <= 0) return;
            long end = Runtime.Now + ProjectileRuntime.Time(seconds);
            if (archer is { } old && old.Skill == shot.SourceSkill && SameTriggerSource(old.Source, source))
            {
                old.Expires = end;
                return;
            }
            if (archer is { } previous) EndMirage(previous);
            var current = archer = new Archer(shot.SourceSkill, source) { Expires = end };
            StartMirage(current);
            double factor = Math.Max(.01, 1 + Own("support_mirage_archer_attack_speed_+%_final") / 100d);
            long Interval() => Math.Max(ProjectileRuntime.TickQuanta, ProjectileRuntime.Time(SupportedAttackSeconds(Conn, current.Skill) / factor));
            current.NextAttack = Runtime.Now + Interval();
            void Fire()
            {
                if (!ReferenceEquals(archer, current)) return;
                if (Runtime.Now >= current.Expires || Conn.PlayerLife == 0 || !WeaponProfileFor(Conn, activeSet: true).Bow
                    || !SameTriggerSource(current.Source, SupportedSkill(Conn, current.Skill)))
                {
                    EndMirage(current);
                    return;
                }
                if (Runtime.Now >= current.NextAttack)
                {
                    double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                    var from = Conn.Walk.Position(now);
                    var target = Conn.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0)
                        .Select(m => m.Walk.Position(now)).Where(p => Dist(from, p) <= 80)
                        .OrderBy(p => Dist(from, p)).Select(p => ((int X, int Y)?)p).FirstOrDefault();
                    if (target is { } aim)
                    {
                        var gem = GemSkillEffects(Conn).FirstOrDefault(g => g.Id == current.Skill);
                        if (gem.Name is not null)
                        {
                            if (current.Actor != 0)
                            {
                                if (Conn.Monsters.TryGetValue(current.Actor, out var actor)) actor.Walk.Teleport(from.X, from.Y, now);
                                WorldRecords(current.Actor, () => ReferenceEquals(archer, current)
                                    ? [InstanceProtocol.ActionStarted(current.Actor, from.X, from.Y, aim.X, aim.Y,
                                        current.Skill, Conn.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(current.Actor)] : null);
                            }
                            var crit = CaptureSkillCriticalRoll(Conn, current.Skill);
                            if (ArrowRain(SkillsFor(Conn).EffectId(gem.Effect)))
                                ReleaseArrowRain(this, current.Skill, gem, aim, crit, 0, mirage: true);
                            else if (ResolveProjectileDefinition(gem.Effect) is not null)
                            {
                                var use = CaptureGemProjectile(Conn, this, current.Skill, gem, aim, crit);
                                Runtime.Release(use with { Seed = ++seed, Payload = MirageOffense(Conn, (ProjectileOffense)use.Payload!) });
                            }
                        }
                    }
                    current.NextAttack = Runtime.Now + (target is null ? ProjectileRuntime.QuantaPerSecond / 10 : Interval());
                }
                Runtime.Schedule(Math.Min(current.Expires, current.NextAttack), Fire);
            }
            Runtime.Schedule(Math.Min(current.Expires, current.NextAttack), Fire);
        }
    }
}

