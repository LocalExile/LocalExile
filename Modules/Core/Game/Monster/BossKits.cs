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
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadBossKitsData()
    {
        CampaignBosses = LoadCampaignBosses();
        GemGranted = [.. ClientTables.TableOrEmpty("GemEffects")
            .SelectMany(g => new[] { g.IntOrNull("GrantedEffect"), g.IntOrNull("GrantedEffect2") }).OfType<int>()];
    }

    /// <summary>A boss the ordinary AI plays as a kit: a monster the campaign places as a boss (IsCampaignBoss: any of
    /// the ten acts' named monsters, big and small), one with a life bar, or an Essence Karui spirit, with more
    /// than one effect the client can play. A boss with an encounter controller never asks (PlanMonsterAi skips it
    /// before this).</summary>
    public static bool IsKitBoss(SkillCatalog catalog, MonsterCatalog.Variety? variety) =>
        variety is not null && (variety.BossHealthBar || IsCampaignBoss(variety) || variety.Id == Yugul.Image
            || SolarisLunaris.IsCelestial(variety) || variety.Id == EssenceKaruiSpirit)
            && Effects(catalog, variety).Count > 1;

    /// <summary>Whether the campaign places this monster as a boss: an area with a town to return to names it in
    /// WorldAreas <c>Bosses_MonsterVarietiesKeys</c> (which index MonsterVarieties). That is every named monster of the
    /// ten acts, big and small, whether or not it has a life bar.</summary>
    public static bool IsCampaignBoss(MonsterCatalog.Variety? variety) =>
        variety is not null && CampaignBosses.Contains(variety.Id);

    static HashSet<string> CampaignBosses = default!;

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
    public static IReadOnlyList<int> Effects(SkillCatalog catalog, MonsterCatalog.Variety variety, bool forSummon = false)
    {
        if (variety.GrantedEffects is not { Count: > 0 } effects) return [];
        List<int> usable = [];
        foreach (int effect in effects)
        {
            if (SolarisLunaris.IsBody(variety) && effect is 6739 or 6740) continue;
            if (effect < 0 || catalog.CastsOnDeath(effect) || IsDamageTakenTrigger(catalog, effect)
                // A summon's movement attack is how it reaches the player (Doubt's spider MonsterFlickerStrike); a boss
                // keeps only its non-movement skills.
                || (catalog.IsMovement(effect) && !(forSummon && catalog.IsAttack(effect)))
                // An aura is no move: it stands on the monster from its spawn (MonsterSkills.Auras).
                || catalog.IsAura(effect)) continue;
            string? id = catalog.EffectId(effect);
            if (id is not null && (id.Contains("Teleport", StringComparison.Ordinal)
                || (!forSummon && id.Contains("Daemon", StringComparison.Ordinal))
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
            // A summon row the tables give no attack, spell or projectile type (Piety's eye spawners: their
            // ActiveSkills rows carry no type at all) is still an action when the server can name what it raises;
            // anything else without a type is no action.
            if (!(catalog.IsAttack(effect) || catalog.IsSpell(effect) || catalog.IsProjectile(effect)) && !BossSummons.HasOverride(effect)) continue;
            // A skill on a corpse is no hit of its own: without one the client plays nothing (MonsterSkills.OnCorpse).
            if (MonsterSkills.IsCorpseSkill(effect)) continue;
            if (!usable.Contains(effect)) usable.Add(effect);
        }
        return usable;
    }

    /// <summary>A summoned monster's kit: its whole usable effect list, not only its cooldown spells. A totem casts its
    /// Arc (no cooldown) as well as its shocked ground, a spider its Viper Strike, a summoned daemon its nova / ball
    /// lightning / storm cascade. <see cref="Effects"/> with <c>forSummon</c> keeps a summon's movement attacks; an effect
    /// that hits for nothing, summons nothing and fires nothing is no action. A cooldown spell stays a special (the summon
    /// idles between casts); a zero-cooldown one rotates at its cast time.</summary>
    public static IReadOnlyList<MonsterKitSkill> SummonKit(Player conn, MonsterCatalog.Variety variety, byte level)
    {
        var catalog = SkillsFor(conn);
        List<MonsterKitSkill> kit = [];
        foreach (int effect in Effects(catalog, variety, forSummon: true))
        {
            int effectLevel = catalog.MaxLevelAt(effect, level);
            var attack = AttackFor(conn, variety, level, effect, (ushort)(0x4000 + kit.Count));
            if (attack.Damage.IsZero && attack.Summon is null && attack.Projectile is null) continue;
            kit.Add(new(effect, attack, catalog.CooldownMs(effect, effectLevel) / 1000d));
        }
        return kit;
    }

    /// <summary>A monster's own kit with a caller's extra specials appended (SpawnMonster's <c>extraKit</c>). An effect the
    /// kit already plays is not a second copy; every appended entry takes the next free kit id (0x4000 + its index), the id
    /// <see cref="SkillsOf"/> sends the actor and <see cref="Consume"/> matches its cooldown by.</summary>
    public static IReadOnlyList<MonsterKitSkill> WithExtra(IReadOnlyList<MonsterKitSkill> kit, IReadOnlyList<MonsterKitSkill>? extra)
    {
        if (extra is not { Count: > 0 }) return kit;
        List<MonsterKitSkill> appended = [];
        foreach (var entry in extra)
        {
            if (kit.Any(e => e.Effect == entry.Effect) || appended.Any(e => e.Effect == entry.Effect)) continue;
            appended.Add(entry with { Attack = entry.Attack with { SkillId = (ushort)(0x4000 + kit.Count + appended.Count) } });
        }
        return appended.Count > 0 ? [.. kit, .. appended] : kit;
    }

    /// <summary>A unique's effects: those <see cref="Effects"/> plays as hits, then those it plays as a travel, a buff on
    /// itself or a curse (MonsterSkills.ShapeOf; a Rogue Exile's Leap Slam, Enduring Cry, Temporal Chains), then its
    /// triggers on damage taken, which only the damage it takes plays (<see cref="DamageTakenThreshold"/>).</summary>
    public static IReadOnlyList<int> UniqueEffects(SkillCatalog catalog, MonsterCatalog.Variety variety)
    {
        List<int> effects = [.. Effects(catalog, variety)];
        foreach (bool trigger in new[] { false, true })
            foreach (int effect in variety.GrantedEffects ?? [])
                if (effect >= 0 && !effects.Contains(effect) && !catalog.CastsOnDeath(effect) && IsDamageTakenTrigger(catalog, effect) == trigger
                    && (MonsterSkills.ShapeOf(catalog, effect) is not null || trigger && (catalog.IsAttack(effect) || catalog.IsSpell(effect))))
                    effects.Add(effect);
        return effects;
    }

    /// <summary>Whether the effect is cast when its user has taken damage (<c>cast_on_damage_taken_%</c>: a Rogue Exile's
    /// EnfeebleOnDamageTaken, MoltenShellOnDamageTaken, ImmortalCallOnDamageTaken). It is no move the monster picks: the
    /// damage it takes sets it off.</summary>
    public static bool IsDamageTakenTrigger(SkillCatalog catalog, int effect) => DamageTakenThreshold(catalog, effect, 1) > 0;

    /// <summary>How much damage the monster takes before the trigger fires at this level
    /// (<c>cast_on_damage_taken_threshold</c>, at least 1); 0 when the effect is no trigger on damage taken.</summary>
    public static int DamageTakenThreshold(SkillCatalog catalog, int effect, int level)
    {
        if (effect < 0 || !(Stat.CastOnDamageTakenPct is var chance)) return 0;
        var stats = catalog.EffectStats(effect, level);
        if (!stats.Any(stat => stat.Item1 == chance && stat.Item2 > 0)) return 0;
        uint? threshold = Stat.CastOnDamageTakenThreshold;
        return Math.Max(1, stats.FirstOrDefault(stat => stat.Item1 == threshold).Item2);
    }

    /// <summary>The life a kit monster just lost to a hit (the hit funnel, Game.ApplyMonsterHitLocked): it counts toward
    /// each of its triggers on damage taken. In the area's frame.</summary>
    public static void TakeDamage(SpawnedMonster monster, long lost)
    {
        if (lost <= 0) return;
        foreach (var entry in monster.Kit)
            if (entry.TriggerThreshold > 0)
                monster.KitDamageTaken[entry.Effect] = monster.KitDamageTaken.GetValueOrDefault(entry.Effect) + lost;
    }

    /// <summary>Its kit: the effect the monster's own default attack uses first (so a boss keeps the attack it had
    /// before the kit), then the variety's other effects in its order. Empty for anything that is not a kit boss.</summary>
    /// <param name="defaultAttack">The attack the monster would have swung without a kit: the kit's first entry is that
    /// same attack under the kit's own id, so a boss fights with the reach, speed and damage it had before.</param>
    /// <param name="unique">A hostile unique monster (a Rogue Exile): it plays its kit like a boss, bar or not.</param>
    public static IReadOnlyList<MonsterKitSkill> Kit(Player player, MonsterCatalog.Variety variety, byte level,
        int defaultEffect = -1, MonsterAttack? defaultAttack = null, bool unique = false)
    {
        var catalog = SkillsFor(player);
        if (!IsKitBoss(catalog, variety) && !(unique && UniqueEffects(catalog, variety).Count > 1)) return [];
        List<MonsterKitSkill> kit = [];
        IReadOnlyList<int> effects = unique ? UniqueEffects(catalog, variety) : Effects(catalog, variety);
        if (defaultEffect >= 0 && effects.Contains(defaultEffect) && !IsDamageTakenTrigger(catalog, defaultEffect)) effects = [defaultEffect, .. effects.Where(e => e != defaultEffect)];
        foreach (int effect in effects)
        {
            int effectLevel = catalog.MaxLevelAt(effect, level);
            double cooldown = catalog.CooldownMs(effect, effectLevel) / 1000d;
            bool isBarrage = BossMoves.BarrageOf(catalog, effect) is not null;
            bool isTeleport = BossMoves.IsTeleport(catalog, effect);
            int trigger = DamageTakenThreshold(catalog, effect, effectLevel);
            ushort id = (ushort)(0x4000 + kit.Count);
            var built = AttackFor(player, variety, level, effect, id);
            // A unique's default skill that places a totem or a minion is that summon, not the plain hit its default
            // attack makes of it (Vickas Giantbone's Ethereal Knives totem).
            var attack = (kit.Count == 0 && defaultAttack is not null && !(unique && built.Summon is not null)
                ? defaultAttack with { SkillId = id } : built) with { Effect = effect };
            if (unique && MonsterSkills.UseOf(catalog, effect, level) is { } use)
            {
                // A travel reaches as far as it carries the monster; a buff on itself is used from where it sees the
                // player; a spell on the player or a corpse (a curse, Volatile Dead) from as far as its action reaches.
                var useStats = GolemData.Skill(catalog, effect, level).Stats;
                int UseStat(uint key) => useStats.GetValueOrDefault(key);
                double reach = use.Shape == MonsterSkillShape.Travel ? MonsterSkills.TravelReach
                    : use.Shape != MonsterSkillShape.Self && catalog.IsSpell(effect)
                        ? Math.Max(attack.Range, Math.Min(variety.AggroRange, ActionClasses.SpellActionDistance(effect, UseStat)))
                        : Math.Max(variety.AggroRange, attack.Range);
                attack = attack with { Use = use, Range = reach };
                cooldown = isTeleport ? (cooldown > 0 ? cooldown : BossMoves.TeleportCooldown)
                    : BossMoves.IsMove(catalog, effect) ? (cooldown > 0 ? Math.Max(cooldown, 3) : BossMoves.MoveCooldown)
                    : use.Shape == MonsterSkillShape.Travel ? Math.Max(cooldown, 3)
                    : use.Shape == MonsterSkillShape.Corpse ? Math.Max(cooldown, 2) : Math.Max(cooldown, use.Seconds + 2);
                kit.Add(new(effect, attack, cooldown) { TriggerThreshold = trigger, IsTeleport = isTeleport, IsBarrage = isBarrage });
                continue;
            }
            // An effect that hits for nothing, summons nothing and fires nothing is no action: a flame breath's own
            // segments, a trigger that waits for the geometry it points at, a shape the client plays and the server
            // never answers. The boss stands through it and the player sees a swing that does nothing. The monster's own
            // default attack is kept whatever it is: it is the attack it had before the kit.
            // A unique's ranged skill flies the projectile the client's art resolver picks for it, as an object of the
            // server's (a monster's action draws none of its own but a mortar's: Game.MonsterProjectileUse). One that
            // resolves to none is left out: its hit would land with nothing in flight.
            // Every skill of a unique is shaped, its default one too: a default that does not shape stays the attack it
            // had before the kit.
            if (unique)
            {
                // A totem's skill is its body's to use: the summon is kept only when that body has something to do.
                if (attack.Summon is { Effect: >= 0 } totem)
                {
                    if (BodyAttack(player, Monsters.Find(totem.Variety.ToString()), level, totem.Effect, 0x4000) is null && kit.Count > 0) continue;
                }
                else if (attack.Summon is null)
                {
                    // A hazard raised under the player that strikes a moment later (Merveil's geysers).
                    if (BossMoves.StrikeOf(catalog, effect, level) is { } hazard)
                    {
                        attack = attack with { Strike = hazard.Strike, AreaRadius = hazard.Radius, Projectile = null, Release = null, ReleaseEffect = null };
                        cooldown = Math.Max(cooldown, hazard.Cooldown);
                    }
                    else if (Shaped(player, variety, level, effect, attack) is { } shaped) attack = shaped;
                    // A monster's own skill the shaping has no form for stays the action it was: the client plays its
                    // clip and what that draws (Merveil, the Twisted's geysers, screech and cascade), and it hits as a
                    // plain hit.
                    else if (kit.Count > 0 && !(Acts(attack) && KeepsUnshaped(catalog, variety, effect))) continue;
                }
            }
            if (!Acts(attack) && kit.Count > 0) continue;
            kit.Add(new(effect, attack, Math.Max(0, cooldown)) { TriggerThreshold = trigger, IsTeleport = isTeleport, IsBarrage = isBarrage });
        }
        return kit;
    }

    // Words of an id that mark a piece another skill sets off (an impact, an explosion, a death effect, a ground), not
    // an action the monster starts itself.
    static readonly string[] SetOffWords = ["Impact", "Explode", "Explosion", "Death", "GroundEffect", "Trigger", "Daemon", "NoEffects"];

    /// <summary>Whether a unique keeps a skill the shaping has no form for, as the plain action it was before the shaping
    /// (<see cref="Kit"/>). Bosses and uniques keep a monster's own skill, but not a piece another skill sets off (a
    /// triggered skill, a geometry spell's hit "GS…", an id with a word of SetOffWords) and not a player skill used by a
    /// Rogue Exile, a shade or a mercenary: those need their form (a Flameblast's channel, a mine), and a plain hit would
    /// land with nothing drawn.</summary>
    public static bool KeepsUnshaped(SkillCatalog catalog, MonsterCatalog.Variety variety, int effect)
    {
        if (!KeepUnshapedForUniques && !IsKitBoss(catalog, variety)) return false;
        string id = catalog.EffectId(effect) ?? "";
        if (id.Length == 0 || catalog.HasType(effect, "Triggered") || id.StartsWith("GS", StringComparison.Ordinal)
            || SetOffWords.Any(w => id.Contains(w, StringComparison.Ordinal))) return false;
        return !(variety.Id.Contains("/Exiles/", StringComparison.Ordinal) || variety.Id.Contains("Mercenar", StringComparison.Ordinal)
            || id.Contains("Exile", StringComparison.Ordinal) || id.Contains("Mercenary", StringComparison.Ordinal)
            || id.Contains("MasterAlly", StringComparison.Ordinal));
    }

    /// <summary>The switch for uniques that are no boss keeping their unshaped skills.</summary>
    public static bool KeepUnshapedForUniques = true;

    /// <summary>Whether an attack does anything: it hits for damage, summons, flies a projectile, throws a trap, puts a
    /// debuff on the player or leaves a ground.</summary>
    public static bool Acts(MonsterAttack a) => !a.Damage.IsZero || a.Summon is not null || a.Projectile is not null
        || a.Trap is not null || a.Debuffs.Count > 0 || a.Ground is not null;

    /// <summary>A unique's skill as it plays, by what the skill is (its ActiveSkillTypes) and what its client action
    /// class does (<see cref="ActionClasses"/>), in this order:
    /// <list type="number">
    /// <item>a trap (Trapped) is thrown at the aim and, set off, plays the rest of this list from where it lies
    /// (MonsterTraps);</item>
    /// <item>a summon (a totem whose body has its skill, a minion the server can name) is the caller's and is kept as it
    /// is; a totem or minion skill without one is left out;</item>
    /// <item>a projectile skill (ProjectilesFromUser, or a ranged skill with no area) flies the projectile its class
    /// creates, else the one the client's art resolver picks (SkillCatalog.SkillArtProjectile), else the one its action
    /// creates for the player's skill on the same ActiveSkills row, as an object of the server's
    /// (Game.MonsterProjectileUse), and leaves its class's ground where it ends;</item>
    /// <item>an area skill (Area, AreaSpell, Rain: Rain of Arrows' arrows fall on the aim) lands within its radius
    /// (<see cref="AreaRadius"/>) of the aim (of its user for a Nova, of the trap for a trap's) and leaves its class's
    /// ground there; one with no radius of its own is as wide as that ground (Desecrate); Storm Call sets a marker that
    /// strikes later (Game.StormCallStrikeOf, MonsterMarkers);</item>
    /// <item>a chaining spell (Chains: Arc) hits its target at its contact;</item>
    /// <item>a melee skill swings as it is.</item>
    /// </list>
    /// Each puts its class's debuffs on the player it hits. Null for a skill the server cannot play: a ranged skill whose
    /// projectile cannot be named (its hit would land with nothing in flight), an area skill without a radius, a trap
    /// whose set-off skill does nothing.</summary>
    /// <param name="setOff">Shape what a trap of the skill plays once set off, rather than the throw.</param>
    /// <param name="body">Shape it as the body summoned for it uses it (a totem's: <see cref="BodyAttack"/>), not as a
    /// summon.</param>
    public static MonsterAttack? Shaped(Player player, MonsterCatalog.Variety variety, byte level, int effect, MonsterAttack attack,
        bool setOff = false, bool body = false)
    {
        var catalog = SkillsFor(player);
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        var names = stats.Where(p => p.Value != 0).Select(p => Stats.Name(p.Key)).OfType<string>().ToList();
        var kind = ActionClasses.OfEffect(effect);
        if (catalog.HasType(effect, "Trapped") && !setOff)
        {
            // A trap is thrown at the aim; what it plays when set off is the skill as any other use of it plays.
            if (Shaped(player, variety, level, effect, attack, setOff: true, body) is not { } payload || !Acts(payload)) return null;
            return attack with { Trap = payload, Projectile = null, Release = null, ReleaseEffect = null, AreaRadius = 0, Ground = null };
        }
        attack = attack with { Debuffs = SkillDebuffs(catalog, effect, kind, Get, names) };
        if (body) attack = attack with { Summon = null };
        else if (attack.Summon is not null) return attack;
        else if (Get(Stat.IsTotem) != 0 || Get(Stat.BaseSkillIsTotemified) != 0
            || (catalog.HasType(effect, "SummonsTotem") && Get(Stat.BaseTotemDuration) > 0)
            || catalog.HasType(effect, "Minion")) return null;
        bool spell = catalog.IsSpell(effect);
        // A skill that is melee too (Frenzy, Puncture, Elemental Hit) swings when its user's attack does.
        bool melee = catalog.IsMelee(effect) && !attack.Ranged;
        bool fromUser = catalog.HasType(effect, "ProjectilesFromUser") && !melee;
        bool area = !melee && (catalog.HasType(effect, "Area") || catalog.HasType(effect, "AreaSpell") || catalog.HasType(effect, "Rain"));
        // An area of its own (a radius, or its class's ground where it lands) is the skill's hit; one without flies its
        // projectile when its art names one (Molten Shell's).
        double? radius = area ? AreaRadius(catalog, effect, level) : null;
        bool ownArea = radius > 0 || kind?.Ground?.At == (setOff ? ActionClasses.GroundAt.Trap : ActionClasses.GroundAt.Landing);
        if (attack.Projectile is null && (fromUser || attack.Ranged && !ownArea) && ArtShot(catalog, effect, level) is { } shot)
            attack = attack with { Projectile = shot, ReleaseEffect = effect, Release = MonsterReleaseFor(player, variety, effect, level, shot) };
        if (attack.Projectile is not null)
            return attack with { Ground = SkillGround(kind, ActionClasses.GroundAt.ProjectileEnd, Get, names, null, spell) };
        if (fromUser)
        {
            // Ranged weapon attacks (MonsterSplitArrow, MonsterBurningArrow, MonsterCausticArrowAtAnimationSpeed)
            // whose art table carries no projectile row still fire as instant ranged weapon attacks (matching default attack).
            if (!spell && catalog.IsRangedAttack(effect) && !attack.Damage.IsZero)
                return attack with { Ground = SkillGround(kind, ActionClasses.GroundAt.ProjectileEnd, Get, names, null, spell) };
            return null;
        }
        if (area)
        {
            var ground = SkillGround(kind, setOff ? ActionClasses.GroundAt.Trap : ActionClasses.GroundAt.Landing, Get, names, radius, spell);
            double reach = radius ?? ground?.Radius ?? 0;
            // Damaging area spells without an explicit radius stat (e.g. DelayedBlast, SeaWitchScreech) use a safe
            // 14-cell default radius rather than failing to shape.
            if (reach <= 0 && !attack.Damage.IsZero && catalog.HasType(effect, "AreaSpell")) reach = 14;
            // A trap's skill without a radius hits whoever set it off (MonsterTraps).
            if (reach <= 0) return setOff ? attack with { AreaAtSelf = true } : null;
            bool selfArea = setOff || catalog.HasType(effect, "Nova")
                || SkillCatalog.ActionTypeId(effect) is "RighteousFire" or "InvisibleFire";
            attack = attack with
            {
                AreaRadius = reach,
                AreaAtSelf = selfArea,
                Range = selfArea && !setOff ? reach : attack.Range,
                Ground = ground,
            };
            if (catalog.SameActionEffects(effect).Any(e => Game.IsStormCall(catalog.EffectId(e))))
                return Game.StormCallStrikeOf(catalog, effect, level) is { } strike ? attack with { Strike = strike } : null;
            return attack;
        }
        // A trap's skill with neither a projectile nor an area hits whoever set it off (MonsterTraps: Bear Trap).
        if (setOff) return attack with { AreaAtSelf = true };
        return catalog.HasType(effect, "Chains") || !attack.Ranged || (body && !attack.Damage.IsZero) ? attack : null;
    }

    /// <summary>The ground a skill's class leaves where <paramref name="at"/> says (ActionClasses.GroundSpec), its kind's
    /// buff holding the skill's stats (ActionClasses.BuffStats) for the class's duration stat: as wide as
    /// <paramref name="radius"/> (its area's), else its class's radius stat, else the kind's art as drawn (GroundEffects
    /// __col4), scaled by the skill's area modifiers. Null for a class that leaves none there.</summary>
    static MonsterGroundSpec? SkillGround(ActionClasses.ActionClass? kind, ActionClasses.GroundAt at, Func<uint, int> get,
        IReadOnlyList<string> names, double? radius, bool spell)
    {
        if (kind?.Ground is not { } spec || spec.At != at) return null;
        int variation = spec.VariationStat is { } stat ? get(stat) : 0;
        if (GroundKindOf(spec.Type, variation) is not { } ground) return null;
        double seconds = get(spec.DurationStat) / 1000.0;
        int basis = kind.RadiusStat is { } own && get(own) > 0 ? get(own) : (int)Math.Round(ground.ArtCells);
        double cells = radius ?? (basis > 0 ? SkillArea.Radius(basis, get, spell) : 0);
        if (seconds <= 0 || cells <= 0) return null;
        return new(spec.Type, variation, ActionClasses.BuffStats(ground.BuffRow, get, names), cells, seconds,
            ClientDrawn: at == ActionClasses.GroundAt.Trap);
    }

    // The GrantedEffects rows a gem grants (GemEffects GrantedEffect, GrantedEffect2): the player's skills.
    static HashSet<int> GemGranted = default!;

    /// <summary>The debuffs a skill's class puts on what it hits (ActionClasses.DebuffSpec), each with the stats the
    /// skill gives it (ActionClasses.BuffStats), for its duration stat: the skill's own, else that of the first other
    /// player gem's effect on its ActiveSkills row that names one (SkillCatalog.SameActionEffects: the exiles' Bear
    /// Trap names none, the gem's 3 s). A debuff whose time no effect names is left out.</summary>
    static IReadOnlyList<MonsterDebuffSpec> SkillDebuffs(SkillCatalog catalog, int effect, ActionClasses.ActionClass? kind,
        Func<uint, int> get, IReadOnlyList<string> names)
    {
        if (kind is null || kind.Debuffs.Count == 0) return [];
        List<MonsterDebuffSpec> debuffs = [];
        foreach (var spec in kind.Debuffs)
        {
            ushort row = BuffRows.Find(spec.Buff);
            if (row == 0) continue;
            int ms = get(spec.DurationStat);
            if (ms <= 0 && spec.DurationStat is var key)
                foreach (int other in catalog.SameActionEffects(effect).Skip(1).Where(GemGranted.Contains))
                    if (GolemData.Skill(catalog, other, 1).Stats.GetValueOrDefault(key) is > 0 and var found) { ms = found; break; }
            if (ms > 0) debuffs.Add(new(row, ActionClasses.BuffStats(row, get, names), ms / 1000.0));
        }
        return debuffs;
    }

    /// <summary>What a summoned body (a monster's totem) does with the skill it was summoned for: that skill at the
    /// summoner's level, as a unique plays it (<see cref="Shaped"/>), from as far as base_totem_range reaches when the
    /// skill names one (cells, as the player's totems target: Game.TotemTargetRange). Null when it would do nothing.</summary>
    public static MonsterAttack? BodyAttack(Player player, MonsterCatalog.Variety? body, byte level, int effect, ushort id)
    {
        if (body is null) return null;
        var catalog = SkillsFor(player);
        // The skill's own summon is this body: the body uses the skill itself.
        var attack = AttackFor(player, body, level, effect, id) with { Effect = effect, Proxy = true };
        if (Shaped(player, body, level, effect, attack, body: true) is not { } shaped) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        if (Stat.BaseTotemRange is var key && stats.GetValueOrDefault(key) is > 0 and var range)
            shaped = shaped with { Range = range };
        return Acts(shaped) ? shaped : null;
    }

    /// <summary>An area skill's radius (cells) from its own stats at the level (SkillArea: its base radius scaled by its
    /// area modifiers); null for a skill that is no area skill or names no radius.</summary>
    public static double? AreaRadius(SkillCatalog catalog, int effect, byte level)
    {
        if (!catalog.HasType(effect, "Area")) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        // The action class may add its own radius to the stat's (ActionClasses: Cold Snap's 15, Rain of Arrows' 24);
        // a skill with neither has no area the server can name.
        int radius = Get(Stat.ActiveSkillBaseAreaOfEffectRadius) + (ActionClasses.OfEffect(effect)?.BaseRadius ?? 0);
        if (radius <= 0) return null;
        return SkillArea.Radius(radius, Get, catalog.IsSpell(effect));
    }

    /// <summary>A skill's projectile as the client resolves its art (SkillCatalog.SkillArtProjectile), else the row its
    /// action class creates for the player's skill on the same ActiveSkills row (SkillCatalog.SharedProjectile), with the count its
    /// stats give one release (number_of_projectiles_override, else base_number_of_projectiles plus
    /// number_of_additional_projectiles); null when the art names none.</summary>
    public static SkillCatalog.MonsterProjectileInfo? ArtShot(SkillCatalog catalog, int effect, byte level)
    {
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if ((ClassProjectile(catalog, effect, Get) ?? catalog.SkillArtProjectile(effect) ?? catalog.SharedProjectile(effect)) is not { } art)
            return null;
        int count = Get(Stat.NumberOfProjectilesOverride) is > 0 and var fixedCount ? fixedCount
            : Math.Max(1, Get(Stat.BaseNumberOfProjectiles)) + Get(Stat.NumberOfAdditionalProjectiles);
        // A nova (projectiles_nova: Lightning Trap's) fires its projectiles around the full turn.
        bool nova = Stat.ProjectilesNova is var novaKey && stats.GetValueOrDefault(novaKey) != 0;
        return new(art.Id, art.Speed, false, art.CollisionRadius) { Count = Math.Max(1, count), SpreadRadians = nova ? Math.Tau : Math.PI / 6 };
    }

    /// <summary>The Projectiles row a skill's action class creates by its own code (ActionClasses.ActionClass.Projectile:
    /// Ensnaring Arrow's TetheringArrow, Siege Ballista's CrossbowSnipeProjectile, Incinerate's Flamethrower1), with its
    /// speed and collision radius; null for a class that names none.</summary>
    static (string Id, int Speed, int CollisionRadius)? ClassProjectile(SkillCatalog catalog, int effect, Func<uint, int> get)
    {
        if (ActionClasses.OfEffect(effect)?.Projectile is not { } pick || catalog.Projectile(pick(get)) is not { } row) return null;
        int radius = ClientTables.TableOrEmpty("Projectiles").FirstOrDefault(p => p.Text("Id") == row.Id).IntOrNull("CollisionRadius") ?? 1;
        return (row.Id, row.Speed, radius);
    }

    /// <summary>The kit as the actor's skill list: 0x4000 + the kit index names the action, as the campaign's own kits do
    /// (DepravedTrinity.Skill).</summary>
    public static IReadOnlyList<GrantedSkill> SkillsOf(Player player, MonsterCatalog.Variety variety, byte level,
        IReadOnlyList<MonsterKitSkill> kit)
    {
        var catalog = SkillsFor(player);
        List<GrantedSkill> skills = [];
        for (int i = 0; i < kit.Count; i++)
        {
            var entry = kit[i];
            int effectLevel = catalog.MaxLevelAt(entry.Effect, level);
            var stats = MonsterActionSkillStats(player, entry.Effect, effectLevel, entry.Attack);
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
    public static MonsterAttack AttackFor(Player player, MonsterCatalog.Variety variety, byte actorLevel, int effect, ushort skillId)
    {
        var catalog = SkillsFor(player);
        var native = GolemData.Skill(catalog, effect, actorLevel);
        int Raw(uint key) => native.Stats.GetValueOrDefault(key);
        bool spell = catalog.IsSpell(effect) || Raw(Stat.CastingSpell) != 0;
        bool isDischarge = SkillCatalog.ActionTypeId(effect) == "Discharge";
        // A ranged skill reaches its variety's MaximumAttackDistance (AttackRange); a spell keeps the aggro range it is
        // given, as the default attack of a ranged spell does (Monsters.Spawn). A melee skill reaches its swing.
        bool ranged = spell && !catalog.IsMelee(effect) || catalog.IsRangedAttack(effect);
        var damage = DamagePacket.Empty;
        // A spell's damage is its spell_*_base_{type}_damage; one with none deals its secondary_*_base_{type}_damage
        // (Bear Trap's), the damage its action deals when it is not cast as a spell hit.
        if (spell || ranged)
            foreach (string prefix in new[] { "spell", "secondary" })
            {
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    string slug = type.ToString().ToLowerInvariant();
                    int min = Raw(Stat.Of($"{prefix}_minimum_base_{slug}_damage")), max = Raw(Stat.Of($"{prefix}_maximum_base_{slug}_damage"));
                    if (max > 0) damage = damage.Add(type, new(min, max));
                }
                if (!damage.IsZero) break;
            }
        if (isDischarge && damage.IsZero)
        {
            // Placeholder per-charge base damage so Acts(attack) succeeds; live Discharge contact replaces this with
            // exact damage scaled by MonsterChargesOf(m) and consumes the charges.
            int lMin = Raw(Stat.SpellMinimumBaseLightningDamagePerRemovablePowerCharge),
                lMax = Raw(Stat.SpellMaximumBaseLightningDamagePerRemovablePowerCharge);
            int fMin = Raw(Stat.SpellMinimumBaseFireDamagePerRemovableEnduranceCharge),
                fMax = Raw(Stat.SpellMaximumBaseFireDamagePerRemovableEnduranceCharge);
            int cMin = Raw(Stat.SpellMinimumBaseColdDamagePerRemovableFrenzyCharge),
                cMax = Raw(Stat.SpellMaximumBaseColdDamagePerRemovableFrenzyCharge);
            if (lMax > 0) damage = damage.Add(DamageType.Lightning, new(lMin, Math.Max(lMin, lMax)));
            else if (fMax > 0) damage = damage.Add(DamageType.Fire, new(fMin, Math.Max(fMin, fMax)));
            else if (cMax > 0) damage = damage.Add(DamageType.Cold, new(cMin, Math.Max(cMin, cMax)));
        }
        if (spell && damage.IsZero)
        {
            int coneMin = Raw(Stat.Of("malachai_elemental_cone_spell_minimum_damage")),
                coneMax = Raw(Stat.Of("malachai_elemental_cone_spell_maximum_damage"));
            if (coneMax > 0)
            {
                double minThird = coneMin / 3d, maxThird = coneMax / 3d;
                damage = damage.Add(DamageType.Fire, new(minThird, maxThird))
                    .Add(DamageType.Cold, new(minThird, maxThird))
                    .Add(DamageType.Lightning, new(minThird, maxThird));
            }
        }
        double seconds = (spell && catalog.CastTimeMs(effect) > 0 ? catalog.CastTimeMs(effect) / 1000d : variety.AttackMs / 1000d)
            / Math.Max(.01, 1 + Raw(spell ? Stat.CastSpeedPlusPct : Stat.AttackSpeedPlusPct) / 100d)
            / Math.Max(.01, 1 + Raw(spell ? Stat.ActiveSkillCastSpeedPlusPctFinal : Stat.ActiveSkillAttackSpeedPlusPctFinal) / 100d);
        if (spell && damage.IsZero)
        {
            // Damage-over-time beam/aura spells (e.g. Tukohama's Vanguard Scorching Ray totem, Wicker Man RF) carry
            // base_fire_damage_to_deal_per_minute rather than hit spell damage: approximate one cast-tick of burning damage.
            int firePerMin = Raw(Stat.BaseFireDamageToDealPerMinute);
            if (firePerMin > 0)
            {
                double tick = Math.Max(1.0, firePerMin / 60.0 * Math.Clamp(seconds, 0.25, 1.5));
                damage = damage.Add(DamageType.Fire, new(tick * 0.9, tick * 1.1));
            }
        }
        // Movement attacks marked Spell+Movement (GoatmanLeapSlam: dash_to_target) carry their damage on a paired
        // geometry row; use the monster's base melee damage so the landing slam deals physical damage.
        bool movementSlam = spell && damage.IsZero && catalog.IsMovement(effect) && !BossMoves.IsTeleport(catalog, effect)
            && catalog.EffectId(effect) is { } eid && (eid.Contains("LeapSlam", StringComparison.Ordinal) || eid.Contains("Slam", StringComparison.Ordinal));
        if ((!spell || movementSlam) && damage.IsZero)
        {
            var (min, max) = Monsters.BaseDamage(variety, actorLevel) ?? (1, 1);
            int more = Raw(Stat.ActiveSkillDamagePlusPctFinal);
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier * Math.Max(0, 1 + more / 100d));
        }
        // A ranged spell casts from as far as it sees the player, up to the farthest its action reaches (ActionClasses).
        double reach = ranged && spell
            ? Math.Max(variety.AttackRange, Math.Min(variety.AggroRange, ActionClasses.SpellActionDistance(effect, Raw)))
            : ranged && Raw(Stat.SpellMaximumActionDistancePlusPct) is > 0 and var distPct
                ? Math.Min(Math.Max(variety.AttackRange, variety.AggroRange), variety.AttackRange * (1 + distPct / 100d))
                : variety.AttackRange;
        var projectile = ranged ? MavenNucleus.Shot(catalog, effect) : null;
        if (projectile is not null && !spell && reach < 45)
            reach = 45;
        var summon = MonsterSummonOf(catalog, effect, actorLevel) 
            // A boss's own summon the tables name no monster for; a player minion skill's monster version stays as it is.
            ?? (SummonData.ForActiveSkill(effect) is null ? BossSummons.For(catalog, variety, effect) : null);
        int hits = 1 + Math.Max(0, Raw(Stat.BaseSkillNumberOfAdditionalHits))
            + (Raw(Stat.SkillDoubleHitsWhenDualWielding) != 0 && DualWields(variety) ? 1 : 0);
        return new(skillId, summon is { CastSeconds: > 0 } ? summon.CastSeconds : Math.Max(.25, seconds), reach, damage)
        {
            Spell = spell,
            IsDischarge = isDischarge,
            DualWielding = HasOffHandWeapon(variety),
            CorruptedBlood = CorruptedBloodFor(Raw),
            Ranged = ranged,
            Projectile = projectile,
            Summon = summon,
            ReleaseEffect = projectile is null ? null : effect,
            Release = projectile is null ? null : MonsterReleaseFor(player, variety, effect, actorLevel, projectile),
            Hits = catalog.IsMelee(effect) && !catalog.IsChannelled(effect) ? hits : 1,
        };
    }

    /// <summary>The attack this boss acts with now: a ready skill of its kit that reaches the player, else a non-cooldown
    /// chase attack (preferring melee walk-up so an out-of-reach boss closes in instead of re-casting an on-cooldown
    /// ranged special). Null only when it has no attack at all.
    /// <para>A skill with a cooldown the tables give it is the boss's special: ready, it takes the turn, which is what
    /// the cooldown is for. Between specials (and for the skills without one, the default swing included) the skill used
    /// longest ago takes it, so the whole list is played rather than one move repeated; on a tie the default swing does,
    /// which is what a boss does with nothing to prove.</para></summary>
    public static MonsterAttack? Choose(SpawnedMonster monster, double now, double distance, Func<MonsterAttack, bool>? usable = null)
    {
        if (monster.Kit.Count == 0) return monster.Attack;
        // A form that has just taken shape teleports first (OpenWithTeleport), when it has a teleport and the player is
        // in its reach (respecting MinRange so it is not consumed while out of range or point-blank).
        if (States.TryGetValue(monster, out var state) && state.OpenWithTeleport)
        {
            MonsterKitSkill? opener = null;
            for (int i = 0; i < monster.Kit.Count; i++)
                if (monster.Kit[i].IsTeleport) { opener = monster.Kit[i]; break; }
            if (opener is null) state.OpenWithTeleport = false;
            else if (opener.Attack.Range >= distance
                && distance >= (opener.Attack.Use?.MinRange ?? 0)
                && Arakaali.Allowed(monster, opener.Effect)
                && (usable is null || usable(opener.Attack)))
            {
                state.OpenWithTeleport = false;
                return opener.Attack;
            }
        }
        // A ranged default attack comes in a burst (Merveil, the Siren's Ice Spears follow one another): once begun it
        // is used again until the burst is full, then the list takes its turns.
        if (monster.Kit[0] is { CooldownSeconds: 0, IsBarrage: false, Attack: { Ranged: true, Use: null, Summon: null } first }
            && state is { StreakCount: > 0 } && state.StreakCount < DefaultBurst
            && first.Range >= distance && Arakaali.Allowed(monster, monster.Kit[0].Effect) && (usable is null || usable(first)))
            return first;
        // A boss whose other skills all reach across the room (Merveil's Ice Spear: her whole aggro range) would never
        // walk again: its melee swing is only a candidate in reach, and the spell always is. When the swing has waited
        // its turn, the boss walks up for it (the ordinary chase does, with the swing as its attack).
        if (WalkUpFor(monster, now, distance) is { } walk) return walk;
        // Specials take a ready turn, but not every one: with several cooldowns between them (Merveil, the Twisted, has
        // five) they would take every turn and the skills without one would never come. So after a special, a skill
        // without a cooldown goes next when one can, and after one of those a ready special does.
        double lastSpecial = 0, lastPlain = 0;
        foreach (var entry in monster.Kit)
        {
            if (!monster.KitReadyAt.TryGetValue(entry.Effect, out double at)) continue;
            if (entry.CooldownSeconds > 0) lastSpecial = Math.Max(lastSpecial, Math.Min(now, at - entry.CooldownSeconds));
            else if (at <= now) lastPlain = Math.Max(lastPlain, at);
        }
        bool plainFirst = lastSpecial > 0 && lastSpecial >= lastPlain;
        MonsterKitSkill? best = null;
        int bestTier = -1;
        double bestUsed = 0;
        foreach (var entry in monster.Kit)
        {
            if (!Arakaali.Allowed(monster, entry.Effect)) continue;
            // A skill that does not reach the player is no candidate: the boss keeps its attack and walks up.
            if (entry.Attack.Range < distance) continue;
            // A travel is for closing a distance: nearer than its least, the monster swings.
            if (entry.Attack.Use is { } use && distance < use.MinRange) continue;
            // A skill that needs what is not there (a corpse) waits.
            if (usable is not null && !usable(entry.Attack)) continue;
            double used = monster.KitReadyAt.GetValueOrDefault(entry.Effect);
            bool special = entry.CooldownSeconds > 0;
            if (special && used > now) continue;
            // A trigger on damage taken is played only by the damage: once enough has been taken, before anything else.
            int tier = entry.TriggerThreshold > 0 ? 2 : special ? 1 : 0;
            if (tier == 2 && monster.KitDamageTaken.GetValueOrDefault(entry.Effect) < entry.TriggerThreshold) continue;
            bool better;
            if (best is null) better = true;
            else if (tier != bestTier) better = plainFirst && tier < 2 && bestTier < 2 ? tier < bestTier : tier > bestTier;
            else if (used != bestUsed) better = used < bestUsed;
            else better = entry.Effect == monster.Kit[0].Effect;
            if (!better) continue;
            best = entry;
            bestTier = tier;
            bestUsed = used;
        }
        return best?.Attack ?? Fallback(monster, usable);
    }

    // Per-monster kit state: walk-up/fight timers (NaN when unset), default-attack burst count, and pending teleport opener.
    sealed class KitState
    {
        public double WalkingSince = double.NaN;
        public double FightSince = double.NaN;
        public int StreakCount;
        public double StreakAt = double.NegativeInfinity;
        public bool OpenWithTeleport;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, KitState> States = new();

    /// <summary>Makes the monster's first kit action its teleport, if its kit has one (a boss's later form).</summary>
    public static void OpenWithTeleport(SpawnedMonster monster) => States.GetOrCreateValue(monster).OpenWithTeleport = true;

    /// <summary>How many times in a row a boss uses a ranged default attack once it begins (ours: the official AI's
    /// weights are not in the client).</summary>
    public static int DefaultBurst = 3;

    /// <summary>How long a melee swing waits before the boss walks up for it, how long it tries, and the farthest player
    /// it walks to (seconds, seconds, cells; ours).</summary>
    public static double MeleeTurnSeconds = 10, WalkUpSeconds = 5, WalkUpFarthest = 60;

    static MonsterAttack? WalkUpFor(SpawnedMonster monster, double now, double distance)
    {
        // Only bosses/uniques periodically walk into melee between ranged casts; ordinary casters stay at spell range.
        if (monster.Variety is { } v && monster.Rarity != MonsterRarity.Unique && !v.BossHealthBar && !IsCampaignBoss(v))
            return null;
        // Its melee swing: a damaging attack with no cooldown that is neither a spell, ranged nor a move.
        MonsterKitSkill? melee = null;
        for (int i = 0; i < monster.Kit.Count; i++)
        {
            var k = monster.Kit[i];
            if (!k.Attack.Ranged && !k.Attack.Spell && k.CooldownSeconds == 0 && !k.Attack.Damage.IsZero
                && k.Attack.Use is null && k.Attack.Summon is null)
            {
                melee = k;
                break;
            }
        }
        if (melee is null) return null;
        if (monster.Variety is { MovementSpeed: 0 } or { Immobile: true }) return null;
        // Only a boss with something else to do from here: one that has nothing but the swing walks up for it anyway.
        bool hasOtherInReach = false;
        for (int i = 0; i < monster.Kit.Count; i++)
        {
            var k = monster.Kit[i];
            if (!ReferenceEquals(k, melee) && k.Attack.Range >= distance && k.Attack.Use is null)
            {
                hasOtherInReach = true;
                break;
            }
        }
        if (!hasOtherInReach) return null;
        var state = States.GetOrCreateValue(monster);
        if (distance <= melee.Attack.Range) { state.WalkingSince = double.NaN; return null; }
        // A swing not used yet has waited since the fight began, not for ever: the boss opens with its spells.
        if (double.IsNaN(state.FightSince)) state.FightSince = now;
        double used = Math.Max(state.FightSince, monster.KitReadyAt.GetValueOrDefault(melee.Effect));
        if (now - used < MeleeTurnSeconds || distance > WalkUpFarthest) { state.WalkingSince = double.NaN; return null; }
        if (double.IsNaN(state.WalkingSince)) state.WalkingSince = now;
        if (now - state.WalkingSince > WalkUpSeconds)
        {
            // It could not get there: the turn is spent, as if it had swung.
            monster.KitReadyAt[melee.Effect] = now;
            state.WalkingSince = double.NaN;
            return null;
        }
        return melee.Attack;
    }

    // When no kit skill is both in reach and off cooldown, never return the last-used special in monster.Attack (which
    // would be an InPlace ranged skill on cooldown and immediately re-fire, bypassing its cooldown). Prefer a
    // zero-cooldown pathing melee swing so the boss walks into melee reach, then any zero-cooldown skill, then any
    // pathing melee skill, then the first allowed entry.
    static MonsterAttack? Fallback(SpawnedMonster monster, Func<MonsterAttack, bool>? usable = null)
    {
        MonsterAttack? anyNoCooldown = null, anyMelee = null, anyUsable = null, anyAllowed = null;
        for (int i = 0; i < monster.Kit.Count; i++)
        {
            var entry = monster.Kit[i];
            if (entry.TriggerThreshold != 0 || !Arakaali.Allowed(monster, entry.Effect)) continue;
            bool ok = usable is null || usable(entry.Attack);
            bool noCooldown = entry.CooldownSeconds <= 0;
            bool melee = !entry.Attack.InPlace;
            if (ok && melee && noCooldown) return entry.Attack;
            if (ok)
            {
                anyNoCooldown ??= noCooldown ? entry.Attack : null;
                anyMelee ??= melee ? entry.Attack : null;
                anyUsable ??= entry.Attack;
            }
            anyAllowed ??= entry.Attack;
        }
        return anyNoCooldown ?? anyMelee ?? anyUsable ?? anyAllowed ?? monster.Kit[0].Attack;
    }

    /// <summary>Records the swing just ordered: the skill it used goes on cooldown, and even one without a cooldown
    /// records when it was used, which is what <see cref="Choose"/> rotates by. Called with the attack the order names,
    /// in the area's frame.</summary>
    public static void Consume(SpawnedMonster monster, MonsterAttack attack, double now)
    {
        if (monster.Kit.Count == 0) return;
        foreach (var entry in monster.Kit)
            if (entry.Attack.SkillId == attack.SkillId)
            {
                // The burst of the default attack (Choose): counted once a swing, ended by any other skill.
                var state = States.GetOrCreateValue(monster);
                if (entry != monster.Kit[0]) state.StreakCount = 0;
                else if (now - state.StreakAt > .2)
                    (state.StreakCount, state.StreakAt) = (state.StreakCount >= DefaultBurst ? 1 : state.StreakCount + 1, now);
                monster.KitReadyAt[entry.Effect] = now + entry.CooldownSeconds;
                if (entry.TriggerThreshold > 0) monster.KitDamageTaken[entry.Effect] = 0;
                return;
            }
    }
}
