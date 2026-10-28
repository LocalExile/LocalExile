using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static Task ScheduleOrdinaryBow(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) aim, double attackSeconds, int action, int epoch)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        double acceptedAt = conn.Clock.Elapsed.TotalSeconds;
        double delay = AttackContactDelay(conn, attackSeconds, false);
        session.Schedule(ProjectileRuntime.Time(acceptedAt + delay),
            () => OrdinaryBowReleaseCurrent(conn, action, epoch) && WeaponProfileFor(conn, true).Bow,
            () =>
            {
                var character = CharacterPlayerStats(conn);
                int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
                int RawCritical(string name) => IsCriticalProhibition(name) ? 0 : Get(name);
                int effect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } id ? Skills.EffectRow(id) : -1;
                var weapon = WeaponProfileFor(conn, activeSet: true);
                int criticalChance = CriticalProhibited(conn, attack: true)
                    ? AttackBaseCriticalStrike.Chance(!weapon.Unarmed, weapon.Critical, conn.Pools.CriticalStrikeChancePct, RawCritical)
                    : conn.Pools.AttackCriticalStrikeChance;
                var captured = OrdinaryBowShot.Capture(WeaponFor(conn), character, Skills.EffectStats(effect, 1), Stats,
                    conn.Pools.Accuracy, criticalChance, conn.Pools.CriticalStrikeMultiplier);
                var shot = new ProjectileOffense(captured.Hit, false, captured.Accuracy, captured.CriticalChance,
                    captured.CriticalMultiplier, "ordinary bow")
                { SkillStats = Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2),
                    Penetration = captured.Penetration, ResistanceMods = captured.ResistanceMods, ImpaleChance = captured.ImpaleChance,
                    AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct, AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
                    AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
                    ReturnMultiplier = ProjectileReturn.DamageMore(Get) };
                var arrow = Skills.Projectile("Metadata/Projectiles/DefaultArrow");
                double speed = ProjectileSpeed.CellsPerSecond(arrow?.Speed ?? 3260, Get, _ => 0,
                    Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
                var profile = GatewayProjectileDefaults.Resolve(arrow?.Id ?? "Metadata/Projectiles/DefaultArrow", Get, speed, attack: true);
                var origin = ProjectileSession.Vector(conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond));
                var target = ProjectileSession.Vector(aim);
                origin += (target - origin).Unit * Math.Min(1.5, (target - origin).Length / 2);
                return new(profile, new(PlayerId, epoch), new(PlayerId, epoch), 1, origin, target)
                    { Payload = shot, ReturnAnchor = new(PlayerId, epoch) };
            });
        return Task.CompletedTask;
    }
}
