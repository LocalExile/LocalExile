using GameServer.Core.World;

public static partial class Game
{
    const uint SpiderMin = Stat.MinimumAddedChaosDamagePerSpidersWebOnEnemy;
    const uint SpiderMax = Stat.MaximumAddedChaosDamagePerSpidersWebOnEnemy;
    const uint SpiderThree = Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAtLeast3SpidersWebs;
    static bool HasSpiderDamage(Func<uint, int> get) => get(SpiderMin) != 0 || get(SpiderMax) != 0 || get(SpiderThree) != 0;
    public static long SpiderDamageIncrease(Func<uint, int> get, int webs) => webs >= 3 ? get(SpiderThree) : 0;
    public static DamagePacket SpiderRawDamage(DamagePacket raw, Func<uint, int> get, int webs, double effectiveness = 1)
        => webs <= 0 ? raw : raw.Add(DamageType.Chaos,
            new(get(SpiderMin) * (double)webs * effectiveness, get(SpiderMax) * (double)webs * effectiveness));

    public static SpiderDamageSource? CaptureSpiderDamage(Player player, SupportedHitInputs inputs, double effectiveness,
        bool context = false, IReadOnlyDictionary<uint, int>? local = null, bool bow = false)
    {
        if (!HasSpiderDamage(inputs.Get) && !HasTypedTargetDamage(inputs.Get)) return null;
        int per = inputs.Get(Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinal);
        int cap = inputs.Get(Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinalMaximum);
        double igniteFactor = per > 0 ? Math.Max(0, 1 + Math.Min((long)per * RecentlyIgnitedCount(player,
            ProjectileWorldTime(player)), cap > 0 ? cap : long.MaxValue) / 100d) : 1;
        return new(inputs.Raw, inputs.Conversion, new Dictionary<uint, int>(inputs.FrozenStats),
            new Dictionary<uint, int>(inputs.Caster), inputs.Attack, inputs.Melee, inputs.Area, inputs.Projectile,
            inputs.Modifiers.ToArray(), new HashSet<int>(inputs.Types), effectiveness, inputs.WieldingWand,
            Context: context, Local: local is null ? null : new Dictionary<uint, int>(local), Bow: bow || inputs.WieldingBow,
            IgniteFactor: igniteFactor, WeaponIncreased: inputs.WeaponIncreased);
    }

    public static PlacedSkills.DamageProfile SpiderDamageProfile(SpiderDamageSource source, int webs, bool blinded,
        bool ignited = false, bool bleeding = false, bool lowLife = false)
    {
        int RawStat(uint key) => source.Stats.GetValueOrDefault(key);
        int Get(uint name) => TypedTargetDamageStat(RawStat, name, ignited, bleeding, lowLife, attack: source.Attack && !source.Secondary);
        long increased = SpiderDamageIncrease(Get, webs) + (blinded ? Get(PlayerHitDamage.VsBlindedDamageStat) : 0);
        var raw = SpiderRawDamage(source.Raw, Get, webs, source.Effectiveness);
        var hitGet = WeaponAttributeDamage.Read(source.Attack && !source.Secondary
            && WeaponAttributeDamage.Armed(Get(Stat.MainHandWeaponType)), Get);
        var hit = PlayerHitDamage.Resolve(raw, hitGet, source.Conversion, spell: !source.Attack && !source.Secondary,
            melee: source.Melee, area: source.Area, projectile: source.Projectile, supportModifiers: source.Modifiers,
            skillTypes: source.Types, secondary: source.Secondary, wieldingWand: source.WieldingWand,
            retaliation: source.Attack && source.Types.Contains(120),
            additionalIncreased: increased + (source.Attack ? source.WeaponIncreased : 0),
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
        int Get(uint n) => GolemStat(player, n);
        if (webs <= 0 || !HasSpiderDamage(Get)) return player;
        var result = new Dictionary<uint, int>(player);
        foreach (var (name, value) in new[] { (Stat.AttackMinimumAddedChaosDamage, Get(SpiderMin)),
                     (Stat.AttackMaximumAddedChaosDamage, Get(SpiderMax)) })
            if (name is var key) result[key] = ActorScale.Saturate((long)result.GetValueOrDefault(key) + (long)value * webs);
        return result;
    }
}
