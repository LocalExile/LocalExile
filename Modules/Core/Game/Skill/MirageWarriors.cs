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
/// <c>warcry_count_power_from_enemies</c>), consumes nearby corpses up to <c>maximum_number_of_spiritual_cry_warriors</c>
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
        public int ChannelHitsLeft;
        public double ChannelInterval;
    }

    sealed class MirageWarriorState(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public readonly List<MirageWarrior> Warriors = [];
    }

    static readonly RuleTable<ClientState, MirageWarriorState> MirageWarriors = new();

    internal static bool IsGeneralsCryEffect(string? effectId) =>
        effectId is "GeneralsCry" or "GeneralsCryAltX" or "GeneralsCryAltY";

    internal static bool IsGeneralsCry(ClientState conn, int effect) =>
        IsGeneralsCryEffect(SkillsFor(conn).EffectId(effect));

    internal static bool IsGeneralsCrySupportEffect(string? effectId) =>
        effectId == GeneralsCrySupportEffect;

    static byte[] MirageWarriorDisplay(uint actor) =>
        InstanceEntities.AddBuff(actor, 1, MirageWarriorDisplayBuffRow, visual: MirageWarriorVisualRow, list: [0, 0, 0]);

    internal static byte[]? MirageWarriorPeerDisplay(ClientState owner, uint actor, uint recipient)
    {
        lock (owner.Monsters)
            return MirageWarriors.TryGetValue(owner, out var state)
                && state.Warriors.Any(w => w.Id == actor)
                ? MirageWarriorDisplay(recipient) : null;
    }

    /// <summary>General's Cry Mirage Warrior damage factor from <c>GeneralsCrySupport</c>'s <c>support_spiritual_cry_damage_+%_final</c>.</summary>
    public static double MirageWarriorDamageFactor(ClientState conn, int cryLevel, int cryQuality)
    {
        int supportRow = SkillsFor(conn).EffectRow(GeneralsCrySupportEffect);
        if (supportRow < 0) return 1;
        var stats = SkillsFor(conn).EffectStats(supportRow, cryLevel, cryQuality);
        if (Stats.Key("support_spiritual_cry_damage_+%_final") is not uint key) return 1;
        int pct = stats.Where(s => s.Item1 == key).Sum(s => s.Item2);
        return Math.Max(0, 1 + pct / 100.0);
    }

    /// <summary>
    /// Eligible linked melee attack skills in the same item and link group as General's Cry, ordered by socket.
    /// Uses native <c>GeneralsCrySupport</c> compatibility rules (Melee &amp; Attack; excludes Cooldown, Triggered,
    /// Totem/Trap/Mine, Vaal, Spell, Instant, Reservation).
    /// </summary>
    public static List<(ushort Id, SupportedGem Gem, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Effect)>
        GeneralsCryLinkedMeleeSkills(ClientState conn, SupportedGem cryGem)
    {
        var result = new List<(ushort, SupportedGem, (ushort, int, string, int, int, uint))>();
        if (cryGem.Host == 0 || cryGem.Group < 0) return result;
        int supportRow = SkillsFor(conn).EffectRow(GeneralsCrySupportEffect);
        var support = Supports.Of(supportRow);
        if (support is null) return result;
        var effects = GemSkillEffects(conn).ToDictionary(g => g.Id);
        foreach (var source in PlayerSkillSourcesLocked(conn)
            .Where(s => s.Active && s.SourceGem is not null && s.Identity.Host == cryGem.Host)
            .OrderBy(s => s.Identity.Socket))
        {
            if (source.Effect == cryGem.Effect) continue;
            var linked = SupportedSkill(conn, source.Id);
            if (linked.Group != cryGem.Group || !linked.RequirementsMet) continue;
            if (Supports.Of(linked.Effect) is not { } target) continue;
            var types = target.Types.Concat(linked.Supports
                .Where(s => s.Effect.Row != supportRow)
                .SelectMany(s => s.Effect.Added)).ToHashSet();
            if (!GemSupports.Compatible(support, types, [], null)) continue;
            if (WeaponRestricted(conn, linked.Effect) is not null) continue;
            if (GemSkillInstanceDisabled(conn, linked.Effect, source.Id, triggered: true) is not null) continue;
            if (!SkillHitPlan.ForSkill(SkillsFor(conn), linked.Effect, linked.Level, linked.Quality).CanHit) continue;
            if (effects.TryGetValue(source.Id, out var gemEffect))
                result.Add((source.Id, linked, gemEffect));
        }
        return result;
    }

    static MonsterCatalog.Variety? MirageWarriorVariety(ClientState conn)
    {
        string cls = conn.ClassRow switch
        {
            0 => "Marauder", 1 => "Witch", 2 => "Scion", 3 => "Ranger", 4 => "Duelist", 5 => "Shadow", _ => "Templar",
        };
        return Monsters.Find($"Metadata/Monsters/MirageWarrior/MirageWarrior{cls}")
            ?? Monsters.Find("Metadata/Monsters/MirageWarrior/MirageWarriorDuelist");
    }

    /// <summary>
    /// Resolves General's Cry when its warcry finishes: replaces any prior Mirage Warriors, counts warcry power
    /// from nearby enemies and corpses, consumes up to the allowed number of nearby corpses, and spawns
    /// non-blocking Mirage Warriors armed with the linked melee attack skill(s).
    /// </summary>
    public static async Task<int> GeneralsCryReleased(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort crySkillId, (int X, int Y) at, int radius, int epoch)
    {
        // Re-casting General's Cry replaces existing Mirage Warriors immediately.
        await DespawnAllMirageWarriors(remote, stream, ciphers, conn);

        SupportedGem cryGem;
        List<(ushort Id, SupportedGem Gem, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Effect)> linked;
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch) || conn.PlayerLife == 0)
                return 0;
            cryGem = SupportedSkill(conn, crySkillId);
            if (!IsGeneralsCry(conn, cryGem.Effect)) return 0;
            linked = GeneralsCryLinkedMeleeSkills(conn, cryGem);
        }
        if (linked.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} General's Cry: no eligible linked melee attack skill");
            return 0;
        }
        if (MirageWarriorVariety(conn) is not { Supported: true } variety) return 0;

        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(cryGem.Stats, name);
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;

        int maxWarriors = Math.Clamp(
            (Own("maximum_number_of_spiritual_cry_warriors") is > 0 and var baseMax ? baseMax : 5)
            + Math.Max(0, Player("generals_cry_maximum_warriors_+")), 1, 20);
        int perFive = Own("spiritual_cry_doubles_summoned_per_5_MP") is > 0 and var p5 ? p5 : 2;
        double duration = Math.Max(0.5, SkillDurationModifiers.Seconds(
            Own("base_skill_effect_duration") is > 0 and var baseDur ? baseDur : 4000,
            Player, Own, DurationKinds(conn, cryGem.Effect)));
        double damageFactor = MirageWarriorDamageFactor(conn, cryGem.Level, cryGem.Quality);
        int extraMovePct = Player("spiritual_cry_double_movement_velocity_+%") + Own("spiritual_cry_double_movement_velocity_+%");
        double speedFactor = MirageWarriorBaseSpeedFactor * Math.Max(0.25, 1 + extraMovePct / 100.0);

        List<(uint Id, string Label, (int X, int Y) At)> consumed = [];
        int power = 0;
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch) || conn.PlayerLife == 0)
                return 0;
            double now = ProjectileWorldTime(conn);
            var nearbyCorpses = conn.Monsters
                .Where(e => IsCorpse(e.Value) && Dist(e.Value.Walk.Position(now), at) <= radius)
                .Select(e => (e.Key, Monster: e.Value, At: e.Value.Walk.Position(now)))
                .OrderBy(e => Dist(e.At, at))
                .ThenBy(e => e.Key)
                .ToList();
            if (nearbyCorpses.Count == 0)
            {
                Console.WriteLine($"[gateway] {remote} General's Cry: no usable corpses within {radius} cells");
                return 0;
            }
            if (Player("warcries_have_infinite_power") != 0)
                power = InfiniteWarcryPower;
            else
            {
                int enemyPower = conn.Monsters.Values
                    .Where(m => m.Hostile && !m.Dead && m.Life > 0 && Dist(m.Walk.Position(now), at) <= radius)
                    .Sum(m => WarcryPower(m.Rarity));
                int corpsePower = nearbyCorpses.Count; // warcry_gain_mp_from_corpses: 1 power per corpse
                int minPower = Math.Max(Player("warcry_minimum_power"), Own("warcry_minimum_power"));
                power = Math.Max(enemyPower + corpsePower, minPower);
            }
            // At least 1 Mirage Warrior when a corpse is available; +perFive per 5 Power, capped by maxWarriors and corpses.
            int wanted = Math.Clamp(Math.Max(1, power / 5 * perFive), 1, maxWarriors);
            int toSummon = Math.Min(nearbyCorpses.Count, wanted);
            for (int i = 0; i < toSummon; i++)
            {
                var (corpseId, corpse, corpseAt) = nearbyCorpses[i];
                corpse.CorpseConsumed = true;
                NoteCorpseConsumedLocked(conn, corpse);
                conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
                conn.Monsters.Remove(corpseId);
                consumed.Add((corpseId, corpse.Label, corpseAt));
            }
        }

        foreach (var (corpseId, label, _) in consumed)
            await SendWorldRemoval(remote, stream, ciphers, conn, corpseId,
                $"0x02bd corpse 0x{corpseId:x} ({label}) consumed by General's Cry");

        var rules = CombatLifetime;
        var looks = Looks(conn, ItemsOf(conn));
        var extraStats = new (uint Key, int Value)[]
        {
            (StatKeys.HideMiniLifeBar, 1),
        };
        var movement = new MonsterMovement { SpeedFactor = speedFactor, ThroughBodies = true };
        byte level = (byte)Math.Clamp(conn.Pools.Level, 1, 100);
        int spawned = 0;

        for (int i = 0; i < consumed.Count; i++)
        {
            var (_, _, spawnAt) = consumed[i];
            if (!conn.Layout.WalkableFor(spawnAt.X, spawnAt.Y, 0)) spawnAt = at;
            var (linkedId, linkedGem, linkedEffect) = linked[i % linked.Count];
            var actorSkill = BuildMirageWarriorSkill(conn, linkedId, linkedGem);
            MirageWarrior? warrior = null;
            bool Current() => CurrentGameRules && ReferenceEquals(CombatLifetime, rules)
                && !conn.Closed && !conn.LeavingArea && conn.ProjectileActive(epoch) && conn.PlayerLife > 0;

            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal,
                spawnAt.X, spawnAt.Y, friendly: true, log: false, scripted: true, nonBlocking: true,
                placement: MonsterPlacement.Established, movement: movement,
                extraStats: extraStats, ownedLooks: looks,
                ownedSkills: actorSkill is null ? [] : [actorSkill],
                ownedCurrent: Current,
                owned: (actorId, actor) =>
                {
                    double now = ProjectileWorldTime(conn);
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
                        Channelled = SkillsFor(conn).IsChannelled(linkedGem.Effect),
                    };
                    var state = MirageWarriorStateOfLocked(conn);
                    state.Warriors.Add(warrior);
                });
            if (id == 0 || warrior is null) continue;
            spawned++;
            await SendWorldRecord(remote, stream, ciphers, conn, id, MirageWarriorDisplay(id),
                $"0x01c1 mirage warrior 0x{id:x} visual", still: Current);
        }

        Console.WriteLine($"[gateway] {remote} General's Cry: {power} power, consumed {consumed.Count} corpse(s), "
            + $"summoned {spawned} Mirage Warrior(s) ({duration:0.0}s, {damageFactor:0.##}x damage) using {linked[0].Effect.Name}");
        return spawned;
    }

    static GrantedSkill? BuildMirageWarriorSkill(ClientState conn, ushort linkedId, SupportedGem linkedGem)
    {
        var merged = SupportedStats(conn, linkedId);
        return SkillsFor(conn).FindByRow(linkedId, linkedGem.Effect, linkedGem.Level,
            [.. merged.Select(kv => (kv.Key, kv.Value))]);
    }

    static MirageWarriorState MirageWarriorStateOfLocked(ClientState conn)
    {
        if (MirageWarriors.TryGetValue(conn, out var state)
            && state.Epoch == conn.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime))
            return state;
        MirageWarriors.Remove(conn);
        MirageWarriors.Add(conn, state = new(conn.ProjectileEpoch, CombatLifetime));
        return state;
    }

    /// <summary>Death, area change or combat retirement: drops Mirage Warriors and queues removals.</summary>
    internal static void MirageWarriorsRetire(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!MirageWarriors.TryGetValue(conn, out var state)) return;
            foreach (var w in state.Warriors)
            {
                conn.Monsters.Remove(w.Id);
                conn.MinionOrigins.TryRemove(w.Id, out _);
                conn.RetiredCombatObjects.Add(w.Id);
            }
            state.Warriors.Clear();
            MirageWarriors.Remove(conn);
        }
    }

    static async Task DespawnAllMirageWarriors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<uint> remove = [];
        lock (conn.Monsters)
        {
            if (!MirageWarriors.TryGetValue(conn, out var state) || state.Warriors.Count == 0) return;
            foreach (var w in state.Warriors)
            {
                conn.Monsters.Remove(w.Id);
                conn.MinionOrigins.TryRemove(w.Id, out _);
                remove.Add(w.Id);
            }
            state.Warriors.Clear();
        }
        foreach (uint id in remove)
            await SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd mirage warrior 0x{id:x} replaced");
    }

    /// <summary>
    /// Steps active Mirage Warriors at game time <paramref name="now"/>: acquires targets (spreading across nearby
    /// enemies), rushes into melee reach, executes the linked melee attack skill using player stats + General's Cry
    /// damage scaling, and despawns warriors after attacking or when expired.
    /// </summary>
    public static async Task MirageWarriorsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now)
    {
        List<uint> toDespawn = [];
        List<(uint Id, byte[] Packet, string? Log)> actions = [];
        List<MirageWarrior> toStrike = [];
        bool retireAll = false;

        using (ProjectileMutation(conn))
        {
            if (!MirageWarriors.TryGetValue(conn, out var state) || state.Warriors.Count == 0) return;
            if (!CurrentGameRules || !ReferenceEquals(state.Rules, CombatLifetime)
                || state.Epoch != conn.ProjectileEpoch || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0)
            {
                retireAll = true;
            }
            else if (conn.Paused) return;
            else
            {
                // Advance warrior walk estimates before checking reach.
                var claimedTargets = new HashSet<uint>();
                foreach (var w in state.Warriors)
                    if (w.Phase != MirageWarriorPhase.Done && w.TargetId != 0
                        && conn.Monsters.TryGetValue(w.TargetId, out var existing)
                        && existing.Hostile && !existing.Dead && existing.Life > 0)
                        claimedTargets.Add(w.TargetId);

                var weapon = WeaponFor(conn);
                for (int i = state.Warriors.Count - 1; i >= 0; i--)
                {
                    var w = state.Warriors[i];
                    if (w.Phase == MirageWarriorPhase.Done || now >= w.ExpiresAt
                        || !conn.Monsters.TryGetValue(w.Id, out var held) || !ReferenceEquals(held, w.Monster))
                    {
                        conn.Monsters.Remove(w.Id);
                        conn.MinionOrigins.TryRemove(w.Id, out _);
                        toDespawn.Add(w.Id);
                        state.Warriors.RemoveAt(i);
                        continue;
                    }

                    var here = w.Monster.Walk.Position(now);
                    if (w.Phase == MirageWarriorPhase.Seeking)
                    {
                        var target = PickMirageWarriorTargetLocked(conn, here, w.TargetId, claimedTargets, now);
                        if (target is not { } picked) continue;
                        w.TargetId = picked.Id;
                        claimedTargets.Add(picked.Id);

                        double reach = MirageWarriorReach(conn, w, weapon, picked.Monster);
                        double dist = Dist(here, picked.At);
                        if (dist <= reach)
                        {
                            w.Phase = MirageWarriorPhase.Attacking;
                            w.Monster.Walk.Cancel(now);
                            double attackSeconds = Math.Max(0.15, SupportedAttackSeconds(conn, w.LinkedSkillId));
                            double contact = GemAttackContactDelay(conn, w.LinkedGem.Effect, attackSeconds, out _, out _);
                            if (w.Channelled)
                            {
                                double interval = Math.Max(0.1, SupportedChannelSeconds(conn, w.LinkedSkillId) ?? attackSeconds);
                                w.ChannelInterval = interval;
                                w.ChannelHitsLeft = 3;
                                w.ContactAt = now + Math.Min(0.1, interval * 0.5);
                                w.FinishAt = Math.Min(w.ExpiresAt, w.ContactAt + interval * 2.5);
                            }
                            else
                            {
                                w.ContactAt = now + contact;
                                w.FinishAt = Math.Min(w.ExpiresAt, now + Math.Max(attackSeconds, contact + 0.05));
                            }
                            actions.Add((w.Id, InstanceProtocol.ActionStarted(w.Id, here.X, here.Y,
                                picked.At.X, picked.At.Y, w.LinkedSkillId, 0xffff, conn.MonsterOrderFlags,
                                targetObjectId: picked.Id),
                                $"0x01ab mirage warrior 0x{w.Id:x} uses {w.LinkedEffect.Name} on 0x{picked.Id:x}"));
                        }
                        else if (now - w.MoveOrderAt >= 0.25)
                        {
                            w.Monster.Walk.Order(picked.At.X, picked.At.Y, now);
                            w.MoveOrderAt = now;
                            actions.Add((w.Id, InstanceProtocol.ActionStarted(w.Id, here.X, here.Y,
                                picked.At.X, picked.At.Y, 0x2909, 0xffff, conn.MonsterOrderFlags), null));
                        }
                    }
                    else if (w.Phase == MirageWarriorPhase.Attacking)
                    {
                        if (w.Channelled)
                        {
                            if (w.ChannelHitsLeft > 0 && now >= w.ContactAt)
                            {
                                w.ChannelHitsLeft--;
                                w.ContactAt = now + w.ChannelInterval;
                                toStrike.Add(w);
                            }
                            if (w.ChannelHitsLeft <= 0 && now >= w.FinishAt)
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
            await DespawnAllMirageWarriors(remote, stream, ciphers, conn);
            return;
        }

        foreach (var (id, packet, log) in actions)
            await SendWorldRecord(remote, stream, ciphers, conn, id, packet, log);

        foreach (var w in toStrike)
            await ExecuteMirageWarriorHit(remote, stream, ciphers, conn, w);

        foreach (uint id in toDespawn)
            await SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd mirage warrior 0x{id:x} vanishes");
    }

    static double MirageWarriorReach(ClientState conn, MirageWarrior w, SkillCatalog.Unarmed weapon, SpawnedMonster target)
    {
        double body = Math.Max(0, LivingCollision.Size(target));
        double baseReach = Math.Max(weapon.Range + 2, 8) + body;
        if (Supports.Of(w.LinkedGem.Effect)?.Types.Contains(7) == true)
        {
            int radius = SkillRadius(conn, w.LinkedGem.Stats, spell: false, w.LinkedGem.Effect);
            return Math.Max(baseReach, Math.Min(radius, 24) + body);
        }
        return baseReach;
    }

    static (uint Id, SpawnedMonster Monster, (int X, int Y) At)? PickMirageWarriorTargetLocked(
        ClientState conn, (int X, int Y) from, uint currentTarget, HashSet<uint> claimed, double now)
    {
        // Keep current alive target if still within aggro range.
        if (currentTarget != 0 && conn.Monsters.TryGetValue(currentTarget, out var kept)
            && kept.Hostile && !kept.Dead && kept.Life > 0 && kept.OwnerId is null && !kept.WorldHidden)
        {
            var keptAt = kept.Walk.Position(now);
            if (Dist(from, keptAt) <= MirageWarriorAggroRange)
                return (currentTarget, kept, keptAt);
        }

        (uint Id, SpawnedMonster Monster, (int X, int Y) At)? bestUnclaimed = null;
        double bestUnclaimedDist = double.MaxValue;
        (uint Id, SpawnedMonster Monster, (int X, int Y) At)? bestAny = null;
        double bestAnyDist = double.MaxValue;

        foreach (var (id, m) in conn.Monsters)
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || m.OwnerId is not null || m.WorldHidden
                || double.IsPositiveInfinity(m.BusyUntil)) continue;
            var at = m.Walk.Position(now);
            double d = Dist(from, at);
            if (d > MirageWarriorAggroRange) continue;
            if (d < bestAnyDist) { bestAnyDist = d; bestAny = (id, m, at); }
            if (!claimed.Contains(id) && d < bestUnclaimedDist) { bestUnclaimedDist = d; bestUnclaimed = (id, m, at); }
        }
        return bestUnclaimed ?? bestAny;
    }

    /// <summary>
    /// Executes one melee attack hit for a Mirage Warrior using the player's attack stats + General's Cry damage
    /// factor, handling melee strikes, area/slam/channelling melee skills, and melee-with-projectile attacks
    /// (e.g. Lightning Strike, Molten Strike, Frost Blades, Volcanic Fissure).
    /// </summary>
    static async Task ExecuteMirageWarriorHit(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, MirageWarrior w)
    {
        ushort skillId = w.LinkedSkillId;
        var gem = w.LinkedEffect;
        var hits = new List<(uint Id, SpawnedMonster Monster, MonsterHit Hit, int Damage, byte HitCode, (int X, int Y) TargetAt, bool Critical, StunHit Stun)>();
        var dotPackets = new List<byte[]>();
        (int X, int Y) warriorPos = default;

        var session = ProjectilesFor(remote, stream, ciphers, conn);
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double now = ProjectileWorldTime(conn);
            warriorPos = w.Monster.Walk.Position(now);

            var targetMonster = w.TargetId != 0 && conn.Monsters.TryGetValue(w.TargetId, out var tm)
                && tm.Hostile && !tm.Dead && tm.Life > 0 ? tm : null;
            var aim = targetMonster?.Walk.Position(now) ?? warriorPos;

            using var scope = new PlayerStatScope(conn, fresh: true);
            var player = new Dictionary<uint, int>(CharacterPlayerStats(conn));
            // Ancestral Bond / "deal no damage yourself" apply to the player, not to Mirage Warriors.
            foreach (string name in new[] { "keystone_ancestral_bond", "deal_no_damage_yourself" })
                if (Stats.Key(name) is uint key) player.Remove(key);

            var admittedGem = SupportedSkill(conn, skillId);
            if (admittedGem.Effect != gem.Effect) return;
            var skillStats = admittedGem.Stats;
            var weapon = WeaponFor(conn);
            var local = weapon.LocalSources;
            var criticalRoll = CaptureSkillCriticalRoll(conn, skillId);
            double factor = w.DamageFactor;

            // Projectile component for hybrid melee skills (Lightning Strike, Molten Strike, Frost Blades, Volcanic Fissure, etc.).
            bool hasProjectiles = UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null;
            uint meleeExcludedId = 0;

            // Resolve melee strike / area hit.
            if (SupportedHitInputsFor(conn, skillId, weapon: weapon, capturedGem: admittedGem,
                area: IsSmite(gem.Effect) ? true : null) is { } capturedInputs)
            {
                var hitPacket = SupportedHitFrom(capturedInputs, suppress: false).Scale(factor);
                var ignite = ScaleSnipeIgnite(SupportedIgnite(conn, skillId, false, player, null, 0, capturedInputs), factor);
                var bleed = ScaleSnipeIgnite(SupportedBleed(conn, skillId, false, player, null, melee: true, 0, capturedInputs), factor);
                var poison = ScaleSnipePoison(SupportedPoison(conn, skillId, false, player, null, 0, capturedInputs), factor);
                var accuracy = SupportedAccuracy(conn, skillId, AttackHand.Main);
                var pen = Penetration.Of(player, Stats, attack: true, skillStats, local);
                var resMods = HitResistMods(player, skillStats);
                int critMult = SupportedCriticalMultiplier(conn, skillId);
                var impale = ImpaleStats.From(player, Stats, attack: true, skillStats, local);
                var critImpale = ImpaleStats.From(player, Stats, attack: true, skillStats, local, critical: true);
                bool isArea = Supports.Of(gem.Effect)?.Types.Contains(7) == true
                    || SkillsFor(conn).IsChannelled(gem.Effect);
                int radius = isArea
                    ? SkillRadius(conn, skillStats, spell: false, gem.Effect)
                    : Math.Max(weapon.Range + 2, 8);
                var centre = isArea
                    ? MeleeAreaCentre.Of(SkillsFor(conn).ActionHash(gem.Effect), warriorPos, aim, radius + weapon.Range)
                    : aim;
                int splashRadius = !isArea && GolemStat(skillStats, "support_melee_splash_area_of_effect_+%") != 0
                    || !isArea && GolemStat(skillStats, "melee_splash") != 0
                    ? SkillRadius(conn, 14, skillStats, spell: false, gem.Effect) : 0;

                List<(uint Id, SpawnedMonster Monster, (int X, int Y) At)> victims;
                if (isArea)
                {
                    victims = [.. conn.Monsters
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
                        var fallback = conn.Monsters
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
                        foreach (var (id, m) in conn.Monsters)
                        {
                            if (id == primaryId || !m.Hostile || m.Dead || m.Life == 0 || m.OwnerId is not null || m.WorldHidden) continue;
                            var mAt = m.Walk.Position(now);
                            if (Dist(mAt, primaryAt) <= splashRadius) victims.Add((id, m, mAt));
                        }
                    }
                }

                var gambler = GamblerFor(player);
                int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
                foreach (var (id, m, targetAt) in victims)
                {
                    if (m.Dead || m.Life == 0) continue;
                    int hitChance = AttackHitChanceAt(conn, id, m, now, accuracy, player);
                    if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, now, conn.AttackAccuracyRandom)) continue;
                    if (MonsterBlocks(m, Random.Shared, spell: false, now, skillStats)) continue;
                    var rolled = WithConditionalSupportDamageLocked(conn, skillStats, attack: true, m,
                        GameEvents.WithOutgoingDamage(conn, player, m,
                            (gambler with { DamageMorePct = 0 }).ForSkill(Stat, spell: false)
                                .ForWeapon(local).RollOutgoingHit(hitPacket, Random.Shared)), now);
                    int critChance = CriticalChanceOn(conn, id, SupportedCriticalChance(conn, skillId),
                        SupportedCriticalChanceVsBlinded(conn, skillId, release: false, null),
                        SupportedCriticalChanceVsGround(conn, skillId, release: false, null));
                    bool critical = RollCriticalStrike(conn, critChance, skillStats, attack: true, skillRoll: criticalRoll, player: player)
                        && EvasionEntropy.ConfirmsCritical(hitChance, conn.AttackAccuracyRandom);
                    int multiplier = CriticalMultiplierOn(m, critMult, player, skillStats);
                    var (damage, _, mitigated) = MonsterHitDamage(m, rolled, critical, attack: true, now, resMods, pen,
                        gambler.InvertsResistance(Random.Shared));
                    if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                    damage = WithImpaleHitLocked(conn, m, damage, rolled, critical, critMult,
                        critical ? critImpale : impale, 1).Damage;
                    var stun = OwnerStun(conn, m, damage, mitigated, melee: true, spell: false, critical, player, skillStats);
                    if (ApplyMonsterHitLocked(conn, m, damage, rampageSource: null, player: null, allyAt: warriorPos,
                        cull: Cull.Of(player, skillStats, critical, spell: false), cullPlayer: player) is not { } applied) continue;
                    if (applied.StageStarts)
                    {
                        CommitBossStage(remote, stream, ciphers, conn, id, m, now);
                        applied = applied with { StageStarts = false };
                    }
                    if (!m.Invulnerable)
                    {
                        AilmentsOnHitLocked(conn, id, m, mitigated, spell: false, critical, multiplier, player, skillStats, now,
                            Random.Shared, poison: poison, sourceSkill: skillId);
                        if (applied.Life > 0 && ignite.CanIgnite(rolled)
                            && IgniteOnHitLocked(conn, id, m, ignite, critical, now, playerIgnite: false) is { } ign)
                            dotPackets.AddRange(ign);
                        if (applied.Life > 0 && rolled[DamageType.Physical].Max > 0
                            && BleedOnHitLocked(conn, id, m, bleed, critical, now) is { } bld)
                            dotPackets.AddRange(bld);
                    }
                    if (meleeExcludedId == 0 && IsLightningStrike(gem.Effect)) meleeExcludedId = id;
                    hits.Add((id, m, applied, damage, HitCode(mitigated), targetAt, critical, stun));
                }
            }

            // Release linked projectiles (e.g. Lightning Strike bolts, Molten Strike balls, Frost Blades, Volcanic Fissure).
            if (hasProjectiles && session.Active)
            {
                var use = CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, from: warriorPos);
                var shot = (ProjectileOffense)use.Payload!;
                ProjectileActorKey? excluded = meleeExcludedId != 0 && conn.Monsters.TryGetValue(meleeExcludedId, out var exM)
                    ? session.ActorKey(meleeExcludedId, exM) : null;
                var mirageShot = shot with
                {
                    MirageSource = null,
                    Mirage = true,
                    CriticalTrigger = null,
                    ClientPredicted = false,
                    ActualCaster = player,
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
            await Send(remote, stream, ciphers, pkt, null);

        var deaths = new List<(uint Id, SpawnedMonster Monster)>();
        foreach (var (id, m, hit, damage, hitCode, targetAt, critical, _) in hits)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, w.Id,
                InstanceProtocol.ActionHit(w.Id, 0xffff, id,
                    flags: MonsterHitFlags(blocked: false, critical: critical),
                    kind: 0x03, code: hitCode, angle: HitAngle(targetAt.X - warriorPos.X, targetAt.Y - warriorPos.Y)),
                null);
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, hit, damage,
                $"Mirage Warrior ({gem.Name})", deaths: deaths);
        }
        foreach (var (id, m) in deaths)
            await MonsterKilled(remote, stream, ciphers, conn, id, m);
    }
}
