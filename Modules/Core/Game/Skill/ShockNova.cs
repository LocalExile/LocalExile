using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool IsPlayerShockNova(string? effect) => effect is "ShockNova" or "ShockNovaAltX";

    readonly record struct ShockNovaPulse(int Inner, int Outer, SupportedGem Gem)
    {
        public bool Covers(double distance, int size) =>
            distance >= Math.Max(0, Inner - size) && distance <= Math.Abs(Outer + size);
    }

    // NewShockNova scales its three fixed radii by the same integer percentage. The disc clones the ring's
    // contact count before the parent increments it. The ordinary gem has one contact per repeat.
    static int ShockNovaRadiusPercent(Func<string, int> get, int ownBase, int repeat = 0)
    {
        int increased = ownBase + get("shock_nova_radius_+%") + repeat * (get(SkillArea.IncreasedPerRepeat)
            + get("shock_nova_skill_area_of_effect_+%_per_repeat") + get("shock_nova_skill_area_of_effect_+%_per_contact_point"));
        return SkillArea.RadiusPercent(name => name == SkillArea.BaseIncreased ? increased : get(name), spell: true);
    }

    static int ShockNovaScale(IReadOnlyDictionary<uint, int> stats, int repeat)
    {
        int Get(string name) => GolemStat(stats, name);
        return 100 + ShockNovaRadiusPercent(Get, Get(SkillArea.BaseIncreased), repeat);
    }

    static ShockNovaPulse ShockNovaHit(SupportedGem gem, IReadOnlyDictionary<uint, int> stats, bool ring, int repeat)
    {
        int Get(string name) => GolemStat(stats, name);
        int scale = ShockNovaScale(stats, repeat);
        int increase = Get("shock_nova_damage_+%") + (ring ? Get("shock_nova_ring_damage_+%") : 0);
        int shock = ring ? Get("shock_nova_ring_chance_to_shock_+%") : 0;
        if (increase != 0 || shock != 0)
        {
            var own = new Dictionary<uint, int>(gem.Stats);
            void Add(string name, int value)
            {
                uint key = Stats.Key(name)!.Value;
                own[key] = own.GetValueOrDefault(key) + value;
            }
            Add("damage_+%", increase);
            Add("base_chance_to_shock_%", shock);
            gem = gem with { Stats = own };
        }
        return new(ring ? 14 * scale / 100 : 0, (ring ? 26 : 33) * scale / 100, gem);
    }

    // Each Procession repeat traces from the caster to the previous accepted cell plus the original step.
    // Layer 1 terminates the line. A gap in layer 0 only excludes that cell as a landing point.
    static (int X, int Y) ProcessionGroundCell(AreaLayout layout, (int X, int Y) from, (int X, int Y) to)
    {
        var last = from;
        foreach (var cell in ClientPathfinder.Line(from, to))
        {
            if ((uint)cell.X >= (uint)layout.GridW || (uint)cell.Y >= (uint)layout.GridH
                || layout.Sight?.Invoke(cell.X, cell.Y) == 0) break;
            if (layout.WalkableFor(cell.X, cell.Y, 0)) last = cell;
        }
        return last;
    }

    static (int X, int Y) ProcessionStep((int X, int Y) from, (int X, int Y) start, float length, float facing)
    {
        if (start != from)
        {
            float dx = (long)start.X - from.X, dy = (long)start.Y - from.Y;
            float scale = length / MathF.Sqrt(dx * dx + dy * dy);
            return ((int)MathF.Floor(dx * scale + .5f), (int)MathF.Floor(dy * scale + .5f));
        }
        // A coincident centre takes the actor's retained rotation (0x14015d790).
        int Cells(float component) => (int)MathF.Floor(component * 10.869565f * length * .092f);
        return (Cells(MathF.Sin(facing)), Cells(-MathF.Cos(facing)));
    }

    static Task CastShockNova(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll critical, SacrificeUse sacrifice,
        int repeats, int seals, double sealMore, bool procession, bool selfTarget)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        var stored = aim;
        return ScheduleNovaReleases(session, id, gem.Effect, actionToken, cooldownUse, sacrifice, repeats, seals, Release,
            procession ? StartProcession : null);

        Action? StartProcession()
        {
            var supported = SupportedSkill(conn, id);
            var stats = SkillAreaStats(conn, supported.Stats, gem.Effect);
            double now = ProjectileWorldTime(conn);
            var from = conn.Walk.Position(now);
            bool cursor = GolemStat(stats, "nova_spells_cast_at_target_location") != 0;
            bool graft = GolemStat(stats, "nova_spells_cast_at_marked_target") != 0;
            stored = cursor ? ProcessionGroundCell(conn.Layout, from, aim)
                : graft ? MarkedNovaCentreLocked(conn, id, supported, now) ?? aim : from;
            if (!cursor && !graft && !selfTarget) FacePlayerAction(conn, from, aim, now, stats);
            if (repeats == 0) return null;
            int radius = 33 * ShockNovaScale(stats, 0) / 100;
            var step = ProcessionStep(from, stored, radius * .5f, conn.Walk.Rotation(now));
            return step == (0, 0) ? null : () => stored = ProcessionGroundCell(conn.Layout, conn.Walk.Position(ProjectileWorldTime(conn)),
                (Offset(stored.X, step.X), Offset(stored.Y, step.Y)));
        }

        static int Offset(int cell, int delta) => (int)Math.Clamp((long)cell + delta, int.MinValue, int.MaxValue);

        Task Release(int repeat, bool seal)
        {
            var repeatCentre = stored; // The detached disc retains this repeat's accepted cell.
            long discAt = session.Runtime.Now + ProjectileRuntime.QuantaPerSecond / 10;
            var ring = Hit(ring: true);
            return Task.WhenAll(ring, AtNovaTime(session, discAt, () => Hit(ring: false)));

            Task Hit(bool ring)
            {
                if (!session.Active || conn.PlayerLife == 0 || !sacrifice.Current) return Task.CompletedTask;
                var supported = SupportedSkill(conn, id);
                var stats = SkillAreaStats(conn, supported.Stats, gem.Effect);
                var pulse = ShockNovaHit(supported, stats, ring, repeat);
                double now = ProjectileWorldTime(conn);
                var centre = (procession && repeat > 0) || GolemStat(stats, "nova_spells_cast_at_target_location") != 0 ? repeatCentre
                    : MarkedNovaCentreLocked(conn, id, supported, now) ?? conn.Walk.Position(now);
                return AreaSkillHitForUse(null, remote, stream, ciphers, conn, id, gem, centre, centre, immediate: true,
                    sequence: sequence, criticalRoll: critical, sacrifice: sacrifice, damageMore: seal ? sealMore : 1,
                    repeat: repeat, finalSpellRepeat: repeat > 0 && repeat == repeats, radiusOverride: pulse.Outer,
                    scheduledSession: session, triggeredCurrent: () => session.Active && conn.PlayerLife > 0, shockNova: pulse);
            }
        }
    }
}
