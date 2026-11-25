using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamageTypes = GameServer.Core.World.DamageTypes;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    // Cached level-scaled native stats for ordinary monster skills (avoids re-interpolating per monster in a pack;
    // RuleTable releases on hot reload).
    static readonly RuleTable<SkillCatalog, ConcurrentDictionary<(int Effect, byte Level), IReadOnlyDictionary<uint, int>?>> MonsterNativeSkillStats = new();

    static IReadOnlyDictionary<uint, int>? NativeMonsterSkillStats(SkillCatalog catalog, int effect, byte level) =>
        effect < 0 ? null : MonsterNativeSkillStats.GetValue(catalog, _ => new()).GetOrAdd((effect, level), key =>
            GolemData.TrySkill(catalog, key.Effect, key.Level, out var native) ? native.Stats : null);

    static int NativeMonsterStat(IReadOnlyDictionary<uint, int>? stats, uint key) =>
        stats is not null ? stats.GetValueOrDefault(key) : 0;

    /// <summary>The distance (cells) at which a monster first notices a player: its variety's MinAgroRange when that
    /// is a real notice distance (0 &lt; Min &lt;= the monster's AggroRange), else its AggroRange. Official captures: zombies
    /// with a MinAgroRange of 40 first move towards the player at 36.6-39.9 cells.</summary>
    public static int MonsterNoticeRange(SpawnedMonster m) =>
        m.Variety is { MinAgroRange: > 0 and var min } && min <= m.AggroRange ? min : m.AggroRange;

    /// <summary>The player distance (cells) past which a chasing monster lets go: its AggroRange (MaxAgroRange) when
    /// its variety has a real notice distance, else twice its AggroRange. Official captures: no chase is re-issued past
    /// MaxAgroRange (106-108 cells).</summary>
    public static int MonsterLeashRange(SpawnedMonster m) =>
        m.Variety is { MinAgroRange: > 0 and var min } && min <= m.AggroRange ? m.AggroRange : 2 * m.AggroRange;

    /// <summary>Ranged spell engagement reach in cells: scales a 60-cell base by native
    /// <c>spell_maximum_action_distance_+%</c> and caps unmodified spells to on-screen range (40 cells for projectiles
    /// like bow monsters, 28 cells for non-projectile area spells like BatScreech), instead of firing across the full
    /// 105–135 cell MaxAgroRange. The Maven and Nucleus keep their arena-wide aggro reach.</summary>
    public static double MonsterSpellReach(SkillCatalog catalog, MonsterCatalog.Variety variety, int effect, int actionDistancePct)
    {
        double maxReach = Math.Max(variety.AttackRange, variety.AggroRange);
        if (MavenNucleus.Is(variety.Id)) return maxReach;
        double scaled = 60.0 * Math.Max(0.2, 1 + actionDistancePct / 100.0);
        if (actionDistancePct <= 0) scaled = Math.Min(scaled, catalog.IsProjectile(effect) ? 40.0 : 28.0);
        return Math.Clamp(scaled, variety.AttackRange, maxReach);
    }

    /// <summary>Base damage for an ordinary monster's spell from its level-scaled GrantedEffectStatSets
    /// (<c>spell_minimum/maximum_base_*_damage</c>, preserving elemental/chaos types so player resistances apply);
    /// falls back to the variety's physical base damage when the effect carries no spell base damage.</summary>
    public static DamagePacket MonsterSpellBaseDamage(IReadOnlyDictionary<uint, int>? nativeStats, int fallbackMin, int fallbackMax,
        double mapBossDamage = 1.0)
    {
        if (nativeStats is not null)
        {
            var damage = DamagePacket.Empty;
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = NativeMonsterStat(nativeStats, Stat.Of($"spell_minimum_base_{slug}_damage"));
                int max = NativeMonsterStat(nativeStats, Stat.Of($"spell_maximum_base_{slug}_damage"));
                if (max > 0) damage = damage.Add(type, new(min, Math.Max(min, max)));
            }
            if (!damage.IsZero)
            {
                int more = NativeMonsterStat(nativeStats, Stat.ActiveSkillDamagePlusPctFinal);
                double factor = Math.Max(0, 1 + more / 100.0) * mapBossDamage;
                return factor == 1.0 ? damage : damage.Scale(factor);
            }
        }
        return DamagePacket.Of(DamageType.Physical, fallbackMin, fallbackMax);
    }

    // A monster's combat granted effect (MavenNucleus.CombatEffect: skip teleports/daemons). Weapon-polymorphic Melee
    // still counts as ranged when the variety's art is a bow or wand.
    public static bool MonsterAttackIsRanged(SkillCatalog skills, MonsterCatalog.Variety variety, int effect) =>
        MavenNucleus.AttackIsRanged(skills, effect) || variety.RangedWeapon && skills.IsAttack(effect);

    // A projectile skill's own stats go to the client with the attack's numbers: the client derives none itself
    // (11-skills.md §4), and without them a Sand Spitter's action faces its target like a melee swing instead of turning
    // its back to spit (`action_should_face_away_from_target`, with `projectile_uses_contact_position`, the spread and
    // minimum range from the stat set). The attack's numbers win on a shared key; the effect's cast-time and mana-cost
    // entries, which an attack action does not use, stay out.
    public static readonly uint[] MonsterSkillStatsNotSent =
        [Stat.BaseSpellCastTimeMs, Stat.VirtualBaseSpellCastTimeMs, Stat.HundredTimesCastsPerSecond, Stat.BaseManaCost, Stat.ManaCost];

    // A monster record's skill_behaviour_variation: its effect's SkillID, as the client's own skill-stat builder adds it
    // (SkillCatalog.BehaviourVariation), unless the record already says. Projectile records keep their own rule above
    // (MonsterProjectileSkillStats: a mortar's spell forms are sent without it, the server keeping their objects).
    public static IReadOnlyList<(uint, int)> WithBehaviourVariation(Player player, int effect, IReadOnlyList<(uint, int)> stats) =>
        SkillsFor(player).BehaviourVariation(effect) is int variation and > 0 && stats.All(s => s.Item1 != Stat.SkillBehaviourVariation)
            ? [.. stats, (Stat.SkillBehaviourVariation, variation)]
            : stats;

    // Native projectile actions read final counts from the actor record. The raw base/additional stats alone do
    // not emit a shot. Keep simulated mortars on their own publication path.
    public static IReadOnlyList<(uint, int)> MonsterActionSkillStats(Player player, int effect, int level, MonsterAttack attack,
        MonsterModEffects? effects = null)
    {
        var stats = ActionSkillStats(player, effect, level, attack, effects);
        if (!attack.Proxy) return stats;
        var totem = TotemFlags.ToHashSet();
        return [.. stats.Where(s => !totem.Contains(s.Item1))];
    }

    /// <summary>The flags that make a skill a totem's summon (SpellTotems.TotemProxy clears the same).</summary>
    static readonly uint[] TotemFlags = [Stat.IsTotem, Stat.BaseSkillIsTotemified, Stat.SkillIsTotemified];

    static IReadOnlyList<(uint, int)> ActionSkillStats(Player player, int effect, int level, MonsterAttack attack,
        MonsterModEffects? effects = null)
    {
        var catalog = SkillsFor(player);
        IReadOnlyList<(uint, int)> Art(IReadOnlyList<(uint, int)> stats) =>
            catalog.ArtVariation(effect) is int art and > 0 && Stat.SkillArtVariation is var key
                && stats.All(s => s.Item1 != key) ? [.. stats, (key, art)] : stats;
        if (attack.Projectile is not null) return Art(MonsterProjectileSkillStats(player, effect, level, attack));
        var own = Art(WithBehaviourVariation(player, effect, attack.SkillStats()));
        if (catalog.EffectId(effect) == "InsectSuicideExplosionHuge") return EssenceInsectSkillStats(player, attack, own, effects);
        if (!attack.Ranged) return own;
        var raw = catalog.EffectStats(effect, level);
        int Get(uint key) => raw.FirstOrDefault(s => s.Item1 == key).Item2;
        int count = Get(Stat.NumberOfProjectilesOverride) is > 0 and var fixedCount ? fixedCount
            : Math.Max(1, Get(Stat.BaseNumberOfProjectiles)) + Get(Stat.NumberOfAdditionalProjectiles)
                + (effects?.AdditionalProjectiles ?? 0);
        count = Math.Max(1, count);
        own = [.. own, (Stat.TotalNumberOfProjectilesToFire, count), (Stat.VirtualTotalNumberOfProjectiles, count),
            (Stat.TotalNumberOfArrowsToFire, count), (Stat.VirtualTotalNumberOfArrows, count)];
        var taken = own.Select(s => s.Item1).ToHashSet();
        foreach (uint key in MonsterSkillStatsNotSent) taken.Add(key);
        return [.. raw.Where(s => !taken.Contains(s.Item1)), .. own];
    }

    public static IReadOnlyList<(uint, int)> MonsterProjectileSkillStats(Player player, int effect, int level, MonsterAttack attack)
    {
        var own = attack.InPlaceSkillStats();
        // A mortar attack is the client's own: its action fires only with a count on the record and draws the MonsterMortar
        // row skill_behaviour_variation names (StatKeys). GGG's base Spitter records carry row 183 and the three counts at
        // its skill's 1, on lockstep and predictive, and its server creates no projectile object; the Gateway's then sends
        // none (MonsterProjectileProfile). The count is the skill's (SkillCatalog.MonsterProjectile), so the client draws as
        // many as the server lands.
        if (attack.Projectile is { Variation: > 0 } mortar)
            own = [.. own, (Stat.SkillBehaviourVariation, mortar.Variation), (Stat.TotalNumberOfProjectilesToFire, mortar.Count),
                (Stat.VirtualTotalNumberOfProjectiles, mortar.Count), (Stat.VirtualTotalNumberOfArrows, mortar.Count)];
        var taken = own.Select(s => s.Item1).ToHashSet();
        foreach (uint key in MonsterSkillStatsNotSent) taken.Add(key);
        return [.. SkillsFor(player).EffectStats(effect, level).Where(s => !taken.Contains(s.Item1)), .. own];
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
    public static MonsterRelease? MonsterReleaseFor(Player player, MonsterCatalog.Variety variety, int effect, int level,
        SkillCatalog.MonsterProjectileInfo projectile, ushort sequence = MonsterActionSequence)
    {
        var catalog = SkillsFor(player);
        var stats = catalog.EffectStats(effect, level);
        bool Has(uint name) => name is var key && stats.Any(s => s.Item1 == key && s.Item2 != 0);
        // A weapon-stanced monster (a Rogue Exile) plays its stance's clip for the skill's own row: Igna Phoenix's
        // Fireball is the Staff stance's SpellProjectileFire, not a root pool. The stance clips carry no position.
        if (SkillAnimations.IsStanced(variety.Id) && SkillAnimations.Key(catalog, effect, DualWields(variety)) is { } key
            && SkillAnimations.StanceClip(variety.Id, SkillAnimations.StanceName(variety.Weapons), key, sequence) is { ContactFraction: double at } stanced)
            return new(stanced.Animation, at, null, Has(Stat.ActionShouldFaceAwayFromTarget));
        var clip = catalog.MonsterContact(variety.Id, projectile.AnimationKey ?? "Melee", sequence);
        if (clip is null) return null;
        var point = Has(Stat.ProjectileUsesContactPosition) ? clip.Point : null;
        if (point is { } p && Has(Stat.UseScaledContactOffset)) point = p.Scaled(variety.ModelScale);
        return new(clip.Animation, clip.ContactFraction ?? 0.5, point, Has(Stat.ActionShouldFaceAwayFromTarget));
    }

    /// <summary>When a weapon-stanced monster's (a Rogue Exile's) kit skill lands, as a fraction of its action: the contact
    /// of the clip its stance plays for the skill's Animation row (SkillAnimations; Leap Slam's hit_ground), chosen with the
    /// action's sequence as the client chooses it. <paramref name="key"/> names the row instead (a walk-up swing's
    /// Melee). Null for any other monster, or without a pool for the row.</summary>
    public static double? StanceContact(Player player, SpawnedMonster m, MonsterAttack a, ushort sequence, string? key = null) =>
        StanceClipOf(player, m, a, sequence, key)?.ContactFraction;

    /// <summary>The clip a stanced monster plays for the attack (StanceContact); null for one with no stances.</summary>
    public static SkillCatalog.MonsterClipContact? StanceClipOf(Player player, SpawnedMonster m, MonsterAttack a, ushort sequence,
        string? key = null)
    {
        if (m.Variety is not { } variety || !SkillAnimations.IsStanced(variety.Id)) return null;
        key ??= a.Effect is int effect ? SkillAnimations.Key(SkillsFor(player), effect, DualWields(variety)) : null;
        return key is null ? null : SkillAnimations.StanceClip(variety.Id, SkillAnimations.StanceName(variety.Weapons), key, sequence);
    }

    /// <summary>It holds a weapon in each hand: its off hand is no shield (ItemStances 10) or quiver (9).</summary>
    // A second weapon in the off hand: not a shield or a quiver (stances 9, 10), nor an entry that is no weapon (stance 0xff:
    // a bow, which is held in that hand, MonsterCatalog's bow records).
    public static bool DualWields(MonsterCatalog.Variety variety) => variety.Weapons.Any(w => w.Slot == 1 && w.Stance is not (9 or 10 or 0xff));

    // Off-hand attack slot on a monster variety: includes dual-wielded weapons and off-hand items whose ItemClass has no
    // stance (e.g. Cannibal ThrowFire's torch, ItemClasses 36 Unarmed: Stance 0xff, Type 37 != 0x68 NoWeaponType). Client
    // off-hand/mortar actions (FUN_141b71790) read OffHandTotalBaseWeaponAttackDurationMs (0x221b); when 0, the animation
    // runs at speed 0.0 and permanently freezes the monster on its first throw while subsequent 0x1ab orders queue forever.
    public static bool HasOffHandWeapon(MonsterCatalog.Variety variety) =>
        variety.Weapons.Any(w => w.Slot == 1 && w.Stance is not (9 or 10) && w.Type != 0x68);

    // The native selector seeds the clip from this action (0x141e9fa80). Keep its contact and launch point together;
    // another action may start on the actor while this shot is in flight. Scripted releases retain their own recipe.
    public static MonsterAttack MonsterAttackForSequence(Player player, SpawnedMonster monster, MonsterAttack attack, ushort sequence) =>
        attack is { ReleaseEffect: int effect, Projectile: { } projectile } && monster.Variety is { } variety
            ? attack with { Release = MonsterReleaseFor(player, variety, effect, monster.Level, projectile, sequence) }
            : attack;

    // Creates one monster: registers its type at this level (0x20, once per connection), creates it (0x2bb) and puts it in
    // the wake zone (0x1a9), and keeps it for the AI. /spawn_mob, the area's packs (PopulateArea) and the player's minions
    // (extra stats on its Stats component; owned runs under the lock that adds it) use it. Its object id.
    public enum MonsterPlacement { Proposed, Established }

    // SavedMonster's cell is established world state. The native Positioned reader (141e323f0) copies XY;
    // it does not run a nearest-clear search. This is shared by normal re-entry and population replay.
    // A planned one (SavedMonster.Planned) is created as the area's population creates its monsters: at a clear cell
    // by its planned one, with its mods rolled now. bulk: see SpawnMonster.
    public static Task<uint> RestoreMonster(Player player, MonsterCatalog.Variety variety, SavedMonster saved, bool bulk = false) =>
        saved.Planned
            ? SpawnPackMonster(saved.PackId, player, variety, saved.Level, saved.Rarity, saved.X, saved.Y,
                log: false, alone: false, bulk: bulk)
            : WasItemisedCorpse(saved) ? PlaceItemisedCorpse(player, variety, (saved.X, saved.Y), saved.Level, saved.Mods)
            : SpawnPackMonster(saved.PackId, player, variety, saved.Level, saved.Rarity, saved.X, saved.Y,
                friendly: !saved.Hostile, log: false, damagedLife: saved.Life, mods: saved.Mods,
                unrisen: saved.Unrisen, placement: MonsterPlacement.Established, bulk: bulk);

    // An area's monsters are all placed at its first entry (14-monster-packs.md §2). One placed far from the player is
    // held back (InstanceWorld.HeldBackMonsters): it is the instance's, counted and saved with it, but it is not
    // created yet, so it is on no client and nothing acts on it or sends about it. RevealHeldBack creates it when a
    // player comes near: up to PopulationRevealPerPass nearest per pass within PopulationRevealCells (a screen is about
    // 50 cells across), and up to PopulationShownPerPass from the outer ring out to PopulationShowCells, the wake
    // zone's half-side, so walking into dense packs or teleporting into a full region never stalls a
    // frame and none is created only to be put to sleep.
    public const int PopulationRevealCells = 120;
    public const int PopulationShowCells = WakeZoneHalfSide;
    public const int PopulationRevealPerPass = 12;
    public const int PopulationShownPerPass = 6;

    // A unique or a monster with a boss bar is never held back: fights and quest scripts address it wherever the player
    // stands, and KeepWakeZone wakes a far boss (SpawnMonster, farBoss).
    static bool HeldBackAt(MonsterCatalog.Variety variety, MonsterRarity rarity, bool hostile, (int X, int Y) at, (int X, int Y) player) =>
        hostile && rarity != MonsterRarity.Unique && !variety.BossHealthBar && Dist(at, player) > PopulationRevealCells;

    /// <summary>The monsters the area still holds back: all hostile and alive. In the area's frame.</summary>
    public static IReadOnlyList<SavedMonster> HeldBackLocked(Player player) =>
        (IReadOnlyList<SavedMonster>?)player.Instance?.World.HeldBackMonsters ?? [];

    /// <summary>Creates the held-back monsters a player of the instance has come near. The runner's
    /// SyncInstanceMonsters calls it, so they are created on the client whose messages the others mirror; the other
    /// clients' next pass creates them there.</summary>
    public static async Task RevealHeldBack(Player player)
    {
        var held = player.Instance.World.HeldBackMonsters;
        List<SavedMonster> due = [];
        if (held.Count == 0 || !CurrentGameWork || player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        double now = AreaTime(player);
        (int X, int Y)[] players = [.. InGame.Keys.Where(c => ReferenceEquals(c.Instance, player.Instance)
            && !c.Closed && !c.LeavingArea && c.Connection.PlayerSent).Select(c => c.Walk.Position(now))];
        if (players.Length == 0) return;
        List<(SavedMonster Held, double Distance)> near = [];
        List<(SavedMonster Held, double Distance)> ring = [];
        foreach (var h in held)
        {
            double d = double.MaxValue;
            foreach (var playerAt in players) d = Math.Min(d, Dist((h.X, h.Y), playerAt));
            if (d <= PopulationRevealCells) near.Add((h, d));
            else if (d <= PopulationShowCells) ring.Add((h, d));
        }
        if (near.Count > 0)
            due.AddRange(near.OrderBy(r => r.Distance).Take(PopulationRevealPerPass).Select(r => r.Held));
        int ringBudget = Math.Min(PopulationShownPerPass, PopulationRevealPerPass - due.Count);
        if (ringBudget > 0 && ring.Count > 0)
            due.AddRange(ring.OrderBy(r => r.Distance).Take(ringBudget).Select(r => r.Held));
        if (due.Count == 0) return;
        // By identity: two planned monsters of one kind on one cell are equal records.
        var leaving = new HashSet<SavedMonster>(due, ReferenceEqualityComparer.Instance);
        held.RemoveAll(leaving.Contains);
        int created = 0;
        try
        {
            // They leave the list before they are among the monsters: the overlay's count waits for the last of them.
            using var recount = HoldMonstersRemaining(player);
            for (; created < due.Count; created++)
                if (Monsters.Find(due[created].Variety.ToString()) is { } variety
                    && await RestoreMonster(player, variety, due[created], bulk: true) == 0) break;
        }
        finally
        {
            // What was not created (this connection is going) stays the instance's: the next runner's pass creates it.
            if (created < due.Count) held.AddRange(due.Skip(created));
            player.Connection.Performance.Count("held-back reveal passes");
            player.Connection.Performance.Count("held-back monsters created", created);
        }
        // A kill counted while the count was held.
        await SendRemainingMonsters(player);
    }

    // Keep the signature used by mods built against 0.6.0-beta.
    public static Task<uint> SpawnMonster(Player player, MonsterCatalog.Variety variety,
        byte level, MonsterRarity rarity, int x, int y, string? name, bool friendly, bool log, uint? damagedLife,
        MonsterModSet? mods, bool alone, bool? unrisen, float rotation,
        IReadOnlyList<(uint Key, int Value)>? extraStats, Action<uint, SpawnedMonster>? owned, Func<bool>? ownedCurrent,
        IReadOnlyList<GrantedSkill>? ownedSkills, uint? ownedMaximumLife, uint ownedMaximumEnergyShield, uint ownedEnergyShield,
        MonsterModSet? extraMods, bool scripted, IReadOnlyList<VisibleItem>? ownedLooks, Func<bool>? ownedAdmission,
        bool ownedInitialDeath, MonsterPlacement placement, int? lifePercent, MonsterMovement? movement,
        (uint Parent, string Bone, byte Kind, byte Flags)? attachTo, bool instanceOwned, bool bulk, bool nonBlocking) =>
        SpawnMonster(player, variety, level, rarity, x, y, name, friendly, log, damagedLife, mods, alone, unrisen, rotation,
            extraStats, owned, ownedCurrent, ownedSkills, ownedMaximumLife, ownedMaximumEnergyShield, ownedEnergyShield,
            extraMods, scripted, ownedLooks, extraKit: null, ownedAdmission, ownedInitialDeath, placement, lifePercent,
            movement, attachTo, instanceOwned, bulk, nonBlocking);

    // The area's type set is shared. Every existing viewer needs the registration before
    // an ordinary spawn or a restored summon publishes a create using that type.
    static async Task EnsureMonsterTypeSent(Player player, MonsterCatalog.Variety variety, byte level,
        Func<bool>? ownedCurrent = null)
    {
        if (player.MonsterTypes.Contains((variety.Row, level))) return;
        string typeLog = $"0x0020 monster type {variety.Id}@{level} (row {variety.Row})";
        if (player.Area.Owned)
        {
            if (CurrentGameWork && !player.Closed && !player.LeavingArea
                && (ownedCurrent is null || ownedCurrent()))
                await RegisterMonsterType(player, (ushort)variety.Row, level, typeLog);
        }
        else
        {
            byte[]? typeBytes = null;
            await Send(player, [], typeLog, atGate: () =>
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea
                    || (ownedCurrent is not null && !ownedCurrent())
                    || !player.MonsterTypes.Add((variety.Row, level)))
                    return null;
                typeBytes = InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]);
                return typeBytes.ToArray();
            });
            if (typeBytes is not null)
                foreach (var peer in player.Frames.Players())
                    if (!ReferenceEquals(peer, player) && !peer.Closed && !peer.LeavingArea)
                        await Send(peer, typeBytes.ToArray(), typeLog);
        }
    }

    public static Task<uint> SpawnMonster(Player player, MonsterCatalog.Variety variety,
        byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true, uint? damagedLife = null,
        MonsterModSet? mods = null, bool alone = true, bool? unrisen = null, float rotation = 0,
        IReadOnlyList<(uint Key, int Value)>? extraStats = null, Action<uint, SpawnedMonster>? owned = null, Func<bool>? ownedCurrent = null,
        IReadOnlyList<GrantedSkill>? ownedSkills = null,
        uint? ownedMaximumLife = null, uint ownedMaximumEnergyShield = 0,
        uint ownedEnergyShield = 0,
        MonsterModSet? extraMods = null, bool scripted = false, IReadOnlyList<VisibleItem>? ownedLooks = null, IReadOnlyList<MonsterKitSkill>? extraKit = null,
        Func<bool>? ownedAdmission = null, bool ownedInitialDeath = false,
        MonsterPlacement placement = MonsterPlacement.Proposed, int? lifePercent = null,
        MonsterMovement? movement = null, (uint Parent, string Bone, byte Kind, byte Flags)? attachTo = null, bool instanceOwned = false,
        // An area's population (PopulateArea) is created in bulk: its create carries the wake and its glow, and the
        // producer does not wait for the birth, which on a lockstep client is a frame per monster.
        bool bulk = false, bool nonBlocking = false) =>
        SpawnPackMonster(0, player, variety, level, rarity, x, y, name, friendly, log, damagedLife, mods, alone, unrisen,
            rotation, extraStats, owned, ownedCurrent, ownedSkills, ownedMaximumLife, ownedMaximumEnergyShield,
            ownedEnergyShield, extraMods, scripted, ownedLooks, extraKit, ownedAdmission, ownedInitialDeath, placement,
            lifePercent, movement, attachTo, instanceOwned, bulk, nonBlocking);

    static async Task<uint> SpawnPackMonster(int packId, Player player, MonsterCatalog.Variety variety,
        byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true, uint? damagedLife = null,
        MonsterModSet? mods = null, bool alone = true, bool? unrisen = null, float rotation = 0,
        IReadOnlyList<(uint Key, int Value)>? extraStats = null, Action<uint, SpawnedMonster>? owned = null, Func<bool>? ownedCurrent = null,
        IReadOnlyList<GrantedSkill>? ownedSkills = null,
        uint? ownedMaximumLife = null, uint ownedMaximumEnergyShield = 0,
        uint ownedEnergyShield = 0,
        MonsterModSet? extraMods = null, bool scripted = false, IReadOnlyList<VisibleItem>? ownedLooks = null, IReadOnlyList<MonsterKitSkill>? extraKit = null,
        Func<bool>? ownedAdmission = null, bool ownedInitialDeath = false,
        MonsterPlacement placement = MonsterPlacement.Proposed, int? lifePercent = null,
        MonsterMovement? movement = null, (uint Parent, string Bone, byte Kind, byte Flags)? attachTo = null, bool instanceOwned = false,
        // An area's population (PopulateArea) is created in bulk: its create carries the wake and its glow, and the
        // producer does not wait for the birth, which on a lockstep client is a frame per monster.
        bool bulk = false, bool nonBlocking = false)
    {
        movement ??= MonsterMovement.Default;
        // A hostile unique (a Rogue Exile, a kept one too) holds the weapons its variety names, as Izaro's are given.
        if (ownedLooks is null && !friendly && rarity == MonsterRarity.Unique && variety.Weapons.Count > 0) ownedLooks = variety.Weapons;
        uint id;
        // Under the lock the projectile visuals and ground art take ids under: minions spawn from their own loop.
        if (!CurrentGameWork || player.Closed || player.LeavingArea || ownedCurrent is not null && !ownedCurrent()) return 0;
        // The instance's own monsters (not a player's minions or allies; a monster's summons are) take the instance's ids: every client has
        // them under the same id, and whoever runs the instance next keeps them (SharedMonsters.cs).
        id = !friendly || instanceOwned ? player.Instance.World.NewMonsterId() : player.TakeObjectId();
        int bodySize = Math.Clamp(variety.ObjectSize - 1, -1, 127);
        if (placement == MonsterPlacement.Proposed && bodySize >= 0)
        {
            bool Floor(int cx, int cy) => player.Layout.WalkableFor(cx, cy, bodySize) && !player.Barrier.Contains((cx, cy));
            if (CollisionEscape.Find((x, y), player.Layout.GridW, player.Layout.GridH, Floor,
                player.MonsterPlacementBlockers(id), moverSize: bodySize) is { } clear) (x, y) = clear;
        }
        // A monster that rises from the ground starts waiting to rise, unless it was kept after rising (MonsterEmerge.cs).
        var emerge = Emerges.Kind(variety, rarity);
        bool waits = emerge != EmergeKind.None && (unrisen ?? true);
        // Its mods (MonsterMods.cs): the kept ones, else rolled for its rarity. They set its life, hit, speed and defences.
        bool freshMods = mods is null;
        if (!friendly || ownedSkills is null || mods is null)
            mods = MonsterModsFor(player, variety, level, rarity, mods, alone);
        // Passive species mods (ModsKeys + Special_ModsKeys + area-tier Part1/Part2/Endgame_ModsKeys) compose into
        // Effects once per monster (tracked by SpeciesApplied so SavedMonster re-entry never stacks them again).
        if (!friendly && MonsterSpeciesModsEnabled && !mods.SpeciesApplied)
        {
            bool isMap = MapDevices.IsMapArea(player.Layout.Hash);
            int tier = Monsters.AreaTier(player.Layout.Id, player.Layout.Hash, isMap);
            var speciesRows = MonsterCatalog.SpeciesModsForArea(variety, tier);
            var nextEffects = mods.Effects;
            if (speciesRows.Count > 0)
            {
                var speciesEffects = MonsterModRolls.SpeciesEffects(speciesRows, AreaRng(player));
                if (speciesEffects.Ids.Count > 0)
                    nextEffects = nextEffects.Plus(speciesEffects);
            }
            mods = mods with { Effects = nextEffects, SpeciesApplied = true };
        }
        // A hostile unique keeps its auras up (a Rogue Exile's Hatred, Haste): their stats are its own from the spawn, and
        // a kept one keeps them in its mods.
        var auras = !friendly && rarity == MonsterRarity.Unique ? MonsterSkills.Auras(SkillsFor(player), variety, level) : default;
        if (freshMods && auras.Stats is { Count: > 0 }) mods = mods with { Effects = mods.Effects.Combine(auras.Stats) };
        // Additional encounter scaling composes with rolled rarity and map effects.
        if (extraMods is not null) mods = mods with { Effects = mods.Effects.Plus(extraMods.Effects) };
        if (!friendly && MapModsOf(player) is { } mapMods && MapMonsterEffects(mapMods) is { Count: > 0 } mapStats)
            mods = mods with { Effects = mods.Effects.Combine(mapStats) };
        // The party bonus (Game.PartyBonus): each player beyond the first adds to the monsters' life by their rarity.
        if (!friendly && PartyMonsterLife(player, rarity) is int party and > 0)
            mods = mods with { Effects = mods.Effects.Combine(new Dictionary<uint, int> { [Stat.MaximumLifePlusPct] = party }) };
        // Full life: the client derives the base from the level (MonsterCatalog.BaseLife); the mods' maximum goes on its Stats.
        // A unique map boss also has the map's and the map-boss table's life and damage increases (MonsterMapBossDifficulty
        // and MonsterMapDifficulty): level 83 is 20.73× life, 1.15× damage.
        bool mapBoss = rarity == MonsterRarity.Unique && (variety.BossHealthBar || MapScarabBosses.IsBoss(variety) || MapBeyond.IsBoss(variety))
            && MapDevices.IsMapArea(player.Layout.Hash);
        if (mapBoss && MapModsOf(player) is { } bossMap)
            mods = mods with { Effects = mods.Effects.Combine(MapBossEffects(bossMap)) };
        var mapBossScaling = mapBoss ? Monsters.MapBossScaling(level) : (Life: 1.0, Damage: 1.0);
        int baseLife = Monsters.BaseLife(variety, level) ?? 100;
        if (mapBoss) baseLife = (int)Math.Round(baseLife * mapBossScaling.Life);
        uint life = (uint)mods.Effects.MaxLife(baseLife, level);
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
        var catalog = SkillsFor(player);
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
        if (friendly && owned is not null && rangedAttack) projectile ??= catalog.MonsterProjectile(effect, includeWeaponAttacks: true);
        if (projectile is not null)
        {
            int extraProj = mods.Effects.AdditionalProjectiles;
            double aoeScale = mods.Effects.AreaScaleFactor;
            if (extraProj > 0 || aoeScale != 1.0)
            {
                int nextCount = Math.Max(1, projectile.Count) + extraProj;
                double nextSpread = nextCount > 1 && projectile.SpreadRadians == 0 ? 0.45 : projectile.SpreadRadians;
                projectile = projectile with
                {
                    Count = nextCount,
                    SpreadRadians = nextSpread,
                    AreaRadius = projectile.AreaRadius > 0 ? projectile.AreaRadius * aoeScale : projectile.AreaRadius,
                };
            }
        }
        // A summoning skill (the Immolator's) brings its monsters whenever the player is within its aggro range, at its
        // own cast time; it deals no hit.
        var summon = variety.GrantedEffects is [int summonEffect, ..] ? MonsterSummonOf(catalog, summonEffect, level) : null;
        // A totem whose body would have nothing to do is no summon: the monster would cast and nothing would rise.
        if (summon is { Effect: >= 0 } && BossKits.BodyAttack(player, Monsters.Find(summon.Variety.ToString()), level, summon.Effect, 0x4000) is null) summon = null;
        // A monster whose effect is a spell (the King's Death Effigy, fetish hazards) needs cast-time stats,
        // not the melee attack duration; the client crashed in its cast-duration path when the actor record carried only
        // attack stats (0xc0000005, live 2026-10-31).
        bool spell = effect >= 0 && catalog.EffectStats(effect, level)
            .Any(s => s.Item1 == Stat.CastingSpell && s.Item2 != 0);
        // Level-scaled native stats for ordinary monster skills (spell elemental base damage, cast/attack speed
        // modifiers, spell_maximum_action_distance_+%, cooldown). Cached per (effect, level).
        var nativeStats = !kingActor && !brine && !atlas && effect >= 0
            ? NativeMonsterSkillStats(catalog, effect, level) : null;
        // A ranged attack reaches its variety's MaximumAttackDistance (a Sand Spitter's 30): the monster walks up to that
        // before it shoots, as GGG's do. A ranged spell uses MonsterSpellReach (capped to on-screen range and scaled by
        // spell_maximum_action_distance_+% instead of the 105–135 cell MaxAgroRange); summoners and Maven/Nucleus keep
        // their aggro reach. An encounter that knows better gives its own share of the attack distance (MonsterMovement.Reach).
        double RangedAttackReach()
        {
            int maxDist = effect >= 0
                ? catalog.EffectStats(effect, level).FirstOrDefault(s => s.Item1 == Stat.MainHandBaseMaximumAttackDistance).Item2
                : 0;
            return maxDist > 0 ? Math.Max(variety.AttackRange, Math.Min(variety.AggroRange, maxDist)) : variety.AttackRange;
        }
        double reach = movement.Reach is double share ? variety.AttackRange * share
            : summon is not null ? Math.Max(variety.AttackRange, variety.AggroRange)
            : rangedAttack && !kingActor && spell ? MonsterSpellReach(catalog, variety, effect, NativeMonsterStat(nativeStats, Stat.SpellMaximumActionDistancePlusPct))
            : rangedAttack && !kingActor ? RangedAttackReach()
            : variety.AttackRange;
        double swing = !kingActor && effect >= 0 && catalog.IsSpell(effect) && catalog.CastTimeMs(effect) > 0
            ? catalog.CastTimeMs(effect) / 1000.0 : variety.AttackMs / 1000.0;
        double attackSeconds = summon is { CastSeconds: > 0 } ? summon.CastSeconds
            : spell && !kingActor && !brine && !atlas
                ? mods.Effects.CastSeconds(swing,
                    NativeMonsterStat(nativeStats, Stat.BaseCastSpeedPlusPct) + NativeMonsterStat(nativeStats, Stat.CastSpeedPlusPct),
                    NativeMonsterStat(nativeStats, Stat.ActiveSkillCastSpeedPlusPctFinal))
            : !kingActor && !brine && !atlas && nativeStats is not null
                ? mods.Effects.AttackSeconds(swing,
                    NativeMonsterStat(nativeStats, Stat.AttackSpeedPlusPct),
                    NativeMonsterStat(nativeStats, Stat.ActiveSkillAttackSpeedPlusPctFinal))
            : mods.Effects.AttackSeconds(swing, spell);
        double cooldownSeconds = !kingActor && !brine && !atlas && summon is null && effect >= 0 && (rangedAttack || spell)
            ? catalog.CooldownMs(effect, catalog.MonsterEffectLevel(effect, level)) / 1000.0 : 0;
        var baseDamage = !kingActor && !brine && !atlas && spell
            ? MonsterSpellBaseDamage(nativeStats, baseMin, baseMax, mapBoss ? mapBossScaling.Damage : 1.0)
            : DamagePacket.Of(DamageType.Physical, baseMin, baseMax);
        // The King's two forms carry the official MeleeAtAnimationSpeedUnique ids (0x4001 humanoid, 0x4000 treant);
        // 0x0102 is the player/generic default attack and has no matching clip on their rigs.
        ushort skillId = KingInMists.IsBoss2(variety) ? KingInMists.Boss2AttackSkill
            : KingInMists.IsBoss3(variety) ? KingInMists.Boss3AttackSkill
            : summon is null && !kingActor && effect >= 0 && catalog.ActionHash(effect) is ushort hash ? (ushort)(hash & 0x3fff)
            : SkillCatalog.FirstSkillId;
        var attack = new MonsterAttack(skillId, attackSeconds, reach, mods.Effects.Hit(baseDamage, level))
        {
            Spell = spell,
            Effect = effect >= 0 ? effect : null,
            CooldownSeconds = cooldownSeconds,
            Scripted = kingActor || brine || atlas,
            // A weapon/item in the off hand, not a shield (ItemStances 10), quiver (9), or bow's left-hand model (Type 0x68).
            // Check variety.Weapons too: dual-wielding monsters like Overseer Krow equip both hands on the client and alternate hands.
            DualWielding = (ownedLooks ?? variety.Weapons).Any(l => l.Slot == 1 && l.Stance is not (9 or 10 or 0xff))
                || (ownedLooks is null && HasOffHandWeapon(variety)),
            Ranged = rangedAttack,
            Projectile = projectile,
            Summon = summon,
            Storm = effect >= 0 ? catalog.MonsterStorm(effect, level) : null,
            LaunchFrames = effect >= 0 ? catalog.LaunchFrames(catalog.EffectId(effect)) : null,
            ReleaseEffect = projectile is null || effect < 0 ? null : effect,
            Release = projectile is null || effect < 0 ? null : MonsterReleaseFor(player, variety, effect, level, projectile),
        };
        if (IsEssenceExplosiveInsect(variety.Id)) attack = EssenceInsectAttack(player, level, skillId, mods.Effects, EssenceInsectEffectFor(variety.Id));
        var skill = effect >= 0
            ? catalog.FindByRow(attack.SkillId, effect, level,
                MonsterActionSkillStats(player, effect, level, attack, mods.Effects)) : null;
        // A boss with stages also has the skill that changes them (Hillock's sword extract, BossFights). A scripted
        // encounter drives its own phases: the built-in stage would heal it and fight the controller for its actions.
        var stage = scripted || atlas ? null : BossFights.For(variety.Id);
        // Do not add skill_is_non_pathing: 0x141e94900 skips cancelling the running MoveTo (0x141e96fe0) for non-pathing
        // skills, so a chasing boss would keep walking through its stage animation on the client and snap back after.
        // The stage sends ActionStartedNow (0x1ab + 0x1ac) at the monster's own cell so MoveTo cancels the chase, hits
        // goal at once, and starts the stage immediately in both lockstep and predictive modes.
        var stageSkill = skill is not null && stage is not null
            ? SkillsFor(player).FindByRow(StageSkillId, stage.SkillEffect, level, WithBehaviourVariation(player, stage.SkillEffect,
                (attack with { SkillId = StageSkillId, Seconds = stage.CastSeconds }).Stats()))
            : null;
        // The King in the Mists needs its whole granted-effect list on the actor, not only the default attack: each 0x1ab
        // names one of these ids (KingInMists.cs). The three Mystic Fetishes likewise need their hazard list, because the
        // official server sends those actions to the fetish actor.
        var kingSkills = Sirus.SkillsFor(player, variety, level) ?? KitavaActFive.SkillsFor(player, variety, level) ?? KitavaActTen.SkillsFor(player, variety, level) ?? DepravedTrinity.SkillsFor(player, variety, level) ?? VaalOversoul.SkillsFor(player, variety, level) ?? DoedreSewers.SkillsFor(player, variety, level) ?? BrineKing.SkillsFor(player, variety, level, attack) ?? Arakaali.SkillsFor(player, variety, level) ?? IzaroSkills.SkillsFor(player, variety, level, attack, skill) ?? IzaroIdols.SkillsFor(player, variety, level, attack, skill) ?? GoddessOfJustice.SkillsFor(player, variety, level, attack, skill) ?? KingInMists.SkillsFor(player, variety, level, attack)
            ?? AtlasBosses.SkillsFor(player, variety, level, attack)
            ?? (KingInMists.IsFetish(variety) ? KingInMists.FetishSkillsFor(player, variety, level, attack) : null);
        // A monster the campaign places as a boss, one with a bar of its own, or a hostile unique (a Rogue Exile), and that
        // has no encounter controller: it plays the rest of its granted effects through the ordinary AI (BossKits). Ordinary
        // Normal/Magic/Rare multi-skill monsters use the cached MonsterKits template so casters, summoners, chargers, leapers,
        // flicker-strikers and charge-ralliers play their retail kits instead of falling back to MeleeAtAnimationSpeed.
        var kit = scripted || kingSkills is not null ? (IReadOnlyList<MonsterKitSkill>)[]
            : BossKits.Kit(player, variety, level, effect, attack, unique: !friendly && rarity == MonsterRarity.Unique);
        if (kit.Count == 0 && !friendly && !scripted && kingSkills is null && extraKit is not { Count: > 0 })
        {
            var (ordinaryKit, ordinaryAuraStats, ordinaryAuraRows) = MonsterKits.ForOrdinary(player, variety, level, effect,
                mods.Effects, mapBoss ? mapBossScaling.Damage : 1.0, applyAuraStats: freshMods && auras.Stats is null);
            if (ordinaryKit.Count > 0) kit = ordinaryKit;
            if (freshMods && auras.Stats is null && ordinaryAuraStats.Count > 0)
                mods = mods with { Effects = mods.Effects.Combine(ordinaryAuraStats) };
            if ((auras.Rows is null || auras.Rows.Count == 0) && ordinaryAuraRows.Count > 0)
                auras = (auras.Stats ?? new Dictionary<uint, int>(), [.. ordinaryAuraRows]);
        }
        // An extra-only kit (the essence spells on a monster with no kit of its own) starts with the monster's own attack:
        // BossKits.Choose replaces the attack with a kit entry on every pass, so without that entry the essence rare lost
        // its melee and only ever cast the kit's cooldown spells — or, given an instant summon, swung that at nothing.
        if (kit.Count == 0 && extraKit is { Count: > 0 } && skill is not null)
            kit = [new MonsterKitSkill(effect, attack with { SkillId = (ushort)0x4000 }, 0)];
        // A caller's extra specials (a summon's own usable kit, BossKits.SummonKit) append to the built kit.
        kit = BossKits.WithExtra(kit, extraKit);
        // The kit's first entry is the effect the default attack uses, given its own id in the kit: the actor needs that
        // list, not a second copy of the default attack under the hash-derived id. The stage skill stays: it is 0x0103.
        var skills = kingSkills is { Count: > 0 } ? kingSkills
            : kit.Count > 0 ? [.. BossKits.SkillsOf(player, variety, level, kit), .. stageSkill is null ? [] : new List<GrantedSkill> { stageSkill }]
            : skill is null ? null : stageSkill is null ? [skill] : [skill, stageSkill];
        if (ownedSkills is not null) skills = ownedSkills;
        if (skills is not null) skills = Yugul.AddSkills(player, variety, level, skills);
        if (skills is not null) skills = SolarisLunaris.AddSkills(player, variety, level, skills);
        // The effect behind each actor skill id, so a mod's command can play one of the monster's own skills by the
        // GrantedEffects Id the client shows (AtlasBosses.ActorAction). Derived from the actor's actual skills list so
        // scripted bosses (kingSkills), kit skills, stage skills, ownedSkills and added boss skills all match what the
        // client received on the actor.
        IReadOnlyList<(int Effect, ushort Id)> skillIds = skills is null ? []
            : [.. skills.Select(s => catalog.EffectsPerLevel((int)s.EffectsPerLevelRow) is { } ge
                ? (ge.Effect, s.Id) : (-1, (ushort)0)).Where(x => x.Item1 >= 0)];
        List<(uint, int)>? stats = skill is null ? null : [.. attack.Stats()];
        // The final maximum too: the client applies no modifier of its own (as for the player's maximum_life), so without it
        // the life bars kept the base maximum and a unique showed full until its life fell below it (live 2026-10-21).
        foreach (var stat in MonsterModClientStats(mods.Effects, life, baseLife).Concat(extraStats ?? [])) (stats ??= []).Add(stat);
        if (DoedreSewers.IsBoss(variety) || Sirus.IsActor(variety)) (stats ??= []).Add((Stat.HideMiniLifeBar, 1));
        uint hostileMaxEs = owned is null && !friendly ? ComputeMonsterMaxEnergyShield(variety, level, baseLife, life, mods.Effects) : 0;
        uint maximumEnergyShieldValue = owned is null ? hostileMaxEs : NativeEnergyShield(ownedMaximumEnergyShield);
        uint energyShieldValue = maximumEnergyShieldValue == 0 ? 0
            : owned is null ? maximumEnergyShieldValue
            : Math.Min(maximumEnergyShieldValue, NativeEnergyShield(ownedEnergyShield));
        if (maximumEnergyShieldValue > 0 && Stat.MaximumEnergyShield is var maximumEnergyShield)
            (stats ??= []).Add((maximumEnergyShield, (int)maximumEnergyShieldValue));
        bool waitingForBanditChoice = !friendly && BanditChoices.In(player) is { } bandit
            && bandit.Variety == variety.Id && variety.ComponentsOrBase.Contains("NPC") && BanditChoices.Held(player, bandit);
        var states = Sirus.StartStates(variety) ?? DoedreSewers.StartStates(variety) ?? DepravedTrinity.StartStates(variety) ?? (player.Layout.Id == "2_6_15" ? BrineKing.StartStates(variety) : brine ? variety.StateMachine?.Set("rock_mode", 1) : null) ?? MavenNucleus.StartStates(variety) ?? MapBeyond.StartStates(variety) ?? BossKits.StartStates(variety) ?? variety.StateMachine;
        // MonsterObject.Blob() omits Actor's two dead-on-birth bytes, so a create packet must carry life >= 1;
        // a dead-on-arrival owned spawn is born with life 1 and immediately zeroed by LifeUpdate(id, 0).
        uint initialLife = Math.Min(damagedLife ?? life, life);
        bool initialDeath = ownedInitialDeath || (owned is not null && initialLife == 0);
        uint wireLife = Math.Max(1u, owned is null ? life : initialLife);
        ushort animationSeed = (ushort)(((id * 2654435761u) ^ ((uint)variety.Hash16 << 16)) >> 16);
        if (animationSeed == 0) animationSeed = 1;
        var monster = new MonsterObject(x, y, wireLife, variety.ComponentsOrBase, ao, rarity, name, states, BossKits.StartState(variety, SolarisLunaris.StartState(player, variety, MonsterEmerge.CreateState(variety, emerge, waits))), team,
            skills, stats)
        {
            Mods = mods.Mods, NameWords = mods.NameWords, LifeData = variety.LifeData, Rotation = rotation, Equipped = ownedLooks ?? variety.Weapons,
            EnergyShield = energyShieldValue,
            ActorStance = Arakaali.Stance(player, variety),
            AnimationSeed = animationSeed,
            HasDialogue = waitingForBanditChoice,
            Blocking = !nonBlocking && variety.PositionedBlocking,
            Parent = attachTo?.Parent ?? 0, Bone = attachTo?.Bone ?? "<root>",
            AttachKind = attachTo?.Kind ?? EffectObject.AttachWithoutRotation, AttachFlags = attachTo?.Flags ?? 0,
        };
        // A monster type needs a level: 0x20 registers "<Id>@<level>" (once per connection), the 0x2bb type is its hash.
        if (QuestKillCommitOutput is { } output)
            output.Add(new([], "quest phase monster type", Kind: PlayerMinions.OutputKind.MonsterType,
                MonsterType: ((ushort)variety.Row, level)));
        else
        {
            // A type the client already has needs no send, and so no turn at the gate: holding the gate while waiting
            // for the combat lock stalled the next cast behind the monster AI, 297 ms on one spawn (live 2026-11-17).
            // In the area's frame, register via Area.Delta (Kind.Message, WorldOrder.First) so every party member
            // receives 0x0020 before the 0x02bb create (WorldOrder.Last) in the same frame.
            await EnsureMonsterTypeSent(player, variety, level, ownedCurrent);
        }
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        // The client multiplies this monster's pathing speed by its own roll in the variety's range, drawn from [id, type]
        // (MonsterSpeedRoll.cs); the walk estimate uses the same speed so arrival, swing range and contact agree with it.
        int speedRoll = MonsterSpeedRoll.Percent(id, type, variety);
        double walkSpeed = mods.Effects.MovementSpeed(variety.MovementSpeed) * MonsterSpeedRoll.Factor(speedRoll) * movement.SpeedFactor;
        // An owned spawn must not send a native object and then lose its roster callback across reload.
        // Commit the neutral identity with its create/wake bytes at one current send gate.
        bool admitted = false, asleep = false;
        byte[] create = InstanceEntities.CreateObject(id, type, monster.Blob());
        string? createLog = !log ? null :
            $"0x02bb create {rarity}{(friendly ? " friendly" : "")}{(waits ? $" {emerge.ToString().ToLowerInvariant()}" : "")} monster 0x{id:x} {variety}{(name is null ? "" : $" \"{name}\"")} type=0x{type:x8} at ({x}, {y}), life {life}, speed {walkSpeed:0.##}{(speedRoll == 0 ? "" : $" (roll {speedRoll:+0;-0}%)")}, components {string.Join(", ", variety.ComponentsOrBase)}";
        if (owned is not null)
        {
            await SendWorldCreate(player, id, () =>
            {
                using (ProjectileMutation(player))
                {
                    if (!CurrentGameWork || player.Closed || player.LeavingArea || ownedCurrent is not null && !ownedCurrent()) return null;
                    if (ownedAdmission is not null && !ownedAdmission()) return null;
                    AddMonsterLocked(); admitted = true;
                    return create;
                }
            }, createLog, wake: true,
                afterCreation: initialDeath ? () => [InstanceProtocol.LifeUpdate(id, 0)] : null);
            if (!admitted) return 0;
        }
        else
        {
            // A boss or buried monster far from the player (Dominus at the top of the Sceptre, The Faun in The Ledge)
            // stays out of the wake zone until a player comes near: StepAreaWake wakes it at WakeZoneHalfSide (172 cells),
            // long before a buried monster rises at MonsterEmerge.Range (24 cells). Woken at creation far away, the
            // client put its far copy to sleep on its own while the server still had it awake, and a 0x2bc update for
            // it crashed the client at 0x1401297ca (an Actor lookup on an object without components, live 2026-11-05).
            bool farBoss = (variety.BossHealthBar || waits) && FarFromPlayers(player, (x, y));
            // Bulk (an area's population): the create carries its wake and its rarity glow, so the client reads them in the
            // same admission and the producer waits for neither — each waited for a birth otherwise, a frame per object on
            // a lockstep client (746 monsters, 3.2 s). A far boss still wakes when the player comes near.
            ushort glow = MonsterRarityBuffs.Row(rarity) ?? 0;
            if (!await SendWorldCreate(player, id, () => create, createLog,
                wake: bulk && !farBoss, awaitBirth: !bulk)) return 0;
            // A magic or rare monster's glow is a buff of its own (MonsterRarityBuffs.cs), cancelled by the client at death.
            if (glow != 0) player.Area.Buffs.Set(id, new(glow));
            if (farBoss) asleep = true;
            else if (!bulk) await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), !log ? null : $"0x01a9 monster 0x{id:x} enters wake zone");
            if (farBoss && log) Console.WriteLine($"[gateway] {player.Connection.Remote} boss 0x{id:x} {variety} created asleep: far from the player");
        }
        // Its weapons' stance: the client picks it only when the equipment changes (InstanceEntities.MonsterEquipmentUpdate).
        if (InstanceEntities.MonsterEquipmentUpdate(id, monster) is { } stance)
            await SendWorldRecord(player, id, stance, !log ? null : $"0x02bc monster 0x{id:x} equipment (its stance)");
        // A magic or rare monster's glow is a buff of its own (MonsterRarityBuffs.cs), cancelled by the client at death.
        // An area's population sends it with the create, above.
        if (owned is not null && MonsterRarityBuffs.Row(rarity) is ushort ownedGlow) player.Area.Buffs.Set(id, new(ownedGlow));
        if (auras.Rows is { Count: > 0 }) await MonsterSkills.ShowAuras(player, id, auras.Rows, log);
        if (log && mods.Affixes.Any()) Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} mods: {MonsterModRolls.Describe(mods)}" +
            (mods.NameWords.Count > 0 ? $", named {MonsterModRolls.NameOf(mods.NameWords)}" : ""));
        if (log && attack.Projectile is { } shot)
            Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ranged attack projectile {shot.Metadata} ({shot.Speed} units/s{(shot.Lobbed ? ", mortar" : "")})");
        if (owned is null)
            using (ProjectileMutation(player))
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea) return 0;
                AddMonsterLocked();
            }
        void AddMonsterLocked()
        {
            MonsterEmerge.AddLocked(player, id, new SpawnedMonster($"{variety.Name}@{level}", player.NewMonsterWalk(id, x, y, walkSpeed, bodySize, rarity == MonsterRarity.Unique, movement.ThroughBodies),
                variety.AggroRange, Math.Max(2, variety.ObjectSize + 1), life)
            {
                PackId = packId,
                SpawnAnchor = (x, y),
                PresentationChanged = () => { foreach (var p in player.Frames.Players()) MonsterDebuffsChanged(p, id); },
                Asleep = asleep,
                Attack = skill is null && kit.Count == 0 ? null : kit.Count > 0 ? kit[0].Attack : attack,
                Kit = kit, SkillIds = skillIds, Hostile = !friendly, Variety = variety, Level = level, Rarity = rarity, Movement = movement,
                Scripted = scripted || !friendly && atlas,
                NonBlocking = nonBlocking,
                Life = initialLife, Stage = stageSkill is null ? null : stage, States = states,
                HitAnimations = Monsters.TakeHit(variety.Id), Mods = mods, SpeedRoll = speedRoll,
                Threshold = QuestsOf(player).ThresholdOf(player.Layout.Id, variety.Id),
                CannotBeStunned = !friendly && (KingInMists.IsBoss(variety) || scripted || atlas),
                KingPhaseLocked = !friendly && KingInMists.IsBoss2(variety),
                KingGrace = !friendly && KingInMists.IsBoss(variety),
                FetishGrace = !friendly && KingInMists.IsFetish(variety),
                // The three thresholds the Atlas bosses change phase at (a quarter of their life each, three times).
                LifePhases = !friendly && (brine || atlas) ? new([75, 50, 25]) : !friendly && DepravedTrinity.IsBoss(variety) ? new([66, 33]) : !friendly ? (Yugul.Phases(player, variety) ?? Arakaali.Phases(player, variety) ?? Sirus.Phases(variety) ?? KitavaActFive.Phases(variety) ?? KitavaActTen.Phases(variety)) : null,
                EntranceGrace = waitingForBanditChoice || !friendly && (brine && player.Layout.Id == "2_6_15" || Sirus.IsActor(variety) || KitavaActFive.IsActor(variety) || KitavaActTen.IsActor(variety) || DepravedTrinity.IsBoss(variety) || VaalOversoul.IsBoss(variety) || DoedreSewers.IsBoss(variety)
                    || player.Layout.Id == SolarisLunaris.Area && (SolarisLunaris.IsBody(variety) || SolarisLunaris.IsCelestial(variety))),
            }, emerge, waits);
            if (player.Area.Monsters.TryGetValue(id, out var added))
            {
                if (owned is null && !friendly)
                {
                    if (maximumEnergyShieldValue > 0) InitMonsterEnergyShield(player.Area, added, maximumEnergyShieldValue);
                    RegisterMonsterAuraCarrier(player.Area, added);
                }
                if (owned is not null) owned(id, added);
            }
            if (friendly && !instanceOwned) player.MinionOrigins[id] = new(type, monster, (ushort)variety.Row, level, rarity);
            else
            {
                player.Instance.World.MonsterOrigins[id] = new(type, monster, (ushort)variety.Row, level, rarity);
            }
        }
        if (waits) WatchBuried(player);
        // A kept monster that was hit: its life as a hit sets it (0x1b1).
        if (owned is null && damagedLife < life)
            await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, damagedLife.Value), !log ? null : $"0x01b1 monster 0x{id:x} life {damagedLife}/{life}");
        if (!friendly && KingInMists.IsBoss(variety)) KingInMists.Begin(player, id);
        // A monster the area did not have until now (a summon, a quest's, a kept instance's): the overlay's count changes.
        await SendRemainingMonsters(player);
        return id;
    }

    // The area's monsters, once the player is in (Agents/docs/protocol/14-monster-packs.md): MonsterPacks scattered over the
    // walk map, the room points' packs and the unique boss at "mapboss" (AreaPopulation). Towns have none; neither does an
    // area without a walk map. Game_MONSTERS=0 turns it off.
    public static async Task PopulateArea(Player player)
    {
        var layout = player.Layout;
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0" || CaptureTour.Active(CharacterName(player))) return;
        // An entry without a walk map cannot place packs yet. Do not mark the shared instance populated: a later
        // entry with its map ready must still place them. An established save (even empty) needs no new walk map.
        if (player.Instance.Monsters is null && layout.Id != Sirus.Area && layout.Walkable is null) return;
        if (Monsters.Error is { } catalogError)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} area monsters: {catalogError}");
            return;
        }
        // Another player already runs this instance's monsters: this client shows theirs (SharedMonsters.cs).
        if (!ClaimMonsterHost(player)) return;
        // The packs go in one monster at a time, and the overlay's count waits for the last of them (the entry's 0x1a0,
        // Messages.cs): a recount per spawn would be a count of the area per monster.
        using var recount = HoldMonstersRemaining(player);
        // The Crux of Nothingness fight places its three Mystic Fetishes at fixed offsets from the room's mapboss point
        // (KingInMists.cs). Keep that point on the connection so the treant form uses the same origin as the humanoid,
        // and so a kept instance recovers it before its saved monsters are restored.
        player.KingOrigin = null;
        if (layout.Id == "AzmeriLeagueBoss"
            && AreaPopulation.FitRooms(layout.Id, player.Seed, [], "data").Points.FirstOrDefault(p => p.Name == "mapboss") is { } mapboss)
            player.KingOrigin = (mapboss.X, mapboss.Y);
        // A capture tour only waits for the recorder: no monsters to attack the character meanwhile.
        if (CaptureTour.Active(CharacterName(player))) return;
        // An instance entered before keeps its monsters as the last connection left them: where they were, with their
        // damage; the dead stay dead (#12).
        if (player.Instance.Monsters is { } saved)
        {
            player.MonstersPlaced = true;
            var arrival = player.Walk.Position(AreaTime(player));
            foreach (var s in saved)
                if (Monsters.Find(s.Variety.ToString()) is not { } kept) continue;
                else if (HeldBackAt(kept, s.Rarity, s.Hostile, (s.X, s.Y), arrival))
                    player.Instance.World.HeldBackMonsters.Add(s);
                else await RestoreMonster(player, kept, s);
            Console.WriteLine($"[gateway] {player.Connection.Remote} area {layout.Id} monsters: {saved.Count} kept from the instance");
            return;
        }
        if (layout.Id == Sirus.Area) { player.MonstersPlaced = true; await Sirus.Populate(player); return; }
        // The packs need the walk map; until it exists nothing is placed, so nothing is kept either.
        if (layout.Walkable is not { } walkable) return;
        player.MonstersPlaced = true;
        if (Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Bosses.Count == 0)) return;
        // Crux of Nothingness is the boss arena, not the maze: the area's `voodoo-king-maze1` pack belongs to the
        // separate maze layout the fight does not build yet.
        if (layout.Id == "AzmeriLeagueBoss") area = area with { Scattered = [] };
        if (Monsters.Error is { } error) { Console.WriteLine($"[gateway] {player.Connection.Remote} area monsters: {error}"); return; }
        var ahead = await PlannedPopulation(player, layout);
        var plan = ahead ?? PlanPopulation(player, layout, area);
        string planned = $"planned {(ahead is null ? "on arrival" : "while the client generated the area")} in {plan.Milliseconds:0} ms";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var spawns = plan.Spawns;
        player.LaterFormPoints.Clear();
        foreach (var (row, cell) in plan.LaterForms)
            if (Monsters.Find(row.ToString()) is { } form) player.LaterFormPoints[form.Id] = cell;
        // Recipe arenas have no captured mapboss point: the held last boss forms next to arrival.
        if (MapBossFights.Current.PhaseOf(layout.Id) is { } phase && !player.LaterFormPoints.ContainsKey(phase.Last))
            player.LaterFormPoints[phase.Last] = (layout.SpawnX + 8, layout.SpawnY);
        foreach (var n in plan.Notes) Console.WriteLine($"[gateway] {player.Connection.Remote} area {layout.Id} monsters: {n}");
        int areaLevel = plan.Area.AreaLevel;
        if (MapRunOf(player) is { Area.Level: > 0 } run)
            areaLevel = run.Area.Level;
        byte level = (byte)Math.Clamp(areaLevel, 1, 100);
        // Only the ones near the player are created now; the rest are the instance's held-back monsters and are created
        // when a player comes near (HeldBackAt).
        var here = player.Walk.Position(AreaTime(player));
        var now = new List<AreaPopulation.Spawn>();
        foreach (var s in spawns)
        {
            if (await MapEssences.Imprison(player, s)) continue;
            if (HeldBackAt(Monsters.Find(s.Variety.ToString())!, s.Rarity, hostile: true, (s.X, s.Y), here))
            {
                player.Instance.World.HeldBackMonsters.Add(new(s.Variety, level, s.Rarity, Hostile: true, s.X, s.Y,
                    Life: uint.MaxValue, Unrisen: true) { Planned = true, PackId = s.PackId });
                continue;
            }
            now.Add(s);
        }
        // A lockstep client's create waits for the frame that carries it (WorldOutput.AwaitBirths): one after another, the
        // packs took a frame each (292 monsters in 9.6 s in The Coast), so they go out together and share frames. Each
        // starts in order, so the ids and rolls are taken in order before its first wait.
        Task<uint> Spawn(AreaPopulation.Spawn s) => SpawnPackMonster(s.PackId, player, Monsters.Find(s.Variety.ToString())!, level,
            s.Rarity, s.X, s.Y, log: s.Rarity is MonsterRarity.Unique or MonsterRarity.Rare, alone: false, bulk: true);
        if (player.Connection.LockstepClient) await Task.WhenAll(now.Select(Spawn).ToList());
        else foreach (var s in now) await Spawn(s);
        if (ServerTrace.Latency)
            Console.WriteLine($"[gateway] {player.Connection.Remote} area {layout.Id} entry: {spawns.Count} monsters ({now.Count} created now, {spawns.Count - now.Count} when a player comes near)");
        int extra = 0;
        if (MapRunOf(player) is { ItemRow: > 0 } opening)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in player.Area.Monsters.Values)
                if (m.Variety is { } v) seen.Add(v.Id);
            foreach (string id in MapBossFights.Current.Witnesses(opening.ItemRow))
            {
                if (!seen.Add(id) || Monsters.Find(id) is not { Supported: true } witness || witness.Id != id) continue;
                extra++;
                await SpawnMonster(player, witness, level, MonsterRarity.Unique,
                    layout.SpawnX + extra * 4, layout.SpawnY, log: true, alone: false);
            }
        }
        if (layout.Id == MavenNucleus.Area)
        {
            var maven = spawns.Select(s => (V: Monsters.Find(s.Variety.ToString())!, s.X, s.Y))
                .FirstOrDefault(s => s.V.Id == MavenNucleus.Maven);
            if (maven.V.Id == MavenNucleus.Maven)
                await SpawnMavenRing(player, MavenNucleus.Phase1Orbs,
                    (maven.X, maven.Y), MavenNucleus.Phase1Radius, level);
        }
        // The league encounters of a new instance (Rogue Exiles, …): their modules place them now, and the instance keeps
        // them with the packs.
        await GameEvents.RunAreaPopulated(new AreaPopulated(player, level, AreaPopulation.PopulationSeed(layout.Hash, player.Seed),
            [.. spawns.Select(s => (s.X, s.Y))]));
        Console.WriteLine($"[gateway] {player.Connection.Remote} area {layout.Id} monsters: created {spawns.Count + extra} at level {level}, {planned}, " +
            $"placed in {watch.ElapsedMilliseconds} ms: " +
            string.Join(", ", spawns.GroupBy(s => s.Pack).Select(g => $"{g.Key} × {g.Count()}")) +
            (extra > 0 ? $", invitation × {extra}" : "") +
            $"; waiting to rise: {MonsterEmerge.Unrisen(player).Count} ({MonsterEmerge.Buried(player).Count} buried)" +
            $"; unique: {string.Join(", ", spawns.Where(s => s.Rarity == MonsterRarity.Unique).Select(s => $"{Monsters.Find(s.Variety.ToString())!.Name} at ({s.X}, {s.Y})").DefaultIfEmpty("none"))}");
    }

    public static async Task<int> SpawnMavenRing(Player player, IReadOnlyList<string> ids, (int X, int Y) centre, int radius, byte level)
    {
        int n = 0;
        foreach (var (id, x, y) in MavenNucleus.Around(ids, centre, radius))
        {
            if (Monsters.Find(id) is not { Supported: true } extra || extra.Id != id) continue;
            n++;
            await SpawnMonster(player, extra, level, MonsterRarity.Unique, x, y, log: true, alone: false);
        }
        if (n > 0)
            Console.WriteLine($"[gateway] {player.Connection.Remote} Maven fight extras: {n} around ({centre.X}, {centre.Y})");
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
        public readonly List<(uint Id, SpawnedMonster Monster, int Action, TotemState.Totem Totem)> DecoySwings = [];
        public readonly List<(uint Id, SpawnedMonster Monster)> BossBars = [];
        public readonly List<PlayerMinions.Attack> MinionSwings = [];
        // What changed on the monsters' chill, freeze and shock (MonsterAilmentChangesLocked), sent before the orders.
        public readonly List<(byte[] Packet, string Log)> Ailments = [];
        // On a lockstep client, the start of the frame the walks were placed in (PlayerWalk.FrameOf at the plan's time).
        public double FrameStart = double.NaN;
    }

    // The connection's own monster AI where its area does not run the game's systems (MonsterAiSystem.cs runs it once a
    // frame for every player otherwise): checked every frame, stepped at the cadence's pace.
    static void StartMonsterAi(Player player, PlayerUpdatesState updates)
    {
        var cadence = new MonsterAiCadence();
        double next = double.NegativeInfinity;
        EveryFrameAsPlayer(player, () => PlayerUpdatesCurrent(player, updates) && player.PendingTransfer is null,
            async player =>
            {
                double now = updates.Schedule.Now(player);
                if (AreaRunsGame(player) || now < next) return;
                await StepMonsterAi(player, cadence);
                next = now + cadence.DelayMilliseconds(updates.Schedule.Now(player)) / 1000.0;
            });
    }

    // Kept separate from socket dispatch so all decisions in a pass use the same game time and player pose.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<MonsterCatalog.Variety, bool> BossControllerVarieties = new();

    static bool IsBossControllerVariety(MonsterCatalog.Variety? variety) =>
        variety is not null && BossControllerVarieties.GetOrAdd(variety, static v =>
            Sirus.IsActor(v) || KitavaActFive.IsActor(v) || KitavaActTen.IsActor(v) || DepravedTrinity.IsBoss(v)
            || VaalOversoul.IsBoss(v) || DoedreSewers.IsBoss(v) || BrineKing.IsBoss(v) || Arakaali.IsBoss(v)
            || KingInMists.IsBoss(v) || KingInMists.IsFetish(v) || KingInMists.IsGuidingLight(v) || AtlasBosses.IsBoss(v)
            || Act6To8Encounters.IsBossControllerVariety(v.Id) || Act9To10Encounters.IsBossControllerVariety(v.Id));

    /// <summary>Pack-wide aggro propagation: when one pack member aggros or takes a hit, nearby living hostile
    /// packmates sharing <see cref="SpawnedMonster.PackId"/> alert immediately and unrisen buried/lurking packmates
    /// schedule a staggered emerge wave. In the area's frame.</summary>
    public static void AlertPackLocked(Player player, SpawnedMonster source, (int X, int Y) origin, double now)
    {
        int packId = source.PackId;
        if (packId <= 0 || !source.Hostile) return;
        int alertRadius = Math.Max(60, source.AggroRange);
        double r2 = (double)alertRadius * alertRadius;
        var lurking = player.Area.EmergeGround.Lurking;
        foreach (var (mateId, mate) in player.Area.MonstersNear(origin, alertRadius + 4))
        {
            if (ReferenceEquals(mate, source) || mate.PackId != packId || !mate.Hostile || mate.Dead || mate.Life == 0
                || mate.Asleep || mate.WorldHidden || mate.EntranceGrace || mate.Scripted)
                continue;
            if (lurking.Contains(mateId)) continue;
            if (mate.Aggro) continue;
            var at = mate.Walk.Position(now);
            double dx = at.X - origin.X, dy = at.Y - origin.Y;
            if (dx * dx + dy * dy > r2) continue;
            mate.Aggro = true;
            mate.ReturningHome = false;
            mate.LastTarget = null;
            if (mate.Target is null && player.PlayerLife > 0 && !InGracePeriod(player))
                mate.Target = player;
        }
        if (MonsterEmerge.SchedulePackEmergeLocked(player, packId, origin, now, excludeId: source.ObjectId))
            WatchBuried(player);
    }

    static bool CanLeashReturn(Player player, uint id, SpawnedMonster m) =>
        m.Hostile && !m.Fleeing && m.SpawnAnchor != default
        && m.Rarity != MonsterRarity.Unique && !BossKits.IsCampaignBoss(m.Variety)
        && m.Variety is not { BossHealthBar: true }
        && m.Variety is not { MovementSpeed: 0 } and not { Immobile: true }
        && !m.Scripted && !m.Invulnerable && !m.StageRunning && m.LifePhases is null
        && m.OwnerId is null && m.SummonerId is null && !player.Minions.Owns(id) && !m.TemporarilyAllied;

    static (int X, int Y)? FindBackoffCell(Player player, SpawnedMonster m, (int X, int Y) from, (int X, int Y) threat,
        int minDistance, double maxRange)
    {
        int size = Math.Max(0, LivingCollision.Size(m));
        double away = from == threat ? AreaRng(player).NextDouble() * Math.Tau
            : Math.Atan2(from.Y - threat.Y, from.X - threat.X);
        double desired = Math.Clamp(minDistance + 10.0, minDistance + 4.0, Math.Max(minDistance + 4.0, maxRange - 4.0));
        double currentDist = Dist(from, threat);
        double step1 = Math.Max(4.0, desired - currentDist);
        double step2 = Math.Max(4.0, step1 * 0.6);
        ReadOnlySpan<double> distances = [step1, step2];
        ReadOnlySpan<double> angles = [0, 0.52, -0.52, 1.05, -1.05];
        for (int di = 0; di < distances.Length; di++)
        {
            double dist = distances[di];
            for (int i = 0; i < angles.Length; i++)
            {
                double a = away + angles[i];
                int tx = from.X + (int)Math.Round(Math.Cos(a) * dist);
                int ty = from.Y + (int)Math.Round(Math.Sin(a) * dist);
                if (!LockstepTakesMove(from, (tx, ty))) continue;
                if (Dist((tx, ty), threat) <= currentDist + 2) continue;
                if (!player.Layout.WalkableFor(tx, ty, size) || player.Barrier.Contains((tx, ty))) continue;
                // Ensure the backoff line does not cut through walls or closed doors.
                bool clear = true;
                foreach (var cell in GameServer.Core.World.ClientPathfinder.Line(from, (tx, ty)))
                {
                    if (cell != from && (!m.Walk.Floor(cell.X, cell.Y) || player.Barrier.Contains(cell)))
                    { clear = false; break; }
                }
                if (clear) return (tx, ty);
            }
        }
        return null;
    }

    public static MonsterAiPlan PlanMonsterAi(Player player, MonsterAiCadence cadence, double now)
    {
        var plan = new MonsterAiPlan();
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PendingTransfer is not null) return plan;
            // The instance's ailments and walks are stepped once, by the connection that runs them (SharedMonsters.cs);
            // each monster's AI runs on its target player's connection (MonsterTargetsLocked below).
            bool runner = IsMonsterRunner(player);
            // In an instance the area presents them, to every player (MonsterAilmentSystem.cs).
            if (!player.Paused && runner && !AreaRunsGame(player)) plan.Ailments.AddRange(MonsterAilmentChangesLocked(player, now));
            if (!cadence.TryBeginStep(now, player.AiEnabled && !player.Paused && player.PlayerLife > 0)) return plan;
            // Before any order replaces a walk: its steps since the last pass would go unchecked.
            if (runner) StepWalkers(player, now);
            using var blockers = player.BeginMonsterBlockerFrame(now);
            if (player.Connection.LockstepClient && player.Connection.LockstepFrame is { } sending) plan.FrameStart = Math.Min(now, sending.Start);
            var (px, py) = player.Walk.Position(now);
            // Entry protection: hostile monsters neither chase nor swing while the player stands protected on the arrival cell.
            bool protectedEntry = InGracePeriod(player);
            bool hasRampageWeapons = CurrentRampageWork && player.StandingRampageWorld is { Actors.Count: > 0 };
            var askers = new List<(MonsterSurround.Asker Asker, SpawnedMonster M, double D)>();
            bool crowdedPack = player.Area.Driven && player.Area.SyncLivingMonsters().Count > 16;
            bool playerMoving = player.Walk.Moving(now);
            var decoyChasers = new List<(MonsterSurround.Asker Asker, SpawnedMonster M, TotemState.Totem Totem)>();
            var bandit = BanditChoices.In(player);
            bool banditHeld = bandit is not null && BanditChoices.Held(player, bandit);
            // Only the monsters in the grid cells around the player: one beyond the wake zone sleeps on this client and
            // gets no order (below), and one that targets another player acts on that player's pass. The query is a
            // Euclid radius, so it must reach the square's corner (WakeZoneCorner), not just its half-side.
            foreach (var (id, m) in player.Area.MonstersNear((px, py), WakeZoneCorner + 16))
            {
                if (bandit is not null && m.Variety?.Id == bandit.Variety && m.Hostile)
                {
                    m.EntranceGrace = banditHeld && !m.Aggro
                        && player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) && origin.Object.HasDialogue;
                    if (m.EntranceGrace) continue;
                }
                // Out of the wake zone the client's copy stands frozen (07 §3); the server's stands too (KeepWakeZone
                // ended its walk) and gets no order until it is back, or the return's 0x128 would put the copy wherever
                // the estimate walked meanwhile (live 2026-11-03: 162 cells, in one jump).
                // A lockstep client's monster takes no order before the frame after the one its hit recovery ends in
                // (TakeHitPacket): one sent earlier waits and starts after the player's steps of that frame, or a frame
                // or more after the estimate's walk.
                if (m.Dead || m.Life == 0 || m.OwnerId is not null || m.StageRunning || m.LifePhases?.Active == true || m.WorldHidden || SolarisLunaris.Held(player, m) || m.Scripted || m.Asleep || OwnedFighter(player, id) is not null
                    || !MonsterAiCadence.Due(double.IsNaN(plan.FrameStart) ? now : plan.FrameStart, m.FlinchUntil)) continue;
                // Asleep for this client (its own wake zone, KeepWakeZone): no order and no boss bar about an object the
                // client has put to sleep; the boss life bar for one crashed the client (live 2026-11-05).
                if (MonsterAsleep(player, id)) continue;
                // The King in the Mists has its own phase controller (KingInMists.cs): one action at a time, all its
                // damage through MonsterSwing/MonsterAreaContact. Its Mystic Fetishes are stationary shrines the
                // controller activates; the ordinary chase would slowly drag them toward the player.
                if (IsBossControllerVariety(m.Variety)) continue;
                if (m.Hostile && protectedEntry) continue;
                var (mx, my) = m.Walk.Position(now);
                double d = Math.Sqrt((double)(mx - px) * (mx - px) + (double)(my - py) * (my - py));
                // Leash return walk back to SpawnAnchor: stepped on the runner even after dropping its player target.
                if (m.ReturningHome && !m.Aggro)
                {
                    if (!runner || !CanLeashReturn(player, id, m) || m.Walk.MovementBlocked(now))
                    {
                        if (runner) m.ReturningHome = false;
                        continue;
                    }
                    double homeDist = Dist((mx, my), m.SpawnAnchor);
                    double reaggroAnchorCap = 1.5 * Math.Max(40, m.AggroRange);
                    Player? reaggroTarget = null;
                    if (homeDist <= reaggroAnchorCap)
                    {
                        double bestDist = MonsterNoticeRange(m);
                        foreach (var peer in player.Frames.Players())
                        {
                            if (peer.Closed || peer.LeavingArea || peer.PlayerLife == 0 || InGracePeriod(peer)) continue;
                            double pd = Dist((mx, my), peer.Walk.Position(now));
                            if (pd <= bestDist) { bestDist = pd; reaggroTarget = peer; }
                        }
                    }
                    if (reaggroTarget is not null)
                    {
                        m.ReturningHome = false;
                        m.Target = reaggroTarget;
                        if (!ReferenceEquals(reaggroTarget, player))
                        {
                            m.Aggro = true;
                            m.LastTarget = null;
                            AlertPackLocked(reaggroTarget, m, (mx, my), now);
                            continue;
                        }
                        // Fall through to normal aggro/combat on this player!
                    }
                    else if (homeDist <= 6 || (m.Walk.Reached && !m.Walk.ReachedGoal && m.LastTarget == m.SpawnAnchor))
                    {
                        m.ReturningHome = false;
                        m.LastTarget = null;
                        continue;
                    }
                    else
                    {
                        if (now < m.BusyUntil || cadence.SwingPending(m)) continue;
                        bool onLeg = m.Leg is not null;
                        if (LockstepReorderDue(player, plan, m, cadence, now)
                            && cadence.TryChase(m, m.SpawnAnchor, now, retryAfterArrival: now >= m.Walk.ArrivesAt && !onLeg, walking: now < m.Walk.ArrivesAt || onLeg))
                        {
                            m.LastTarget = m.SpawnAnchor;
                            m.SwingGeneration = null;
                            var beforeHome = m.Walk.Save();
                            var fromHome = PlaceMove(plan, m, m.SpawnAnchor, now);
                            plan.MoveLegs.Add((id, m, NextPlannedAction(m), fromHome, double.IsNaN(plan.FrameStart) ? CutToLeg(m, m.SpawnAnchor, now, beforeHome) : m.SpawnAnchor));
                        }
                        continue;
                    }
                }
                // One player at a time: the instance's monster acts for whoever it targets, on that one's connection.
                if (m.Hostile && !MonsterTargetsLocked(player, id, m, (mx, my), d, now)) continue;
                if (m.TemporarilyAllied && !TemporaryAllyDriverLocked(player, id, m, (mx, my), now)) continue;
                var decoy = DecoyTarget(m, now);
                if (m.Hostile && decoy is null && GolemEnemyOrderLocked(player, id, m, now, d)) continue;
                // A boss's kit (BossKits): the skill that is ready and reaches the player, else its chase swing. Never
                // while a swing or arrived lockstep walk-up swing still runs: the attack an order names must stay the one
                // its hit is applied with, and switching skills must close any open melee walk-up so StepWalkUp never
                // tests a melee chase against a newly chosen ranged skill's reach.
                if (m.Kit.Count > 0 && now >= m.BusyUntil && m.ClientSwingAt is null && !cadence.SwingPending(m))
                {
                    var chosen = BossKits.Choose(m, now, d, a => MonsterSkills.Usable(player, id, m, a, now));
                    if (chosen?.SkillId != m.Attack?.SkillId) m.ClientChase = null;
                    m.Attack = chosen;
                }
                if (!m.Aggro && d <= MonsterNoticeRange(m))
                {
                    m.Aggro = true;
                    m.ReturningHome = false;
                    m.LastTarget = null;
                    AlertPackLocked(player, m, (mx, my), now);
                }
                // No boss bar during an encounter's entrance: its script shows the bar as the fight starts.
                if (m.Aggro && !m.BossBarShown && !m.EntranceGrace && m.Variety is { BossHealthBar: true } && m.States?.IndexOf("boss_life_bar") >= 0)
                {
                    m.BossBarShown = true;
                    plan.BossBars.Add((id, m));
                }
                // A flee walk is not cut short. A minion or temporary ally keeps its own target policy.
                // Retail leash: break pursuit when the player pulls beyond MonsterLeashRange OR when an ordinary monster
                // is dragged beyond 2 × AggroRange from its SpawnAnchor, then walk back to SpawnAnchor.
                else if (decoy is null && m.Aggro && !m.Fleeing && !player.Minions.Owns(id) && !m.TemporarilyAllied
                    && (d > MonsterLeashRange(m) || (CanLeashReturn(player, id, m) && Dist((mx, my), m.SpawnAnchor) > 2.0 * Math.Max(40, m.AggroRange))))
                {
                    m.Aggro = false;
                    m.Target = null;
                    m.SwingGeneration = null;
                    m.LastTarget = null;
                    m.ClientChase = null;
                    if (CanLeashReturn(player, id, m) && Dist((mx, my), m.SpawnAnchor) > 6 && !m.Walk.MovementBlocked(now))
                    {
                        m.ReturningHome = true;
                        var beforeLeash = m.Walk.Save();
                        var fromLeash = PlaceMove(plan, m, m.SpawnAnchor, now);
                        m.LastTarget = m.SpawnAnchor;
                        plan.MoveLegs.Add((id, m, NextPlannedAction(m), fromLeash, double.IsNaN(plan.FrameStart) ? CutToLeg(m, m.SpawnAnchor, now, beforeLeash) : m.SpawnAnchor));
                        continue;
                    }
                    // On a lockstep client it gets no order, as GGG's gets none when a pursuit ends: any order still at the
                    // gate is dropped and the estimate goes on with its last order, as the client's monster does (a
                    // chase's walk-up after the player, NextPlannedAction), so the wake zone's return (LockstepPlace)
                    // finds the client's monster where the estimate is (M0 bench: 69 of 74 still walking 0.5 s after a
                    // dropped one-cell give-up Move). GGG's idle orders after it are a difference
                    // (Lockstep.Differences.cs).
                    if (player.Connection.LockstepClient)
                    {
                        NextPlannedAction(m);
                        Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) gives up at ({mx}, {my}): not sent, its walk goes on to {m.Walk.Destination}");
                        continue;
                    }
                    // GGG ends a predictive chase with S→C 0x1ac before the give-up Move (925 of 1166 live give-ups).
                    plan.EndedActions.Add(id);
                    // A Move to its own cell does not replace the attack a predictive client is still chasing with, so it
                    // goes to the next cell.
                    var stay = NextCell(player, m, mx, my);
                    plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, stay, now), stay, "gives up"));
                    continue;
                }
                // Frozen: it does nothing until the freeze ends. On a predictive client it stands where it is: a chase order
                // stays open on the client even when the estimate has stopped (the action goes on after the player until
                // something replaces it), so a Move onto its own cell stops it.
                // Pinned by a trap's spikes (Pins.cs): held as a frozen monster is.
                double action = Pinned(m, now) ? 0 : MonsterActionFactor(m, now);
                if (action == 0)
                {
                    if (now < m.Walk.ArrivesAt || m.LastTarget is not null)
                    {
                        // A lockstep client takes no Move onto the walker's own cell (07 §6), so nothing stops it: no
                        // stop is sent, any order still at the gate is dropped, and the estimate keeps its walk as the
                        // client's monster does, a chase's walk-up with its goal test (no is_frozen stop for the player,
                        // 07 §5; a monster's freeze is not traced).
                        if (player.Connection.LockstepClient) NextPlannedAction(m);
                        else plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, (mx, my), now), (mx, my), "is frozen"));
                    }
                    m.LastTarget = null;
                    continue;
                }
                // Held by a Bear Trap: its walk or chase stops, once; it may still attack where it stands. A predictive client
                // stops on a Move onto its own cell, as a freeze's; a lockstep client takes none, so it gets a knockback's
                // teleport onto that cell (live 2026-11-19: without one it walked its whole leg at the slowed speed).
                if (BearTrapHoldStarts(m, now))
                {
                    bool walking = now < m.Walk.ArrivesAt || m.LastTarget is not null;
                    Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) held by a Bear Trap at ({mx}, {my})"
                        + (walking ? $", its walk to {m.Walk.Destination} stopped" : ""));
                    if (walking)
                    {
                        if (!player.Connection.LockstepClient)
                            plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, (mx, my), now), (mx, my), "is held by a Bear Trap"));
                        else
                        {
                            NextPlannedAction(m);
                            m.Walk.Teleport(mx, my, now);
                            m.KnockbackPending = (mx, my);
                        }
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
                // Nor is a swing at a Decoy Totem: the player's distance says nothing about it.
                if (decoy is null && !player.Connection.LockstepClient && m.Attack is { InPlace: false } ordered && m.SwingGeneration == m.ActionGeneration
                    && now - m.SwingOrderedAt < SwingOrderSeconds && d > ordered.Range)
                {
                    m.BusyUntil = now;
                    m.SwingGeneration = null;
                    m.ActionGeneration++;
                }
                if (player.Minions.Attacks.TryGetValue(id, out var pendingMinion))
                {
                    if (ReferenceEquals(pendingMinion.Monster, m) && (player.Minions.Fights(id) || m.FightsForPlayers)
                        && TemporaryAllyAttackCurrent(pendingMinion, now))
                    { plan.MinionSwings.Add(pendingMinion); continue; }
                    player.Minions.Attacks.Remove(id);
                }
                if (!m.Aggro || !MonsterAiCadence.Due(now, m.BusyUntil) || cadence.SwingPending(m)) continue;
                // Totems, statues, vines and the like (MovementSpeed 0) never walk: the estimate's minimum speed must not
                // move them, so they get no chase. Neither does a type the client never walks (Pathfinding immobile),
                // whatever its speed column says: the client would only hop it to each order's from cell.
                bool still = m.Variety is { MovementSpeed: 0 } or { Immobile: true } || m.Walk.MovementBlocked(now);
                // The player's fighting minions attack his enemies near them; with none they follow him.
                if ((player.Minions.Fights(id) || m.FightsForPlayers) && m.Attack is { } ma
                    && MinionTargetLocked(player, id, m, (mx, my), (px, py), now) is { } target)
                {
                    // A lockstep client takes no Move within 2 cells (07 §6), so a minion that near its approach cell gets
                    // its attack aimed at the target instead. A melee (pathing) attack walks up on the client
                    // (0x141e9afc0), as a melee leg that short does (PlaceLockstepLeg), and swings in the frame it gets
                    // there (07 §6): the estimate walks the same walk-up and times the swing from its end. An in-place
                    // one (skill_is_non_pathing) plays where the minion stands, once its walk has ended.
                    bool walkUp = !still && !target.InReach && !double.IsNaN(plan.FrameStart) && !LockstepTakesMove((mx, my), target.Approach);
                    // Stand, then act, as a hostile monster does (below): an in-place action waits for the walk's end.
                    if ((target.InReach || walkUp) && StandsToAct(ma) && m.Walk.Moving(now)) continue;
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
                        var attack = new PlayerMinions.Attack(id, m, (mx, my), target.Id, target.Monster, target.At,
                            begins + ma.Seconds * (ma.Release?.Fraction ?? .5))
                            { TemporaryAllyUntil = m.TemporarilyAllied ? m.TemporaryAllyUntil : null };
                        player.Minions.Attacks[id] = attack;
                        plan.MinionSwings.Add(attack);
                    }
                    // A new path when the target moved, else again after arriving short, at most twice a second.
                    else if (!still && (m.LastTarget is not { } last || Math.Abs(last.X - target.Approach.X) + Math.Abs(last.Y - target.Approach.Y) >= 3
                        || now >= m.Walk.ArrivesAt && now >= m.NextChaseRetryAt))
                    {
                        m.NextChaseRetryAt = now + 0.5;
                        m.LastTarget = target.Approach;
                        m.SwingGeneration = null;
                        plan.Moves.Add((id, m, NextPlannedAction(m), PlaceMove(plan, m, target.Approach, now),
                            target.Approach, $"minion chases monster 0x{target.Id:x}"));
                    }
                    continue;
                }
                if (m.TemporarilyAllied) continue;
                if (m.Hostile && decoy is { } targetDecoy)
                {
                    PlanDecoyTarget(player, cadence, plan, id, m, targetDecoy.Source, now, (mx, my), decoyChasers);
                    continue;
                }
                if (hasRampageWeapons && PlanRampageWeaponTarget(player, cadence, plan, id, m, now, (mx, my), d)) continue;
                // Attack cadence remains the attack's own duration; faster AI only reduces the delay after expiry.
                // A pending echo/contact keeps its original generation instead of reserving repeated swings at the gate.
                // A chill slows the swing by its action speed, as the native clip does.
                // A lockstep client's chase walk-up swings by itself, waiting for no 0x1ac (0x141e9afc0), in the frame its
                // goal test finds the player in reach: where the monster stands when the order applies
                // (PlaceLockstepChase), or on the way, every frame and at each waypoint, against the player's cell then
                // (StepFrames). The server takes that swing as its own, from that frame's start, with no order: a second
                // one would only wait behind the swing (0x141e937b0), and GGG's next 0x1ab after its 0x1ac comes 0.96 s
                // later at the median. The hit still asks the reach at the contact.
                if (player.Connection.LockstepClient && m.ClientSwingAt is { } begun)
                {
                    m.ClientSwingAt = null;
                    if (m.ClientSwingAction == m.ActionGeneration && m.SwingGeneration is null)
                    {
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) swings from its walk-up at {m.Walk.Position(begun)}: "
                            + $"player ({px}, {py}) {d:0.0} away now, begun {now - begun:0.000} s ago (t={now:0.000})");
                        // The swing is the chase order's skill, whatever the kit chose while the monster walked: it hits as
                        // that attack, and that is what the kit records as used.
                        if (m.ClientChaseAttack is { } chased) m.Attack = chased;
                        if (m.Attack is { } swung) BossKits.Consume(m, swung, begun);
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) walk-up swing is skill 0x{m.Attack?.SkillId ?? 0:x4}");
                        m.BusyUntil = begun + MonsterSwingSeconds(player, m, begun) / action + player.LockstepActionTail;
                        m.LastTarget = null;
                        m.SwingGeneration = m.ActionGeneration;
                        m.SwingOrderedAt = begun;
                        cadence.BeginSwing(m, m.ActionGeneration);
                        if (m.Attack is { } arrived) BossKits.Consume(m, arrived, begun);
                        plan.ArrivedSwings.Add((id, m, m.ActionGeneration, begun));
                        continue;
                    }
                }
                // A swing order: a monster's in-place skill, a summoner's (its actor's clip, not a walk-up), a melee attack
                // of a monster that never walks (the chase's own 0x1ab, whose goal test passes as it applies: 0x141e81c20),
                // and any attack on a predictive client, where a walking player has already gone farther: no swing right
                // at the edge of the reach. A lockstep client's walking melee monster swings only by its chase's goal test
                // (above): standing in reach, it gets the chase, which starts the swing in the order's own frame.
                // A summon is no shot: a summoner whose attack row also flies a projectile still summons, as before.
                // Ranged MinimumAttackDistance backoff: ranged archers/casters kite back when the player steps inside
                // Variety.MinimumAttackDistance (cooldown-gated and walkability-checked to prevent jitter or wall-clipping).
                if (!still && m.Hostile && m.Aggro && !m.Fleeing && !m.ReturningHome
                    && m.Variety is { MinimumAttackDistance: > 0 } mv
                    && m.Attack is { Ranged: true, Summon: null, Use: null } rangedBackoff
                    && rangedBackoff.Range >= mv.MinimumAttackDistance + 6
                    && d < mv.MinimumAttackDistance && now >= m.NextBackoffAt)
                {
                    m.NextBackoffAt = now + 3.0;
                    if (FindBackoffCell(player, m, (mx, my), (px, py), mv.MinimumAttackDistance, rangedBackoff.Range) is { } back)
                    {
                        m.LastTarget = back;
                        m.SwingGeneration = null;
                        var beforeBack = m.Walk.Save();
                        var fromBack = PlaceMove(plan, m, back, now);
                        plan.MoveLegs.Add((id, m, NextPlannedAction(m), fromBack, double.IsNaN(plan.FrameStart) ? CutToLeg(m, back, now, beforeBack) : back));
                        continue;
                    }
                }
                // Any non-lobbed ranged attack or spell (with or without a projectile object) requires line of sight.
                bool blockedShot = m.Hostile && m.Attack is { Ranged: true, Summon: null } shot && shot.Projectile?.Lobbed != true && d <= shot.Range
                    && player.Layout.Sight is { } sight && GameServer.Core.World.ClientPathfinder.LineBelow((mx, my), (px, py), sight, 1);
                // Stand, then act: an action played where the monster stands (an in-place skill, a summon) is ordered only
                // once its walk has ended. The client's in-place action leaves a Move's pathing running (07 §6, "A walk
                // under an in-place action"), so a shot ordered mid-walk flew while the monster walked on, and the next
                // order's from cell put it back. A lockstep client takes no Move near its own cell, so nothing can cut
                // the walk short: a monster in reach of such an action gets no new walk and acts when this one ends.
                bool inReach = !blockedShot && m.Hostile && m.Attack is { } r
                    && d <= (r.InPlace || player.Connection.LockstepClient || !player.Walk.Moving(now) ? r.Range : r.Range - MovingReachMargin);
                if (inReach && m.Attack is { } standing && StandsToAct(standing) && m.Walk.Moving(now)) continue;
                if (inReach && m.Attack is { } a && (a.InPlace || a.Summon is not null || still || !player.Connection.LockstepClient)
                    && BossKits.Ready(m, a, now)
                    && SummonReadyLocked(player, id, m, a, now)
                    && (m.Kit.Count > 0 || MonsterAiCadence.Due(now, m.CooldownReadyAt)))
                {
                    m.BusyUntil = now + MonsterSwingSeconds(player, m, now) / action + player.LockstepActionTail;
                    // A melee swing starts at the end of its walk-up: the estimate's walk ends where the monster stands.
                    // An in-place action comes only once the walk has ended (above).
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
                    // A steady walker (MonsterMovement.ReorderSeconds): while it is on its way it is ordered again at most this
                    // often, as an order restarts its walk clip (live, Izaro: every 200 ms he walked in steps).
                    bool steady = m.Movement.ReorderSeconds is not null;
                    if (m.Movement.ReorderSeconds is double every && now < m.Walk.ArrivesAt && !cadence.ChaseOverdue(m, now, every - MonsterAiCadence.AttackChaseSeconds))
                    {
                        // Between orders the client's walk-up follows the player by itself: so does the estimate, or an
                        // order's from cell would put it back (live; a lockstep client's estimate has StepWalkUp).
                        if (!player.Connection.LockstepClient && m.LastTarget is { } aimed && Math.Abs(aimed.X - px) + Math.Abs(aimed.Y - py) >= MonsterAiCadence.TargetChangeCells)
                        {
                            var followed = m.Walk.Approach(px, py, melee.Range - 1, now, LivingCollision.PlayerSize);
                            m.LastTarget = (px, py);
                            if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) follows from {followed.Here} to {followed.Stop}, arrives in {followed.At - now:0.000} s, speed {m.Walk.Speed:0.#}, player ({px}, {py}) (t={now:0.000})");
                        }
                        continue;
                    }
                    if (crowdedPack && !playerMoving && m.LastTarget is null && m.Walk.Reached && !m.Walk.ReachedGoal
                        && !MonsterAiCadence.Due(now, m.NextChaseRetryAt)) continue;
                    double chaseStagger = crowdedPack ? (id % 6) * MonsterAiCadence.StepSeconds : 0;
                    if (!LockstepReorderDue(player, plan, m, cadence, now) || !cadence.TryAttackChase(m, now, chaseStagger)) continue;
                    // Far off, a Move to the cell LegCells along the walk-up instead of the attack aimed at the player,
                    // ordered again on the same cadence; the attack's own walk-up from within a leg of the reach. An
                    // estimate that finds no way at all keeps the attack order: the client's own search may.
                    if (!steady && d > melee.Range - 1 + LegCells)
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
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) chase walk from {walk.Here} to {walk.Stop}, arrives in {walk.At - now:0.000} s, speed {m.Walk.Speed:0.#}, "
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
                    if (d <= m.StopRange && !blockedShot) continue;
                    double f = blockedShot ? 0 : (m.StopRange - 1) / d;
                    (int X, int Y) to = ((int)Math.Round(px + (mx - px) * f), (int)Math.Round(py + (my - py) * f));
                    if (!player.Layout.WalkableFor(to.X, to.Y, 0) || player.Barrier.Contains(to)) to = (px, py);
                    if (!LockstepReorderDue(player, plan, m, cadence, now) || !cadence.TryChase(m, to, now, walking: now < m.Walk.ArrivesAt || m.Leg is not null)) continue;
                    m.LastTarget = to;
                    m.SwingGeneration = null;
                    var before = m.Walk.Save();
                    var from = PlaceMove(plan, m, to, now);
                    plan.MoveLegs.Add((id, m, NextPlannedAction(m), from, double.IsNaN(plan.FrameStart) ? CutToLeg(m, to, now, before) : to));
                }
                else if (LivingCollision.StepsWithBodies(m))
                    askers.Add((new(id, mx, my, LivingCollision.Size(m), m.LastTarget,
                        m.Attack?.Range ?? double.PositiveInfinity, !blockedShot && m.Attack?.Ranged == true), m, d));
            }
            if (askers.Count == 0 && decoyChasers.Count == 0) return plan;
            var fixedBlockers = blockers.FixedBlockers;
            bool Standable(int x, int y, int size) => player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y))
                && !GameServer.Core.World.ClientPathfinder.ObjectBlocks(fixedBlockers, x, y, size + 1, size + 4);
            if (decoyChasers.Count > 0)
                PlanDecoyChasesLocked(player, cadence, plan, now, decoyChasers, blockers.Bodies, (px, py), Standable);
            if (askers.Count == 0) return plan;
            // Every other living body near the player keeps its place: the chasers' cells go around it.
            var asking = askers.Select(a => a.Asker.Id).ToHashSet();
            var occupied = blockers.Bodies.Where(b => !asking.Contains(b.Id)
                && Math.Max(Math.Abs(b.X - px), Math.Abs(b.Y - py)) < 64).Select(b => (b.X, b.Y, b.Size));
            long surroundAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocationAt = GC.GetAllocatedBytesForCurrentThread();
            var slots = MonsterSurround.Assign(askers.Select(a => a.Asker), (px, py), occupied, Standable);
            player.Connection.Performance.Elapsed(LatencyPhase.Surround, surroundAt);
            player.Connection.Performance.SurroundAllocation(GC.GetAllocatedBytesForCurrentThread() - allocationAt);
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
                if (!LockstepReorderDue(player, plan, m, cadence, now) || !cadence.TryChase(m, to, now, retryAfterArrival: now >= m.Walk.ArrivesAt && !onLeg, walking: now < m.Walk.ArrivesAt || onLeg)) continue;
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
    static bool LockstepReorderDue(Player player, MonsterAiPlan plan, SpawnedMonster m, MonsterAiCadence cadence, double now)
    {
        if (!player.Connection.LockstepClient || double.IsNaN(plan.FrameStart)) return true;
        var (into, step) = m.Walk.StepPhase(plan.FrameStart);
        return step == 0 || into < FrameMs / 1000d + 1e-6 || cadence.ChaseOverdue(m, now, 2 * step);
    }

    // An action the client plays where the actor stands, not at the end of a walk-up of its own: it must not be ordered
    // while the actor walks (PlanMonsterAi, "Stand, then act").
    static bool StandsToAct(MonsterAttack a) => a.InPlace || a.Summon is not null;

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
    static (int X, int Y) NextCell(Player player, SpawnedMonster m, int x, int y)
    {
        int size = Math.Max(0, LivingCollision.Size(m));
        for (int reach = 1; reach <= 3; reach++)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (player.Layout.WalkableFor(x + dx * reach, y + dy * reach, size) && !player.Barrier.Contains((x + dx * reach, y + dy * reach)))
                    return (x + dx * reach, y + dy * reach);
        return (x, y);
    }

    public static async Task StepMonsterAi(Player player,
        MonsterAiCadence cadence)
    {
        MonsterAiPlan plan;
        await ExpireSummonedMonsters(player);
        await MonsterTrapsTick(player);
        await MonsterMarkersTick(player);
        await HostileGroundsTick(player);
        await PlayerDebuffsTick(player);
        if (player.PlayerLife == 0) await CampaignBossHelpers.OnPlayerDiedInBossArea(player);
        if (IncomingHitTriggersPending(player))
            ReleaseDamageTakenTriggers(player, ProjectilesFor(player));
        // Advance the projectile runtime first: decisions and committed projectile contacts share one game time and pose.
        using (ProjectileMutation(player, phase: "ai")) plan = PlanMonsterAi(player, cadence, Math.Max(ProjectileWorldTime(player), AreaTime(player)));
        foreach (var (packet, log) in plan.Ailments) await Send(player, packet, log);
        foreach (uint id in plan.EndedActions)
            await SendWorldRecord(player, id, InstanceProtocol.ActionEnded(id), ServerTrace.Ai ? $"0x01ac monster 0x{id:x} action ended" : null);
        await SendMonsterAiOrders(player, plan);
        foreach (var (id, m) in plan.BossBars)
        {
            if (!MonsterPresent(player, id, m)) continue; BossBars(player).Add(id);
            await ShowBossLifeBar(id, m, player);
        }
        if (!CurrentGameWork) return;
        foreach (var (id, m, action) in plan.Swings)
            _ = RunPlannedMonsterSwing(player, cadence, id, m, action);
        foreach (var (id, m, action, begun) in plan.ArrivedSwings)
            _ = RunPlannedMonsterSwing(player, cadence, id, m, action, begun);
        foreach (var attack in plan.MinionSwings)
            StartMinionSwing(player, attack);
        foreach (var (id, m, action, target, weapon) in plan.WeaponSwings)
            _ = RunPlannedRampageWeaponSwing(player, cadence, id, m, action, target, weapon);
        foreach (var (id, m, action, totem) in plan.DecoySwings)
            _ = RunDecoySwing(player, cadence, id, m, action, totem);
        player.Connection.Performance.MaybeLog(player.Connection.Remote);
    }

    /// <summary>The orders of an AI pass (its moves, chases and legs), each as the record it writes when its turn comes:
    /// on a lockstep client the walk is placed then, at the start of the frame being sent (LockstepOrderFrom).</summary>
    public static List<(uint Id, SpawnedMonster M, int Action, Func<byte[]?> Packet, string? Log)> MonsterAiOrders(Player player, MonsterAiPlan plan)
    {
        var orders = new List<(uint Id, SpawnedMonster M, int Action, Func<byte[]?> Packet, string? Log)>();
        const ushort Move = 0x2909;
        // A chase is re-ordered every few ticks: its lines only with Game_TRACE=ai (the packet history keeps them).
        foreach (var (id, m, action, from, to, why) in plan.Moves)
            orders.Add((id, m, action, () => PlanMoveAtGate(player, plan, id, m, action, from, to, why),
                ServerTrace.Ai || why == "gives up" ? $"0x01ab monster 0x{id:x} {why}: move from {from} to {to}" : null));
        foreach (var (id, m, action, from, to) in plan.Chases)
            orders.Add((id, m, action, () => ChaseAtGate(player, plan, id, m, action, from, to),
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases the player from {from} to {to}" : null));
        foreach (var (id, m, action, from, to) in plan.Legs)
            orders.Add((id, m, action, () =>
                {
                    var aim = to;
                    bool noWay = false;
                    if (LockstepOrderFrom(player, plan, id, m, action, from, at =>
                    {
                        var (here, leg, attack) = PlaceLockstepLeg(player, m, action, at);
                        (aim, noWay) = (leg, attack);
                        return here;
                    }) is not { } start) return null;
                    if (noWay)
                    {
                        if (ServerTrace.Ai) Console.WriteLine($"[gateway] monster 0x{id:x} ({m.Label}) leg finds no way from {start}: attack order at the player {aim}");
                        return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, m.Attack!.SkillId, m.NextSequence(), 0, targetObjectId: player.Id);
                    }
                    return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, Move, NextMonsterMoveSequence(player, m), player.MonsterOrderFlags);
                },
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases a leg from {from} to {to}" : null));
        foreach (var (id, m, action, from, to) in plan.MoveLegs)
            orders.Add((id, m, action, () =>
                {
                    var aim = to;
                    bool taken = true;
                    if (LockstepOrderFrom(player, plan, id, m, action, from, at =>
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
                    return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, Move, NextMonsterMoveSequence(player, m), player.MonsterOrderFlags);
                },
                ServerTrace.Ai ? $"0x01ab monster 0x{id:x} chases from {from} to {to}" : null));
        return orders;
    }

    public static async Task RunPlannedMonsterSwing(Player player, MonsterAiCadence cadence, uint id, SpawnedMonster m, int action, double? begunAt = null)
    {
        bool handedOff = false;
        try { handedOff = await MonsterSwingCore(player, id, m, action, cadence: cadence, begunAt: begunAt); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        // Anything else is a server bug: the swing was already shown, so name the lost hit instead of faulting the task
        // unobserved (review 2026-10-30).
        catch (Exception ex) { Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} swing {action} failed: {ServerLog.Describe(ex)}"); }
        finally { if (!handedOff) cadence.CompleteSwing(m, action); }
    }

    // A monster swings at the player: its skill as an action aimed at the player (0x1ab, flags | 0x80 + target id; a melee
    // swing with its own rising sequence and the 0x1ac that starts it), then at the contact point (the clip's keyframe,
    // half the attack time without rig data; a projectile skill's clip contact, MonsterRelease) the hit lands if the monster still lives and the player is still in reach (walking away dodges
    // it): the player's life goes down by the monster's damage (0x1b1 on the player). At 0 the client runs the player's
    // death.
    // PoE's evasion entropy (wiki: Evasion): shared by every attack against the player, reset after 3.33 s. Call under
    // the area's frame.
    public static bool EvadesLocked(Player player, int hitChance, double now)
    {
        if (player.EvasionEntropy < 0 || now - player.LastEvasionAt > 3.33)
            player.EvasionEntropy = player.IncomingHitRandom.Next(100);
        player.LastEvasionAt = now;
        player.EvasionEntropy += hitChance;
        if (player.EvasionEntropy < 100) return true;
        player.EvasionEntropy -= 100;
        return false;
    }

    // Ordinary AI work belongs to this monster object in this connection. Generations also prevent a flinch that has
    // already ended from reviving an old queued order or contact. Callers may already hold Monsters.
    public static bool MonsterActionCurrent(Player player, uint id, SpawnedMonster m, int action)
    {
        return MonsterPresent(player, id, m) && player.PlayerLife > 0
            && (m.Hostile || KingInMists.IsFetish(m.Variety) || player.Minions.Owns(id, m)) && !m.StageRunning
            && m.ActionGeneration == action && AreaTime(player) >= m.FlinchUntil;
    }

    /// <summary>When a monster's projectile skill releases its shot, as a delay from the start of its action
    /// 0x1ab: its measured launch frame when it has one (MonsterAttack.LaunchFrames), scaled by any runtime action-speed
    /// change (chill/haste) and clamped strictly inside the action duration so accelerated monsters never overrun
    /// <c>BusyUntil</c> and drop their shot; every skill without a row keeps the clip contact's fraction of the attack
    /// time. Read from the attack built once per skill, never per hit.</summary>
    public static double MonsterLaunchDelay(MonsterAttack attack, double contact, double seconds)
    {
        if (attack.LaunchFrames is not int frames) return seconds * contact;
        double frameSeconds = Program.FrameMs / 1000d;
        double scale = attack.Seconds > 0 && seconds > 0 ? seconds / attack.Seconds : 1;
        double delay = Math.Max(0, frames) * frameSeconds * scale;
        return seconds > 0 ? Math.Min(delay, Math.Max(0, seconds - frameSeconds)) : delay;
    }

    public static bool MonsterPresent(Player player, uint id, SpawnedMonster m) =>
        CurrentGameWork && !player.Closed && !player.LeavingArea && !player.RageRetired && player.PendingTransfer is null && !m.Dead && m.Life > 0
        && ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), m);

    // Apply the stage effect and end recovery on the area game clock, at the actual send gate. Hot-reloaded
    // workers resume the same deadlines and one-shot state; a pause cannot consume the remaining stage time.
    public static async Task CompleteMonsterStage(Player player, uint id, SpawnedMonster m)
    {
        while (true)
        {
            if (!MonsterPresent(player, id, m) || !m.StageRunning) return;
            bool completed = false;
            await SendWorldRecords(player, id, () =>
            {
                var bytes = AdvanceMonsterStageRecords(player, id, m, AreaTime(player));
                completed = !m.StageRunning;
                if (bytes is not null)
                    Console.WriteLine($"[gateway] {player.Connection.Remote} => monster 0x{id:x} ({m.Label}) stage " +
                        $"{(completed ? "complete" : "effect")}: life {m.Life}, game time {AreaTime(player):0.000}");
                return bytes;
            });
            if (completed) return;
            int wait;
            double next = m.StageEffectApplied ? m.BusyUntil : m.StageEffectAt;
            wait = player.Paused ? 100 : (int)Math.Clamp(Math.Ceiling((next - AreaTime(player)) * 1000), 16, 1000);
            await AreaWait(PlayerUpdateRole.MonsterAction, wait);
        }
    }

    // Called only at the send gate; all one-shot state changes and their packets belong to the same guarded update.
    public static byte[]? AdvanceMonsterStage(Player player, uint id, SpawnedMonster m, double now) =>
        AdvanceMonsterStageRecords(player, id, m, now) is { } records ? [.. records.SelectMany(r => r)] : null;

    public static byte[][]? AdvanceMonsterStageRecords(Player player, uint id, SpawnedMonster m, double now)
    {
        if (!MonsterPresent(player, id, m) || !m.StageRunning || player.Paused) return null;
        var stage = m.Stage!;
        // A stage started by an older loaded module retains its recovery deadline.
        if (double.IsNaN(m.StageEffectAt))
            m.StageEffectAt = m.BusyUntil - stage.CastSeconds + stage.EffectSeconds(m.Variety is { } variety
                ? SkillsFor(player).MonsterContact(variety.Id, "Throw", 0xffff) : null);
        var bytes = new List<byte[]>();
        if (!m.StageEffectApplied && now >= m.StageEffectAt)
        {
            if (m.Variety?.ComponentsOrBase.ToList().IndexOf("Transitionable") is int t and >= 0)
                bytes.Add(InstanceEntities.TransitionableUpdate(id, (byte)t, stage.TransitionTo));
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
    public static ((int X, int Y) Here, (int X, int Y) Aim, bool Attack) PlaceLockstepLeg(Player player, SpawnedMonster m, int action, double at)
    {
        var playerAt = player.Walk.Position(at);
        var before = m.Walk.Save();
        var walk = m.Walk.Approach(playerAt.X, playerAt.Y, m.Attack!.Range - 1, at, LivingCollision.PlayerSize);
        var leg = m.Walk.Ahead(at, LegCells / m.Walk.Speed);
        // A leg within two cells is no way either: the client would not take it (LockstepTakesMove), and the attack's
        // walk-up is the one the estimate just placed.
        if (!LockstepTakesMove(walk.Here, leg))
        {
            TakeLockstepChase(m, action, walk.Here, playerAt, at);
            return (walk.Here, playerAt, true);
        }
        // The client gets only the leg, so it goes over the walk before the walk-up, which never ran: over the walk-up a
        // stand's kept time would be spent and the leg start a step late. The leg ends any walk-up the client ran.
        m.Walk.Restore(before);
        m.ClientChase = null;
        return (m.Walk.Order(leg.X, leg.Y, at), leg, false);
    }

    // A chase order at the send gate: the attack aimed at the player, from the cell its walk-up starts on, or null when
    // the walk-up it replaces began its swing first (LockstepOrderFrom).
    public static byte[]? ChaseAtGate(Player player, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, (int X, int Y) to)
    {
        // Placed at the gate, the chase aims at the player's cell at that frame's start, as the plan would have.
        var aim = to;
        if (LockstepOrderFrom(player, plan, id, m, action, from, at =>
        {
            var (here, playerAt) = PlaceLockstepChase(player, m, action, at);
            aim = playerAt;
            return here;
        }) is not { } start) return null;
        return InstanceProtocol.ActionStarted(id, start.X, start.Y, aim.X, aim.Y, m.Attack!.SkillId, m.NextSequence(), 0, targetObjectId: player.Id);
    }

    // A melee chase order (the attack aimed at the player) placed at a lockstep frame's start `at`: the walk-up to the
    // player's cell then, as the order starts at the frame's start, before any object ticks (a monster in a hit's recovery
    // is ordered only from the frame after it: TakeHitPacket). Returns the walker's cell and the player's.
    public static ((int X, int Y) Here, (int X, int Y) Aim) PlaceLockstepChase(Player player, SpawnedMonster m, int action, double at)
    {
        var playerAt = player.Walk.Position(at);
        var here = m.Walk.Approach(playerAt.X, playerAt.Y, m.Attack!.Range - 1, at, LivingCollision.PlayerSize).Here;
        TakeLockstepChase(m, action, here, playerAt, at);
        return (here, playerAt);
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
        m.ClientChaseAttack = m.Attack;
        if (m.Walk.InReachOf(here, player, (int)(m.Attack!.Range - 1), LivingCollision.PlayerSize))
            (m.ClientSwingAt, m.ClientSwingAction) = (at, action);
        else if (m.Walk.Reached && !m.Walk.ReachedGoal)
        {
            m.LastTarget = null;
            m.NextChaseRetryAt = at + MonsterAiCadence.RetrySeconds;
        }
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
    public static byte[]? PlanMoveAtGate(Player player, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, (int X, int Y) to, string why)
    {
        const ushort Move = 0x2909;
        bool taken = true;
        if (LockstepOrderFrom(player, plan, id, m, action, from, at =>
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
        return InstanceProtocol.ActionStarted(id, start.X, start.Y, to.X, to.Y, Move, NextMonsterMoveSequence(player, m), player.MonsterOrderFlags);
    }

    // The actor installs each order's sequence on its action (0x14219aa10); its hit lookup matches that sequence
    // on the same actor (0x141e977f0). Lockstep Moves and chases share the actor's rising order counter.
    // Called only after admission, in the area's frame, so a refused Move consumes no sequence.
    static ushort NextMonsterMoveSequence(Player player, SpawnedMonster m) =>
        player.Connection.LockstepClient ? m.NextSequence() : player.NextActionSequence();

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

    static (int X, int Y)? LockstepOrderFrom(Player player, MonsterAiPlan plan, uint id, SpawnedMonster m, int action,
        (int X, int Y) from, Func<double, (int X, int Y)> place)
    {
        if (double.IsNaN(plan.FrameStart) || player.Connection.LockstepFrame is not { } frame) return from;
        // Out of this client's wake zone since the plan: its copy has stopped ticking (the 0x1aa went out first), so an
        // order would walk the estimate alone until it is back. Nothing is sent and nothing placed (KeepWakeZone).
        if (MonsterAsleep(player, id)) return null;
        StepWalkers(player, frame.Start);
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
    public static double LockstepOrderStart(Player player, double now)
    {
        if (!player.Connection.LockstepClient || player.Connection.LockstepFrame is not { } frame) return now;
        StepWalkers(player, frame.Start);
        return Math.Min(now, frame.Start);
    }

    /// <summary>Sends every movement/chase order of an AI pass. On lockstep/area-frame clients, batches all ready
    /// monster orders into a single SendWorldBatch under one shared MonsterBlockerFrame so BlockerBuild runs once per
    /// pass instead of once per monster (saving ~1,000 blocker builds/sec under 1,000-monster crowds).</summary>
    public static async Task SendMonsterAiOrders(Player player, MonsterAiPlan plan)
    {
        var orders = MonsterAiOrders(player, plan);
        if (orders.Count == 0) return;
        if (!player.Connection.LockstepClient && !InAreaFrame(player))
        {
            foreach (var (id, m, action, packet, log) in orders)
                await SendMonsterAction(player, id, m, action, packet, log);
            return;
        }
        var ready = new List<(uint Id, SpawnedMonster M, int Action, Func<byte[]?> Packet, string? Log, WorldObject Target)>(orders.Count);
        List<(uint Id, SpawnedMonster M, int Action, Func<byte[]?> Packet, string? Log)>? unborn = null;
        foreach (var order in orders)
        {
            if (!MonsterActionCurrent(player, order.Id, order.M, order.Action)) continue;
            var target = player.Connection.WorldOutput.PublishedObject(order.Id);
            if (target.IsReady) ready.Add((order.Id, order.M, order.Action, order.Packet, order.Log, target));
            else (unborn ??= []).Add(order);
        }
        if (ready.Count > 0)
        {
            WorldObject[] targets = new WorldObject[ready.Count];
            for (int i = 0; i < ready.Count; i++) targets[i] = ready[i].Target;
            while (CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0)
            {
                bool checkedAtGate = false;
                await SendWorldBatch(player, targets, _ =>
                {
                    using (ProjectileMutation(player, phase: "send"))
                    {
                        if (player.Paused) return null;
                        checkedAtGate = true;
                        double gateAt = player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame
                            ? frame.Start : AreaTime(player);
                        if (player.Connection.LockstepClient && player.Connection.LockstepFrame is not null)
                            StepWalkers(player, gateAt);
                        using var blockers = player.BeginMonsterBlockerFrame(gateAt);
                        List<OutputRecord>? records = null;
                        for (int i = 0; i < ready.Count; i++)
                        {
                            var (id, m, action, packet, log, target) = ready[i];
                            if (!MonsterActionCurrent(player, id, m, action)) continue;
                            if (packet() is { Length: > 0 } bytes)
                                (records ??= new List<OutputRecord>(ready.Count)).Add(PresentedRecord(target, bytes, log));
                        }
                        return records is { Count: > 0 } ? new OutputBatch(records.ToArray()) : null;
                    }
                });
                if (checkedAtGate) break;
                await AreaWait(PlayerUpdateRole.MonsterAction, 16);
            }
        }
        if (unborn is not null)
            foreach (var (id, m, action, packet, log) in unborn)
                await SendMonsterAction(player, id, m, action, packet, log);
    }

    // The record belongs to the acting monster unless the caller names another published object: a Rampage weapon's
    // hit answers with the weapon's own life (RampageIncoming), and the action's generation still gates that answer.
    public static async Task<bool> SendMonsterAction(Player player, uint id, SpawnedMonster m, int action, Func<byte[]?> packet, string? log = null,
        uint? recordId = null)
    {
        uint record = recordId ?? id;
        while (MonsterActionCurrent(player, id, m, action))
        {
            bool checkedAtGate = false, sent = false;
            await SendWorldRecord(player, record, [], log, atGate: () =>
            {
                // The projectile runtime first, as in the AI pass: a lockstep order places its walk here, and a contact
                // still pending before that would read the new walk's cells as the old walk's.
                using (ProjectileMutation(player, phase: "send"))
                {
                    if (!MonsterActionCurrent(player, id, m, action) || player.Paused) return null;
                    checkedAtGate = true;
                    var bytes = packet();
                    sent = bytes is not null;
                    // Everyone in the area sees it, with the frame's delta (SharedMonsters.cs).
                    return bytes;
                }
            });
            if (checkedAtGate) return sent;
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
        return false;
    }

    const int MonsterActionRecheckMs = 100;

    public static async Task<bool> WaitMonsterActionTime(Player player, uint id, SpawnedMonster m, int action, double until)
    {
        if (!CurrentGameWork || !double.IsFinite(until)) return false;
        // Waits for the time itself, looking again at most every MonsterActionRecheckMs (a cancelled action is let go
        // then), instead of every 16 ms: each attacking monster's wait took the area lock 60 times a second, and in a pack
        // fight these waits were the lock's largest user (CPU profile 2026-11-18: 3.7 s of lock waits in 25 s).
        while (true)
        {
            int wait;
            if (!MonsterActionCurrent(player, id, m, action)) return false;
            double left = until - AreaTime(player);
            if (!player.Paused && left <= 0) return true;
            wait = player.Paused ? MonsterActionRecheckMs : (int)Math.Clamp(Math.Ceiling(left * 1000), 1, MonsterActionRecheckMs);
            await AreaWait(PlayerUpdateRole.MonsterAction, wait);
        }
    }

    // Only an admitted ordinary melee swing with complete clock metadata uses this progress clock. The area clock
    // stops during a pause; generations and the same presence/recovery checks as other contacts cancel stale work.
    public static async Task<bool> WaitMonsterMeleeContact(Player player, uint id, SpawnedMonster m, int action)
    {
        if (!CurrentGameWork) return false;
        while (true)
        {
            if (!MonsterActionCurrent(player, id, m, action)
                || m.MeleeClock is not { } clock || clock.Generation != action) return false;
            if (!player.Paused && clock.Clock.Elapsed(AreaTime(player)) >= clock.Contact) return true;
            await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
    }

    // Melee is Animation.dat category 1 (its column 2 is false), so Actor.slow_animations_go_to_idle controls the
    // native clamp. Unknown resource composition or clip metadata retains the existing fraction-based schedule.
    // Round the pre-action-speed period as MonsterAttack.Stats does: native animation setup reads those integer ms.
    public static double? MonsterMeleeContactSeconds(SkillCatalog.MonsterClipContact? clip, bool? slowAnimationsGoToIdle,
        double attackSeconds) => clip is { ClockMetadataAvailable: true, ContactMs: { } contact }
            && slowAnimationsGoToIdle is { } idle
        ? AnimationEventClock.Seconds(clip.LengthMs, contact, Math.Round(attackSeconds * 1000) / 1000,
            idle, clip.SlowBeginMs, clip.SlowEndMs, clip.BudgetSeconds)
        : null;

    public static bool BeginMonsterMeleeClockLocked(Player player, SpawnedMonster m, int action, ushort sequence,
        double startedAt, double now)
    {
        if (m.ActionGeneration != action || m.SwingGeneration != action) return false;
        m.MeleeClock = null;
        if (m.Attack is not { InPlace: false, Release: null, Projectile: null, Summon: null, DualWielding: false } attack
            || m.Variety is not { } variety)
            return false;
        var catalog = SkillsFor(player);
        // Special melee skills and kit-owned/custom skill ids can select other animation categories. Only the
        // native Melee action is established here; off-hand selection likewise needs its own clip metadata.
        if (catalog.ActionName(attack.SkillId) != "Melee") return false;
        double period = MonsterSwingSeconds(player, m, now);
        if (MonsterMeleeContactSeconds(catalog.MeleeClock(variety.Id, sequence), variety.SlowAnimationsGoToIdle, period)
            is not { } contact) return false;
        // Native body +0x1f8 changes when virtual_action_speed_+% is published, independently of the clip's raw
        // speed. Use the same rounded stat, rather than folding an unrounded action factor into the clip duration.
        var clock = new ActionTimeClock(startedAt, Math.Max(0, 1 + m.Ailments.SentActionSpeed / 100d));
        double duration = Math.Round(period * 1000) / 1000;
        m.MeleeClock = (action, clock, contact, duration, player.LockstepActionTail);
        m.BusyUntil = clock.Deadline(duration) + player.LockstepActionTail;
        return true;
    }

    // Called when the AI queues an action-speed stat, as its walk retiming is; the packet is sent by the ensuing
    // dispatch. Freeze keeps its existing AI cancellation/contact suppression path, without a -100 speed stat.
    public static void RetimeMonsterMeleeClockLocked(SpawnedMonster m, double now, int actionSpeedPercent)
    {
        if (m.MeleeClock is not { } clock || clock.Generation != m.ActionGeneration || m.SwingGeneration != clock.Generation) return;
        bool recovering = clock.Clock.Elapsed(now) < clock.Duration;
        clock.Clock.SetRate(now, Math.Max(0, 1 + actionSpeedPercent / 100d));
        if (recovering) m.BusyUntil = clock.Clock.Deadline(clock.Duration) + clock.Tail;
    }

    // Commit the flinch with native Actor TakeHit recovery, which cancels the current walk/attack and blocks new
    // actions. ActionEnded or a rig-only AnimationUpdate leaves Positioned's movement advancing underneath the clip.
    // If the send gate was busy, recovery starts when sent; a pause does not consume that recovery time.
    public static async Task SendMonsterFlinch(Player player, uint id, SpawnedMonster m, StunHit stun, bool log)
    {
        while (true)
        {
            if (!MonsterPresent(player, id, m) || m.Invulnerable || m.HitAnimations is not { Count: > 0 }
                || m.Effects.CannotBeStunned || HitReaction.Chance(stun, m.MaxLife) <= 0) return;
            bool checkedAtGate = false;
            await SendWorldRecord(player, id, [], null, atGate: () =>
            {
                if (!MonsterPresent(player, id, m) || player.Paused) return null;
                checkedAtGate = true;
                double now = AreaTime(player);
                if (!HitReaction.TryFlinch(m, stun, now, AreaRng(player))) return null;
                float seconds = (float)(m.FlinchUntil - now);
                if (log) Console.WriteLine($"[gateway] {player.Connection.Remote} => 0x01d1 monster 0x{id:x} TakeHit recovery {seconds:0.000} s, " +
                    $"action {m.ActionGeneration}, recovery until {m.FlinchUntil:0.000}");
                // Animation.dat row 5 selects TakeHit through this actor's native ACT. Keep clip selection and
                // completion with Actor recovery instead of following it with a rig-only 0x1b2 override.
                return TakeHitPacket(player, id, m, m.FlinchUntil, now);
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
    public static byte[] TakeHitPacket(Player player, uint id, SpawnedMonster m, double until, double now)
    {
        float seconds = (float)(until - now);
        if (m.Walk.OnFrames(now)) m.Walk.Cancel(now);
        if (player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame && m.FlinchUntil == until)
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
    // recoverable unreserved Life). A blocked hit still counts:
    // blocking does not prevent the defender's when-hit effects (wiki Blocking). Call in the area's frame.
    public static void GainLifeBeforeEnemyHitLocked(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        uint lifeCap = LifeCap(player);
        if (player.PlayerLife == 0 || player.PlayerLife >= lifeCap
            || !(Stat.MissingUnreservedLifePctGainedAsLifeBeforeHit is var key)
            || stats.GetValueOrDefault(key) is not (> 0 and var pct)) return;
        if (LifeRecovery.NonLeechBlocked(name => stats.GetValueOrDefault(name))) return;
        var leech = LeechOf(player);
        player.PlayerLife = PetrifiedBlood.RecoverLife(player, player.PlayerLife,
            (lifeCap - player.PlayerLife) * (double)pct / 100.0, ref leech.LifeInstantCarry, minimum: 1);
    }

    // The sequence a predictive swing's 0x1ab carries: the walk-up takes the monster's next sequence, and so does
    // an in-place action. Until now the in-place one kept the 0xffff placeholder, leaving the client with no current
    // action for the matching 0x1af (one access violation in StartChannelling from reusing 0xffff, recorded in
    // ClientState.cs). An explicit sequence a caller passed is kept; a lockstep order takes its sequence from
    // MonsterSwingOrderLocked instead.
    public static ushort MonsterSwingSequence(Player player, SpawnedMonster m, bool walkUp, ushort sequence) =>
        !player.Connection.LockstepClient && (walkUp || sequence == MonsterActionSequence) ? m.NextSequence() : sequence;

    // A swing order written in its send gate at `now` (under conn.Monsters): when it starts (LockstepOrderStart), its
    // attack time, the monster's and the player's cells there, and the monster's state from it; null when the monster
    // cannot act. A generic swing's action speed is read at `now`, not at the order's start: a freeze or chill sent
    // earlier in the frame reaches the client before the order applies.
    public static (double StartedAt, double Seconds, (int X, int Y) From, (int X, int Y) Player, ushort Sequence)? MonsterSwingOrderLocked(
        Player player, SpawnedMonster m, MonsterAttack a, int action, bool generic, double seconds, double now,
        ushort sequence = MonsterActionSequence)
    {
        double startedAt = LockstepOrderStart(player, now);
        if (generic)
        {
            double acceptedFactor = MonsterActionFactor(m, now);
            if (acceptedFactor <= 0) return null;
            seconds = MonsterSwingSeconds(player, m, now) / acceptedFactor;
        }
        m.MeleeClock = null;
        m.BusyUntil = startedAt + seconds + player.LockstepActionTail;
        if (a.Summon is { } summoning) m.SummonReadyAt = startedAt + summoning.CooldownSeconds;
        if (a.CooldownSeconds > 0 && m.Kit.Count == 0) m.CooldownReadyAt = startedAt + a.CooldownSeconds;
        m.SwingGeneration = action;
        m.SwingOrderedAt = startedAt;
        // An in-place action does not touch a lockstep client's walker: a walk under way goes on under it. The Actor
        // stops that walk when it ends the action, with the stop that waits for the tick, so the walk ends with the
        // next frame's steps. The action's own frame counts toward its time here (unlike a hit's recovery): a 0.99 s
        // attack ordered in frame n was ended in n + 30 and its walk stood from n + 31 (tick trace 2026-11-10, two of
        // two; only that duration is traced).
        m.ClientActionWalkStop = player.Connection.LockstepClient && (!generic || a.InPlace) && m.Walk.Moving(startedAt)
            && LockstepCountdownFrames((float)seconds) is { } frames ? startedAt + frames * (FrameMs / 1000d) : null;
        // The accepted action owns the sequence that its later hit must name (0x14219aa10, 0x141e977f0).
        // Assign it after admission and carry it with the order, including through a projectile's flight.
        if (player.Connection.LockstepClient && a.Summon is null) sequence = m.NextSequence();
        return (startedAt, seconds, m.Walk.Position(startedAt), player.Walk.Position(startedAt), sequence);
    }

    public static Task MonsterSwing(Player player, uint id,
        SpawnedMonster m, int action, MonsterAttack? use = null, bool area = false, double areaRadius = 0, ushort? sequence = null,
        double? begunAt = null) =>
        MonsterSwingCore(player, id, m, action, use, area, areaRadius, sequence, begunAt, cadence: null);

    internal static async Task<bool> MonsterSwingCore(Player player, uint id,
        SpawnedMonster m, int action, MonsterAttack? use = null, bool area = false, double areaRadius = 0, ushort? sequence = null,
        double? begunAt = null, MonsterAiCadence? cadence = null)
    {
        if ((use ?? m.Attack) is not { } a) return false;
        // Entry protection: a protected player is not attacked (the planner also skips hostile monsters meanwhile).
        if (!CurrentGameWork || InGracePeriod(player)) return false;
        // A kit skill that is more than a hit: a travel, a buff on itself, a curse (MonsterSkills).
        // Not a swing the client's walk-up began: that one is the chase's melee clip, whatever the kit chose since.
        if (a.Use is { } skill && !area && begunAt is null)
        {
            await MonsterSkills.Play(player, id, m, action, a, skill);
            return false;
        }
        // A walk-up swing is the chase's melee clip: it hits as the kit's plain attack, not as the skill chosen since.
        if (begunAt is not null && a.Use is not null && m.Kit.Count > 0) a = m.Kit[0].Attack;
        // A swing the client's own chase action began when its walk-up got there (a lockstep client, PlanMonsterAi): no
        // order goes out, the clip is the chase order's, and the contact counts from the start of the frame it began in.
        if (begunAt is { } begun && use is null && !area)
        {
            double begunSeconds;
            ushort begunSeq;
            bool nativeClock;
            if (!MonsterActionCurrent(player, id, m, action)) return false;
            double factor = MonsterActionFactor(m, begun);
            if (factor == 0) return false;
            begunSeconds = MonsterSwingSeconds(player, m, begun) / factor;
            begunSeq = m.Sequence;
            nativeClock = BeginMonsterMeleeClockLocked(player, m, action, begunSeq, begun, begun);
            if (ServerTrace.Ai) Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) swings at the player from its walk-up, begun {AreaTime(player) - begun:0.000} s ago, attack time {begunSeconds:0.00} s");
            // A weapon-stanced monster's swing lands on its stance clip's contact, ahead of the native melee clock.
            double? begunStance = StanceContact(player, m, a, begunSeq, "Melee");
            if (AreaSetup.Runs(player))
            {
                bool useClock = nativeClock && begunStance is null;
                double contactAt = useClock ? 0 : begun + begunSeconds * (begunStance
                    ?? (m.Variety is { } v ? SkillsFor(player).MonsterContact(v.Id, "Melee", begunSeq)?.ContactFraction : null) ?? 0.5);
                QueueMonsterMeleeContact(player, cadence, id, m, action, begunSeq, a, useClock, contactAt);
                return true;
            }
            if (nativeClock && begunStance is null)
            {
                if (!await WaitMonsterMeleeContact(player, id, m, action)) return false;
            }
            else
            {
                double begunContact = begunStance
                    ?? (m.Variety is { } v ? SkillsFor(player).MonsterContact(v.Id, "Melee", begunSeq)?.ContactFraction : null) ?? 0.5;
                if (!await WaitMonsterActionTime(player, id, m, action, begun + begunSeconds * begunContact)) return false;
            }
            await MonsterContact(player, id, m, action, begunSeq, a);
            return false;
        }
        // Its attack time now: a chill slows it by its action speed, Devout of Gruthkul by the player's attacker slow.
        // A King skill carries its own cast time. Predictive keeps its existing sequence selection;
        // lockstep assigns the actor's sequence when the send gate admits the action below.
        double seconds;
        ushort seq;
        if (use is null && !area)
        {
            double factor = MonsterActionFactor(m, ProjectileWorldTime(player));
            if (factor == 0) return false;
            seconds = MonsterSwingSeconds(player, m, ProjectileWorldTime(player)) / factor;
            seq = sequence ?? MonsterActionSequence;
        }
        else
        {
            seconds = a.Seconds;
            seq = sequence ?? player.NextActionSequence();
        }
        double startedAt = 0;
        bool meleeClock = false;
        (int X, int Y)? projectileAim = null;
        (int X, int Y) stormAim = default;
        (int X, int Y) areaAim = default;
        (int X, int Y) beamOrigin = default, beamAim = default;
        bool walkUp = use is null && !area && !a.InPlace;
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            double now = AreaTime(player);
            if (MonsterSwingOrderLocked(player, m, a, action, use is null && !area, seconds, now, seq) is not { } order) return null;
            (startedAt, seconds, seq) = (order.StartedAt, order.Seconds, order.Sequence);
            var (from, playerAt) = (order.From, order.Player);
            (beamOrigin, beamAim) = (from, playerAt);
            // The client-drawn straight mortar retains the action order's aim throughout its windup
            // (141ba7c30 -> 141b6c630). Reacquiring here at release would turn a dodged shot toward the player.
            if (a.Projectile is { Variation: > 0, Lobbed: false }) projectileAim = playerAt;
            // A storm is aimed where the player stood when the cast began; its falls scatter around that cell.
            if (a.Storm is not null) stormAim = playerAt;
            // An area skill lands where its action aims (a nova around its user), wherever the player goes meanwhile.
            areaAim = a.AreaAtSelf ? from : playerAt;
            seq = MonsterSwingSequence(player, m, walkUp, seq);
            a = MonsterAttackForSequence(player, m, a, seq);
            if (walkUp) meleeClock = BeginMonsterMeleeClockLocked(player, m, action, seq, startedAt, now);
            // A summoning skill's action (ActionTypes SummonSpecificMonstersInFront) plays nothing on the client (live
            // 2026-11-01: the Immolator stayed shut): its actor's clip is played instead (0x1d1, SummonAnimation).
            if (a.Summon is not null) return InstanceProtocol.ActorAnimation(id, (float)seconds, SummonAnimation);
            return walkUp ? InstanceProtocol.ActionStartedNow(id, from.X, from.Y, playerAt.X, playerAt.Y, a.SkillId, seq, 0, player.Id)
                : InstanceProtocol.ActionStarted(id, from.X, from.Y, playerAt.X, playerAt.Y, a.SkillId, seq, player.MonsterOrderFlags, targetObjectId: player.Id);
        }, ServerTrace.Combat || ServerTrace.Ai
            ? (a.Summon is not null ? $"0x01d1 monster 0x{id:x} ({m.Label}) summons, cast time {seconds:0.00} s"
                : $"0x01ab monster 0x{id:x} ({m.Label}) attacks the player with {(a.Effect is int used ? SkillsFor(player).EffectId(used) : null) ?? "its attack"} 0x{a.SkillId:x4}, attack time {seconds:0.00} s")
            : null)) return false;
        if (a.Summon is null && BossKits.EffectOf(m, a) is int channelEffect && SkillsFor(player).IsChannelled(channelEffect))
        {
            double remaining = Math.Max(0, startedAt + seconds - AreaTime(player));
            SendAfter(player, remaining, () => MonsterActionCurrent(player, id, m, action)
                ? SendWorldRecord(player, id, InstanceProtocol.ChannelFinished(id, seq), $"0x01ae monster 0x{id:x} channel 0x{seq:x4} ended")
                : Task.CompletedTask);
        }
// A clip-only action (a dev command's NoContact) lands nothing: its hit or area belongs to the caller, which
        // sends it on its own clock. A zero-damage contact here would only play the clip's hit effect.
        if (a.NoContact) return true;
        if (IsEssenceExplosiveInsect(m.Variety?.Id))
        {
            if (await WaitMonsterActionTime(player, id, m, action, startedAt + seconds))
                await ExplodeEssenceInsect(player, id, m, a, action);
            return false;
        }
        if (BossKits.EffectOf(m, a) == BossKits.PietyBeamEffect)
        {
            // All recipients use the original cast's aim and the shared instance clock. Each owns its hit gate,
            // defences, minions and cooldown, including after the original target dies or moves out of the sweep.
            var primary = RunPietyBeam(player, id, m, a, action, seq, startedAt, seconds, beamOrigin, beamAim);
            var peers = player.Instance?.Presence.PlayersExcept(player.Id);
            if (peers is { Length: > 0 })
                await Task.WhenAll(peers.Where(other => !other.Closed && !other.LeavingArea && ReferenceEquals(other.Instance, player.Instance))
                    .Select(other => RunPietyBeam(other, id, m, a, action, seq, startedAt, seconds, beamOrigin, beamAim)).Append(primary));
            else await primary;
            return false;
        }
        // The contact: the clip's keyframe (the Spitter's attack1 spits at 37.5 % of the attack; GGG's melee hits land by
        // their clip too), half the attack time without rig data. A weapon-stanced monster's skill lands on its stance
        // clip's contact, ahead of the native melee clock.
        double? stanceContact = a.Release is null ? StanceContact(player, m, a, seq) : null;
        if (AreaSetup.Runs(player) && !area && a.AreaRadius <= 0 && a.Hits <= 1 && a.Summon is null && a.Trap is null && a.Strike is null && a.Storm is null && a.Projectile is null)
        {
            bool useClock = meleeClock && stanceContact is null;
            double contactAt = useClock ? 0 : startedAt + seconds * (a.Release?.Fraction ?? stanceContact ?? (walkUp && m.Variety is { } variety
                ? SkillsFor(player).MonsterContact(variety.Id, "Melee", seq)?.ContactFraction : null) ?? 0.5);
            QueueMonsterMeleeContact(player, cadence, id, m, action, seq, a, useClock, contactAt);
            return true;
        }
        if (meleeClock && stanceContact is null)
        {
            if (!await WaitMonsterMeleeContact(player, id, m, action)) return false;
        }
        else
        {
            // Other contacts retain their fraction of the attack time, or half without rig data. A known melee
            // clock does not select this unused fallback or load the legacy contact corpus at its first swing.
            double contact = a.Release?.Fraction ?? stanceContact ?? (walkUp && m.Variety is { } variety
                ? SkillsFor(player).MonsterContact(variety.Id, "Melee", seq)?.ContactFraction : null) ?? 0.5;
            if (a.Projectile is not null && a.Effect is int barrageEffect
                && BossMoves.BarrageOf(SkillsFor(player), barrageEffect) is { } pendingBarrage)
                m.BusyUntil = Math.Max(m.BusyUntil,
                    startedAt + MonsterLaunchDelay(a, contact, seconds) + (pendingBarrage.Count - 1) * pendingBarrage.Seconds + player.LockstepActionTail);
            if (player.Connection.LockstepClient && a.Projectile is { Variation: > 0, Lobbed: false } nativeProjectile)
            {
                // Like the player's native shot, the monster emits at the end of the frame that crosses contact.
                // Starting between frames lets the server hit a moving player before the visible flight reaches them.
                LaunchMonsterProjectile(player, id, m, a, nativeProjectile, action, projectileAim,
                    LockstepShotAt(startedAt, MonsterLaunchDelay(a, contact, seconds)), seq);
                return false;
            }
            if (!await WaitMonsterActionTime(player, id, m, action, startedAt + MonsterLaunchDelay(a, contact, seconds))) return false;
        }
        if (a.Summon is { } summon)
        {
            await MonsterSummon(player, id, m, summon);
            return false;
        }
        if (a.Storm is { } storm)
        {
            // Its falls begin as the cast ends and run for the skill's own duration, one per delay;
            // each fall resolves on the server where the bolt would have landed (no lobbed bolt per fall:
            // the client draws the storm from the cast). The falls outlive the action, so they stop with
            // the monster, not with the action.
            MonsterFirestormStorm(player, id, m, a, action, storm, startedAt + seconds, stormAim, seq);
            return false;
        }
        if (a.Trap is { } payload)
        {
            await ThrowMonsterTraps(player, id, m, a, payload, areaAim);
            return false;
        }
        if (a.Strike is { } strike)
        {
            // A hazard that stays (BossMoves.Strikes: Merveil's geysers): several come up, the first under the player and
            // the rest around it, and each strikes again while its art stands.
            if (a.Effect is int strikeEffect && BossMoves.StrikeOf(SkillsFor(player), strikeEffect, m.Level) is { } many)
            {
                List<(int X, int Y)> spots = [areaAim, .. BossMoves.StrikeSpots(areaAim, many.Count - 1, many.Spread, AreaRng(player), player.Layout, player.Barrier)];
                foreach (var spot in spots)
                {
                    await PlaceMonsterMarker(player, id, m, a, strike, spot);
                    for (int pulse = 1; pulse <= many.Pulses; pulse++)
                        await PlaceMonsterMarker(player, id, m, a,
                            strike with { Seconds = strike.Seconds + pulse * many.Pulse, MarkerArt = null, ImpactArt = null }, spot);
                }
                return false;
            }
            await PlaceMonsterMarker(player, id, m, a, strike, areaAim);
            return false;
        }
        if (a.Projectile is { } projectile)
        {
            // The shot leaves at the contact and flies; its hit lands when it reaches the player (Program.ProjectileHostile.cs).
            LaunchMonsterProjectile(player, id, m, a, projectile, action, projectileAim, sequence: seq);
            // A barrage (BossMoves.Barrages: Merveil's Ice Spears): the rest follow from the same cast, each at where
            // the player then stands, while the action is still the monster's.
            if (a.Effect is int shotEffect && BossMoves.BarrageOf(SkillsFor(player), shotEffect) is { } barrage)
            {
                double releaseNow = AreaTime(player);
                m.BusyUntil = Math.Max(m.BusyUntil, releaseNow + (barrage.Count - 1) * barrage.Seconds + player.LockstepActionTail);
                for (int i = 1; i < barrage.Count; i++)
                    LaunchMonsterProjectile(player, id, m, a, projectile, action, null, releaseNow + i * barrage.Seconds,
                        sequence: seq, turn: true);
            }
            return false;
        }
        if (a.IsDischarge)
        {
            if (MonsterKits.PrepareDischargeContactLocked(player, id, m, a) is not { } charged) return false;
            a = charged;
        }
        if (a.AreaRadius > 0 && !area)
        {
            await MonsterAreaHit(player, id, m, a, action, areaAim, a.AreaRadius * m.Effects.AreaScaleFactor, seq);
            return false;
        }
        if (area) { await MonsterAreaContact(player, id, m, a, action, areaRadius, seq); return false; }
        await MonsterContact(player, id, m, action, seq, a);
        // A multi-strike skill's later hits land on the clip's later contact frames, within the same action.
        if (a.Hits > 1 && StanceClipOf(player, m, a, seq) is { } clip)
            for (int hit = 1; hit < a.Hits && clip.ContactFractionAt(hit) is double at; hit++)
            {
                if (!await WaitMonsterActionTime(player, id, m, action, startedAt + seconds * at)) return false;
                await MonsterContact(player, id, m, action, seq, a);
            }
        return false;
    }

    /// <summary>Animation.dat <c>SpellAreaOfEffect</c>, which a summoner's actor plays as it summons (the Immolator's
    /// <c>.act</c>: <c>spawn</c>, its doors opening and shutting).</summary>
    public const byte SummonAnimation = 22;

    /// <summary>How many of its monsters a summoner keeps alive at once when its skill names no limit (our number: the
    /// SummonSpecificMonsters data has no cap; the original's Immolators keep bringing skeletons while the player is near).</summary>
    public const int MaxSummoned = 5;

    // In the area's frame: whether a monster's summoning skill may be used now (off cooldown and, for a skill with no
    // limit of its own, fewer than MaxSummoned of its monsters alive; one with a limit retires its oldest instead);
    // any other attack may.
    public static bool SummonReadyLocked(Player player, uint id, SpawnedMonster m, MonsterAttack a, double now) =>
        a.Summon is null || now >= m.SummonReadyAt && (a.Summon.Limit > 0
            || player.Area.Monsters.Values.Count(s => s.SummonerId == id && !s.Dead && s.Life > 0) < MaxSummoned);

    // A summoning skill's contact: its monsters (normal, at the summoner's level) where the skill puts them, its offset
    // ahead of the summoner towards the player (summon_specific_monsters_in_front_offset); hostile, as their summoner's,
    // and giving no experience or items when the skill says so.
    public static async Task MonsterSummon(Player player, uint id,
        SpawnedMonster m, SkillCatalog.MonsterSummonInfo summon)
    {
        if (Monsters.Find(summon.Variety.ToString()) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) summons MonsterVarieties row {summon.Variety}: it cannot be created");
            return;
        }
        (int X, int Y) at;
        if (!MonsterSummonerCurrentLocked(player, id, m)) return;
        List<uint> surplus;
        double expires;
        double now = AreaTime(player);
        var (from, playerAt) = (m.Walk.Position(now), player.Walk.Position(now));
        double dx = playerAt.X - from.X, dy = playerAt.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy), ahead = summon.Offset / WorldUnits.PerCell;
        at = summon.AtTarget ? player.Layout.Near(playerAt)
            : length < 1 ? from : ((int)Math.Round(from.X + dx / length * ahead), (int)Math.Round(from.Y + dy / length * ahead));
        surplus = SurplusMinionsLocked(player, id, summon, summon.Count);
        expires = now + summon.Lifetime;
        if (surplus.Count > 0) await RetireSummonedMonsters(player, surplus, "makes room for a newer one");
        // A body summoned for a skill (a totem) holds that skill alone, as its kit's slot 0x4000, and acts with it.
        var acts = summon.Effect >= 0 ? BossKits.BodyAttack(player, variety, m.Level, summon.Effect, 0x4000) : null;
        if (summon.Effect >= 0 && acts is null) return;
        IReadOnlyList<GrantedSkill>? skills = acts is null ? null
            : BossKits.SkillsOf(player, variety, m.Level, [new MonsterKitSkill(summon.Effect, acts, 0)]);
        int created = 0;
        for (int k = 0; k < summon.Count; k++)
            if (await SpawnMonster(player, variety, m.Level, MonsterRarity.Normal, at.X, at.Y, alone: true,
                extraMods: EssenceSummonModifiers(variety),
                ownedSkills: skills,
                extraKit: summon.Effect >= 0 ? null : BossKits.SummonKit(player, variety, m.Level),
                ownedAdmission: () => MonsterSummonerCurrentLocked(player, id, m) && EssenceSummonCanSpawnLocked(player, id, variety),
                owned: (_, summoned) =>
                {
                    RegisterMonsterSummonLocked(summoned, id, m, summon);
                    summoned.ExpiresAt = expires;
                    if (acts is not null) summoned.Attack = acts;
                }) != 0) created++;
        Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) summons {created}/{summon.Count} {variety.Name} at ({at.X}, {at.Y})");
        // Doubt's spiders land with their mortar burst; the essence's own landing art for the variety.
        if (created > 0) MapEssences.OnSummonSpawned(player, variety.Id, at);
    }

    /// <summary>Piety, the Abomination's rotating 360-degree beam (<c>PietyCorruptedLightningBeam</c>): aims at the
    /// player's cell on cast, then sweeps −900° (2.5 clockwise turns) across the active window of its 11.2 s cast at
    /// 120-cell reach and 0.33 s per-target hit cooldown (3.29.3c client RE: 0x141b491a0, 0x141b49320, 0x141b4960d,
    /// 0x141b4974d).</summary>
    public static async Task RunPietyBeam(Player player,
        uint id, SpawnedMonster m, MonsterAttack a, int action, ushort sequence, double startedAt, double seconds,
        (int X, int Y) origin, (int X, int Y) aim)
    {
        const double WindupFraction = 0.14, EndFraction = 0.89, SweepRadians = -5 * Math.PI,
            Length = 120, HalfWidth = 4.5, HitCooldown = 0.33, MinionDamageFactor = 0.45;
        double sweepStart = startedAt + seconds * WindupFraction,
            sweepEnd = startedAt + seconds * EndFraction;
        if (!await WaitMonsterActionTime(player, id, m, action, sweepStart)) return;
        double startAngle = aim == origin ? 0 : Math.Atan2(aim.Y - origin.Y, aim.X - origin.X);
        double prevAngle = startAngle, nextPlayerHit = double.NegativeInfinity;
        MonsterAttack? minionAttack = null;
        Dictionary<uint, double>? nextMinionHit = null;
        while (MonsterActionCurrent(player, id, m, action))
        {
            double now;
            bool paused, frozen, shouldHitPlayer = false, minionHit = false;
            double angle = prevAngle;
            paused = player.Paused;
            now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
            frozen = MonsterActionFactor(m, now) == 0;
            if (!paused && !frozen && now < sweepEnd)
            {
                double t = Math.Clamp((now - sweepStart) / Math.Max(0.01, sweepEnd - sweepStart), 0, 1);
                angle = startAngle + SweepRadians * t;
                bool protectedByGrace = InGracePeriod(player) || MonsterAsleep(player, id);
                if (!protectedByGrace && player.Golems.All.Count + player.Reservations.Fighters.Count + player.Summons.Fighters.Count > 0)
                {
                    minionAttack ??= a with { Damage = a.Damage.Scale(MinionDamageFactor) };
                    foreach (var golem in player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values).ToArray())
                    {
                        if (golem.Monster.Dead || golem.Monster.Life == 0) continue;
                        nextMinionHit ??= [];
                        if (now < nextMinionHit.GetValueOrDefault(golem.Id, double.NegativeInfinity)) continue;
                        if (!InSweptBeam(origin, golem.Monster.Walk.Position(now), prevAngle, angle, Length, HalfWidth)) continue;
                        nextMinionHit[golem.Id] = now + HitCooldown;
                        if (GolemTakeHitLocked(player, golem, id, m, minionAttack, now, area: true)) minionHit = true;
                    }
                }
                if (!protectedByGrace && now >= nextPlayerHit && InSweptBeam(origin, player.Walk.Position(now), prevAngle, angle, Length, HalfWidth))
                    shouldHitPlayer = true;
            }
            if (frozen) return;
            if (paused) { await AreaWait(PlayerUpdateRole.MonsterAction, 50); continue; }
            if (now >= sweepEnd) return;
            if (minionHit) await PublishMinionOutcomes(player);
            if (shouldHitPlayer)
            {
                MonsterHitOutcome? outcome = null;
                double hitFrom = prevAngle, hitTo = angle;
                if (await SendMonsterAction(player, id, m, action, () =>
                {
                    if (InGracePeriod(player) || MonsterAsleep(player, id)) return null;
                    double hitNow = Math.Max(ProjectileWorldTime(player), AreaTime(player));
                    if (hitNow >= sweepEnd || MonsterActionFactor(m, hitNow) == 0 || hitNow < nextPlayerHit) return null;
                    var here = player.Walk.Position(hitNow);
                    if (!InSweptBeam(origin, here, hitFrom, hitTo, Length, HalfWidth)) return null;
                    nextPlayerHit = hitNow + HitCooldown;
                    outcome = CommitMonsterHitLocked(player, id, m, a, hitNow, here, origin, checkRange: false, sequence: sequence);
                    return outcome?.HitPacket;
                }, $"0x01af monster 0x{id:x} ({m.Label}) beam hit on the player") && outcome is not null)
                    await PresentMonsterHit(player, id, outcome);
            }
            prevAngle = angle;
            await AreaWait(PlayerUpdateRole.MonsterAction, 50);
        }
    }

    static bool InSweptBeam((int X, int Y) origin, (int X, int Y) target, double fromAngle, double toAngle, double length, double halfWidth)
    {
        double dx = target.X - origin.X, dy = target.Y - origin.Y;
        double dist2 = dx * dx + dy * dy;
        if (dist2 > (length + halfWidth) * (length + halfWidth)) return false;
        if (dist2 <= halfWidth * halfWidth) return true;
        // Check distance to the current beam ray, plus whether the target's bearing fell inside the angular wedge
        // swept since the previous tick (fromAngle >= toAngle because SweepRadians is negative).
        double cos = Math.Cos(toAngle), sin = Math.Sin(toAngle);
        double proj = Math.Clamp(dx * cos + dy * sin, 0, length);
        double px = dx - proj * cos, py = dy - proj * sin;
        if (px * px + py * py <= halfWidth * halfWidth) return true;
        double sweep = Math.Abs(toAngle - fromAngle);
        if (sweep <= 1e-6) return false;
        double bearing = Math.Atan2(dy, dx);
        double delta = (fromAngle - bearing) % (2 * Math.PI);
        if (delta < 0) delta += 2 * Math.PI;
        return delta <= sweep && dist2 <= length * length;
    }

    /// <summary>An area skill's contact: the player takes the hit when they still stand in the aimed area. The area is
    /// centred on the target cell captured when the action began; moving out of it dodges the hit.</summary>
    public static async Task MonsterAreaContact(Player player,
        uint id, SpawnedMonster m, MonsterAttack a, int action, double radius, ushort sequence = 0)
    {
        if (sequence == 0) sequence = player.NextActionSequence();
        (int X, int Y) aim = default;
        double startedAt = 0;
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            startedAt = AreaTime(player);
            var from = m.Walk.Position(startedAt);
            aim = player.Walk.Position(startedAt);
            m.BusyUntil = startedAt + a.Seconds;
            m.SwingGeneration = action;
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, a.SkillId, sequence, player.MonsterOrderFlags, targetObjectId: player.Id);
        }, $"0x01ab monster 0x{id:x} ({m.Label}) uses area skill 0x{a.SkillId:x4}, radius {radius:0.#}")) return;
        if (!await WaitMonsterActionTime(player, id, m, action, startedAt + a.Seconds * 0.5)) return;
        await MonsterAreaHit(player, id, m, a, action, aim, radius, sequence);
    }

    /// <summary>An area skill's hit at its contact: every golem and the player within <paramref name="radius"/> of
    /// <paramref name="aim"/>.</summary>
    public static async Task MonsterAreaHit(Player player,
        uint id, SpawnedMonster m, MonsterAttack a, int action, (int X, int Y) aim, double radius, ushort sequence)
    {
        if (a.Damage.IsZero)
        {
            await MonsterAreaDebuffs(player, id, a, aim, radius);
            await LeaveSkillGround(player, id, a, aim);
            return;
        }
        MonsterHitOutcome? outcome = null;
        if (!await SendMonsterAction(player, id, m, action, () =>
            (outcome = AreaHitLocked(player, id, m, a, aim, radius, sequence))?.HitPacket,
            $"0x01af monster 0x{id:x} area hit on the player")) return;
        if (outcome is not null) await PresentMonsterHit(player, id, outcome);
        await LeaveSkillGround(player, id, a, aim);
    }

    /// <summary>The ground a monster skill leaves where it landed (MonsterAttack.Ground), unless its own action draws
    /// it on another object (a trap's: MonsterTraps).</summary>
    public static async Task LeaveSkillGround(Player player, uint owner, MonsterAttack a, (int X, int Y) at)
    {
        if (a.Ground is not { } ground || GroundKindOf(ground.Type, ground.ArtVariation) is not { } kind) return;
        await PlaceHostileGround(player, owner, ground.ClientDrawn ? kind with { Art = "" } : kind, ground.Stats, at,
            ground.Radius, ground.Seconds);
    }

    /// <summary>A monster skill's area hit that no longer belongs to the monster's own action: a trap of its going off,
    /// a strike that lands after the cast. It is gated on <paramref name="still"/> (its source still there) at the send
    /// gate instead of on the monster's current swing; the monster's stats and damage pipeline still make the hit.</summary>
    public static async Task MonsterDetachedAreaHit(Player player,
        uint id, SpawnedMonster m, MonsterAttack a, (int X, int Y) aim, double radius, ushort sequence, Func<bool> still)
    {
        if (a.Damage.IsZero)
        {
            if (!still()) return;
            await MonsterAreaDebuffs(player, id, a, aim, radius);
            await LeaveSkillGround(player, id, a, aim);
            return;
        }
        MonsterHitOutcome? outcome = null;
        await SendWorldRecord(player, id, [], $"0x01af monster 0x{id:x} ({m.Label}) area hit on the player",
            atGate: () =>
            {
                using (ProjectileMutation(player, phase: "send"))
                {
                    if (!still() || player.Paused || player.PlayerLife == 0) return null;
                    var bytes = (outcome = AreaHitLocked(player, id, m, a, aim, radius, sequence))?.HitPacket;
                    return bytes;
                }
            });
        if (outcome is not null) await PresentMonsterHit(player, id, outcome);
        if (still()) await LeaveSkillGround(player, id, a, aim);
    }

    /// <summary>A skill that deals no damage (Wither): the player standing in its area at its contact gets its debuffs,
    /// with no hit.</summary>
    static async Task MonsterAreaDebuffs(Player player, uint id, MonsterAttack a, (int X, int Y) aim, double radius)
    {
        if (a.Debuffs.Count == 0) return;
        bool inside;
        inside = !InGracePeriod(player) && player.PlayerLife > 0 && Dist(player.Walk.Position(AreaTime(player)), aim) <= radius;
        if (!inside) return;
        foreach (var spec in a.Debuffs) await AddPlayerDebuff(player, spec.Row, id, spec.Stats, spec.Seconds);
    }

    // At the send gate: every golem and the player within radius of aim take the hit; the player's outcome, or null.
    static MonsterHitOutcome? AreaHitLocked(Player player, uint id, SpawnedMonster m, MonsterAttack a,
        (int X, int Y) aim, double radius, ushort sequence)
    {
        if (InGracePeriod(player)) return null;
        double now = AreaTime(player);
        var here = player.Walk.Position(now);
        foreach (var golem in player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).ToArray())
            if (!golem.Monster.Dead && Dist(golem.Monster.Walk.Position(now), aim) <= radius)
                GolemTakeHitLocked(player, golem, id, m, a, now, area: true);
        double dx = here.X - aim.X, dy = here.Y - aim.Y;
        if (dx * dx + dy * dy > radius * radius) return null;
        return CommitMonsterHitLocked(player, id, m, a, now, here, aim, checkRange: false, sequence: sequence);
    }

    // A melee contact at the clip's keyframe: committed at the actual send gate under the monster's action, so a
    // flinch accepted while it was queued suppresses both the packet and the life mutation. A ranged monster's shot commits
    // the same pipeline when its flight reaches the player instead (Program.ProjectileHostile.cs).
    public static async Task MonsterContact(Player player,
        uint id, SpawnedMonster m, int action, ushort sequence = MonsterActionSequence, MonsterAttack? use = null)
    {
        if ((use ?? m.Attack) is not { } a) return;
        MonsterHitOutcome? outcome = null;
        if (!await SendMonsterAction(player, id, m, action, () =>
        {
            // Entry protection: the player standing protected on the arrival cell takes no hit.
            if (InGracePeriod(player)) return null;
            // The contact time is the later of the projectile runtime's committed time (a buffered input cannot rewrite
            // that history) and the connection clock: the runtime is not advanced at this gate, so its time can lag a
            // clock read the life tick or a mortar landing already made, and the fortification and recent-hit trackers
            // refuse to go backwards (review 2026-10-30: the refusal faulted the swing's task and the hit was lost).
            double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
            // A freeze that landed during the swing interrupts it.
            if (MonsterActionFactor(m, now) == 0)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) frozen: its swing is cancelled");
                return null;
            }
            outcome = CommitMonsterHitLocked(player, id, m, a, now, player.Walk.Position(now), m.Walk.Position(now), checkRange: true, sequence: sequence);
            return outcome?.HitPacket;
        }, ServerTrace.Combat ? $"0x01af monster 0x{id:x} hit on the player (kind 3, sequence 0x{sequence:x4})" : null)) return;
        if (outcome is not null) await PresentMonsterHit(player, id, outcome);
        // An essence's Misery rare adds a stack to the player its attack hits (the wiki); a monster without it is a
        // cheap no-op.
        if (outcome is not null) MapEssences.MiseryOnAttack(player, m, player);
    }

    static int DamageTakenFromMonster(SpawnedMonster? monster, IReadOnlyDictionary<uint, int> stats) =>
        stats.GetValueOrDefault(Stat.DamageTakenPlusPctFromSkeletons) is int skeletons and not 0
        && monster?.Variety is { } variety && MonsterModRolls.HasVarietyTag(variety.Row, "skeleton") ? skeletons : 0;

    /// <summary>The monster hit packet's flags: generic particles (0x4000), main hand (0x0200) and blood (0x0001) always,
    /// 0x0040 for a blocked hit and 0x0020 for a critical strike (action-hit-packet-3.29.3b.md §2).</summary>
    public static ushort MonsterHitFlags(bool blocked, bool critical) =>
        (ushort)(0x4201 | (blocked ? 0x0040 : 0) | (critical ? 0x0020 : 0));

    /// <summary>What a monster's hit on the player did (CommitMonsterHitLocked), for the packets that present it.</summary>
    public sealed record MonsterHitOutcome(byte[] HitPacket, uint Life, uint Mana, uint ManaBefore, int Damage, int RawDamage,
        bool Blocked, bool Critical, PlayerRecoveryIntent? Recovery, RecentEnemyHits.Decision Armour, bool Spirit = false)
    {
        /// <summary>The caster and the debuffs its unblocked hit puts on the player (MonsterAttack.Debuffs).</summary>
        public (uint Caster, IReadOnlyList<MonsterDebuffSpec> Specs)? Debuffs { get; init; }
    }

    // The monster's hit on the player, committed in the area's frame at the moment it lands: reach (melee only),
    // evasion, the mod-rolled packet, block, armour, resistances, ailments, recovery and death. Null when it misses or is
    // evaded. `from` is where the hit comes from: the monster for a swing, the shot's contact point for a projectile.
    public static MonsterHitOutcome? CommitMonsterHitLocked(Player player, uint id, SpawnedMonster m, MonsterAttack a,
        double now, (int X, int Y) here, (int X, int Y) from, bool checkRange, ushort sequence = MonsterActionSequence)
    {
        // A retired producer is refused before recording an accepted hit or touching any current resource state.
        if (!CurrentGameWork) return null;
        bool spellHit = a.Spell && !a.Secondary, attackHit = !a.Spell && !a.Secondary;
        bool projectile = a.Projectile is not null;
        int priorContacts = player.RecentEnemyHits.Count(now);
        using var defenceTime = FortificationOf(player).Capture(now);
        // Resolve lazy final defences here, never at AI collection or before a queued echo/pause wait.
        var stats = PlayerStatValues(player);
        int StatOf(uint key) => stats.GetValueOrDefault(key);
        var reduction = PlayerDamageReduction(player, stats);
        if (checkRange && Dist(here, from) > a.Range + 2)
        {
            if (ServerTrace.Combat)
                Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} missed: the player is {Dist(here, from):0.0} away");
            return null;
        }
        if (spellHit && DodgesSpellHit(player)) return null;
        // The monster's accuracy comes from DefaultMonsterStats at its level. Evasion uses PoE's shared entropy
        // counter: every attack adds its hit chance, and reaching 100 consumes 100 and hits (evasion cannot streak
        // indefinitely). A new counter starts after 3.33 s without an attack.
        int baseAccuracy = m.Effects.Accuracy(Monsters.BaseAccuracy(m.Variety, m.Level) ?? 0);
        int monsterAccuracy = MonsterBlindRating(player, id, m, baseAccuracy, now, stats);
        int evasion = TimedBlind.Apply(PlayerEvasionBase(player, now), PlayerBlindValue(player, stats).Less);
        evasion = EvasionKeystones.Rating(evasion, projectile, !a.Ranged && !a.Spell, StatOf);
        int hitChance = DamageModel.AccuracyHitChance(evasion, monsterAccuracy);
        bool cannotEvade = StatOf(Stat.KeystoneUnwaveringStance) != 0 || StatOf(Stat.CannotEvade) != 0
            || StatOf(Stat.BaseCannotEvade) != 0 || StatOf(Stat.MapCannotEvade) != 0;
        if (attackHit && !cannotEvade && StatOf(Stat.ChanceToEvadeAttacksPct) is int additionalEvade and not 0)
            hitChance = (int)Math.Clamp((long)hitChance - additionalEvade, 5L, 100L);
        if (attackHit && EvadesLocked(player, hitChance, now))
        {
            if (ServerTrace.Combat)
                Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) evaded: accuracy {monsterAccuracy} " +
                    $"vs evasion {evasion}, {hitChance}% chance to hit, entropy {player.EvasionEntropy:0}");
            return null;
        }
        GainLifeBeforeEnemyHitLocked(player, stats);
        // Roll the whole monster-mod packet (the Gambler's incoming luck included), then mitigate each component
        // with this hit's one armour decision.
        var gambler = GamblerFor(stats);
        // The King's Mystic Fetishes leave Portent Vapors: 10% increased damage taken per stack from his hits only.
        var hitDamage = a.Damage;
        if (m.EncounterStats is { } encounter) hitDamage = MonsterModEffects.AddedTo(hitDamage, encounter);
        foreach (var temporary in m.TemporaryStats.Values) hitDamage = MonsterModEffects.AddedTo(hitDamage, temporary);
        if (KingInMists.IsBoss(m.Variety) && player.KingPortentStacks > 0 && now < player.KingPortentUntil)
            hitDamage = hitDamage.Scale(1 + 0.1 * player.KingPortentStacks);
        var suppression = spellHit ? Suppression(player) : default;
        bool RollSuppression() => spellHit && suppression.Chance > 0
            && DamageModel.Suppresses(suppression.Chance, player.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
        // Vaal Grace needs the suppression outcome before rolling the damage. Other hits keep their ordinary roll order.
        bool suppressionChangesRoll = spellHit && StatOf(Stat.DamageTakenFromSuppressedHitsIsUnlucky) != 0;
        bool suppressed = suppressionChangesRoll && RollSuppression();
        if (suppressed && gambler.EnemyDamageExtraRolls >= 0)
            gambler = gambler with { EnemyDamageExtraRolls = gambler.EnemyDamageExtraRolls - 1 };
        var rolled = gambler.RollIncomingHit(hitDamage, player.IncomingHitRandom);
        int baseCritPermyriad = a.Secondary ? 0
            : spellHit ? (a.Effect is int eff ? SkillsFor(player).SpellCritChance(eff, m.Level) ?? 0 : 0)
            : MonsterBaseCritChancePermyriad;
        rolled = MonsterModHit(m, rolled, player.PlayerLife, player.PlayerMaxLife, player.IncomingHitRandom, out bool monsterCritical,
            () => SelfCriticalReduction(player),
            now: now, cannotCrit: VaalOversoul.CannotCrit(m, a), additionalCritical: PlayerBrittleCriticalChance(player, now),
            // Overwhelm's aura lowers the multiplier of a monster in it: -30 base_critical_strike_multiplier_+.
            extraCriticalMultiplier: () => EnemyAuraStatLocked(player, m, here, now, Stat.BaseCriticalStrikeMultiplierPlus),
            baseCritPermyriad: baseCritPermyriad);   // its crit, Frenzy charges, Executioner's
        // The player's passive enemy auras (Born under Fire): the hit and its ailment base convert alike, so ignite
        // sees the converted damage. Converting does not remove stun, so the hit recovery below reads a.Damage.
        var baseHit = a.Damage.Scale(SpiderOutgoingFactor(m, now));
        if (EnemyAuraConversionLocked(player, m, here, now, StatOf) is { } auraConversion)
            (rolled, baseHit) = (rolled.Convert(auraConversion), baseHit.Convert(auraConversion));
        // Glacial Wave and Sap: less damage dealt is all of it (PoB Damage MORE), so the ignite its crit puts on you too.
        double lessened = m.Ailments.ChillDamageDealtFactor(now) * SappedDamageFactor(m, now);
        if (lessened < 1) (rolled, baseHit) = (rolled.Scale(lessened), baseHit.Scale(lessened));
        // Damage taken as shifts the hit's types before any mitigation. Chill, freeze and shock read the shifted damage
        // taken; ignite still reads the damage dealt (baseHit), as the wiki's Damage taken as says.
        // Then each type taken may be avoided, and ignite reads only the damage dealt that lands.
        if (!suppressionChangesRoll) suppressed = RollSuppression();
        // "when you Suppress Spell Damage from a Unique Enemy" (Loyalty Tattoo of Ahuana).
        if (suppressed && m.Rarity == MonsterRarity.Unique && player.PlayerLife > 0)
            LoyaltySuppressedHitLocked(player, stats);
        var shifted = PlayerDamageTaken.Shift(rolled, StatOf);
        var taken = PlayerDamageTaken.Avoid(shifted, StatOf, player.IncomingHitRandom, out var avoided, ElusiveAvoidPct(player),
            projectile, projectile && player.RecentEvents.Happened(TakenProjectileDamage, now));
        baseHit = PlayerDamageTaken.Landed(baseHit, StatOf, avoided);
        int elemPen = Math.Max(0, m.Effects[Stat.ReduceEnemyElementalResistancePct]);
        var mitigated = gambler.MitigateIncomingHit(taken, type =>
            new DamageModel.ResistanceValue(Resistance(player, type), MaxResistance(player, type),
                Penetration: type is DamageType.Fire or DamageType.Cold or DamageType.Lightning ? elemPen : 0), player.IncomingHitRandom);
        var immune = PlayerImmunities(player, stats);
        // This remains the original next RNG draw when the feature is absent. Blocking never
        // consumes an armour draw; one lazy decision is shared by the whole rolled packet.
        int blockChance = a.Spell ? player.Pools.SpellBlockChance : player.Pools.BlockChance;
        if (projectile)
            blockChance = stats.GetValueOrDefault(a.Spell ? Stat.SpellProjectileBlockPct : Stat.ProjectileAttackBlockPct, blockChance);
        bool blocked = !a.Secondary && DamageModel.Blocks(blockChance,
            player.IncomingHitRandom.NextDouble() * 100, a.Spell ? player.Pools.SpellBlockLucky : player.Pools.AttackBlockLucky,
            a.Spell ? player.Pools.SpellBlockUnlucky : player.Pools.AttackBlockUnlucky);
        bool armourRelevant = mitigated.Components.Any(c => !immune.Contains(c.Type)
            && Math.Round(c.Range.Min) > 0 && PlayerHitMitigation.ArmourPercent(player.Pools, c.Type, StatOf) > 0);
        var armour = IncomingArmourLocked(player, !blocked && armourRelevant, priorContacts, stats);
        int rawDamage = (int)Math.Round(taken[DamageType.Physical].Min);
        int damage = 0, chaosDamage = 0, physicalDamage = 0;
        var ailmentTaken = DamagePacket.Empty;
        int sourceTakenPct = ActorScale.Saturate((long)DamageTakenFromBlindedPct(player, id, stats)
            + DamageTakenFromMonster(m, stats) + NearbyAllyDamageTaken(player, here, now));
        bool hitRecently = player.RecentEvents.Happened(StatConditions.BeenHit, now);
        int pantheonElemTakenPct = hitRecently
            ? StatOf(Stat.ElementalDamageTakenPlusPctIfBeenHitRecently)
            : StatOf(Stat.ElementalDamageTakenPlusPctIfNotHitRecently);
        bool isAreaHit = a.AreaRadius > 0 || a.Projectile is { AreaRadius: > 0 } || a.Projectile is { Lobbed: true };
        bool halfAreaDamage = isAreaHit && StatOf(Stat.TakeHalfAreaDamageFromHitPctChance) is int halfAreaChance and > 0
            && GameServer.Core.World.RandomRolls.Chance(halfAreaChance, 100, player.IncomingHitRandom);
        double reservationFactor = ReservationHitTakenFactor(player, m, now);
        foreach (var (type, range) in mitigated.Components)
        {
            int value = immune.Contains(type) ? 0 : (int)Math.Round(range.Min);
            value = PlayerHitMitigation.AfterResistance(player.Pools, type, value,
                reduction.Physical, reduction.Elemental, armour.EffectiveArmour,
                PlayerHitMitigation.ArmourPercent(player.Pools, type, StatOf),
                imbalancedGuard: StatOf(Stat.KeystoneSacredBastion) != 0);
            // Flat damage taken after resistance and armour, then the multipliers: the increased damage taken adds to the
            // shock's, and a blocked hit deals only its share (PlayerDamageTaken). Monster hits are attacks, and melee
            // unless ranged.
            if (!immune.Contains(type)) value = Math.Max(0, value + PlayerDamageTaken.Flat(type, attack: attackHit, StatOf));
            if (type == DamageType.Physical)
                value = AntiquarianEffects.RangedPhysical(value, a.Ranged, StatOf);
            int extraTakenPct = sourceTakenPct + (DamageTypes.IsElemental(type) ? pantheonElemTakenPct : 0);
            value = (int)Math.Round(value * player.ElementalAilments.DamageTakenFactor(now,
                    extraTakenPct + PlayerDamageTaken.IncreasedPct(type, melee: !a.Ranged && attackHit, StatOf, spell: spellHit, projectile: projectile))
                * reservationFactor * ReservationTypedHitTakenFactor(player, type, now)
                * (halfAreaDamage ? 0.5 : 1.0)
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
        damage = FortificationDamageTakenLocked(player, damage, now, stats);
        chaosDamage = FortificationDamageTakenLocked(player, chaosDamage, now, stats);
        physicalDamage = Math.Min(FortificationDamageTakenLocked(player, physicalDamage, now, stats), Math.Max(0, damage - chaosDamage));
        if (attackHit && !a.Ranged && damage > 0)
        {
            int before = damage; damage = GolemSafeguardLocked(player, damage, now);
            chaosDamage = (int)((long)chaosDamage * damage / before);
            physicalDamage = (int)((long)physicalDamage * damage / before);
        }
        if (damage > 0)
        {
            int full = damage;
            damage = GameEvents.AdjustIncomingDamage(player, damage);
            chaosDamage = (int)Math.Round((double)chaosDamage * damage / full);
            physicalDamage = Math.Min((int)Math.Round((double)physicalDamage * damage / full), Math.Max(0, damage - chaosDamage));
        }
        // Untiring: avoidance, suppression and what a block keeps out count as prevented; damage taken as another type does not.
        PreventedDamageOf(player).Add(now, shifted[DamageType.Physical].Min - physicalDamage);
        if (!blocked) m.HitPlayerAt = now;
        uint manaBefore = player.PlayerMana;
        if (blocked) RecoverOnBlockLocked(player, stats, now, attackHit);
        // A blocked hit recoups the share it deals (ailmentTaken holds it).
        RecoupHitLocked(player, ailmentTaken.Select((_, r) =>
            new(FortificationDamageTakenLocked(player, (int)r.Min, now, stats), FortificationDamageTakenLocked(player, (int)r.Max, now, stats))), now, stats);
        long lifeBefore = player.PlayerLife;
        int lifeDamage = DamagePlayerHitTyped(player, physicalDamage, Math.Max(0, damage - physicalDamage - chaosDamage), chaosDamage, now, stats,
            ailmentTaken);
        player.PlayerLife = (uint)Math.Max(0, lifeBefore - lifeDamage);
        if (!blocked)
        {
            ApplyEqualisingHitLocked(player, m);
            if (player.PlayerLife > 0) QueueMonsterFlaskChargeLossLocked(player, m);
        }
        TrimLeechLocked(player);
        var incomingAilments = MergeMonsterModAilments(a.Ailments, m.Effects);
        ApplyPlayerAilmentsLocked(player, baseHit, ailmentTaken.Select((_, r) =>
            new(FortificationDamageTakenLocked(player, (int)r.Min, now, stats), FortificationDamageTakenLocked(player, (int)r.Max, now, stats))),
            monsterCritical, blocked, now, stats, incomingAilments, source: id);
        AlternateAilmentsOnPlayerLocked(player, m, ailmentTaken.Select((_, r) =>
            new(FortificationDamageTakenLocked(player, (int)r.Min, now, stats), FortificationDamageTakenLocked(player, (int)r.Max, now, stats))), blocked, now, stats, incomingAilments);
        ApplyMonsterDamagingAilmentsLocked(player, id, m, baseHit, blocked, now, attack: attackHit, prepared: stats,
            skillPoisonChance: a.PoisonChance, skillPoisonSeconds: a.PoisonSeconds, critical: monsterCritical);
        ApplyMonsterDecayLocked(player, id, m, blocked, now, stats);
        ApplyCorruptedBloodOnHit(player, a, baseHit, blocked, now, stats);
        // Monster on-hit charge gains (of Frenzy, of Power, species charge-on-hit, of Enervation).
        MonsterChargesOnHitDealtLocked(player, id, m, spellHit, blocked, now, player.IncomingHitRandom);
        // Monster life leech on hit (Remora's: life_leech_from_any_damage_permyriad, capped at 10% max life per hit).
        if (!blocked && damage > 0 && !m.Dead && m.Life > 0 && m.Life < m.MaxLife
            && m.Effects[Stat.LifeLeechFromAnyDamagePermyriad] is int leechPermyriad and > 0)
        {
            long leeched = Math.Min((long)damage * leechPermyriad / 10_000, Math.Max(1L, m.MaxLife / 10));
            if (leeched > 0) m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + leeched);
        }
        RecordIncomingHitLocked(player, now, spellHit, blocked, suppressed, lifeDamage, damage > 0, monsterCritical, projectile: projectile);
        RecentHitLifeLossOnHitLocked(player, now);
        UnhingedHitRecoveryLocked(player, now, stats);
        // Brine Bound: a hit that lands, blocked too (blocking keeps the defender's when-hit effects, wiki
        // Blocking), may freeze its monster for 1 s. Its own random keeps the incoming hit's draws in order.
        if (GameServer.Core.World.RandomRolls.Chance(StatOf(Stat.ChanceToFreezeEnemiesFor1SecondWhenHitPct), 100, AreaRng(player)))
            InflictedLocked(player, m, m.Ailments.Freeze(1, now, StatOf, name => m.Effects[name], AilmentArea(StatOf, _ => 0, spell: false)));
        AilmentsWhenHitLocked(player, m, StatOf, blocked, now, AreaRng(player));
        if (StatOf(Stat.BlindEnemiesWhenHitPctChance) is > 0 and var blindAttackerChance)
            InflictMonsterBlind(player, id, m, now, (blindAttackerChance, StatOf(Stat.BlindEffectPlusPct), BlindSeconds(StatOf)),
                AreaRng(player));
        ReservationWhenHitLocked(player, id, m, blocked, now);
        if (player.PlayerLife == 0) { ClearIncomingHitTriggers(player); player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
        RecordHardcoreDeath(player);
        bool spirit = m.Rarity == MonsterRarity.Unique
            && SpiritChieftainsWhenHitLocked(player, stats, blocked, monsterCritical, Math.Max(0, lifeBefore - player.PlayerLife));
        PlayerRecoveryIntent? recovery = null;
        bool alwaysStun = m.Effects[Stat.AlwaysStun] > 0;
        bool stunEligible = alwaysStun || (!a.Ranged && a.Damage.Components.All(c => c.Type == DamageType.Physical));
        if (!blocked && player.PlayerLife > 0 && stunEligible)
            recovery = CommitPlayerHitRecoveryLocked(player, alwaysStun ? Math.Max(damage, (int)player.PlayerMaxLife) : damage, now);
        else if (blocked && player.PlayerLife > 0 && stunEligible
            && StatOf(Stat.CannotBeStunnedIfHaveBeenStunnedOrBlockedStunningHitInPast2Seconds) != 0)
        {
            int stunCheckDamage = alwaysStun ? (int)player.PlayerMaxLife : Math.Max(rawDamage, damage);
            if (alwaysStun || AreaRng(player).NextDouble() * 100 < PlayerHitRecovery.Chance(stunCheckDamage, player.PlayerMaxLife, StatOf))
                player.LastStunnedOrBlockedStunAt = now;
        }
        else if (AntiquarianEffects.StunRecoverySource is var turtle && stats.GetValueOrDefault(turtle) > 0)
            Console.WriteLine($"[gateway] Turtle Shell hit ineligible: block={blocked}, ranged={a.Ranged}, "
                + $"alive={player.PlayerLife > 0}, types={string.Join(',', a.Damage.Components.Select(c => c.Type))}");
        if (recovery is not null && StatOf(Stat.DamageTakenPctRecoveredAsLifeFromStunningHits) is > 0 and var recoverPct
            && !LifeRecovery.NonLeechBlocked(StatOf))
        {
            double carry = 0;
            player.PlayerLife = PetrifiedBlood.RecoverLife(player, player.PlayerLife, (long)damage * recoverPct / 100, ref carry);
            TrimLeechLocked(player);
        }
        uint life = player.PlayerLife;
        RageWhenHitLocked(player);
        if (!blocked && player.PlayerLife > 0 && m.Effects[Stat.GlobalChanceToBlindOnHitPct] is > 0 and var blindChance
            && (blindChance >= 100 || player.IncomingHitRandom.Next(100) < blindChance))
            InflictPlayerBlind(player, 4 * Math.Max(0, 1 + m.Effects[Stat.BlindDurationPlusPct] / 100d),
                m.Effects[Stat.BlindEffectPlusPct], stats);
        // The hit packet carries this outcome's presentation and is processed at once (kind 3): 0x4000 particles,
        // 0x0200 main hand, 0x0001 blood, 0x0040 blocked, 0x0020 critical, code = the largest damage component.
        // The 3.29.3b decode (action-hit-packet-3.29.3b.md §§1–4) shows kind 2 only queues the package until an
        // action with that sequence consumes it, which 0xffff never guarantees, and that 0x0400 asks the client for
        // its own stun roll; the player's recovery is the authoritative 0x1d1 (SendPlayerHitRecovery).
        var packet = InstanceProtocol.ActionHit(id, sequence, player.Id, flags: MonsterHitFlags(blocked, monsterCritical), kind: 0x03,
            code: HitCode(mitigated), angle: HitAngle(here.X - from.X, here.Y - from.Y));
        var hitDebuffs = !blocked && life > 0 ? CollectMonsterHitDebuffs(a.Debuffs, m.Effects, player.IncomingHitRandom) : null;
        return new(packet, life, player.PlayerMana, manaBefore, damage, rawDamage, blocked, monsterCritical, recovery, armour, spirit)
        { Debuffs = hitDebuffs is { Count: > 0 } ? (id, hitDebuffs) : null };
    }

    static PlayerElementalAilments.HitSettings MergeMonsterModAilments(PlayerElementalAilments.HitSettings skill, MonsterModEffects e)
    {
        int ignite = e[Stat.AlwaysIgnite] > 0 ? 100 : 0;
        int freeze = Math.Max(0, e[Stat.BaseChanceToFreezePct]);
        int shock = e[Stat.AlwaysShock] > 0 ? 100 : 0;
        if (ignite == 0 && freeze == 0 && shock == 0) return skill;
        return skill with
        {
            IgniteChance = Math.Min(100, skill.IgniteChance + ignite),
            FreezeChance = Math.Min(100, skill.FreezeChance + freeze),
            ShockChance = Math.Min(100, skill.ShockChance + shock),
        };
    }

    static readonly IReadOnlyDictionary<uint, int> PlayerCrushedDebuffStats =
        new Dictionary<uint, int> { [Stat.BaseAdditionalPhysicalDamageReductionPct] = -CrushReductionPct };

    static IReadOnlyList<MonsterDebuffSpec>? CollectMonsterHitDebuffs(IReadOnlyList<MonsterDebuffSpec> baseDebuffs,
        MonsterModEffects e, Random random)
    {
        bool crush = e[Stat.ChanceToCrushOnHitPct] is int cc and > 0
            && GameServer.Core.World.RandomRolls.Chance(cc, 100, random);
        int fireExp = e[Stat.FireExposureOnHitMagnitude];
        int coldExp = e[Stat.ColdExposureOnHitMagnitude];
        int lightExp = e[Stat.LightningExposureOnHitMagnitude];
        if (!crush && fireExp >= 0 && coldExp >= 0 && lightExp >= 0)
            return baseDebuffs.Count > 0 ? baseDebuffs : null;
        var list = baseDebuffs.Count > 0 ? new List<MonsterDebuffSpec>(baseDebuffs) : new List<MonsterDebuffSpec>(2);
        if (crush) list.Add(new(CrushedBuffRow, PlayerCrushedDebuffStats, CrushSeconds));
        if (fireExp < 0) list.Add(new(ExposureBuffRows[0], new Dictionary<uint, int> { [Stat.BaseFireDamageResistancePct] = fireExp }, ExposureSeconds));
        if (coldExp < 0) list.Add(new(ExposureBuffRows[1], new Dictionary<uint, int> { [Stat.BaseColdDamageResistancePct] = coldExp }, ExposureSeconds));
        if (lightExp < 0) list.Add(new(ExposureBuffRows[2], new Dictionary<uint, int> { [Stat.BaseLightningDamageResistancePct] = lightExp }, ExposureSeconds));
        return list;
    }

    // The packets after a committed hit: energy shield, native recovery, the life bar and life, mana, death or flask charges.
    public static async Task PresentMonsterHit(Player player, uint id,
        MonsterHitOutcome hit)
    {
        if (!CurrentGameWork) return;
        await SendPlayerEnergyShield(player);
        if (!CurrentGameWork) return;
        if (hit.Spirit) await SendSpiritChieftain(player);
        if (!CurrentGameWork) return;
        if (hit.Recovery is not null) await SendPlayerHitRecovery(player, hit.Recovery);
        if (!CurrentGameWork) return;
        // At 0 the Life tick stops refreshing the overhead life bar (see Attack): hide it.
        if (hit.Life == 0)
            { SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]); Note("0x01b3 player hide_mini_life_bar = 1"); }
        if (ServerTrace.Combat || hit.Life == 0)
            Note($"0x01b1 player hit by monster 0x{id:x} for {hit.Damage}{(hit.Critical ? " (critical)" : "")}" +
                $"{(hit.RawDamage != hit.Damage ? $" ({hit.RawDamage} physical before {DamageModel.ArmourReduction(hit.Armour.EffectiveArmour, hit.RawDamage)}% armour, effective rating {hit.Armour.EffectiveArmour})" : "")}" +
                $"{(hit.Blocked ? " (blocked)" : "")}, life {hit.Life}/{player.PlayerMaxLife}{(hit.Life == 0 ? " (dies)" : "")}");
        if (hit.Mana != hit.ManaBefore)
            await SendPlayerMana(player,
                $"0x01b1 player mana {hit.Mana} ({(long)hit.Mana - hit.ManaBefore:+0;-0}{(hit.Blocked ? ", blocked" : "")})");
        if (!CurrentGameWork) return;
        if (hit.Life == 0)
        {
            await PlayerDied(player, $"0x0055 death screen ({player.Character.League ?? "?"} league)");
            await CampaignBossHelpers.OnPlayerDiedInBossArea(player);
        }
        else
        {
            if (!hit.Blocked) await SendAttackerSlows(player, id);
            await GainFlaskChargesWhenHit(player);
            if (hit.Debuffs is { } debuffs)
                foreach (var spec in debuffs.Specs) await AddPlayerDebuff(player, spec.Row, debuffs.Caster, spec.Stats, spec.Seconds);
        }
    }

    public const uint AttackerSlow = Stat.EnemiesThatHitYouWithAttackRecentlyAttackSpeedPlusPct;

    // A monster's attack time now. Devout of Gruthkul: an enemy whose attack hit the player in the last 4 s gets the
    // player's current reduced attack speed (Pools.AttackerSlowPct), additive with its own increases.
    public static double MonsterSwingSeconds(Player player, SpawnedMonster m, double now)
    {
        if (m.Attack is not { } a) return 0;
        var effects = m.Effects;
        int extra = MonsterOnLowLife(m) ? effects[a.Spell ? Stat.CastSpeedPlusPctWhenOnLowLife : Stat.AttackSpeedPlusPctWhenOnLowLife] : 0;
        if (!a.Spell && now - m.HitPlayerAt <= RecentlySeconds) extra += player.Pools.AttackerSlowPct;
        if (extra == 0) return a.Seconds;
        int increased = effects[a.Spell ? Stat.BaseCastSpeedPlusPct : Stat.AttackSpeedPlusPct];
        return a.Seconds * Math.Max(.1, 1 + increased / 100d) / Math.Max(.1, 1 + (increased + extra) / 100d);
    }

    // The attack stats on a monster's Stats component (it carries them from its creation, 11-skills.md §6) again when
    // its attack time changes or goes back, so the client's swing can follow the server's. After a hit, and as a stat
    // presentation (the player's slow changes with the stats; a slowed monster's "recently" ends on PresentIn).
    public static Task SendAttackerSlows(Player player) => SendAttackerSlows(player, null);

    public static Task SendAttackerSlows(Player player, uint? attackerId)
    {
        bool slows = player.Pools.AttackerSlowPct != 0;
        var tracked = player.Area.SlowedAttackers;
        double now = ProjectileWorldTime(player);
        List<uint>? retired = null;
        void Check(uint mid, SpawnedMonster m)
        {
            if (m.SwingSecondsSent is not null && m.HitPlayerAt + RecentlySeconds > now)
                PresentIn(player, m.HitPlayerAt + RecentlySeconds - now + 1e-6);
            if (m.Dead || m.Attack is not { } a)
            {
                if (m.SwingSecondsSent is not null) { m.SwingSecondsSent = null; (retired ??= []).Add(mid); }
                return;
            }
            if (m.SwingSecondsSent is null && (a.Spell || !slows || now - m.HitPlayerAt > RecentlySeconds)
                && (!MonsterOnLowLife(m) || m.Effects[a.Spell ? Stat.CastSpeedPlusPctWhenOnLowLife : Stat.AttackSpeedPlusPctWhenOnLowLife] == 0)) return;
            double seconds = MonsterSwingSeconds(player, m, now);
            if (seconds == (m.SwingSecondsSent ?? a.Seconds)) return;
            m.SwingSecondsSent = seconds == a.Seconds ? null : seconds;
            if (m.SwingSecondsSent is not null)
            {
                tracked.Add(mid);
                if (m.HitPlayerAt + RecentlySeconds > now)
                    PresentIn(player, m.HitPlayerAt + RecentlySeconds - now + 1e-6);
            }
            else (retired ??= []).Add(mid);
            var current = a with { Seconds = seconds };
            SetStats(player, mid, a.Spell ? current.InPlaceSkillStats() : current.Stats());
        }
        if (attackerId is uint aid)
        {
            if (tracked.Count > 0)
                foreach (uint mid in tracked)
                {
                    if (mid == aid) continue;
                    if (player.Area.Monsters.TryGetValue(mid, out var tm)) Check(mid, tm);
                    else (retired ??= []).Add(mid);
                }
            if (player.Area.Monsters.TryGetValue(aid, out var attacker)) Check(aid, attacker);
        }
        else
        {
            foreach (var (mid, m) in player.Area.Monsters) Check(mid, m);
        }
        if (retired is not null) foreach (uint mid in retired) tracked.Remove(mid);
        return Task.CompletedTask;
    }
}
