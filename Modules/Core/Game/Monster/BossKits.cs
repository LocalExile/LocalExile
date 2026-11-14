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
                || id.Contains("EmptyAction", StringComparison.Ordinal)
                || id.Contains("Emerge", StringComparison.Ordinal)
                || id.Contains("Retreat", StringComparison.Ordinal))) continue;
            if (!(catalog.IsAttack(effect) || catalog.IsSpell(effect) || catalog.IsProjectile(effect))) continue;
            if (!usable.Contains(effect)) usable.Add(effect);
        }
        return usable;
    }

    /// <summary>Its kit: the effect the monster's own default attack uses first (so a boss keeps the attack it had
    /// before the kit), then the variety's other effects in its order. Empty for anything that is not a kit boss.</summary>
    /// <param name="defaultAttack">The attack the monster would have swung without a kit: the kit's first entry is that
    /// same attack under the kit's own id, so a boss fights with the reach, speed and damage it had before.</param>
    public static IReadOnlyList<MonsterKitSkill> Kit(ClientState conn, MonsterCatalog.Variety variety, byte level,
        int defaultEffect = -1, MonsterAttack? defaultAttack = null)
    {
        var catalog = SkillsFor(conn);
        if (!IsKitBoss(catalog, variety)) return [];
        List<MonsterKitSkill> kit = [];
        IReadOnlyList<int> effects = Effects(catalog, variety);
        if (defaultEffect >= 0 && effects.Contains(defaultEffect)) effects = [defaultEffect, .. effects.Where(e => e != defaultEffect)];
        foreach (int effect in effects)
        {
            int effectLevel = catalog.MaxLevelAt(effect, level);
            double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
            ushort id = (ushort)(0x4000 + kit.Count);
            var attack = kit.Count == 0 && defaultAttack is not null ? defaultAttack with { SkillId = id } : AttackFor(conn, variety, level, effect, id);
            // An effect that hits for nothing, summons nothing and fires nothing is no action: a flame breath's own
            // segments, a trigger that waits for the geometry it points at, a shape the client plays and the server
            // never answers. The boss stands through it and the player sees a swing that does nothing. The monster's own
            // default attack is kept whatever it is: it is the attack it had before the kit.
            bool acts = !attack.Damage.IsZero || attack.Summon is not null || attack.Projectile is not null;
            if (!acts && kit.Count > 0) continue;
            kit.Add(new(effect, attack, Math.Max(0, cooldown)));
        }
        return kit;
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
            var stats = entry.Attack.Projectile is null
                ? WithBehaviourVariation(conn, entry.Effect, entry.Attack.SkillStats())
                : MonsterProjectileSkillStats(conn, entry.Effect, effectLevel, entry.Attack);
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
        var summon = catalog.MonsterSummon(effect, actorLevel);
        return new(skillId, summon is { CastSeconds: > 0 } ? summon.CastSeconds : Math.Max(.25, seconds), reach, damage)
        {
            Spell = spell,
            Ranged = ranged,
            Projectile = projectile,
            Summon = summon,
            ReleaseEffect = projectile is null ? null : effect,
            Release = projectile is null ? null : MonsterReleaseFor(conn, variety, effect, actorLevel, projectile),
        };
    }

    /// <summary>The attack this boss acts with now: a ready skill of its kit that reaches the player, else the attack it
    /// had (the default swing, which the walk-up brings into reach). Null only when it has no attack at all.
    /// <para>A skill with a cooldown the tables give it is the boss's special: ready, it takes the turn, which is what
    /// the cooldown is for. Between specials (and for the skills without one, the default swing included) the skill used
    /// longest ago takes it, so the whole list is played rather than one move repeated; on a tie the default swing does,
    /// which is what a boss does with nothing to prove.</para></summary>
    public static MonsterAttack? Choose(SpawnedMonster monster, double now, double distance)
    {
        if (monster.Kit.Count == 0) return monster.Attack;
        MonsterKitSkill? best = null;
        int bestTier = -1;
        double bestUsed = 0;
        foreach (var entry in monster.Kit)
        {
            // A skill that does not reach the player is no candidate: the boss keeps its attack and walks up.
            if (entry.Attack.Range < distance) continue;
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
