using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static double MirageDamageFactor(IReadOnlyDictionary<uint, int> stats) =>
        Stats.Key("support_mirage_archer_damage_+%_final") is uint k ? Math.Max(0, 1 + stats.GetValueOrDefault(k) / 100d) : 1;

    static ProjectileOffense MirageOffense(ClientState conn, ProjectileOffense shot)
    {
        double factor = MirageDamageFactor(shot.SkillStats);
        return shot with
        {
            MirageSource = null, CriticalTrigger = null, ClientPredicted = false,
            ActualCaster = CharacterPlayerStats(conn), Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
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
        }
        Archer? archer;

        void MirageEffect(string phase)
        {
            var at = Conn.Walk.Position(Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
            Effect(at.X, at.Y, $"Metadata/Effects/Spells/mirage_archer/{phase}.ao", ProjectileRuntime.Time(1));
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
            var current = archer = new Archer(shot.SourceSkill, source) { Expires = end };
            MirageEffect("appear");
            double factor = Math.Max(.01, 1 + Own("support_mirage_archer_attack_speed_+%_final") / 100d);
            long Interval() => Math.Max(ProjectileRuntime.TickQuanta, ProjectileRuntime.Time(SupportedAttackSeconds(Conn, current.Skill) / factor));
            current.NextAttack = Runtime.Now + Interval();
            void Fire()
            {
                if (!ReferenceEquals(archer, current)) return;
                if (Runtime.Now >= current.Expires || Conn.PlayerLife == 0 || !WeaponProfileFor(Conn, activeSet: true).Bow
                    || !SameTriggerSource(current.Source, SupportedSkill(Conn, current.Skill)))
                {
                    archer = null;
                    MirageEffect("disappear");
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

