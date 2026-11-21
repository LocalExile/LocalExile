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
        variety is not null && (variety.BossHealthBar || IsCampaignBoss(variety) || variety.Id == Yugul.Image
            || SolarisLunaris.IsCelestial(variety))
            && Effects(catalog, variety).Count > 1;

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
            if (SolarisLunaris.IsBody(variety) && effect is 6739 or 6740) continue;
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
            var stats = MonsterActionSkillStats(conn, entry.Effect, effectLevel, entry.Attack);
            if (catalog.FindByRow((ushort)(0x4000 + i), entry.Effect, effectLevel, stats) is { } granted) skills.Add(granted);
        }
        return skills;
    }

    /// <summary>Piety, the Abomination's sweeping 360-degree beam (<c>PietyCorruptedLightningBeam</c>, ActionType 354):
    /// rotates −900° (2.5 turns) over the active portion of its 11.2 s cast, 120-cell length, 0.33 s per-target hit
    /// cooldown (3.29.3c client RE: 0x141b49320, 0x141b4960d, 0x141b4974d).</summary>
    public const int PietyBeamEffect = 5003;

    /// <summary>Avarius and Innocence (<c>transitionable_num_states = 3</c>) must start in state 1, not their final state 3:
    /// the client's <c>AvariusSuperProjectile</c> admission check (0x141a3775a) refuses to run when Transitionable is 3.
    /// Amarissa and Ambrosia (<c>SeaWitchBossDaughter.ot</c>) have <c>Targetable = false</c> until state 2, and Merveil
    /// Form 1 starts in state 1 so stepping 1 → 2 on aggro triggers her native <c>MerveilStartFight</c> dialogue.</summary>
    public static byte StartState(MonsterCatalog.Variety variety, byte otherwise) =>
        variety.Id is "Metadata/Monsters/AvariusCasticus/AvariusCasticus"
            or "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine"
            or "Metadata/Monsters/AvariusCasticus/AvariusCasticusMap"
            or "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivineMap"
            or "Metadata/Monsters/seawitchillusion/BossMerveil"
            ? (byte)1
        : variety.Id is "Metadata/Monsters/Seawitch/SeaWitchBossSpark"
            or "Metadata/Monsters/Seawitch/SeaWitchBossFirestorm"
            ? (byte)2
        : otherwise;

    /// <summary>Shared StateMachine overrides for kit bosses at spawn: Brutus's <c>brute.ot</c> gates his chain-hook
    /// dialogue (<c>BrutusChainRandom</c>) behind <c>dialogue_lines == 1</c>.</summary>
    public static StateMachineData? StartStates(MonsterCatalog.Variety variety) =>
        variety.Id == "Metadata/Monsters/Brute/BossBrute"
            ? variety.StateMachine?.Set("dialogue_lines", 1) : null;

    /// <summary>The granted effect row a boss's kit entry came from for <paramref name="attack"/>, or −1.</summary>
    public static int EffectOf(SpawnedMonster monster, MonsterAttack attack)
    {
        for (int i = 0; i < monster.Kit.Count; i++)
            if (monster.Kit[i].Attack.SkillId == attack.SkillId) return monster.Kit[i].Effect;
        return -1;
    }

    /// <summary>Whether <paramref name="attack"/> is off cooldown for this boss's kit (always true for non-kit monsters
    /// and zero-cooldown skills).</summary>
    public static bool Ready(SpawnedMonster monster, MonsterAttack attack, double now)
    {
        for (int i = 0; i < monster.Kit.Count; i++)
        {
            var entry = monster.Kit[i];
            if (entry.Attack.SkillId == attack.SkillId)
                return entry.CooldownSeconds <= 0 || monster.KitReadyAt.GetValueOrDefault(entry.Effect) <= now;
        }
        return true;
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
        // A ranged skill reaches its variety's MaximumAttackDistance (AttackRange); a spell keeps the aggro range it is
        // given, as the default attack of a ranged spell does (Monsters.Spawn). A melee skill reaches its swing.
        bool ranged = spell && !catalog.IsMelee(effect) || catalog.IsRangedAttack(effect);
        var damage = DamagePacket.Empty;
        if (spell || ranged)
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = Raw($"spell_minimum_base_{slug}_damage"), max = Raw($"spell_maximum_base_{slug}_damage");
                if (max > 0) damage = damage.Add(type, new(min, max));
            }
        if (spell && damage.IsZero)
        {
            int coneMin = Raw("malachai_elemental_cone_spell_minimum_damage"),
                coneMax = Raw("malachai_elemental_cone_spell_maximum_damage");
            if (coneMax > 0)
            {
                double minThird = coneMin / 3d, maxThird = coneMax / 3d;
                damage = damage.Add(DamageType.Fire, new(minThird, maxThird))
                    .Add(DamageType.Cold, new(minThird, maxThird))
                    .Add(DamageType.Lightning, new(minThird, maxThird));
            }
        }
        if (!spell && damage.IsZero)
        {
            var (min, max) = Monsters.BaseDamage(variety, actorLevel) ?? (1, 1);
            int more = Raw("active_skill_damage_+%_final");
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier * Math.Max(0, 1 + more / 100d));
        }
        double seconds = (spell && catalog.CastTimeMs(effect) > 0 ? catalog.CastTimeMs(effect) / 1000d : variety.AttackMs / 1000d)
            / Math.Max(.01, 1 + Raw(spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100d);
        double reach = ranged && spell ? Math.Max(variety.AttackRange, variety.AggroRange)
            : ranged && Raw("spell_maximum_action_distance_+%") is > 0 and var distPct
                ? Math.Min(Math.Max(variety.AttackRange, variety.AggroRange), variety.AttackRange * (1 + distPct / 100d))
                : variety.AttackRange;
        var projectile = ranged ? MavenNucleus.Shot(catalog, effect) : null;
        if (projectile is not null && !spell && reach < 45)
            reach = 45;
        var summon = catalog.MonsterSummon(effect, actorLevel);
        bool dualWielding = variety.Weapons.Any(l => l.Slot == 1 && l.Stance is not (9 or 10));
        return new(skillId, summon is { CastSeconds: > 0 } ? summon.CastSeconds : Math.Max(.25, seconds), reach, damage)
        {
            Spell = spell,
            DualWielding = dualWielding,
            CorruptedBlood = CorruptedBloodOnHit.From(Raw),
            Ranged = ranged,
            Projectile = projectile,
            Summon = summon,
            ReleaseEffect = projectile is null ? null : effect,
            Release = projectile is null ? null : MonsterReleaseFor(conn, variety, effect, actorLevel, projectile),
        };
    }

    /// <summary>The attack this boss acts with now: a ready skill of its kit that reaches the player, else a non-cooldown
    /// chase attack (preferring melee walk-up so an out-of-reach boss closes in instead of re-casting an on-cooldown
    /// ranged special). Null only when it has no attack at all.
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
            if (!Arakaali.Allowed(monster, entry.Effect)) continue;
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
        return best?.Attack ?? Fallback(monster);
    }

    // When no kit skill is both in reach and off cooldown, never return the last-used special in monster.Attack (which
    // would be an InPlace ranged skill on cooldown and immediately re-fire, bypassing its cooldown). Prefer a
    // zero-cooldown pathing melee swing so the boss walks into melee reach, then any zero-cooldown skill, then any
    // pathing melee skill, then the first allowed entry.
    static MonsterAttack? Fallback(SpawnedMonster monster)
    {
        MonsterAttack? anyNoCooldown = null, anyMelee = null, anyAllowed = null;
        for (int i = 0; i < monster.Kit.Count; i++)
        {
            var entry = monster.Kit[i];
            if (!Arakaali.Allowed(monster, entry.Effect)) continue;
            bool noCooldown = entry.CooldownSeconds <= 0;
            bool melee = !entry.Attack.InPlace;
            if (melee && noCooldown) return entry.Attack;
            anyNoCooldown ??= noCooldown ? entry.Attack : null;
            anyMelee ??= melee ? entry.Attack : null;
            anyAllowed ??= entry.Attack;
        }
        return anyNoCooldown ?? anyMelee ?? anyAllowed;
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
