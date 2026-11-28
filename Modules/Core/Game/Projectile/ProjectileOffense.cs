using GameServer.Core.Projectiles;
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
    public bool WeaponAttack { get; init; }
    internal bool DirectAttackContact { get; init; }
    public GameServer.Core.Protocol.Instance.Item? SkeletonWeaponContactSource { get; init; }
    /// <summary>Whether an attack hit counts as a projectile attack (false for non-projectile rains and secondary explosions).</summary>
    public bool? ProjectileAttack { get; init; }
    public Game.TinctureKillBonus? TinctureKill { get; init; }
    public bool NoExtraCriticalDamage { get; init; }
    internal Game.TargetCriticalBonuses TargetCritical { get; init; }
    /// <summary>An optional encounter owner; expired owners cannot commit further contacts.</summary>
    public IReadOnlyDictionary<uint, int>? ActualCaster { get; init; }
    public SpawnedMonster? BrandedTarget { get; init; }
    public object? Owner { get; init; }
    public bool IntimidatingCryExerted { get; init; }
    internal int OtherWarcryExertions { get; init; }
    internal int? RallyingDamage { get; init; }
    public double HitProfileMore { get; init; } = 1;
    public double AilmentProfileMore { get; init; } = 1;
    public double DotProfileMore { get; init; } = 1;
    public Func<bool>? Valid { get; init; }
    public DamagePacket? AreaHit { get; init; }
    internal sealed record AreaAilments(MonsterIgnites.Settings Ignite, MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);
    internal AreaAilments? ConeAilments { get; init; }
    internal AreaAilments? BlindedConeAilments { get; init; }
    internal Game.ForbiddenRiteCast? ForbiddenRite { get; init; }
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
    /// <summary>Essence Drain's debuff, put on every monster a projectile hit leaves alive (MonsterEssenceDrains.cs).</summary>
    public MonsterEssenceDrain.Cast? EssenceDrain { get; init; }

    public DamageOverTime? TrarthusBurn { get; init; }
    internal DamageOverTime? ConflagrationBurn { get; init; }
    internal MonsterContagion.Cast? Soulrend { get; init; }
    internal Game.FleshrendCast? Fleshrend { get; init; }
    internal double GhorrDotMore { get; init; } = 1;
    /// <summary>Caustic Arrow's non-stacking caustic ground cloud, spawned at every arrow impact and end (CausticClouds.cs).</summary>
    public sealed record CausticCloudSpec(int Radius, double Duration, double Dps, int ArtVariation);
    public CausticCloudSpec? CausticCloud { get; init; }
    public sealed record GroundBladeSpec(int EveryProjectile, int Maximum, long Lifetime);
    public GroundBladeSpec? GroundBlade { get; init; }
    internal Game.UnearthCorpse? Unearth { get; init; }
    public Game.ExplosiveFuseSpec? ExplosiveFuse { get; init; }
    public Game.EnsnareSpec? Ensnare { get; init; }
    internal Game.ElectrocutionSpec? Electrocution { get; init; }
    internal Game.VenomGyreCapture? VenomGyre { get; init; }
    internal Game.FusilladeCast? Fusillade { get; init; }
    public ImpaleStats.Settings? ImpaleSettings { get; init; }
    public ImpaleStats.Settings? CriticalImpaleSettings { get; init; }
    public AttackAccuracyProfile? AccuracyProfile { get; init; }
    public SkillCriticalRoll? CriticalRoll { get; init; }
    /// <summary>Released by the player's own totem: its kills are the player's own device's (OwnedKills).</summary>
    public bool FromTotem { get; init; }
    /// <summary>Thrown by a trap or mine: Bear Trap's damage taken from traps and mines applies (MonsterTakesTrapOrMineHit).</summary>
    public bool FromTrapOrMine { get; init; }
    public bool FromMine { get; init; }
    internal bool FreezingPulseFalloff { get; init; }
    internal int RemainingChainDamagePercent { get; init; }
    internal int CompletedChainDamagePercent { get; init; }
    internal int DamageIncreasePerChain { get; init; }
    internal int DamageIncreasePerPierce { get; init; }
    /// <summary>The local stats of the weapon an attack was made with (HitRollModifiers.ForWeapon); null for a spell.</summary>
    public IReadOnlyDictionary<uint, int>? WeaponLocal { get; init; }
    internal bool OffHandWeapon { get; init; }
    public string? WeaponClass { get; init; }
    public LeechSources Leech { get; init; } = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty);
    public int Effect { get; init; } = -1;
    public ushort SourceSkill { get; init; }
    public Game.LinkedTriggerSource? CriticalTrigger { get; init; }
    public long? RampageEpoch { get; init; }
    public RampageCatalog.SkillLevel? RampageSkill { get; init; }
    public int Level { get; init; } = 1;
    /// <summary>The skill's gem type, for the art its worn skill effects pick.</summary>
    public uint Gem { get; init; }
    public int Quality { get; init; }
    public int AccuracyMoreUnique { get; init; }
    public int AccuracyMoreClose { get; init; }
    public int LocalStunThresholdReduction { get; init; }
    public bool AlwaysHits { get; init; }
    /// <summary>A hybrid strike's melee victim cannot also take that strike's projectile damage.</summary>
    public GameServer.Core.Projectiles.ProjectileActorKey? ExcludedTarget { get; init; }
    public bool FarShot { get; init; }
    public bool PointBlank { get; init; }
    public double ReturnMultiplier { get; init; } = 1;
    public int DamageMorePerGroundBounce { get; init; }
    public int DamageMoreAfterPierce { get; init; }
    public int DamageMorePerElementalAilment { get; init; }
    internal sealed class LancingSteelHitSeries(double repeatMultiplier)
    {
        public readonly HashSet<ProjectileActorKey> Targets = [];
        public double RepeatMultiplier { get; } = repeatMultiplier;
    }

    internal LancingSteelHitSeries? LancingSteelHits { get; init; }
    internal double ShatteringSteelAmmoHitBonus { get; init; }
    public BlindHitInput Blind { get; init; }
    /// <summary>The hit, area hit and damaging ailments captured with the conditional increased damage against a
    /// Blinded target, or null when the shot has none.</summary>
    public ProjectileOffenseAlternate? VsBlinded { get; init; }
    public SpiderDamageSource? Spider { get; init; }
    public SpiderDamageSource? SpiderArea { get; init; }
    public int? SecondFormCriticalChance { get; init; }
    internal sealed record FormCriticalOptions(int? VsBlinded, Game.GroundCritical? VsGround, int MultiplierPlus);
    internal FormCriticalOptions? SecondFormOptions { get; init; }
    /// <summary>The chance against a target the player has Blinded (Program.CriticalChanceOn), or null.</summary>
    public int? VsBlindedCriticalChance { get; init; }
    /// <summary>The chances against a target on the player's profane or consecrated ground (Game.GroundCritical), or null.</summary>
    public Game.GroundCritical? VsGroundCriticalChance { get; init; }
    public string? AreaVisual { get; init; }
    public string? FlightVisual { get; init; }
    public Game.SupportedGem? MirageSource { get; init; }
    /// <summary>A Mirage Archer's shot: damage it deals later (Caustic Arrow's ground) takes the mirage's less damage.</summary>
    public bool Mirage { get; init; }
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
    internal int ScourgePods { get; init; }
    internal bool ScourgePodsPerProjectile { get; init; }
    internal Game.SomaticShellApplications? SomaticApplications { get; init; }
    internal bool BypassesSomaticShell { get; init; }
    internal int SkillResistanceInversionChance { get; init; }
    /// <summary>The use's once-per-use grants (the Frenzy gem's charge), shared by its volley: the first enemy hit
    /// claims them.</summary>
    public UseOnce? FirstHit { get; init; }
    internal Game.TraumaUse? Trauma { get; init; }
    internal Game.SupportedGem? TraumaSource { get; init; }

    /// <summary>Selects the frozen output for the target's state before this hit's side effects, without recapturing
    /// release inputs or drawing random numbers.</summary>
    public ProjectileOffense Against(bool targetWasBlinded, int webs = 0, bool ignited = false, bool bleeding = false, bool lowLife = false)
        => Against(targetWasBlinded, webs, ignited, bleeding, lowLife, maimed: false);

    internal ProjectileOffense Against(bool targetWasBlinded, int webs, bool ignited, bool bleeding, bool lowLife, bool maimed,
        Game.TargetContact contact = default, bool? areaContact = null)
    {
        var cone = areaContact is true ? ConeAilments : null;
        if ((cone is null ? Spider : SpiderArea) is { } spider
            && Game.ProjectileTargetChangesDamage(spider, targetWasBlinded, webs, ignited, bleeding, lowLife, maimed, contact))
        {
            var profile = Game.SpiderDamageProfile(spider, webs, targetWasBlinded, ignited, bleeding, lowLife, maimed, contact: contact);
            if (cone is not null)
                return this with { AreaHit = profile.Hit.Scale(HitProfileMore),
                    Ignite = Scale(profile.Ignite, AilmentProfileMore), Bleed = Scale(profile.Bleed, AilmentProfileMore),
                    Poison = Scale(profile.Poison, AilmentProfileMore) };
            var areaHit = areaContact is not false && SpiderArea is { } area
                ? Game.SpiderDamageProfile(area, webs, targetWasBlinded, ignited, bleeding, lowLife, maimed,
                    contact: contact, hitOnly: true).Hit.Scale(HitProfileMore) : AreaHit;
            return this with { Hit = profile.Hit.Scale(HitProfileMore), AreaHit = areaHit,
                Ignite = Scale(profile.Ignite, AilmentProfileMore), Bleed = Scale(profile.Bleed, AilmentProfileMore),
                Poison = Scale(profile.Poison, AilmentProfileMore) };
        }
        if (cone is not null)
        {
            if (targetWasBlinded && BlindedConeAilments is { } blinded) cone = blinded;
            return this with
            {
                AreaHit = targetWasBlinded && VsBlinded?.AreaHit is { } hit ? hit.Scale(HitProfileMore) : AreaHit,
                Ignite = Scale(cone.Ignite, AilmentProfileMore), Bleed = Scale(cone.Bleed, AilmentProfileMore),
                Poison = Scale(cone.Poison, AilmentProfileMore),
            };
        }
        return targetWasBlinded && VsBlinded is { } alternate ? this with
        {
            Hit = alternate.Hit.Scale(HitProfileMore),
            AreaHit = alternate.AreaHit?.Scale(HitProfileMore),
            Ignite = Scale(alternate.Ignite, AilmentProfileMore),
            Bleed = Scale(alternate.Bleed, AilmentProfileMore),
            Poison = Scale(alternate.Poison, AilmentProfileMore),
        }
        : this;
    }

    public ProjectileOffense ScaleDamage(double more) => more == 1 ? this : this with
    {
        Hit = Hit.Scale(more), AreaHit = AreaHit?.Scale(more),
        HitProfileMore = HitProfileMore * more, AilmentProfileMore = AilmentProfileMore * more,
        DotProfileMore = DotProfileMore * more,
        Ignite = Scale(Ignite, more), Bleed = Scale(Bleed, more), Poison = Scale(Poison, more),
        Soulrend = Soulrend is { } cast ? cast with { DamagePerSecond = cast.DamagePerSecond * more } : null,
    };

    public ProjectileOffense ScaleHitAndAilmentDamage(double more) => more == 1 ? this : this with
    {
        Hit = Hit.Scale(more), AreaHit = AreaHit?.Scale(more),
        HitProfileMore = HitProfileMore * more, AilmentProfileMore = AilmentProfileMore * more,
        Ignite = Scale(Ignite, more), Bleed = Scale(Bleed, more), Poison = Scale(Poison, more),
    };

    static MonsterIgnites.Settings Scale(MonsterIgnites.Settings value, double more) => value.ScaleDamage(more);

    static MonsterPoisons.Settings Scale(MonsterPoisons.Settings value, double more) => value with
    {
        Min = value.Min * more, Max = value.Max * more,
        CriticalMin = value.CriticalMin * more, CriticalMax = value.CriticalMax * more,
    };
}

/// <summary>Claimed once by whichever of a use's projectiles hits first.</summary>
public sealed class UseOnce
{
    int claimed;
    public bool Claim() => Interlocked.Exchange(ref claimed, 1) == 0;
}
