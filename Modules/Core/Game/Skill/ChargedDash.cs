using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort ChargedDashAction = 0x2d76;
    const double ChargedDashWaveStep = .03125;
    static readonly RuleTable<Player, ChargedDashChannel> ChargedDashChannels = new();

    internal sealed class ChargedDashPath((int X, int Y) origin, int travelPct)
    {
        readonly double reach = Math.Max(0, 60 + 60L * travelPct / 100);
        readonly double budget = Math.Max(0, 90 + 90L * travelPct / 100);
        readonly List<(int X, int Y)> cells = [origin];
        readonly List<(int X, int Y)> wave = [origin];
        double x = origin.X + .5, y = origin.Y + .5;
        double nextSample = 10;
        public double Distance { get; private set; }
        public bool Stopped { get; private set; }
        public (int X, int Y) End => ((int)Math.Floor(x), (int)Math.Floor(y));
        public double Progress => reach > 0 ? Math.Min(1, (Distance + 1e-8) / reach) : 0;

        internal void Advance((int X, int Y) aim, double advance, AreaLayout layout,
            IReadOnlySet<(int X, int Y)> barriers)
        {
            if (Stopped || advance <= 0) return;
            double dx = (double)aim.X - origin.X, dy = (double)aim.Y - origin.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length == 0) return;
            dx /= length;
            dy /= length;
            double offsetX = x - origin.X - .5, offsetY = y - origin.Y - .5;
            double along = offsetX * dx + offsetY * dy;
            double remainingRadius = -along + Math.Sqrt(Math.Max(0,
                along * along + reach * reach - offsetX * offsetX - offsetY * offsetY));
            double distance = Math.Max(0, Math.Min(advance, Math.Min(budget - Distance, remainingRadius)));
            Stopped = distance >= Math.Min(budget - Distance, remainingRadius) - 1e-8;
            var desired = ((int)Math.Floor(x + dx * distance), (int)Math.Floor(y + dy * distance));
            var last = End;
            foreach (var cell in ClientPathfinder.Line(last, desired).Skip(1))
            {
                if (cell.X < 0 || cell.Y < 0 || cell.X >= layout.GridW || cell.Y >= layout.GridH
                    || barriers.Contains(cell) || (layout.Sight is { } sight ? sight(cell.X, cell.Y) < 1
                        : !layout.WalkableFor(cell.X, cell.Y, 0)))
                {
                    double safe = (last.X + .5 - x) * dx + (last.Y + .5 - y) * dy;
                    distance = Math.Clamp(safe, 0, distance);
                    Stopped = true;
                    break;
                }
                cells.Add(cell);
                last = cell;
            }
            while (nextSample <= Distance + distance)
            {
                double step = nextSample - Distance;
                wave.Add(((int)Math.Floor(x + dx * step), (int)Math.Floor(y + dy * step)));
                nextSample += 10;
            }
            x += dx * distance;
            y += dy * distance;
            Distance += distance;
        }

        // Releasing copies the recorded contacts. Only a projection that has stopped records its unsampled endpoint.
        internal (int X, int Y)[] WavePoints() => Stopped && wave[^1] != End ? [.. wave, End] : [.. wave];

        internal (int X, int Y) Landing(AreaLayout layout, IReadOnlySet<(int X, int Y)> barriers,
            IReadOnlyList<ClientPathfinder.Blocker> blockers)
        {
            for (int i = cells.Count - 1; i > 0; i--)
            {
                var cell = cells[i];
                if (layout.WalkableFor(cell.X, cell.Y, LivingCollision.PlayerSize)
                    && !barriers.Contains(cell) && !ClientPathfinder.ObjectBlocks(blockers, cell.X, cell.Y,
                        LivingCollision.PlayerSize + 1, LivingCollision.PlayerSize + 4))
                    return cell;
            }
            return origin;
        }
    }

    sealed class ChargedDashChannel(SupportedGem source, ushort skill, ushort sequence, int action, int generation,
        int areaEpoch, int interruption, double at, (int X, int Y) origin, (int X, int Y) aim, int travelPct)
    {
        public SupportedGem Source { get; } = source;
        public ushort Skill { get; } = skill;
        public ushort Sequence { get; } = sequence;
        public int Action { get; } = action;
        public int Generation { get; } = generation;
        public int AreaEpoch { get; } = areaEpoch;
        public int Interruption { get; } = interruption;
        public ChargedDashPath Path { get; } = new(origin, travelPct);
        public (int X, int Y) Aim = aim;
        public double At = at, StageDistance;
        public int PaidSteps, Stages = 1;
    }

    static bool IsChargedDash(Player player, int effect) => SkillsFor(player).ActionHash(effect) == ChargedDashAction
        && SkillsFor(player).EffectId(effect) is "ChargedDash" or "ChargedDashAltX";

    static ChargedDashChannel? CurrentChargedDash(Player player) =>
        ChargedDashChannels.TryGetValue(player, out var state) && state.Generation == player.ChannelGeneration
            && state.Skill == player.ChannelSkill && state.Action == player.ActionCount ? state : null;

    static void BeginChargedDash(Player player, SupportedGem source, ushort skill, ushort sequence, int action,
        int generation, (int X, int Y) from, (int X, int Y) aim, double at)
    {
        int travel = ActorScale.Saturate((long)source.Stats.GetValueOrDefault(Stat.SkillTravelDistancePlusPct)
            + CharacterPlayerStats(player).GetValueOrDefault(Stat.SkillTravelDistancePlusPct));
        ChargedDashChannels.AddOrUpdate(player, new(source, skill, sequence, action, generation, player.ProjectileEpoch,
            player.ActionInterruptionGeneration, at, from, aim, travel));
    }

    static void AdvanceChargedDash(Player player, ChargedDashChannel dash, double at)
    {
        if (at <= dash.At) return;
        var stats = dash.Source.Stats;
        double movement = (1 + stats.GetValueOrDefault(Stat.ChargedDashMovementSpeedPlusPctFinal) / 100d)
            * (1 + stats.GetValueOrDefault(Stat.ChargedDashSkillInherentMovementSpeedPlusPctFinal) / 100d);
        dash.Path.Advance(dash.Aim, (at - dash.At) * player.Walk.Speed * Math.Max(0, movement), player.Layout, player.Barrier);
        dash.At = at;
    }

    static bool AimChargedDash(Player player, (int X, int Y) aim, double at)
    {
        var dash = CurrentChargedDash(player);
        if (dash is null || dash.Aim == aim) return false;
        AdvanceChargedDash(player, dash, at);
        dash.Aim = aim;
        return true;
    }

    static void StepChargedDash(Player player, ChargedDashChannel dash, double now, double focusedMore)
    {
        AdvanceChargedDash(player, dash, now);
        if (!dash.Path.Stopped && dash.Path.Distance > dash.StageDistance) dash.Stages++;
        dash.StageDistance = dash.Path.Distance;
        if ((++dash.PaidSteps & 1) == 0) _ = ChargedDashWave(player, dash, now, false, focusedMore);
    }

    static void RetireChargedDash(Player player, int generation)
    {
        if (ChargedDashChannels.TryGetValue(player, out var dash) && dash.Generation == generation)
            ChargedDashChannels.Remove(player);
    }

    static (int X, int Y)? ReleaseChargedDash(Player player, double at)
    {
        if (CurrentChargedDash(player) is not { } dash) return null;
        RetireChargedDash(player, dash.Generation);
        double now = ProjectileWorldTime(player);
        at = Math.Max(at, now);
        if (!player.ProjectileActive(dash.AreaEpoch) || player.PlayerLife == 0) return null;
        if (!ChargedDashWaveCurrent(player, dash) || PlayerActionBlocked(player, at, now))
            return player.Walk.Position(at);
        AdvanceChargedDash(player, dash, at);
        var landing = SkillsFor(player).IsMovement(dash.Source.Effect)
            ? dash.Path.Landing(player.Layout, player.Barrier, player.Blockers)
            : player.Walk.Position(at);
        player.Walk.Teleport(landing.X, landing.Y, at);
        int focus = dash.Source.Stats.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct);
        double startedAt = player.PaidChannel?.StartedAt ?? dash.At;
        _ = ChargedDashWave(player, dash, at, true, FocusedChannelMore(at - startedAt, focus));
        return landing;
    }

    static bool ChargedDashWaveCurrent(Player player, ChargedDashChannel dash) => CurrentGameWork
        && player.ProjectileActive(dash.AreaEpoch) && player.PlayerLife > 0 && !player.RageRetired
        && player.ActionInterruptionGeneration == dash.Interruption
        && SameTriggerSource(dash.Source, SupportedSkill(player, dash.Skill))
        && GemSkillInstanceDisabled(player, dash.Source.Effect, dash.Skill) is null;

    internal static int ChargedDashRadius(double progress, int scale, int finalBonus) =>
        (int)((long)(14 + finalBonus + (int)(12 * Math.Clamp(progress, 0, 1))) * scale / 100);

    internal static (double Hit, double Ailment) ChargedDashDamage(IReadOnlyDictionary<uint, int> stats,
        bool release, bool stopped, int stages)
    {
        if (release)
        {
            double scale = Math.Max(0, 1 + stats.GetValueOrDefault(Stat.ChargedDashDamagePlusPctFinal) / 100d
                + stages * (stats.GetValueOrDefault(Stat.ChargedDashDamagePlusPctFinalPerStack) / 100d));
            return (1, scale);
        }
        return (stopped ? Math.Max(0, 1 + stats.GetValueOrDefault(Stat.ChargedDashChannellingDamageAtFullStacksPlusPctFinal) / 100d) : 1, 1);
    }

    static async Task ChargedDashWave(Player player, ChargedDashChannel dash, double start, bool release, double focusedMore)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == dash.Skill);
        if (gem.Name is null || !ChargedDashWaveCurrent(player, dash)) return;
        var points = dash.Path.WavePoints();
        double progress = dash.Path.Progress;
        var (hitMore, hitAndAilmentMore) = ChargedDashDamage(dash.Source.Stats, release, dash.Path.Stopped, dash.Stages);
        var stats = SkillAreaStats(player, dash.Source.Stats, gem.Effect);
        int scale = 100 + SkillArea.RadiusPercent(stats.GetValueOrDefault, spell: false);
        int final = stats.GetValueOrDefault(Stat.ChargedDashAreaOfEffectRadiusPlusOfFinalExplosion);
        if (release) final += 5;
        var rolledCrit = CaptureSkillCriticalRoll(player, dash.Skill);
        var hit = new HashSet<uint>();
        for (int i = 0; i < points.Length; i++)
        {
            double wait = start + ChargedDashWaveStep * i - AreaTime(player);
            if (wait > 0) await AreaDelay(player, wait);
            await WhilePaused(player);
            if (!ChargedDashWaveCurrent(player, dash)) return;
            var centre = points[i];
            int radius = ChargedDashRadius(progress, scale, i == points.Length - 1 ? final : 0);
            if (radius <= 0) continue;
            await AreaSkillHitForUse(1, player, dash.Skill, gem, centre, centre, immediate: true,
                sequence: dash.Sequence, damageMore: hitMore * focusedMore, hitAndAilmentMore: hitAndAilmentMore,
                criticalRoll: rolledCrit, radiusOverride: radius, once: hit, triggeredCurrent: () => ChargedDashWaveCurrent(player, dash),
                targetFilter: (id, at) => player.Area.Monsters.TryGetValue(id, out var m) && !m.WorldHidden
                    && (player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(centre, at, sight, 1)),
                allowMeleeSplash: false, nearbyOnly: true);
        }
    }

    // Both variants use the client's ChargedDashAim action, which can relocate the local actor on release.
    // Follow the finish with the authoritative landing in the same record so both variants agree with the server.
    static byte[] ChannelFinishedRecord(Player player, ushort sequence, (int X, int Y)? landing) => landing is { } at
        ? [.. InstanceProtocol.ChannelFinished(player.Id, sequence), .. InstanceProtocol.Teleport(player.Id, at.X, at.Y)]
        : InstanceProtocol.ChannelFinished(player.Id, sequence);
}
