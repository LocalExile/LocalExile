using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>A ground effect's art: GroundEffects' row for a GroundEffectTypes Id and variation (<c>__col1</c>; the
/// newest art, the highest <c>__col2</c>). Its AOFile lists the pieces the client lays, each drawn for
/// <c>__col4</c> world units (120 for Caustic Arrow's five).</summary>
public sealed record GroundArt(string[] Pieces, int PieceRadius)
{
    static readonly Lazy<Dictionary<(string, int), GroundArt>> Rows = new(() =>
    {
        var rows = new Dictionary<(string, int), (int Version, GroundArt Art)>();
        IReadOnlyList<ClientRow>? types = ClientTables.TableOrNull("GroundEffectTypes"), effects = ClientTables.TableOrNull("GroundEffects");
        if (types is null || effects is null) return new Dictionary<(string, int), GroundArt>();
        foreach (var r in effects)
            if (r.IntOrNull("GroundEffectTypesKey") is int type && type >= 0 && type < types.Count
                && types[type].Text("Id") is { Length: > 0 } id && r.IntOrNull("__col1") is int variation
                && r.Texts("AOFile") is { Length: > 0 } aos && r.IntOrNull("__col4") is int size and > 0
                && (!rows.TryGetValue((id, variation), out var have) || (r.IntOrNull("__col2") ?? 0) > have.Version))
                rows[(id, variation)] = (r.IntOrNull("__col2") ?? 0, new GroundArt([.. aos.Distinct()], size));
        return rows.ToDictionary(kv => kv.Key, kv => kv.Value.Art);
    });

    public static GroundArt? Of(string type, int variation) => Rows.Value.GetValueOrDefault((type, variation));
}

public static partial class Game
{
    // The stretch of time a monster has already taken caustic ground damage for, and the fraction carried to its next tick.
    // shortcut: the ground that ticks first charges the stretch, not the strongest one (a mirage's or a totem's ground
    // under the player's); keep the highest rate per stretch if such overlaps matter.
    sealed class CausticCover
    {
        public long Until;
        public double Fraction;
    }

    public sealed partial class ProjectileSession
    {
        // Caustic Arrow's own caustic ground: GroundEffects' CausticCloud variation 2 (five pieces of 120 world units).
        // A worn skill effect names another through ground_caustic_art_variation (Celestial 7, Oblivion 13).
        const int CausticArrowGroundVariation = 2;
        /// <summary>The clips of a ground piece: the one it appears with and its length, its loop and the row's end
        /// effect with its length, as indices in the piece's rig (.amd, read from the client 2026-11-17).
        /// caustic_arrow_ground/rig.amd: fadein 0 (0.366 s), fadeout 1 (1.5 s), loop 2; the Celestial ground's rig is the
        /// same size. poison_arrow/rig.amd (Tentacles): cloudbegone 0 (2.083 s), cloudemerge 1 (0.833 s), cloudstuff 2.
        /// Null for a rig not read yet: its piece is drawn as it is made and removed at the end.</summary>
        static (uint Start, double StartSeconds, uint Loop, uint End, double EndSeconds)? GroundClips(string ao) =>
            ao.Contains("/caustic_arrow_ground/", StringComparison.OrdinalIgnoreCase)
                || ao.Contains("/caustic_arrow/celestial/celestial_caustic_ground_", StringComparison.OrdinalIgnoreCase)
                ? (0u, .366, 2u, 1u, 1.5)
            : ao.EndsWith("/Microtransactions/Spells/poison_arrow/rig.ao", StringComparison.OrdinalIgnoreCase)
                ? (1u, .833, 2u, 0u, 2.083)
            : null;

        readonly ConditionalWeakTable<SpawnedMonster, CausticCover> causticCover = new();

        /// <summary>Caustic Arrow leaves caustic ground where its arrow stops: on an enemy, on terrain or at the end of its
        /// flight (base_skill_effect_duration, 2 s; base_chaos_damage_to_deal_per_minute). The radius is 20 cells (PoE
        /// Wiki "Caustic Arrow") plus the gem's active_skill_base_radius_+. A skill without that damage (Caustic Arrow of
        /// Poison) leaves none. The art is the GroundEffects row's pieces laid over the radius (<see cref="GroundPieces"/>):
        /// the client's own ground effect is not sent.</summary>
        void CausticGroundAtEnd(ProjectileEvent value)
        {
            var p = value.Projectile;
            if (value.Kind != ProjectileEventKind.End || p.Use.Payload is not ProjectileOffense shot
                || value.Reason is not (ProjectileEndReason.Enemy or ProjectileEndReason.Terrain
                    or ProjectileEndReason.Distance or ProjectileEndReason.Lifetime or ProjectileEndReason.Landing)
                || SkillsFor(Conn).EffectId(shot.Effect)?.StartsWith("CausticArrow", StringComparison.Ordinal) != true) return;
            var own = shot.SkillStats;
            var player = CharacterPlayerStats(Conn);
            int Own(string key) => Stats.Key(key) is uint stat ? own.GetValueOrDefault(stat) : 0;
            int Player(string key) => Stats.Key(key) is uint stat ? player.GetValueOrDefault(stat) : 0;
            int Get(string key) => Own(key) + Player(key);
            if (Own("base_chaos_damage_to_deal_per_minute") <= 0) return;
            double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
            if (duration <= 0) return;
            double dps = new SkillDamageContext(SkillHitKind.Attack, shot.ActualCaster ?? player).Suppressed
                ? 0 : ChaosGroundDps(Get, Own, "caustic_arrow_damage_+%") * (shot.Mirage ? MirageDamageFactor(own) : 1);
            int worn = SkillCosmeticStat(Conn, shot.Gem, "ground_caustic_art_variation");
            PlaceCausticGround(value.Position, SkillArea.Radius(20, Get, false), duration, dps,
                (worn > 0 ? GroundArt.Of("CausticCloud", worn) : null) ?? GroundArt.Of("CausticCloud", CausticArrowGroundVariation));
        }

        /// <summary>Where a ground's pieces go: one in the middle and one ring of at most six around it out to the radius,
        /// each piece at its own size (±10%), a little off its place and turned its own way. A ground no larger than a
        /// piece is one piece scaled to it. Radii are in cells.</summary>
        internal static List<(ProjectileVector Offset, double Radius, float Rotation)> GroundPieces(double radius,
            double piece, Random random)
        {
            var pieces = new List<(ProjectileVector, double, float)>();
            float Turn() => (float)(random.NextDouble() * Math.Tau);
            // At most seven objects per ground, however large: every piece is a server object with its own packets, and
            // a Caustic Arrow build with GMP lays several grounds per attack. Larger grounds get larger pieces instead
            // (radius / 2.85 keeps a ring of six closing on the middle piece and on each other).
            double size = Math.Max(piece, radius / 2.85);
            if (piece <= 0 || radius <= size * 1.15)
            {
                pieces.Add((default, radius, Turn()));
                return pieces;
            }
            pieces.Add((default, size, Turn()));
            // The art fades towards its rim, so the ring sits 0.85 of a piece inside the radius.
            double distance = radius - size * .85, start = random.NextDouble() * Math.Tau;
            int count = Math.Clamp((int)Math.Ceiling(Math.Tau * distance / (size * 1.3)), 3, 6);
            for (int i = 0; i < count; i++)
                pieces.Add((new ProjectileVector(distance * (.92 + random.NextDouble() * .16), 0)
                        .Rotate(start + Math.Tau * (i + (random.NextDouble() - .5) * .3) / count),
                    size * (.9 + random.NextDouble() * .2), Turn()));
            return pieces;
        }

        /// <summary>A caustic ground: chaos damage over time to the hostile monsters standing on it. Caustic grounds do
        /// not stack, so a monster on several takes each stretch of time once (<see cref="CausticCover"/>).</summary>
        internal void PlaceCausticGround(ProjectileVector centre, double radius, double duration, double dps, GroundArt? art)
        {
            if (art is { Pieces.Length: > 0 })
            {
                // The pieces are dealt out in a shuffled order, so neighbours do not show the same one.
                var order = art.Pieces.OrderBy(_ => Random.Shared.Next()).ToArray();
                int next = 0;
                foreach (var (offset, size, turn) in GroundPieces(radius, art.PieceRadius * (23d / 250), Random.Shared))
                {
                    string ao = order[next++ % order.Length];
                    var clips = GroundClips(ao);
                    Effect((int)Math.Round(centre.X + offset.X), (int)Math.Round(centre.Y + offset.Y), ao,
                        ProjectileRuntime.Time(duration), size, artRadius: Art.ArtRadius(ao) ?? art.PieceRadius, rotation: turn,
                        startClip: clips?.Start,
                        thenClip: clips is { } c ? (c.Loop, ProjectileRuntime.Time(c.StartSeconds)) : null,
                        endClip: clips is { } e ? (e.End, ProjectileRuntime.Time(e.EndSeconds)) : null);
                }
            }
            long previous = Runtime.Now, end = previous + Math.Max(1, ProjectileRuntime.Time(duration));
            void Tick()
            {
                long now = Runtime.Now;
                double at = now / (double)ProjectileRuntime.QuantaPerSecond;
                foreach (var (id, monster) in Conn.Monsters.ToArray())
                {
                    if (!monster.Hostile || monster.Dead || monster.Life == 0
                        || (Vector(monster.Walk.Position(at)) - centre).Length > radius) continue;
                    var cover = causticCover.GetValue(monster, _ => new CausticCover());
                    long from = Math.Max(previous, cover.Until);
                    if (now <= from) continue;
                    cover.Until = now;
                    double total = cover.Fraction + (now - from) / (double)ProjectileRuntime.QuantaPerSecond * dps
                        * MonsterDotTaken(monster, DamageType.Chaos, at);
                    int damage = (int)Math.Clamp(Math.Floor(total), 0, int.MaxValue);
                    cover.Fraction = total - Math.Floor(total);
                    if (damage == 0) continue;
                    var (life, stage) = TakeOwnerDamageLocked(Conn, monster, damage);
                    monster.Life = life;
                    monster.Aggro = life > 0;
                    if (stage) CommitProjectileBossStage(this, id, monster, now);
                    World(id, InstanceProtocol.LifeUpdate(id, life, u2c: DotDeathKind(monster, life)));
                    if (life == 0)
                    {
                        monster.Dead = true;
                        monster.LastHitBy = Conn;
                        CollectVaalSoulAtDeathLocked(Conn, monster);
                        Enqueue(() => DotKilled(remote, stream, ciphers, Conn, id, monster));
                    }
                }
                previous = now;
                if (now < end) Runtime.Schedule(Math.Min(end, now + ProjectileRuntime.QuantaPerSecond / 10), Tick);
            }
            Tick();
        }
    }
}
