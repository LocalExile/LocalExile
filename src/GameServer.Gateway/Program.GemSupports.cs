using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

partial class Program
{
    internal static readonly GemSupports Supports = new();
    internal sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        Dictionary<uint, int> Stats, IReadOnlyList<(uint, int)> Modifiers, double CostMultiplier);

    // Use the complete identity list: assigning just the socketed subset would retire host-zero passive grants.
    internal static SupportedGem SupportedSkill(ClientState conn, ushort skillId)
    {
        var grant = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (grant.Name is null) return new(-1, 1, 0, [], [], [], 1);
        if (grant.Gem == 0) return new(grant.Effect, grant.Level, grant.Quality, [],
            Skills.EffectStats(grant.Effect, grant.Level, grant.Quality).ToDictionary(s => s.Item1, s => s.Item2), [], 1);
        var inventory = ItemsOf(conn);
        var places = inventory.SocketedGemPlaces();
        var active = places.SelectMany(p => Items.GemEffects(p.Gem).Select(e => (Place: p, Effect: e))).ToList();
        var passive = PassiveEffects.Skills(PhreciaNodes(conn)).ToArray();
        var ids = GemSkillIdsOf.GetOrAdd(CharacterName(conn), _ => new GemSkillIds()).Assign(
            [.. active.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect)),
                .. passive.Select(g => new GemSkillIds.Gem(0, g.Node, g.Effect))], GemSkillIdPool.Value);
        int index = Enumerable.Range(0, active.Count).FirstOrDefault(i => ids[i] == skillId, -1);
        if (index < 0) return new(-1, 1, 0, [], [], [], 1);
        var selected = active[index];
        var host = inventory.SocketHost(selected.Place.ItemId)!;
        int group = GemSupports.LinkGroup(host, selected.Place.Socket);
        var candidates = new List<GemSupports.Candidate>();
        foreach (var p in places.Where(p => p.ItemId == selected.Place.ItemId
            && GemSupports.LinkGroup(host, p.Socket) == group && GemRequirementsUnmet(conn, p.Gem) is null))
            if (Items.ByHash(p.Gem.TypeHash) is { } type && Items.Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, Items.GemLevel(p.Gem), p.Gem.Quality, p.Socket));
            }
        var supports = Supports.Resolve(selected.Effect, candidates, Skills, ActiveMainHand(conn)?.Type?.ClassRow);
        int Value(string stat) => Stats.Key(stat) is uint k ? supports.Sum(s => s.Stats.Where(v => v.Key == k).Sum(v => v.Value)) : 0;
        // Family/global bonuses are already composed; requirements still use the intrinsic gem.
        int level = Math.Clamp(grant.Level + Value("supported_active_skill_gem_level_+"), 1, 40);
        int quality = grant.Quality + Value("supported_active_skill_gem_quality_%");
        var modifiers = new Dictionary<uint, int>();
        foreach (var support in supports)
            foreach (var (key, value) in support.Stats)
            {
                string stat = Stats.Name(key) ?? "";
                if (stat is "base_spell_cast_time_ms" or "virtual_base_spell_cast_time_ms" or "hundred_times_casts_per_second"
                    or "casting_spell" or "base_mana_cost" or "mana_cost" or "base_life_cost" or "life_cost") continue;
                modifiers[key] = stat.EndsWith("_final", StringComparison.Ordinal) && modifiers.TryGetValue(key, out int previous)
                    ? (int)Math.Round(100 * ((1 + previous / 100.0) * (1 + value / 100.0) - 1))
                    : modifiers.GetValueOrDefault(key) + value;
            }
        // Support added damage is local to this link group, never a character source.
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                if (Stats.Key($"global_{bound}_added_{type}_damage") is uint k && modifiers.TryGetValue(k, out int damage))
                    foreach (string kind in new[] { "attack", "spell" })
                        if (Stats.Key($"{kind}_{bound}_added_{type}_damage") is uint target)
                            modifiers[target] = modifiers.GetValueOrDefault(target) + damage;
        var own = SumSkillInputs(Skills.EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2), modifiers);
        return new(selected.Effect, level, quality, supports, own, [.. modifiers.Select(s => (s.Key, s.Value))],
            supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0));
    }

    internal static Dictionary<uint, int> SumSkillInputs(IReadOnlyDictionary<uint, int> player, IEnumerable<(uint, int)> own)
    {
        var result = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own)
            result[key] = (int)Math.Clamp((long)result.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return result;
    }

    static Dictionary<uint, int> SumSkillInputs(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own) =>
        SumSkillInputs(player, own.Select(s => (s.Key, s.Value)));

    internal static Dictionary<uint, int> SupportedStatsFor(ClientState conn, ushort skillId,
        IReadOnlyDictionary<uint, int> character, bool characterStatsWin = false)
    {
        var gem = SupportedSkill(conn, skillId);
        return Skills.EffectStatsFor(gem.Effect, gem.Level, gem.Quality, character, characterStatsWin, gem.Modifiers);
    }

    static Dictionary<uint, int> SupportedStats(ClientState conn, ushort skillId) =>
        SupportedStatsFor(conn, skillId, CharacterPlayerStats(conn));

    static double SupportedAttackSeconds(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        var player = CharacterPlayerStats(conn);
        var stats = SumSkillInputs(player, gem.Stats);
        var (speed, more) = SupportedAttackSpeed(conn, gem, player);
        return SkillAttackTiming.Seconds(WeaponFor(conn).AttackMs, speed, Skills.AttackSpeedMultiplier(gem.Effect, gem.Level),
            ActionSpeed.Multiplier(stats, Stats.Key) * more);
    }

    static (int Increased, double More) SupportedAttackSpeed(ClientState conn, SupportedGem gem,
        IReadOnlyDictionary<uint, int> player)
    {
        var profile = WeaponProfileFor(conn, activeSet: true);
        int Own(string stat) => Stats.Key(stat) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        double factor = (1 + profile.LocalAttackSpeedPct / 100.0) * (profile.DualWield || profile.Unarmed ? 1.1 : 1);
        int speed = player.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct)
            + (int)Math.Round((Own("attack_speed_+%") + Own("attack_and_cast_speed_+%")) * factor);
        double more = Math.Max(.01, 1 + Own("support_melee_physical_damage_attack_speed_+%_final") / 100.0)
            * Math.Max(.01, 1 + Own("support_multistrike_attack_speed_+%_final") / 100.0);
        return (speed, more);
    }

    internal static DamagePacket SupportedHit(ClientState conn, ushort skillId,
        IReadOnlyDictionary<uint, int>? character = null, SupportedGem? captured = null)
    {
        var gem = captured ?? SupportedSkill(conn, skillId);
        character ??= CharacterPlayerStats(conn);
        var plan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return DamagePacket.Empty;
        bool attack = plan.Kind == SkillHitKind.Attack;
        var raw = SumSkillInputs(character, gem.Stats);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        if (attack && Stats.Key("attack_damage_+%") is uint attackIncrease)
            raw[attackIncrease] = raw.GetValueOrDefault(attackIncrease)
                + (types.Contains(7) && Stats.Key("area_damage_+%") is uint area ? raw.GetValueOrDefault(area) : 0)
                + (types.Contains(2) && Stats.Key("projectile_damage_+%") is uint projectile ? raw.GetValueOrDefault(projectile) : 0);
        int Get(string name) => Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0;
        if (!SkillRestrictions.From(Get).DealsDamage(Skills.IsSpell(gem.Effect))) return DamagePacket.Empty;
        var hit = attack
            ? AttackSkillHit.BaseHit(WeaponFor(conn), raw, Stats, MeleeAttack(conn, gem.Effect),
                Skills.AttackBaseMultiplier(gem.Effect, gem.Level), Skills.DamageEffectiveness(gem.Effect, gem.Level))
            : SpellDamage.Packet(plan.SpellBase, Get, AddedEffectiveness(gem.Effect, gem.Level));
        var modifiers = gem.Modifiers.Select(s => (Stats.Name(s.Item1) ?? "", s.Item2)).ToList();
        hit = GemSupportDamage.Scale(hit, modifiers, types, afterConversion: false);
        var conversionInputs = new Dictionary<uint, int>(character);
        foreach (var (key, value) in Skills.EffectStats(gem.Effect, gem.Level)) conversionInputs.TryAdd(key, value);
        conversionInputs = SumSkillInputs(SumSkillInputs(conversionInputs, Skills.QualityStats(gem.Effect, gem.Quality)), gem.Modifiers);
        hit = hit.Convert(DamageStats.Conversion(conversionInputs, Stats, attack));
        return GemSupportDamage.Scale(hit, modifiers, types, afterConversion: true);
    }

    internal static int SupportedCriticalChance(ClientState conn, ushort skillId,
        IReadOnlyDictionary<uint, int>? player = null, SupportedGem? captured = null)
    {
        var gem = captured ?? SupportedSkill(conn, skillId);
        player ??= CharacterPlayerStats(conn);
        int Own(string name) => Stats.Key(name) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (Skills.IsAttack(gem.Effect))
        {
            if (new[] { "global_cannot_crit", "virtual_global_cannot_crit", "cannot_critical_strike_with_attacks" }
                .Any(name => Player(name) != 0 || Own(name) != 0)) return 0;
            var weapon = WeaponProfileFor(conn, activeSet: true);
            int basis = AttackBaseCriticalStrike.OfMainHand(!weapon.Unarmed, weapon.Critical, Player);
            int totalBase = (int)Math.Clamp((long)basis + Own("additional_base_critical_strike_chance"), int.MinValue, int.MaxValue);
            return DamageModel.CriticalStrikeChancePerMyriad(totalBase,
                conn.Pools.CriticalStrikeChancePct + AttackBaseCriticalStrike.IncreasedSum(weapon, name => Own(name)));
        }
        int spellBase = (int)Math.Clamp((long)(Skills.SpellCritChance(gem.Effect, gem.Level) ?? 0)
            + Own("additional_base_critical_strike_chance"), int.MinValue, int.MaxValue);
        return SpellCriticalStrike.Chance(spellBase, Stats, player, gem.Stats, conn.Pools.SpellCriticalStrikeChancePct);
    }

    static (int Flat, int Percent)? SupportedReservation(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (ReservationFor(conn, gem.Effect, gem.Level) is not { } reservation) return null;
        return ((int)Math.Floor(reservation.Flat * gem.CostMultiplier), (int)Math.Floor(reservation.Percent * gem.CostMultiplier));
    }

    internal static int SupportedCriticalMultiplier(ClientState conn, ushort skillId, SupportedGem? captured = null)
    {
        var gem = captured ?? SupportedSkill(conn, skillId);
        int extra = Stats.Key("base_critical_strike_multiplier_+") is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        return CriticalMultiplierFor(conn, spell: !Skills.IsAttack(gem.Effect), melee: MeleeAttack(conn, gem.Effect)) + extra;
    }
}
