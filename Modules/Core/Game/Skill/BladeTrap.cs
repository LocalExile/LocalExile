using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;

public static partial class Game
{
    // The native weapon-trap action sweeps two opposite sectors every 100 ms and
    // keeps a shared 200 ms contact gate per target.
    const double BladeTrapStepSeconds = .1;
    const double BladeTrapContactSeconds = .2;

    sealed record BladeTrapSpin(double TurnsPerSecond, double Seconds, double Radius,
        PlacedSkills.DamageSnapshot? First, PlacedSkills.DamageSnapshot? Second);

    static readonly RuleTable<PlacedSkills.Placed, BladeTrapSpin> BladeTrapSpins = new();

    static BladeTrapSpin? CaptureBladeTrap(Player player, ushort skill, SupportedGem gem, string? effect,
        IReadOnlyDictionary<uint, int> characterStats, PlacedSkills.DamageSnapshot? main,
        Dictionary<uint, int> payload)
    {
        if (effect is not ("BladeTrap" or "BladeTrapAltX" or "BladeTrapAltY")) return null;
        var (mainFits, offFits) = HandsFit(player, gem.Effect, gem.Stats);
        bool dualWield = characterStats.GetValueOrDefault(Stat.IsDualWielding) != 0;
        int Get(uint key) => ActorScale.Saturate((long)payload.GetValueOrDefault(key) + characterStats.GetValueOrDefault(key));
        int speed = ActorScale.Saturate((long)Get(Stat.BaseWeaponTrapRotationSpeedPlusPct)
            + (dualWield ? Get(Stat.WeaponTrapRotationSpeedPlusPctIfDualWielding) : 0));
        double rate = 2 * Math.Max(0, 1 + speed / 100d);
        double turns = Math.Max(0, Get(Stat.BaseWeaponTrapTotalRotationPct)
            + (dualWield ? (long)Get(Stat.WeaponTrapTotalRotationPctIfDualWielding) : 0)) / 100d;
        double seconds = effect == "BladeTrapAltX" ? payload.GetValueOrDefault(Stat.SkillEffectDuration) / 1000d
            : rate > 0 ? turns / rate : 0;
        if (effect == "BladeTrapAltX") turns = seconds * rate;
        payload[Stat.WeaponTrapTotalRotationPct] = ActorScale.Saturate((long)Math.Round(turns * 100));
        payload[Stat.WeaponTrapRotationSpeedPlusPct] = speed;
        var off = offFits && OffhandWeaponFor(player) is { } weapon ? CapturePlacedDamage(player, skill, gem, weapon) : null;
        var first = mainFits ? main : off;
        return new(rate, seconds, SkillRadius(player, gem.Stats, spell: false, gem.Effect),
            first, off ?? first);
    }

    static void StartBladeTrap(Player player, PlacedSkills.Placed placed, BladeTrapSpin spin, bool currentSkill, double more)
    {
        if (spin.Seconds <= 0 || spin.TurnsPerSecond <= 0 || spin.Radius <= 0) return;
        var session = ProjectilesFor(player);
        var first = placed with { Offense = spin.First };
        var second = ReferenceEquals(spin.First, spin.Second) ? first : placed with { Offense = spin.Second };
        var firstTargets = new List<KeyValuePair<uint, SpawnedMonster>>();
        var secondTargets = new List<KeyValuePair<uint, SpawnedMonster>>();
        var readyAfter = new Dictionary<uint, long>();
        var runtime = session.Runtime;
        long activeAt = runtime.Now;
        long end = activeAt + ProjectileRuntime.Time(spin.Seconds);
        long interval = ProjectileRuntime.Time(BladeTrapStepSeconds);
        long hitInterval = ProjectileRuntime.Time(BladeTrapContactSeconds);
        long previous = activeAt;
        runtime.Schedule(Math.Min(end, activeAt + interval), Sweep);
        if (ServerTrace.Skills)
            Console.WriteLine($"[gateway] blade trap 0x{placed.Id:x} active seconds={spin.Seconds:0.###} "
                + $"rate={spin.TurnsPerSecond:0.###} radius={spin.Radius:0.###} time={activeAt}");

        void Sweep()
        {
            if (!session.Active || player.PlayerLife == 0) return;
            long at = runtime.Now;
            double now = at / (double)ProjectileRuntime.QuantaPerSecond;
            double angle = -Math.PI / 2 - (previous - activeAt) / (double)ProjectileRuntime.QuantaPerSecond
                * spin.TurnsPerSecond * Math.Tau;
            double sweep = (at - previous) / (double)ProjectileRuntime.QuantaPerSecond * spin.TurnsPerSecond * Math.Tau;
            firstTargets.Clear();
            secondTargets.Clear();
            foreach (var entry in player.Area.MonstersNear(placed.At, spin.Radius))
            {
                var (key, monster) = entry;
                if (monster is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false, WorldHidden: false }
                    || readyAfter.GetValueOrDefault(key) > at) continue;
                var position = monster.Walk.Position(now);
                var delta = (X: position.X - placed.At.X, Y: position.Y - placed.At.Y);
                if (Dist(position, placed.At) > spin.Radius) continue;
                double ahead = (angle - Math.Atan2(delta.Y, delta.X)) % Math.Tau;
                if (ahead < 0) ahead += Math.Tau;
                if (ahead <= sweep || delta == (0, 0)) firstTargets.Add(entry);
                else if (ahead >= Math.PI && ahead - Math.PI <= sweep) secondTargets.Add(entry);
                else continue;
                readyAfter[key] = at + hitInterval;
            }
            if (firstTargets.Count != 0 || secondTargets.Count != 0)
            {
                using var stats = new PlayerStatScope(player, fresh: true);
                var critical = currentSkill ? CaptureSkillCriticalRoll(player, placed.Skill) : null;
                if (firstTargets.Count > 0)
                    PlacedAreaDamageLocked(player, first, placed.At, spin.Radius, now, more, currentSkill,
                        spellDamage: false, sharedCritical: critical, selected: firstTargets);
                if (secondTargets.Count > 0)
                    PlacedAreaDamageLocked(player, second, placed.At, spin.Radius, now, more, currentSkill,
                        spellDamage: false, sharedCritical: critical, selected: secondTargets);
                if (ServerTrace.Skills)
                    Console.WriteLine($"[gateway] blade trap 0x{placed.Id:x} sweep time={at} first={firstTargets.Count} second={secondTargets.Count}");
            }
            previous = at;
            if (at < end) runtime.Schedule(Math.Min(end, at + interval), Sweep);
        }
    }
}
