using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
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

    static ShockNovaPulse ShockNovaHit(SupportedGem gem, IReadOnlyDictionary<uint, int> stats, bool ring, int repeat)
    {
        int Get(string name) => GolemStat(stats, name);
        int scale = 100 + ShockNovaRadiusPercent(Get, Get(SkillArea.BaseIncreased), repeat);
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

    static Task CastShockNova(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll critical, SacrificeUse sacrifice,
        int repeats, int seals, double sealMore)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        return ScheduleNovaReleases(session, id, gem.Effect, actionToken, cooldownUse, sacrifice, repeats, seals, Release);

        Task Release(int repeat, bool seal)
        {
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
                var centre = GolemStat(stats, "nova_spells_cast_at_target_location") != 0 ? aim
                    : MarkedNovaCentreLocked(conn, id, supported, now) ?? conn.Walk.Position(now);
                return AreaSkillHitForUse(null, remote, stream, ciphers, conn, id, gem, centre, centre, immediate: true,
                    sequence: sequence, criticalRoll: critical, sacrifice: sacrifice, damageMore: seal ? sealMore : 1,
                    repeat: repeat, finalSpellRepeat: repeat > 0 && repeat == repeats, radiusOverride: pulse.Outer,
                    scheduledSession: session, triggeredCurrent: () => session.Active && conn.PlayerLife > 0, shockNova: pulse);
            }
        }
    }
}
