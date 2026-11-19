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
        session.PlaceVortexGround(centre, radius, seconds, rate, SkillAilmentStats(player, own), groundArt);
    }

    sealed record VortexField(ProjectileSession Owner, MonsterElementalAilments.Circle Area, long End, double Dps,
        MonsterElementalAilments.GroundChill Chill);

    // The instance's fields share a clock and its Monsters lock. One timer settles only the strongest Vortex on each
    // monster, including fields from another caster. Its owner receives the kill. A retiring clock hands the timer on.
    static readonly RuleTable<SortedDictionary<uint, SpawnedMonster>, VortexGround> VortexGrounds = new();

    internal static void EnterChillingGroundLocked(ClientState conn, uint id, SpawnedMonster monster, double now)
    {
        foreach (var caster in conn.ChillingTrailCasters)
            foreach (var patch in caster.Reservations.Trail)
                if (now < patch.Until && (patch.Epoch is null || patch.Epoch == caster.ProjectileEpoch))
                    ObserveChilledGroundLocked(caster, patch, monster, Math.Max(now, patch.From));
        if (VortexGrounds.TryGetValue(conn.Monsters, out var ground)) ground.Enter(id, monster, now);
    }

    sealed class VortexGround(SortedDictionary<uint, SpawnedMonster> monsters)
    {
        sealed class Exposure(uint id, (int X, int Y) cell, double since)
        {
            public readonly uint Id = id;
            public readonly double Since = since;
            public (int X, int Y) Cell = cell;
            public double Carry;
            public MonsterModEffects? Effects;
            public int FieldVersion;
        }
        readonly List<VortexField> fields = [];
        readonly Dictionary<SpawnedMonster, Exposure> samples = new(ReferenceEqualityComparer.Instance);
        ProjectileSession? driver;
        long through, timer;
        int fieldVersion;
        void BeforeChillChange(double at) => Advance(ProjectileRuntime.Time(at));

        public void Add(ProjectileSession owner, (int, int) centre, int radius, long duration, double dps, Func<string, int> stats)
        {
            if (fields.Count == 0) through = owner.Runtime.Now;
            else Advance(owner.Runtime.Now);
            fields.Add(new(owner, new(centre, radius), through + duration, dps, MonsterElementalAilments.GroundChill.From(stats)));
            fieldVersion++;
            Sample(through, through);
            if (driver is null) Arm();
        }

        public void Remove(ProjectileSession owner)
        {
            Advance(Math.Max(owner.Runtime.Now, ProjectileRuntime.Time(owner.Conn.Clock.Elapsed.TotalSeconds)));
            RemoveFields(f => ReferenceEquals(f.Owner, owner));
            if (ReferenceEquals(owner, driver)) Arm();
        }

        void RemoveFields(Predicate<VortexField> ended)
        {
            for (int i = fields.Count - 1; i >= 0; i--)
            {
                var field = fields[i];
                if (!ended(field)) continue;
                foreach (var monster in samples.Keys) monster.Ailments.RemoveChillingArea(field);
                fields.RemoveAt(i);
            }
            if (fields.Count == 0)
            {
                foreach (var monster in samples.Keys) monster.Ailments.ChillChanging -= BeforeChillChange;
                samples.Clear();
            }
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
                if (fields.Any(f => f.End <= end)) RemoveFields(f => f.End <= end);
            }
            through = Math.Max(through, now);
        }

        static double Seconds(long time) => time / (double)ProjectileRuntime.QuantaPerSecond;

        static bool CanDamage(SpawnedMonster m) => m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null } && !m.Invulnerable;

        public void Enter(uint id, SpawnedMonster monster, double now)
        {
            if (fields.Count == 0 || !CanDamage(monster)) return;
            double at = Math.Max(now, Seconds(through));
            Track(id, monster, monster.Walk.Position(at), at);
        }

        void Track(uint id, SpawnedMonster monster, (int X, int Y) cell, double at)
        {
            var exposure = new Exposure(id, cell, at);
            samples.Add(monster, exposure);
            monster.Ailments.ChillChanging += BeforeChillChange;
            ObserveFields(monster, exposure, at);
        }

        void ObserveFields(SpawnedMonster monster, Exposure exposure, double at)
        {
            var effects = monster.Effects;
            // Position is checked at contact. Reproject the ground only for a new field or a changed target.
            if (exposure.FieldVersion == fieldVersion && ReferenceEquals(exposure.Effects, effects)) return;
            exposure.FieldVersion = fieldVersion;
            exposure.Effects = effects;
            BindFields(monster, at, effects);
        }

        void BindFields(SpawnedMonster monster, double at, MonsterModEffects effects)
        {
            int Target(string name) => effects[name];
            foreach (var field in fields)
            {
                double end = Seconds(field.End);
                if (at >= end) continue;
                double magnitude = monster.Ailments.ChillingAreaMagnitude(field.Chill, Target);
                monster.Ailments.SetChillingArea(field, at, end, magnitude, field.Chill.ColdTaken, monster.Walk, field.Area);
            }
        }

        void Untrack(SpawnedMonster monster)
        {
            foreach (var field in fields) monster.Ailments.RemoveChillingArea(field);
            monster.Ailments.ChillChanging -= BeforeChillChange;
            samples.Remove(monster);
        }

        void Sample(long from, long until)
        {
            double end = Seconds(until);
            foreach (var (monster, exposure) in samples.ToArray())
            {
                if (fields.Count != 0 && monsters.TryGetValue(exposure.Id, out var current) && ReferenceEquals(monster, current)
                    && CanDamage(monster)) continue;
                Untrack(monster);
            }
            if (fields.Count == 0) return;
            foreach (var (id, monster) in monsters.ToArray())
            {
                if (!CanDamage(monster)) continue;
                var cell = monster.Walk.Position(end);
                if (!samples.TryGetValue(monster, out var exposure))
                {
                    Track(id, monster, cell, end);
                    continue;
                }
                // The driver's last tick can precede this monster's admission by another player.
                double start = Math.Max(Seconds(from), exposure.Since);
                ObserveFields(monster, exposure, start);
                if (end < start) continue;
                if (end > start)
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
                            if (field.Dps > (best?.Dps ?? 0) && field.Area.Contains(point)) best = field;
                        if (best is null) return;
                        double total = exposure.Carry;
                        while (a < b)
                        {
                            double next = monster.Ailments.NextChillChange(a, b);
                            total += best.Dps * (next - a) * MonsterDotTaken(monster, DamageType.Cold, a, point);
                            a = next;
                        }
                        double whole = Math.Floor(total);
                        exposure.Carry = total - whole;
                        if (whole >= 1 && best.Owner.GroundDamage(id, monster, (int)Math.Min(int.MaxValue, whole), ProjectileRuntime.Time(b)))
                            QueueVortexKill(best.Owner, id, monster, b);
                    }
                }
                exposure.Cell = cell;
                if (!CanDamage(monster)) Untrack(monster);
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

    static void SettleChillingGround(ClientState conn, double now)
    {
        if (VortexGrounds.TryGetValue(conn.Monsters, out var ground)) ground.Advance(ProjectileRuntime.Time(now));
    }

    internal static Task FinishVortexGround(ClientState conn)
    {
        lock (conn.Monsters)
        {
            double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
            SettleChillingGround(conn, now);
            ClearChillingTrailsLocked(conn);
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
