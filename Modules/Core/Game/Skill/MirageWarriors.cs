using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// General's Cry (<c>GeneralsCry</c> / <c>GeneralsCryAltX</c> / <c>GeneralsCryAltY</c>, support half
/// <c>GeneralsCrySupport</c>): warcry that counts power from nearby enemies and corpses (<c>warcry_gain_mp_from_corpses</c>,
/// <c>warcry_count_power_from_enemies</c>), uses nearby corpses up to <c>maximum_number_of_spiritual_cry_warriors</c>
/// (5 base + gem quality + <c>generals_cry_maximum_warriors_+</c>), and summons non-blocking Mirage Warriors
/// (<c>Metadata/Monsters/MirageWarrior/MirageWarrior&lt;Class&gt;</c>, BuffVisuals 1215 <c>mirage_warrior_visual</c>).
/// Each Mirage Warrior rushes a nearby living enemy, executes a linked melee attack skill using the player's attack
/// stats scaled by <c>support_spiritual_cry_damage_+%_final</c> (-60%..-51%), and despawns once its attack finishes
/// or its <c>base_skill_effect_duration</c> (4.0–4.9 s scaled by duration modifiers) expires.
/// </summary>
public static partial class Game
{
    public const string GeneralsCrySupportEffect = "GeneralsCrySupport";

    /// <summary>BuffDefinitions 1748 (<c>mirage_archer_mirage_display_buff</c>) with BuffVisuals 1215 (<c>mirage_warrior_visual</c>).</summary>
    const ushort MirageWarriorDisplayBuffRow = 1748;
    const ushort MirageWarriorVisualRow = 1215;

    /// <summary>MirageWarriorBase.ot sets <c>base_movement_velocity_+% = 50</c>; server walk estimate matches it.</summary>
    const double MirageWarriorBaseSpeedFactor = 1.5;
    const double MirageWarriorAggroRange = 80;

    enum MirageWarriorPhase { Seeking, Attacking, Done }

    sealed class MirageWarrior
    {
        public uint Id;
        public SpawnedMonster Monster = null!;
        public ushort LinkedSkillId;
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) LinkedEffect;
        public SupportedGem LinkedGem = null!;
        public double DamageFactor;
        public double SpawnedAt;
        public double ExpiresAt;
        public MirageWarriorPhase Phase = MirageWarriorPhase.Seeking;
        public uint TargetId;
        public double MoveOrderAt = double.NegativeInfinity;
        public double ContactAt;
        public double FinishAt;
        public bool Struck;
        public bool Channelled;
        public double ChannelInterval;
    }

    sealed class MirageWarriorState(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public readonly List<MirageWarrior> Warriors = [];
        public int Release;
    }

    static readonly RuleTable<Player, MirageWarriorState> MirageWarriors = new();

    internal static bool IsGeneralsCryEffect(string? effectId) =>
        effectId is "GeneralsCry" or "GeneralsCryAltX" or "GeneralsCryAltY";

    internal static bool IsGeneralsCry(Player player, int effect) =>
        IsGeneralsCryEffect(SkillsFor(player).EffectId(effect));

    internal static bool IsGeneralsCrySupportEffect(string? effectId) =>
        effectId == GeneralsCrySupportEffect;

    static byte[] MirageWarriorDisplay(uint actor) =>
        InstanceEntities.AddBuff(actor, 1, MirageWarriorDisplayBuffRow, visual: MirageWarriorVisualRow, list: [0, 0, 0]);

    internal static byte[]? MirageWarriorPeerDisplay(Player owner, uint actor, uint recipient) =>
        MirageWarriors.TryGetValue(owner, out var state)
            && state.Warriors.Any(w => w.Id == actor)
            ? MirageWarriorDisplay(recipient) : null;

    /// <summary>General's Cry Mirage Warrior damage factor from <c>GeneralsCrySupport</c>'s <c>support_spiritual_cry_damage_+%_final</c>.</summary>
    public static double MirageWarriorDamageFactor(Player player, int cryLevel, int cryQuality)
    {
        int supportRow = SkillsFor(player).EffectRow(GeneralsCrySupportEffect);
        if (supportRow < 0) return 1;
        var stats = SkillsFor(player).EffectStats(supportRow, cryLevel, cryQuality);
        int pct = stats.Where(s => s.Item1 == Stat.SupportSpiritualCryDamagePlusPctFinal).Sum(s => s.Item2);
        return Math.Max(0, 1 + pct / 100.0);
    }

    /// <summary>
    /// Eligible linked melee attack skills in the same item and link group as General's Cry, ordered by socket.
    /// Uses native <c>GeneralsCrySupport</c> compatibility rules (Melee &amp; Attack; excludes Cooldown, Triggered,
    /// Totem/Trap/Mine, Vaal, Spell, Instant, Reservation).
    /// </summary>
    public static List<(ushort Id, SupportedGem Gem, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Effect)>
        GeneralsCryLinkedMeleeSkills(Player player, SupportedGem cryGem)
    {
        var result = new List<(ushort, SupportedGem, (ushort, int, string, int, int, uint))>();
        if (cryGem.Host == 0 || cryGem.Group < 0) return result;
        int supportRow = SkillsFor(player).EffectRow(GeneralsCrySupportEffect);
        var support = Supports.Of(supportRow);
        if (support is null) return result;
        var effects = GemSkillEffects(player).ToDictionary(g => g.Id);
        foreach (var source in PlayerSkillSourcesLocked(player)
            .Where(s => s.Active && s.SourceGem is not null && s.Identity.Host == cryGem.Host)
            .OrderBy(s => s.Identity.Socket))
        {
            if (source.Effect == cryGem.Effect) continue;
            var linked = SupportedSkill(player, source.Id);
            if (linked.Group != cryGem.Group || !linked.RequirementsMet) continue;
            if (Supports.Of(linked.Effect) is not { } target) continue;
            var types = target.Types.Concat(linked.Supports
                .Where(s => s.Effect.Row != supportRow)
                .SelectMany(s => s.Effect.Added)).ToHashSet();
            if (!GemSupports.Compatible(support, types, [], null)) continue;
            if (WeaponRestricted(player, linked.Effect) is not null) continue;
            if (GemSkillInstanceDisabled(player, linked.Effect, source.Id, triggered: true) is not null) continue;
            if (!SkillHitPlan.ForSkill(SkillsFor(player), linked.Effect, linked.Level, linked.Quality).CanHit) continue;
            if (effects.TryGetValue(source.Id, out var gemEffect))
                result.Add((source.Id, linked, gemEffect));
        }
        return result;
    }

    static MonsterCatalog.Variety? MirageWarriorVariety(Player player)
    {
        string cls = player.Character.ClassRow switch
        {
            0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar",
        };
        return Monsters.Find($"Metadata/Monsters/MirageWarrior/MirageWarrior{cls}")
            ?? Monsters.Find("Metadata/Monsters/MirageWarrior/MirageWarriorDuelist");
    }

    /// <summary>
    /// Resolves General's Cry when its warcry finishes: replaces any prior Mirage Warriors, counts warcry power
    /// from nearby enemies and corpses, selects up to the allowed number of nearby corpses, and spawns
    /// non-blocking Mirage Warriors armed with the linked melee attack skill(s).
    /// </summary>
    public static async Task<int> GeneralsCryReleased(Player player, ushort crySkillId, (int X, int Y) at, int radius, int epoch)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || !player.ProjectileActive(epoch) || player.PlayerLife == 0)
            return 0;
        var cryGem = SupportedSkill(player, crySkillId);
        if (!IsGeneralsCry(player, cryGem.Effect)) return 0;
        var linked = GeneralsCryLinkedMeleeSkills(player, cryGem);
        var releaseState = MirageWarriorStateOfLocked(player);
        int release = ++releaseState.Release;
        await DespawnMirageWarriors(player, finishAttacks: true);
        if (linked.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} General's Cry: no eligible linked melee attack skill");
            return 0;
        }
        if (MirageWarriorVariety(player) is not { Supported: true } variety) return 0;

        var playerStats = CharacterPlayerStats(player);
        int Own(uint key) => GolemStat(cryGem.Stats, key);
        int PlayerStat(uint key) => playerStats.GetValueOrDefault(key);

        int maxWarriors = Math.Clamp(
            (Own(Stat.MaximumNumberOfSpiritualCryWarriors) is > 0 and var baseMax ? baseMax : 5)
            + Math.Max(0, PlayerStat(Stat.GeneralsCryMaximumWarriorsPlus)), 1, 20);
        int perFive = Own(Stat.SpiritualCryDoublesSummonedPer5MP) is > 0 and var p5 ? p5 : 2;
        double duration = Math.Max(0.5, SkillDurationModifiers.Seconds(
            Own(Stat.BaseSkillEffectDuration) is > 0 and var baseDur ? baseDur : 4000,
            PlayerStat, Own, DurationKinds(player, cryGem.Effect)));
        double damageFactor = MirageWarriorDamageFactor(player, cryGem.Level, cryGem.Quality);
        int extraMovePct = PlayerStat(Stat.SpiritualCryDoubleMovementVelocityPlusPct) + Own(Stat.SpiritualCryDoubleMovementVelocityPlusPct);
        double speedFactor = MirageWarriorBaseSpeedFactor * Math.Max(0.25, 1 + extraMovePct / 100.0);

        List<(int X, int Y)> spawnPoints;
        int power = 0;
        {
            if (!CurrentGameRules || player.Closed || player.LeavingArea || !player.ProjectileActive(epoch) || player.PlayerLife == 0)
                return 0;
            double now = ProjectileWorldTime(player);
            var nearbyCorpses = player.Area.Monsters
                .Where(e => IsCorpse(e.Value) && Dist(e.Value.Walk.Position(now), at) <= radius)
                .Select(e => (e.Key, Monster: e.Value, At: e.Value.Walk.Position(now)))
                .OrderBy(e => Dist(e.At, at))
                .ThenBy(e => e.Key)
                .ToList();
            if (nearbyCorpses.Count == 0)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} General's Cry: no usable corpses within {radius} cells");
                return 0;
            }
            if (PlayerStat(Stat.WarcriesHaveInfinitePower) != 0)
                power = InfiniteWarcryPower;
            else
            {
                int enemyPower = player.Area.Monsters.Values
                    .Where(m => m.Hostile && !m.Dead && m.Life > 0 && Dist(m.Walk.Position(now), at) <= radius)
                    .Sum(m => WarcryPower(m.Rarity));
                int corpsePower = nearbyCorpses.Sum(e => e.Monster.CreatedAsCorpse ? 1 : WarcryPower(e.Monster.Rarity));
                uint minPowerKey = Stat.Of("warcry_minimum_power");
                int minPower = Math.Max(PlayerStat(minPowerKey), Own(minPowerKey));
                power = Math.Max(enemyPower + corpsePower, minPower);
            }
            // At least 1 Mirage Warrior when a corpse is available; +perFive per 5 Power, capped by maxWarriors and corpses.
            int wanted = Math.Clamp(Math.Max(1, power / 5 * perFive), 1, maxWarriors);
            spawnPoints = nearbyCorpses.Take(wanted).Select(e => e.At).ToList();
        }

        var rules = CombatLifetime;
        var looks = Looks(player, ItemsOf(player));
        var extraStats = new (uint Key, int Value)[]
        {
            (Stat.HideMiniLifeBar, 1),
        };
        var movement = new MonsterMovement { SpeedFactor = speedFactor, ThroughBodies = true };
        byte level = (byte)Math.Clamp(player.Pools.Level, 1, 100);
        int spawned = 0;

        bool Current() => CurrentGameRules && ReferenceEquals(CombatLifetime, rules)
            && Volatile.Read(ref releaseState.Release) == release
            && !player.Closed && !player.LeavingArea && player.ProjectileActive(epoch) && player.PlayerLife > 0;
        double spawnTime = AreaTime(player) + 0.3;
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            while (Current() && AreaTime(player) < spawnTime) await Task.Delay(16);
            if (!Current()) break;
            spawnTime = AreaTime(player) + 0.2;
            var spawnAt = spawnPoints[i];
            if (!player.Layout.WalkableFor(spawnAt.X, spawnAt.Y, 0)) spawnAt = at;
            var (linkedId, linkedGem, linkedEffect) = linked[i % linked.Count];
            var actorSkill = BuildMirageWarriorSkill(player, linkedId, linkedGem);
            MirageWarrior? warrior = null;
            uint id = await SpawnMonster(player, variety, level, MonsterRarity.Normal,
                spawnAt.X, spawnAt.Y, friendly: true, log: false, scripted: true, nonBlocking: true,
                placement: MonsterPlacement.Established, movement: movement,
                extraStats: extraStats, ownedLooks: looks,
                ownedSkills: actorSkill is null ? [] : [actorSkill],
                ownedCurrent: Current,
                owned: (actorId, actor) =>
                {
                    double now = ProjectileWorldTime(player);
                    actor.NoRewards = true;
                    actor.EntranceGrace = true;
                    actor.Attack = null;
                    warrior = new MirageWarrior
                    {
                        Id = actorId,
                        Monster = actor,
                        LinkedSkillId = linkedId,
                        LinkedEffect = linkedEffect,
                        LinkedGem = linkedGem,
                        DamageFactor = damageFactor,
                        SpawnedAt = now,
                        ExpiresAt = now + duration,
                        Channelled = SkillsFor(player).IsChannelled(linkedGem.Effect),
                    };
                    var state = MirageWarriorStateOfLocked(player);
                    state.Warriors.Add(warrior);
                });
            if (id == 0 || warrior is null) continue;
            spawned++;
            await SendWorldRecord(player, id, MirageWarriorDisplay(id),
                $"0x01c1 mirage warrior 0x{id:x} visual", still: Current);
        }

        Console.WriteLine($"[gateway] {player.Connection.Remote} General's Cry: {power} power, "
            + $"summoned {spawned} Mirage Warrior(s) ({duration:0.0}s, {damageFactor:0.##}x damage) using {linked[0].Effect.Name}");
        return spawned;
    }

    static GrantedSkill? BuildMirageWarriorSkill(Player player, ushort linkedId, SupportedGem linkedGem)
    {
        var merged = SupportedStats(player, linkedId);
        return SkillsFor(player).FindByRow(linkedId, linkedGem.Effect, linkedGem.Level,
            [.. merged.Select(kv => (kv.Key, kv.Value))]);
    }

    static MirageWarriorState MirageWarriorStateOfLocked(Player player)
    {
        if (MirageWarriors.TryGetValue(player, out var state)
            && state.Epoch == player.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime))
            return state;
        MirageWarriors.Remove(player);
        MirageWarriors.Add(player, state = new(player.ProjectileEpoch, CombatLifetime));
        return state;
    }

    /// <summary>Death, area change or combat retirement: drops Mirage Warriors and queues removals.</summary>
    internal static void MirageWarriorsRetire(Player player)
    {
        if (!MirageWarriors.TryGetValue(player, out var state)) return;
        foreach (var w in state.Warriors)
        {
            player.Area.Monsters.Remove(w.Id);
            player.MinionOrigins.TryRemove(w.Id, out _);
            player.RetiredCombatObjects.Add(w.Id);
        }
        Interlocked.Increment(ref state.Release);
        state.Warriors.Clear();
        MirageWarriors.Remove(player);
    }

    static async Task DespawnMirageWarriors(Player player, bool finishAttacks)
    {
        List<uint> remove = [];
        if (!MirageWarriors.TryGetValue(player, out var state) || state.Warriors.Count == 0) return;
        for (int i = state.Warriors.Count - 1; i >= 0; i--)
        {
            var w = state.Warriors[i];
            if (finishAttacks && w.Phase == MirageWarriorPhase.Attacking) continue;
            state.Warriors.RemoveAt(i);
            player.Area.Monsters.Remove(w.Id);
            player.MinionOrigins.TryRemove(w.Id, out _);
            remove.Add(w.Id);
        }
        foreach (uint id in remove)
            await SendWorldRemoval(player, id, $"0x02bd mirage warrior 0x{id:x} replaced");
    }

    /// <summary>
    /// Steps active Mirage Warriors at game time <paramref name="now"/>: acquires the strongest nearby enemies, rushes into melee reach, executes the linked melee attack skill using player stats + General's Cry
    /// damage scaling, and despawns warriors after attacking or when expired.
    /// </summary>
    public static async Task MirageWarriorsTick(Player player, double now)
    {
        List<uint> toDespawn = [];
        List<(uint Id, byte[] Packet, string? Log)> actions = [];
        List<MirageWarrior> toStrike = [];
        bool retireAll = false;

        {
            if (!MirageWarriors.TryGetValue(player, out var state) || state.Warriors.Count == 0) return;
            if (!CurrentGameRules || !ReferenceEquals(state.Rules, CombatLifetime)
                || state.Epoch != player.ProjectileEpoch || player.Closed || player.LeavingArea || player.PlayerLife == 0)
            {
                retireAll = true;
            }
            else if (player.Paused) return;
            else
            {
                var weapon = WeaponFor(player);
                for (int i = state.Warriors.Count - 1; i >= 0; i--)
                {
                    var w = state.Warriors[i];
                    if (w.Phase == MirageWarriorPhase.Done || now >= w.ExpiresAt
                        || !player.Area.Monsters.TryGetValue(w.Id, out var held) || !ReferenceEquals(held, w.Monster))
                    {
                        player.Area.Monsters.Remove(w.Id);
                        player.MinionOrigins.TryRemove(w.Id, out _);
                        toDespawn.Add(w.Id);
                        state.Warriors.RemoveAt(i);
                        continue;
                    }

                    var here = w.Monster.Walk.Position(now);
                    if (w.Phase == MirageWarriorPhase.Seeking)
                    {
                        var target = PickMirageWarriorTargetLocked(player, here, w.TargetId, now);
                        if (target is not { } picked) continue;
                        w.TargetId = picked.Id;

                        double reach = MirageWarriorReach(player, w, weapon, picked.Monster);
                        double dist = Dist(here, picked.At);
                        if (dist <= reach)
                        {
                            w.Phase = MirageWarriorPhase.Attacking;
                            w.Monster.Walk.Cancel(now);
                            double attackSeconds = Math.Max(0.15, SupportedAttackSeconds(player, w.LinkedSkillId));
                            double contact = GemAttackContactDelay(player, w.LinkedGem.Effect, attackSeconds, out _, out _);
                            if (w.Channelled)
                            {
                                double interval = Math.Max(0.1, SupportedChannelSeconds(player, w.LinkedSkillId) ?? attackSeconds);
                                w.ChannelInterval = interval;
                                w.ContactAt = now + Math.Min(0.1, interval * 0.5);
                                w.FinishAt = Math.Min(w.ExpiresAt, now + 1.0);
                            }
                            else
                            {
                                w.ContactAt = now + contact;
                                w.FinishAt = Math.Min(w.ExpiresAt, now + Math.Max(attackSeconds, contact + 0.05));
                            }
                            actions.Add((w.Id, InstanceProtocol.ActionStarted(w.Id, here.X, here.Y,
                                picked.At.X, picked.At.Y, w.LinkedSkillId, 0xffff, player.MonsterOrderFlags,
                                targetObjectId: picked.Id),
                                $"0x01ab mirage warrior 0x{w.Id:x} uses {w.LinkedEffect.Name} on 0x{picked.Id:x}"));
                        }
                        else if (now - w.MoveOrderAt >= 0.25)
                        {
                            w.Monster.Walk.Order(picked.At.X, picked.At.Y, now);
                            w.MoveOrderAt = now;
                            actions.Add((w.Id, InstanceProtocol.ActionStarted(w.Id, here.X, here.Y,
                                picked.At.X, picked.At.Y, 0x2909, 0xffff, player.MonsterOrderFlags), null));
                        }
                    }
                    else if (w.Phase == MirageWarriorPhase.Attacking)
                    {
                        if (w.Channelled)
                        {
                            if (now < w.FinishAt && now >= w.ContactAt)
                            {
                                w.ContactAt = now + w.ChannelInterval;
                                toStrike.Add(w);
                            }
                            if (now >= w.FinishAt)
                            {
                                w.Phase = MirageWarriorPhase.Done;
                                actions.Add((w.Id, InstanceProtocol.ActionEnded(w.Id), null));
                            }
                        }
                        else
                        {
                            if (!w.Struck && now >= w.ContactAt)
                            {
                                w.Struck = true;
                                toStrike.Add(w);
                            }
                            if (w.Struck && now >= w.FinishAt)
                            {
                                w.Phase = MirageWarriorPhase.Done;
                                actions.Add((w.Id, InstanceProtocol.ActionEnded(w.Id), null));
                            }
                        }
                    }
                }
            }
        }

        if (retireAll)
        {
            await DespawnMirageWarriors(player, finishAttacks: false);
            return;
        }

        foreach (var (id, packet, log) in actions)
            await SendWorldRecord(player, id, packet, log);

        foreach (var w in toStrike)
            await ExecuteMirageWarriorHit(player, w);

        foreach (uint id in toDespawn)
            await SendWorldRemoval(player, id, $"0x02bd mirage warrior 0x{id:x} vanishes");
    }

    static double MirageWarriorReach(Player player, MirageWarrior w, SkillCatalog.Unarmed weapon, SpawnedMonster target)
    {
        double body = Math.Max(0, LivingCollision.Size(target));
        double baseReach = Math.Max(weapon.Range + 2, 8) + body;
        if (Supports.Of(w.LinkedGem.Effect)?.Types.Contains(7) == true)
        {
            int radius = SkillRadius(player, w.LinkedGem.Stats, spell: false, w.LinkedGem.Effect);
            return Math.Max(baseReach, Math.Min(radius, 24) + body);
        }
        return baseReach;
    }

    static (uint Id, SpawnedMonster Monster, (int X, int Y) At)? PickMirageWarriorTargetLocked(
        Player player, (int X, int Y) from, uint currentTarget, double now)
    {
        // Keep current alive target if still within aggro range.
        if (currentTarget != 0 && player.Area.Monsters.TryGetValue(currentTarget, out var kept)
            && kept.Hostile && !kept.Dead && kept.Life > 0 && kept.OwnerId is null && !kept.WorldHidden)
        {
            var keptAt = kept.Walk.Position(now);
            if (Dist(from, keptAt) <= MirageWarriorAggroRange)
                return (currentTarget, kept, keptAt);
        }

        (uint Id, SpawnedMonster Monster, (int X, int Y) At)? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || m.OwnerId is not null || m.WorldHidden
                || double.IsPositiveInfinity(m.BusyUntil)) continue;
            var at = m.Walk.Position(now);
            double d = Dist(from, at);
            if (d > MirageWarriorAggroRange) continue;
            if (best is { } found && (m.Rarity < found.Monster.Rarity
                || m.Rarity == found.Monster.Rarity && d >= bestDistance)) continue;
            bestDistance = d;
            best = (id, m, at);
        }
        return best;
    }

    /// <summary>
    /// Executes one melee attack hit for a Mirage Warrior using the player's attack stats + General's Cry damage
    /// factor, handling melee strikes, area/slam/channelling melee skills, and melee-with-projectile attacks
    /// (e.g. Lightning Strike, Molten Strike, Frost Blades, Volcanic Fissure).
    /// </summary>
    static async Task ExecuteMirageWarriorHit(Player player, MirageWarrior w)
    {
        ushort skillId = w.LinkedSkillId;
        var gem = w.LinkedEffect;
        var hits = new List<(uint Id, SpawnedMonster Monster, MonsterHit Hit, int Damage, byte HitCode, (int X, int Y) TargetAt, bool Critical, StunHit Stun)>();
        var dotPackets = new List<byte[]>();
        (int X, int Y) warriorPos = default;

        var session = ProjectilesFor(player);
        {
            if (!CurrentGameRules || player.Closed || player.LeavingArea || player.PlayerLife == 0
                || !player.Area.Monsters.TryGetValue(w.Id, out var actor) || !ReferenceEquals(actor, w.Monster)) return;
            double now = ProjectileWorldTime(player);
            warriorPos = w.Monster.Walk.Position(now);

            var targetMonster = w.TargetId != 0 && player.Area.Monsters.TryGetValue(w.TargetId, out var tm)
                && tm.Hostile && !tm.Dead && tm.Life > 0 ? tm : null;
            var aim = targetMonster?.Walk.Position(now) ?? warriorPos;

            using var scope = new PlayerStatScope(player, fresh: true);
            var playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            // Ancestral Bond / "deal no damage yourself" apply to the player, not to Mirage Warriors.
            playerStats.Remove(Stat.KeystoneAncestralBond);
            playerStats.Remove(Stat.DealNoDamageYourself);

            var admittedGem = SupportedSkill(player, skillId);
            if (admittedGem.Effect != gem.Effect) return;
            var skillStats = admittedGem.Stats;
            var weapon = WeaponFor(player);
            var local = weapon.LocalSources;
            var criticalRoll = CaptureSkillCriticalRoll(player, skillId);
            double factor = w.DamageFactor;

            // Projectile component for hybrid melee skills (Lightning Strike, Molten Strike, Frost Blades, Volcanic Fissure, etc.).
            bool hasProjectiles = UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null;
            uint meleeExcludedId = 0;

            // Resolve melee strike / area hit.
            if (SupportedHitInputsFor(player, skillId, weapon: weapon, capturedGem: admittedGem,
                area: IsSmite(gem.Effect) ? true : null) is { } capturedInputs)
            {
                var hitPacket = SupportedHitFrom(capturedInputs, suppress: false).Scale(factor);
                var ignite = ScaleSnipeIgnite(SupportedIgnite(player, skillId, false, playerStats, null, 0, capturedInputs), factor);
                var bleed = ScaleSnipeIgnite(SupportedBleed(player, skillId, false, playerStats, null, melee: true, 0, capturedInputs), factor);
                var poison = ScaleSnipePoison(SupportedPoison(player, skillId, false, playerStats, null, 0, capturedInputs), factor);
                var accuracy = SupportedAccuracy(player, skillId, AttackHand.Main);
                var pen = Penetration.Of(playerStats, Stats, attack: true, skillStats, local);
                var ailmentStats = WithWeaponAilmentStats(skillStats, local);
                var resMods = HitResistMods(playerStats, skillStats);
                int critMult = SupportedCriticalMultiplier(player, skillId);
                var impale = WeaponImpaleSettings(playerStats, Stats, attack: true, skillStats, weapon);
                var critImpale = WeaponImpaleSettings(playerStats, Stats, attack: true, skillStats, weapon, critical: true);
                bool isArea = Supports.Of(gem.Effect)?.Types.Contains(7) == true
                    || SkillsFor(player).IsChannelled(gem.Effect);
                int radius = isArea
                    ? SkillRadius(player, skillStats, spell: false, gem.Effect)
                    : Math.Max(weapon.Range + 2, 8);
                var centre = isArea
                    ? MeleeAreaCentre.Of(SkillsFor(player).ActionHash(gem.Effect), warriorPos, aim, radius + weapon.Range)
                    : aim;
                int splashRadius = !isArea && GolemStat(skillStats, Stat.MeleeSplashAreaOfEffectPlusPct) != 0
                    || !isArea && GolemStat(skillStats, Stat.MeleeSplash) != 0
                    ? SkillRadius(player, 14, skillStats, spell: false, gem.Effect) : 0;

                List<(uint Id, SpawnedMonster Monster, (int X, int Y) At)> victims;
                if (isArea)
                {
                    victims = [.. player.Area.Monsters
                        .Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null && !e.Value.WorldHidden)
                        .Select(e => (e.Key, e.Value, At: e.Value.Walk.Position(now)))
                        .Where(e => Dist(e.At, centre) <= radius + Math.Max(0, LivingCollision.Size(e.Value)))];
                }
                else
                {
                    victims = [];
                    if (targetMonster is not null && Dist(warriorPos, aim) <= radius + Math.Max(0, LivingCollision.Size(targetMonster)) + 2)
                        victims.Add((w.TargetId, targetMonster, aim));
                    else
                    {
                        var fallback = player.Area.Monsters
                            .Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null && !e.Value.WorldHidden)
                            .Select(e => (e.Key, e.Value, At: e.Value.Walk.Position(now)))
                            .Where(e => Dist(warriorPos, e.At) <= radius + Math.Max(0, LivingCollision.Size(e.Value)) + 2)
                            .OrderBy(e => Dist(warriorPos, e.At)).FirstOrDefault();
                        if (fallback.Value is not null) victims.Add(fallback);
                    }
                    if (splashRadius > 0 && victims.Count > 0)
                    {
                        var primaryAt = victims[0].At;
                        uint primaryId = victims[0].Id;
                        foreach (var (id, m) in player.Area.Monsters)
                        {
                            if (id == primaryId || !m.Hostile || m.Dead || m.Life == 0 || m.OwnerId is not null || m.WorldHidden) continue;
                            var mAt = m.Walk.Position(now);
                            if (Dist(mAt, primaryAt) <= splashRadius) victims.Add((id, m, mAt));
                        }
                    }
                }

                var hitRolls = HitRollsFor(playerStats);
                int SkillStat(uint k) => skillStats.GetValueOrDefault(k);
                var dualStrikeBonuses = DualStrikeBonuses.From(skillStats);
                foreach (var (id, m, targetAt) in victims)
                {
                    if (m.Dead || m.Life == 0) continue;
                    int hitChance = AttackHitChanceAt(player, id, m, now, accuracy, playerStats);
                    if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, now, player.AttackAccuracyRandom)) continue;
                    if (MonsterBlocks(m, AreaRng(player), spell: false, now, skillStats)) continue;
                    var fullLifeStrike = dualStrikeBonuses.Against(m);
                    double damageMore = fullLifeStrike.DamageMore;
                    var contactHit = damageMore == 1 ? hitPacket : hitPacket.Scale(damageMore);
                    var rolled = WithConditionalSupportDamageLocked(player, skillStats, attack: true, m,
                        GameEvents.WithOutgoingDamage(player, playerStats, m,
                            (hitRolls with { DamageMorePct = 0 }).ForSkill(SkillStat, spell: false)
                                .ForWeapon(local).RollOutgoingHit(contactHit, AreaRng(player))), now);
                    int critChance = CriticalChanceOn(player, id, SupportedCriticalChance(player, skillId),
                        SupportedCriticalChanceVsBlinded(player, skillId, release: false, null),
                        SupportedCriticalChanceVsGround(player, skillId, release: false, null));
                    bool critical = RollCriticalStrike(player, fullLifeStrike.CriticalChance(critChance), skillStats,
                        attack: true, skillRoll: criticalRoll, playerStats: playerStats)
                        && EvasionEntropy.ConfirmsCritical(hitChance, player.AttackAccuracyRandom);
                    int sourceMultiplier = CriticalMultiplierWithWeapon(critMult, m, local);
                    int multiplier = CriticalMultiplierOn(m, sourceMultiplier, now, playerStats, skillStats);
                    var (damage, _, mitigated, takenRules) = ResolveMonsterHit(m, rolled, critical, attack: true, now, resMods, pen,
                        hitRolls.InvertsResistance(AreaRng(player)), AreaRng(player));
                    if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                    int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: false, now, damage, takenRules);
                    damage = WithImpaleHitLocked(player, m, damage, rolled, critical,
                        sourceMultiplier + FullLifeCriticalMultiplier(m, playerStats, skillStats), critical ? critImpale : impale, 1).Damage;
                    var stun = OwnerStun(player, gem.Effect, m, damage, mitigated, melee: true, spell: false, critical, playerStats, skillStats);
                    if (ApplyMonsterHitLocked(player, m, damage, rampageSource: null, playerStats: null, allyAt: warriorPos,
                        cull: Cull.Of(playerStats, skillStats, critical, spell: false).WithWeapon(local), cullPlayer: playerStats,
                        chaosDamage: chaosDamage) is not { } applied) continue;
                    if (applied.StageStarts)
                    {
                        CommitBossStage(player, id, m, now);
                        applied = applied with { StageStarts = false };
                    }
                    if (!m.Invulnerable)
                    {
                        AilmentsOnHitLocked(player, id, m, mitigated, spell: false, critical, multiplier, playerStats, ailmentStats, now,
                            AreaRng(player), sourceEffect: gem.Effect, poison: damageMore == 1 ? poison : RepeatAilment(poison, damageMore),
                            sourceSkill: skillId, ownHit: false);
                        if (applied.Life > 0 && ignite.CanIgnite(rolled)
                            && IgniteOnHitLocked(player, id, m, damageMore == 1 ? ignite : RepeatAilment(ignite, damageMore),
                                critical, now, playerIgnite: false) is { } ign)
                            dotPackets.AddRange(ign);
                        if (applied.Life > 0 && rolled[DamageType.Physical].Max > 0
                            && BleedOnHitLocked(player, id, m, damageMore == 1 ? bleed : RepeatAilment(bleed, damageMore),
                                critical, now) is { } bld)
                            dotPackets.AddRange(bld);
                    }
                    if (meleeExcludedId == 0 && IsLightningStrike(gem.Effect)) meleeExcludedId = id;
                    hits.Add((id, m, applied, damage, HitCode(mitigated), targetAt, critical, stun));
                }
            }

            // Release linked projectiles (e.g. Lightning Strike bolts, Molten Strike balls, Frost Blades, Volcanic Fissure).
            if (hasProjectiles && session.Active)
            {
                var use = CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, from: warriorPos);
                var shot = (ProjectileOffense)use.Payload!;
                ProjectileActorKey? excluded = meleeExcludedId != 0 && player.Area.Monsters.TryGetValue(meleeExcludedId, out var exM)
                    ? session.ActorKey(meleeExcludedId, exM) : null;
                var mirageShot = shot with
                {
                    MirageSource = null,
                    Mirage = true,
                    CriticalTrigger = null,
                    ClientPredicted = false,
                    ActualCaster = playerStats,
                    ExcludedTarget = excluded,
                    Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
                    Hit = shot.Hit.Scale(factor),
                    AreaHit = shot.AreaHit?.Scale(factor),
                    HitProfileMore = shot.HitProfileMore * factor,
                    AilmentProfileMore = shot.AilmentProfileMore * factor,
                    Ignite = ScaleSnipeIgnite(shot.Ignite, factor),
                    Bleed = ScaleSnipeIgnite(shot.Bleed, factor),
                    Poison = ScaleSnipePoison(shot.Poison, factor),
                };
                session.ReleaseNow(use with { Payload = mirageShot });
            }
        }

        foreach (var pkt in dotPackets)
            await Send(player, pkt, null);

        var deaths = new List<(uint Id, SpawnedMonster Monster)>();
        foreach (var (id, m, hit, damage, hitCode, targetAt, critical, _) in hits)
        {
            await SendWorldRecord(player, w.Id,
                InstanceProtocol.ActionHit(w.Id, 0xffff, id,
                    flags: MonsterHitFlags(blocked: false, critical: critical),
                    kind: 0x03, code: hitCode, angle: HitAngle(targetAt.X - warriorPos.X, targetAt.Y - warriorPos.Y)),
                null);
            await NotifyMonsterHit(player, id, m, hit, damage,
                $"Mirage Warrior ({gem.Name})", deaths: deaths);
        }
        foreach (var (id, m) in deaths)
            await MonsterKilled(player, id, m);
    }
}
