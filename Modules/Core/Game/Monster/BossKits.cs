using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>A boss's skill kit, played by the ordinary monster AI: every granted effect the client can play as an action,
/// each with the cooldown the tables give it, chosen one action at a time. A boss without a controller of its own
/// (Monsters.PlanMonsterAi skips those) used to walk up and swing its single default attack forever; this is what gives
/// it the rest of its list.</summary>
public static class BossKits
{
    /// <summary>A boss the ordinary AI plays as a kit: a monster the campaign places as a boss (IsCampaignBoss: any of
    /// the ten acts' named monsters, big and small) or one with a life bar of its own, with more
    /// than one effect the client can play. A boss with an encounter controller never asks (PlanMonsterAi skips it
    /// before this).</summary>
    public static bool IsKitBoss(SkillCatalog catalog, MonsterCatalog.Variety? variety) =>
        variety is not null && (variety.BossHealthBar || IsCampaignBoss(variety)) && Effects(catalog, variety).Count > 1;

    /// <summary>Whether the campaign places this monster as a boss: an area with a town to return to names it in
    /// WorldAreas <c>Bosses_MonsterVarietiesKeys</c> (which index MonsterVarieties). That is every named monster of the
    /// ten acts, big and small, whether or not it has a life bar.</summary>
    public static bool IsCampaignBoss(MonsterCatalog.Variety? variety) =>
        variety is not null && CampaignBosses.Value.Contains(variety.Id);

    static readonly Lazy<HashSet<string>> CampaignBosses = new(LoadCampaignBosses);

    static HashSet<string> LoadCampaignBosses()
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (!ClientTables.Has("WorldAreas") || !ClientTables.Has("MonsterVarieties")) return ids;
        List<string> varieties = [];
        foreach (var v in ClientTables.Table("MonsterVarieties")) varieties.Add(v.Text("Id") ?? "");
        foreach (var area in ClientTables.Table("WorldAreas"))
        {
            // A town is what tells a campaign area from a league's or a test's: every act's areas return to one.
            if (area.IsNull("ParentTown_WorldAreasKey")) continue;
            foreach (int row in area.Ints("Bosses_MonsterVarietiesKeys"))
                if (row >= 0 && row < varieties.Count && varieties[row].Length > 0) ids.Add(varieties[row]);
        }
        return ids;
    }

    /// <summary>The variety's granted effects the client can play as an action: an attack, a spell or a projectile, and
    /// not a movement, teleport, daemon, memory-game, bond, mark or soul skill. A bond or a mark links the monster to a
    /// second object the server does not create (Dominus' ChillingBond crashed the client at 0x1401297ca, live
    /// 2026-11-05); a movement skill is the walk, not a swing (MavenNucleus.CombatEffect).
    /// </summary>
    public static IReadOnlyList<int> Effects(SkillCatalog catalog, MonsterCatalog.Variety variety)
    {
        if (variety.GrantedEffects is not { Count: > 0 } effects) return [];
        List<int> usable = [];
        foreach (int effect in effects)
        {
            if (effect < 0 || catalog.IsMovement(effect) || catalog.CastsOnDeath(effect)) continue;
            string? id = catalog.EffectId(effect);
            if (id is not null && (id.Contains("Teleport", StringComparison.Ordinal)
                || id.Contains("Daemon", StringComparison.Ordinal)
                || id.Contains("MemoryGame", StringComparison.Ordinal)
                || id.Contains("Bond", StringComparison.Ordinal)
                || id.Contains("Mark", StringComparison.Ordinal)
                || id.Contains("Souls", StringComparison.Ordinal)
                // This action drives the arena's ArakaaliTurret_A/B objects, not a standalone hit. Without them its
                // native shuffle dereferences an empty turret list (live Arakaali crash at 0x141c7a0e4).
                || id.StartsWith("SpiderGodActivateLaser", StringComparison.Ordinal)
                || id.Contains("EmptyAction", StringComparison.Ordinal)
                || id.Contains("Emerge", StringComparison.Ordinal)
                || id.Contains("Retreat", StringComparison.Ordinal))) continue;
            if (!(catalog.IsAttack(effect) || catalog.IsSpell(effect) || catalog.IsProjectile(effect))) continue;
            // A skill on a corpse is no hit of its own: without one the client plays nothing (MonsterSkills.OnCorpse).
            if (MonsterSkills.IsCorpseSkill(effect)) continue;
            if (!usable.Contains(effect)) usable.Add(effect);
        }
        return usable;
    }

    /// <summary>A unique's effects: those <see cref="Effects"/> plays as hits, then those it plays as a travel, a buff on
    /// itself or a curse (MonsterSkills.ShapeOf; a Rogue Exile's Leap Slam, Enduring Cry, Temporal Chains).</summary>
    public static IReadOnlyList<int> UniqueEffects(SkillCatalog catalog, MonsterCatalog.Variety variety)
    {
        List<int> effects = [.. Effects(catalog, variety)];
        foreach (int effect in variety.GrantedEffects ?? [])
            if (effect >= 0 && !effects.Contains(effect) && !catalog.CastsOnDeath(effect) && MonsterSkills.ShapeOf(catalog, effect) is not null)
                effects.Add(effect);
        return effects;
    }

    /// <summary>Its kit: the effect the monster's own default attack uses first (so a boss keeps the attack it had
    /// before the kit), then the variety's other effects in its order. Empty for anything that is not a kit boss.</summary>
    /// <param name="defaultAttack">The attack the monster would have swung without a kit: the kit's first entry is that
    /// same attack under the kit's own id, so a boss fights with the reach, speed and damage it had before.</param>
    /// <param name="unique">A hostile unique monster (a Rogue Exile): it plays its kit like a boss, bar or not.</param>
    public static IReadOnlyList<MonsterKitSkill> Kit(ClientState conn, MonsterCatalog.Variety variety, byte level,
        int defaultEffect = -1, MonsterAttack? defaultAttack = null, bool unique = false)
    {
        var catalog = SkillsFor(conn);
        if (!IsKitBoss(catalog, variety) && !(unique && UniqueEffects(catalog, variety).Count > 1)) return [];
        List<MonsterKitSkill> kit = [];
        IReadOnlyList<int> effects = unique ? UniqueEffects(catalog, variety) : Effects(catalog, variety);
        if (defaultEffect >= 0 && effects.Contains(defaultEffect)) effects = [defaultEffect, .. effects.Where(e => e != defaultEffect)];
        foreach (int effect in effects)
        {
            int effectLevel = catalog.MaxLevelAt(effect, level);
            double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
            ushort id = (ushort)(0x4000 + kit.Count);
            var attack = (kit.Count == 0 && defaultAttack is not null ? defaultAttack with { SkillId = id } : AttackFor(conn, variety, level, effect, id))
                with { Effect = effect };
            if (unique && MonsterSkills.UseOf(catalog, effect, level) is { } use)
            {
                // A travel reaches as far as it carries the monster; a buff or a curse is used from where it sees the player.
                attack = attack with { Use = use, Range = use.Shape == MonsterSkillShape.Travel ? MonsterSkills.TravelReach : Math.Max(variety.AggroRange, attack.Range) };
                cooldown = use.Shape == MonsterSkillShape.Travel ? Math.Max(cooldown, 3)
                    : use.Shape == MonsterSkillShape.Corpse ? Math.Max(cooldown, 2) : Math.Max(cooldown, use.Seconds + 2);
                kit.Add(new(effect, attack, cooldown));
                continue;
            }
            // An effect that hits for nothing, summons nothing and fires nothing is no action: a flame breath's own
            // segments, a trigger that waits for the geometry it points at, a shape the client plays and the server
            // never answers. The boss stands through it and the player sees a swing that does nothing. The monster's own
            // default attack is kept whatever it is: it is the attack it had before the kit.
            // A unique's ranged skill flies the projectile the client's art resolver picks for it, as an object of the
            // server's (a monster's action draws none of its own but a mortar's: Game.MonsterProjectileUse). One that
            // resolves to none is left out: its hit would land with nothing in flight.
            if (unique && kit.Count > 0)
            {
                // A totem's skill is its body's to use: the summon is kept only when that body has something to do.
                if (attack.Summon is { Effect: >= 0 } totem)
                {
                    if (BodyAttack(conn, Monsters.Find(totem.Variety.ToString()), level, totem.Effect, 0x4000) is null) continue;
                }
                else if (attack.Summon is null)
                {
                    if (Shaped(conn, variety, level, effect, attack) is not { } shaped) continue;
                    attack = shaped;
                }
            }
            bool acts = !attack.Damage.IsZero || attack.Summon is not null || attack.Projectile is not null;
            if (!acts && kit.Count > 0) continue;
            kit.Add(new(effect, attack, Math.Max(0, cooldown)));
        }
        return kit;
    }

    /// <summary>A unique's skill as it plays: a ranged one flies the projectile the client's art resolver picks for it,
    /// as an object of the server's (a monster's action draws none of its own but a mortar's: Game.MonsterProjectileUse);
    /// an area one lands within its radius; a trap is thrown, and plays one of those once set off (MonsterTraps). Null for
    /// one the server cannot play: a ranged skill whose art resolves no projectile (its hit would land with nothing in
    /// flight), a trap whose set-off skill hits for nothing or has no shape. Storm Call sets a marker that strikes later
    /// (Game.StormCallStrikeOf, MonsterMarkers).</summary>
    /// <param name="setOff">Shape what a trap of the skill plays once set off, rather than the throw.</param>
    public static MonsterAttack? Shaped(ClientState conn, MonsterCatalog.Variety variety, byte level, int effect, MonsterAttack attack,
        bool setOff = false)
    {
        var catalog = SkillsFor(conn);
        if (catalog.HasType(effect, "Trapped") && !setOff)
        {
            // A trap is thrown at the aim; what it plays when set off is the skill as any other use of it plays.
            if (Shaped(conn, variety, level, effect, attack, setOff: true) is not { } payload) return null;
            if (payload.Damage.IsZero || payload.Projectile is null && payload.AreaRadius <= 0) return null;
            return attack with { Trap = payload, Projectile = null, Release = null, ReleaseEffect = null, AreaRadius = 0 };
        }
        if (attack.Ranged && attack.Projectile is null)
        {
            if (ArtShot(catalog, effect, level) is not { } shot) return null;
            return attack with { Projectile = shot, ReleaseEffect = effect, Release = MonsterReleaseFor(conn, variety, effect, level, shot) };
        }
        if (attack.Projectile is not null) return attack;
        if (catalog.HasType(effect, "SummonsTotem") && attack.Summon is null) return null;
        if (AreaRadius(catalog, effect, level) is double radius && !catalog.IsMelee(effect))
            attack = attack with { AreaRadius = radius, AreaAtSelf = catalog.HasType(effect, "Nova") };
        if (catalog.SameActionEffects(effect).Any(e => Game.IsStormCall(catalog.EffectId(e))))
            return attack.AreaRadius > 0 && Game.StormCallStrikeOf(catalog, effect, level) is { } strike
                ? attack with { Strike = strike } : null;
        return attack;
    }

    /// <summary>What a summoned body (a monster's totem) does with the skill it was summoned for: that skill at the
    /// summoner's level, as a unique plays it (<see cref="Shaped"/>), from as far as base_totem_range reaches when the
    /// skill names one (cells, as the player's totems target: Game.TotemTargetRange). Null when it would do nothing.</summary>
    public static MonsterAttack? BodyAttack(ClientState conn, MonsterCatalog.Variety? body, byte level, int effect, ushort id)
    {
        if (body is null) return null;
        var catalog = SkillsFor(conn);
        var attack = AttackFor(conn, body, level, effect, id) with { Effect = effect };
        if (attack.Summon is not null) return null;
        if (Shaped(conn, body, level, effect, attack) is not { } shaped) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        if (Stats.Key("base_totem_range") is uint key && stats.GetValueOrDefault(key) is > 0 and var range)
            shaped = shaped with { Range = range };
        return shaped.Damage.IsZero && shaped.Projectile is null ? null : shaped;
    }

    /// <summary>An area skill's radius (cells) from its own stats at the level (SkillArea: its base radius scaled by its
    /// area modifiers); null for a skill that is no area skill or names no radius.</summary>
    public static double? AreaRadius(SkillCatalog catalog, int effect, byte level)
    {
        if (!catalog.HasType(effect, "Area")) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The action class may add its own radius to the stat's (SkillArea.ActionBaseRadius: Cold Snap's 15, Storm
        // Call's 20); a skill with neither has no area the server can name.
        int radius = Get("active_skill_base_area_of_effect_radius") + (SkillArea.ActionBaseRadius(SkillCatalog.ActionTypeId(effect)) ?? 0);
        if (radius <= 0) return null;
        return SkillArea.Radius(radius, Get, catalog.IsSpell(effect));
    }

    /// <summary>A skill's projectile as the client resolves its art (SkillCatalog.SkillArtProjectile), else the row its
    /// action class creates for the player's skill on the same ActiveSkills row (SkillCatalog.SharedProjectile), with the count its
    /// stats give one release (number_of_projectiles_override, else base_number_of_projectiles plus
    /// number_of_additional_projectiles); null when the art names none.</summary>
    public static SkillCatalog.MonsterProjectileInfo? ArtShot(SkillCatalog catalog, int effect, byte level)
    {
        if ((catalog.SkillArtProjectile(effect) ?? catalog.SharedProjectile(effect)) is not { } art) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int count = Get("number_of_projectiles_override") is > 0 and var fixedCount ? fixedCount
            : Math.Max(1, Get("base_number_of_projectiles")) + Get("number_of_additional_projectiles");
        // A nova (projectiles_nova: Lightning Trap's) fires its projectiles around the full turn.
        bool nova = Stats.Key("projectiles_nova") is uint novaKey && stats.GetValueOrDefault(novaKey) != 0;
        return new(art.Id, art.Speed, false, art.CollisionRadius) { Count = Math.Max(1, count), SpreadRadians = nova ? Math.Tau : Math.PI / 6 };
    }

    /// <summary>The kit as the actor's skill list: 0x4000 + the kit index names the action, as the campaign's own kits do
    /// (DepravedTrinity.Skill).</summary>
    public static IReadOnlyList<GrantedSkill> SkillsOf(ClientState conn, MonsterCatalog.Variety variety, byte level,
        IReadOnlyList<MonsterKitSkill> kit)
    {
        var catalog = SkillsFor(conn);
        List<GrantedSkill> skills = [];
        for (int i = 0; i < kit.Count; i++)
        {
            var entry = kit[i];
            int effectLevel = catalog.MaxLevelAt(entry.Effect, level);
            var stats = MonsterActionSkillStats(conn, entry.Effect, effectLevel, entry.Attack);
            if (catalog.FindByRow((ushort)(0x4000 + i), entry.Effect, effectLevel, stats) is { } granted) skills.Add(granted);
        }
        return skills;
    }

    /// <summary>One action of a kit, built the way a monster's default attack is (DepravedTrinity.Attack): the skill's
    /// own damage and cast time at the actor's level, the reach it reaches the player from, and the projectile whose flight
    /// carries the hit. Its id is the kit slot (0x4000 + the kit index), which is the one the actor's skill list names.</summary>
    public static MonsterAttack AttackFor(ClientState conn, MonsterCatalog.Variety variety, byte actorLevel, int effect, ushort skillId)
    {
        var catalog = SkillsFor(conn);
        var native = GolemData.Skill(catalog, effect, actorLevel);
        int Raw(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        bool spell = catalog.IsSpell(effect);
        var damage = DamagePacket.Empty;
        if (spell)
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = Raw($"spell_minimum_base_{slug}_damage"), max = Raw($"spell_maximum_base_{slug}_damage");
                if (max > 0) damage = damage.Add(type, new(min, max));
            }
        if (!spell)
        {
            var (min, max) = Monsters.BaseDamage(variety, actorLevel) ?? (1, 1);
            int more = Raw("active_skill_damage_+%_final");
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier * Math.Max(0, 1 + more / 100d));
        }
        double seconds = (spell && catalog.CastTimeMs(effect) > 0 ? catalog.CastTimeMs(effect) / 1000d : variety.AttackMs / 1000d)
            / Math.Max(.01, 1 + Raw(spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100d);
        // A ranged skill reaches its variety's MaximumAttackDistance (AttackRange); a spell keeps the aggro range it is
        // given, as the default attack of a ranged spell does (Monsters.Spawn). A melee skill reaches its swing.
        bool ranged = spell && !catalog.IsMelee(effect) || catalog.IsRangedAttack(effect);
        double reach = ranged && spell ? Math.Max(variety.AttackRange, variety.AggroRange) : variety.AttackRange;
        var projectile = ranged ? MavenNucleus.Shot(catalog, effect) : null;
        var summon = MonsterSummonOf(catalog, effect, actorLevel);
        int hits = 1 + Math.Max(0, Raw("base_skill_number_of_additional_hits"))
            + (Raw("skill_double_hits_when_dual_wielding") != 0 && DualWields(variety) ? 1 : 0);
        return new(skillId, summon is { CastSeconds: > 0 } ? summon.CastSeconds : Math.Max(.25, seconds), reach, damage)
        {
            Spell = spell,
            Ranged = ranged,
            Projectile = projectile,
            Summon = summon,
            ReleaseEffect = projectile is null ? null : effect,
            Release = projectile is null ? null : MonsterReleaseFor(conn, variety, effect, actorLevel, projectile),
            Hits = catalog.IsMelee(effect) && !catalog.IsChannelled(effect) ? hits : 1,
        };
    }

    /// <summary>The attack this boss acts with now: a ready skill of its kit that reaches the player, else the attack it
    /// had (the default swing, which the walk-up brings into reach). Null only when it has no attack at all.
    /// <para>A skill with a cooldown the tables give it is the boss's special: ready, it takes the turn, which is what
    /// the cooldown is for. Between specials (and for the skills without one, the default swing included) the skill used
    /// longest ago takes it, so the whole list is played rather than one move repeated; on a tie the default swing does,
    /// which is what a boss does with nothing to prove.</para></summary>
    public static MonsterAttack? Choose(SpawnedMonster monster, double now, double distance, Func<MonsterAttack, bool>? usable = null)
    {
        if (monster.Kit.Count == 0) return monster.Attack;
        MonsterKitSkill? best = null;
        int bestTier = -1;
        double bestUsed = 0;
        foreach (var entry in monster.Kit)
        {
            // A skill that does not reach the player is no candidate: the boss keeps its attack and walks up.
            if (entry.Attack.Range < distance) continue;
            // A travel is for closing a distance: nearer than its least, the monster swings.
            if (entry.Attack.Use is { } use && distance < use.MinRange) continue;
            // A skill that needs what is not there (a corpse) waits.
            if (usable is not null && !usable(entry.Attack)) continue;
            double used = monster.KitReadyAt.GetValueOrDefault(entry.Effect);
            bool special = entry.CooldownSeconds > 0;
            if (special && used > now) continue;
            bool better;
            if (best is null) better = true;
            else if (special != (bestTier > 0)) better = special;
            else if (used != bestUsed) better = used < bestUsed;
            else better = entry.Effect == monster.Kit[0].Effect;
            if (!better) continue;
            best = entry;
            bestTier = special ? 1 : 0;
            bestUsed = used;
        }
        return best?.Attack ?? monster.Attack;
    }

    /// <summary>Records the swing just ordered: the skill it used goes on cooldown, and even one without a cooldown
    /// records when it was used, which is what <see cref="Choose"/> rotates by. Called with the attack the order names,
    /// under the Monsters lock.</summary>
    public static void Consume(SpawnedMonster monster, MonsterAttack attack, double now)
    {
        if (monster.Kit.Count == 0) return;
        foreach (var entry in monster.Kit)
            if (entry.Attack.SkillId == attack.SkillId)
            {
                monster.KitReadyAt[entry.Effect] = now + entry.CooldownSeconds;
                return;
            }
    }
}
