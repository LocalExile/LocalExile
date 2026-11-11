using GameServer.Core.World;


/// <summary>Damage outputs captured for a target condition at release. A null
/// <see cref="ProjectileOffense.VsBlinded"/> means the shot has no frozen alternate and must stay on its normal
/// damage if the target later becomes Blinded.</summary>
public sealed record ProjectileOffenseAlternate(DamagePacket Hit, DamagePacket? AreaHit, MonsterIgnites.Settings Ignite,
    MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);

/// <summary>Frozen offensive inputs. Targets, including evasion and resistance, are resolved at contact.</summary>
public sealed record ProjectileOffense(DamagePacket Hit, bool Spell, int Accuracy, int CriticalChance,
    int CriticalMultiplier, string Label)
{
    /// <summary>An optional encounter owner; expired owners cannot commit further contacts.</summary>
    public IReadOnlyDictionary<uint, int>? ActualCaster { get; init; }
    public object? Owner { get; init; }
    public bool IntimidatingCryExerted { get; init; }
    public Func<bool>? Valid { get; init; }
    public DamagePacket? AreaHit { get; init; }
    public Penetration.PerType Penetration { get; init; }
    public HitResistances.Mods ResistanceMods { get; init; }
    public int ImpaleChance { get; init; }
    /// <summary>The ignite its hits can inflict, captured at release (MonsterIgnites); the default ignites nothing.</summary>
    public MonsterIgnites.Settings Ignite { get; init; }
    /// <summary>The bleed an attack's hits can inflict, captured at release (MonsterBleeds); the default bleeds nothing.</summary>
    public MonsterIgnites.Settings Bleed { get; init; }
    /// <summary>The maim chance an attack's hits can inflict, captured at release; the default maims nothing.</summary>
    public int MaimChance { get; init; }
    /// <summary>The poison damage and duration its hits deal, captured at release (MonsterPoisons); the default poisons
    /// nothing.</summary>
    public MonsterPoisons.Settings Poison { get; init; }
    public ImpaleStats.Settings? ImpaleSettings { get; init; }
    public ImpaleStats.Settings? CriticalImpaleSettings { get; init; }
    public AttackAccuracyProfile? AccuracyProfile { get; init; }
    public SkillCriticalRoll? CriticalRoll { get; init; }
    public LeechSources Leech { get; init; } = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty);
    public int Effect { get; init; } = -1;
    public ushort SourceSkill { get; init; }
    public Game.LinkedTriggerSource? CriticalTrigger { get; init; }
    public long? RampageEpoch { get; init; }
    public RampageCatalog.SkillLevel? RampageSkill { get; init; }
    public int Level { get; init; } = 1;
    public int Quality { get; init; }
    public int AccuracyMoreUnique { get; init; }
    public int AccuracyMoreClose { get; init; }
    public bool AlwaysHits { get; init; }
    public bool FarShot { get; init; }
    public bool PointBlank { get; init; }
    public double ReturnMultiplier { get; init; } = 1;
    public BlindHitInput Blind { get; init; }
    /// <summary>The hit, area hit and damaging ailments captured with the conditional increased damage against a
    /// Blinded target, or null when the shot has none.</summary>
    public ProjectileOffenseAlternate? VsBlinded { get; init; }
    public SpiderDamageSource? Spider { get; init; }
    public SpiderDamageSource? SpiderArea { get; init; }
    public int? SecondFormCriticalChance { get; init; }
    /// <summary>The chance against a target the player has Blinded (Program.CriticalChanceOn), or null.</summary>
    public int? VsBlindedCriticalChance { get; init; }
    /// <summary>The chance against a target on the player's profane ground (Rational Doctrine), or null.</summary>
    public int? VsProfaneCriticalChance { get; init; }
    public string? AreaVisual { get; init; }
    public string? FlightVisual { get; init; }
    /// <summary>The client spawns this use's primary projectiles itself (its own skill use), so the server sends no object
    /// for them; continuation children still get one.</summary>
    public bool ClientPredicted { get; init; }
    public Game.ProjectilePresentation? Presentation { get; init; }
    /// <summary>Send S→C 0x1af on each contact (the target's blood and particles), as the melee default attack does.
    /// <see cref="ActionSequence"/> is the shot's action, so the client matches the package to it (kind 3,
    /// action-hit-packet-3.29.3b.md §3); without one the fabricated 0xffff still plays immediately.</summary>
    public bool HitPacket { get; init; }
    public ushort? ActionSequence { get; init; }
    public IReadOnlyDictionary<uint, int> SkillStats { get; init; } = new Dictionary<uint, int>();

    /// <summary>Selects the frozen output for the target's state before this hit's side effects, without recapturing
    /// release inputs or drawing random numbers.</summary>
    public ProjectileOffense Against(bool targetWasBlinded, int webs = 0)
    {
        if (webs > 0 && Spider is { } spider)
        {
            var profile = Game.SpiderDamageProfile(spider, webs, targetWasBlinded);
            return this with { Hit = profile.Hit, AreaHit = SpiderArea is { } area
                ? Game.SpiderDamageProfile(area, webs, targetWasBlinded).Hit : AreaHit,
                Ignite = profile.Ignite, Bleed = profile.Bleed, Poison = profile.Poison };
        }
        return targetWasBlinded && VsBlinded is { } alternate ? this with
        {
            Hit = alternate.Hit,
            AreaHit = alternate.AreaHit,
            Ignite = alternate.Ignite,
            Bleed = alternate.Bleed,
            Poison = alternate.Poison,
        }
        : this;
    }
}
