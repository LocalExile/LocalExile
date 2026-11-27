using System.Numerics;
using GameServer.Core.World;

public static partial class Game
{
    internal static class EarthshatterFissures
    {
        internal const ushort Action = 0xb3fc;
        internal const int TimedVariation = 2;
        const int BaseWidth = 10;
        const int MinimumEndSeparation = 8;
        const int CellsPerStep = 3;
        internal const double StepSeconds = .025;

        internal readonly record struct Fissure((int X, int Y) Start, (int X, int Y) End, int Length, int Width)
        {
            internal int Steps => (Length + CellsPerStep - 1) / CellsPerStep;
            internal double Seconds => Steps * StepSeconds;

            internal bool Reaches((int X, int Y) at, double bodyRadius, int step)
            {
                var axis = new Vector2(End.X - Start.X, End.Y - Start.Y);
                var delta = new Vector2(at.X - Start.X, at.Y - Start.Y);
                float length = axis.Length();
                float along = Vector2.Dot(axis, delta) / length;
                float side = Math.Abs(axis.X * delta.Y - axis.Y * delta.X) / length;
                double front = length * Math.Min(1d, step * (double)CellsPerStep / Length);
                double previous = length * Math.Max(0d, (step - 1) * (double)CellsPerStep / Length);
                return along >= previous - bodyRadius && along <= front + bodyRadius && side <= Width / 2d + bodyRadius;
            }
        }

        // The contact is ten cells forward. The single fissure faces the aim. For several fissures the first
        // angle and the shuffle of the length pool share the same action RNG.
        internal static List<Fissure> Plan((int X, int Y) from, (int X, int Y) aim, ushort sequence, uint owner,
            uint detail, uint repeatsLeft, uint repeatVariant, int baseCount, int extraChance, int lengthPercent,
            float areaScale, Func<(int X, int Y), bool> canPass)
        {
            if (baseCount <= 0 || areaScale <= 0 || lengthPercent <= -100) return [];
            var random = new AnimationVariantSelector.NativeRandom([sequence, detail, owner, repeatsLeft, repeatVariant, 0, 0, 0]);
            int count = baseCount;
            if (extraChance > 0)
            {
                var extra = random;
                if (extra.Uniform(0, 99) < extraChance) count++;
            }

            var direction = new Vector2(aim.X - from.X, aim.Y - from.Y);
            direction = direction == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(direction);
            var impact = Offset(from, direction * 10);
            if (!canPass(impact)) impact = from;

            float angle = count == 1 ? MathF.Atan2(direction.X, -direction.Y) : random.Range(0, MathF.Tau);
            if (angle < 0) angle += MathF.Tau;
            float scale = (100 + (float)lengthPercent) / 100;
            float min = 16 * scale, max = 28 * scale;
            float step = (max - min) / count;
            var lengths = new List<int>(count + 1);
            for (float length = min; lengths.Count <= count && (length <= max || lengths.Count < count); length += step)
                lengths.Add((int)length);
            for (int i = 1; i < lengths.Count; i++)
            {
                int other = (int)random.Uniform((uint)i + 1);
                (lengths[i], lengths[other]) = (lengths[other], lengths[i]);
            }

            int width = (int)(areaScale * BaseWidth);
            var fissures = new List<Fissure>(count);
            for (int i = 0; i < count; i++, angle += MathF.Tau / count)
            {
                var facing = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
                var desired = Offset(impact, facing * (lengths[i] * areaScale));
                var end = impact;
                foreach (var cell in ClientPathfinder.Line(impact, desired).Skip(1))
                {
                    if (!canPass(cell)) break;
                    end = cell;
                }
                if (end == impact || fissures.Any(fissure => Distance(end, fissure.End) <= MinimumEndSeparation))
                    continue;
                fissures.Add(new(impact, end, (int)Distance(impact, end), width));
            }
            return fissures;
        }

        static (int X, int Y) Offset((int X, int Y) point, Vector2 delta) =>
            (point.X + (int)MathF.Floor(delta.X + .5f), point.Y + (int)MathF.Floor(delta.Y + .5f));

        static float Distance((int X, int Y) a, (int X, int Y) b) =>
            new Vector2(a.X - b.X, a.Y - b.Y).Length();
    }
}
