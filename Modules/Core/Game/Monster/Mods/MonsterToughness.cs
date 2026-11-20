public static partial class Game
{
    public const string MonsterToughness = "monster_difficulty_tankiness_+%";

    // Toughness changes damage taken, not maximum Life (3.25.0 Monster Balance). Its less-damage percentage is
    // rounded before damage: +100 takes 50%, +200 takes 33%. The pinned monster modifiers are all nonnegative.
    public static double MonsterToughnessFactor(MonsterModEffects effects)
    {
        int toughness = effects[MonsterToughness];
        if (toughness == 0) return 1;
        double less = Math.Floor(100 - 10000 / (100d + Math.Max(-99, toughness)) + .5);
        return Math.Max(0, (100 - less) / 100);
    }
}
