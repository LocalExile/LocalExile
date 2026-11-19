using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static readonly string[] VortexSupportMore =
    [
        "support_controlled_destruction_spell_damage_+%_final", "support_gem_elemental_damage_+%_final",
        "support_area_concentrate_area_damage_+%_final", "support_echo_damage_+%_final",
        "support_efficacy_spell_damage_+%_final", "support_efficacy_damage_over_time_+%_final",
        "support_rapid_decay_damage_over_time_+%_final", "support_reduced_duration_damage_+%_final",
        "support_hypothermia_cold_damage_over_time_+%_final", "support_bonechill_cold_damage_+%_final",
        "cruelty_damage_over_time_+%_final",
    ];

    // GroundEffectTypes.Maelstorm uses ground_maelstrom_art_variation, independently of the cast's explosion art.
    static readonly Lazy<Dictionary<int, (string Ao, double Radius)>> VortexGroundArt = new(() =>
    {
        int type = ClientTables.Table("GroundEffectTypes").Single(r => r.Text("Id") == "Maelstorm").Index;
        return ClientTables.Table("GroundEffects").Where(r => r.Int("GroundEffectTypesKey") == type)
            .ToDictionary(r => r.Int("__col1"), r => (r.Texts("AOFile").Single(), r.Real("__col4")));
    });

    static void PlaceVortex(ProjectileSession session, (int X, int Y) centre, int effect, IReadOnlyDictionary<uint, int> own,
        int repeat, double more, bool throughBolt, int groundArt)
    {
        var conn = session.Conn;
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(own, name);
        int Player(string name) => GolemStat(player, name);
        int Get(string name) => Own(name) + Player(name);
        int Duration(string name) => Player(name) + (name == SkillDurationModifiers.IncreasedDuration ? Get("frost_bolt_nova_duration_+%") : 0);
        double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Duration, Own);
        if (seconds <= 0) return;

        double increased = (double)Get("damage_+%") + Get("area_damage_+%") + Get("spell_damage_+%") + Get("elemental_damage_+%")
            + Get("cold_damage_+%") + Get("damage_over_time_+%") + Get("cold_damage_over_time_+%") + Get("frost_bolt_nova_damage_+%")
            + (throughBolt ? Get("active_skill_damage_+%_when_cast_on_frostbolt") : 0);
        double dps = Own("base_cold_damage_to_deal_per_minute") / 60d * Math.Max(0, 1 + increased / 100);
        dps *= Math.Max(0, 1 + ((double)Get("dot_multiplier_+") + Get("cold_dot_multiplier_+")) / 100);
        foreach (string stat in VortexSupportMore) dps *= Math.Max(0, 1 + Own(stat) / 100d);
        dps *= more * GreaterEchoHitMore(repeat, Own) * ChargeDamageMore(player, spell: true)
            * PlayerHitDamage.SpellMore(Player, DamageTypeHistory.Cold) * Math.Max(0, 1 + Player("delirium_aura_damage_over_time_+%_final") / 100d);
        var damage = new SkillDamageContext(SkillHitKind.Spell, player).OverTime(new(DamageType.Cold, dps, seconds));
        int radius = SkillRadius(conn, own, true, effect, repeat, throughBolt, secondary: true);
        double rate = Own("base_deal_no_damage") != 0 || Get("deal_no_cold_damage_over_time") != 0 || DamageStats.Prohibited(DamageType.Cold, Own)
            ? 0 : damage.DamagePerSecond;
        session.PlaceVortexGround(centre, radius, seconds, rate, Get, groundArt);
    }

    sealed record VortexField(ProjectileSession Owner, (int X, int Y) Centre, int Radius, long End, double Dps, Func<string, int> Stats)
    {
        public bool Contains((int X, int Y) cell)
        {
            double dx = (double)Centre.X - cell.X, dy = (double)Centre.Y - cell.Y;
            return dx * dx + dy * dy <= (double)Radius * Radius;
        }
    }

    // The instance's fields share a clock and its Monsters lock. One timer settles only the strongest Vortex on each
    // monster, including fields from another caster. Its owner receives the kill. A retiring clock hands the timer on.
    static readonly RuleTable<SortedDictionary<uint, SpawnedMonster>, VortexGround> VortexGrounds = new();

    sealed class VortexGround(SortedDictionary<uint, SpawnedMonster> monsters)
    {
        sealed class Exposure(uint id, (int X, int Y) cell)
        {
            public readonly uint Id = id;
            public (int X, int Y) Cell = cell;
            public double Carry;
        }
        readonly List<VortexField> fields = [];
        readonly Dictionary<SpawnedMonster, Exposure> samples = new(ReferenceEqualityComparer.Instance);
        ProjectileSession? driver;
        long through, timer;

        public void Add(ProjectileSession owner, (int, int) centre, int radius, long duration, double dps, Func<string, int> stats)
        {
            if (fields.Count == 0) through = owner.Runtime.Now;
            else Advance(owner.Runtime.Now);
            fields.Add(new(owner, centre, radius, through + duration, dps, stats));
            Sample(through, through);
            if (driver is null) Arm();
        }

        public void Remove(ProjectileSession owner)
        {
            Advance(Math.Max(owner.Runtime.Now, ProjectileRuntime.Time(owner.Conn.Clock.Elapsed.TotalSeconds)));
            if (fields.RemoveAll(f => ReferenceEquals(f.Owner, owner)) > 0) Sample(through, through);
            if (ReferenceEquals(owner, driver)) Arm();
        }

        void Arm()
        {
            long ticket = ++timer;
            driver = fields.Count == 0 ? null : fields[0].Owner;
            if (driver is not { } clock) return;
            long next = Math.Min(through + ProjectileRuntime.QuantaPerSecond / 10, fields.Min(f => f.End));
            clock.Runtime.Schedule(Math.Max(clock.Runtime.Now, next), () =>
            {
                if (ticket != timer) return;
                Advance(clock.Runtime.Now);
                Arm();
            });
        }

        public void Advance(long now)
        {
            while (through < now && fields.Count > 0)
            {
                long end = Math.Min(now, fields.Min(f => f.End));
                Sample(through, end);
                through = end;
                if (fields.RemoveAll(f => f.End <= end) > 0) Sample(end, end);
            }
            through = Math.Max(through, now);
        }

        static double Seconds(long time) => time / (double)ProjectileRuntime.QuantaPerSecond;

        void Sample(long from, long until)
        {
            double start = Seconds(from), end = Seconds(until);
            foreach (var (monster, exposure) in samples.ToArray())
            {
                if (fields.Count != 0 && monsters.TryGetValue(exposure.Id, out var current) && ReferenceEquals(monster, current)
                    && monster is { Hostile: true, Dead: false, Life: > 0, OwnerId: null } && !monster.Invulnerable) continue;
                monster.Ailments.SetChillingArea(start, start, 0);
                samples.Remove(monster);
            }
            if (fields.Count == 0) return;
            foreach (var (id, monster) in monsters.ToArray())
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null } || monster.Invulnerable) continue;
                var cell = monster.Walk.Position(end);
                if (samples.TryGetValue(monster, out var exposure) && end > start)
                {
                    var position = exposure.Cell;
                    double at = start;
                    if (monster.Walk.MovingSeconds(start, end) > 0)
                    {
                        foreach (var step in monster.Walk.Steps(start, end))
                        {
                            Charge(position, at, step.At);
                            (position, at) = (step.Cell, step.At);
                        }
                    }
                    else if (position != cell)
                    {
                        at = Math.Clamp(monster.Walk.ArrivesAt, start, end);
                        Charge(position, start, at);
                        position = cell;
                    }
                    Charge(position, at, end);

                    void Charge((int X, int Y) point, double a, double b)
                    {
                        if (b <= a || monster.Life == 0 || monster.Invulnerable) return;
                        VortexField? best = null;
                        foreach (var field in fields)
                            if (field.Dps > (best?.Dps ?? 0) && field.Contains(point)) best = field;
                        if (best is null) return;
                        double total = exposure.Carry + best.Dps * (b - a) * MonsterDotTaken(monster, DamageType.Cold, a);
                        double whole = Math.Floor(total);
                        exposure.Carry = total - whole;
                        if (whole >= 1 && best.Owner.GroundDamage(id, monster, (int)Math.Min(int.MaxValue, whole), ProjectileRuntime.Time(b)))
                            QueueVortexKill(best.Owner, id, monster, b);
                    }
                }
                if (exposure is null) samples.Add(monster, new(id, cell));
                else exposure.Cell = cell;
                double chill = 0, expires = end;
                foreach (var field in fields)
                {
                    if (field.End <= until || !field.Contains(cell) || monster.Dead) continue;
                    double magnitude = monster.Ailments.ChillingAreaMagnitude(field.Stats, name => monster.Effects[name]);
                    if (magnitude > chill) (chill, expires) = (magnitude, Seconds(field.End));
                    else if (magnitude == chill) expires = Math.Max(expires, Seconds(field.End));
                }
                monster.Ailments.SetChillingArea(end, expires, chill);
            }
        }
    }

    // A monster can die on the final fraction when its caster leaves. Bind its ordinary DoT kill to the old
    // instance and caster and finish it before player-leaving completes and saves the character. Only socket output
    // depends on the flight session; the XP gate must be taken outside the shared Monsters lock.
    sealed class VortexKills { public Task Pending = Task.CompletedTask; }
    static readonly RuleTable<ClientState, VortexKills> VortexKillQueues = new();

    static void QueueVortexKill(ProjectileSession owner, uint id, SpawnedMonster monster, double at)
    {
        var conn = owner.Conn;
        StopCorpseBlocking(owner.remote, owner.stream, owner.ciphers, conn, id, monster);
        var queue = VortexKillQueues.GetValue(conn, _ => new());
        var previous = queue.Pending;
        queue.Pending = Task.Run(async () =>
        {
            try { await previous; }
            finally
            {
                lock (LevelUpdatesFor(conn).Gate)
                lock (conn.Monsters)
                    CommitAcceptedKillLocked(conn, id, monster, at, playerKill: true);
            }
        });
    }

    internal static Task FinishVortexGround(ClientState conn)
    {
        lock (conn.Monsters)
        {
            conn.ProjectileRuntime?.ClearVortex();
            return VortexKillQueues.TryGetValue(conn, out var kills) ? kills.Pending : Task.CompletedTask;
        }
    }

    public sealed partial class ProjectileSession
    {
        internal readonly HashSet<long> ReservedVortexBolts = [];
        VortexGround? ownedGround;

        internal void SettleVortexGround()
        {
            if (VortexGrounds.TryGetValue(Conn.Monsters, out var ground)) ground.Advance(Runtime.Now);
        }

        internal void PlaceVortexGround((int X, int Y) centre, int radius, double seconds, double dps, Func<string, int> stats, int groundArt)
        {
            long duration = ProjectileRuntime.Time(seconds);
            if (duration <= 0 || radius <= 0) return;
            ownedGround ??= VortexGrounds.GetValue(Conn.Monsters, monsters => new(monsters));
            ownedGround.Add(this, centre, radius, duration, dps, stats);
            var (ao, nativeRadius) = VortexGroundArt.Value[groundArt];
            Effect(centre.X, centre.Y, ao, duration, radius, positionedFlags0: EffectObject.FixedRotation, artRadius: nativeRadius);
        }

        internal void ClearVortex()
        {
            ReservedVortexBolts.Clear();
            ownedGround?.Remove(this);
            ownedGround = null;
        }
    }
}
