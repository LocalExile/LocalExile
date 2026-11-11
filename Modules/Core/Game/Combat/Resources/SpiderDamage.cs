using GameServer.Core.World;

public static partial class Game
{
    const string SpiderMin = "minimum_added_chaos_damage_per_spiders_web_on_enemy";
    const string SpiderMax = "maximum_added_chaos_damage_per_spiders_web_on_enemy";
    const string SpiderThree = "hit_and_ailment_damage_+%_vs_enemies_affected_by_at_least_3_spiders_webs";
    static bool HasSpiderDamage(Func<string, int> get) => get(SpiderMin) != 0 || get(SpiderMax) != 0 || get(SpiderThree) != 0;
    public static long SpiderDamageIncrease(Func<string, int> get, int webs) => webs >= 3 ? get(SpiderThree) : 0;
    public static DamagePacket SpiderRawDamage(DamagePacket raw, Func<string, int> get, int webs, double effectiveness = 1)
        => webs <= 0 ? raw : raw.Add(DamageType.Chaos,
            new(get(SpiderMin) * (double)webs * effectiveness, get(SpiderMax) * (double)webs * effectiveness));

    public static SpiderDamageSource? CaptureSpiderDamage(ClientState conn, SupportedHitInputs inputs, double effectiveness,
        bool context = false, IReadOnlyDictionary<string, int>? local = null, bool bow = false)
    {
        if (!HasSpiderDamage(inputs.Get) && !HasTypedTargetDamage(inputs.Get)) return null;
        int per = inputs.Get("support_recent_ignites_ignite_damage_per_recent_ignite_+%_final");
        int cap = inputs.Get("support_recent_ignites_ignite_damage_per_recent_ignite_+%_final_maximum");
        double igniteFactor = per > 0 ? Math.Max(0, 1 + Math.Min((long)per * RecentlyIgnitedCount(conn,
            ProjectileWorldTime(conn)), cap > 0 ? cap : long.MaxValue) / 100d) : 1;
        return new(inputs.Raw, inputs.Conversion, new Dictionary<uint, int>(inputs.FrozenStats),
            new Dictionary<uint, int>(inputs.Caster), inputs.Attack, inputs.Melee, inputs.Area, inputs.Projectile,
            inputs.Modifiers.ToArray(), new HashSet<int>(inputs.Types), effectiveness, inputs.WieldingWand,
            Context: context, Local: local is null ? null : new Dictionary<string, int>(local), Bow: bow || inputs.WieldingBow,
            IgniteFactor: igniteFactor);
    }

    public static PlacedSkills.DamageProfile SpiderDamageProfile(SpiderDamageSource source, int webs, bool blinded,
        bool ignited = false, bool bleeding = false)
    {
        int RawStat(string name) => Stats.Key(name) is uint key ? source.Stats.GetValueOrDefault(key) : 0;
        int Get(string name) => TypedTargetDamageStat(RawStat, name, ignited, bleeding);
        long increased = SpiderDamageIncrease(Get, webs) + (blinded ? Get(PlayerHitDamage.VsBlindedDamageStat) : 0);
        var raw = SpiderRawDamage(source.Raw, Get, webs, source.Effectiveness);
        var hit = PlayerHitDamage.Resolve(raw, Get, source.Conversion, spell: !source.Attack && !source.Secondary,
            melee: source.Melee, area: source.Area, projectile: source.Projectile, supportModifiers: source.Modifiers,
            skillTypes: source.Types, secondary: source.Secondary, wieldingWand: source.WieldingWand,
            retaliation: source.Attack && source.Types.Contains(120), additionalIncreased: increased,
            wieldingBow: source.Attack && source.Bow);
        if (source.Attack)
        {
            hit = RageStats.ApplyAttack(hit, Get);
            if (source.Volatile) hit = Volatility.Apply(hit, Get);
            hit = MaximumAttackDamage.Apply(hit, Get);
        }
        if (source.Context) hit = new SkillDamageContext(source.Attack ? SkillHitKind.Attack : SkillHitKind.Spell, source.Caster).Hit(hit);
        string hand = source.Secondary ? "secondary" : source.Attack ? "main_hand" : "spell";
        var rows = AilmentPanelStats.Compute(new(hand, Spell: !source.Attack && !source.Secondary, source.Melee),
            raw, source.Conversion, Get, increased);
        var ignite = WithIgniteSpread(MonsterIgnites.Settings.From(rows, hand, Get), source.Stats, null,
            !source.Attack && !source.Secondary, hand);
        ignite = ignite with { Min = ignite.Min * source.IgniteFactor, Max = ignite.Max * source.IgniteFactor,
            CriticalMin = ignite.CriticalMin * source.IgniteFactor, CriticalMax = ignite.CriticalMax * source.IgniteFactor };
        var poison = MonsterPoisons.Settings.From(rows, hand, Get, source.Local, projectileAttack: source.Attack && source.Projectile);
        poison = poison with { Min = poison.Min * source.PoisonFactor, Max = poison.Max * source.PoisonFactor,
            CriticalMin = poison.CriticalMin * source.PoisonFactor, CriticalMax = poison.CriticalMax * source.PoisonFactor };
        var bleed = source.Attack ? BleedSettings(rows, hand, Get, source.Local, source.Melee, source.Bow,
            projectileAttack: source.Projectile) : MonsterIgnites.Settings.None;
        return new(hit, ignite, bleed, poison);
    }

    static IReadOnlyDictionary<uint, int> SpiderAttackStats(IReadOnlyDictionary<uint, int> player, int webs)
    {
        int Get(string n) => GolemStat(player, n);
        if (webs <= 0 || !HasSpiderDamage(Get)) return player;
        var result = new Dictionary<uint, int>(player);
        foreach (var (name, value) in new[] { ("attack_minimum_added_chaos_damage", Get(SpiderMin)),
                     ("attack_maximum_added_chaos_damage", Get(SpiderMax)) })
            if (Stats.Key(name) is uint key) result[key] = ActorScale.Saturate((long)result.GetValueOrDefault(key) + (long)value * webs);
        return result;
    }
}
