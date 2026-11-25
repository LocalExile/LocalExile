using GameServer.Core.World;

public static partial class Game
{
    const string VaalReaveEffect = "VaalReave";
    const ushort ReaveCounterRow = 222;
    const double ReaveIdleSeconds = 3;
    const double ReaveDecaySeconds = 1;
    const double ReaveHalfAngle = 67.5 * Math.PI / 180;
    static readonly double ReaveEdge = Math.Cos(ReaveHalfAngle);
    static readonly RuleTable<Player, ReaveCounter> ReaveCounters = new();

    sealed class ReaveCounter(GameArea area)
    {
        public readonly GameArea Area = area;
        public int Stages;
        public AreaTimer? Timer;
    }

    readonly record struct ReaveArc((int X, int Y) Origin, double X, double Y, int Radius)
    {
        public bool Covers((int X, int Y) at, int size)
        {
            if (Radius == 0) return false;
            double x = at.X - Origin.X, y = at.Y - Origin.Y;
            double square = x * x + y * y;
            double reach = Radius + Math.Max(0, size);
            return square <= reach * reach && x * X + y * Y >= Math.Sqrt(square) * ReaveEdge;
        }
    }

    static bool IsReave(string? effect) => effect is "Reave" or "ReaveAltX" or "ReaveAltY" or VaalReaveEffect;

    public static int ReaveStages(Player player) => ReaveCounters.TryGetValue(player, out var counter)
        && ReferenceEquals(counter.Area, player.Area) ? counter.Stages : 0;

    static ReaveArc ReaveArea((int X, int Y) from, (int X, int Y) to, int radius, int rotation, int repeat)
    {
        double x = (double)to.X - from.X, y = (double)to.Y - from.Y;
        if (x == 0 && y == 0)
            (x, y) = rotation == 0 ? (0, -1) : (1, 0);
        double length = Math.Sqrt(x * x + y * y);
        x /= length;
        y /= length;
        if (rotation != 0)
        {
            // Vaal Reave rotates a 30-cell aim vector before each contact (3.29.3c FUN_141bd91e0).
            x = (int)(x * 30);
            y = (int)(y * 30);
            double angle = repeat * rotation * Math.PI / 180;
            var (sin, cos) = Math.SinCos(angle);
            (x, y) = ((int)(x * cos - y * sin), (int)(x * sin + y * cos));
            length = Math.Sqrt(x * x + y * y);
            x /= length;
            y /= length;
        }
        var origin = (from.X - (int)(x * LivingCollision.PlayerSize), from.Y - (int)(y * LivingCollision.PlayerSize));
        return new(origin, x, y, radius);
    }

    static int ReaveCap(Func<uint, int> get) => (int)Math.Clamp(4L + get(Stat.ReaveAdditionalMaxStacks), 1L, short.MaxValue);

    static void GainReaveStage(Player player, Func<uint, int> get)
    {
        int stages = ReaveStages(player);
        SetReaveStages(player, stages < ReaveCap(get) ? stages + 1 : stages);
    }

    static void StartVaalReave(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        int start = Math.Min(ReaveCap(Get), Get(Stat.ReaveAdditionalStartingStacks));
        if (start > 0) SetReaveStages(player, Math.Max(start, ReaveStages(player)));
    }

    static void SetReaveStages(Player player, int stages)
    {
        InArea(player, area =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
            var counter = ReaveCounters.GetValue(player, _ => new(area));
            if (!ReferenceEquals(counter.Area, area))
            {
                ClearReaveStages(player);
                ReaveCounters.Add(player, counter = new(area));
            }
            counter.Stages = stages;
            if (counter.Timer is { } timer)
                timer.EndFrame = area.Frame + Frames(ReaveIdleSeconds);
            else
                counter.Timer = Schedule(ReaveIdleSeconds);
            Present();

            long Frames(double seconds) => (long)Math.Ceiling(seconds / FrameSeconds);
            AreaTimer Schedule(double seconds) =>
                area.Timers.After(area.Frame, Frames(seconds), Decay, new EntityId(player.Id), CombatLifetime);
            void Present() => area.Buffs.Keep(player.Id, new(ReaveCounterRow, player.Id, Stacks: (ushort)counter.Stages));
            void Decay(AreaTimer _)
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
                    || --counter.Stages == 0)
                {
                    ClearReaveStages(player);
                    return;
                }
                counter.Timer = Schedule(ReaveDecaySeconds);
                Present();
            }
        });
    }

    internal static void ClearReaveStages(Player player)
    {
        if (!ReaveCounters.TryGetValue(player, out var counter)) return;
        counter.Timer?.Cancel();
        counter.Area.Buffs.Clear(player.Id, ReaveCounterRow, player.Id);
        ReaveCounters.Remove(player);
    }

    internal static void ClearReaveCounters()
    {
        foreach (var (player, _) in ReaveCounters) ClearReaveStages(player);
    }

    static SupportedGem ReaveConversion(Player player, SupportedGem gem, int conversion)
    {
        uint key = AreaRng(player).Next(3) switch
        {
            0 => Stat.SkillPhysicalDamagePctToConvertToFire,
            1 => Stat.SkillPhysicalDamagePctToConvertToCold,
            _ => Stat.SkillPhysicalDamagePctToConvertToLightning
        };
        return gem with { Stats = new Dictionary<uint, int>(gem.Stats)
        {
            [key] = ActorScale.Saturate((long)gem.Stats.GetValueOrDefault(key) + conversion),
        } };
    }
}
