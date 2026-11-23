using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static double MirageDamageFactor(IReadOnlyDictionary<uint, int> stats) =>
        Stat.SupportMirageArcherDamagePlusPctFinal is var k ? Math.Max(0, 1 + stats.GetValueOrDefault(k) / 100d) : 1;

    static ProjectileOffense MirageOffense(Player player, ProjectileOffense shot)
    {
        double factor = MirageDamageFactor(shot.SkillStats);
        var caster = new Dictionary<uint, int>(CharacterPlayerStats(player));
        // These restrictions belong to the player using the skill himself, not to the mirage.
        foreach (uint name in new[] { Stat.KeystoneAncestralBond, Stat.DealNoDamageYourself })
            caster.Remove(name);
        return shot with
        {
            MirageSource = null, Mirage = true, CriticalTrigger = null, ClientPredicted = false,
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

        // The archer's look (MirageArcherDisplayBuff reads list 0..2).
        static readonly Buff MirageDisplay = new(1748, Visual: 802, List: [0, 0, 0]);

        void StartMirage(Archer current)
        {
            string name = Player.Character.ClassRow switch
            { 0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar" };
            if (Monsters.Find($"Metadata/Monsters/Mirage/{name}Mirage") is not { Supported: true } variety) return;
            current.Buff = Player.NextBuff++;
            Enqueue(async () =>
            {
                bool Current() => Active && ReferenceEquals(archer, current);
                if (!Current()) return;
                var at = Player.Walk.Position(Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
                var stats = new Dictionary<uint, int>(current.Source.Stats);
                if (Stat.SkillUsedByMirageArcher is var mirage) stats[mirage] = 1;
                var skill = SkillsFor(Player).FindByRow(current.Skill, current.Source.Effect, current.Source.Level,
                    [.. stats.Select(p => (p.Key, p.Value))]);
                await SpawnMonster(Player, variety, (byte)Math.Clamp(Player.Pools.Level, 1, 100),
                    MonsterRarity.Normal, at.X, at.Y, friendly: true, log: false,
                    ownedLooks: Looks(Player, ItemsOf(Player)), ownedSkills: skill is null ? [] : [skill],
                    attachTo: (Player.Id, "Head_Attachment", 0, 3), ownedCurrent: Current,
                    owned: (id, actor) =>
                    {
                        current.Actor = id;
                        actor.NoRewards = true; actor.EntranceGrace = true; actor.Attack = null;
                    });
                if (current.Actor == 0) return;
                if (!Current()) return;
                Player.Area.Buffs.Set(current.Actor, MirageDisplay);
                current.BuffShown = true;
                // Native MirageArcherVisualBuff reads list[0] in its constructor and destructor.
                Player.Area.Buffs.Set(Player.Id, new(1017, Player.Id, List: [0]));
            });
        }

        void EndMirage(Archer current, bool retire = false)
        {
            if (ReferenceEquals(archer, current)) archer = null;
            if (current.Actor != 0)
            {
                Player.Area.Monsters.Remove(current.Actor);
                Player.MinionOrigins.TryRemove(current.Actor, out _);
            }
            if (retire)
            {
                if (current.Actor != 0) Player.RetiredCombatObjects.Add(current.Actor);
                if (current.BuffShown) Player.Area.Buffs.Clear(Player.Id, 1017, Player.Id);
                return;
            }
            Enqueue(async () =>
            {
                if (current.BuffShown) Player.Area.Buffs.Clear(Player.Id, 1017, Player.Id);
                if (current.Actor != 0)
                    await SendWorldRemoval(Player, current.Actor);
            });
        }

        void MirageOnArrowHit(ProjectileOffense shot)
        {
            if (shot.MirageSource is not { } source || !WeaponProfileFor(this.Player, activeSet: true).Bow
                || !SameTriggerSource(source, SupportedSkill(this.Player, shot.SourceSkill))) return;
            int Own(uint key) => source.Stats.GetValueOrDefault(key);
            var playerStats = CharacterPlayerStats(this.Player);
            int PlayerStat(uint key) => playerStats.GetValueOrDefault(key);
            double seconds = SkillDurationModifiers.Seconds(Own(Stat.SupportMirageArcherBaseDuration), PlayerStat, Own)
                * Math.Max(0, 1 + (PlayerStat(Stat.MirageArcherDurationPlusPct) + Own(Stat.MirageArcherDurationPlusPct)) / 100d);
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
            double factor = Math.Max(.01, 1 + Own(Stat.SupportMirageArcherAttackSpeedPlusPctFinal) / 100d);
            long Interval() => Math.Max(ProjectileRuntime.TickQuanta, ProjectileRuntime.Time(SupportedAttackSeconds(this.Player, current.Skill) / factor));
            current.NextAttack = Runtime.Now + Interval();
            void Fire()
            {
                if (!ReferenceEquals(archer, current)) return;
                if (Runtime.Now >= current.Expires || this.Player.PlayerLife == 0 || !WeaponProfileFor(this.Player, activeSet: true).Bow
                    || !SameTriggerSource(current.Source, SupportedSkill(this.Player, current.Skill)))
                {
                    EndMirage(current);
                    return;
                }
                if (Runtime.Now >= current.NextAttack)
                {
                    double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                    var from = this.Player.Walk.Position(now);
                    var target = Player.Area.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0)
                        .Select(m => m.Walk.Position(now)).Where(p => Dist(from, p) <= 80)
                        .OrderBy(p => Dist(from, p)).Select(p => ((int X, int Y)?)p).FirstOrDefault();
                    if (target is { } aim)
                    {
                        var gem = GemSkillEffects(Player).FirstOrDefault(g => g.Id == current.Skill);
                        if (gem.Name is not null)
                        {
                            if (current.Actor != 0)
                            {
                                if (this.Player.Area.Monsters.TryGetValue(current.Actor, out var actor)) actor.Walk.Teleport(from.X, from.Y, now);
                                WorldRecords(current.Actor, () => ReferenceEquals(archer, current)
                                    ? [InstanceProtocol.ActionStarted(current.Actor, from.X, from.Y, aim.X, aim.Y,
                                        current.Skill, 0xffff, 0x0408), InstanceProtocol.ActionEnded(current.Actor)] : null);
                            }
                            var crit = CaptureSkillCriticalRoll(this.Player, current.Skill);
                            if (ArrowRain(SkillsFor(this.Player).EffectId(gem.Effect)))
                                ReleaseArrowRain(this, current.Skill, gem, ((int X, int Y))aim, crit, 0, mirage: true);
                            else if (ResolveProjectileDefinition(gem.Effect) is not null)
                            {
                                var use = CaptureGemProjectile(this.Player, this, current.Skill, gem, ((int X, int Y))aim, crit);
                                Runtime.Release(use with { Seed = ++seed, Payload = MirageOffense(this.Player, (ProjectileOffense)use.Payload!) });
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

