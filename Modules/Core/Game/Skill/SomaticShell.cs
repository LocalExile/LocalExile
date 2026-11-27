using GameServer.Core.Projectiles;

public static partial class Game
{
    const string SomaticShellEffect = "SomaticShell";
    const string KineticShellProjectile = "Metadata/Projectiles/KineticShellProjectile";
    const ushort KineticShellBuff = 2664;
    const ushort KineticShellImmunityBuff = 2672;

    static readonly ProjectileSkillDefinition SomaticShellProjectile = new(KineticShellProjectile,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(KineticShellProjectile, get, speed, attack: true)))
    {
        SpecializeRelease = (context, use) =>
        {
            var session = context.Session;
            long now = session.Runtime.Now;
            var target = session.SomaticTarget(use, use.Origin, use.Aim);
            return use with
            {
                Aim = target?.Position(now) ?? use.Aim,
                Payload = ((ProjectileOffense)use.Payload!) with
                {
                    SomaticApplications = new(1),
                    BypassesSomaticShell = context.Own(Stat.DamageCannotBreakKineticShells) != 0
                }
            };
        }
    };

    internal sealed class SomaticShellApplications(int remaining)
    {
        public int Remaining = remaining;
    }

    internal sealed class SomaticShellState(ProjectileSession session, GameArea area, SpawnedMonster target,
        ProjectileUse use, ProjectileAreaRecipe areaHit, int remaining, int percent, double until, int immunityMs)
    {
        public int Remaining = remaining;
        public int Percent { get; } = percent;
        public double Until { get; } = until;
        public int ImmunityMs { get; } = immunityMs;
        public ProjectileSession Session { get; } = session;
        public GameArea Area { get; } = area;
        public SpawnedMonster Target { get; } = target;
        public ProjectileUse Use { get; } = use;
        public ProjectileAreaRecipe AreaHit { get; } = areaHit;
    }

    sealed record SomaticImmunity(double Until);
    static readonly RuleTable<SpawnedMonster, SomaticShellState> SomaticShells = new();
    static readonly RuleTable<SpawnedMonster, SomaticImmunity> SomaticImmunities = new();

    internal static void ClearSomaticShellLocked(SpawnedMonster target)
    {
        if (SomaticShells.TryGetValue(target, out var shell)) shell.Session.RemoveSomaticShell(shell);
    }

    static int AbsorbSomaticShell(SpawnedMonster target, int damage, ref int chaos, double now, bool bypassesShell)
    {
        if (bypassesShell || damage <= 0 || target.Invulnerable || !SomaticShells.TryGetValue(target, out var shell))
            return damage;
        if (now >= shell.Until || !shell.Session.Active)
        {
            shell.Session.RemoveSomaticShell(shell);
            return damage;
        }
        int absorbed = (int)Math.Min(shell.Remaining, (long)damage * shell.Percent / 100);
        if (absorbed <= 0) return damage;
        shell.Remaining -= absorbed;
        chaos -= (int)((long)chaos * absorbed / damage);
        if (shell.Remaining == 0) shell.Session.BreakSomaticShell(shell, now);
        return damage - absorbed;
    }

    public sealed partial class ProjectileSession
    {
        List<SomaticShellState>? somaticShells;

        internal ProjectileActor? SomaticTarget(ProjectileUse use, ProjectileVector origin, ProjectileVector aim,
            ProjectileActorKey? exclude = null)
        {
            long now = Runtime.Now;
            return ActorsNear(origin, use.Profile.Range, now)
                .Where(actor => actor.Team != use.Team && actor.Key != use.Owner && actor.Key != exclude)
                .Select(actor => (Actor: actor, At: actor.Position(now)))
                .Where(candidate => (candidate.At - origin).Length <= use.Profile.Range
                    && TraceTerrain(origin, candidate.At) is null)
                .OrderBy(candidate => (candidate.At - aim).Length).ThenBy(candidate => candidate.Actor.Key.Id)
                .Select(candidate => candidate.Actor).FirstOrDefault();
        }

        void ApplySomaticShell(ProjectileDamage contact, SpawnedMonster target, ProjectileOffense shot,
            IReadOnlyDictionary<uint, int> playerStats)
        {
            if (!Active || contact.IsArea || !target.Hostile || shot.SomaticApplications is not { Remaining: > 0 } applications
                || target.Effects[Stat.BaseCannotBeKineticShelled] != 0) return;
            double now = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
            if (SomaticImmunities.TryGetValue(target, out var immunity) && immunity.Until > now) return;
            int Own(uint key) => shot.SkillStats.GetValueOrDefault(key);
            int capacity = Own(Stat.KineticShellMaximumDamageAbsorbedFromHits);
            int limit = Own(Stat.KineticShellNumberOfActiveBuffsAllowed);
            int percent = Math.Clamp(Own(Stat.KineticShellHitDamageAbsorbedPct), 0, 100);
            int radius = SkillRadius(Player, shot.SkillStats, spell: false, shot.Effect);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
                key => playerStats.GetValueOrDefault(key), Own);
            if (capacity <= 0 || limit <= 0 || percent <= 0 || radius <= 0 || duration <= 0) return;
            applications.Remaining--;
            if (SomaticShells.TryGetValue(target, out var previous)) previous.Session.RemoveSomaticShell(previous);
            var own = somaticShells ??= [];
            for (int i = own.Count - 1; i >= 0; i--)
                if (own[i].Target.Dead || own[i].Target.Life == 0) RemoveSomaticShell(own[i]);
            while (own.Count >= limit) RemoveSomaticShell(own[0]);
            double areaMore = Math.Max(0, 1 + Own(Stat.ActiveSkillAreaDamagePlusPctFinal) / 100d);
            var shell = new SomaticShellState(this, Player.Area, target, contact.Projectile.Use,
                new(SomaticShellEffect, radius, Multiplier: areaMore), capacity, percent, now + duration,
                Own(Stat.KineticShellImmunityAfterExplosionMs));
            own.Add(shell);
            SomaticShells.AddOrUpdate(target, shell);
            shell.Area.Buffs.Set(target.ObjectId, new(KineticShellBuff, Player.Id, shot.SourceSkill,
                Values: [(uint)percent], U68: Buff.Seconds(duration), F6c: 1));
            Runtime.Schedule(contact.At + ProjectileRuntime.Time(duration), () => RemoveSomaticShell(shell));
        }

        internal void RemoveSomaticShell(SomaticShellState shell)
        {
            if (somaticShells?.Remove(shell) != true) return;
            if (SomaticShells.TryGetValue(shell.Target, out var current) && ReferenceEquals(shell, current))
            {
                SomaticShells.Remove(shell.Target);
                shell.Area.Buffs.Clear(shell.Target.ObjectId, KineticShellBuff, Player.Id);
            }
        }

        internal void BreakSomaticShell(SomaticShellState shell, double now)
        {
            RemoveSomaticShell(shell);
            var target = shell.Target;
            var origin = Vector(target.Walk.Position(now));
            var excluded = ActorKey(target.ObjectId, target);
            var use = shell.Use;
            var shot = (ProjectileOffense)use.Payload!;
            if (shell.ImmunityMs > 0)
            {
                double duration = shell.ImmunityMs / 1000d;
                var immunity = new SomaticImmunity(now + duration);
                SomaticImmunities.AddOrUpdate(target, immunity);
                shell.Area.Buffs.Set(target.ObjectId, new(KineticShellImmunityBuff, Player.Id, U68: Buff.Seconds(duration), F6c: 1));
                Runtime.Schedule(Math.Max(Runtime.Now, ProjectileRuntime.Time(immunity.Until)), () =>
                {
                    if (!SomaticImmunities.TryGetValue(target, out var current) || !ReferenceEquals(immunity, current)) return;
                    SomaticImmunities.Remove(target);
                    shell.Area.Buffs.Clear(target.ObjectId, KineticShellImmunityBuff, Player.Id);
                });
            }
            Runtime.Schedule(Math.Max(Runtime.Now, ProjectileRuntime.Time(now)), () =>
            {
                if (!Active || Player.PlayerLife == 0) return;
                Runtime.Impact(use with { Origin = origin, Payload = shot with { SomaticApplications = null } }, origin, shell.AreaHit);
                var next = SomaticTarget(use, origin, origin, excluded);
                if (next is null) return;
                int transfers = shot.SkillStats.GetValueOrDefault(Stat.KineticShellTransferToXTargetsOnProjectileHit);
                Runtime.Release(use with
                {
                    Origin = origin, Aim = next.Position(Runtime.Now), ProjectileTargets = null, ProjectilePayloads = null,
                    Payload = shot with
                    {
                        ExcludedTarget = excluded, ClientPredicted = false, MirageSource = null,
                        SomaticApplications = new(Math.Max(0, transfers))
                    }
                });
            });
        }
    }
}
