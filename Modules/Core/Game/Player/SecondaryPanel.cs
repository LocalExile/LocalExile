using GameServer.Core.World;

public static partial class Game
{
    /// <summary>
    /// The Offence tab's secondary-damage rows (issue #75) for a selected skill that is neither an attack nor a spell
    /// but deals secondary damage (<c>display_skill_deals_secondary_damage</c>: Bear Trap, Herald of Ice's shatter):
    /// its own <c>secondary_*_base_*_damage</c> hit through conversion with the generic, area and projectile buckets
    /// only (<see cref="PlayerHitDamage"/>, no added attack or spell damage), the twelve <c>secondary_*_damage</c>
    /// ranges, the critical chance (the skill's base with the generic increases) and multiplier, the critical-weighted
    /// Damage per Use (these skills ask for the average, not DPS) and the secondary ailment rows. Display rows only:
    /// a placed trap's actual hit uses the same secondary damage inputs.
    /// </summary>
    public static List<(uint Stat, int Value)> SecondaryPanelRows(Player conn, Func<uint, int> get,
        IReadOnlyDictionary<uint, int> player, SupportedGem supported, DamagePacket secondaryBase)
    {
        int Player(uint key) => player.GetValueOrDefault(key);
        int Own(uint key) => supported.Stats.GetValueOrDefault(key);
        var types = Supports.Of(supported.Effect)?.Types.ToHashSet() ?? [];
        var merged = MergeSkillStats(player, supported.Stats);
        var conversion = DamageStats.Conversion(merged, Stats, attack: null);
        var modifiers = supported.Supports.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)).ToList();
        bool area = get(Stat.IsAreaDamage) != 0, projectile = get(Stat.BaseIsProjectile) != 0;
        var raw = SecondaryRawHit(conn, supported, secondaryBase, get);
        var hit = PlayerHitDamage.Resolve(raw, get, conversion, spell: false, area: area, projectile: projectile,
            supportModifiers: modifiers, skillTypes: types, secondary: true);
        int chance = SpellCriticalStrike.Chance(SkillsFor(conn).SpellCritChance(supported.Effect, supported.Level), Player, Own,
            playerIncrease: Player(Stat.CriticalStrikeChancePlusPct));
        int multiplier = CriticalHitMultiplier((long)DamageModel.BaseCriticalStrikeMultiplier
            + Player(Stat.BaseCriticalStrikeMultiplierPlus) + Own(Stat.BaseCriticalStrikeMultiplierPlus), player, supported.Stats);
        double factor = CriticalStrikePolicy.DamageFactor(CriticalProhibitedBy(player, attack: false, supported.Stats) ? 0 : chance,
            multiplier, CriticalExtraRollsFor(conn, player, supported.Stats));
        var rows = new List<(uint, int)> { (Stat.DisplaySkillDealsSecondaryDamage, 1) };
        // The skill inherits the default attack's hand rows with the player's stats: clear them, as a spell does.
        foreach (string hand in new[] { "main_hand", "off_hand" })
            rows.AddRange(PanelStats.DamageRows(hand).Concat(AilmentPanelStats.Rows(hand)).Select(n => (n, 0)));
        var ranges = PanelStats.HandDamage(hit, "secondary").ToDictionary(r => r.Stat, r => r.Value);
        rows.AddRange(PanelStats.DamageRows("secondary").Select(stat => (stat, ranges.GetValueOrDefault(stat))));
        rows.Add((Stat.SecondaryCriticalStrikeChance, chance));
        rows.Add((Stat.SecondaryCriticalStrikeMultiplier, multiplier));
        rows.Add((Stat.DisplayHundredTimesDamagePerSkillUse, SpellDamage.Display((hit.MinTotal, hit.MaxTotal), 0, factor).PerUse));
        rows.Add((Stat.HundredTimesDamagePerSecond, 0));
        rows.AddRange(AilmentPanelStats.Compute(new("secondary", Spell: false),
            raw, conversion, get));
        return rows;
    }

    static DamagePacket SecondaryRawHit(Player conn, SupportedGem gem, DamagePacket basis, Func<uint, int> get) =>
        DamageStats.RawAdded(basis, name => Stats.Name(name).StartsWith("spell_", StringComparison.Ordinal)
            && Stats.Name(name).Contains("_added_", StringComparison.Ordinal) ? 0 : get(name), spell: true,
            AddedEffectiveness(conn, gem.Effect, gem.Level));
}
