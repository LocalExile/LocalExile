using GameServer.Core.World;

/// <summary>Frozen offensive inputs. Targets, including evasion and resistance, are resolved at contact.</summary>
sealed record ProjectileOffense(DamagePacket Hit, bool Spell, int Accuracy, int CriticalChance,
    int CriticalMultiplier, string Label)
{
    public DamagePacket? AreaHit { get; init; }
    public Penetration.PerType Penetration { get; init; }
    public HitResistances.Mods ResistanceMods { get; init; }
    public int ImpaleChance { get; init; }
    public int Effect { get; init; } = -1;
    public int Level { get; init; } = 1;
    public int Quality { get; init; }
    public int AccuracyMoreUnique { get; init; }
    public int AccuracyMoreClose { get; init; }
    public bool AlwaysHits { get; init; }
    public bool FarShot { get; init; }
    public double ReturnMultiplier { get; init; } = 1;
    public int? SecondFormCriticalChance { get; init; }
    public string? AreaVisual { get; init; }
    public string? FlightVisual { get; init; }
    public Program.ProjectilePresentation? Presentation { get; init; }
    public IReadOnlyDictionary<uint, int> SkillStats { get; init; } = new Dictionary<uint, int>();
}
