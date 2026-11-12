using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    // A monster's combat granted effect (MavenNucleus.CombatEffect: skip teleports/daemons). Weapon-polymorphic Melee
    // still counts as ranged when the variety's art is a bow or wand.
    public static bool MonsterAttackIsRanged(SkillCatalog skills, MonsterCatalog.Variety variety, int effect) =>
        MavenNucleus.AttackIsRanged(skills, effect) || variety.RangedWeapon && skills.IsAttack(effect);

    // A projectile skill's own stats go to the client with the attack's numbers: the client derives none itself
    // (11-skills.md §4), and without them a Sand Spitter's action faces its target like a melee swing instead of turning
    // its back to spit (`action_should_face_away_from_target`, with `projectile_uses_contact_position`, the spread and
    // minimum range from the stat set). The attack's numbers win on a shared key; the effect's cast-time and mana-cost
    // entries, which an attack action does not use, stay out.
    public static readonly string[] MonsterSkillStatsNotSent =
        ["base_spell_cast_time_ms", "virtual_base_spell_cast_time_ms", "hundred_times_casts_per_second", "base_mana_cost", "mana_cost"];

    // A monster record's skill_behaviour_variation: its effect's SkillID, as the client's own skill-stat builder adds it
    // (SkillCatalog.BehaviourVariation), unless the record already says. Projectile records keep their own rule above
    // (MonsterProjectileSkillStats: a mortar's spell forms are sent without it, the server keeping their objects).
    public static IReadOnlyList<(uint, int)> WithBehaviourVariation(ClientState conn, int effect, IReadOnlyList<(uint, int)> stats) =>
        SkillsFor(conn).BehaviourVariation(effect) is int variation and > 0 && stats.All(s => s.Item1 != StatKeys.SkillBehaviourVariation)
            ? [.. stats, (StatKeys.SkillBehaviourVariation, variation)]
            : stats;

    public static IReadOnlyList<(uint, int)> MonsterProjectileSkillStats(ClientState conn, int effect, int level, MonsterAttack attack)
    {
        var own = attack.InPlaceSkillStats();
        // A mortar attack is the client's own: its action fires only with a count on the record and draws the MonsterMortar
        // row skill_behaviour_variation names (StatKeys). GGG's base Spitter records carry row 183 and the three counts at
        // its skill's 1, on lockstep and predictive, and its server creates no projectile object; the Gateway's then sends
        // none (MonsterProjectileProfile). The count is the skill's (SkillCatalog.MonsterProjectile), so the client draws as
        // many as the server lands.
        if (attack.Projectile is { Variation: > 0 } mortar)
            own = [.. own, (StatKeys.SkillBehaviourVariation, mortar.Variation), (StatKeys.TotalProjectilesToFire, mortar.Count),
                (StatKeys.VirtualTotalProjectiles, mortar.Count), (StatKeys.VirtualTotalArrows, mortar.Count)];
        var taken = own.Select(s => s.Item1).ToHashSet();
        foreach (string name in MonsterSkillStatsNotSent) if (Stats.Key(name) is uint key) taken.Add(key);
        return [.. SkillsFor(conn).EffectStats(effect, level).Where(s => !taken.Contains(s.Item1)), .. own];
    }

    /// <summary>The sequence of a monster's actions (0x1ab); the client's clip selector is seeded with it.</summary>
    public const ushort MonsterActionSequence = 0xffff;

    // Predictive only (a lockstep client's chase walk-up swings by its own goal test, StepFrames): how long after its order
    // a melee swing can still turn out not to have begun on the client, and how far inside its reach a monster must be to
    // swing at a walking player (37 cells a second over the order's way to the client). Neither is sourced yet.
    const double SwingOrderSeconds = .15, MovingReachMargin = 2;
    // A melee walk-up farther than this past the reach goes in legs of this many cells (07 §5): the client paths every
    // order out of one budget per frame, which a search toward a player a long way off spends, finding nothing 27–57 %
    // of the time, and one of a dozen cells leaves whole, finding its path 94 % (live 2026-11-03).
    const int LegCells = 12;

    // A projectile skill's release, from what the client itself plays: the clip of the variety's ACT pool for the skill's
    // animation key (MonsterMortar.Animation; the Spitter's SpellProjectile = attack1, attack2), chosen with the native
    // sequence-seeded selector, gives the contact keyframe's time, which the client stretches to the attack time, and its
    // position. The skill's stats say whether the shot leaves from that position (projectile_uses_contact_position),
    // whether the offset follows the model's scale (use_scaled_contact_offset, MonsterVarieties ModelSizeMultiplier) and
    // whether the monster turns its back for it (action_should_face_away_from_target). Without rig data: null, the
    // halfway release and the default point.
    public static MonsterRelease? MonsterReleaseFor(ClientState conn, MonsterCatalog.Variety variety, int effect, int level,
        SkillCatalog.MonsterProjectileInfo projectile, ushort sequence = MonsterActionSequence)
    {
        var catalog = SkillsFor(conn);
        var stats = catalog.EffectStats(effect, level);
        bool Has(string name) => Stats.Key(name) is uint key && stats.Any(s => s.Item1 == key && s.Item2 != 0);
        var clip = catalog.MonsterContact(variety.Id, projectile.AnimationKey ?? "Melee", sequence);
        if (clip is null) return null;
        var point = Has("projectile_uses_contact_position") ? clip.Point : null;
        if (point is { } p && Has("use_scaled_contact_offset")) point = p.Scaled(variety.ModelScale);
        return new(clip.Animation, clip.ContactFraction ?? 0.5, point, Has("action_should_face_away_from_target"));
    }

    // The native selector seeds the clip from this action (0x141e9fa80). Keep its contact and launch point together;
    // another action may start on the actor while this shot is in flight. Scripted releases retain their own recipe.
    public static MonsterAttack MonsterAttackForSequence(ClientState conn, SpawnedMonster monster, MonsterAttack attack, ushort sequence) =>
        attack is { ReleaseEffect: int effect, Projectile: { } projectile } && monster.Variety is { } variety
            ? attack with { Release = MonsterReleaseFor(conn, variety, effect, monster.Level, projectile, sequence) }
            : attack;

    // Creates one monster: registers its type at this level (0x20, once per connection), creates it (0x2bb) and puts it in
    // the wake zone (0x1a9), and keeps it for the AI. /spawn_mob, the area's packs (PopulateArea) and the player's minions
    // (extra stats on its Stats component; owned runs under the lock that adds it) use it. Its object id.
    public enum MonsterPlacement { Proposed, Established }

    // SavedMonster's cell is established world state. The native Positioned reader (141e323f0) copies XY;
    // it does not run a nearest-clear search. This is shared by normal re-entry and population replay.
    public static Task<uint> RestoreMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, MonsterCatalog.Variety variety, SavedMonster saved) =>
        SpawnMonster(remote, stream, ciphers, conn, variety, saved.Level, saved.Rarity, saved.X, saved.Y,
            friendly: !saved.Hostile, log: false, damagedLife: saved.Life, mods: saved.Mods,
            unrisen: saved.Unrisen, placement: MonsterPlacement.Established);

    public static async Task<uint> SpawnMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MonsterCatalog.Variety variety,
        byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true, uint? damagedLife = null,
        MonsterModSet? mods = null, bool alone = true, bool? unrisen = null, float rotation = 0,
        IReadOnlyList<(uint Key, int Value)>? extraStats = null, Action<uint, SpawnedMonster>? owned = null, Func<bool>? ownedCurrent = null,
        IReadOnlyList<GrantedSkill>? ownedSkills = null, uint? ownedMaximumLife = null,
        MonsterModSet? extraMods = null, bool scripted = false, IReadOnlyList<VisibleItem>? ownedLooks = null,
        Func<bool>? ownedAdmission = null, MonsterPlacement placement = MonsterPlacement.Proposed, int? lifePercent = null,
        MonsterMovement? movement = null)
    {
        movement ??= MonsterMovement.Default;
        uint id;
        // Under the lock the projectile visuals and ground art take ids under: minions spawn from their own loop.
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || ownedCurrent is not null && !ownedCurrent()) return 0;
            // The instance's own monsters (not a player's minions or allies; a monster's summons are) take the instance's ids: every client has
            // them under the same id, and whoever runs the instance next keeps them (SharedMonsters.cs).
            id = !friendly ? conn.Instance.World.NewMonsterId() : conn.TakeObjectId();
        }
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        if (placement == MonsterPlacement.Proposed && bodySize >= 0)
        {
            bool Floor(int cx, int cy) => conn.Layout.WalkableFor(cx, cy, bodySize) && !conn.Barrier.Contains((cx, cy));
            if (CollisionEscape.Find((x, y), conn.Layout.GridW, conn.Layout.GridH, Floor,
                conn.MonsterPlacementBlockers(id), moverSize: bodySize) is { } clear) (x, y) = clear;
        }
        // A monster that rises from the ground starts waiting to rise, unless it was kept after rising (MonsterEmerge.cs).
        var emerge = Emerges.Kind(variety, rarity);
        bool waits = emerge != EmergeKind.None && (unrisen ?? true);
        // Its mods (MonsterMods.cs): the kept ones, else rolled for its rarity. They set its life, hit, speed and defences.
        mods = MonsterModsFor(conn, variety, level, rarity, mods, alone);
        // Additional encounter scaling composes with rolled rarity and map effects.
        if (extraMods is not null) mods = mods with { Effects = mods.Effects.Plus(extraMods.Effects) };
        if (!friendly && MapModsOf(conn) is { } mapMods && MapMonsterEffects(mapMods) is { Count: > 0 } mapStats)
            mods = mods with { Effects = mods.Effects.Combine(mapStats) };
        // The party bonus (Game.PartyBonus): each player beyond the first adds to the monsters' life by their rarity.
        if (!friendly && PartyMonsterLife(conn, rarity) is int party and > 0)
            mods = mods with { Effects = mods.Effects.Combine(new Dictionary<string, int> { ["maximum_life_+%"] = party }) };
        // Full life: the client derives the base from the level (MonsterCatalog.BaseLife); the mods' maximum goes on its Stats.
        // A unique map boss also has the map's and the map-boss table's life and damage increases (MonsterMapBossDifficulty
        // and MonsterMapDifficulty): level 83 is 20.73× life, 1.15× damage.
        bool mapBoss = rarity == MonsterRarity.Unique && variety.BossHealthBar && MapDevices.Value.IsMapArea(conn.Layout.Hash);
        var mapBossScaling = mapBoss ? Monsters.MapBossScaling(level) : (Life: 1.0, Damage: 1.0);
        int baseLife = Monsters.BaseLife(variety, level) ?? 100;
        if (mapBoss) baseLife = (int)Math.Round(baseLife * mapBossScaling.Life);
        uint life = (uint)mods.Effects.MaxLife(baseLife);
        if (ownedMaximumLife is uint ownLife) life = ownLife;
        // A part of its full life (Izaro's later Aspirant's Trials start with what the earlier ones left).
        if (lifePercent is int percent and < 100) damagedLife = Math.Max(1u, (uint)((ulong)life * (uint)Math.Max(0, percent) / 100));
        // The type supplies the animated object; Game_MONSTER_AO=1 also sends the variety's first AOFiles path (Animated
        // bit0), which is not needed.
        string? ao = Environment.GetEnvironmentVariable("Game_MONSTER_AO") == "1" ? variety.AnimatedObject : null;
        ushort team = friendly ? (ushort)0 : (ushort)1;
        // Its attack: the variety's first granted effect (Hungry Corpse: Melee) at its level, with the variety's attack time,
        // reach and the level's damage as the skill's final stats (11-skills.md §7).
        // The mods' damage and speed: the rarity's more damage per hit and faster attacks with that much less damage, the
        // affixes' increased damage, attack speed and extra elemental / chaos damage (MonsterModEffects.Hit).
        var (baseMin, baseMax) = Monsters.BaseDamage(variety, level) ?? (1, 1);
        if (mapBoss)
        {
            baseMin = Math.Max(1, (int)Math.Round(baseMin * mapBossScaling.Damage));
            baseMax = Math.Max(1, (int)Math.Round(baseMax * mapBossScaling.Damage));
        }
        var catalog = SkillsFor(conn);
        // The King's actors keep their first granted effect, reach and official ids: KingInMists.cs plays their actions
        // itself. Every other monster takes its combat effect (MavenNucleus.CombatEffect).
        bool kingActor = KingInMists.IsActor(variety);
        bool brine = BrineKing.IsBoss(variety);
        // The Shaper, the Elder and Sirus are scripted fights too (AtlasBosses.cs, ShaperFight.cs, ElderFight.cs,
        // SirusFight.cs): their record's swing is their basic skill, and every other skill of their kit is played by their
        // controller through its own id.
        bool atlas = AtlasBosses.IsBoss(variety);
        int effect = brine ? BrineKing.MeleeEffect : atlas ? AtlasBosses.BasicEffect(variety)
            : !kingActor ? MavenNucleus.CombatEffect(catalog, variety)
            : variety.GrantedEffects is [int firstEffect, ..] ? firstEffect : -1;
        bool rangedAttack = kingActor
            ? effect >= 0 && (catalog.IsRangedAttack(effect) || variety.RangedWeapon && catalog.IsAttack(effect))
            : MonsterAttackIsRanged(catalog, variety, effect);
        // A mortar skill's projectile (Sand Spitter): the shot flies and lands its hit; other ranged skills keep the
        // instant contact until their projectile is known (SkillCatalog.MonsterProjectile). Its release time and point
        // come from the clip the client plays for it (MonsterReleaseFor). MavenProjectile and the Nucleus barrages are
        // monster_projectile_spell rows (not mortars): MavenNucleus.Shot names their Projectiles.dat art.
        var projectile = rangedAttack ? MavenNucleus.Shot(catalog, effect) : null;
        // A summoning skill (the Immolator's) brings its monsters whenever the player is within its aggro range, at its
        // own cast time; it deals no hit.
        var summon = variety.GrantedEffects is [int summonEffect, ..] ? catalog.MonsterSummon(summonEffect, level) : null;
        // A monster whose effect is a spell (the King's Death Effigy, fetish hazards) needs cast-time stats,
        // not the melee attack duration; the client crashed in its cast-duration path when the actor record carried only
        // attack stats (0xc0000005, live 2026-10-31).
        bool spell = effect >= 0 && catalog.EffectStats(effect, level)
            .Any(s => s.Item1 == StatKeys.CastingSpell && s.Item2 != 0);
        // A ranged attack reaches its variety's MaximumAttackDistance (a Sand Spitter's 30): the monster walks up to that
        // before it shoots, as GGG's do. A ranged spell (the Maven's and the Nucleus' barrages) and a summoner keep the
        // aggro range they were given with the map affixes; the attack distance of such a variety is its melee fallback.
        // An encounter that knows better gives its own share of the attack distance (MonsterMovement.Reach).
        double reach = movement.Reach is double share ? variety.AttackRange * share
            : summon is not null || rangedAttack && !kingActor && spell ? Math.Max(variety.AttackRange, variety.AggroRange) : variety.AttackRange;
        double swing = !kingActor && effect >= 0 && catalog.IsSpell(effect) && catalog.CastTimeMs(effect) > 0
            ? catalog.CastTimeMs(effect) / 1000.0 : variety.AttackMs / 1000.0;
        // The King's two forms carry the official MeleeAtAnimationSpeedUnique ids (0x4001 humanoid, 0x4000 treant);
        // 0x0102 is the player/generic default attack and has no matching clip on their rigs.
        ushort skillId = KingInMists.IsBoss2(variety) ? KingInMists.Boss2AttackSkill
            : KingInMists.IsBoss3(variety) ? KingInMists.Boss3AttackSkill
            : summon is null && !kingActor && effect >= 0 && catalog.ActionHash(effect) is ushort hash ? (ushort)(hash & 0x3fff)
            : SkillCatalog.FirstSkillId;
        var attack = new MonsterAttack(skillId, summon is { CastSeconds: > 0 } ? summon.CastSeconds : mods.Effects.AttackSeconds(swing), reach,
            mods.Effects.Hit(DamagePacket.Of(DamageType.Physical, baseMin, baseMax)))
        {
            Spell = spell,
            Scripted = kingActor || brine || atlas,
            Ranged = rangedAttack,
            Projectile = projectile,
            Summon = summon,
            ReleaseEffect = projectile is null || effect < 0 ? null : effect,
            Release = projectile is null || effect < 0 ? null : MonsterReleaseFor(conn, variety, effect, level, projectile),
        };
        var skill = effect >= 0
            ? catalog.FindByRow(attack.SkillId, effect, level,
                attack.Projectile is null ? WithBehaviourVariation(conn, effect, attack.SkillStats()) : MonsterProjectileSkillStats(conn, effect, level, attack)) : null;
        // A boss with stages also has the skill that changes them (Hillock's sword extract, BossFights). A scripted
        // encounter drives its own phases: the built-in stage would heal it and fight the controller for its actions.
        var stage = scripted || atlas ? null : BossFights.For(variety.Id);
        // Its stats as the attack's (skill_is_non_pathing above all, and the stage's own time): without them the client sent
        // the action through its pathing step, played nothing and kept the monster in it (live 2026-10-21).
        var stageSkill = skill is not null && stage is not null
            ? SkillsFor(conn).FindByRow(StageSkillId, stage.SkillEffect, level, WithBehaviourVariation(conn, stage.SkillEffect,
                (attack with { SkillId = StageSkillId, Seconds = stage.CastSeconds }).InPlaceSkillStats()))
            : null;
        // The King in the Mists needs its whole granted-effect list on the actor, not only the default attack: each 0x1ab
        // names one of these ids (KingInMists.cs). The three Mystic Fetishes likewise need their hazard list, because the
        // official server sends those actions to the fetish actor.
        var kingSkills = Sirus.SkillsFor(conn, variety, level) ?? KitavaActFive.SkillsFor(conn, variety, level) ?? KitavaActTen.SkillsFor(conn, variety, level) ?? DepravedTrinity.SkillsFor(conn, variety, level) ?? VaalOversoul.SkillsFor(conn, variety, level) ?? DoedreSewers.SkillsFor(conn, variety, level) ?? BrineKing.SkillsFor(conn, variety, level, attack) ?? KingInMists.SkillsFor(conn, variety, level, attack)
            ?? AtlasBosses.SkillsFor(conn, variety, level, attack)
            ?? (KingInMists.IsFetish(variety) ? KingInMists.FetishSkillsFor(conn, variety, level, attack) : null);
        // A monster the campaign places as a boss, or one with a bar of its own, and that has no encounter controller:
        // it plays the rest of its granted effects through the ordinary AI (BossKits). Every other campaign boss walked
        // up and swung one default attack forever. A scripted encounter keeps its own list and its own actions.
        var kit = scripted || kingSkills is not null ? (IReadOnlyList<MonsterKitSkill>)[] : BossKits.Kit(conn, variety, level, effect, attack);
        // The kit's first entry is the effect the default attack uses, given its own id in the kit: the actor needs that
        // list, not a second copy of the default attack under the hash-derived id. The stage skill stays: it is 0x0103.
        var skills = kingSkills is { Count: > 0 } ? kingSkills
            : kit.Count > 0 ? [.. BossKits.SkillsOf(conn, variety, level, kit), .. stageSkill is null ? [] : new List<GrantedSkill> { stageSkill }]
            : skill is null ? null : stageSkill is null ? [skill] : [skill, stageSkill];
        if (ownedSkills is not null) skills = ownedSkills;
        List<(uint, int)>? stats = skill is null ? null : [.. attack.Stats()];
        // The final maximum too: the client applies no modifier of its own (as for the player's maximum_life), so without it
        // the life bars kept the base maximum and a unique showed full until its life fell below it (live 2026-10-21).
        foreach (var stat in MonsterModClientStats(mods.Effects, life, baseLife).Concat(extraStats ?? [])) (stats ??= []).Add(stat);
        if (DoedreSewers.IsBoss(variety) || Sirus.IsActor(variety)) (stats ??= []).Add((StatKeys.HideMiniLifeBar, 1));
        var states = Sirus.StartStates(variety) ?? DoedreSewers.StartStates(variety) ?? BrineKing.StartStates(variety) ?? MavenNucleus.StartStates(variety) ?? variety.StateMachine;
        var monster = new MonsterObject(x, y, life, variety.ComponentsOrBase, ao, rarity, name, states, MonsterEmerge.CreateState(variety, emerge, waits), team,
            skills, stats)
        {
            Mods = mods.Mods, NameWords = mods.NameWords, LifeData = variety.LifeData, Rotation = rotation, Equipped = ownedLooks,
        };
        // A monster type needs a level: 0x20 registers "<Id>@<level>" (once per connection), the 0x2bb type is its hash.
        if (QuestKillCommitOutput is { } output)
            output.Add(new([], "quest phase monster type", Kind: PlayerMinions.OutputKind.MonsterType,
                MonsterType: ((ushort)variety.Row, level)));
        else await Send(remote, stream, ciphers, [], $"0x0020 monster type {variety.Id}@{level} (row {variety.Row})", atGate: () =>
        {
            lock (conn.Monsters)
                return CurrentGameWork && !conn.Closed && !conn.LeavingArea
                    && (ownedCurrent is null || ownedCurrent()) && conn.MonsterTypes.Add((variety.Row, level))
                    ? InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]) : null;
        });
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        // The client multiplies this monster's pathing speed by its own roll in the variety's range, drawn from [id, type]
        // (MonsterSpeedRoll.cs); the walk estimate uses the same speed so arrival, swing range and contact agree with it.
        int speedRoll = MonsterSpeedRoll.Percent(id, type, variety);
        double walkSpeed = mods.Effects.MovementSpeed(variety.MovementSpeed) * MonsterSpeedRoll.Factor(speedRoll);
        // An owned spawn must not send a native object and then lose its roster callback across reload.
        // Commit the neutral identity with its create/wake bytes at one current send gate.
        bool admitted = false;
        byte[] create = InstanceEntities.CreateObject(id, type, monster.Blob());
        string? createLog = !log ? null :
            $"0x02bb create {rarity}{(friendly ? " friendly" : "")}{(waits ? $" {emerge.ToString().ToLowerInvariant()}" : "")} monster 0x{id:x} {variety}{(name is null ? "" : $" \"{name}\"")} type=0x{type:x8} at ({x}, {y}), life {life}, speed {walkSpeed:0.##}{(speedRoll == 0 ? "" : $" (roll {speedRoll:+0;-0}%)")}, components {string.Join(", ", variety.ComponentsOrBase)}";
        if (owned is not null)
        {
            await SendWorldCreate(conn, id, () =>
            {
                using (ProjectileMutation(conn))
                {
                    if (!CurrentGameWork || conn.Closed || conn.LeavingArea || ownedCurrent is not null && !ownedCurrent()) return null;
                    if (ownedAdmission is not null && !ownedAdmission()) return null;
                    AddMonsterLocked(); admitted = true;
                    return create;
                }
            }, createLog, wake: true, transport: (remote, stream, ciphers));
            if (!admitted) return 0;
        }
        else
        {
            if (!await SendWorldCreate(conn, id, () => create, createLog, transport: (remote, stream, ciphers))) return 0;
            // A boss far from the player (Dominus at the top of the Sceptre, created at the area entry) stays out of the
            // wake zone until the player comes near: KeepWakeZone wakes it there. Woken at creation, the client put its far
            // copy to sleep on its own while the server still had it awake, and the boss life bar (0x2bc boss_life_bar) for
            // it crashed the client at 0x1401297ca (an Actor lookup on an object without components, live 2026-11-05).
            bool farBoss = variety.BossHealthBar && Dist((x, y), conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds)) > WakeZoneRadius;
            if (farBoss) lock (conn.Monsters) conn.AsleepMonsters.Add(id);
            else await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), !log ? null : $"0x01a9 monster 0x{id:x} enters wake zone");
            if (farBoss && log) Console.WriteLine($"[gateway] {remote} boss 0x{id:x} {variety} created asleep: far from the player");
        }
        // A magic or rare monster's glow is a buff of its own (MonsterRarityBuffs.cs), cancelled by the client at death.
        if (MonsterRarityBuffs.Row(rarity) is ushort glow)
        {
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.AddBuff(id, buff, glow), !log ? null :
                $"0x01c1 monster 0x{id:x} buff {MonsterRarityBuffs.Id(glow)} (BuffDefinitions row {glow})");
        }
        if (log && mods.Affixes.Any()) Console.WriteLine($"[gateway] {remote} monster 0x{id:x} mods: {MonsterModRolls.Value.Describe(mods)}" +
            (mods.NameWords.Count > 0 ? $", named {MonsterModRolls.Value.NameOf(mods.NameWords)}" : ""));
        if (log && attack.Projectile is { } shot)
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ranged attack projectile {shot.Metadata} ({shot.Speed} units/s{(shot.Lobbed ? ", mortar" : "")})");
        if (owned is null)
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return 0;
                AddMonsterLocked();
            }
        void AddMonsterLocked()
        {
            MonsterEmerge.AddLocked(conn, id, new SpawnedMonster($"{variety.Name}@{level}", conn.NewMonsterWalk(id, x, y, walkSpeed, bodySize, rarity == MonsterRarity.Unique),
                variety.AggroRange, Math.Max(2, variety.ObjectSize + 1), life)
            {
                Attack = skill is null && kit.Count == 0 ? null : kit.Count > 0 ? kit[0].Attack : attack,
                Kit = kit, Hostile = !friendly, Variety = variety, Level = level, Rarity = rarity,
                Scripted = !friendly && (scripted || atlas),
                Life = Math.Min(damagedLife ?? life, life), Stage = stageSkill is null ? null : stage, States = states,
                HitAnimations = Monsters.TakeHit(variety.Id), Mods = mods, SpeedRoll = speedRoll,
                Threshold = QuestsOf(conn).ThresholdOf(conn.Layout.Id, variety.Id),
                CannotBeStunned = !friendly && (KingInMists.IsBoss(variety) || scripted || atlas),
                KingPhaseLocked = !friendly && KingInMists.IsBoss2(variety),
                KingGrace = !friendly && KingInMists.IsBoss(variety),
                FetishGrace = !friendly && KingInMists.IsFetish(variety),
                // The three thresholds the Atlas bosses change phase at (a quarter of their life each, three times).
                LifePhases = !friendly && (brine || atlas) ? new([75, 50, 25]) : !friendly && DepravedTrinity.IsBoss(variety) ? new([66, 33]) : !friendly ? (Sirus.Phases(variety) ?? KitavaActFive.Phases(variety) ?? KitavaActTen.Phases(variety)) : null,
                EntranceGrace = !friendly && (Sirus.IsActor(variety) || KitavaActFive.IsActor(variety) || KitavaActTen.IsActor(variety) || DepravedTrinity.IsBoss(variety) || VaalOversoul.IsBoss(variety) || DoedreSewers.IsBoss(variety)),
            }, emerge, waits);
            if (owned is not null && conn.Monsters.TryGetValue(id, out var added)) owned(id, added);
            if (friendly) conn.MinionOrigins[id] = new(type, monster, (ushort)variety.Row, level, rarity);
            else
            {
                conn.Instance.World.MonsterOrigins[id] = new(type, monster, (ushort)variety.Row, level, rarity);
                // Created on this client where it was spawned (SyncInstanceMonsters creates it on the others).
                conn.MonsterViews[id] = new((x, y), (x, y), Math.Min(damagedLife ?? life, life));
            }
        }
        if (waits) WatchBuried(remote, stream, ciphers, conn);
        // A kept monster that was hit: its life as a hit sets it (0x1b1).
        if (damagedLife < life)
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, damagedLife.Value), !log ? null : $"0x01b1 monster 0x{id:x} life {damagedLife}/{life}");
        if (!friendly && KingInMists.IsBoss(variety)) KingInMists.Begin(remote, stream, ciphers, conn, id);
        return id;
    }

    // The area's monsters, once the player is in (Agents/docs/protocol/14-monster-packs.md): MonsterPacks scattered over the
    // walk map, the room points' packs and the unique boss at "mapboss" (AreaPopulation). Towns have none; neither does an
    // area without a walk map. Game_MONSTERS=0 turns it off.
    public static async Task PopulateArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        // Another player already runs this instance's monsters: this client shows theirs (SharedMonsters.cs).
        if (!ClaimMonsterHost(remote, conn)) return;
        var layout = conn.Layout;
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        // The Crux of Nothingness fight places its three Mystic Fetishes at fixed offsets from the room's mapboss point
        // (KingInMists.cs). Keep that point on the connection so the treant form uses the same origin as the humanoid,
        // and so a kept instance recovers it before its saved monsters are restored.
        conn.KingOrigin = null;
        if (layout.Id == "AzmeriLeagueBoss"
            && AreaPopulation.FitRooms(layout.Id, conn.Seed, [], "data").Points.FirstOrDefault(p => p.Name == "mapboss") is { } mapboss)
            conn.KingOrigin = (mapboss.X, mapboss.Y);
        // A capture tour only waits for the recorder: no monsters to attack the character meanwhile.
        if (CaptureTour.Active(CharacterName(conn))) return;
        // An instance entered before keeps its monsters as the last connection left them: where they were, with their
        // damage; the dead stay dead (#12).
        if (conn.Instance.Monsters is { } saved)
        {
            conn.MonstersPlaced = true;
            foreach (var s in saved)
                if (Monsters.Find(s.Variety.ToString()) is { } kept)
                    await RestoreMonster(remote, stream, ciphers, conn, kept, s);
            Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {saved.Count} kept from the instance");
            return;
        }
        if (layout.Id == Sirus.Area) { conn.MonstersPlaced = true; await Sirus.Populate(conn); return; }
        // The packs need the walk map; until it exists nothing is placed, so nothing is kept either.
        if (layout.Walkable is not { } walkable) return;
        conn.MonstersPlaced = true;
        if (Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Bosses.Count == 0)) return;
        // Crux of Nothingness is the boss arena, not the maze: the area's `voodoo-king-maze1` pack belongs to the
        // separate maze layout the fight does not build yet.
        if (layout.Id == "AzmeriLeagueBoss") area = area with { Scattered = [] };
        if (Monsters.Error is { } error) { Console.WriteLine($"[gateway] {remote} area monsters: {error}"); return; }
        // Emerge monsters (Gravel Eater, Tide Strider …) are placed waiting and rise near the player (MonsterEmerge.cs).
        var quests = QuestsOf(conn);
        // A boss a quest brings (an NPC that turns into it, a second form, an object's use: Quests.QuestSpawned) is not
        // placed with the area.
        bool Spawnable(int row) => Monsters.Find(row.ToString()) is { Supported: true } v && Emerges.CanPlace(v) && !quests.QuestSpawned(layout.Id, v.Id);
        // A boss's later form (Abberath2, Shakari2, Malachai's second form, The Depraved Trinity) comes with its fight.
        area = AreaPopulation.LaterForms(area, row => Monsters.Find(row.ToString()) is { } v ? (v.Id, v.Name) : null);
        // Device-recipe phases hold the last WorldAreas boss (Maven's Nucleus, Sirus, Atziri) until the first wave is dead.
        area = MapBossFights.Current.HoldFinal(area, row => Monsters.Find(row.ToString())?.Id);
        // The Ashen Fields' boss depends on the character's Act 2 bandit choice (MapBossOverride).
        area = MapBossOverride.Apply(area, Quests.Names(PlayerQuestFlags(conn)), row => Monsters.Find(row.ToString())?.Id);
        var notes = new List<string>();
        var placedRooms = new List<AreaPopulation.PlacedRoom>();
        var (points, objects, _) = AreaPopulation.FitRooms(layout.Id, conn.Seed, notes, "data", placedRooms: placedRooms);
        // A boss whose room has a spawner object for it (The Quay's Tolman) stands at the spawner.
        points.AddRange(AreaPopulation.SpawnerPoints(area, objects, row => Monsters.Find(row.ToString())?.Id));
        // A Trial of Ascendancy's rooms (live 2026-10-31, the original): no monsters among the traps, so the area's
        // scattered packs keep away from them (LabyrinthTrials.TrapClearance); what the room file places stands there:
        // its normal / magic / rare pack points (the Lower Prison trial's plaque platform), its monsters (the Imperial
        // Gardens trial's four Devourers) and its spawners' monsters (LabyrinthTrials.Spawners), rare.
        var trialRooms = placedRooms.Where(r => r.Objects.Any(o => o.Type is LabyrinthTrials.Plaque or LabyrinthTrials.IntroDoor)).ToList();
        var trapClear = new HashSet<(int X, int Y)>();
        // The Labyrinth's own rooms keep their packs off their traps too: the generated rooms' and tiles' trap lines.
        var labyrinthTraps = LabyrinthTrials.InLabyrinth(layout.Id) && ServerLevels.For(layout.Id, conn.Seed) is { } labyrinthLevel
            ? labyrinthLevel.RoomObjects.Concat(labyrinthLevel.TileLabyrinthObjects).Select(o => (o.Type, o.X, o.Y))
            : [];
        foreach (var trap in trialRooms.SelectMany(r => r.Objects).Select(o => (o.Type, o.X, o.Y)).Concat(labyrinthTraps)
                     .Where(o => o.Type.StartsWith("Metadata/Terrain/Labyrinth/Traps/", StringComparison.Ordinal)))
            for (int dy = -LabyrinthTrials.TrapClearance; dy <= LabyrinthTrials.TrapClearance; dy++)
                for (int dx = -LabyrinthTrials.TrapClearance; dx <= LabyrinthTrials.TrapClearance; dx++)
                    trapClear.Add((trap.X + dx, trap.Y + dy));
        // Where the later forms the area leaves out would stand, for a quest that forms one (Quests.FormsRule).
        var laterForms = AreaPopulation.LaterFormPoints(area, points, walkable);
        lock (conn.Monsters)
        {
            conn.LaterFormPoints.Clear();
            foreach (var (row, cell) in laterForms)
                if (Monsters.Find(row.ToString()) is { } form) conn.LaterFormPoints[form.Id] = cell;
            // Recipe arenas have no captured mapboss point: the held last boss forms next to arrival.
            if (MapBossFights.Current.PhaseOf(layout.Id) is { } phase && !conn.LaterFormPoints.ContainsKey(phase.Last))
                conn.LaterFormPoints[phase.Last] = (layout.SpawnX + 8, layout.SpawnY);
        }
        var mapMods = MapDevice.Current(CharacterName(conn), layout.Hash)?.Mods;
        var scale = mapMods?.Packs ?? default;
        var spawns = WithTwinBosses(AreaPopulation.Plan(area, layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
            [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], points,
            AreaPopulation.PopulationSeed(layout.Hash, conn.Seed), Spawnable, notes, scale,
            unassignedAtArrival: MapDevices.Value.IsMapArea(layout.Hash), roomPacks: [.. trialRooms.SelectMany(r => r.Points)],
            scatterable: trapClear.Count == 0 ? null : (x, y) => !trapClear.Contains((x, y))), mapMods).ToList();
        foreach (var chest in QuestsOf(conn).Chests(layout.Id).Where(r => r.Guards is not null))
            foreach (var room in placedRooms.Where(r => !trialRooms.Contains(r) && r.Objects.Any(o => o.Type == chest.ChestType)))
                foreach (var point in room.Points.Where(p => chest.Guards!.Contains(p.Name)))
                    if (Monsters.Find(point.Name) is { } guard && guard.Id == point.Name && Spawnable(guard.Row))
                        spawns.Add(new AreaPopulation.Spawn(guard.Row, point.X, point.Y, MonsterRarity.Normal, "quest chest guard"));
        foreach (var room in trialRooms)
            foreach (var p in room.Points)
            {
                bool spawner = LabyrinthTrials.Spawners.TryGetValue(p.Name, out var spawned);
                string? id = spawner ? spawned : p.Name.StartsWith("Metadata/Monsters/", StringComparison.Ordinal) ? p.Name : null;
                if (id is null) continue;
                if (Monsters.Find(id) is { } v && v.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && Spawnable(v.Row))
                    spawns.Add(new AreaPopulation.Spawn(v.Row, p.X, p.Y, spawner ? MonsterRarity.Rare : MonsterRarity.Normal, "trial room"));
                else notes.Add($"trial room {room.Path.Split('/')[^1]}: {p.Name} at ({p.X}, {p.Y}) cannot be created");
            }
        foreach (var n in notes) Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {n}");
        int areaLevel = area.AreaLevel;
        if (MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == layout.Hash && openMap.Area.Level > 0)
            areaLevel = openMap.Area.Level;
        byte level = (byte)Math.Clamp(areaLevel, 1, 100);
        foreach (var s in spawns)
        {
            var variety = Monsters.Find(s.Variety.ToString())!;
            bool notable = s.Rarity is MonsterRarity.Unique or MonsterRarity.Rare;
            await SpawnMonster(remote, stream, ciphers, conn, variety, level, s.Rarity, s.X, s.Y, log: notable, alone: false);
        }
        int extra = 0;
        if (MapDevice.Current(CharacterName(conn), layout.Hash) is { ItemRow: > 0 } opening)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            lock (conn.Monsters)
                foreach (var m in conn.Monsters.Values)
                    if (m.Variety is { } v) seen.Add(v.Id);
            foreach (string id in MapBossFights.Current.Witnesses(opening.ItemRow))
            {
                if (!seen.Add(id) || Monsters.Find(id) is not { Supported: true } witness || witness.Id != id) continue;
                extra++;
                await SpawnMonster(remote, stream, ciphers, conn, witness, level, MonsterRarity.Unique,
                    layout.SpawnX + extra * 4, layout.SpawnY, log: true, alone: false);
            }
        }
        if (layout.Id == MavenNucleus.Area)
        {
            var maven = spawns.Select(s => (V: Monsters.Find(s.Variety.ToString())!, s.X, s.Y))
                .FirstOrDefault(s => s.V.Id == MavenNucleus.Maven);
            if (maven.V.Id == MavenNucleus.Maven)
                await SpawnMavenRing(remote, stream, ciphers, conn, MavenNucleus.Phase1Orbs,
                    (maven.X, maven.Y), MavenNucleus.Phase1Radius, level);
        }
        Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: created {spawns.Count + extra} at level {level}: " +
            string.Join(", ", spawns.GroupBy(s => s.Pack).Select(g => $"{g.Key} × {g.Count()}")) +
            (extra > 0 ? $", invitation × {extra}" : "") +
            $"; waiting to rise: {MonsterEmerge.Unrisen(conn).Count} ({MonsterEmerge.Buried(conn).Count} buried)" +
            $"; unique: {string.Join(", ", spawns.Where(s => s.Rarity == MonsterRarity.Unique).Select(s => $"{Monsters.Find(s.Variety.ToString())!.Name} at ({s.X}, {s.Y})").DefaultIfEmpty("none"))}");
    }

    public static async Task<int> SpawnMavenRing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, IReadOnlyList<string> ids, (int X, int Y) centre, int radius, byte level)
    {
        int n = 0;
        foreach (var (id, x, y) in MavenNucleus.Around(ids, centre, radius))
        {
            if (Monsters.Find(id) is not { Supported: true } extra || extra.Id != id) continue;
            n++;
            await SpawnMonster(remote, stream, ciphers, conn, extra, level, MonsterRarity.Unique, x, y, log: true, alone: false);
        }
        if (n > 0)
            Console.WriteLine($"[gateway] {remote} Maven fight extras: {n} around ({centre.X}, {centre.Y})");
        return n;
    }

    public sealed class MonsterAiPlan
    {
        // Chases ended this pass: each gets S→C 0x1ac before its give-up Move, as GGG's server sends it (925 of 1166
        // live give-ups). Only a predictive client reads it; a lockstep one ignores 0x1ac (07 §11).
        public readonly List<uint> EndedActions = [];
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, (int X, int Y) From, (int X, int Y) To, string Why)> Moves = [];
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, (int X, int Y) From, (int X, int Y) To)> Chases = [];
        // A far melee chase's leg: a Move to the cell LegCells along the walk-up. Placed by the plan on a predictive client
        // (To is the leg's cell); placed at the gate on a lockstep one, from the player's cell then (To is the plan's).
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, (int X, int Y) From, (int X, int Y) To)> Legs = [];
        // A Move chase (a surround slot's, a negative footprint's), cut to a leg when longer: placed by the plan on a
        // predictive client (To is the leg's cell, or the Move's when it is no longer); placed and cut at the gate on a
        // lockstep one (To is the Move's cell).
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, (int X, int Y) From, (int X, int Y) To)> MoveLegs = [];
        public readonly List<(uint Id, SpawnedMonster Monster, int Action)> Swings = [];
        // Swings a lockstep client's monster began by itself when its chase walk-up got there (no 0x1ab: the client's
        // action is already swinging), with the start of the frame the swing began in.
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, double BegunAt)> ArrivedSwings = [];
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, uint Target, SpawnedMonster Weapon)> WeaponSwings = [];
        public readonly List<(uint Id, SpawnedMonster Monster)> BossBars = [];
        public readonly List<PlayerMinions.Attack> MinionSwings = [];
        // What changed on the monsters' chill, freeze and shock (MonsterAilmentChangesLocked), sent before the orders.
        public readonly List<(byte[] Packet, string Log)> Ailments = [];
        // On a lockstep client, the start of the frame the walks were placed in (PlayerWalk.FrameOf at the plan's time).
        public double FrameStart = double.NaN;
    }

    public static async Task RunMonsterAi(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        var cadence = new MonsterAiCadence();
        try
        {
            while (PlayerUpdatesCurrent(conn, updates) && !conn.LeavingArea && conn.PendingTransfer is null)
            {
                await StepMonsterAi(remote, stream, ciphers, conn, cadence);
                if (!PlayerUpdatesCurrent(conn, updates)) return;
                int delay;
                lock (conn.Monsters)
                    delay = cadence.DelayMilliseconds(updates.Schedule.Now(conn));
                await updates.Schedule.Wait(PlayerUpdateRole.MonsterAi, delay, updates.Stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }

    // Kept separate from socket dispatch so all decisions in a pass use the same game time and player pose.
    public static MonsterAiPlan PlanMonsterAi(ClientState conn, MonsterAiCadence cadence, double now)
    {
        var plan = new MonsterAiPlan();
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null) return plan;
            // The instance's ailments and walks are stepped once, by the connection that runs them (SharedMonsters.cs);
            // each monster's AI runs on its target player's connection (MonsterTargetsLocked below).
            bool runner = IsMonsterRunner(conn);
            if (!conn.Paused && runner) plan.Ailments.AddRange(MonsterAilmentChangesLocked(conn, now));
            if (!cadence.TryBeginStep(now, conn.AiEnabled && !conn.Paused && conn.PlayerLife > 0)) return plan;
            // Before any order replaces a walk: its steps since the last pass would go unchecked.
            if (runner) StepWalkers(conn, now);
            using var blockers = conn.BeginMonsterBlockerFrame(now);
            if (conn.LockstepClient && conn.LockstepFrame is { } sending) plan.FrameStart = Math.Min(now, sending.Start);
            var (px, py) = conn.Walk.Position(now);
            // Entry protection: hostile monsters neither chase nor swing while the player stands protected on the arrival cell.
            bool protectedEntry = InGracePeriod(conn);
            var askers = new List<(MonsterSurround.Asker Asker, SpawnedMonster M, double D)>();
            foreach (var (id, m) in conn.Monsters)
            {
                // Out of the wake zone the client's copy stands frozen (07 §3); the server's stands too (KeepWakeZone
                // ended its walk) and gets no order until it is back, or the return's 0x128 would put the copy wherever
                // the estimate walked meanwhile (live 2026-11-03: 162 cells, in one jump).
                // A lockstep client's monster takes no order before the frame after the one its hit recovery ends in
                // (TakeHitPacket): one sent earlier waits and starts after the player's steps of that frame, or a frame
                // or more after the estimate's walk.
                if (m.Dead || m.Life == 0 || m.OwnerId is not null || m.StageRunning || m.Scripted || m.Asleep || OwnedFighter(conn, id) is not null
                    || !MonsterAiCadence.Due(double.IsNaN(plan.FrameStart) ? now : plan.FrameStart, m.FlinchUntil)) continue;
                // Asleep for this client (its own wake zone, KeepWakeZone): no order and no boss bar about an object the
                // client has put to sleep; the boss life bar for one crashed the client (live 2026-11-05).
                if (conn.AsleepMonsters.Contains(id)) continue;
                // The King in the Mists has its own phase controller (KingInMists.cs): one action at a time, all its
                // damage through MonsterSwing/MonsterAreaContact. Its Mystic Fetishes are stationary shrines the
                // controller activates; the ordinary chase would slowly drag them toward the player.
                if (Sirus.IsActor(m.Variety) || KitavaActFive.IsActor(m.Variety) || KitavaActTen.IsActor(m.Variety) || DepravedTrinity.IsBoss(m.Variety) || VaalOversoul.IsBoss(m.Variety) || DoedreSewers.IsBoss(m.Variety) || BrineKing.IsBoss(m.Variety) || KingInMists.IsBoss(m.Variety) || KingInMists.IsFetish(m.Variety)
                    || KingInMists.IsGuidingLight(m.Variety) || AtlasBosses.IsBoss(m.Variety)) continue;
                if (m.Hostile && protectedEntry) continue;
                var (mx, my) = m.Walk.Position(now);
                double d = Math.Sqrt((double)(mx - px) * (mx - px) + (double)(my - py) * (my - py));
                // One player at a time: the instance's monster acts for whoever it targets, on that one's connection.
                if (m.Hostile && !MonsterTargetsLocked(conn, id, m, (mx, my), d, now)) continue;
                if (m.Hostile && GolemEnemyOrderLocked(conn, id, m, now, d)) continue;
                // A boss's kit (BossKits): the skill that is ready and reaches the player, else its default swing. Never
                // while a swing still runs: the attack an order names must stay the one its hit is applied with.
                if (m.Kit.Count > 0 && now >= m.BusyUntil) m.Attack = BossKits.Choose(m, now, d);
                if (!m.Aggro && d <= m.AggroRange) { m.Aggro = true; m.LastTarget = null; }
                // No boss bar during an encounter's entrance: its script shows the bar as the fight starts.
                if (m.Aggro && !m.BossBarShown && !m.EntranceGrace && m.Variety is { BossHealthBar: true } && m.States?.IndexOf("boss_life_bar") >= 0)
                {
                    m.BossBarShown = true;
                    plan.BossBars.Add((id, m));
                }
                else if (m.Aggro && !m.Fleeing && d > 2 * m.AggroRange && !conn.Minions.Owns(id))   // a flee walk is not cut short; a minion keeps following
                {
                    m.Aggro = false;
                    m.SwingGeneration = null;
                    m.LastTarget = null;
                    // On a lockstep client it gets no order, as GGG's gets none when a pursuit ends: any order still at the
                    // gate is dropped and the estimate goes on with its last order, as the client's monster does (a
                    // chase's walk-up after the player, NextPlannedAction), so the wake zone's return (LockstepPlace)
                    // finds the client's monster where the estimate is (M0 bench: 69 of 74 still walking 0.5 s after a
                    // dropped one-cell give-up Move). GGG's idle orders after it are a difference
                    // (Lockstep.Differences.cs).
                    if (conn.LockstepClient)
                    {
                        NextPlannedAction(m);
                        Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) gives up at ({mx}, {my}): not sent, its walk goes on to {m.Walk.Destination}");
                        continue;
                    }
                    // GGG ends a predictive chase with S→C 0x1ac before the give-up Move (925 of 1166 live give-ups).
                    plan.EndedActions.Add(id);
                    // A Move to its own cell does not replace the attack a predictive client is still chasing with, so it
                    // goes to the next cell.
                    var stay = NextCell(conn, m, mx, my);
                    plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, stay, now), stay, "gives up"));
                    continue;
                }
                // Frozen: it does nothing until the freeze ends. On a predictive client it stands where it is: a chase order
                // stays open on the client even when the estimate has stopped (the action goes on after the player until
                // something replaces it), so a Move onto its own cell stops it.
                double action = MonsterActionFactor(m, now);
                if (action == 0)
                {
                    if (now < m.Walk.ArrivesAt || m.LastTarget is not null)
                    {
                        // A lockstep client takes no Move onto the walker's own cell (07 §6), so nothing stops it: no
                        // stop is sent, any order still at the gate is dropped, and the estimate keeps its walk as the
                        // client's monster does, a chase's walk-up with its goal test (no is_frozen stop for the player,
                        // 07 §5; a monster's freeze is not traced).
                        if (conn.LockstepClient) NextPlannedAction(m);
                        else plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, (mx, my), now), (mx, my), "is frozen"));
                    }
                    m.LastTarget = null;
                    continue;
                }
                // Fleeing (MonsterFlee): once a started swing ends, it walks to its flee point and does nothing
                // else until that walk ends, there or short of it; then it chases again.
                if (m.Fleeing)
                {
                    if (now < m.BusyUntil || cadence.SwingPending(m)) continue;
                    if (m.FleeTo is { } flee)
                    {
                        m.FleeTo = m.LastTarget = null;
                        m.SwingGeneration = null;
                        plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, flee, now), flee, "flees"));
                        continue;
                    }
                    if (now < m.Walk.ArrivesAt) continue;
                    m.Fleeing = false;
                }
                // A swing that has only just been ordered: the client's monster has not begun it while the player is out of
                // reach, it walks after the player until it can (07 §5), so the chase goes on and the swing is off.
                // A lockstep client needs no 0x1ac to begin, so it can't be called off: it would stand through the swing.
                if (!conn.LockstepClient && m.Attack is { InPlace: false } ordered && m.SwingGeneration == m.ActionGeneration
                    && now - m.SwingOrderedAt < SwingOrderSeconds && d > ordered.Range)
                {
                    m.BusyUntil = now;
                    m.SwingGeneration = null;
                    m.ActionGeneration++;
                }
                if (conn.Minions.Attacks.TryGetValue(id, out var pendingMinion))
                {
                    if (ReferenceEquals(pendingMinion.Monster, m) && conn.Minions.Fights(id))
                    { plan.MinionSwings.Add(pendingMinion); continue; }
                    conn.Minions.Attacks.Remove(id);
                }
                if (!m.Aggro || !MonsterAiCadence.Due(now, m.BusyUntil) || cadence.SwingPending(m)) continue;
                // The player's fighting minions attack his enemies near them; with none they follow him.
                if (conn.Minions.Fights(id) && m.Attack is { } ma && MinionTargetLocked(conn, id, m, (mx, my), (px, py), now) is { } target)
                {
                    // A lockstep client takes no Move within 2 cells (07 §6), so a minion that near its approach cell gets
                    // its attack aimed at the target instead. A melee (pathing) attack walks up on the client
                    // (0x141e9afc0), as a melee leg that short does (PlaceLockstepLeg), and swings in the frame it gets
                    // there (07 §6): the estimate walks the same walk-up and times the swing from its end. An in-place
                    // one (skill_is_non_pathing) plays where the minion stands, and its walk is left as it is.
                    bool walkUp = !target.InReach && !double.IsNaN(plan.FrameStart) && !LockstepTakesMove((mx, my), target.Approach);
                    if (target.InReach || walkUp)
                    {
                        double begins = now;
                        if (!ma.InPlace)
                        {
                            (int X, int Y) stop = walkUp ? target.Approach : (mx, my);
                            m.Walk.Order(stop.X, stop.Y, now);
                            if (walkUp) begins = Math.Max(now, m.Walk.ArrivesAt);
                        }
                        m.BusyUntil = begins + ma.Seconds;
                        m.LastTarget = null;
                        var attack = new PlayerMinions.Attack(id, m, (mx, my), target.Id, target.Monster, target.At, begins + ma.Seconds / 2);
                        conn.Minions.Attacks[id] = attack;
                        plan.MinionSwings.Add(attack);
                    }
                    // A new path when the target moved, else again after arriving short, at most twice a second.
                    else if (m.LastTarget is not { } last || Math.Abs(last.X - target.Approach.X) + Math.Abs(last.Y - target.Approach.Y) >= 3
                        || now >= m.Walk.ArrivesAt && now >= m.NextChaseRetryAt)
                    {
                        m.NextChaseRetryAt = now + 0.5;
                        m.LastTarget = target.Approach;
                        m.SwingGeneration = null;
                        plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, target.Approach, now),
                            target.Approach, $"minion chases monster 0x{target.Id:x}"));
                    }
                    continue;
                }
                if (PlanRampageWeaponTarget(conn, cadence, plan, id, m, now, (mx, my), d)) continue;
                // Attack cadence remains the attack's own duration; faster AI only reduces the delay after expiry.
                // A pending echo/contact keeps its original generation instead of reserving repeated swings at the gate.
                // A chill slows the swing by its action speed, as the native clip does.
                // A lockstep client's chase walk-up swings by itself, waiting for no 0x1ac (0x141e9afc0), in the frame its
                // goal test finds the player in reach: where the monster stands when the order applies
                // (PlaceLockstepChase), or on the way, every frame and at each waypoint, against the player's cell then
                // (StepFrames). The server takes that swing as its own, from that frame's start, with no order: a second
                // one would only wait behind the swing (0x141e937b0), and GGG's next 0x1ab after its 0x1ac comes 0.96 s
                // later at the median. The hit still asks the reach at the contact.
                if (conn.LockstepClient && m.ClientSwingAt is { } begun)
                {
                    m.ClientSwingAt = null;
                    if (m.ClientSwingAction == m.ActionGeneration && m.SwingGeneration is null)
                    {
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) swings from its walk-up at {m.Walk.Position(begun)}: "
                            + $"player ({px}, {py}) {d:0.0} away now, begun {now - begun:0.000} s ago (t={now:0.000})");
                        m.BusyUntil = begun + MonsterSwingSeconds(conn, m, begun) / action + conn.LockstepActionTail;
                        m.LastTarget = null;
                        m.SwingGeneration = m.ActionGeneration;
                        m.SwingOrderedAt = begun;
                        cadence.BeginSwing(m, m.ActionGeneration);
                        plan.ArrivedSwings.Add((id, m, m.ActionGeneration, begun));
                        continue;
                    }
                }
                // Totems, statues, vines and the like (MovementSpeed 0) never walk: the estimate's minimum speed must not
                // move them, so they get no chase. Neither does a type the client never walks (Pathfinding immobile),
                // whatever its speed column says: the client would only hop it to each order's from cell.
                bool still = m.Variety is { MovementSpeed: 0 } or { Immobile: true };
                // A swing order: a monster's in-place skill, a summoner's (its actor's clip, not a walk-up), a melee attack
                // of a monster that never walks (the chase's own 0x1ab, whose goal test passes as it applies: 0x141e81c20),
                // and any attack on a predictive client, where a walking player has already gone farther: no swing right
                // at the edge of the reach. A lockstep client's walking melee monster swings only by its chase's goal test
                // (above): standing in reach, it gets the chase, which starts the swing in the order's own frame.
                if (m.Hostile && m.Attack is { } a && (a.InPlace || a.Summon is not null || still || !conn.LockstepClient)
                    && d <= (a.InPlace || conn.LockstepClient || !conn.Walk.Moving(now) ? a.Range : a.Range - MovingReachMargin)
                    && SummonReadyLocked(conn, id, m, a, now))
                {
                    m.BusyUntil = now + MonsterSwingSeconds(conn, m, now) / action + conn.LockstepActionTail;
                    if (ServerTrace.Ai && conn.LockstepClient && a.InPlace && m.Walk.Moving(now))
                    {
                        var frame = conn.LockstepFrame;
                        var inFrame = frame is { } f ? m.Walk.Steps(f.Start, f.End) : [];
                        var next = m.Walk.Steps(now, now + 1);
                        Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) shoots while walking from ({mx}, {my}): frame {(frame is { } fr ? $"{fr.Start:0.000}-{fr.End:0.000}" : "none")}, "
                            + $"steps in frame {inFrame.Count}{(inFrame.Count > 0 ? $" (first {inFrame[0].At:0.000} to {inFrame[0].Cell})" : "")}, "
                            + $"next step {(next.Count > 0 ? $"{next[0].At:0.000} to {next[0].Cell}" : "none")}, walk ends {m.Walk.ArrivesAt:0.000} at {m.Walk.Destination} (t={now:0.000})");
                    }
                    // A melee swing starts at the end of its walk-up. An attack during a Move ends that with
                    // 0x141e96fe0(…, 0), which does not stop the pathing: the monster swings while it walks on to the
                    // Move's end, on a lockstep client too (live 2026-11-03, 377 Sand Spitter shots ordered mid-Move: the
                    // clip starts on the order's frame and the walk goes on to the Move's target, 1.4 cells left at the
                    // median, 4.2 at the 90th percentile), so the estimate keeps its walk.
                    if (!a.InPlace && m.LastTarget is not null) m.Walk.Order(mx, my, now);
                    m.LastTarget = null;
                    m.SwingGeneration = ++m.ActionGeneration;
                    cadence.BeginSwing(m, m.ActionGeneration);
                    // The order names this attack's skill: what it used now goes on cooldown.
                    BossKits.Consume(m, a, now);
                    plan.Swings.Add((id, m, m.ActionGeneration));
                    continue;
                }
                if (still) continue;
                // A melee monster chases as GGG's do: its attack aimed at the player, repeated, so the client walks it to
                // where it really has the player. The estimate walks the same walk-up: until the distance is within the
                // attack's reach (max range reach - 1, the player's size as the tolerance: 0x141e95400, 0x141e81950).
                if (m.Hostile && m.Attack is { InPlace: false } melee)
                {
                    if (!LockstepReorderDue(conn, plan, m, cadence, now) || !cadence.TryAttackChase(m, now)) continue;
                    // Far off, a Move to the cell LegCells along the walk-up instead of the attack aimed at the player,
                    // ordered again on the same cadence; the attack's own walk-up from within a leg of the reach. An
                    // estimate that finds no way at all keeps the attack order: the client's own search may.
                    if (d > melee.Range - 1 + LegCells)
                    {
                        (int X, int Y) leg = (px, py), start = m.Walk.Position(now);
                        bool placed = !double.IsNaN(plan.FrameStart);
                        if (!placed)
                        {
                            m.Walk.Approach(px, py, melee.Range - 1, now, LivingCollision.PlayerSize);
                            leg = m.Walk.Ahead(now, LegCells / m.Walk.Speed);
                            placed = leg != start;
                            if (placed) start = m.Walk.Order(leg.X, leg.Y, now);
                        }
                        if (placed)
                        {
                            if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) chase leg from {start} to {leg}: player ({px}, {py}) {d:0.0} away (t={now:0.000})");
                            m.LastTarget = null;
                            m.SwingGeneration = null;
                            plan.Legs.Add((id, m, NextPlannedAction(m), start, leg));
                            continue;
                        }
                    }
                    (int X, int Y) here;
                    if (double.IsNaN(plan.FrameStart))
                    {
                        var walk = m.Walk.Approach(px, py, melee.Range - 1, now, LivingCollision.PlayerSize);
                        here = walk.Here;
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) chase walk from {walk.Here} to {walk.Stop}, arrives in {walk.At - now:0.000} s, "
                            + $"reached goal {m.Walk.ReachedGoal}, player ({px}, {py}) {d:0.0} away (t={now:0.000})");
                    }
                    else
                    {
                        // A lockstep client's walk is placed when the order goes out (LockstepOrderFrom).
                        here = m.Walk.Position(now);
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) chase from {here}, placed at the gate: player ({px}, {py}) {d:0.0} away (t={now:0.000})");
                    }
                    m.LastTarget = (px, py);
                    m.SwingGeneration = null;
                    plan.Chases.Add((id, m, NextPlannedAction(m), here, (px, py)));
                    continue;
                }
                // Bodies take distinct surround slots. A negative footprint does not collide with living actors,
                // but still chases: retain its direct approach without reserving space in the surround.
                if (LivingCollision.Size(m) < 0)
                {
                    if (d <= m.StopRange) continue;
                    double f = (m.StopRange - 1) / d;
                    (int X, int Y) to = ((int)Math.Round(px + (mx - px) * f), (int)Math.Round(py + (my - py) * f));
                    if (!conn.Layout.WalkableFor(to.X, to.Y, 0) || conn.Barrier.Contains(to)) to = (px, py);
                    if (!LockstepReorderDue(conn, plan, m, cadence, now) || !cadence.TryChase(m, to, now, walking: now < m.Walk.ArrivesAt || m.Leg is not null)) continue;
                    m.LastTarget = to;
                    m.SwingGeneration = null;
                    var before = m.Walk.Save();
                    var from = PlaceMove(plan, m, to, now);
                    plan.MoveLegs.Add((id, m, NextPlannedAction(m), from, double.IsNaN(plan.FrameStart) ? CutToLeg(m, to, now, before) : to));
                }
                else if (LivingCollision.StepsWithBodies(m))
                    askers.Add((new(id, mx, my, LivingCollision.Size(m), m.LastTarget,
                        m.Attack?.Range ?? double.PositiveInfinity, m.Attack?.Ranged == true), m, d));
            }
            if (askers.Count == 0) return plan;
            // Every other living body near the player keeps its place: the chasers' cells go around it.
            var asking = askers.Select(a => a.Asker.Id).ToHashSet();
            var occupied = blockers.Bodies.Where(b => !asking.Contains(b.Id)
                && Math.Max(Math.Abs(b.X - px), Math.Abs(b.Y - py)) < 64).Select(b => (b.X, b.Y, b.Size));
            var fixedBlockers = blockers.FixedBlockers;
            long surroundAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocationAt = GC.GetAllocatedBytesForCurrentThread();
            var slots = MonsterSurround.Assign(askers.Select(a => a.Asker), (px, py), occupied,
                (x, y, size) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y))
                    && !GameServer.Core.World.ClientPathfinder.ObjectBlocks(fixedBlockers, x, y, size + 1, size + 4));
            conn.Performance.Elapsed(LatencyPhase.Surround, surroundAt);
            conn.Performance.SurroundAllocation(GC.GetAllocatedBytesForCurrentThread() - allocationAt);
            foreach (var (asker, m, d) in askers)
            {
                // A full surround (every ring taken): aim at the player and queue behind the others, as before.
                (int X, int Y) to = slots.TryGetValue(asker.Id, out var slot) ? slot : (px, py);
                // Standing in its cell already: it waits there for the swing (no order, no new path).
                if ((asker.X, asker.Y) == to) { m.LastTarget = to; continue; }
                // The repath budget and the three-cell change rule apply; the native budget can end a path short of its
                // cell, so the same cell is retried after arrival on the bounded retry cadence, and a walk still under
                // way is ordered again on the chase cadence (MonsterAiCadence.TryChase).
                // A walk at its leg's end is still on its way: ordered on, not retried as an arrival.
                bool onLeg = m.Leg is not null;
                if (!LockstepReorderDue(conn, plan, m, cadence, now) || !cadence.TryChase(m, to, now, retryAfterArrival: now >= m.Walk.ArrivesAt && !onLeg, walking: now < m.Walk.ArrivesAt || onLeg)) continue;
                m.LastTarget = to;
                m.SwingGeneration = null;
                var before = m.Walk.Save();
                var from = PlaceMove(plan, m, to, now);
                plan.MoveLegs.Add((asker.Id, m, NextPlannedAction(m), from, double.IsNaN(plan.FrameStart) ? CutToLeg(m, to, now, before) : to));
            }
        }
        return plan;
    }

    // On a lockstep client an order whose from cell is not the client's cell puts the client's monster onto it and zeroes
    // its walk's accumulator (0x141e34b40), while the estimate keeps its fraction of the step in progress: the client's
    // next step comes that fraction later than the estimate's, the next order finds the cells apart again as often as
    // not, and the estimate runs a step ahead through runs of orders (live 2026-11-03: two of five orders after such a
    // set found the cells apart, against one of eight after a match; 20 to 26 % of a fight's orders had the estimate a
    // cell ahead). So a monster whose estimate is walking is ordered again only at a frame start right after the
    // estimate's step, where the fraction is under a frame and a client a cell off loses little; the order waits at most
    // two steps past its cadence for such a frame. A standing estimate, and a predictive client, order as ever.
    static bool LockstepReorderDue(ClientState conn, MonsterAiPlan plan, SpawnedMonster m, MonsterAiCadence cadence, double now)
    {
        if (!conn.LockstepClient || double.IsNaN(plan.FrameStart)) return true;
        var (into, step) = m.Walk.StepPhase(plan.FrameStart);
        return step == 0 || into < FrameMs / 1000d + 1e-6 || cadence.ChaseOverdue(m, now, 2 * step);
    }

    // The action generation of an order the plan makes, or of a lockstep give-up or freeze, which sends nothing: any
    // older order still at the gate is dropped. A chase walk-up the client runs (WalkUpOpen) is carried to the new
    // generation, as the client runs it until an order it takes applies (LockstepOrderFrom, at the start of the frame
    // being sent; a Move it refuses leaves its action going on, 0x141949a20), and a give-up or a freeze sends it nothing.
    // A swing order, a flinch, a stage and a transfer move the generation on themselves and close it.
    public static int NextPlannedAction(SpawnedMonster m)
    {
        if (m.ClientChase == m.ActionGeneration) m.ClientChase = m.ActionGeneration + 1;
        return ++m.ActionGeneration;
    }

    // A Move's walk: placed now on a predictive client; on a lockstep client the plan only names the target and the walk
    // is placed when the order goes out (LockstepOrderFrom), so the cell reported here is the walker's cell now.
    static (int X, int Y) PlaceMove(MonsterAiPlan plan, SpawnedMonster m, (int X, int Y) to, double now) =>
        double.IsNaN(plan.FrameStart) ? m.Walk.Order(to.X, to.Y, now) : m.Walk.Position(now);

    // A Move chase's walk, just placed at `at` over the walk saved as `before`, cut to its first LegCells cells when it is
    // longer (07 §5): the walker goes to the leg's cell and is ordered on from there on the chase cadence. Returns the cell
    // to order: the leg's, or the Move's own when the walk is no longer than a leg (or found no way). The leg goes over
    // `before`, as the client gets only the leg: the walk to `to` never ran, and over it a stand's kept time would be spent
    // and the leg start a step late.
    static (int X, int Y) CutToLeg(SpawnedMonster m, (int X, int Y) to, double at, GameServer.Core.World.PlayerWalk.Snapshot before)
    {
        double legSeconds = m.Walk.Speed > 0 ? LegCells / m.Walk.Speed : 0;
        var leg = legSeconds > 0 && m.Walk.ArrivesAt - at > legSeconds + 1e-9 ? m.Walk.Ahead(at, legSeconds) : to;
        if (leg == to || leg == m.Walk.Position(at)) { m.Leg = null; return to; }
        m.Walk.Restore(before);
        m.Walk.Order(leg.X, leg.Y, at);
        m.Leg = leg;
        return leg;
    }

    // The nearest other cell a monster can stand on, or its own when it has none.
    static (int X, int Y) NextCell(ClientState conn, SpawnedMonster m, int x, int y)
    {
        int size = Math.Max(0, LivingCollision.Size(m));
        for (int reach = 1; reach <= 3; reach++)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (conn.Layout.WalkableFor(x + dx * reach, y + dy * reach, size) && !conn.Barrier.Contains((x + dx * reach, y + dy * reach)))
                    return (x + dx * reach, y + dy * reach);
        return (x, y);
    }

    public static async Task StepMonsterAi(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        MonsterAiCadence cadence)
    {
        MonsterAiPlan plan;
        if (DamageTriggers.TryGetValue(conn, out _))
            ReleaseDamageTakenTriggers(conn, ProjectilesFor(remote, stream, ciphers, conn));
        // Advance the projectile runtime first: decisions and committed projectile contacts share one game time and pose.
        using (ProjectileMutation(conn, phase: "ai")) plan = PlanMonsterAi(conn, cadence, Math.Max(ProjectileWorldTime(conn), AreaTime(conn)));
        const ushort Move = 0x2909;
        foreach (var (packet, log) in plan.Ailments) await Send(remote, stream, ciphers, packet, log);
        foreach (uint id in plan.EndedActions)
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.ActionEnded(id), ServerTrace.Ai ? $"0x01ac monster 0x{id:x} action ended" : null);
        // A chase is re-ordered every few ticks: its lines only with Game_TRACE=ai (the packet history keeps them).
        foreach (var (id, m, action, from, to, why) in plan.Moves)
            await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () => PlanMoveAtGate(conn, plan, id, m, action, from, to, why),
                ServerTrace.Ai || why == "gives up" ? $"0x01ab monster 0x{id:x} {why}: move from {from} to {to}" : null);
        foreach (var (id, m, action, from, to) in plan.Chases)
            await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () => ChaseAtGate(conn, plan, id, m, action, from, to),
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases the player from {from} to {to}" : null);
        foreach (var (id, m, action, from, to) in plan.Legs)
            await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
                {
                    var aim = to;
                    bool noWay = false;
                    if (LockstepOrderFrom(conn, plan, id, m, action, from, at =>
                    {
                        var (here, leg, attack) = PlaceLockstepLeg(conn, m, action, at);
                        (aim, noWay) = (leg, attack);
                        return here;
                    }) is not { } start) return null;
                    if (noWay)
                    {
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) leg finds no way from {start}: attack order at the player {aim}");
                        return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, m.Attack!.SkillId, m.NextSequence(), 0, targetObjectId: PlayerId);
                    }
                    return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, Move, NextMonsterMoveSequence(conn, m), conn.MonsterOrderFlags);
                },
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases a leg from {from} to {to}" : null);
        foreach (var (id, m, action, from, to) in plan.MoveLegs)
            await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
                {
                    var aim = to;
                    bool taken = true;
                    if (LockstepOrderFrom(conn, plan, id, m, action, from, at =>
                    {
                        var (here, leg, take) = PlaceLockstepMoveLeg(m, to, at);
                        (aim, taken) = (leg, take);
                        return here;
                    }) is not { } start) return null;
                    if (!taken)
                    {
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) Move to {aim} within 2 cells of {start}: not sent, its walk goes on");
                        return null;
                    }
                    return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, Move, NextMonsterMoveSequence(conn, m), conn.MonsterOrderFlags);
                },
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases from {from} to {to}" : null);
        foreach (var (id, m) in plan.BossBars)
        {
            lock (conn.Monsters) { if (!MonsterPresent(conn, id, m)) continue; conn.BossBarsSeen.Add(id); }
            await ShowBossLifeBar(remote, stream, ciphers, id, m, conn);
        }
        if (!CurrentGameWork) return;
        foreach (var (id, m, action) in plan.Swings)
            _ = RunPlannedMonsterSwing(remote, stream, ciphers, conn, cadence, id, m, action);
        foreach (var (id, m, action, begun) in plan.ArrivedSwings)
            _ = RunPlannedMonsterSwing(remote, stream, ciphers, conn, cadence, id, m, action, begun);
        foreach (var attack in plan.MinionSwings)
            StartMinionSwing(remote, stream, ciphers, conn, attack);
        foreach (var (id, m, action, target, weapon) in plan.WeaponSwings)
            _ = RunPlannedRampageWeaponSwing(remote, stream, ciphers, conn, cadence, id, m, action, target, weapon);
        conn.Performance.MaybeLog(remote);
    }

    public static async Task RunPlannedMonsterSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, MonsterAiCadence cadence, uint id, SpawnedMonster m, int action, double? begunAt = null)
    {
        try { await MonsterSwing(remote, stream, ciphers, conn, id, m, action, begunAt: begunAt); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        // Anything else is a server bug: the swing was already shown, so name the lost hit instead of faulting the task
        // unobserved (review 2026-10-30).
        catch (Exception ex) { Console.WriteLine($"[gateway] {remote} monster 0x{id:x} swing {action} failed: {ServerLog.Describe(ex)}"); }
        finally { lock (conn.Monsters) cadence.CompleteSwing(m, action); }
    }

    // A monster swings at the player: its skill as an action aimed at the player (0x1ab, flags | 0x80 + target id; a melee
    // swing with its own rising sequence and the 0x1ac that starts it), then at the contact point (the clip's keyframe,
    // half the attack time without rig data; a projectile skill's clip contact, MonsterRelease) the hit lands if the monster still lives and the player is still in reach (walking away dodges
    // it): the player's life goes down by the monster's damage (0x1b1 on the player). At 0 the client runs the player's
    // death.
    // PoE's evasion entropy (wiki: Evasion): shared by every attack against the player, reset after 3.33 s. Call under
    // the Monsters lock.
    public static bool EvadesLocked(ClientState conn, int hitChance, double now)
    {
        if (conn.EvasionEntropy < 0 || now - conn.LastEvasionAt > 3.33)
            conn.EvasionEntropy = conn.IncomingHitRandom.Next(100);
        conn.LastEvasionAt = now;
        conn.EvasionEntropy += hitChance;
        if (conn.EvasionEntropy < 100) return true;
        conn.EvasionEntropy -= 100;
        return false;
    }

    // Ordinary AI work belongs to this monster object in this connection. Generations also prevent a flinch that has
    // already ended from reviving an old queued order or contact. Callers may already hold Monsters.
    public static bool MonsterActionCurrent(ClientState conn, uint id, SpawnedMonster m, int action)
    {
        lock (conn.Monsters)
            return MonsterPresent(conn, id, m) && conn.PlayerLife > 0
                && (m.Hostile || KingInMists.IsFetish(m.Variety)) && !m.StageRunning
                && m.ActionGeneration == action && conn.Clock.Elapsed.TotalSeconds >= m.FlinchUntil;
    }

    public static bool MonsterPresent(ClientState conn, uint id, SpawnedMonster m) =>
        CurrentGameWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PendingTransfer is null && !m.Dead && m.Life > 0
        && ReferenceEquals(conn.Monsters.GetValueOrDefault(id), m);

    // Apply the stage effect and end recovery on the area game clock, at the actual send gate. Hot-reloaded
    // workers resume the same deadlines and one-shot state; a pause cannot consume the remaining stage time.
    public static async Task CompleteMonsterStage(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, SpawnedMonster m)
    {
        while (true)
        {
            lock (conn.Monsters) if (!MonsterPresent(conn, id, m) || !m.StageRunning) return;
            bool completed = false;
            await SendWorldRecords(remote, stream, ciphers, conn, id, () =>
            {
                var bytes = AdvanceMonsterStageRecords(conn, id, m, AreaTime(conn));
                completed = !m.StageRunning;
                if (bytes is not null)
                    Console.WriteLine($"[gateway] {remote} => monster 0x{id:x} ({m.Label}) stage " +
                        $"{(completed ? "complete" : "effect")}: life {m.Life}, game time {AreaTime(conn):0.000}");
                return bytes;
            });
            if (completed) return;
            int wait;
            lock (conn.Monsters)
            {
                double next = m.StageEffectApplied ? m.BusyUntil : m.StageEffectAt;
                wait = conn.Paused ? 100 : (int)Math.Clamp(Math.Ceiling((next - AreaTime(conn)) * 1000), 16, 1000);
            }
            await AreaWait(PlayerUpdateRole.MonsterAction, wait);
        }
    }

    // Called only at the send gate; all one-shot state changes and their packets belong to the same guarded update.
    public static byte[]? AdvanceMonsterStage(ClientState conn, uint id, SpawnedMonster m, double now) =>
        AdvanceMonsterStageRecords(conn, id, m, now) is { } records ? [.. records.SelectMany(r => r)] : null;

    public static byte[][]? AdvanceMonsterStageRecords(ClientState conn, uint id, SpawnedMonster m, double now)
    {
        lock (conn.Monsters)
        {
            if (!MonsterPresent(conn, id, m) || !m.StageRunning || conn.Paused) return null;
            var stage = m.Stage!;
            // A stage started by an older loaded module retains its recovery deadline.
            if (double.IsNaN(m.StageEffectAt))
                m.StageEffectAt = m.BusyUntil - stage.CastSeconds + stage.EffectSeconds(m.Variety is { } variety
                    ? SkillsFor(conn).MonsterContact(variety.Id, "Throw", 0xffff) : null);
            var bytes = new List<byte[]>();
            if (!m.StageEffectApplied && now >= m.StageEffectAt)
            {
                if (m.Variety?.ComponentsOrBase.ToList().IndexOf("Transitionable") is int t and >= 0)
                    bytes.Add(InstanceEntities.TransitionableUpdate(id, (byte)t, stage.TransitionTo));
                bytes.Add(InstanceProtocol.LifeUpdate(id, m.MaxLife));
                m.Life = m.MaxLife;
                m.StageEffectApplied = true;
            }
            if (now >= m.BusyUntil)
            {
                bytes.Add(InstanceProtocol.ActionEnded(id));
                m.StageRunning = false;
            }
            return bytes.Count > 0 ? bytes.ToArray() : null;
        }
    }

    // Recheck at the actual encryption/send gate: movement, a swing echo or its contact may have waited behind a hit
    // reaction. Paused work keeps its original generation and resumes only while that generation still exists.
    // A lockstep client applies an order at the start of the frame its 0x1ab goes out in, so a lockstep order's walk is
    // placed here, in the send gate under conn.Monsters: at the start of the frame being sent (the frame advances in
    // the frame end's own gate, so an order written after a 0x2b7 sees the new frame), after the steps the walk the
    // client is still running takes in the frames before it (StepWalkers), from the cell they leave the walker on,
    // which the order reports.
    // Placed by the plan instead, a walk whose 0x1ab a frame end overtook (the AI pass and the frame loop are separate
    // tasks: 14 of 1 193 chase orders in a traced fight, live 2026-11-03) started a frame early on the server, and the
    // overtaken frame was replayed with the new walk where the client still ran the old one. A predictive client's walk
    // is the plan's.
    // The chase walk-up the order replaces stays open through those frames (NextPlannedAction), testing its goal as the
    // client's does. When it began its swing there, or the AI has since taken that swing, the order would only wait
    // behind the swing (0x141e937b0), so nothing is sent and the AI takes the swing on its next pass.
    // A melee chase leg placed at a lockstep frame's start `at`: the walk-up to the player's cell then, cut to its leg.
    // Returns the walker's cell, the cell to order and whether to send the attack aimed at the player instead: an
    // estimate that finds no way keeps the attack order, as the planner does without frames (the client's own search
    // may find one; a Move to its own cell would leave it standing).
    public static ((int X, int Y) Here, (int X, int Y) Aim, bool Attack) PlaceLockstepLeg(ClientState conn, SpawnedMonster m, int action, double at)
    {
        var player = conn.Walk.Position(at);
        var before = m.Walk.Save();
        var walk = m.Walk.Approach(player.X, player.Y, m.Attack!.Range - 1, at, LivingCollision.PlayerSize);
        var leg = m.Walk.Ahead(at, LegCells / m.Walk.Speed);
        // A leg within two cells is no way either: the client would not take it (LockstepTakesMove), and the attack's
        // walk-up is the one the estimate just placed.
        if (!LockstepTakesMove(walk.Here, leg))
        {
            TakeLockstepChase(m, action, walk.Here, player, at);
            return (walk.Here, player, true);
        }
        // The client gets only the leg, so it goes over the walk before the walk-up, which never ran: over the walk-up a
        // stand's kept time would be spent and the leg start a step late. The leg ends any walk-up the client ran.
        m.Walk.Restore(before);
        m.ClientChase = null;
        return (m.Walk.Order(leg.X, leg.Y, at), leg, false);
    }

    // A chase order at the send gate: the attack aimed at the player, from the cell its walk-up starts on, or null when
    // the walk-up it replaces began its swing first (LockstepOrderFrom).
    public static byte[]? ChaseAtGate(ClientState conn, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, (int X, int Y) to)
    {
        // Placed at the gate, the chase aims at the player's cell at that frame's start, as the plan would have.
        var aim = to;
        if (LockstepOrderFrom(conn, plan, id, m, action, from, at =>
        {
            var (here, player) = PlaceLockstepChase(conn, m, action, at);
            aim = player;
            return here;
        }) is not { } start) return null;
        return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, m.Attack!.SkillId, m.NextSequence(), 0, targetObjectId: PlayerId);
    }

    // A melee chase order (the attack aimed at the player) placed at a lockstep frame's start `at`: the walk-up to the
    // player's cell then, as the order starts at the frame's start, before any object ticks (a monster in a hit's recovery
    // is ordered only from the frame after it: TakeHitPacket). Returns the walker's cell and the player's.
    public static ((int X, int Y) Here, (int X, int Y) Aim) PlaceLockstepChase(ClientState conn, SpawnedMonster m, int action, double at)
    {
        var player = conn.Walk.Position(at);
        var here = m.Walk.Approach(player.X, player.Y, m.Attack!.Range - 1, at, LivingCollision.PlayerSize).Here;
        TakeLockstepChase(m, action, here, player, at);
        return (here, player);
    }

    // A chase order whose walk-up was just placed at `at` from `here`, as the client takes it. Its MoveTo tests the goal
    // where the monster stands first (0x141e81c20 → 0x141e81950): met, the swing starts in the order's own frame
    // (0x141e82310 → 0x141e9afc0, no 0x1ac in lockstep), for the AI to take (PlanMonsterAi). A search that finds no path
    // ends the action (0x141e82010 → 0x141e82310). Otherwise the walk-up is open, and the step pass tests its goal every
    // frame (StepFrames), with the search timer the setup's search zeroed (0x141e82010) counting from this frame.
    static void TakeLockstepChase(SpawnedMonster m, int action, (int X, int Y) here, (int X, int Y) player, double at)
    {
        m.LastTarget = player;
        m.ClientChase = null;
        m.ClientChaseTimerFrom = at;
        if (m.Walk.InReachOf(here, player, (int)(m.Attack!.Range - 1), LivingCollision.PlayerSize))
            (m.ClientSwingAt, m.ClientSwingAction) = (at, action);
        else if (m.Walk.Reached && !m.Walk.ReachedGoal) m.LastTarget = null;
        else m.ClientChase = action;
    }

    // A Move chase (a surround slot) placed at a lockstep frame's start `at` and cut to its leg. Returns the walker's
    // cell, the cell to order and whether to send it: a Move the client would not take is not sent, and the walk the
    // estimate had goes on, as the client's action does (any walk-up too); one it takes ends that action.
    public static ((int X, int Y) Here, (int X, int Y) Aim, bool Taken) PlaceLockstepMoveLeg(SpawnedMonster m, (int X, int Y) to, double at)
    {
        var here = m.Walk.Position(at);
        var (saved, leg) = (m.Walk.Save(), m.Leg);
        var placed = m.Walk.Order(to.X, to.Y, at);
        var aim = CutToLeg(m, to, at, saved);
        if (LockstepTakesMove(here, aim))
        {
            m.ClientChase = null;
            return (placed, aim, true);
        }
        m.Walk.Restore(saved);
        m.Leg = leg;
        return (here, aim, false);
    }

    // A plan Move (a flight, a minion's or a Rampage chase; a give-up or a freeze's stop on a predictive client) at the
    // send gate: its 0x1ab, or null when a lockstep client would not take it (LockstepTakesMove, 07 §6). On a lockstep
    // client the walk is placed at the start of the frame being sent (LockstepOrderFrom); a Move it would not take is not
    // sent and the estimate keeps its walk, as the client's action goes on (any walk-up too); one it takes ends that
    // action.
    public static byte[]? PlanMoveAtGate(ClientState conn, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, (int X, int Y) to, string why)
    {
        const ushort Move = 0x2909;
        bool taken = true;
        if (LockstepOrderFrom(conn, plan, id, m, action, from, at =>
        {
            var here = m.Walk.Position(at);
            taken = LockstepTakesMove(here, to);
            if (!taken) return here;
            m.ClientChase = null;
            return m.Walk.Order(to.X, to.Y, at);
        }) is not { } start) return null;
        if (!taken)
        {
            if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) {why}: Move to {to} within 2 cells of {start}: not sent, its walk goes on to {m.Walk.Destination}");
            return null;
        }
        return InstanceProtocol.ActionStarted(id, start.X, start.Y, to.X, to.Y, Move, NextMonsterMoveSequence(conn, m), conn.MonsterOrderFlags);
    }

    // The actor installs each order's sequence on its action (0x14219aa10); its hit lookup matches that sequence
    // on the same actor (0x141e977f0). Lockstep Moves and chases share the actor's rising order counter.
    // Called only after admission, under the Monsters lock, so a refused Move consumes no sequence.
    static ushort NextMonsterMoveSequence(ClientState conn, SpawnedMonster m) =>
        conn.LockstepClient ? m.NextSequence() : conn.NextActionSequence();

    // A lockstep client takes a Move order only when its target is 3 or more cells from the walker's cell. The test is
    // the admission of Move and MoveSpecial (vtable slot +0x130, 0x141949a20): dx*dx + dy*dy >= 9 from the walker's own
    // cell to the target, which 0x141e94900 runs before it ends the current action and before 0x141e95080 sets the
    // pair; the order's pair plays no part. A refused Move is destroyed there: no position set, no search, and the
    // current action goes on. MoveForced (0x3f1f) has no such test. The client sums the squares in 32-bit ints and
    // reads the sum as unsigned (0x1400f0c90), so this does too, wrapping as the client's does; within an area's cells
    // it is Chebyshev 3 or more.
    // Live 2026-11-04, monster Moves applied under trace-lockstep-tick.js with the path search hooked: 317 of 317 within
    // 2 cells untouched, 8 244 of 8 244 farther ones searched, set when the from cell differed. The player's Moves
    // follow the same rule (07 §6).
    public static bool LockstepTakesMove((int X, int Y) here, (int X, int Y) aim)
    {
        unchecked
        {
            int dx = aim.X - here.X, dy = aim.Y - here.Y;
            return (uint)(dx * dx + dy * dy) >= 9;
        }
    }

    static (int X, int Y)? LockstepOrderFrom(ClientState conn, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, Func<double, (int X, int Y)> place)
    {
        if (double.IsNaN(plan.FrameStart) || conn.LockstepFrame is not { } frame) return from;
        // Out of this client's wake zone since the plan: its copy has stopped ticking (the 0x1aa went out first), so an
        // order would walk the estimate alone until it is back. Nothing is sent and nothing placed (KeepWakeZone).
        if (conn.AsleepMonsters.Contains(id)) return null;
        StepWalkers(conn, frame.Start);
        if (m.ClientSwingAt is not null && m.ClientSwingAction == action || m.SwingGeneration == action)
        {
            if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) order {action} not sent: its walk-up began its swing"
                + $"{(m.ClientSwingAt is { } at ? $" at {at:0.000}" : "")} before the frame from {frame.Start:0.000}");
            return null;
        }
        m.ClientActionWalkStop = null;   // the order replaces the action whose end would have stopped the walk
        return place(frame.Start);
    }

    // When an order written now in its send gate (under conn.Monsters) starts on the client. A lockstep client starts it at
    // the start of the frame its 0x1ab goes out in, before any object ticks (07 §6), so an in-place attack is dated there
    // (its contact and BusyUntil follow) and placed there, after the steps of the frames before it, as a walk is
    // (LockstepOrderFrom). Those steps are usually replayed already (the plan's pass and the frame pass after each 0x2b7
    // step the walkers to the frame's start); a gate that runs between a 0x2b7 and that frame pass still has the frame
    // before it to replay, where a step the client found blocked would otherwise leave the from cell a step ahead.
    public static double LockstepOrderStart(ClientState conn, double now)
    {
        if (!conn.LockstepClient || conn.LockstepFrame is not { } frame) return now;
        StepWalkers(conn, frame.Start);
        return Math.Min(now, frame.Start);
    }

    public static async Task<bool> SendMonsterAction(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, SpawnedMonster m, int action, Func<byte[]?> packet, string? log = null)
    {
        while (MonsterActionCurrent(conn, id, m, action))
        {
            bool checkedAtGate = false, sent = false;
            await SendWorldRecord(remote, stream, ciphers, conn, id, [], log, atGate: () =>
            {
                // The projectile runtime first, as in the AI pass: a lockstep order places its walk here, and a contact
                // still pending before that would read the new walk's cells as the old walk's.
                using (ProjectileMutation(conn, phase: "send"))
                {
                    if (!MonsterActionCurrent(conn, id, m, action) || conn.Paused) return null;
                    checkedAtGate = true;
                    var bytes = packet();
                    sent = bytes is not null;
                    // The others in the instance see it too (SharedMonsters.cs).
                    if (bytes is not null) BroadcastMonsterMessage(conn, id, bytes);
                    return bytes;
                }
            });
            if (checkedAtGate) return sent;
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
        return false;
    }

    public static async Task<bool> WaitMonsterActionTime(ClientState conn, uint id, SpawnedMonster m, int action, double until)
    {
        if (!CurrentGameWork || !double.IsFinite(until)) return false;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!MonsterActionCurrent(conn, id, m, action)) return false;
                if (!conn.Paused && AreaTime(conn) >= until) return true;
            }
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
    }

    // Commit the flinch with native Actor TakeHit recovery, which cancels the current walk/attack and blocks new
    // actions. ActionEnded or a rig-only AnimationUpdate leaves Positioned's movement advancing underneath the clip.
    // If the send gate was busy, recovery starts when sent; a pause does not consume that recovery time.
    public static async Task SendMonsterFlinch(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, SpawnedMonster m, StunHit stun, bool log)
    {
        while (true)
        {
            lock (conn.Monsters)
                if (!MonsterPresent(conn, id, m) || m.Invulnerable || m.HitAnimations is not { Count: > 0 }
                    || m.Effects.CannotBeStunned || HitReaction.Chance(stun, m.MaxLife) <= 0) return;
            bool checkedAtGate = false;
            await SendWorldRecord(remote, stream, ciphers, conn, id, [], null, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (!MonsterPresent(conn, id, m) || conn.Paused) return null;
                    checkedAtGate = true;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    if (!HitReaction.TryFlinch(m, stun, now, Random.Shared)) return null;
                    float seconds = (float)(m.FlinchUntil - now);
                    if (log) Console.WriteLine($"[gateway] {remote} => 0x01d1 monster 0x{id:x} TakeHit recovery {seconds:0.000} s, " +
                        $"action {m.ActionGeneration}, recovery until {m.FlinchUntil:0.000}");
                    // Animation.dat row 5 selects TakeHit through this actor's native ACT. Keep clip selection and
                    // completion with Actor recovery instead of following it with a rig-only 0x1b2 override.
                    var flinch = TakeHitPacket(conn, id, m, m.FlinchUntil, now);
                    BroadcastMonsterMessage(conn, id, flinch);
                    return flinch;
                }
            });
            if (checkedAtGate) return;
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
    }

    // A monster's 0x1d1 TakeHit for the recovery from `now` until `until` (its FlinchUntil when that is still the hit's),
    // written in its send gate (under conn.Monsters): every hit's and a Rampage stun's (RampageStunPacket). On a lockstep
    // client that recovery runs in the client's frames from the frame being sent, so FlinchUntil becomes the end of the
    // frame it ends in (LockstepRecoveryEnds), and the AI orders the monster from the next frame on, where an order starts
    // at the frame's start as any other does. An order sent in the end frame itself would start in the monster's own
    // tick, after the player's: its chase would test his cell after his step there, which the server knows only once that
    // frame is complete (a body that blocks the step, or an order of his placed later in the frame, changes it). The cost
    // is one frame's stand after the recovery; the frame GGG orders in after a TakeHit is not traced.
    // A walker on frames is stopped here, in the frame the client gets the 0x1d1 in, not where the hit was committed
    // (HitReaction.TryFlinch): a projectile's output goes out after its commit, often a frame later, and the client walks
    // until then.
    public static byte[] TakeHitPacket(ClientState conn, uint id, SpawnedMonster m, double until, double now)
    {
        float seconds = (float)(until - now);
        if (m.Walk.OnFrames(now)) m.Walk.Cancel(now);
        if (conn.LockstepClient && conn.LockstepFrame is { } frame && m.FlinchUntil == until)
            m.FlinchUntil = LockstepRecoveryEnds(frame, seconds);
        return InstanceProtocol.ActorAnimation(id, seconds, PlayerHitRecovery.TakeHitAnimation);
    }

    // The end of the frame in whose tick a lockstep client ends a monster's TakeHit recovery of `seconds`, its 0x1d1
    // written in `frame`. Meanwhile the monster starts nothing: the 0x1d1's handler (0x141e9c700) sets Actor +0x219 & 2,
    // the action gate (0x141e941a0) refuses every start while it is set, and an order that comes meanwhile waits in the
    // Actor (0x141e937b0 keeps it at +0x1c0). The monster's own tick counts each frame's time (33 ms as float seconds)
    // against the duration, through Actor +0x26c (0x141e957f0) or the end of the clip fit to the duration (0x141e99c50),
    // and in the frame that uses it up 0x141e99d50 clears the mask and starts the waiting order (0x141e94900). The
    // 0x1d1's own frame does not count: bench 2026-11-05, a 1.0 s recovery ended 31 frames after it (orders sent 30
    // frames after it were seen still waiting after their own frame's step, none of 114 sent 31 after). Not traced: the
    // rate the countdown multiplies the frame's time by (+0x1a0's +0x1f8; a chill may slow it) and a TakeHit the
    // client refuses (0x141e9c700 returns early during some actions), for which the AI waits all the same.
    public static double LockstepRecoveryEnds((double Start, double End) frame, float seconds) =>
        LockstepCountdownFrames(seconds) is { } frames ? frame.Start + (frames + 1) * (frame.End - frame.Start) : double.PositiveInfinity;

    // How many frames' time (33 ms as float seconds each) uses up a duration the client counts down in its ticks; null
    // for a count the frame's time no longer changes (an infinite or huge duration), which never ends on the client.
    public static int? LockstepCountdownFrames(float seconds)
    {
        float left = seconds, length = FrameMs / 1000f;
        int frames = 0;
        for (; left > 0; frames++)
        {
            if (left - length == left) return null;
            left -= length;
        }
        return frames;
    }

    // "Gain X% of Missing Unreserved Life before being Hit by an Enemy" (PoB CalcDefence: only while alive, capped at the
    // recoverable Life). Life reservation is not modelled, so unreserved Life is maximum Life. A blocked hit still counts:
    // blocking does not prevent the defender's when-hit effects (wiki Blocking). Call under the Monsters lock.
    public static void GainLifeBeforeEnemyHitLocked(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0 || conn.PlayerLife >= conn.PlayerMaxLife
            || Stats.Key("missing_unreserved_life_%_gained_as_life_before_hit") is not uint key
            || stats.GetValueOrDefault(key) is not (> 0 and var pct)) return;
        var leech = LeechOf(conn);
        conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, conn.PlayerLife,
            (conn.PlayerMaxLife - conn.PlayerLife) * (double)pct / 100.0, ref leech.LifeInstantCarry, minimum: 1);
    }

    // A swing order written in its send gate at `now` (under conn.Monsters): when it starts (LockstepOrderStart), its
    // attack time, the monster's and the player's cells there, and the monster's state from it; null when the monster
    // cannot act. A generic swing's action speed is read at `now`, not at the order's start: a freeze or chill sent
    // earlier in the frame reaches the client before the order applies.
    public static (double StartedAt, double Seconds, (int X, int Y) From, (int X, int Y) Player, ushort Sequence)? MonsterSwingOrderLocked(
        ClientState conn, SpawnedMonster m, MonsterAttack a, int action, bool generic, double seconds, double now,
        ushort sequence = MonsterActionSequence)
    {
        double startedAt = LockstepOrderStart(conn, now);
        if (generic)
        {
            double acceptedFactor = MonsterActionFactor(m, now);
            if (acceptedFactor <= 0) return null;
            seconds = MonsterSwingSeconds(conn, m, now) / acceptedFactor;
        }
        m.BusyUntil = startedAt + seconds + conn.LockstepActionTail;
        if (a.Summon is { } summoning) m.SummonReadyAt = startedAt + summoning.CooldownSeconds;
        m.SwingGeneration = action;
        m.SwingOrderedAt = startedAt;
        // An in-place action does not touch a lockstep client's walker: a walk under way goes on under it. The Actor
        // stops that walk when it ends the action, with the stop that waits for the tick, so the walk ends with the
        // next frame's steps. The action's own frame counts toward its time here (unlike a hit's recovery): a 0.99 s
        // attack ordered in frame n was ended in n + 30 and its walk stood from n + 31 (tick trace 2026-11-10, two of
        // two; only that duration is traced).
        m.ClientActionWalkStop = conn.LockstepClient && (!generic || a.InPlace) && m.Walk.Moving(startedAt)
            && LockstepCountdownFrames((float)seconds) is { } frames ? startedAt + frames * (FrameMs / 1000d) : null;
        // The accepted action owns the sequence that its later hit must name (0x14219aa10, 0x141e977f0).
        // Assign it after admission and carry it with the order, including through a projectile's flight.
        if (conn.LockstepClient && a.Summon is null) sequence = m.NextSequence();
        return (startedAt, seconds, m.Walk.Position(startedAt), conn.Walk.Position(startedAt), sequence);
    }

    public static async Task MonsterSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int action, MonsterAttack? use = null, bool area = false, double areaRadius = 0, ushort? sequence = null,
        double? begunAt = null)
    {
        if ((use ?? m.Attack) is not { } a) return;
        // Entry protection: a protected player is not attacked (the planner also skips hostile monsters meanwhile).
        if (!CurrentGameWork || InGracePeriod(conn)) return;
        // A swing the client's own chase action began when its walk-up got there (a lockstep client, PlanMonsterAi): no
        // order goes out, the clip is the chase order's, and the contact counts from the start of the frame it began in.
        if (begunAt is { } begun && use is null && !area)
        {
            double begunSeconds;
            ushort begunSeq;
            lock (conn.Monsters)
            {
                double factor = MonsterActionFactor(m, begun);
                if (factor == 0) return;
                begunSeconds = MonsterSwingSeconds(conn, m, begun) / factor;
                begunSeq = m.Sequence;
            }
            if (ServerTrace.Ai) Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) swings at the player from its walk-up, begun {conn.Clock.Elapsed.TotalSeconds - begun:0.000} s ago, attack time {begunSeconds:0.00} s");
            double begunContact = (m.Variety is { } v ? SkillsFor(conn).MonsterContact(v.Id, "Melee", begunSeq)?.ContactFraction : null) ?? 0.5;
            if (!await WaitMonsterActionTime(conn, id, m, action, begun + begunSeconds * begunContact)) return;
            await MonsterContact(remote, stream, ciphers, conn, id, m, action, begunSeq, a);
            return;
        }
        // Its attack time now: a chill slows it by its action speed, Devout of Gruthkul by the player's attacker slow.
        // A King skill carries its own cast time. Predictive keeps its existing sequence selection;
        // lockstep assigns the actor's sequence when the send gate admits the action below.
        double seconds;
        ushort seq;
        if (use is null && !area)
        {
            lock (conn.Monsters)
            {
                double factor = MonsterActionFactor(m, ProjectileWorldTime(conn));
                if (factor == 0) return;
                seconds = MonsterSwingSeconds(conn, m, ProjectileWorldTime(conn)) / factor;
            }
            seq = sequence ?? MonsterActionSequence;
        }
        else
        {
            seconds = a.Seconds;
            seq = sequence ?? conn.NextActionSequence();
        }
        double startedAt = 0;
        (int X, int Y)? projectileAim = null;
        bool walkUp = use is null && !area && !a.InPlace;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (MonsterSwingOrderLocked(conn, m, a, action, use is null && !area, seconds, AreaTime(conn), seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            var (from, player) = (order.From, order.Player);
            // The client-drawn straight mortar retains the action order's aim throughout its windup
            // (141ba7c30 -> 141b6c630). Reacquiring here at release would turn a dodged shot toward the player.
            if (a.Projectile is { Variation: > 0, Lobbed: false }) projectileAim = player;
            if (walkUp && !conn.LockstepClient) seq = m.NextSequence();
            a = MonsterAttackForSequence(conn, m, a, seq);
            // A summoning skill's action (ActionTypes SummonSpecificMonstersInFront) plays nothing on the client (live
            // 2026-11-01: the Immolator stayed shut): its actor's clip is played instead (0x1d1, SummonAnimation).
            if (a.Summon is not null) return InstanceProtocol.ActorAnimation(id, (float)seconds, SummonAnimation);
            return walkUp ? InstanceProtocol.ActionStartedNow(id, from.X, from.Y, player.X, player.Y, a.SkillId, seq, 0, PlayerId)
                : InstanceProtocol.ActionStarted(id, from.X, from.Y, player.X, player.Y, a.SkillId, seq, conn.MonsterOrderFlags, targetObjectId: PlayerId);
        }, a.Summon is not null ? $"0x01d1 monster 0x{id:x} ({m.Label}) summons, cast time {seconds:0.00} s"
            : $"0x01ab monster 0x{id:x} ({m.Label}) attacks the player, attack time {seconds:0.00} s")) return;
        // The contact: the clip's keyframe (the Spitter's attack1 spits at 37.5 % of the attack; GGG's melee hits land by
        // their clip too), half the attack time without rig data.
        double contact = a.Release?.Fraction ?? (walkUp && m.Variety is { } variety
            ? SkillsFor(conn).MonsterContact(variety.Id, "Melee", seq)?.ContactFraction : null) ?? 0.5;
        if (conn.LockstepClient && a.Projectile is { Variation: > 0, Lobbed: false } nativeProjectile)
        {
            // Like the player's native shot, the monster emits at the end of the frame that crosses contact.
            // Starting between frames lets the server hit a moving player before the visible flight reaches them.
            LaunchMonsterProjectile(remote, stream, ciphers, conn, id, m, a, nativeProjectile, action, projectileAim,
                LockstepShotAt(startedAt, seconds * contact), seq);
            return;
        }
        if (!await WaitMonsterActionTime(conn, id, m, action, startedAt + seconds * contact)) return;
        if (a.Summon is { } summon)
        {
            await MonsterSummon(remote, stream, ciphers, conn, id, m, summon);
            return;
        }
        if (a.Projectile is { } projectile)
        {
            // The shot leaves at the contact and flies; its hit lands when it reaches the player (Program.ProjectileHostile.cs).
            LaunchMonsterProjectile(remote, stream, ciphers, conn, id, m, a, projectile, action, projectileAim, sequence: seq);
            return;
        }
        if (area) { await MonsterAreaContact(remote, stream, ciphers, conn, id, m, a, action, areaRadius, seq); return; }
        await MonsterContact(remote, stream, ciphers, conn, id, m, action, seq, a);
    }

    /// <summary>Animation.dat <c>SpellAreaOfEffect</c>, which a summoner's actor plays as it summons (the Immolator's
    /// <c>.act</c>: <c>spawn</c>, its doors opening and shutting).</summary>
    public const byte SummonAnimation = 22;

    /// <summary>How many of its monsters a summoner keeps alive at once (our number: the data has no cap; the original's
    /// Immolators keep bringing skeletons while the player is near).</summary>
    public const int MaxSummoned = 5;

    // Under the Monsters lock: whether a monster's summoning skill may be used now (off cooldown, fewer than MaxSummoned
    // of its monsters alive); any other attack may.
    public static bool SummonReadyLocked(ClientState conn, uint id, SpawnedMonster m, MonsterAttack a, double now) =>
        a.Summon is null || now >= m.SummonReadyAt && conn.Monsters.Values.Count(s => s.SummonerId == id && !s.Dead && s.Life > 0) < MaxSummoned;

    // A summoning skill's contact: its monsters (normal, at the summoner's level) where the skill puts them, its offset
    // ahead of the summoner towards the player (summon_specific_monsters_in_front_offset); hostile, as their summoner's,
    // and giving no experience or items when the skill says so.
    public static async Task MonsterSummon(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, SkillCatalog.MonsterSummonInfo summon)
    {
        if (Monsters.Find(summon.Variety.ToString()) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) summons MonsterVarieties row {summon.Variety}: it cannot be created");
            return;
        }
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (from, player) = (m.Walk.Position(now), conn.Walk.Position(now));
            double dx = player.X - from.X, dy = player.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy), ahead = summon.Offset / WorldUnits.PerCell;
            at = length < 1 ? from : ((int)Math.Round(from.X + dx / length * ahead), (int)Math.Round(from.Y + dy / length * ahead));
        }
        for (int k = 0; k < summon.Count; k++)
            await SpawnMonster(remote, stream, ciphers, conn, variety, m.Level, MonsterRarity.Normal, at.X, at.Y, alone: true,
                owned: (_, summoned) => { summoned.SummonerId = id; summoned.NoRewards = summon.NoRewards; });
        Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) summons {summon.Count} {variety.Name} at ({at.X}, {at.Y})");
    }

    /// <summary>An area skill's contact: the player takes the hit when they still stand in the aimed area. The area is
    /// centred on the target cell captured when the action began; moving out of it dodges the hit.</summary>
    public static async Task MonsterAreaContact(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, MonsterAttack a, int action, double radius, ushort sequence = 0)
    {
        if (sequence == 0) sequence = conn.NextActionSequence();
        (int X, int Y) aim = default;
        double startedAt = 0;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            startedAt = conn.Clock.Elapsed.TotalSeconds;
            var from = m.Walk.Position(startedAt);
            aim = conn.Walk.Position(startedAt);
            m.BusyUntil = startedAt + a.Seconds;
            m.SwingGeneration = action;
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, a.SkillId, sequence, conn.MonsterOrderFlags, targetObjectId: PlayerId);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses area skill 0x{a.SkillId:x4}, radius {radius:0.#}")) return;
        if (!await WaitMonsterActionTime(conn, id, m, action, startedAt + a.Seconds * 0.5)) return;
        MonsterHitOutcome? outcome = null;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            if (InGracePeriod(conn)) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var here = conn.Walk.Position(now);
            foreach (var golem in conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).ToArray())
                if (!golem.Monster.Dead && Dist(golem.Monster.Walk.Position(now), aim) <= radius)
                    GolemTakeHitLocked(conn, golem, id, m, a, now, area: true);
            double dx = here.X - aim.X, dy = here.Y - aim.Y;
            if (dx * dx + dy * dy > radius * radius) return null;
            outcome = CommitMonsterHitLocked(remote, conn, id, m, a, now, here, aim, checkRange: false, sequence: sequence);
            return outcome?.HitPacket;
        }, $"0x01af monster 0x{id:x} area hit on the player")) return;
        if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
    }

    // A melee contact at the clip's keyframe: committed at the actual send gate under the monster's action, so a
    // flinch accepted while it was queued suppresses both the packet and the life mutation. A ranged monster's shot commits
    // the same pipeline when its flight reaches the player instead (Program.ProjectileHostile.cs).
    public static async Task MonsterContact(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, int action, ushort sequence = MonsterActionSequence, MonsterAttack? use = null)
    {
        if ((use ?? m.Attack) is not { } a) return;
        MonsterHitOutcome? outcome = null;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            // Entry protection: the player standing protected on the arrival cell takes no hit.
            if (InGracePeriod(conn)) return null;
            // The contact time is the later of the projectile runtime's committed time (a buffered input cannot rewrite
            // that history) and the connection clock: the runtime is not advanced at this gate, so its time can lag a
            // clock read the life tick or a mortar landing already made, and the fortification and recent-hit trackers
            // refuse to go backwards (review 2026-10-30: the refusal faulted the swing's task and the hit was lost).
            double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
            // A freeze that landed during the swing interrupts it.
            if (MonsterActionFactor(m, now) == 0)
            {
                Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) frozen: its swing is cancelled");
                return null;
            }
            outcome = CommitMonsterHitLocked(remote, conn, id, m, a, now, conn.Walk.Position(now), m.Walk.Position(now), checkRange: true, sequence: sequence);
            return outcome?.HitPacket;
        }, $"0x01af monster 0x{id:x} hit on the player (kind 3, sequence 0x{sequence:x4})")) return;
        if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
    }

    /// <summary>The monster hit packet's flags: generic particles (0x4000), main hand (0x0200) and blood (0x0001) always,
    /// 0x0040 for a blocked hit and 0x0020 for a critical strike (action-hit-packet-3.29.3b.md §2).</summary>
    public static ushort MonsterHitFlags(bool blocked, bool critical) =>
        (ushort)(0x4201 | (blocked ? 0x0040 : 0) | (critical ? 0x0020 : 0));

    /// <summary>What a monster's hit on the player did (CommitMonsterHitLocked), for the packets that present it.</summary>
    public sealed record MonsterHitOutcome(byte[] HitPacket, uint Life, uint Mana, uint ManaBefore, int Damage, int RawDamage,
        bool Blocked, bool Critical, PlayerRecoveryIntent? Recovery, RecentEnemyHits.Decision Armour, bool Spirit = false);

    // The monster's hit on the player, committed under the Monsters lock at the moment it lands: reach (melee only),
    // evasion, the mod-rolled packet, block, armour, resistances, ailments, recovery and death. Null when it misses or is
    // evaded. `from` is where the hit comes from: the monster for a swing, the shot's contact point for a projectile.
    public static MonsterHitOutcome? CommitMonsterHitLocked(string remote, ClientState conn, uint id, SpawnedMonster m, MonsterAttack a,
        double now, (int X, int Y) here, (int X, int Y) from, bool checkRange, ushort sequence = MonsterActionSequence)
    {
        // A retired producer is refused before recording an accepted hit or touching any current resource state.
        if (!CurrentGameWork) return null;
        int priorContacts = conn.RecentEnemyHits.Count(now);
        using var defenceTime = FortificationOf(conn).Capture(now);
        // Resolve lazy final defences here, never at AI collection or before a queued echo/pause wait.
        var stats = CharacterPlayerStats(conn);
        int StatOf(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var reduction = PlayerDamageReduction(conn, stats);
        if (checkRange && Dist(here, from) > a.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} missed: the player is {Dist(here, from):0.0} away");
            return null;
        }
        // The monster's accuracy comes from DefaultMonsterStats at its level. Evasion uses PoE's shared entropy
        // counter: every attack adds its hit chance, and reaching 100 consumes 100 and hits (evasion cannot streak
        // indefinitely). A new counter starts after 3.33 s without an attack.
        int baseAccuracy = m.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0);
        int monsterAccuracy = MonsterBlindRating(conn, id, m, baseAccuracy, now, stats);
        int evasion = TimedBlind.Apply(PlayerEvasionBase(conn, now), PlayerBlindValue(conn, stats).Less);
        int hitChance = DamageModel.AccuracyHitChance(evasion, monsterAccuracy);
        if (!a.Spell && EvadesLocked(conn, hitChance, now))
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) evaded: accuracy {monsterAccuracy} " +
                $"vs evasion {evasion}, {hitChance}% chance to hit, entropy {conn.EvasionEntropy:0}");
            return null;
        }
        GainLifeBeforeEnemyHitLocked(conn, stats);
        // Roll the whole monster-mod packet (the Gambler's incoming luck included), then mitigate each component
        // with this hit's one armour decision.
        var gambler = GamblerFor(stats);
        // The King's Mystic Fetishes leave Portent Vapors: 10% increased damage taken per stack from his hits only.
        var hitDamage = a.Damage;
        if (m.EncounterStats is { } encounter) hitDamage = MonsterModEffects.AddedTo(hitDamage, encounter);
        if (KingInMists.IsBoss(m.Variety) && conn.KingPortentStacks > 0 && now < conn.KingPortentUntil)
            hitDamage = hitDamage.Scale(1 + 0.1 * conn.KingPortentStacks);
        var rolled = gambler.RollIncomingHit(hitDamage, conn.IncomingHitRandom);
        rolled = MonsterModHit(m, rolled, conn.PlayerLife, conn.PlayerMaxLife, conn.IncomingHitRandom, out bool monsterCritical,
            // Overwhelm's aura lowers the multiplier of a monster in it: -30 base_critical_strike_multiplier_+.
            () => SelfCriticalReduction(conn) - EnemyAuraStatLocked(conn, m, here, now, "base_critical_strike_multiplier_+"),
            cannotCrit: VaalOversoul.CannotCrit(m, a));   // its crit, Executioner's
        // The player's passive enemy auras (Born under Fire): the hit and its ailment base convert alike, so ignite
        // sees the converted damage. Converting does not remove stun, so the hit recovery below reads a.Damage.
        var baseHit = a.Damage;
        if (EnemyAuraConversionLocked(conn, m, here, now, StatOf) is { } auraConversion)
            (rolled, baseHit) = (rolled.Convert(auraConversion), baseHit.Convert(auraConversion));
        // Glacial Wave: less damage dealt is all of it (PoB Damage MORE), so the ignite its crit puts on you too.
        double lessened = m.Ailments.ChillDamageDealtFactor(now);
        if (lessened < 1) (rolled, baseHit) = (rolled.Scale(lessened), baseHit.Scale(lessened));
        // Damage taken as shifts the hit's types before any mitigation. Chill, freeze and shock read the shifted damage
        // taken; ignite still reads the damage dealt (baseHit), as the wiki's Damage taken as says.
        // Then each type taken may be avoided, and ignite reads only the damage dealt that lands.
        var suppression = a.Spell ? Suppression(conn) : default;
        bool suppressed = a.Spell && suppression.Chance > 0
            && DamageModel.Suppresses(suppression.Chance, conn.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
        // "when you Suppress Spell Damage from a Unique Enemy" (Loyalty Tattoo of Ahuana).
        if (suppressed && m.Rarity == MonsterRarity.Unique && conn.PlayerLife > 0)
            LoyaltySuppressedHitLocked(conn, stats);
        var shifted = PlayerDamageTaken.Shift(rolled, StatOf);
        var taken = PlayerDamageTaken.Avoid(shifted, StatOf, conn.IncomingHitRandom, out var avoided);
        baseHit = PlayerDamageTaken.Landed(baseHit, StatOf, avoided);
        var mitigated = gambler.MitigateIncomingHit(taken, type =>
            new DamageModel.ResistanceValue(Resistance(conn, type), MaxResistance(conn, type)), conn.IncomingHitRandom);
        var immune = PlayerImmunities(conn);
        // This remains the original next RNG draw when the feature is absent. Blocking never
        // consumes an armour draw; one lazy decision is shared by the whole rolled packet.
        bool blocked = DamageModel.Blocks(a.Spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance,
            conn.IncomingHitRandom.NextDouble() * 100, a.Spell ? conn.Pools.SpellBlockLucky : conn.Pools.AttackBlockLucky,
            a.Spell ? conn.Pools.SpellBlockUnlucky : conn.Pools.AttackBlockUnlucky);
        bool armourRelevant = mitigated.Components.Any(c => !immune.Contains(c.Type)
            && Math.Round(c.Range.Min) > 0 && PlayerHitMitigation.ArmourPercent(conn.Pools, c.Type, StatOf) > 0);
        var armour = IncomingArmourLocked(conn, !blocked && armourRelevant, priorContacts, stats);
        int rawDamage = (int)Math.Round(taken[DamageType.Physical].Min);
        int damage = 0, chaosDamage = 0, physicalDamage = 0;
        var ailmentTaken = DamagePacket.Empty;
        int blindedPct = DamageTakenFromBlindedPct(conn, id, stats);
        double reservationFactor = ReservationHitTakenFactor(conn, m, now);
        foreach (var (type, range) in mitigated.Components)
        {
            int value = immune.Contains(type) ? 0 : (int)Math.Round(range.Min);
            value = PlayerHitMitigation.AfterResistance(conn.Pools, type, value,
                reduction.Physical, reduction.Elemental, armour.EffectiveArmour,
                PlayerHitMitigation.ArmourPercent(conn.Pools, type, StatOf));
            // Flat damage taken after resistance and armour, then the multipliers: the increased damage taken adds to the
            // shock's, and a blocked hit deals only its share (PlayerDamageTaken). Monster hits are attacks, and melee
            // unless ranged.
            if (!immune.Contains(type)) value = Math.Max(0, value + PlayerDamageTaken.Flat(type, attack: !a.Spell, StatOf));
            if (type == DamageType.Physical)
                value = AntiquarianEffects.RangedPhysical(value, a.Ranged, StatOf);
            value = (int)Math.Round(value * conn.ElementalAilments.DamageTakenFactor(now,
                    blindedPct + PlayerDamageTaken.IncreasedPct(type, melee: !a.Ranged && !a.Spell, StatOf))
                * reservationFactor * ReservationTypedHitTakenFactor(conn, type, now)
                * (blocked ? PlayerDamageTaken.BlockedPct(type, StatOf) / 100.0 : 1));
            // Suppression prevents calculated damage. Armour must see the unsuppressed hit, and flat taken damage
            // belongs to that hit too (as in the synthetic spell-hit path).
            if (suppressed) value = DamageModel.Suppress(value, suppression.Effect);
            // Excommunicate Support: an excommunicated enemy cannot deal chaos damage.
            if (type == DamageType.Chaos && m.ExcommunicatedUntil > now) value = 0;
            ailmentTaken = ailmentTaken.Add(type, new(value, value));
            if (type == DamageType.Chaos) chaosDamage += value;
            if (type == DamageType.Physical) physicalDamage += value;
            damage += value;
        }
        damage = FortificationDamageTaken(conn, damage);
        chaosDamage = FortificationDamageTaken(conn, chaosDamage);
        physicalDamage = Math.Min(FortificationDamageTaken(conn, physicalDamage), Math.Max(0, damage - chaosDamage));
        if (!a.Spell && !a.Ranged && damage > 0)
        {
            int before = damage; damage = GolemSafeguardLocked(conn, damage, now);
            chaosDamage = (int)((long)chaosDamage * damage / before);
            physicalDamage = (int)((long)physicalDamage * damage / before);
        }
        if (damage > 0)
        {
            int full = damage;
            damage = GameEvents.AdjustIncomingDamage(conn, damage);
            chaosDamage = (int)Math.Round((double)chaosDamage * damage / full);
            physicalDamage = Math.Min((int)Math.Round((double)physicalDamage * damage / full), Math.Max(0, damage - chaosDamage));
        }
        // Untiring: avoidance, suppression and what a block keeps out count as prevented; damage taken as another type does not.
        PreventedDamageOf(conn).Add(now, shifted[DamageType.Physical].Min - physicalDamage);
        if (!blocked) m.HitPlayerAt = now;
        uint manaBefore = conn.PlayerMana;
        var onBlock = blocked ? BlockRecovery(conn, stats) : default;
        int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock + onBlock.Life : 0;
        int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock + onBlock.Mana : 0;
        // A blocked hit recoups the share it deals (ailmentTaken holds it).
        RecoupHitLocked(conn, ailmentTaken.Select((_, r) =>
            new(FortificationDamageTaken(conn, (int)r.Min), FortificationDamageTaken(conn, (int)r.Max))), now, stats);
        long lifeBefore = conn.PlayerLife;
        int lifeDamage = DamagePlayerHitTyped(conn, physicalDamage, Math.Max(0, damage - physicalDamage - chaosDamage), chaosDamage, now, stats,
            ailmentTaken);
        uint afterDamage = (uint)Math.Max(0, lifeBefore - lifeDamage);
        conn.PlayerLife = gainedLife > 0
            ? PetrifiedBlood.RecoverLife(conn, afterDamage, gainedLife, ref LeechOf(conn).LifeInstantCarry, minimum: 1)
            : afterDamage;
        conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
        conn.EnergyShield.Recover(onBlock.EnergyShield);
        if (lifeBefore > conn.PlayerLife && (lifeBefore - conn.PlayerLife) * 100 >= (long)conn.PlayerMaxLife * 15)
            QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.SavageHit);
        TrimLeechLocked(conn);
        ApplyPlayerAilmentsLocked(conn, baseHit, ailmentTaken.Select((_, r) =>
            new(FortificationDamageTaken(conn, (int)r.Min), FortificationDamageTaken(conn, (int)r.Max))),
            monsterCritical, blocked, now, stats, source: id);
        VaalOversoul.LaserShock(conn, m, a, ailmentTaken, blocked, now, stats);
        Sirus.OnHit(conn, m, a, baseHit, ailmentTaken, monsterCritical, blocked, now, stats);
        conn.RecentEnemyHits.Record(now);
        conn.RecentEvents.Record(StatConditions.BeenHit, now);
        if (blocked)
        {
            conn.RecentEvents.Record(StatConditions.Blocked, now);
            conn.RecentEvents.Record(StatConditions.BlockedAttack, now);
        }
        if (lifeDamage > 0 && (long)lifeDamage * 100 >= (long)conn.PlayerMaxLife * 15)
            conn.RecentEvents.Record(StatConditions.TakenSavageHit, now);
        ChargesWhenHit(conn, blocked);
        // Brine Bound: a hit that lands, blocked too (blocking keeps the defender's when-hit effects, wiki
        // Blocking), may freeze its monster for 1 s. Its own random keeps the incoming hit's draws in order.
        if (GameServer.Core.World.RandomRolls.Chance(StatOf("chance_to_freeze_enemies_for_1_second_when_hit_%"), 100, Random.Shared))
            m.Ailments.Freeze(1, now, StatOf, name => m.Effects[name], AilmentArea(StatOf, _ => 0, spell: false));
        AilmentsWhenHitLocked(m, StatOf, blocked, now);
        ReservationWhenHitLocked(conn, id, m, blocked, now);
        if (conn.PlayerLife == 0) { DamageTriggers.Remove(conn); conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
        RecordHardcoreDeath(conn);
        bool spirit = m.Rarity == MonsterRarity.Unique
            && SpiritChieftainsWhenHitLocked(conn, stats, blocked, monsterCritical, Math.Max(0, lifeBefore - conn.PlayerLife));
        PlayerRecoveryIntent? recovery = null;
        if (!blocked && !a.Ranged && conn.PlayerLife > 0 && a.Damage.Components.All(c => c.Type == DamageType.Physical))
            recovery = CommitPlayerHitRecoveryLocked(conn, damage, now);
        else if (Stats.Key(AntiquarianEffects.StunRecoverySource) is uint turtle && stats.GetValueOrDefault(turtle) > 0)
            Console.WriteLine($"[gateway] Turtle Shell hit ineligible: block={blocked}, ranged={a.Ranged}, "
                + $"alive={conn.PlayerLife > 0}, types={string.Join(',', a.Damage.Components.Select(c => c.Type))}");
        if (recovery is not null && StatOf("damage_taken_%_recovered_as_life_from_stunning_hits") is > 0 and var recoverPct
            && StatOf("cannot_recover_life") == 0)
        {
            double carry = 0;
            conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, conn.PlayerLife, (long)damage * recoverPct / 100, ref carry);
            TrimLeechLocked(conn);
        }
        uint life = conn.PlayerLife;
        RageWhenHitLocked(conn);
        if (!blocked && conn.PlayerLife > 0 && m.Effects["global_chance_to_blind_on_hit_%"] is > 0 and var blindChance
            && (blindChance >= 100 || conn.IncomingHitRandom.Next(100) < blindChance))
            InflictPlayerBlind(conn, 4 * Math.Max(0, 1 + m.Effects["blind_duration_+%"] / 100d),
                m.Effects["blind_effect_+%"], stats);
        // The hit packet carries this outcome's presentation and is processed at once (kind 3): 0x4000 particles,
        // 0x0200 main hand, 0x0001 blood, 0x0040 blocked, 0x0020 critical, code = the largest damage component.
        // The 3.29.3b decode (action-hit-packet-3.29.3b.md §§1–4) shows kind 2 only queues the package until an
        // action with that sequence consumes it, which 0xffff never guarantees, and that 0x0400 asks the client for
        // its own stun roll; the player's recovery is the authoritative 0x1d1 (SendPlayerHitRecovery).
        var packet = InstanceProtocol.ActionHit(id, sequence, PlayerId, flags: MonsterHitFlags(blocked, monsterCritical), kind: 0x03,
            code: HitCode(mitigated), angle: HitAngle(here.X - from.X, here.Y - from.Y));
        return new(packet, life, conn.PlayerMana, manaBefore, damage, rawDamage, blocked, monsterCritical, recovery, armour, spirit);
    }

    // The packets after a committed hit: energy shield, native recovery, the life bar and life, mana, death or flask charges.
    public static async Task PresentMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        MonsterHitOutcome hit)
    {
        if (!CurrentGameWork) return;
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        if (!CurrentGameWork) return;
        if (hit.Spirit) await SendSpiritChieftain(remote, stream, ciphers, conn);
        if (!CurrentGameWork) return;
        if (hit.Recovery is not null) await SendPlayerHitRecovery(remote, stream, ciphers, conn, hit.Recovery);
        if (!CurrentGameWork) return;
        // At 0 the Life tick stops refreshing the overhead life bar (see Attack): hide it.
        if (hit.Life == 0)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1");
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, hit.Life, conn.PlayerReservedLife),
            $"0x01b1 player hit by monster 0x{id:x} for {hit.Damage}{(hit.Critical ? " (critical)" : "")}" +
            $"{(hit.RawDamage != hit.Damage ? $" ({hit.RawDamage} physical before {DamageModel.ArmourReduction(hit.Armour.EffectiveArmour, hit.RawDamage)}% armour, effective rating {hit.Armour.EffectiveArmour})" : "")}" +
            $"{(hit.Blocked ? " (blocked)" : "")}, life {hit.Life}/{conn.PlayerMaxLife}{(hit.Life == 0 ? " (dies)" : "")}");
        if (hit.Mana != hit.ManaBefore)
            await SendPlayerMana(remote, stream, ciphers, conn,
                $"0x01b1 player mana {hit.Mana} ({(long)hit.Mana - hit.ManaBefore:+0;-0}{(hit.Blocked ? ", blocked" : "")})");
        if (!CurrentGameWork) return;
        if (hit.Life == 0)
            await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
        else
        {
            if (hit.Blocked) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Block);
            else
            {
                await SendAttackerSlows(remote, stream, ciphers, conn);
                await GainFlaskChargesWhenHit(remote, stream, ciphers, conn);
            }
        }
    }

    public const string AttackerSlow = "enemies_that_hit_you_with_attack_recently_attack_speed_+%";

    // A monster's attack time now. Devout of Gruthkul: an enemy whose attack hit the player in the last 4 s gets the
    // player's current reduced attack speed (Pools.AttackerSlowPct), additive with its own increases.
    public static double MonsterSwingSeconds(ClientState conn, SpawnedMonster m, double now) =>
        m.Attack is not { } a ? 0
        : now - m.HitPlayerAt <= RecentlySeconds ? m.Effects.AttackSeconds(a.Seconds, conn.Pools.AttackerSlowPct) : a.Seconds;

    // The attack stats on a monster's Stats component (it carries them from its creation, 11-skills.md §6) again when
    // its attack time changes or goes back, so the client's swing can follow the server's. Called from the player tick
    // and after a hit.
    public static async Task SendAttackerSlows(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var changed = new List<(uint Id, double Seconds, IReadOnlyList<(uint, int)> Stats)>();
        lock (conn.Monsters)
        {
            double now = ProjectileWorldTime(conn);
            bool slows = conn.Pools.AttackerSlowPct != 0;
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Attack is not { } a || m.SwingSecondsSent is null && (!slows || now - m.HitPlayerAt > RecentlySeconds)) continue;
                double seconds = MonsterSwingSeconds(conn, m, now);
                if (seconds == (m.SwingSecondsSent ?? a.Seconds)) continue;
                m.SwingSecondsSent = seconds == a.Seconds ? null : seconds;
                changed.Add((id, seconds, (a with { Seconds = seconds }).Stats()));
            }
        }
        foreach (var (id, seconds, stats) in changed)
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, stats),
                $"0x01b3 monster 0x{id:x} attack time {seconds:0.000} s (enemies that hit you recently)");
    }
}
