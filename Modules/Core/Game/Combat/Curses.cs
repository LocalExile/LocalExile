using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static readonly HashSet<string> CurseRecipientStats = [
        "base_resist_all_elements_%", "base_fire_damage_resistance_%", "base_cold_damage_resistance_%",
        "base_lightning_damage_resistance_%", "base_chaos_damage_resistance_%", "accuracy_rating_+%",
        "enfeeble_damage_+%_final", "enfeeble_damage_+%_vs_rare_or_unique_final", "physical_damage_taken_+%",
        "damage_taken_+%_on_low_life", "debilitate_self_for_x_milliseconds_on_hit",
        "temporal_chains_action_speed_+%_final", "temporal_chains_action_speed_+%_vs_rare_or_unique_final",
        "buff_time_passed_+%_other_than_temporal_chains", "chance_to_be_ignited_%", "chance_to_be_frozen_%", "chance_to_be_shocked_%",
        "receive_bleeding_chance_%_when_hit_by_attack", "enemy_aggravate_bleeding_on_attack_hit_chance_%_against_self",
        "enemy_additional_critical_strike_chance_against_self", "enemy_additional_critical_strike_multiplier_against_self",
        "life_granted_when_killed", "mana_granted_when_killed", "chance_to_grant_power_charge_on_death_%",
        "chance_to_grant_frenzy_charge_on_death_%", "chance_to_grant_endurance_charge_on_death_%",
        "grant_attacker_power_charge_when_hit_%_chance", "grant_attacker_frenzy_charge_when_hit_%_chance", "grant_attacker_endurance_charge_when_hit_%_chance",
        "projectiles_hitting_self_split_into_x", "projectile_damage_taken_+%", "base_additional_physical_damage_reduction_%",
        "minimum_added_physical_damage_taken", "maximum_added_physical_damage_taken", "life_granted_when_hit_by_attacks", "mana_granted_when_hit_by_attacks",
        "enemy_grant_X_rage_on_stun", "enemy_chance_to_double_stun_duration_%_vs_self",
        "life_leech_on_any_damage_when_hit_by_attack_permyriad", "mana_leech_on_any_damage_when_hit_by_attack_permyriad",
        "alchemists_mark_grant_attacker_x_flask_charges_when_hit_once_per_3s",
        "alchemists_mark_igniter_creates_burning_ground_%_ignite_damage", "alchemists_mark_poisoner_creates_caustic_ground_%_poison_damage"
    ];

    static bool ExternalTemporalChains(ClientState conn, string effect) => effect == "TemporalChains"
        && GameEvents.GrantedSkillExecutable.For(conn).Any(accept => accept(conn, effect));

    static void CurseCharges(ClientState conn, SpawnedMonster target, bool death)
    {
        foreach (var (kind, name) in new[] { (ChargeType.Power, "power"), (ChargeType.Frenzy, "frenzy"), (ChargeType.Endurance, "endurance") })
            if (RandomRolls.Chance(target.Curses[death ? $"chance_to_grant_{name}_charge_on_death_%" : $"grant_attacker_{name}_charge_when_hit_%_chance"], 100, Random.Shared))
                ChargesOf(conn).Enqueue(new ChargeEvent.Grant(kind, 1, "curse"));
    }

    static int CurseLimit(ClientState conn, IReadOnlyDictionary<uint, int> player)
    {
        int Get(string n) => GolemStat(player, n);
        return Get("has_curse_limit_equal_to_maximum_power_charges") != 0 ? Get("max_power_charges")
            : Math.Max(0, 1 + Get("number_of_additional_curses_allowed"));
    }

    internal static int CurseRadius(ClientState conn, ushort skill, bool aura, SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var own = (captured ?? SupportedSkill(conn, skill)).Stats; var player = sources ?? CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(player, n) + GolemStat(own, n);
        // Hexes retain their own base radius when Blasphemy converts them to auras.
        return SkillArea.Radius(22,
            n => Get(n) + (n == SkillArea.BaseIncreased ? Get("curse_area_of_effect_+%") : 0), spell: true);
    }

    internal static bool ApplyCurseLocked(ClientState conn, SpawnedMonster monster, ushort skill, double now, bool aura,
        SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var gem = captured ?? SupportedSkill(conn, skill); string effect = SkillsFor(conn).EffectId(gem.Effect) ?? "";
        if (!CurseCatalog.All.TryGetValue(effect, out var definition) || ExternalTemporalChains(conn, effect)
            || monster.Dead || monster.Life == 0 || !monster.Hostile || !gem.RequirementsMet) return false;
        var player = sources ?? CharacterPlayerStats(conn);
        int Own(string n) => GolemStat(gem.Stats, n);
        int Get(string n) => GolemStat(player, n) + Own(n);
        if (monster.Mods.Effects["immune_to_curses"] != 0 || !definition.Mark && monster.Mods.Effects["hexproof"] != 0
            && Get("ignore_hexproof") == 0 && Get("hexes_ignore_hexproof") == 0
            && !RandomRolls.Chance(Get("chance_to_ignore_hexproof_%"), 100, Random.Shared)) return false;
        double factor = Math.Max(0, 1 + (Get("curse_effect_+%") + Get(definition.Mark ? "mark_effect_+%" : "hex_effect_+%")
            + (definition.Mark ? Get("mark_skills_curse_effect_+%") : 0)
            + Get(ReservationNames.GetValueOrDefault(effect, effect.ToLowerInvariant()) + "_curse_effect_+%")
            + monster.Mods.Effects["curse_effect_on_self_+%"]) / 100.0)
            * Math.Max(0, 1 + Own("support_blasphemy_curse_effect_+%_final") / 100.0)
            * Math.Max(0, 1 + Own("support_hextouch_curse_effect_+%_final") / 100.0);
        var stats = gem.Stats.Where(p => CurseRecipientStats.Contains(Stats.Name(p.Key) ?? ""))
            .ToDictionary(p => Stats.Name(p.Key)!, p => (int)Math.Truncate(p.Value * factor));
        double seconds = definition.Mark ? double.PositiveInfinity : SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"),
            n => GolemStat(player, n) + (n == SkillDurationModifiers.IncreasedDuration ? Get("curse_duration_+%") + Get("curse_skill_effect_duration_+%") : 0),
            n => Own(n) + (n == SkillDurationModifiers.IncreasedDuration ? Own("base_curse_duration_+%") : 0));
        double rate = RecipientEffectTime.Rate(5, n => monster.Mods.Effects[n], temporalChains: effect == "TemporalChains");
        seconds *= Math.Max(0, 1 + Own("hextouch_support_curse_duration_+%_final") / 100d);
        if (aura && monster.Curses.All.GetValueOrDefault((conn.PeerId, effect)) is { Aura: true } existing
            && existing.Skill == skill && existing.Stats.Count == stats.Count && existing.Stats.All(p => stats.GetValueOrDefault(p.Key) == p.Value)) return true;
        if (definition.Mark)
            foreach (var m in conn.Monsters.Values.Where(m => !ReferenceEquals(m, monster)))
                foreach (var mark in m.Curses.All.Values.Where(c => c.Caster == conn.PeerId && c.Mark).ToArray()) m.Curses.Remove(mark.Caster, mark.Effect);
        monster.Curses.Put(new(conn.PeerId, effect, skill, definition.Buff, conn.NextBuff++, now,
            aura ? double.PositiveInfinity : now + seconds / rate, aura, definition.Mark, stats), CurseLimit(conn, player));
        return true;
    }

    static void CursesOnHitLocked(ClientState conn, SpawnedMonster target, ushort sourceSkill, bool spell, double now)
    {
        if (target.Dead || target.Life == 0 || conn.Reservations.Secondary) return;
        var source = sourceSkill != 0 ? SupportedSkill(conn, sourceSkill) : null;
        bool hextouch = source is not null && GolemStat(source.Stats, "apply_linked_curses_on_hit_%") is var chance
            && RandomRolls.Chance(chance, 100, Random.Shared);
        if (!hextouch && (spell || target.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))) return;
        var player = CharacterPlayerStats(conn);
        foreach (var skill in GemSkillEffects(conn))
        {
            string effect = SkillsFor(conn).EffectId(skill.Effect) ?? "";
            if (!CurseCatalog.All.TryGetValue(effect, out var curse)) continue;
            var gem = SupportedSkill(conn, skill.Id);
            if (!gem.RequirementsMet) continue;
            if (!curse.Mark && hextouch && source!.Host != 0 && gem.Host == source.Host && gem.Group == source.Group
                && GolemStat(gem.Stats, "curse_triggered_by_hextouch") != 0)
                ApplyCurseLocked(conn, target, skill.Id, now, false, gem, player);
            if (curse.Mark && !spell && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                && GolemStat(gem.Stats, "trigger_on_attack_hit_against_rare_or_unique") != 0
                && now >= conn.SkillReadyAt.GetValueOrDefault(gem.Effect))
            {
                if (!PayMana(conn, skill.Id, out _, out _)) continue;
                conn.SkillReadyAt[gem.Effect] = now + 4;
                ApplyCurseLocked(conn, target, skill.Id, now, false, gem, player);
            }
        }
    }

    static async Task<bool> CastCurse(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skill, (int X, int Y) aim, uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, skill); string effect = SkillsFor(conn).EffectId(gem.Effect)!;
        double release = AreaTime(conn) + (CastMs(conn, gem.Effect, skill) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea
                    || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration) return false;
                if (!conn.Paused && AreaTime(conn) >= release)
                {
                    double now = AreaTime(conn); var player = CharacterPlayerStats(conn);
                    var currentGem = SupportedSkill(conn, skill); int radius = CurseRadius(conn, skill, false, currentGem, player);
                    foreach (var (id, m) in conn.Monsters)
                        if (CurseCatalog.All[effect].Mark ? id == target : Dist(m.Walk.Position(now), aim) <= radius)
                            ApplyCurseLocked(conn, m, skill, now, false, currentGem, player);
                    break;
                }
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await EnemyDebuffsTick(remote, stream, ciphers, conn, AreaTime(conn));
        return true;
    }

    static void CursesTickLocked(ClientState conn, double now, List<byte> bytes)
    {
        var active = conn.Auras.Values.Where(a => CurseCatalog.Hex(a.EffectId) && !ExternalTemporalChains(conn, a.EffectId)).ToArray();
        var player = active.Length > 0 ? CharacterPlayerStats(conn) : null;
        var auras = active.TakeLast(player is null ? 0 : CurseLimit(conn, player))
            .Select(a => (Aura: a, Gem: SupportedSkill(conn, a.SkillId)))
            .Select(a => (a.Aura, a.Gem, Radius: CurseRadius(conn, a.Aura.SkillId, true, a.Gem, player))).ToArray();
        var here = conn.Walk.Position(now);
        foreach (var (id, m) in conn.Monsters)
        {
            foreach (var curse in m.Curses.All.Values.Where(c => c.Mark && (m.Dead || m.Life == 0 || c.Caster == conn.PeerId && conn.PlayerLife == 0)).ToArray())
                m.Curses.Remove(curse.Caster, curse.Effect);
            foreach (var curse in m.Curses.All.Values.Where(c => c.Caster == conn.PeerId && c.Aura).ToArray())
                if (conn.PlayerLife == 0 || m.Dead || !auras.Any(a => a.Aura.SkillId == curse.Skill && Dist(m.Walk.Position(now), here) <= a.Radius))
                    m.Curses.Remove(curse.Caster, curse.Effect);
            if (conn.PlayerLife > 0 && !InTown(conn))
                foreach (var aura in auras)
                    if (Dist(m.Walk.Position(now), here) <= aura.Radius) ApplyCurseLocked(conn, m, aura.Aura.SkillId, now, true, aura.Gem, player);
            m.Curses.Expire(now);
            var changed = m.Curses.Presented.Values.Where(c => !m.Curses.All.TryGetValue((c.Caster, c.Effect), out var next) || next.Buff != c.Buff).ToArray();
            foreach (var old in changed)
            { bytes.AddRange(InstanceEntities.RemoveBuff(id, old.Buff)); m.Curses.Presented.Remove((old.Caster, old.Effect)); }
            foreach (var curse in m.Curses.All.Values.Where(c => !m.Curses.Presented.ContainsKey((c.Caster, c.Effect))))
            {
                bytes.AddRange(InstanceEntities.AddBuff(id, curse.Buff, curse.Row, PlayerId, curse.Skill,
                    u68: double.IsFinite(curse.Until) ? BitConverter.SingleToUInt32Bits((float)Math.Max(0, curse.Until - now)) : null));
                m.Curses.Presented[(curse.Caster, curse.Effect)] = curse;
            }
            double slow = m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                ? m.Curses["temporal_chains_action_speed_+%_vs_rare_or_unique_final"] : m.Curses["temporal_chains_action_speed_+%_final"];
            if (!ExternalTemporalChains(conn, "TemporalChains"))
            {
                double oldRate = m.RecipientDebuffRate;
                double rate = RecipientEffectTime.Rate(2, n => m.Effects[n]);
                if (rate != oldRate)
                {
                    m.Ignites.SetExpiryRate(now, rate); m.Bleeds.SetExpiryRate(now, rate); m.Poisons.SetExpiryRate(now, rate); m.Ailments.SetExpiryRate(now, rate);
                    m.TauntedUntil = RecipientEffectTime.RetimedDeadline(m.TauntedUntil, now, oldRate, rate); m.RecipientDebuffRate = rate;
                }
                m.ReceivedActionFactor = Math.Max(.25, 1 + slow / 100); m.ReceivedActionUntil = slow != 0 ? double.PositiveInfinity : now;
            }
            if (m.Curses.PresentedVersion != m.Curses.Version)
            {
                var keys = m.Curses.PresentedStats.Concat(m.Curses.Stats.Keys).Distinct().ToArray();
                bytes.AddRange(InstanceEntities.StatsUpdate(id, keys.Select(n => (Key: Stats.Key(n), Value: m.Effects[n]))
                    .Where(p => p.Key is not null).Select(p => (p.Key!.Value, p.Value)).ToList()));
                m.Curses.PresentedStats.Clear(); m.Curses.PresentedStats.UnionWith(m.Curses.Stats.Keys);
                m.Curses.PresentedVersion = m.Curses.Version;
            }
        }
    }
}
