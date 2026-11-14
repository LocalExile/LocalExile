using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>BuffDefinitions row 0 <c>ignited</c> ("You're on fire", BuffVisuals 2: the burning flames).</summary>
    public const ushort IgnitedBuffRow = 0;

    // The increases the wiki's Ignite lists (Damage, Damage over Time, Elemental, Fire, Burning, Ignite Damage) and
    // ailment damage; hit-only ones (spell, area, attack, melee damage) never reach an ailment.
    static readonly string[] IgniteIncreases = ["damage_+%", "damage_over_time_+%", "elemental_damage_+%", "fire_damage_+%",
        "fire_damage_over_time_+%", "burn_damage_+%", "ignite_damage_+%", "base_ailment_damage_+%"];

    /// <summary>
    /// The ignite a granted skill's hit inflicts on <paramref name="m"/>, or null (wiki Ignite), for a hit without the
    /// panel's ignite rows (Abberath's Fury: <see cref="TriggerFootstepSkill"/>). The hit needs fire damage
    /// (<paramref name="baseFire"/>: its base and added fire damage after conversion, before any increase) and a critical
    /// strike (always ignites) or the chance (base_chance_to_ignite_%, chance_to_ignite_%, chance_to_freeze_shock_ignite_%);
    /// the ignite prohibitions (MonsterElementalAilments.CannotInflict) stop it. It burns 90% of that fire per second (Ailments.Ignite) × the ignite increases ×
    /// <paramref name="more"/> × the damage over time multipliers (critical_ailment_dot_multiplier_+ on a critical strike:
    /// Character.ot's 50, PanelStatDefaults), for 4 s × increased
    /// ignite duration × the monster's base_self_ignite_duration_-%; the "faster" stats as in
    /// <see cref="MonsterIgnites.Settings.From"/>. <paramref name="attacker"/> gives the player's and the skill's stats.
    /// </summary>
    public static DamageOverTime? IgniteFromHit(double baseFire, bool critical, Func<string, int> attacker, double more,
        SpawnedMonster m, Random random)
    {
        if (baseFire <= 0 || MonsterElementalAilments.CannotInflict("ignite", attacker)
            || attacker("never_damaging_ailments") != 0) return null;
        int chance = attacker("base_chance_to_ignite_%") + attacker("chance_to_ignite_%") + attacker("chance_to_freeze_shock_ignite_%")
            + (attacker("always_ignite") != 0 ? 100 : 0);
        if (!critical && !RandomRolls.Chance(chance, 100, random)) return null;
        double multiplier = 1 + (attacker("dot_multiplier_+") + attacker("fire_dot_multiplier_+") + attacker("ignite_dot_multiplier_+")
            + (critical ? attacker("critical_ailment_dot_multiplier_+") : 0)) / 100.0;
        var ignite = GameServer.Core.World.Ailments.Ignite(DamagePacket.Of(DamageType.Fire, baseFire, baseFire),
            IgniteIncreases.Sum(attacker), more * multiplier);
        double faster = MonsterIgnites.Faster(attacker);
        double seconds = faster > 0 ? ignite.RemainingSeconds * Math.Max(0, 1 + attacker("ignite_duration_+%") / 100.0)
            * MonsterIgnites.SelfDuration(name => m.Mods.Effects[name]) / faster : 0;
        return ignite.DamagePerSecond > 0 && seconds > 0
            ? ignite with { DamagePerSecond = ignite.DamagePerSecond * faster, RemainingSeconds = seconds } : null;
    }

    /// <summary>Puts a worked-out ignite on a living monster (under the Monsters lock): the packets that show it
    /// (<see cref="ShowIgnitedLocked"/>), or null when nothing was added.</summary>
    public static List<byte[]>? IgniteLocked(ClientState conn, uint id, SpawnedMonster m, DamageOverTime ignite, double now)
    {
        if (m.Dead || m.Life == 0 || !m.Ignites.Add(now, ignite.DamagePerSecond, ignite.RemainingSeconds)) return null;
        return ShowDotBuffLocked(conn, id, m.Ignites, IgnitedBuffRow, now);
    }

    // The ignited (or bleeding) buff's timer runs to the monster's last ignite's (bleed's) end: every new one sends it
    // again (the old instance off first). Under the Monsters lock.
    static List<byte[]> ShowDotBuffLocked(ClientState conn, uint id, MonsterIgnites set, ushort row, double now)
    {
        var packets = new List<byte[]>();
        if (set.Buff is ushort old) packets.Add(InstanceEntities.RemoveBuff(id, old));
        ushort buff = conn.NextBuff++;
        set.Buff = buff;
        packets.Add(InstanceEntities.AddBuff(id, buff, row, PlayerId,
            u68: BitConverter.SingleToUInt32Bits((float)set.RemainingBase(now)),
            visual: row == IgnitedBuffRow ? AilmentArtVisual(conn, "ignite")
                : row == LabyrinthTrials.BleedingBuffRow ? AilmentArtVisual(conn, "bleed") : null));
        return packets;
    }

    // The art of the ailments the player causes: BuffVisualsArtVariations' Id and the derived stat SkillArtVariations
    // names for it. The value is that of the skill that last worked out its ailments (a worn Skill Gem Effect's
    // ignite_art_variation …, or gloves that restyle ailments), kept per connection. The buff names its BuffVisuals row,
    // as every buff whose art follows a variation does (11-skills.md §12); 0 keeps the buff's own art.
    static readonly (string Buff, string Stat)[] AilmentArtStats =
        [("ignite", "virtual_ignite_visual_override"), ("poison", "virtual_poison_art_variation"), ("bleed", "virtual_bleed_art_variation")];

    static readonly RuleTable<ClientState, System.Collections.Concurrent.ConcurrentDictionary<string, int>> AilmentArt = new();

    /// <summary>Notes the art of the ailments this skill's use can cause (AilmentArt), where its ignite is worked out:
    /// its gem's worn Skill Gem Effect's stats and the character's cosmetics, by the client's rules (DerivedArt).</summary>
    public static void NoteIgniteArt(ClientState conn, ushort skillId)
    {
        var worn = new Dictionary<uint, int>();
        uint gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId).Gem;
        if (gem != 0)
            foreach (var (key, cosmetic) in CharacterSaves.SkillCosmetics(CharacterName(conn)))
                if (CharacterSaves.SkillCosmeticGem(key) == gem && Mtx.Resolve(cosmetic) is { } record && MtxEffects.Of(record.MtxTypeHash) is { } fx)
                    foreach (var (k, v) in fx.Stats) worn[k] = v;
        var derived = SkillCosmeticDerivedStats(conn, worn).ToDictionary(s => s.Stat, s => s.Value);
        var noted = AilmentArt.GetOrCreateValue(conn);
        foreach (var (buff, stat) in AilmentArtStats)
            noted[buff] = Stats.Key(stat) is uint key ? derived.GetValueOrDefault(key) : 0;
    }

    /// <summary>The settings as given, with the skill's ignite art noted (for an object initializer).</summary>
    public static MonsterIgnites.Settings NotedIgnite(ClientState conn, ushort skillId, MonsterIgnites.Settings settings)
    {
        NoteIgniteArt(conn, skillId);
        return settings;
    }

    /// <summary>The BuffVisuals row an ailment's buff shows ("ignite", "poison", "bleed"), null for the buff's own.</summary>
    public static ushort? AilmentArtVisual(ClientState conn, string buff) =>
        AilmentArt.TryGetValue(conn, out var art) && art.GetValueOrDefault(buff) is int variation and not 0
            ? Art.BuffVisual(buff, variation) : null;

    /// <summary>What a skill's hit can ignite with, as a projectile captures it at release: the skill's ignite rows in the
    /// main hand for an attack (the spell's for a spell; <paramref name="weapon"/>: a shield skill's shield) and the
    /// chance from <paramref name="player"/> and the skill's stats.</summary>
    public static MonsterIgnites.Settings SupportedIgnite(ClientState conn, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> player, SkillCatalog.Unarmed? weapon = null)
    {
        var merged = MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
        string hand = spell ? "spell" : "main_hand";
        NoteIgniteArt(conn, skillId);
        var settings = MonsterIgnites.Settings.From(SupportedAilmentRows(conn, skillId, hand, weapon), hand,
            name => Stats.Key(name) is uint key ? merged.GetValueOrDefault(key) : 0);
        // Controlled Blaze: "(10-18)% more Damage with Ignite per Enemy Ignited Recently", capped by the support.
        int per = Stats.Key("support_recent_ignites_ignite_damage_per_recent_ignite_+%_final") is uint perKey
            ? merged.GetValueOrDefault(perKey) : 0;
        if (per > 0)
        {
            int cap = Stats.Key("support_recent_ignites_ignite_damage_per_recent_ignite_+%_final_maximum") is uint capKey
                ? merged.GetValueOrDefault(capKey) : 0;
            int recent = RecentlyIgnitedCount(conn, ProjectileWorldTime(conn));
            long added = Math.Min((long)per * recent, cap > 0 ? cap : long.MaxValue);
            double more = Math.Max(0, 1 + added / 100.0);
            settings = settings with { Min = settings.Min * more, Max = settings.Max * more,
                CriticalMin = settings.CriticalMin * more, CriticalMax = settings.CriticalMax * more };
        }
        return WithOverheat(conn, skillId,
            WithIgniteSpread(settings, player, SupportedSkill(conn, skillId).Stats, spell, hand));
    }

    /// <summary>The attacker's ignite proliferation radius in cells, the largest of its sources: Ignite Proliferation
    /// Support's, the gloves', the notable's, the flask's, the generic stat, the main hand's (attacks in that hand) and
    /// Elemental Proliferation's (virtual_elemental_status_effect_aura_radius, the larger of its two sources). The client
    /// has no virtual_ignite_proliferation_radius, so this follows the shock calculator's "largest source" rule (not
    /// verified for ignite).</summary>
    public static int IgniteProliferationRadius(Func<string, int> get, string hand) => Math.Max(0, new[]
    {
        get("support_ignite_proliferation_radius"), get("ignite_proliferation_radius"),
        get("gloves_ignite_proliferation_radius"), get("passive_notable_ignite_proliferation_radius"),
        get("flask_ignite_proliferation_radius"), hand == "main_hand" ? get("main_hand_ignite_proliferation_radius") : 0,
        get("elemental_status_effect_aura_radius"), get("elementalist_elemental_status_effect_aura_radius"),
    }.Max());

    /// <summary>Settings with their ignite's spread: <see cref="IgniteProliferationRadius"/> from the player's and the
    /// skill's stats, grown by the hit's area modifier as a chill's or shock's is (MonsterElementalAilments.SpreadRadius).
    /// </summary>
    public static MonsterIgnites.Settings WithIgniteSpread(MonsterIgnites.Settings settings,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill, bool spell, string hand)
    {
        if (settings.Seconds <= 0) return settings;
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        int radius = IgniteProliferationRadius(name => Player(name) + Skill(name), hand);
        if (radius <= 0) return settings;
        double area = AilmentArea(Player, Skill, spell);
        return settings with { Spread = (int)Math.Floor(radius * Math.Floor(100 * Math.Sqrt(Math.Max(0, area))) / 100) };
    }

    /// <summary>How many enemies were ignited in the last four seconds (Controlled Blaze).</summary>
    public static int RecentlyIgnitedCount(ClientState conn, double now)
    {
        lock (conn.Monsters)
            return conn.Monsters.Values.Count(m => m.Ignites.LastApplied > 0 && m.Ignites.LastApplied >= now - 4);
    }

    /// <summary>The default melee attack's ignite, as the ordinary bow or wand shot's (<c>ProjectileRanged</c>): the panel's
    /// main-hand rows from the weapon's raw packet and the character's conversion.</summary>
    public static MonsterIgnites.Settings DefaultAttackIgnite(ClientState conn, IReadOnlyDictionary<uint, int> player)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        return WithIgniteSpread(MonsterIgnites.Settings.From(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(WeaponFor(conn), Get), DamageStats.Conversion(player, Stats, attack: true), Get),
            "main_hand", Get), player, null, spell: false, "main_hand");
    }

    // Called under the combat lock at an accepted hit that dealt fire damage and left the monster alive: the hit's ignite
    // (MonsterIgnites.TryApply, its duration less by the target's base_self_ignite_duration_-%), and the packets of the
    // monster's ignited buff, or null when it did not ignite.
    public static List<byte[]>? IgniteOnHitLocked(ClientState conn, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now)
    {
        if (target.Invulnerable) return null;
        var e = target.Mods.Effects;
        settings = settings with { Seconds = settings.Seconds * MonsterIgnites.SelfDuration(name => e[name]) };
        if (!target.Ignites.TryApply(settings, critical, now, e["avoid_ignite_%"],
                e["immune_to_ignite"] != 0 || e["cannot_be_ignited"] != 0, conn.IgniteRandom)) return null;
        // Combustion: ignites from the supported skill apply their fire resistance reduction for the ignite's duration.
        if (settings.FireExposure < 0)
        {
            double until = target.Ignites.Until;
            var key = (DamageType.Fire, settings.FireExposure);
            target.Exposures[key] = Math.Max(target.Exposures.GetValueOrDefault(key, double.NegativeInfinity), until);
            target.ExposureUntil[0] = Math.Max(target.ExposureUntil[0], until);
        }
        conn.RecentEvents.Record(StatConditions.IgnitedEnemy, now);
        return ShowDotBuffLocked(conn, id, target.Ignites, IgnitedBuffRow, now);
    }

    public static string IgnitedLog(uint id, SpawnedMonster target, bool critical, double now) =>
        $"monster 0x{id:x} ({target.Label}) ignited{(critical ? " by a critical hit" : "")} for {target.Ignites.Until - now:0.##} s, {target.Ignites.PerSecond(now):0.#}/s"
        + (target.Ignites.SpreadRadius > 0 ? $", spreads {target.Ignites.SpreadRadius} cells" : "");

    public static void IgniteOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now) =>
        QueueDotBuff(session, IgniteOnHitLocked(session.Conn, id, target, settings, critical, now),
            IgnitedLog(id, target, critical, now));

    // Queues a projectile hit's ignited or bleeding buff packets on its session, the log on the last.
    static void QueueDotBuff(ProjectileSession session, List<byte[]>? packets, string log)
    {
        for (int i = 0; i < packets?.Count; i++)
            session.Packet(packets[i], i < packets.Count - 1 ? null : log);
    }

    // Sends a hit's ignited or bleeding buff packets (IgniteOnHitLocked, BleedOnHitLocked) after the lock.
    static async Task SendDotBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers, List<byte[]>? packets,
        string log)
    {
        if (packets is null) return;
        for (int i = 0; i < packets.Count; i++)
            await Send(remote, stream, ciphers, packets[i], i < packets.Count - 1 ? null : "0x01c1 " + log);
    }

    // One tick of the ignited monsters (RunPlayerTick): the strongest ignite's fire damage through the monster's
    // resistance and damage taken (MonsterDotTaken), the buff off when the last ignite ends, and kills, as Righteous
    // Fire's burn (BurnAround) commits them. They burn on while the player is dead (the kill then gives no experience,
    // MonsterKilled).
    public static Task BurnIgnited(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        TickStrongestDots(remote, stream, ciphers, conn, m => m.Ignites, (m, now) => MonsterDotTaken(m, DamageType.Fire, now),
            "burnt to death by ignite");

    // One tick of a strongest-only set (ignites, bleeds: <paramref name="of"/>), <paramref name="factor"/> the monster's
    // mitigation of it, <paramref name="aggravated"/> the strongest aggravated bleed's extra share (MonsterIgnites.Advance).
    static async Task TickStrongestDots(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        Func<SpawnedMonster, MonsterIgnites> of, Func<SpawnedMonster, double, double> factor, string died,
        Func<SpawnedMonster, double, double>? aggravated = null)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        var ended = new List<(uint Id, ushort Buff)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                var set = of(m);
                if (set.Idle) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) set.Clear();
                else damage = set.Advance(now, factor(m, now), aggravated?.Invoke(m, now) ?? 0);
                if (!set.Burning && set.Buff is ushort gone) { ended.Add((id, gone)); set.Buff = null; }
                if (damage == 0) continue;
                (m.Life, bool stageStarts) = TakeOwnerDamageLocked(conn, m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    CollectVaalSoulAtDeathLocked(conn, m);
                    set.Clear(burnt: true);
                    if (set.Buff is ushort buff) { ended.Add((id, buff)); set.Buff = null; }
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
            }
        }
        foreach (var (id, buff) in ended)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.RemoveBuff(id, buff), null);
            // The buff may be another player's (who applied the ailment): theirs goes too (instances are the instance's).
            ToOthers(conn, id, InstanceEntities.RemoveBuff(id, buff));
        }
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
            {
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
                if (m.Poisons.Buff is ushort pbuff)
                {
                    await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.RemoveBuff(id, pbuff), null);
                    m.Poisons.Buff = null;
                }
            }
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) {died}" : null);
            if (life == 0) await DotKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
