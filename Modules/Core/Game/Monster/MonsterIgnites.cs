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
    static readonly uint[] IgniteIncreases = [Stat.DamagePlusPct, Stat.DamageOverTimePlusPct, Stat.ElementalDamagePlusPct, Stat.FireDamagePlusPct,
        Stat.FireDamageOverTimePlusPct, Stat.BurnDamagePlusPct, Stat.IgniteDamagePlusPct, Stat.BaseAilmentDamagePlusPct];

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
    public static DamageOverTime? IgniteFromHit(double baseFire, bool critical, Func<uint, int> attacker, double more,
        SpawnedMonster m, Random random)
    {
        if (baseFire <= 0 || MonsterElementalAilments.CannotInflict("ignite", attacker)
            || attacker(Stat.NeverDamagingAilments) != 0) return null;
        int chance = attacker(Stat.BaseChanceToIgnitePct) + attacker(Stat.ChanceToIgnitePct) + attacker(Stat.ChanceToFreezeShockIgnitePct)
            + (attacker(Stat.AlwaysIgnite) != 0 ? 100 : 0);
        if (!critical && !RandomRolls.Chance(chance, 100, random)) return null;
        double multiplier = 1 + (attacker(Stat.DotMultiplierPlus) + attacker(Stat.FireDotMultiplierPlus) + attacker(Stat.IgniteDotMultiplierPlus)
            + (critical ? attacker(Stat.CriticalAilmentDotMultiplierPlus) : 0)) / 100.0;
        var ignite = GameServer.Core.World.Ailments.Ignite(DamagePacket.Of(DamageType.Fire, baseFire, baseFire),
            IgniteIncreases.Sum(attacker), more * multiplier);
        double faster = MonsterIgnites.Faster(attacker);
        double seconds = faster > 0 ? ignite.RemainingSeconds * Math.Max(0, 1 + attacker(Stat.IgniteDurationPlusPct) / 100.0)
            * MonsterIgnites.SelfDuration(name => m.Mods.Effects[name]) / faster : 0;
        return ignite.DamagePerSecond > 0 && seconds > 0
            ? ignite with { DamagePerSecond = ignite.DamagePerSecond * faster, RemainingSeconds = seconds } : null;
    }

    /// <summary>Puts a worked-out ignite on a living monster (in the area's frame): the packets that show it
    /// (<see cref="ShowIgnitedLocked"/>), or null when nothing was added.</summary>
    public static List<byte[]>? IgniteLocked(Player player, uint id, SpawnedMonster m, DamageOverTime ignite, double now)
    {
        if (m.Dead || m.Life == 0 || !m.Ignites.Add(now, ignite.DamagePerSecond, ignite.RemainingSeconds,
                aggravated: false, spread: 0, damageTakenPct: UtilityFlaskStat(player, UtilityFlasks.IgnitedDuringEffectTakenStat))) return null;
        return ShowDotBuffLocked(player, id, m.Ignites, IgnitedBuffRow, now);
    }

    // The ignited (or bleeding) buff's timer runs to the monster's last ignite's (bleed's) end: every new one sends it
    // again (the old instance off first). In the area's frame.
    static List<byte[]> ShowDotBuffLocked(Player player, uint id, MonsterIgnites set, ushort row, double now)
    {
        ShowDot(player, id, ref set.Buff, ref set.BuffSource, new(row, player.Id, U68: Buff.Seconds(set.RemainingBase(now)),
            Visual: row == IgnitedBuffRow ? AilmentArtVisual(player, "ignite")
                : row == LabyrinthTrials.BleedingBuffRow ? AilmentArtVisual(player, "bleed") : null));
        return [];
    }

    // A damage-over-time set's buff on an entity: one row from one source (the set keeps its key), set when it starts or
    // its end moves; another source's or row's goes first.
    internal static void ShowDot(Player player, uint id, ref ushort? row, ref uint source, Buff buff)
    {
        if (row is ushort shown && (shown != buff.Row || source != buff.Source)) player.Area.Buffs.Clear(id, shown, source);
        player.Area.Buffs.Set(id, buff);
        (row, source) = (buff.Row, buff.Source);
    }

    internal static void EndDot(Player player, uint id, ref ushort? row, uint source)
    {
        if (row is ushort shown) player.Area.Buffs.Clear(id, shown, source);
        row = null;
    }

    // The art of the ailments the player causes: BuffVisualsArtVariations' Id and the derived stat SkillArtVariations
    // names for it. The value is that of the skill that last worked out its ailments (a worn Skill Gem Effect's
    // ignite_art_variation …, or gloves that restyle ailments), kept per connection. The buff names its BuffVisuals row,
    // as every buff whose art follows a variation does (11-skills.md §12); 0 keeps the buff's own art.
    static readonly (string Buff, uint Stat)[] AilmentArtStats =
        [("ignite", Stat.VirtualIgniteVisualOverride), ("poison", Stat.VirtualPoisonArtVariation), ("bleed", Stat.VirtualBleedArtVariation)];

    static readonly Store<Player, System.Collections.Concurrent.ConcurrentDictionary<string, int>> AilmentArt = new();

    /// <summary>Notes the art of the ailments this skill's use can cause (AilmentArt), where its ignite is worked out:
    /// its gem's worn Skill Gem Effect's stats and the character's cosmetics, by the client's rules (DerivedArt).</summary>
    public static void NoteIgniteArt(Player player, ushort skillId)
    {
        var worn = new Dictionary<uint, int>();
        uint gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId).Gem;
        if (gem != 0)
            foreach (var (key, cosmetic) in CharacterSaves.SkillCosmetics(CharacterName(player)))
                if (CharacterSaves.SkillCosmeticGem(key) == gem && Mtx.Resolve(cosmetic) is { } record && MtxEffects.Of(record.MtxTypeHash) is { } fx)
                    foreach (var (k, v) in fx.Stats) worn[k] = v;
        var derived = SkillCosmeticDerivedStats(player, worn).ToDictionary(s => s.Stat, s => s.Value);
        var noted = AilmentArt.GetOrCreateValue(player);
        foreach (var (buff, stat) in AilmentArtStats)
            noted[buff] = stat is var key ? derived.GetValueOrDefault(key) : 0;
    }

    /// <summary>The settings as given, with the skill's ignite art noted (for an object initializer).</summary>
    public static MonsterIgnites.Settings NotedIgnite(Player player, ushort skillId, MonsterIgnites.Settings settings)
    {
        NoteIgniteArt(player, skillId);
        return settings;
    }

    /// <summary>The BuffVisuals row an ailment's buff shows ("ignite", "poison", "bleed"), null for the buff's own.</summary>
    public static ushort? AilmentArtVisual(Player player, string buff) =>
        AilmentArt.TryGetValue(player, out var art) && art.GetValueOrDefault(buff) is int variation and not 0
            ? Art.BuffVisual(buff, variation) : null;

    /// <summary>What a skill's hit can ignite with, as a projectile captures it at release: the skill's ignite rows in the
    /// main hand for an attack (the spell's for a spell; <paramref name="weapon"/>: a shield skill's shield) and the
    /// chance from <paramref name="player"/> and the skill's stats.</summary>
    public static MonsterIgnites.Settings SupportedIgnite(Player player, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> playerStats, SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0,
        SupportedHitInputs? capturedInputs = null)
    {
        Func<uint, int> get;
        if (capturedInputs is null)
        {
            var merged = MergeSkillStats(playerStats, SupportedSkill(player, skillId).Stats);
            get = name => name is var key ? merged.GetValueOrDefault(key) : 0;
        }
        else
            get = capturedInputs.Get;
        string hand = spell ? "spell" : "main_hand";
        NoteIgniteArt(player, skillId);
        var rows = capturedInputs is null
            ? SupportedAilmentRows(player, skillId, hand, weapon, additionalIncreased)
            : SupportedAilmentRowsFrom(capturedInputs, hand, additionalIncreased);
        var settings = MonsterIgnites.Settings.From(rows, hand, get);
        // Controlled Blaze: "(10-18)% more Damage with Ignite per Enemy Ignited Recently", capped by the support.
        int per = get(Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinal);
        if (per > 0)
        {
            int cap = get(Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinalMaximum);
            int recent = RecentlyIgnitedCount(player, ProjectileWorldTime(player));
            long added = Math.Min((long)per * recent, cap > 0 ? cap : long.MaxValue);
            double more = Math.Max(0, 1 + added / 100.0);
            settings = settings with { Min = settings.Min * more, Max = settings.Max * more,
                CriticalMin = settings.CriticalMin * more, CriticalMax = settings.CriticalMax * more };
        }
        return WithOverheat(player, skillId,
            WithIgniteSpread(settings, playerStats, SupportedSkill(player, skillId).Stats, spell, hand));
    }

    /// <summary>The attacker's ignite proliferation radius in cells, the largest of its sources: Ignite Proliferation
    /// Support's, the gloves', the notable's, the flask's, the generic stat, the main hand's (attacks in that hand) and
    /// Elemental Proliferation's (virtual_elemental_status_effect_aura_radius, the larger of its two sources). The client
    /// has no virtual_ignite_proliferation_radius, so this follows the shock calculator's "largest source" rule (not
    /// verified for ignite).</summary>
    public static int IgniteProliferationRadius(Func<uint, int> get, string hand) => Math.Max(0, new[]
    {
        get(Stat.SupportIgniteProliferationRadius), get(Stat.IgniteProliferationRadius),
        get(Stat.GlovesIgniteProliferationRadius), get(Stat.PassiveNotableIgniteProliferationRadius),
        get(Stat.FlaskIgniteProliferationRadius), hand == "main_hand" ? get(Stat.MainHandIgniteProliferationRadius) : 0,
        get(Stat.ElementalStatusEffectAuraRadius), get(Stat.ElementalistElementalStatusEffectAuraRadius),
    }.Max());

    /// <summary>Settings with their ignite's spread: <see cref="IgniteProliferationRadius"/> from the player's and the
    /// skill's stats, grown by the hit's area modifier as a chill's or shock's is (MonsterElementalAilments.SpreadRadius).
    /// </summary>
    public static MonsterIgnites.Settings WithIgniteSpread(MonsterIgnites.Settings settings,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill, bool spell, string hand)
    {
        if (settings.Seconds <= 0) return settings;
        int Player(uint key) => player.GetValueOrDefault(key);
        int Skill(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        int radius = IgniteProliferationRadius(name => Player(name) + Skill(name), hand);
        if (radius <= 0) return settings;
        double area = AilmentArea(Player, Skill, spell);
        return settings with { Spread = (int)Math.Floor(radius * Math.Floor(100 * Math.Sqrt(Math.Max(0, area))) / 100) };
    }

    /// <summary>How many enemies were ignited in the last four seconds (Controlled Blaze).</summary>
    public static int RecentlyIgnitedCount(Player player, double now)
    {
        return player.Area.Monsters.Values.Count(m => m.Ignites.LastApplied > 0 && m.Ignites.LastApplied >= now - 4);
    }

    /// <summary>The default melee attack's ignite, as the ordinary bow or wand shot's (<c>ProjectileRanged</c>): the panel's
    /// main-hand rows from the weapon's raw packet and the character's conversion.</summary>
    public static MonsterIgnites.Settings DefaultAttackIgnite(Player player, IReadOnlyDictionary<uint, int> playerStats,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        var sourceWeapon = weapon ?? WeaponFor(player);
        return WithIgniteSpread(MonsterIgnites.Settings.From(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(sourceWeapon, Get), conversion ?? DamageStats.Conversion(playerStats, Stats, attack: true),
            Get, additionalIncreased),
            "main_hand", Get), playerStats, null, spell: false, "main_hand");
    }

    // Called under the combat lock at an accepted hit that dealt fire damage and left the monster alive: the hit's ignite
    // (MonsterIgnites.TryApply, its duration less by the target's base_self_ignite_duration_-%), and the packets of the
    // monster's ignited buff, or null when it did not ignite.
    public static List<byte[]>? IgniteOnHitLocked(Player player, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool playerIgnite = true) =>
        IgniteOnHitLocked(player, id, target, settings, critical, now, default(AttackAilmentGains), playerIgnite);

    public static List<byte[]>? IgniteOnHitLocked(Player player, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, AttackAilmentGains attackGains,
        bool playerIgnite = true)
    {
        if (target.Invulnerable) return null;
        var e = target.Mods.Effects;
        settings = settings with { Seconds = settings.Seconds * MonsterIgnites.SelfDuration(name => e[name]) };
        if (!target.Ignites.TryApply(settings, critical, now, e[Stat.AvoidIgnitePct],
                e[Stat.ImmuneToIgnite] != 0 || e[Stat.CannotBeIgnited] != 0, player.IgniteRandom,
                UtilityFlaskStat(player, UtilityFlasks.IgnitedDuringEffectTakenStat))) return null;
        // Combustion: ignites from the supported skill apply their fire resistance reduction for the ignite's duration.
        if (settings.FireExposure < 0)
        {
            double until = target.Ignites.Until;
            var key = (DamageType.Fire, settings.FireExposure);
            target.Exposures[key] = Math.Max(target.Exposures.GetValueOrDefault(key, double.NegativeInfinity), until);
            { target.ExposureUntil[0] = Math.Max(target.ExposureUntil[0], until); target.PresentationChanged?.Invoke(); }
        }
        if (playerIgnite && target.Hostile)
        {
            player.RecentEvents.Record(StatConditions.IgnitedEnemy, now);
            HerEmbraceOnIgniteLocked(player, now);
            GainFromAttackAilmentLocked(player, attackGains.IgniteFrenzy, ChargeType.Frenzy, FrenzyOnAttackIgnite);
        }
        return ShowDotBuffLocked(player, id, target.Ignites, IgnitedBuffRow, now);
    }

    public static string IgnitedLog(uint id, SpawnedMonster target, bool critical, double now) =>
        $"monster 0x{id:x} ({target.Label}) ignited{(critical ? " by a critical hit" : "")} for {target.Ignites.Until - now:0.##} s, {target.Ignites.PerSecond(now):0.#}/s"
        + (target.Ignites.SpreadRadius > 0 ? $", spreads {target.Ignites.SpreadRadius} cells" : "");

    public static void IgniteOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool playerIgnite = true) =>
        IgniteOnHitLocked(session, id, target, settings, critical, now, default(AttackAilmentGains), playerIgnite);

    public static void IgniteOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, AttackAilmentGains attackGains,
        bool playerIgnite = true)
    {
        // The log is built only for a hit that ignited: formatting it for every hit of an explosion was its own cost.
        if (IgniteOnHitLocked(session.Player, id, target, settings, critical, now, attackGains, playerIgnite) is { Count: > 0 } packets)
            QueueDotBuff(session, packets, IgnitedLog(id, target, critical, now));
    }

    // Queues a projectile hit's ignited or bleeding buff packets on its session, the log on the last.
    static void QueueDotBuff(ProjectileSession session, List<byte[]>? packets, string log)
    {
        for (int i = 0; i < packets?.Count; i++)
            session.Packet(packets[i], i < packets.Count - 1 ? null : log);
    }

    // Sends a hit's ignited or bleeding buff packets (IgniteOnHitLocked, BleedOnHitLocked) after the lock.
    static async Task SendDotBuff(Player player, List<byte[]>? packets,
        string log)
    {
        if (packets is null) return;
        for (int i = 0; i < packets.Count; i++)
            await Send(player, packets[i], i < packets.Count - 1 ? null : "0x01c1 " + log);
    }

    // One tick of the ignited monsters (the area's monster damage over time): the strongest ignite's fire damage through the monster's
    // resistance and damage taken (MonsterDotTaken), the buff off when the last ignite ends, and kills, as Righteous
    // Fire's burn (BurnAround) commits them. They burn on while the player is dead (the kill then gives no experience,
    // MonsterKilled).
    public static Task BurnIgnited(Player player) =>
        TickStrongestDots(player, m => m.Ignites, (m, now) => MonsterDotTaken(m, DamageType.Fire, now),
            "burnt to death by ignite");

    // One tick of a strongest-only set (ignites, bleeds: <paramref name="of"/>), <paramref name="factor"/> the monster's
    // mitigation of it, <paramref name="aggravated"/> the strongest aggravated bleed's extra share (MonsterIgnites.Advance).
    static async Task TickStrongestDots(Player player,
        Func<SpawnedMonster, MonsterIgnites> of, Func<SpawnedMonster, double, double> factor, string died,
        Func<SpawnedMonster, double, double>? aggravated = null, Action<SpawnedMonster, double>? settle = null)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea) return;
            double now = ProjectileWorldTime(player);
            foreach (var (id, m) in player.Area.Monsters)
            {
                if (OwnedGolemActor(player, id, m)) continue;
                var set = of(m);
                if (set.Idle) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) set.Clear();
                else
                {
                    settle?.Invoke(m, now);
                    damage = set.Advance(now, factor(m, now), aggravated?.Invoke(m, now) ?? 0);
                }
                if (!set.Burning) EndDot(player, id, ref set.Buff, set.BuffSource);
                if (damage == 0) continue;
                (m.Life, bool stageStarts) = TakeOwnerDamageLocked(player, m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    CollectVaalSoulAtDeathLocked(player, m);
                    EndDot(player, id, ref set.Buff, set.BuffSource);
                    set.Clear(burnt: true);
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) CommitBossStage(player, id, m, now);
            }
        }
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
            {
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
                EndDot(player, id, ref m.Poisons.Buff, m.Poisons.BuffSource);
            }
            Note(life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) {died}" : null);
            if (life == 0) await DotKilled(player, id, m);
        }
    }
}
