using GameServer.Core.Protocol.Instance;

partial class Program
{
    internal static readonly GemSupports Supports = new();
    sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        Dictionary<uint, int> Stats, Item? SourceGem = null, bool RequirementsMet = true);

    static SupportedGem SupportedSkill(ClientState conn, ushort skillId)
    {
        lock (conn.Monsters) return SupportedSkillAt(conn, skillId, null);
    }

    static SupportedGem SupportedSkillAt(ClientState conn, ushort skillId, GemRequirementSnapshot? requirements)
    {
        string name = CharacterName(conn);
        var equipment = requirements?.Equipment ?? ItemsOf(conn).EquipmentSnapshot();
        var places = GemRequirementSnapshot.PlacesIn(equipment);
        var active = places.SelectMany(p => Items.GemEffects(p.Gem).Select(e => (Place: p, Effect: e))).ToList();
        var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds()).Assign(
            [.. active.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect))], GemSkillIdPool.Value);
        int index = Enumerable.Range(0, ids.Count).FirstOrDefault(i => ids[i] == skillId, -1);
        if (index < 0) return new(-1, 1, 0, [], []);
        var selected = active[index];
        // Vaal source/support eligibility must not inherit an earlier equipment notification's cached pools.
        if (requirements is null && Skills.VaalBudget(selected.Effect, Items.GemLevel(selected.Place.Gem)) is not null)
            requirements = CurrentGemRequirements(conn, equipment);
        var host = equipment.Single(e => e.Item.Id == selected.Place.ItemId).Item.Item;
        int group = GemSupports.LinkGroup(host, selected.Place.Socket);
        int Quality(int socket, Item gem)
        {
            int colour = Items.ByHash(gem.TypeHash) is { } b ? Items.Gem(b)?.Colour ?? 0 : 0;
            return gem.Quality + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        foreach (var p in places.Where(p => p.ItemId == selected.Place.ItemId
            && GemSupports.LinkGroup(host, p.Socket) == group && GemRequirementsUnmetAt(conn, p.Gem, requirements) is null))
            if (Items.ByHash(p.Gem.TypeHash) is { } type && Items.Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, Items.GemLevel(p.Gem), Quality(p.Socket, p.Gem), p.Socket));
            }
        var weapon = equipment.Where(e => e.Row == PlayerInventories.Weapon1).Select(e => e.Item).FirstOrDefault();
        int? weaponClass = weapon is null ? null : Items.ByHash(weapon.Item.TypeHash)?.ClassRow;
        var supports = Supports.Resolve(selected.Effect, candidates, Skills, weaponClass);
        int Value(string stat) => Stats.Key(stat) is uint k ? supports.Sum(s => s.Stats.Where(v => v.Key == k).Sum(v => v.Value)) : 0;
        int level = EffectiveGemLevel(conn, selected.Place.Gem, Value("supported_active_skill_gem_level_+"));
        int quality = Quality(selected.Place.Socket, selected.Place.Gem) + Value("supported_active_skill_gem_quality_%");
        var own = Skills.EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        foreach (var support in supports)
            foreach (var (key, value) in support.Stats)
            {
                string stat = Stats.Name(key) ?? "";
                if (stat is "base_spell_cast_time_ms" or "virtual_base_spell_cast_time_ms" or "hundred_times_casts_per_second"
                    or "casting_spell" or "base_mana_cost" or "mana_cost") continue;
                own[key] = stat.EndsWith("_final", StringComparison.Ordinal) && own.TryGetValue(key, out int previous)
                    ? (int)Math.Round(100 * ((1 + previous / 100.0) * (1 + value / 100.0) - 1))
                    : own.GetValueOrDefault(key) + value;
            }
        double costMultiplier = supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0);
        foreach (string stat in new[] { "mana_cost", "base_mana_cost" })
            if (Stats.Key(stat) is uint k && own.TryGetValue(k, out int cost)) own[k] = (int)Math.Floor(cost * costMultiplier);
        // Global added damage on support gems applies to this skill, not to the character or other link groups.
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                if (Stats.Key($"global_{bound}_added_{type}_damage") is uint k && own.TryGetValue(k, out int damage))
                    foreach (string kind in new[] { "attack", "spell" })
                        if (Stats.Key($"{kind}_{bound}_added_{type}_damage") is uint target)
                            own[target] = own.GetValueOrDefault(target) + damage;
        if (Skills.IsAura(selected.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(selected.Effect, level, quality, supports, own, selected.Place.Gem,
            requirements?.Inactive.Contains(selected.Place.ItemId) != true
                && GemRequirementsUnmetAt(conn, selected.Place.Gem, requirements) is null);
    }

    static Dictionary<uint, int> SupportedStats(ClientState conn, ushort skillId)
    {
        var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
    }

    static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        var result = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own)
        {
            string stat = Stats.Name(key) ?? "";
            bool replace = stat is "mana_cost" or "base_mana_cost" or "casting_spell" or "virtual_base_spell_cast_time_ms"
                or "base_spell_cast_time_ms" or "hundred_times_casts_per_second" || stat.Contains("_base_", StringComparison.Ordinal)
                && (stat.StartsWith("spell_minimum", StringComparison.Ordinal) || stat.StartsWith("spell_maximum", StringComparison.Ordinal));
            result[key] = replace ? value : result.GetValueOrDefault(key) + value;
        }
        return result;
    }

    static double SupportedAttackSeconds(ClientState conn, ushort skillId)
    {
        var stats = SupportedStats(conn, skillId);
        int Get(string stat) => Stats.Key(stat) is uint k ? stats.GetValueOrDefault(k) : 0;
        var gem = SupportedSkill(conn, skillId);
        if (Stats.Key("attack_speed_+%") is uint raw && Stats.Key("main_hand_attack_speed_+%") is uint final)
        {
            var profile = WeaponProfileFor(conn);
            double factor = (1 + profile.LocalAttackSpeedPct / 100.0) * (profile.DualWield || profile.Unarmed ? 1.1 : 1);
            stats[final] = stats.GetValueOrDefault(final) + (int)Math.Round(gem.Stats.GetValueOrDefault(raw) * factor);
        }
        double more = Supports.AttackSpeedMultiplier(gem.Effect, gem.Level) * Math.Max(.01, 1 + Get("support_melee_physical_damage_attack_speed_+%_final") / 100.0)
            * Math.Max(.01, 1 + Get("support_multistrike_attack_speed_+%_final") / 100.0);
        return Math.Max(TickSeconds, WeaponFor(conn).AttackSeconds(stats) / more);
    }

    static GameServer.Core.World.DamagePacket SupportedHit(ClientState conn, ushort skillId)
        => SupportedHitFor(conn, skillId, suppress: true);

    internal static GameServer.Core.World.DamagePacket SupportedProjectileHit(ClientState conn, ushort skillId, bool area)
        => SupportedHitFor(conn, skillId, suppress: false, area);

    static GameServer.Core.World.DamagePacket SupportedHitFor(ClientState conn, ushort skillId, bool suppress, bool? area = null)
    {
        var gem = SupportedSkill(conn, skillId);
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, gem.Stats);
        int Get(string name) => name == "is_area_damage" && area is bool isArea ? isArea ? 1 : 0
            : Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        bool attack = Skills.IsAttack(gem.Effect);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        if (area is true) types.Add(7);
        else if (area is false) types.Remove(7);
        var plan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return GameServer.Core.World.DamagePacket.Empty;
        var hit = attack
            ? SpellDamage.WithEffectiveness(SupportedAttackDamage.Hit(WeaponFor(conn), Get, types), Skills.DamageEffectiveness(gem.Effect, gem.Level))
            : SpellDamage.Packet(plan.SpellBase, Get, AddedEffectiveness(gem.Effect, gem.Level));
        var modifiers = gem.Supports.SelectMany(s => s.Stats).Select(s => (Stats.Name(s.Key) ?? "", s.Value)).ToList();
        hit = GemSupportDamage.Scale(hit, modifiers, types, afterConversion: false);
        hit = hit.Convert(DamageStats.Conversion(stats, Stats, attack));
        hit = GemSupportDamage.Scale(hit, modifiers, types, afterConversion: true);
        if (attack) hit = RageStats.ApplyAttack(hit, Get);
        return suppress ? new SkillDamageContext(plan.Kind, caster).Hit(hit) : hit;
    }

    static int SupportedCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, false);

    // A release retains the unprohibited offensive chance; the hit evaluates current owner prohibitions separately.
    static int SupportedReleaseCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, true);

    static int SupportedCriticalChanceFor(ClientState conn, ushort skillId, bool release)
    {
        var gem = SupportedSkill(conn, skillId);
        var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        int Own(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        int Player(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (Skills.IsAttack(gem.Effect))
        {
            if (new[] { "global_cannot_crit", "virtual_global_cannot_crit", "cannot_critical_strike_with_attacks" }
                .Any(name => Player(name) != 0 || Own(name) != 0)) return 0;
            var weapon = WeaponProfileFor(conn, activeSet: true);
            int basis = weapon.Unarmed && (!Skills.IsMelee(gem.Effect) || Skills.IsSpell(gem.Effect)) ? 0
                : AttackBaseCriticalStrike.OfMainHand(!weapon.Unarmed, weapon.Critical, Player);
            int totalBase = (int)Math.Clamp((long)basis + Own("additional_base_critical_strike_chance"), int.MinValue, int.MaxValue);
            long increased = conn.Pools.CriticalStrikeChancePct
                + AttackBaseCriticalStrike.IncreasedSum(weapon, name => Own(name));
            return GameServer.Core.World.DamageModel.CriticalStrikeChancePerMyriad(totalBase, increased);
        }
        return SpellCriticalStrike.Chance((Skills.SpellCritChance(gem.Effect, gem.Level) ?? 0)
            + Own("additional_base_critical_strike_chance"), Player, Own, conn.Pools.SpellCriticalStrikeChancePct);
    }

    static (int Flat, int Percent)? SupportedReservation(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (Skills.Reservation(gem.Effect, gem.Level) is not { } reservation) return null;
        double factor = gem.Supports.Aggregate(1.0, (v, support) => v * support.CostMultiplier / 100.0);
        return ((int)Math.Floor(reservation.Flat * factor), (int)Math.Floor(reservation.Percent * factor));
    }

    static int SupportedCriticalMultiplier(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        int extra = Stats.Key("base_critical_strike_multiplier_+") is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        return (int)Math.Clamp((long)(Skills.IsAttack(gem.Effect)
            ? AttackCriticalMultiplier(conn, CharacterPlayerStats(conn), gem.Effect, gem.Stats)
            : conn.Pools.SpellCriticalStrikeMultiplier) + extra, int.MinValue, int.MaxValue);
    }
}
