using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string BonespireEffect = "TriggeredSupportBonespire";
    const uint BonespireChanceStat = Stat.SupportTriggerSeizeTheFleshOnWarcryChancePct;

    static void TriggerBonespireLocked(Player player, ushort skillId, SupportedGem parent, (int X, int Y) at, double now)
    {
        if (parent.Supports.FirstOrDefault(s => SupportHas(s, BonespireChanceStat)) is not { } support
            || !SameTriggerSource(parent, SupportedSkill(player, skillId))
            || SupportCompanionSourceLocked(player, parent, support, BonespireEffect, "HungryLoopSupportedByBonespire") is not { } source
            || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null
            || CooldownForSkill(player, source.Id) is not { } cooldown) return;

        var session = ProjectilesFor(player);
        if (!session.Active || player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea) return;
        var gem = SupportedSkill(player, source.Id);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        int limit = Own(Stat.GraftSkillUulnetBoneSpiresMaxSpiresAllowed);
        int count = Math.Min(limit, Own(Stat.GraftSkillUulnetolBoneSpiresNumOfSpiresToCreate));
        int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
        int burst = SkillRadius(player, Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), gem.Stats, spell: false, source.Effect);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), CharacterPlayerStats(player).GetValueOrDefault,
            Own, DurationKinds(player, source.Effect));
        if (count <= 0 || radius <= 0 || burst <= 0 || duration <= 0) return;

        int key = CooldownKey(player, source.Effect, source.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(parent.Stats.GetValueOrDefault(BonespireChanceStat), 100, AreaRng(player))
            || !state.TryUse(now, cooldown)) return;

        const string name = "Seize the Flesh";
        var damage = CaptureAreaAttack(player, source.Id, gem, AttackHand.Main, HandWeapon(player, AttackHand.Main), null, 1, 1, name);
        var use = (source.Id, source.Effect, Name: name, gem.Level, gem.Quality, source.Gem);
        var critical = CaptureSkillCriticalRoll(player, source.Id);
        ushort sequence = NextLockstepSequence(player);
        bool Active() => session.Active && player.PlayerLife > 0 && CurrentGameWork;
        player.CooldownSources[source.Effect] = source.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: true);
        session.Enqueue(() => SendNativeCooldownRefresh(player));

        var points = BonespirePoints(player, at, radius, count, now);
        long start = ProjectileRuntime.Time(now);
        if (ServerTrace.Skills)
            Console.WriteLine($"[gateway] {player.Connection.Remote} bonespire trigger at={start} count={points.Count} radius={radius} burst={burst}");
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            session.ScheduleContact(start + ProjectileRuntime.Time(.25 * i / count), Active, () =>
                session.PlaceBonespire(point, limit, duration, burst, () => Detonate(point)));
        }

        void Detonate((int X, int Y) point)
        {
            var hit = AreaSkillHitForUse(null, player, source.Id, use, point, point, immediate: true,
                sequence: sequence, melee: false, criticalRoll: critical, radiusOverride: burst,
                triggeredCurrent: Active, scheduledSession: session, cryExerted: false, allowMeleeSplash: false,
                nearbyOnly: true, retainedAttack: damage, targetFilter: (_, target) => BonespireLineClear(player, point, target));
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Seize the Flesh hit must not yield.");
            hit.GetAwaiter().GetResult();
        }
    }

    static bool BonespireLineClear(Player player, (int X, int Y) from, (int X, int Y) to) =>
        player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(from, to, sight, 1);

    static List<(int X, int Y)> BonespirePoints(Player player, (int X, int Y) at, int radius, int count, double now)
    {
        var random = AreaRng(player);
        var targets = player.Area.MonstersNear(at, radius).Where(e => e.Value is
                { Hostile: true, Dead: false, Life: > 0, WorldHidden: false, OwnerId: null })
            .Select(e => e.Value.Walk.Position(now)).Where(point => Dist(at, point) <= radius).ToArray();
        var points = new List<(int X, int Y)>(count);
        // The native action searches around enemies at 15 cells, then uses an open-ground pattern if it cannot place them.
        for (int attempt = 0; targets.Length > 0 && points.Count < count && attempt < 1000; attempt++)
            Add(targets[points.Count % targets.Length], 15, random.NextDouble() * Math.Tau);
        if (points.Count == count) return points;

        points.Clear();
        double rotation = random.NextDouble() * Math.Tau;
        int candidates = Math.Clamp((int)Math.Min(250, (double)radius * radius * .6 / 25), Math.Min(count, 250), 250);
        for (int i = 0; i < candidates && points.Count < count; i++)
            Add(at, radius * Math.Sqrt((i + .5) / candidates), rotation + i * (Math.Tau / (1 + Math.Sqrt(5)) * 2));
        return points;

        void Add((int X, int Y) origin, double distance, double angle)
        {
            var point = (X: origin.X + (int)Math.Round(Math.Cos(angle) * distance),
                Y: origin.Y + (int)Math.Round(Math.Sin(angle) * distance));
            if (player.Layout.WalkableFor(point.X, point.Y, 0) && !player.Barrier.Contains(point)
                && BonespireLineClear(player, origin, point) && points.All(previous => Dist(previous, point) >= 5))
                points.Add(point);
        }
    }

    public sealed partial class ProjectileSession
    {
        const string BonespireArt = "Metadata/Effects/Spells/monsters_effects/League_Chayula/GraftSystem/Active_UulNetol_02/bone_spike.ao";
        const string BonespireBurstArt = "Metadata/Effects/Spells/monsters_effects/League_Chayula/GraftSystem/Active_UulNetol_02/shatter_aoe.ao";
        sealed record Bonespire((int X, int Y) At, long Expires, int Radius, Action RemoveArt, Action Detonate);
        readonly List<Bonespire> bonespires = [];
        internal bool HasBonespires => bonespires.Count != 0;

        internal void PlaceBonespire((int X, int Y) at, int limit, double duration, int radius, Action detonate)
        {
            while (bonespires.Count >= limit) RemoveBonespire(bonespires[0], "limit");
            long lifetime = ProjectileRuntime.Time(duration);
            Action? remove = null;
            Effect(at.X, at.Y, BonespireArt, lifetime, captureRemoval: action => remove = action);
            var spire = new Bonespire(at, Runtime.Now + lifetime, radius, remove!, detonate);
            bonespires.Add(spire);
            if (ServerTrace.Skills)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} bonespire place at={Runtime.Now} cell={at} total={bonespires.Count}");
            Runtime.Schedule(spire.Expires, () => RemoveBonespire(spire, "expiry"));
        }

        void RemoveBonespire(Bonespire spire, string reason)
        {
            if (!bonespires.Remove(spire)) return;
            spire.RemoveArt();
            if (ServerTrace.Skills)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} bonespire remove reason={reason} at={Runtime.Now} cell={spire.At} total={bonespires.Count}");
        }

        internal void DetonateBonespires((int X, int Y) centre, double radius, double now)
        {
            long start = ProjectileRuntime.Time(now);
            foreach (var spire in bonespires.ToArray())
            {
                if (start >= spire.Expires || Dist(centre, spire.At) > radius || !BonespireLineClear(Player, centre, spire.At))
                    continue;
                RemoveBonespire(spire, "slam");
                ScheduleContact(start, () => Player.PlayerLife > 0, () =>
                {
                    Effect(spire.At.X, spire.At.Y, BonespireBurstArt, ProjectileRuntime.Time(.5), spire.Radius);
                    spire.Detonate();
                });
            }
        }

        void ClearBonespires()
        {
            while (bonespires.Count != 0) RemoveBonespire(bonespires[^1], "retire");
        }
    }
}
