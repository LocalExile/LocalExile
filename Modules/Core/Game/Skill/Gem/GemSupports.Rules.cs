using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static readonly GemSupports Supports = new();
    public sealed record SupportedGem(int Effect, int Level, int Quality, IReadOnlyList<GemSupports.Applied> Supports,
        Dictionary<uint, int> Stats, Item? SourceGem = null, bool RequirementsMet = true, bool Granted = false,
        uint Host = 0, int Group = -1);

    public static SupportedGem SupportedSkill(ClientState conn, ushort skillId)
    {
        if (BuildScopeOf(conn) is not { } scope)
            lock (conn.Monsters) return SupportedSkillAt(conn, skillId, null);
        // Within one PlayerSkills build each gem is resolved once; callers get their own copy of its stats.
        if (!scope.Supported.TryGetValue(skillId, out var gem))
            lock (conn.Monsters) scope.Supported[skillId] = gem = SupportedSkillAt(conn, skillId, null);
        return gem with { Stats = new(gem.Stats) };
    }

    public static SupportedGem SupportedSkillAt(ClientState conn, ushort skillId, GemRequirementSnapshot? requirements)
    {
        using var rawSources = new GemSourceScope(conn);
        rawSources.Enter();
        string name = CharacterName(conn);
        var equipment = requirements?.Equipment ?? ItemsOf(conn).EquipmentSnapshot();
        var places = GemRequirementSnapshot.PlacesIn(equipment);
        var active = places
            .Where(p => ItemsFor(conn).ByHash(p.Gem.TypeHash) is not { } type ? !LeagueFor(conn).HardMode : LeagueFor(conn).Rules.AllowsGemBase(type.Id))
            .SelectMany(p => ItemsFor(conn).GemEffects(p.Gem).Select(e => (Place: p, Effect: e))).ToList();
        var granted = GrantedSkillsOf(conn);
        var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds()).Assign(
            [.. active.Select(g => new GemSkillIds.Gem(g.Place.ItemId, g.Place.Socket, g.Effect)),
                .. granted.Select(g => new GemSkillIds.Gem(g.Host, 0, g.Effect))], GemSkillIdPool.Value);
        int index = Enumerable.Range(0, ids.Count).FirstOrDefault(i => ids[i] == skillId, -1);
        if (index < 0) return new(-1, 1, 0, [], []);
        if (index >= active.Count) return GrantedSupportedSkill(conn, granted[index - active.Count]);
        var selected = active[index];
        // Vaal source/support eligibility must not inherit an earlier equipment notification's cached pools.
        if (requirements is null && SkillsFor(conn).VaalBudget(selected.Effect, ItemsFor(conn).GemLevel(selected.Place.Gem)) is not null)
            requirements = CurrentGemRequirements(conn, equipment);
        var host = equipment.Single(e => e.Item.Id == selected.Place.ItemId).Item.Item;
        var local = ItemModStats.Local(host, ItemsFor(conn).Mods);
        (int Level, int Quality) LocalBonus(int socket, Item gem) =>
            SocketedGemBonuses.For(host, socket, gem, ItemsFor(conn), SkillsFor(conn), local, conn.Pools.Level);
        int group = GemSupports.LinkGroup(host, selected.Place.Socket);
        int Quality(int socket, Item gem)
        {
            int colour = ItemsFor(conn).ByHash(gem.TypeHash) is { } b ? ItemsFor(conn).Gem(b)?.Colour ?? 0 : 0;
            return gem.Quality + LocalBonus(socket, gem).Quality
                + (colour is >= 1 and <= 3 && (int)host.Sockets[socket].Colour == colour ? 10 : 0);
        }
        var candidates = new List<GemSupports.Candidate>();
        foreach (var p in places.Where(p => p.ItemId == selected.Place.ItemId
            && GemSupports.LinkGroup(host, p.Socket) == group && GemRequirementsUnmetAt(conn, p.Gem, requirements) is null))
            if (ItemsFor(conn).ByHash(p.Gem.TypeHash) is { } type && ItemsFor(conn).Gem(type) is { IsSupport: true } gem)
            {
                var variant = gem.Variant(p.Gem.GemVariant);
                foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0))
                    candidates.Add(new(effect, EffectiveGemLevel(conn, p.Gem, LocalBonus(p.Socket, p.Gem).Level),
                        Quality(p.Socket, p.Gem), p.Socket));
            }
        candidates.AddRange(Supports.ItemCandidates(host, ItemsFor(conn).Mods));
        var weapon = equipment.Where(e => e.Row == PlayerInventories.Weapon1).Select(e => e.Item).FirstOrDefault();
        int? weaponClass = weapon is null ? null : ItemsFor(conn).ByHash(weapon.Item.TypeHash)?.ClassRow;
        var supports = Supports.Resolve(selected.Effect, candidates, SkillsFor(conn), weaponClass,
            ItemsFor(conn).GemLevel(selected.Place.Gem));
        int Value(string stat) => Stats.Key(stat) is uint k ? supports.Sum(s => s.Stats.Where(v => v.Key == k).Sum(v => v.Value)) : 0;
        // "+1 to Supported <type> Skill Gems" (Awakened supports): every supported_*_skill_gem_level_+ counts, the
        // generic active-gem one included.
        int SupportGemLevels()
        {
            int total = 0;
            foreach (var support in supports)
                foreach (var (key, value) in support.Stats)
                    if (Stats.Name(key) is { } stat && stat.StartsWith("supported_", StringComparison.Ordinal)
                        && stat.EndsWith("_skill_gem_level_+", StringComparison.Ordinal))
                        total += value;
            return total;
        }
        int level = EffectiveGemLevel(conn, selected.Place.Gem,
            SupportGemLevels() + LocalBonus(selected.Place.Socket, selected.Place.Gem).Level);
        int quality = Quality(selected.Place.Socket, selected.Place.Gem) + Value("supported_active_skill_gem_quality_%")
            + GemQualityBonus(conn, selected.Place.Gem);
        var own = SkillsFor(conn).EffectStats(selected.Effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        foreach (var support in supports)
            foreach (var (key, value) in support.Stats)
            {
                string stat = Stats.Name(key) ?? "";
                if (stat is "base_spell_cast_time_ms" or "virtual_base_spell_cast_time_ms" or "hundred_times_casts_per_second"
                    or "casting_spell" or "base_life_cost" or "life_cost" or "base_mana_cost" or "mana_cost" or "base_mana_cost_per_minute" or "mana_cost_per_minute") continue;
                own[key] = stat.EndsWith("_final", StringComparison.Ordinal) && own.TryGetValue(key, out int previous)
                    ? (int)Math.Round(100 * ((1 + previous / 100.0) * (1 + value / 100.0) - 1))
                    : own.GetValueOrDefault(key) + value;
            }
        // Machinations: its "per Keystone" stats count the character's allocated keystones. The chaos gain feeds the
        // plain stats read by damage conversion and resource costs, scoped to this supported skill.
        int? keystones = null;
        foreach (string stat in new[] { "skill_cost_+%_per_keystone", "physical_damage_%_to_add_as_chaos_per_keystone" })
            if (Stats.Key(stat) is uint perKey && own.TryGetValue(perKey, out int per))
            {
                own[perKey] = per * (keystones ??= KeystoneCount(conn));
                if (stat == "physical_damage_%_to_add_as_chaos_per_keystone" && own[perKey] != 0
                    && Stats.Key("physical_damage_%_to_add_as_chaos") is uint plain)
                    own[plain] = own.GetValueOrDefault(plain) + own[perKey];
                if (stat == "skill_cost_+%_per_keystone" && Stats.Key("base_cost_+%") is uint costKey)
                    own[costKey] = own.GetValueOrDefault(costKey) + own[perKey];
            }
        double costMultiplier = supports.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0);
        foreach (string stat in new[] { "mana_cost", "base_mana_cost", "life_cost", "base_life_cost", "mana_cost_per_minute", "base_mana_cost_per_minute", "base_rage_cost", "total_base_rage_cost" })
            if (Stats.Key(stat) is uint k && own.TryGetValue(k, out int cost)) own[k] = (int)Math.Floor(cost * costMultiplier);
        // Global added damage on support gems applies to this skill, not to the character or other link groups.
        foreach (string type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
            foreach (string bound in new[] { "minimum", "maximum" })
                if (Stats.Key($"global_{bound}_added_{type}_damage") is uint k && own.TryGetValue(k, out int damage))
                    foreach (string kind in new[] { "attack", "spell" })
                        if (Stats.Key($"{kind}_{bound}_added_{type}_damage") is uint target)
                            own[target] = own.GetValueOrDefault(target) + damage;
        if (SkillsFor(conn).IsAura(selected.Effect, supports.Select(s => s.Effect.Row)))
            own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        if (SkillsFor(conn).IsMelee(selected.Effect)
            && conn.Auras.Values.FirstOrDefault(a => a.EffectId == "BloodAndSand" && a.AffectsSelf) is { } stance)
        {
            string input = stance.Stance == "Sand" ? "blood_sand_stance_melee_skills_area_of_effect_+%_final_in_sand_stance"
                : "blood_sand_stance_melee_skills_area_of_effect_+%_final_in_blood_stance";
            if (Stats.Key(input) is uint source && Stats.Key("active_skill_area_of_effect_+%_final") is uint target)
            {
                int value = SkillsFor(conn).EffectStats(stance.Effect, stance.Level, stance.Quality).FirstOrDefault(s => s.Item1 == source).Item2;
                own[target] = (int)Math.Round(100 * ((1 + own.GetValueOrDefault(target) / 100.0) * (1 + value / 100.0) - 1));
            }
        }
        return new(selected.Effect, level, quality, supports, own, selected.Place.Gem,
            requirements?.Inactive.Contains(selected.Place.ItemId) != true
                && GemRequirementsUnmetAt(conn, selected.Place.Gem, requirements) is null, Host: selected.Place.ItemId, Group: group);
    }

    public static Dictionary<uint, int> SupportedStats(ClientState conn, ushort skillId)
    {
        var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
    }

    // Generosity: the increased effect a support gives the aura it supports, and whether the aura may affect the player
    // ("Supported Auras do not affect you", aura_cannot_affect_self).
    public static (int EffectPct, bool AffectsSelf) AuraSupport(ClientState conn, ushort skillId)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return (Stat("non_curse_aura_effect_+%") + Stat("aura_effect_+%"), Stat("aura_cannot_affect_self") == 0);
    }

    public static Dictionary<uint, int> MergeSkillStats(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        var result = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own)
        {
            string stat = Stats.Name(key) ?? "";
            bool replace = stat is "base_life_cost" or "life_cost" or "mana_cost" or "base_mana_cost" or "casting_spell" or "virtual_base_spell_cast_time_ms"
                or "base_spell_cast_time_ms" or "hundred_times_casts_per_second" || stat.Contains("_base_", StringComparison.Ordinal)
                && (stat.StartsWith("spell_minimum", StringComparison.Ordinal) || stat.StartsWith("spell_maximum", StringComparison.Ordinal));
            result[key] = replace ? value : ArcaneMight.IsGrant(key)
                ? ArcaneMight.Combine(key, result.GetValueOrDefault(key), value) : result.GetValueOrDefault(key) + value;
        }
        if (Stats.Key("mana_cost") is uint mana && own.TryGetValue(mana, out int baseCost))
            result[mana] = ManaRecoveryRules.Cost(baseCost, name => Stats.Key(name) is uint key ? result.GetValueOrDefault(key) : 0);
        if (Stats.Key("life_cost") is uint life && own.TryGetValue(life, out int nativeLife))
        {
            int Read(string name) => Stats.Key(name) is uint key ? result.GetValueOrDefault(key) : 0;
            result[life] = (int)Math.Clamp(decimal.Floor(nativeLife * Math.Max(0m,
                100m + Read("base_cost_+%") + Read("base_life_cost_+%") + Read("life_cost_+%")) / 100m), 0, int.MaxValue);
        }
        return result;
    }

    public static double SupportedAttackSeconds(ClientState conn, ushort skillId) => SupportedAttackSecondsFor(conn, skillId, null);

    // Multistrike: "Supported Skills Repeat an additional X times" (base_melee_attack_repeat_count). The repeats are
    // hits of the same use, so the skill's attack time is split between them and the support's per-repeat more damage
    // applies to each one after the first.
    public static int MeleeAttackRepeats(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (!SkillsFor(conn).IsAttack(gem.Effect)) return 0;
        return Math.Clamp(Stats.Key("base_melee_attack_repeat_count") is uint key
            ? gem.Stats.GetValueOrDefault(key) : 0, 0, 3);
    }

    public static double MeleeRepeatDamageMore(ClientState conn, ushort skillId, int repeat)
    {
        if (repeat <= 0) return 1;
        var gem = SupportedSkill(conn, skillId);
        string stat = repeat switch
        {
            1 => "multistrike_damage_+%_final_on_first_repeat",
            2 => "multistrike_damage_+%_final_on_second_repeat",
            _ => "multistrike_damage_+%_final_on_third_repeat",
        };
        return Stats.Key(stat) is uint key ? Math.Max(0, 1 + gem.Stats.GetValueOrDefault(key) / 100.0) : 1;
    }

    // Hallow Support: a melee hit inflicts Hallowing Flame, and the next attack against that enemy gains its share of
    // the attack's physical damage as extra fire (the sweep applies and removes it per target).
    public static void HallowOnMeleeHit(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        if (Get("support_hallow_inflict_hallowing_flame_on_melee_hit") == 0) return;
        double seconds = Math.Max(0, Get("hallowing_flame_duration_ms")) / 1000.0;
        if (seconds <= 0) return;
        m.HallowingFlameUntil = Math.Max(m.HallowingFlameUntil, now + seconds);
        m.HallowingFlamePct = 25 * Math.Max(0, 1 + Get("hallowing_flame_magnitude_+%") / 100.0);
    }

    // Excommunicate Support: melee hits excommunicate the enemy for its listed duration; an excommunicated monster's
    // chaos damage is suppressed (the support's physical-as-extra gains already use the conversion stats).
    public static void ExcommunicateOnMeleeHit(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        double milliseconds = Math.Max(0, Get("support_excommunicate_on_melee_attack_hit_for_X_ms"));
        if (milliseconds > 0) m.ExcommunicatedUntil = Math.Max(m.ExcommunicatedUntil, now + milliseconds / 1000.0);
    }

    // Devour Support: a killing blow recovers its flat life/mana and consumes the corpse. The kill path removes the
    // corpse object (ConsumeDevouredCorpseLocked), so the death, its loot and its corpse-blocking cleanup run first.
    public static void DevourOnKill(ClientState conn, SpawnedMonster corpse, IReadOnlyDictionary<uint, int>? skill)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        int life = Get("killing_blow_consumes_corpse_restore_x_life");
        int mana = Get("killing_blow_consumes_corpse_restore_x_mana");
        if (life > 0 && conn.PlayerLife > 0)
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + life);
        if (mana > 0 && conn.PlayerLife > 0)
            conn.PlayerMana = (uint)Math.Min(UnreservedMana(conn), (long)conn.PlayerMana + mana);
        // A shattered or already consumed corpse has nothing left to consume; its life/mana still recovered above, as
        // before, but the kill sends no removal for an object that is already gone.
        if ((life > 0 || mana > 0) && !corpse.Ailments.Shattered && IsCorpse(corpse)) corpse.DevourCorpse = true;
    }

    // FireBeam's duration slot (0x1419ea070) uses base cast time / cast speed, including fractional milliseconds.
    // Channel repeats do not start new actions, so neither spell nor attack channels use the new-action frame cap.
    // A channelled attack swings on its weapon's attack time (Cyclone); a channelled spell pays and stages on its cast
    // time (Winter Orb's 0.25 s, not the caster's weapon).
    public static double? SupportedChannelSeconds(ClientState conn, ushort skillId)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && SkillsFor(conn).IsSpell(gem.Effect) && !SkillsFor(conn).IsAttack(gem.Effect))
        {
            if (SpellTimingFor(conn, gem.Effect, skillId)?.DurationSeconds is not double duration || duration <= 0)
                return null;
            double seconds = duration / PlayerActionFactor(conn);
            return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
        }
        return SupportedAttackSecondsFor(conn, skillId, null, clampToTick: false);
    }

    // The supported attack's swing time with the off-hand weapon of a dual-wielded pair: its own duration at the
    // off-hand increased speed (the main weapon's local speed taken back out), with the same supports.
    public static double SupportedOffHandAttackSeconds(ClientState conn, ushort skillId, SkillCatalog.Unarmed offHand) =>
        SupportedAttackSecondsFor(conn, skillId, offHand);

    public static double SupportedAttackSecondsFor(ClientState conn, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick = true, bool proxy = false) => SupportedAttackTiming(conn, skillId, offHand, clampToTick, proxy).Seconds;

    /// <summary>The supported attack's main-hand increased attack speed, the increase its swing time divides by before
    /// any more multiplier: what the client reads as the skill's <c>combined_main_hand_attack_speed_+%</c>.</summary>
    public static int SupportedMainHandAttackSpeedPct(ClientState conn, ushort skillId) =>
        SupportedAttackTiming(conn, skillId, null, clampToTick: true, proxy: false).Increased;

    static (double Seconds, int Increased) SupportedAttackTiming(ClientState conn, ushort skillId, SkillCatalog.Unarmed? offHand,
        bool clampToTick, bool proxy)
    {
        var stats = SupportedStats(conn, skillId);
        int Get(string stat) => Stats.Key(stat) is uint k ? stats.GetValueOrDefault(k) : 0;
        var gem = SupportedSkill(conn, skillId);
        if (Stats.Key("attack_speed_+%") is uint raw && Stats.Key("main_hand_attack_speed_+%") is uint final)
        {
            var profile = WeaponProfileFor(conn);
            double factor = profile.DualWield || profile.Unarmed ? 1.1 : 1;
            stats[final] = stats.GetValueOrDefault(final) + (int)Math.Round(gem.Stats.GetValueOrDefault(raw) * factor);
            // "Chain Hook and Shield Charge have X% increased Attack Speed per 10 Rampage Kills".
            if (SkillsFor(conn).EffectId(gem.Effect) is { } effectId
                && (effectId.StartsWith("ChainHook", StringComparison.Ordinal)
                    || effectId.StartsWith("ShieldCharge", StringComparison.Ordinal))
                && Stats.Key("chain_hook_and_shield_charge_attack_speed_+%_per_10_rampage_stacks") is uint rampageSpeed
                && stats.GetValueOrDefault(rampageSpeed) is int perTen and > 0)
                stats[final] += conn.Rampage.Current.Stacks / 10 * perTen;
        }
        if (proxy && Stats.Key("main_hand_attack_speed_+%") is uint proxySpeed)
            stats[proxySpeed] = stats.GetValueOrDefault(proxySpeed) + Get("totem_skill_attack_speed_+%")
                + Get("totems_attack_speed_+%_per_active_totem") * conn.Totems.Living(conn.Clock.Elapsed.TotalSeconds).Count()
                + Get("attack_speed_+%_per_maximum_totem") * TotemLimit(conn, skillId);
        double more = Supports.AttackSpeedMultiplier(gem.Effect, gem.Level) * Math.Max(.01, 1 + Get("support_melee_physical_damage_attack_speed_+%_final") / 100.0)
            * Math.Max(.01, 1 + Get("support_multiple_attacks_melee_attack_speed_+%_final") / 100.0)
            // Pulverise: "15% less Attack Speed".
            * Math.Max(.01, 1 + Get("support_pulverise_attack_speed_+%_final") / 100.0)
            // Vicious Projectiles: "10% less Attack Speed".
            * Math.Max(.01, 1 + Get("support_projectile_attack_speed_+%_final") / 100.0);
        if (proxy) more *= Math.Max(.01, 1 + Get("support_attack_totem_attack_speed_+%_final") / 100d);
        int barrageTime = Get("support_barrage_attack_time_+%_per_projectile_fired");
        if (barrageTime > 0 && ResolveProjectilePanelParameters(gem.Effect) is { } projectileParameters)
        {
            var projectiles = projectileParameters.Resolve(Get, 1, ArrowsFor(conn, gem.Effect));
            int count = Get("projectiles_rain") != 0 && projectiles.EndEmission is { } rain
                ? rain.Child.Count : projectiles.Count;
            // The native final-speed consumer divides by 1 + the per-projectile time penalty before rounding.
            more /= 1 + (double)barrageTime * count / 100;
        }
        // Rage Support: "19% increased Attack Speed with at least 10 Rage" (the rage stat set carries current_rage).
        if (Get("current_rage") >= 10 && Get("attack_speed_+%_with_at_least_10_rage") is int rageSpeed and > 0
            && Stats.Key("main_hand_attack_speed_+%") is uint rageBucket)
            stats[rageBucket] += rageSpeed;
        // A skill's own increased Attack Speed per Frenzy Charge (Flicker Strike's 10%): the character's per-charge stats
        // are in its stats already (ChargeEffects), this one only on the skill. Without it the client swung faster than
        // the server's timer allowed and some uses were ignored (live 2026-11-09, 5-6 charges).
        if (Stats.Key("base_attack_speed_+%_per_frenzy_charge") is uint perFrenzy
            && gem.Stats.GetValueOrDefault(perFrenzy) is int skillPerFrenzy and not 0
            && Stats.Key("main_hand_attack_speed_+%") is uint frenzyBucket)
        {
            int frenzy;
            lock (conn.Monsters) frenzy = ChargesOf(conn).Effective(ChargeType.Frenzy);
            stats[frenzyBucket] = stats.GetValueOrDefault(frenzyBucket) + skillPerFrenzy * frenzy;
        }
        // Close Combat's Combat Rush: more Attack Speed for a travel skill it does not support.
        more *= CombatRushMore(conn, skillId, gem, conn.Clock.Elapsed.TotalSeconds);
        // Supports.AttackSpeedMultiplier reads the skill's own GEPL row too: its inherent -20 on Tornado Shot is
        // already part of this factor. Do not apply the same GEPL value again.
        int baseSpeed = offHand is null ? WeaponFor(conn).IncreasedAttackSpeed(stats)
            : SkillCatalog.Unarmed.OffHandAttackSpeedPct(stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct),
                WeaponProfileFor(conn, activeSet: true).LocalAttackSpeedPct);
        double seconds = offHand is null ? WeaponFor(conn).AttackSeconds(stats) : DualWieldPanel.OffHandSeconds(offHand, baseSpeed);
        // Preserve the weapon timing's existing minimum, but compose MORE through the native integer final percent.
        double paced = seconds * Math.Max(.01, 1 + baseSpeed / 100.0)
            / (1 + SkillAttackTiming.FinalSpeedPercent(baseSpeed, more) / 100.0) / (proxy ? 1 : PlayerActionFactor(conn));
        return (clampToTick ? Math.Max(TickSeconds, paced) : Math.Max(.001, paced), baseSpeed);
    }

    public static GameServer.Core.World.DamagePacket SupportedHit(ClientState conn, ushort skillId)
        => SupportedHitFor(conn, skillId, suppress: true);

    // The allocated keystones a "per Keystone" stat counts: the character's own, and the ones a tattoo turns a node
    // into (the node itself is not a keystone then).
    static int KeystoneCount(ClientState conn)
    {
        int count = 0;
        foreach (ushort node in conn.Passives)
            if (Program.Passives.Find(node) is { IsKeystone: true }) count++;
        foreach (var (node, chosen) in conn.TattooKeystones)
            if (Program.Passives.Find(node) is not { IsKeystone: true } && Program.Passives.Find(chosen) is { IsKeystone: true })
                count++;
        return count;
    }

    // Close Combat: a supported melee hit grants Combat Rush, which speeds up travel skills the support does not
    // support. A paid travel use consumes it, retaining the speed only for that use's action.
    public static void CombatRushOnHit(ClientState conn, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        int Get(string name) => skill is not null && Stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0;
        if (Get("supported_by_slashing") == 0) return;
        int milliseconds = Get("support_slashing_buff_base_duration_ms");
        int speed = Get("support_slashing_buff_attack_speed_+%_final_to_grant");
        if (milliseconds <= 0 || speed <= 0) return;
        conn.CombatRushUntil = Math.Max(conn.CombatRushUntil, now + milliseconds / 1000.0);
        conn.CombatRushAttackSpeed = Math.Max(conn.CombatRushAttackSpeed, speed);
    }

    public static double CombatRushMore(ClientState conn, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(conn).IsTravel(gem.Effect) || gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat")) return 1;
        int speed = conn.CombatRushUse is { } use && use.Action == conn.ActionCount && use.Skill == skillId
            ? use.Speed : conn.CombatRushUntil > now ? conn.CombatRushAttackSpeed : 0;
        return Math.Max(.01, 1 + speed / 100.0);
    }

    public static void ConsumeCombatRush(ClientState conn, ushort skillId, SupportedGem gem, double now)
    {
        if (!SkillsFor(conn).IsTravel(gem.Effect) || conn.CombatRushUntil <= now) return;
        conn.CombatRushUse = (conn.ActionCount, skillId,
            gem.Supports.Any(s => s.Effect.Id == "SupportCloseCombat") ? 0 : conn.CombatRushAttackSpeed);
        conn.CombatRushUntil = 0;
        conn.CombatRushAttackSpeed = 0;
    }

    public static GameServer.Core.World.DamagePacket SupportedProjectileHit(ClientState conn, ushort skillId, bool area, int addedChaos = 0)
        => SupportedHitFor(conn, skillId, suppress: false, area, projectile: true, addedChaos: addedChaos);

    // The same supported attack swung with the off-hand weapon of a dual-wielded attack (the Offence tab's Off Hand rows).
    public static GameServer.Core.World.DamagePacket SupportedOffHandHit(ClientState conn, ushort skillId, SkillCatalog.Unarmed offHand)
        => SupportedHitFor(conn, skillId, suppress: true, weapon: offHand);

    // A supported skill's hit inputs before conversion and modifiers, shared by the hit itself and the panel's ailment
    // rows, which apply their own buckets to the same raw packet and conversion. Null when the skill cannot hit.
    public sealed record SupportedHitInputs(GameServer.Core.World.DamagePacket Raw, Func<string, int> Get,
        GameServer.Core.World.DamageConversion Conversion, bool Attack, bool Melee, bool Area, bool Projectile,
        List<(string Name, int Value)> Modifiers, HashSet<int> Types, SkillHitPlan Plan, Dictionary<uint, int> Caster,
        bool WieldingWand = false, bool WieldingBow = false);

    // charges: the removable charge counts a charge-spending use counted (Discharge), instead of the ones held now.
    public static SupportedHitInputs? SupportedHitInputsFor(ClientState conn, ushort skillId, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, bool projectile = false,
        int addedChaos = 0, bool totem = false)
    {
        var gem = SupportedSkill(conn, skillId);
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, gem.Stats);
        if (charges is not null)
            foreach (var (type, count) in charges)
                if (Stats.Key($"current_removable_{Charges.Name(type)}_charges") is uint removable) stats[removable] = count;
        if (totem && Stats.Key("damage_+%") is uint damage)
            stats[damage] = stats.GetValueOrDefault(damage) + GolemStat(stats, "totem_damage_+%");
        int Get(string name) => name == "is_area_damage" && area is bool isArea ? isArea ? 1 : 0
            : Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        bool attack = SkillsFor(conn).IsAttack(gem.Effect);
        var types = Supports.Of(gem.Effect)?.Types.ToHashSet() ?? [];
        // Lightning Strike's two components share a gem, not a damage context. Its bolts do not gain melee
        // modifiers, and its strike does not gain projectile modifiers or the projectile-only less multiplier.
        bool lightningStrike = IsLightningStrike(gem.Effect);
        if (lightningStrike) types.Remove(projectile ? 19 : 2);
        if (area is true) types.Add(7);
        else if (area is false) types.Remove(7);
        var plan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!plan.CanHit) return null;
        var attackWeapon = attack ? weapon ?? WeaponFor(conn) : null;
        var coefficients = SkillsFor(conn).DamageCoefficients(gem.Effect, gem.Level);
        var raw = attackWeapon is not null
            ? SupportedAttackDamage.Raw(attackWeapon, Get, coefficients.Base, coefficients.Added)
            : DamageStats.RawAdded(plan.SpellBaseWith(Get), Get, spell: true, AddedEffectiveness(conn, gem.Effect, gem.Level));
        if (!attack && addedChaos > 0)
            raw = raw.Add(GameServer.Core.World.DamageType.Chaos,
                new GameServer.Core.World.DamageRange(addedChaos, addedChaos).Scale(AddedEffectiveness(conn, gem.Effect, gem.Level)));
        if (lightningStrike && projectile)
            raw = raw.Scale(Math.Max(0, 1 + Get("active_skill_hit_ailment_damage_with_projectile_+%_final") / 100.0));
        // Spellblade: a supported spell gains a share of the one-hand melee weapon's damage as added spell damage,
        // scaled by the spell's damage effectiveness like any other added damage.
        if (!attack && Get("support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage") is int bladeGain and > 0)
        {
            var bladeWeapon = weapon ?? WeaponFor(conn);
            if (bladeWeapon.Melee && bladeWeapon.LocalDamage is not null)
            {
                double effectiveness = AddedEffectiveness(conn, gem.Effect, gem.Level);
                // The weapon's attack-only attribute grant is not added damage for this spell.
                foreach (var (type, range) in SupportedAttackDamage.Raw(bladeWeapon with { LocalSources = null }, Get).Components)
                    raw = raw.Add(type, range.Scale(bladeGain / 100.0 * effectiveness));
            }
        }
        var modifiers = gem.Supports.SelectMany(s => s.Stats).Select(s => (Stats.Name(s.Key) ?? "", s.Value)).ToList();
        return new(raw, Get, DamageStats.Conversion(stats, Stats, attack), attack,
            Melee: attack && types.Contains(19), Area: attack ? types.Contains(7) : Get("is_area_damage") != 0,
            Projectile: attack ? types.Contains(2) : Get("base_is_projectile") != 0, modifiers, types, plan, caster,
            WieldingWand: attackWeapon?.WieldingWand == true,
            WieldingBow: attackWeapon?.Bow == true);
    }

    public static GameServer.Core.World.DamagePacket SupportedHitFor(ClientState conn, ushort skillId, bool suppress, bool? area = null,
        SkillCatalog.Unarmed? weapon = null, IReadOnlyDictionary<ChargeType, int>? charges = null, bool projectile = false,
        int addedChaos = 0)
        => SupportedHitInputsFor(conn, skillId, area, weapon, charges, projectile: projectile, addedChaos: addedChaos) is { } inputs ? SupportedHitFrom(inputs, suppress) : GameServer.Core.World.DamagePacket.Empty;

    // The supported hit from inputs already worked out (GemSkills reads one gem's inputs once for its hit and ailment rows).
    public static GameServer.Core.World.DamagePacket SupportedHitFrom(SupportedHitInputs inputs, bool suppress)
    {
        var hit = PlayerHitDamage.Resolve(inputs.Raw, inputs.Get, inputs.Conversion, spell: !inputs.Attack,
            melee: inputs.Melee, area: inputs.Area, projectile: inputs.Projectile,
            supportModifiers: inputs.Modifiers, skillTypes: inputs.Types,
            wieldingBow: inputs.WieldingBow, wieldingWand: inputs.WieldingWand, retaliation: inputs.Attack && inputs.Types.Contains(120));
        if (inputs.Attack) hit = MaximumAttackDamage.Apply(Volatility.Apply(RageStats.ApplyAttack(hit, inputs.Get), inputs.Get), inputs.Get);
        return suppress ? new SkillDamageContext(inputs.Plan.Kind, inputs.Caster).Hit(hit) : hit;
    }

    // The damaging-ailment rows of a supported skill's hit in one context (AilmentPanelStats): the main or off hand of
    // an attack with that hand's weapon, or a spell's / secondary-damage skill's hit.
    public static List<(string Stat, int Value)> SupportedAilmentRows(ClientState conn, ushort skillId, string hand,
        SkillCatalog.Unarmed? weapon = null, bool projectile = false, int addedChaos = 0)
        => SupportedHitInputsFor(conn, skillId, weapon: weapon, projectile: projectile, addedChaos: addedChaos) is { } inputs ? SupportedAilmentRowsFrom(inputs, hand) : [];

    public static List<(string Stat, int Value)> SupportedAilmentRowsFrom(SupportedHitInputs inputs, string hand)
    {
        var context = new AilmentPanelStats.Context(hand, Spell: !inputs.Attack, inputs.Melee);
        return AilmentPanelStats.Compute(context, inputs.Raw, inputs.Conversion, inputs.Get);
    }

    public static AttackAccuracyProfile SupportedAccuracy(ClientState conn, ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        long Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        var player = CharacterPlayerStats(conn);
        return AttackAccuracyProfile.From(conn.Pools, player, Stats, own, MainHandLocalSources(conn))
            with { Rating = PlayerBlindAccuracy(conn, AccuracyRating.Selected(conn.Pools, WeaponProfileFor(conn, activeSet: true), Own), player) };
    }

    public static int SupportedCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, false);

    // A release retains the unprohibited offensive chance; the hit evaluates current owner prohibitions separately.
    public static int SupportedReleaseCriticalChance(ClientState conn, ushort skillId) => SupportedCriticalChanceFor(conn, skillId, true);

    // extraIncrease: a target condition's increase (critical_strike_chance_+%_vs_blinded_enemies, Game's Blind.cs).
    public static int SupportedCriticalChanceFor(ClientState conn, ushort skillId, bool release, long extraIncrease = 0)
    {
        var gem = SupportedSkill(conn, skillId);
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return 0;
        // Inspiration: "Supported Skills have (6-11)% increased Critical Strike Chance per Inspiration Charge".
        if (conn.SupportGems.InspirationCharges > 0
            && Stats.Key("critical_strike_chance_+%_per_righteous_charge") is uint chargeCrit)
            extraIncrease += (long)gem.Stats.GetValueOrDefault(chargeCrit) * conn.SupportGems.InspirationCharges;
        var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        int Own(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        int Player(string name) => release && IsCriticalProhibition(name) ? 0
            : Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (SkillsFor(conn).IsAttack(gem.Effect))
        {
            if (new[] { "global_cannot_crit", "virtual_global_cannot_crit", "cannot_critical_strike_with_attacks" }
                .Any(name => Player(name) != 0 || Own(name) != 0)) return 0;
            var weapon = WeaponProfileFor(conn, activeSet: true);
            int basis = weapon.Unarmed && (!SkillsFor(conn).IsMelee(gem.Effect) || SkillsFor(conn).IsSpell(gem.Effect)) ? 0
                : AttackBaseCriticalStrike.OfMainHand(!weapon.Unarmed, weapon.Critical, Player);
            long increased = conn.Pools.CriticalStrikeChancePct + extraIncrease
                + AttackBaseCriticalStrike.IncreasedSum(weapon, name => Own(name));
            return DisplayedCriticalChance(conn, AttackBaseCriticalStrike.ChanceOfBase(basis, increased, Player, Own), Player);
        }
        return DisplayedCriticalChance(conn, SpellCriticalStrike.Chance(SkillsFor(conn).SpellCritChance(gem.Effect, gem.Level),
            Player, Own, conn.Pools.SpellCriticalStrikeChancePct + extraIncrease), Player);
    }

    public static (int Flat, int Percent)? SupportedReservation(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        var sourceReservation = SkillsFor(conn).Reservation(gem.Effect, gem.Level);
        if (CurseCatalog.Hex(SkillsFor(conn).EffectId(gem.Effect)))
        {
            var blasphemy = gem.Supports.FirstOrDefault(s => IsBlasphemySupport(s.Effect.Id));
            if (blasphemy is null) return null;
            sourceReservation = SkillsFor(conn).Reservation(blasphemy.Effect.Row, blasphemy.Gem.Level);
            var sources = CharacterStatSources(conn).Stats;
            if (SkillsFor(conn).EffectId(gem.Effect) == "TemporalChains" && Stats.Key("temporal_chains_no_reservation") is uint free
                && conn.PlayerStats.GetValueOrDefault(free, sources.GetValueOrDefault(free)) != 0) return (0, 0);
        }
        if (sourceReservation is not { } reservation) return null;
        double factor = gem.Supports.Aggregate(1.0, (v, support) => v * support.CostMultiplier / 100.0);
        return ((int)Math.Floor(reservation.Flat * factor), (int)Math.Floor(reservation.Percent * factor));
    }

    public static int SupportedCriticalMultiplier(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        int extra = Stats.Key("base_critical_strike_multiplier_+") is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        return (int)Math.Clamp((long)(SkillsFor(conn).IsAttack(gem.Effect)
            ? AttackCriticalMultiplier(conn, CharacterPlayerStats(conn), gem.Effect, gem.Stats)
            : conn.Pools.SpellCriticalStrikeMultiplier) + extra, int.MinValue, int.MaxValue);
    }
}
