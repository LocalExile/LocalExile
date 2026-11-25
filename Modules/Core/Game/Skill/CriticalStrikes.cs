
public static partial class Game
{
    // "Your Critical Strikes do not deal extra Damage": the stat, and the two keystones whose text says the same
    // (Elemental Overload, Perfect Agony). Resolute and Precise Technique say "Never deal Critical Strikes", which is
    // CriticalProhibitedBy instead.
    public static bool CriticalHitsDealNoExtraDamage(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        bool Flag(uint key) => player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0;
        return Flag(Stat.NoCriticalStrikeMultiplier)
            || Flag(Stat.KeystoneElementalOverload) || Flag(Stat.KeystoneAilmentCrit);
    }

    public static int CriticalHitMultiplier(long ordinary, bool noExtra) => noExtra ? 100
        : (int)Math.Clamp(ordinary, int.MinValue, int.MaxValue);

    public static int CriticalHitMultiplier(long ordinary, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null) => CriticalHitMultiplier(
            // "Your Critical Strike Multiplier is 300%" (Maligaro's Virtuosity): the total, whatever else adds to it.
            player.GetValueOrDefault(Stat.CriticalStrikeMultiplierIs300) != 0 ? 300 : ordinary,
            CriticalHitsDealNoExtraDamage(player, own));

    static long ClawDaggerCriticalBonus(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own)
    {
        const uint stat = Stat.CriticalStrikeMultiplierPlusPctWithClawsDaggers;
        long bonus = (long)player.GetValueOrDefault(stat) + (own?.GetValueOrDefault(stat) ?? 0);
        return bonus != 0 && (weapon.ItemClass is "Claw" or "Dagger" or "Rune Dagger"
            || WeaponIdentity.AllOneHandMelee(weapon.LocalSources)) ? bonus : 0;
    }

    public static bool IsCriticalProhibition(uint name) => name is Stat.GlobalCannotCrit
        or Stat.VirtualGlobalCannotCrit or Stat.CannotCriticalStrikeWithAttacks
        or Stat.ResoluteTechnique or Stat.KeystonePreciseTechnique;

    public static (bool LowLife, bool Focused, int ExtraRolls) CriticalContext(Player player)
    {
        var playerStats = CharacterPlayerStats(player);
        bool focused = Stat.HasFocus is var key && playerStats.GetValueOrDefault(key) != 0;
        return (CriticalOnLowLife(player, playerStats), focused, CriticalExtraRollsFor(player, playerStats));
    }

    // Compute the native virtual_on_low_life input from current pools. Actions capture its luck contribution at use;
    // the panel continues to show the owner's current state.
    // Focus and Vulnerability use their current raw state inputs; their skill/curse lifecycles are separate mechanics.
    public static bool CriticalOnLowLife(Player player, IReadOnlyDictionary<uint, int> playerStats) =>
        CriticalOnLowLife(() => player.PlayerLife, () => player.PlayerMaxLife, playerStats);

    // From the life and maximum life as read once (the stat snapshot's tail inputs), or as they are now.
    public static bool CriticalOnLowLife(uint life, uint maximumLife, IReadOnlyDictionary<uint, int> player) =>
        CriticalOnLowLife(() => life, () => maximumLife, player);

    static bool CriticalOnLowLife(Func<uint> life, Func<uint> maximumLife, IReadOnlyDictionary<uint, int> player)
    {
        int Get(uint key) => player.GetValueOrDefault(key);
        bool counts = Get(Stat.YouCountAsLowLifeWhileNotOnFullLife) != 0 && !CountsAsFullLife(life(), maximumLife(), player)
            || Get(Stat.YouCountAsLowLifeWhileAffectedByVulnerability) != 0
                && (Get(Stat.CursedByVulnerability) != 0 || Get(Stat.YouAreCursedWithVulnerability) != 0);
        return CriticalStrikePolicy.OnLowLife(life(), maximumLife(),
            Get(Stat.LowLifeThresholdPctOverride), Get(Stat.LowLifeThresholdPct), counts);
    }

    public static int CriticalExtraRolls(Player player, IReadOnlyDictionary<uint, int>? own = null)
    {
        return CriticalExtraRollsFor(player, CharacterPlayerStats(player), own);
    }

    public static int CriticalExtraRollsFor(Player player, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? own = null) =>
        CriticalExtraRollsFor(CriticalOnLowLife(player, playerStats), playerStats, own);

    public static int CriticalExtraRollsFor(bool onLowLife, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        int Player(uint key) => player.GetValueOrDefault(key);
        int Own(uint key) => own?.GetValueOrDefault(key) ?? 0;
        long Sum(uint name) => (long)Player(name) + Own(name);
        return CriticalStrikePolicy.ExtraRolls(Sum(Stat.ExtraCriticalRolls),
            Sum(Stat.ExtraCriticalRollsWhileOnLowLife), onLowLife,
            Sum(Stat.ExtraCriticalRollsDuringFocus), Player(Stat.HasFocus) != 0,
            Player(Stat.KeystoneSharpAndBrittle) != 0 || Own(Stat.KeystoneSharpAndBrittle) != 0);
    }

    public static bool CriticalProhibited(Player player, bool attack, IReadOnlyDictionary<uint, int>? own = null)
        => CriticalProhibitedBy(CharacterPlayerStats(player), attack, own);

    public static bool CriticalProhibitedBy(IReadOnlyDictionary<uint, int> player, bool attack,
        IReadOnlyDictionary<uint, int>? own)
    {
        bool Flag(uint key) => player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0;
        return Flag(Stat.GlobalCannotCrit) || Flag(Stat.VirtualGlobalCannotCrit)
            // Resolute Technique and Precise Technique ("Never deal Critical Strikes"), and the attack-only cannot-crit.
            || attack && (Flag(Stat.CannotCriticalStrikeWithAttacks)
                || Flag(Stat.ResoluteTechnique) || Flag(Stat.KeystonePreciseTechnique));
    }

    // The original per-type damage draws still precede the original critical Next(10000). Conditional luck adds
    // at most one following draw. This RNG order is a local policy, not evidence of the official server's sequence.
    public static bool RollCriticalStrike(Player player, int chance, IReadOnlyDictionary<uint, int>? own = null,
        bool attack = true, SkillCriticalRoll? skillRoll = null, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        if (chance <= 0 && !HoldingFishingRod(player)) return false;
        playerStats ??= CharacterPlayerStats(player);
        if (AlwaysCritical(player, name => name is var key ? playerStats.GetValueOrDefault(key) : 0))
            return !CriticalProhibitedBy(playerStats, attack, own);
        if (chance <= 0) return false;
        int allowedChance = CriticalProhibitedBy(playerStats, attack, own) ? 0 : chance;
        return skillRoll is not null ? skillRoll.Check(allowedChance, player.CriticalRandom)
            : CriticalStrikePolicy.Roll(allowedChance, player.CriticalRandom, CriticalExtraRollsFor(player, playerStats, own));
    }

    public static SkillCriticalRoll CaptureSkillCriticalRoll(Player player, ushort skillId)
    {
        int effect = SkillEffectId(player, skillId) is { } id ? SkillsFor(player).EffectRow(id) : -1;
        var own = skillId == SkillCatalog.FirstSkillId
            ? SkillsFor(player).EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2)
            : SupportedSkill(player, skillId).Stats;
        return new(CriticalExtraRollsFor(player, CharacterPlayerStats(player), own));
    }

    // Baited Breath (always_crit_while_holding_fishing_rod): while holding a Fishing Rod every hit is a critical strike
    // without a roll, whatever its base chance; a cannot-crit prohibition still wins (wiki). The client's
    // virtual_always_crit calculator (0x1427625b0) reads the stat only for main_hand_weapon_type 15; PoB: CritChance
    // OVERRIDE 100.
    static bool AlwaysCritical(Player player, Func<uint, int> get) =>
        get(Stat.AlwaysCritWhileHoldingFishingRod) != 0 && HoldingFishingRod(player);

    // The chance the panel shows under it: 100% where the hit could crit at all; a 0% base still shows 0 although its
    // hits crit (wiki Baited Breath). Prohibited chances are already 0.
    static int DisplayedCriticalChance(Player player, int chance, Func<uint, int> get) =>
        chance > 0 && AlwaysCritical(player, get) ? 10_000 : chance;

    public static int AttackCriticalMultiplier(Player player, IReadOnlyDictionary<uint, int> playerStats,
        int effect = -1, IReadOnlyDictionary<uint, int>? own = null) =>
        AttackCriticalMultiplier(player.Pools, WeaponProfileFor(player, activeSet: true).Unarmed, MeleeAttack(player, effect),
            effect >= 0 && SkillsFor(player).IsSpell(effect), playerStats, own);

    // The default attack's (no gem effect) from the stat snapshot's tail inputs: the active set's profile decides both.
    public static int AttackCriticalMultiplier(CharacterStats.Pools pools, bool unarmed, bool melee, bool spell,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null)
    {
        uint? key = (CriticalStrikePolicy.UnarmedMeleeMultiplierStat);
        long extra = key is uint k ? (long)player.GetValueOrDefault(k) + (own?.GetValueOrDefault(k) ?? 0) : 0;
        return CriticalHitMultiplier(CriticalStrikePolicy.Multiplier(pools.CriticalStrikeMultiplier,
            extra, unarmed, melee, spell), player, own);
    }
}
