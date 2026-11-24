using GameServer.Core.Projectiles;

public static partial class Game
{
    /// <summary>The object a storm's fall is drawn with (Projectiles.dat): the generic monster fire projectile, a
    /// burning ball with a 466 ms impact clip. No Projectiles row is Firestorm's own (all 1410 rows searched), so this
    /// is the closest one that flies and explodes in fire; the damage is the skill's own (SkillCatalog.MonsterStorm).</summary>
    public const string StormFallProjectile = "Metadata/Projectiles/MonsterGenericProjectileFire";

    /// <summary>One cast's falls: when each lands (<paramref name="startsAt"/> is the first, seconds of the connection
    /// clock) and the cell it lands on. Every fall is scattered uniformly inside the skill's drop area around the cell
    /// the cast was aimed at — Fire Fury's 30 falls, 5.1 cells wide, 0.1 s apart. The scatter is seeded from the
    /// action's sequence, the fall's index and the monster, as the client's own aim scatter is (MonsterMortarTarget).
    /// Pure, so a test reads the schedule without a clock or a wire.</summary>
    public static (double At, (int X, int Y) Cell)[] StormFalls(SkillCatalog.StormInfo storm, double startsAt,
        (int X, int Y) aim, uint owner, ushort sequence)
    {
        var falls = new (double At, (int X, int Y) Cell)[storm.Falls];
        for (int i = 0; i < falls.Length; i++)
        {
            var random = new AnimationVariantSelector.NativeRandom([sequence, (uint)i, owner, 0, 0, 0, 0, 0]);
            double angle = random.Range(0, 6.2831855f);
            double distance = Math.Sqrt(random.Range(0, 1)) * storm.Radius;
            falls[i] = (startsAt + i * storm.IntervalSeconds,
                ((int)Math.Round(aim.X + Math.Sin(angle) * distance), (int)Math.Round(aim.Y - Math.Cos(angle) * distance)));
        }
        return falls;
    }

    /// <summary>A storm skill's cast (Fire Fury's Firestorm): one fall per interval for the skill's duration, each
    /// resolving on the server where the bolt would have landed. The client draws the storm from the cast (0x01ab),
    /// so the server sends no lobbed bolt per fall: at each fall's time the bolt's own EndArea resolves on the fall's
    /// cell, with the skill's fire damage (HostileHit -> CommitMonsterHitLocked). The falls outlive the action — the
    /// storm lasts its duration, not its caster's cast — and stop with the monster: <paramref name="still"/> keeps
    /// them only while it is present. <paramref name="startsAt"/> is the first fall's time, seconds of the connection
    /// clock.</summary>
    public static void MonsterFirestormStorm(Player player, uint id, SpawnedMonster m, MonsterAttack a, int action,
        SkillCatalog.StormInfo storm, double startsAt, (int X, int Y) aim, ushort sequence)
    {
        var skills = SkillsFor(player);
        int speed = skills.Projectile(StormFallProjectile) is (_, int rowSpeed) ? rowSpeed : 3260;
        int collision = skills.ProjectileContactCells(StormFallProjectile) ?? 2;
        var falling = a with { Damage = storm.Damage, Spell = true };
        var fall = new SkillCatalog.MonsterProjectileInfo(StormFallProjectile, speed, Lobbed: true, collision)
        {
            Count = 1,
            // The drop area is also the fall's own explosion: the skill's one area stat, scaled by its storm area stat.
            AreaRadius = storm.Radius,
        };
        foreach (var (at, cell) in StormFalls(storm, startsAt, aim, id, sequence))
            LaunchMonsterFirestormFall(player, id, m, falling, fall, action, at, cell, sequence);
    }

    /// <summary>One fall of a storm: no projectile object. At the fall's time, resolve the bolt's own EndArea where
    /// it would have landed (Runtime.Impact: a grounded impact, no flight, no direct-hit victim), so the area hit
    /// reaches the player through the same HostileHit -> CommitMonsterHitLocked path as the bolt's explosion. A fresh
    /// use per fall, so the projectile hit ledger never refuses a later fall for an earlier one's hit; the falls
    /// outlive the action, so the rule is the monster still being present — never the action staying current.</summary>
    public static void LaunchMonsterFirestormFall(Player player, uint id, SpawnedMonster m, MonsterAttack a,
        SkillCatalog.MonsterProjectileInfo projectile, int action, double at, (int X, int Y) cell, ushort sequence)
    {
        var session = ProjectilesFor(player);
        session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(at)), () =>
        {
            if (!MonsterPresent(player, id, m) || InGracePeriod(player)) return;
            try
            {
                // The impact's origin is the monster's cell at the fall's time, as a lobbed shot's would be; the hit
                // itself is read from the fall's cell (the contact position), exactly as the bolt's explosion is.
                var from = ProjectileSession.Vector(m.Walk.Position(at));
                var centre = ProjectileSession.Vector(cell);
                double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, _ => 0, _ => 0,
                    Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
                double landing = Math.Max(0.5, (centre - from).Length);
                var profile = MonsterProjectileProfile(projectile, speed, landing, Math.Max(landing, a.Range) * 1.5 + 5);
                var owner = session.ActorKey(id, m);
                var use = new ProjectileUse(profile, owner, owner, 2, from, centre)
                {
                    Payload = new HostileProjectileOffense(id, m, a, action, $"{m.Label} {projectile.Metadata}")
                        { Sequence = sequence },
                };
                if (profile.EndArea is { } area) session.Runtime.Impact(use, centre, area);
            }
            catch (InvalidOperationException ex)
            { Console.WriteLine($"[gateway] {player.Connection.Remote} firestorm fall unavailable: {ex.Message}"); }
        });
    }
}
