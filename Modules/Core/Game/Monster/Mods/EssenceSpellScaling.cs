using GameServer.Core;
using static Game;
using static Program;

/// <summary>
/// Tier scaling for the essence spells' projectile and summon counts. The counts observed on a tier-16 map (monster level
/// 83, a tier-7 Deafening essence) are the maximum; a released rare's essence tier scales them down uniformly toward level 1
/// (a tier-1 Whispering essence), so an essence that appears from the first area level still casts a minimal spell. The
/// corrupted essences (tier 8) always cast the maximum. Map and monster modifiers add projectiles on top (the ordinary
/// monster-mod extra-projectiles path), as they do for any monster spell.
/// <para>The scale is the essence's tier over the seven normal tiers: <c>max(1, round(maxCount × min(tier,7) / 7))</c>.
/// An essence type that only starts at a higher tier (Misery at tier 5) simply never appears at the low levels, so its
/// counts begin partway up the scale. The maximum counts are the live observations at tier 7 (the wiki behaviours); where
/// the skill's own per-level stats already scale a count, the tier scale replaces it so the progression is uniform.</para>
/// </summary>
public static partial class MapEssences
{
    /// <summary>The maximum projectile/summon count of a daemon effect at tier 7 (live observations on a tier-16 map).
    /// Effects absent here keep the skill's own count.</summary>
    static readonly Dictionary<int, int> SpellMaxCount = new()
    {
        [2300] = 16, // Hatred — EssenceArcticBreathNova: a circle of Creeping Frost projectiles
        [2303] = 18, // Anger — EssenceTriggeredFlameBreath: fire arrows released around the rare
        [6181] = 12, // Wrath — EssenceWrathMortarProjectile: lightning molten-strike projectiles
        [4958] = 3,  // Woe — EssenceSummonLivingCrystal: Energy Locus casting Lightning Tendrils
        [4957] = 3,  // Greed — EssenceSummonBloodProjectileDaemon: the red bubble's corrupted-blood projectiles
        [4196] = 3,  // Contempt — EssenceDaemonSummonKaruiSpirit: ice Ancestral Warchief spirits (Screaming)
        [4194] = 3,  // Sorrow — EssenceDaemonSummonTotemVortex: Vortex totems
        [4193] = 3,  // Fear — EssenceDaemonSummonGhost: Charging Corsair
        [5564] = 3,  // Doubt — EssenceSummonSpiderWithMortar: Essence Tarantula spiders
        [4195] = 1,  // Spite — EssenceDaemonSummonTotemShockedGround: Arc totem
        [4202] = 1,  // Dread — EssenceDaemonSummonChaosGolem: Guardian Golem
        [4207] = 3,  // Delirium — EssenceDaemonSummonExplosiveInsect: Carrion-minion casters
        [31] = 8,    // Hysteria — EssenceFireballBurst: Fireball projectiles
        [4203] = 1,  // Hysteria — EssenceDaemonSummonFlameGolemMeteor: Flameblast
        [4204] = 9,  // Horror — EssenceDaemonStormCascadeDaemon: the line of ice-explosion circles
        [4210] = 1,  // Torment — EssenceDaemonSummonOrbOfStormsDaemon: Ball Lightning
        [2145] = 6,  // Rage — EssenceDaemonFireRuneTrapCluster: fire rune mines
        [5357] = 1,  // Suffering — EssenceFrostBomb: Frost Bomb
        [4294] = 3,  // Insanity — EssenceLightningBeacon: Storm Call
        [4205] = 3,  // Horror — EssenceDaemonSummonCycloneSpirals: Maelström
    };

    /// <summary>The tier-scaled count of a spell: the tier-7 maximum reduced uniformly over the seven normal tiers, at
    /// least one. The corrupted tier (8) keeps the maximum.</summary>
    public static int ScaleCount(int maxCount, int tier)
    {
        if (maxCount <= 1) return Math.Max(1, maxCount);
        int t = Math.Clamp(tier, 1, 7);
        return Math.Max(1, (int)Math.Round(maxCount * t / 7.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>The tier-scaled count for a daemon effect, or its own <paramref name="own"/> count when the effect has no
    /// observed maximum.</summary>
    public static int ScaledSpellCount(int effect, int tier, int own) =>
        SpellMaxCount.TryGetValue(effect, out int max) ? ScaleCount(max, tier) : own;
}