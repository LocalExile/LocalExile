

// Native weapon-stat enum: Bow is 9, whereas its ItemClasses row + 1 is 14 and its model stance is 8.
public static class WeaponTypeStats
{
    public static int? BowType(string? activeMainClass) => activeMainClass == "Bow" ? 9 : null;
}
