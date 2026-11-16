using GameServer.Core.Projectiles;
using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;
using static Game;

/// <summary>
/// The ground a monster's shot leaves where it lands, which hurts the player standing in it for the skill's duration:
/// Lunaris's dark orb and the Shaper-influence frost bolt leave a vortex (GroundEffectTypes Maelstorm), a caustic or
/// Malachai mortar a degenerating patch. The damage is the skill's per-minute degeneration (AtlasBosses.DamageOverTime,
/// and a caustic cloud's own stat), through the shared typed ArenaHazards ground delivery:
/// grounds of one type do not stack, the strongest counts; grounds of different types add up.
/// </summary>
public static class MonsterGrounds
{
    /// <summary>What a shot's ground is: its GroundEffectTypes Id, radius in cells, seconds, damage a second before the
    /// player's resistances, the buff it shows and its art.</summary>
    internal sealed record Kind(string Type, double Radius, double Seconds, DamagePacket PerSecond, ushort? Buff,
        (string Ao, int ArtRadius)? Art);
    sealed record Ground(Kind Kind, (int X, int Y) At, double Until);
    sealed class State
    {
        public readonly List<Ground> Grounds = [];
        public readonly Dictionary<string, (ushort Instance, ushort Row)> Buffs = [];
        public readonly ArenaHazards.Exposure Exposure = new();
        public double Next;
    }
    static readonly RuleTable<ClientState, State> States = new();

    /// <summary>The ground a shot leaves where it lands, or null: the shots of MonsterShots' ground recipes whose skill
    /// deals damage over time for a duration.</summary>
    internal static Kind? Of(SkillCatalog.MonsterProjectileInfo shot, IReadOnlyDictionary<uint, int>? scaled = null)
    {
        if (shot.Recipe is not ("Maelstrom" or "AtlasVortex" or "GroundMortar" or "Lantern")) return null;
        int Get(string name) => MonsterShots.Stat(shot, name);
        // The damage by the monster's level (the skill's interpolated stats: Lunaris's 67425/min at 60), when known.
        var perSecond = AtlasBosses.DamageOverTime(scaled is null ? Get
            : name => Stats.Key(name) is uint key && scaled.TryGetValue(key, out int value) ? value : Get(name));
        if (Get("base_projectile_ground_caustic_cloud_damage_per_minute") / 60.0 is >= 1 and var caustic)
            perSecond = perSecond.Add(DamageType.Chaos, new DamageRange(Math.Floor(caustic), Math.Ceiling(caustic)));
        double seconds = Get("base_skill_effect_duration") / 1000.0 * Math.Max(0, 1 + Get("skill_effect_duration_+%") / 100.0);
        if (perSecond.IsZero || seconds <= 0) return null;
        // The ground's type is the one whose art variation the skill names; its art's size stands in for its radius (not
        // verified: no stat gives one), else the shot's landing area (Malachai's mortar names no art).
        string type = "Degen";
        ushort? buff = null;
        (string Ao, int ArtRadius)? art = null;
        int artVariation = 0;
        foreach (var (key, variation) in shot.SkillStats ?? new Dictionary<uint, int>())
            if (FlaskGrounds.TypeOfArtStat((int)key - 1) is { } ground)
            {
                (type, buff, art, artVariation) = (ground.Type, ground.Buff, FlaskGrounds.Art(ground.Type, variation), variation);
                break;
            }
        // A lantern names no art stat: its ground burns (GGG: a burning ground of 12 cells, with ground_fire_burn).
        if (shot.Recipe == "Lantern" && Stats.Key("ground_fire_art_variation") is uint fire && FlaskGrounds.TypeOfArtStat((int)fire - 1) is { } burning)
            (type, buff, art) = (burning.Type, burning.Buff, FlaskGrounds.Art(burning.Type, 0));
        // The art row's own buff, where it names one, is the one GGG shows (Lunaris's ground_maelstrom_chill_lunaris).
        if (art is not null && FlaskGrounds.ArtBuff(type, artVariation) is ushort own) buff = own;
        // Measured radii where the skill names none: Lunaris's damage zone 18 cells (GGG: the damage stops at 18 when the
        // player walks out), a lantern's 12 (its ground's create); others the art's size, else the landing area.
        double radius = Get("projectile_ground_caustic_cloud_radius") is > 0 and var statRadius ? statRadius
            : shot.Recipe == "Maelstrom" ? MonsterShots.AreaRadius(shot, 18)
            : shot.Recipe == "Lantern" ? MonsterShots.AreaRadius(shot, 12)
            : art is { ArtRadius: > 0 } drawn ? drawn.ArtRadius / WorldUnits.PerCell
            : MonsterShots.AreaRadius(shot, MonsterMortarRadius());
        return new(type, radius, seconds, perSecond, buff, art);
    }

    /// <summary>A monster's shot ended at <paramref name="at"/>: its ground, if it leaves one, starts there. Called by the
    /// projectile session under the connection's lock.</summary>
    public static void Landed(ProjectileSession session, HostileProjectileOffense shot, ProjectileVector at)
    {
        var conn = session.Conn;
        // The damage at the monster's level, by its rarity's more damage (GGG: Lunaris's ground is her level-60 value
        // x 1.7, MonsterUnique5's 70 %, without the unique's 33 % less).
        var scaled = shot.Attack.ReleaseEffect is int effect
            ? GolemData.Skill(SkillsFor(conn), effect, Math.Max(1, (int)shot.Monster.Level)).Stats : null;
        if (shot.Attack.Projectile is not { } fired || Of(fired, scaled) is not { } kind) return;
        kind = kind with { PerSecond = kind.PerSecond.Scale(1 + shot.Monster.Mods.Effects["monster_rarity_damage_+%_final"] / 100.0) };
        var cell = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
        var state = States.GetValue(conn, _ => new());
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (state.Grounds.Count == 0) state.Exposure.Last = now;
        state.Grounds.Add(new(kind, cell, now + kind.Seconds));
        // The server draws the ground (GGG sends Lunaris's darkness as its own object too, fading 0.96 s after its end).
        if (kind.Art is { } art)
            session.Effect(cell.Item1, cell.Item2, art.Ao, ProjectileRuntime.Time(kind.Seconds + (fired.Recipe == "Maelstrom" ? .96 : 0)), kind.Radius);
    }

    /// <summary>Every player tick: each ground type's degeneration on the player for the grounds they stand in, every
    /// 0.25 s.</summary>
    public static Task Tick(ClientState conn) => Step(conn, conn.Clock.Elapsed.TotalSeconds);

    internal static async Task Step(ClientState conn, double now)
    {
        if (!States.TryGetValue(conn, out var state)) return;
        Dictionary<string, ushort> buffs;
        lock (conn.Monsters)
        {
            if (now < state.Next || conn.Closed || conn.LeavingArea) return;
            state.Next = now + .25;
            state.Grounds.RemoveAll(g => g.Until <= now);
            var here = conn.Walk.Position(now);
            var immune = PlayerImmunities(conn);
            buffs = conn.PlayerLife == 0 || conn.Paused || InGracePeriod(conn) ? [] : state.Grounds
                .Where(g => Distance(g.At, here) <= g.Kind.Radius)
                .GroupBy(g => g.Kind.Type)
                .Select(group => group.MaxBy(g => Mitigated(conn, g.Kind.PerSecond, immune))!)
                .Where(g => g.Kind.Buff.HasValue)
                .ToDictionary(g => g.Kind.Type, g => g.Kind.Buff!.Value);
        }
        await ArenaHazards.Ground(conn, state.Exposure, now, (position, minion) =>
        {
            var result = DamagePacket.Empty;
            foreach (var group in state.Grounds.Where(g => Distance(g.At, position) <= g.Kind.Radius).GroupBy(g => g.Kind.Type))
            {
                var strongest = group.MaxBy(g => g.Kind.PerSecond.AverageTotal)!;
                // Ground degeneration is deterministic; retain its type and average through mitigation.
                foreach (var part in strongest.Kind.PerSecond.Components)
                    result = result.Add(part.Type, new(part.Range.Average, part.Range.Average));
            }
            return result;
        }, () => States.TryGetValue(conn, out var current) && ReferenceEquals(current, state));
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var (type, shown) in state.Buffs.ToArray())
            if (!buffs.TryGetValue(type, out var row) || row != shown.Row)
            {
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, shown.Instance), null);
                state.Buffs.Remove(type);
            }
        foreach (var (type, row) in buffs)
            if (!state.Buffs.ContainsKey(type))
            {
                ushort instance = conn.NextBuff++;
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceEntities.AddBuff(PlayerId, instance, row), null);
                state.Buffs[type] = (instance, row);
            }
        lock (conn.Monsters)
            if (state.Grounds.Count == 0 && state.Buffs.Count == 0) States.Remove(conn);
    }

    // The ground's damage a second after the player's resistances (degeneration ignores armour), none of an immune type.
    static double Mitigated(ClientState conn, DamagePacket perSecond, HashSet<DamageType> immune) =>
        perSecond.Components.Where(c => !immune.Contains(c.Type)).Sum(c => c.Type == DamageType.Physical ? c.Range.Average
            : DamageModel.Mitigate(c.Range.Average, Resistance(conn, c.Type), MaxResistance(conn, c.Type)));

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>The player leaves the area: its grounds go with it.</summary>
    public static Task Leaving(ClientState conn)
    {
        if (States.TryGetValue(conn, out var state))
        {
            States.Remove(conn);
        }
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { foreach (var pair in States.ToArray()) _ = Leaving(pair.Key); }
    }
}
