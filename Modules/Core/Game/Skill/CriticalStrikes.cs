
public static partial class Game
{
    public static bool CriticalHitsDealNoExtraDamage(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null) => Stat.NoCriticalStrikeMultiplier is var key
            && (player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0);

    public static int CriticalHitMultiplier(long ordinary, bool noExtra) => noExtra ? 100
        : (int)Math.Clamp(ordinary, int.MinValue, int.MaxValue);

    public static int CriticalHitMultiplier(long ordinary, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null) => CriticalHitMultiplier(ordinary,
            CriticalHitsDealNoExtraDamage(player, own));

    public static bool IsCriticalProhibition(uint name) => name is Stat.GlobalCannotCrit
        or Stat.VirtualGlobalCannotCrit or Stat.CannotCriticalStrikeWithAttacks;

    public static (bool LowLife, bool Focused, int ExtraRolls) CriticalContext(Player conn)
    {
        var player = CharacterPlayerStats(conn);
        bool focused = Stat.HasFocus is var key && player.GetValueOrDefault(key) != 0;
        return (CriticalOnLowLife(conn, player), focused, CriticalExtraRollsFor(conn, player));
    }

    // Compute the native virtual_on_low_life input from current pools. Actions capture its luck contribution at use;
    // the panel continues to show the owner's current state.
    // Focus and Vulnerability use their current raw state inputs; their skill/curse lifecycles are separate mechanics.
    public static bool CriticalOnLowLife(Player conn, IReadOnlyDictionary<uint, int> player) =>
        CriticalOnLowLife(() => conn.PlayerLife, () => conn.PlayerMaxLife, player);

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

    public static int CriticalExtraRolls(Player conn, IReadOnlyDictionary<uint, int>? own = null)
    {
        lock (conn.Monsters)
            return CriticalExtraRollsFor(conn, CharacterPlayerStats(conn), own);
    }

    public static int CriticalExtraRollsFor(Player conn, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own = null) =>
        CriticalExtraRollsFor(CriticalOnLowLife(conn, player), player, own);

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

    public static bool CriticalProhibited(Player conn, bool attack, IReadOnlyDictionary<uint, int>? own = null)
        => CriticalProhibitedBy(CharacterPlayerStats(conn), attack, own);

    public static bool CriticalProhibitedBy(IReadOnlyDictionary<uint, int> player, bool attack,
        IReadOnlyDictionary<uint, int>? own)
    {
        bool Flag(uint key) => (player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0);
        return Flag(Stat.GlobalCannotCrit) || Flag(Stat.VirtualGlobalCannotCrit)
            || attack && Flag(Stat.CannotCriticalStrikeWithAttacks);
    }

    // The original per-type damage draws still precede the original critical Next(10000). Conditional luck adds
    // at most one following draw. This RNG order is a local policy, not evidence of the official server's sequence.
    public static bool RollCriticalStrike(Player conn, int chance, IReadOnlyDictionary<uint, int>? own = null,
        bool attack = true, SkillCriticalRoll? skillRoll = null, IReadOnlyDictionary<uint, int>? player = null)
    {
        if (chance <= 0 && !HoldingFishingRod(conn)) return false;
        lock (conn.Monsters)
        {
            player ??= CharacterPlayerStats(conn);
            if (AlwaysCritical(conn, name => name is var key ? player.GetValueOrDefault(key) : 0))
                return !CriticalProhibitedBy(player, attack, own);
            if (chance <= 0) return false;
            int allowedChance = CriticalProhibitedBy(player, attack, own) ? 0 : chance;
            return skillRoll is not null ? skillRoll.Check(allowedChance, conn.CriticalRandom)
                : CriticalStrikePolicy.Roll(allowedChance, conn.CriticalRandom, CriticalExtraRollsFor(conn, player, own));
        }
    }

    public static SkillCriticalRoll CaptureSkillCriticalRoll(Player conn, ushort skillId)
    {
        lock (conn.Monsters)
        {
            int effect = SkillEffectId(conn, skillId) is { } id ? SkillsFor(conn).EffectRow(id) : -1;
            var own = skillId == SkillCatalog.FirstSkillId
                ? SkillsFor(conn).EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2)
                : SupportedSkill(conn, skillId).Stats;
            return new(CriticalExtraRollsFor(conn, CharacterPlayerStats(conn), own));
        }
    }

    // Baited Breath (always_crit_while_holding_fishing_rod): while holding a Fishing Rod every hit is a critical strike
    // without a roll, whatever its base chance; a cannot-crit prohibition still wins (wiki). The client's
    // virtual_always_crit calculator (0x1427625b0) reads the stat only for main_hand_weapon_type 15; PoB: CritChance
    // OVERRIDE 100.
    static bool AlwaysCritical(Player conn, Func<uint, int> get) =>
        get(Stat.AlwaysCritWhileHoldingFishingRod) != 0 && HoldingFishingRod(conn);

    // The chance the panel shows under it: 100% where the hit could crit at all; a 0% base still shows 0 although its
    // hits crit (wiki Baited Breath). Prohibited chances are already 0.
    static int DisplayedCriticalChance(Player conn, int chance, Func<uint, int> get) =>
        chance > 0 && AlwaysCritical(conn, get) ? 10_000 : chance;

    public static int AttackCriticalMultiplier(Player conn, IReadOnlyDictionary<uint, int> player,
        int effect = -1, IReadOnlyDictionary<uint, int>? own = null) =>
        AttackCriticalMultiplier(conn.Pools, WeaponProfileFor(conn, activeSet: true).Unarmed, MeleeAttack(conn, effect),
            effect >= 0 && SkillsFor(conn).IsSpell(effect), player, own);

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
