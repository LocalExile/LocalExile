using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.Bosses;

/// <summary>
/// Registration hub and shared retail 3.29.3c combat, arena-object, voice-line, and loot infrastructure for the three
/// Settlers of Kalguur boss encounters:
/// <list type="bullet">
/// <item><see cref="BlackKnightController"/> — The Black Knight in Starfall Crater (<c>SettlersBossFallenStar</c>, WorldAreas#1919)</item>
/// <item><see cref="AdmiralValeriusController"/> — Admiral Valerius in Sailor's Folly (<c>SettlersBossPirateCove</c>, WorldAreas#1920)</item>
/// <item><see cref="BanditLordController"/> — Sasan, the Bandit Lord in Abandoned Port (<c>SettlersBossAlleyway</c>, WorldAreas#1921)</item>
/// </list>
/// All three controllers replace their auto-populated boss with a <c>Scripted = true</c> actor carrying the full retail
/// <c>0x4000+</c> <c>GrantedSkill</c> list so the ordinary monster AI never fights the controller and every animation
/// clip resolves natively on the 3.29.3c client.
/// </summary>
public static class SettlersBosses
{
    public const string KingsmarchAreaId = "KalguuranSettlersLeague";
    public const ushort KingsmarchAreaHash = 36691; // WorldAreas#1918
    public const ushort MoveSkill = 0x2909;         // ActionTypes Move HASH16 & 0x3fff
    public const ushort InvulnerabilityBuffRow = 7; // BuffDefinitions#7 god_mode ("Invulnerability")
    public const ushort CannotBeDamagedBuffRow = 350;

    public const string SettlersBossPortalType = "Metadata/MiscellaneousObjects/Settlers/SettlersBossPortal";
    public const string BossForceFieldDoorType = "Metadata/MiscellaneousObjects/BossForceFieldDoor";
    public const string ArenaCentreMarkerType = "Metadata/Monsters/LeagueKalguur/ArenaCentreMarker";
    public const string ArenaMiddleType = "Metadata/MiscellaneousObjects/ArenaMiddle";

    // Verified client.db NPCs rows (loaded dynamically in LoadGameData with exact 3.29.3c fallbacks).
    internal static ushort BanditLordNpcRow = 680;
    internal static ushort BlackKnightNpcRow = 681;
    internal static ushort AdmiralValeriusNpcRow = 682;

    static Dictionary<string, StateMachineData?> ObjectStates = new(StringComparer.Ordinal);
    static Dictionary<string, ushort> VoiceLines = new(StringComparer.Ordinal);
    static string[] ValidRunegraftIds = [];
    static volatile bool DataLoaded;

    // Shared timed visual effect handle.
    internal sealed record TimedEffect(uint Id, (int X, int Y) At, double EndsAt, string Model, float Scale = 1f);

    // Shared per-player "Soaked to the Bone" debuff tracking (BuffDefinitions#2477, max 8 stacks, +10% damage taken per stack).
    internal sealed class SoakedState
    {
        public readonly object Lock = new();
        public int Stacks;
        public double ExpiresAt;
        public ushort BuffInstance;
        public bool Counted;
    }

    internal static readonly ConditionalWeakTable<Player, SoakedState> SoakedDebuffs = new();
    internal static int SoakedHolders;

    /// <summary>Loads shared StateMachine defaults, boss NPC voice lines, and the valid Runegraft drop pool from <c>client.db</c>.</summary>
    public static void LoadGameData()
    {
        var wantedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            SettlersBossPortalType,
            BossForceFieldDoorType,
            BlackKnightController.BossVarietyId,
            BlackKnightController.PillarType,
            BlackKnightController.LaserPillarType,
            BlackKnightController.MainDepositType,
            BlackKnightController.SmallDepositType,
            AdmiralValeriusController.BossVarietyId,
            AdmiralValeriusController.CannonballMarkerType,
            AdmiralValeriusController.PirateGeyserType,
            BanditLordController.BossVarietyId,
            BanditLordController.FlareObjectType,
            BanditLordController.FireCircleType,
            BanditLordController.FireWakeType,
            BanditLordController.RubbishStackType,
        };

        var shared = ObjectTypes.SharedStates();
        ObjectStates = ObjectTypes.All()
            .Where(t => wantedTypes.Contains(t.Type))
            .ToDictionary(t => t.Type, t => ObjectTypes.StateMachine(t.Row, shared), StringComparer.Ordinal);

        // NPCs rows for S->C 0x01c7 boss voice lines.
        if (ClientTables.Has("NPCs"))
        {
            foreach (var r in ClientTables.Table("NPCs"))
            {
                string? id = r.TextOrNull("Id");
                if (id == BanditLordController.BossVarietyId) BanditLordNpcRow = (ushort)r.Index;
                else if (id == BlackKnightController.BossVarietyId) BlackKnightNpcRow = (ushort)r.Index;
                else if (id == AdmiralValeriusController.BossVarietyId) AdmiralValeriusNpcRow = (ushort)r.Index;
            }
        }

        // NPCTextAudio rows for all 3 bosses.
        var lines = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (ClientTables.Has("NPCTextAudio"))
        {
            foreach (var r in ClientTables.Table("NPCTextAudio"))
            {
                string? id = r.TextOrNull("Id");
                if (id is null) continue;
                if (id.StartsWith("TheBlackKnight_", StringComparison.Ordinal)
                    || id.StartsWith("AdmiralValerious_", StringComparison.Ordinal)
                    || id.StartsWith("BanditArcherBoss_", StringComparison.Ordinal))
                {
                    lines.TryAdd(id, (ushort)r.Index);
                }
            }
        }
        VoiceLines = lines;

        // Valid 3.29.3c Runegraft BaseItemTypes (excluding [DNT - UNUSED] rows).
        var runegrafts = new List<string>();
        if (ClientTables.Has("BaseItemTypes"))
        {
            foreach (var r in ClientTables.Table("BaseItemTypes"))
            {
                string? id = r.TextOrNull("Id");
                string? name = r.TextOrNull("Name");
                if (id is null || name is null) continue;
                if (id.StartsWith("Metadata/Items/Currency/Runegraft", StringComparison.Ordinal)
                    && !name.Contains("DNT", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("UNUSED", StringComparison.OrdinalIgnoreCase))
                {
                    runegrafts.Add(id);
                }
            }
        }
        ValidRunegraftIds = [.. runegrafts];
        DataLoaded = true;
    }

    /// <summary>Registers all three Settlers boss arena controllers onto <see cref="GameEvents"/>.</summary>
    public static void Register(IModuleContext module) =>
        Register(module.Manifest.Id, module.Lifetime);

    /// <summary>Registers all three Settlers boss arena controllers under <paramref name="ownerId"/>.</summary>
    public static void Register(string ownerId, IModuleLifetime? lifetime = null)
    {
        if (!DataLoaded) LoadGameData();

        GameEvents.AreaGenerated.Add(ownerId, OnAreaGenerated);
        GameEvents.AreaPopulated.Add(ownerId, OnAreaPopulated);
        GameEvents.PlayerEntered.Add(ownerId, OnPlayerEntered);
        GameEvents.AreaTick.Add(ownerId, GameEvents.InArea(BlackKnightController.AreaId, BlackKnightController.Tick));
        GameEvents.AreaTick.Add(ownerId, GameEvents.InArea(AdmiralValeriusController.AreaId, AdmiralValeriusController.Tick));
        GameEvents.AreaTick.Add(ownerId, GameEvents.InArea(BanditLordController.AreaId, BanditLordController.Tick));
        GameEvents.MonsterKilled.Add(ownerId, OnMonsterKilled);
        GameEvents.ObjectUse.Add(ownerId, OnObjectUse);
        GameEvents.PlayerLeaving.Add(ownerId, OnPlayerLeaving);
        GameEvents.Stats.Add(ownerId, ApplyBossDebuffStats);

        lifetime?.Own(new UnloadCleanup());
    }

    sealed class UnloadCleanup : IDisposable
    {
        public void Dispose()
        {
            BlackKnightController.ResetOnUnload();
            AdmiralValeriusController.ResetOnUnload();
            BanditLordController.ResetOnUnload();
            SoakedDebuffs.Clear();
            Volatile.Write(ref SoakedHolders, 0);
            SettlersBossCallbacks.Reset();
        }
    }

    static async Task OnAreaGenerated(Player player)
    {
        switch (player.Layout.Id)
        {
            case BlackKnightController.AreaId:
                await BlackKnightController.OnAreaGenerated(player);
                break;
            case AdmiralValeriusController.AreaId:
                await AdmiralValeriusController.OnAreaGenerated(player);
                break;
            case BanditLordController.AreaId:
                await BanditLordController.OnAreaGenerated(player);
                break;
        }
    }

    static async Task OnAreaPopulated(AreaPopulated populated)
    {
        switch (populated.Player.Layout.Id)
        {
            case BlackKnightController.AreaId:
                await BlackKnightController.OnAreaPopulated(populated.Player, populated.Level);
                break;
            case AdmiralValeriusController.AreaId:
                await AdmiralValeriusController.OnAreaPopulated(populated.Player, populated.Level);
                break;
            case BanditLordController.AreaId:
                await BanditLordController.OnAreaPopulated(populated.Player, populated.Level);
                break;
        }
    }

    static async Task OnPlayerEntered(Player player)
    {
        switch (player.Layout.Id)
        {
            case BlackKnightController.AreaId:
                await BlackKnightController.OnPlayerEntered(player);
                break;
            case AdmiralValeriusController.AreaId:
                await AdmiralValeriusController.OnPlayerEntered(player);
                break;
            case BanditLordController.AreaId:
                await BanditLordController.OnPlayerEntered(player);
                break;
        }
    }

    static async Task OnMonsterKilled(MonsterKill kill)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        switch (kill.Player.Layout.Id)
        {
            case BlackKnightController.AreaId:
                await BlackKnightController.OnMonsterKilled(kill);
                break;
            case AdmiralValeriusController.AreaId:
                await AdmiralValeriusController.OnMonsterKilled(kill);
                break;
            case BanditLordController.AreaId:
                await BanditLordController.OnMonsterKilled(kill);
                break;
        }
    }

    static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt) =>
        player.Layout.Id switch
        {
            BlackKnightController.AreaId => await BlackKnightController.OnObjectUse(player, use, requestedAt),
            AdmiralValeriusController.AreaId => await AdmiralValeriusController.OnObjectUse(player, use, requestedAt),
            BanditLordController.AreaId => await BanditLordController.OnObjectUse(player, use, requestedAt),
            _ => false,
        };

    static async Task OnPlayerLeaving(Player player, string reason)
    {
        // Clear any lingering Soaked to the Bone debuff when leaving Sailor's Folly.
        if (Volatile.Read(ref SoakedHolders) > 0 && SoakedDebuffs.TryGetValue(player, out var soaked))
        {
            lock (soaked.Lock)
            {
                if (soaked.Counted)
                {
                    soaked.Counted = false;
                    Interlocked.Decrement(ref SoakedHolders);
                }
                soaked.Stacks = 0;
                soaked.ExpiresAt = 0;
                soaked.BuffInstance = 0;
            }
        }
        switch (player.Layout.Id)
        {
            case BlackKnightController.AreaId:
                await BlackKnightController.OnPlayerLeaving(player);
                break;
            case AdmiralValeriusController.AreaId:
                await AdmiralValeriusController.OnPlayerLeaving(player);
                break;
            case BanditLordController.AreaId:
                await BanditLordController.OnPlayerLeaving(player);
                break;
        }
    }

    static void ApplyBossDebuffStats(StatSheet sheet)
    {
        if (Volatile.Read(ref SoakedHolders) == 0) return;
        var player = sheet.Player;
        if (player.Layout.Id != AdmiralValeriusController.AreaId) return;
        if (!SoakedDebuffs.TryGetValue(player, out var soaked)) return;
        int stacks;
        double expiresAt;
        lock (soaked.Lock)
        {
            stacks = soaked.Stacks;
            expiresAt = soaked.ExpiresAt;
        }
        if (stacks <= 0 || AreaTime(player) >= expiresAt) return;
        // Pure read-only stat application; TickSoakedExpiries/OnPlayerLeaving own removal & RemoveBuff packets.
        // BuffDefinitions#2477 (kalguur_admiral_geyser_debuff) grants Stat#479 (base_damage_taken_+%): +10% per stack up to 8 stacks.
        sheet.Add(Stat.BaseDamageTakenPlusPct, stacks * AdmiralValeriusController.SoakedDamageTakenPerStack);
    }

    internal static async Task RefreshPlayerStats(Player player, string why)
    {
        if (player.Closed || player.LeavingArea) return;
        MarkPlayerStats(player, PlayerStatStep.Sources);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), $"0x01b3 player stats (Settlers boss: {why})");
    }

    // ---- Shared Arena & Monster Helpers ----

    internal static bool Alive(SpawnedMonster? m) => m is { Dead: false, Life: > 0 };

    internal static double Distance((int X, int Y) a, (int X, int Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static bool Near((int X, int Y) a, (int X, int Y) b, double radius)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    internal static (int X, int Y) WalkableNear(Player player, (int X, int Y) at) =>
        player.Layout.Walkable is { } w && !w(at.X, at.Y) ? player.Layout.Near(at) : at;

    internal static (int X, int Y) StepToward((int X, int Y) from, (int X, int Y) to, double dist)
    {
        double d = Distance(from, to);
        if (d <= dist || d < 0.001) return to;
        return ((int)Math.Round(from.X + (to.X - from.X) * dist / d),
                (int)Math.Round(from.Y + (to.Y - from.Y) * dist / d));
    }

    internal static Player[] ActivePlayers(Player runner) =>
        [.. runner.Frames.Players().Where(p => !p.Closed && !p.LeavingArea && p.Connection.PlayerSent
            && ReferenceEquals(p.Instance, runner.Instance))];

    internal static Player? NearestLivingPlayer(Player runner, (int X, int Y) origin, double now, double maxRange = 200, uint? awakeId = null)
    {
        Player? best = null;
        double bestDist = maxRange;
        foreach (var p in runner.Frames.Players())
        {
            if (p.Closed || p.LeavingArea || !p.Connection.PlayerSent || !ReferenceEquals(p.Instance, runner.Instance))
                continue;
            if (p.PlayerLife == 0 || p.Paused || InGracePeriod(p))
                continue;
            if (awakeId is uint mid && (!MonsterShown(p, mid) || MonsterAsleep(p, mid)))
                continue;
            double d = Distance(origin, p.Walk.Position(now));
            if (d <= bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    internal static StateMachineData? BaseStates(string type) =>
        ObjectStates.GetValueOrDefault(type)
        ?? (QuestObjectTypes.Find(type)?.Components.Contains("StateMachine") == true ? new StateMachineData([], 0) : null);

    internal static QuestObjectTypes.Info? ObjectInfo(string type)
    {
        if (QuestObjectTypes.Find(type) is not { } info) return null;
        bool hasSm = ObjectStates.GetValueOrDefault(type) is not null || info.Components.Contains("StateMachine");
        return QuestObject.CanSerialize(info.Components, hasSm) ? info : null;
    }

    internal static ushort SkillSlot(int[] effects, int effect)
    {
        int idx = Array.IndexOf(effects, effect);
        return (ushort)(0x4000 + Math.Max(0, idx));
    }

    /// <summary>Builds the native <c>0x4000+</c> <see cref="GrantedSkill"/> list for a scripted Settlers boss.</summary>
    internal static IReadOnlyList<GrantedSkill> BuildBossSkills(Player player, MonsterCatalog.Variety variety, byte level, int[] effects)
    {
        var catalog = SkillsFor(player);
        var list = new List<GrantedSkill>(effects.Length);
        for (int i = 0; i < effects.Length; i++)
        {
            int effect = effects[i];
            ushort slot = (ushort)(0x4000 + i);
            int rank = catalog.MaxLevelAt(effect, level);
            var atk = BossKits.AttackFor(player, variety, level, effect, slot);
            if (catalog.FindByRow(slot, effect, rank, MonsterActionSkillStats(player, effect, rank, atk)) is { } granted)
                list.Add(granted);
        }
        return list;
    }

    /// <summary>Builds a scripted <see cref="MonsterAttack"/> from a boss's retail GrantedEffect row with clean conversion &amp; fallback scaling.</summary>
    internal static MonsterAttack BuildBossAttack(Player player, SpawnedMonster boss, int[] effects, int effect,
        double scale = 1.0, double range = 30, int physToColdPct = 0, int physToFirePct = 0,
        bool? forceSpell = null, SkillCatalog.MonsterProjectileInfo? projectile = null)
    {
        ushort slot = SkillSlot(effects, effect);
        var variety = boss.Variety!;
        var atk = BossKits.AttackFor(player, variety, boss.Level, effect, slot);
        DamagePacket damage = atk.Damage;
        if (damage.IsZero)
        {
            var (min, max) = Monsters.BaseDamage(variety, boss.Level) ?? (150, 225);
            damage = DamagePacket.Of(DamageType.Physical, min, max);
        }
        if (scale != 1.0) damage = damage.Scale(scale);
        if (physToColdPct > 0) damage = ConvertPhysical(damage, DamageType.Cold, physToColdPct);
        if (physToFirePct > 0) damage = ConvertPhysical(damage, DamageType.Fire, physToFirePct);
        return atk with
        {
            SkillId = slot,
            Range = Math.Max(atk.Range, range),
            Damage = damage,
            Spell = forceSpell ?? atk.Spell,
            Scripted = true,
            Ranged = projectile is not null || atk.Ranged,
            Projectile = projectile ?? atk.Projectile,
        };
    }

    internal static DamagePacket ConvertPhysical(DamagePacket packet, DamageType to, int percent)
    {
        int pct = Math.Clamp(percent, 0, 100);
        if (pct == 0) return packet;
        var physical = packet[DamageType.Physical];
        if (physical.Max <= 0) return packet;
        var converted = physical.Scale(pct / 100.0);
        return packet.Select((type, range) => type == DamageType.Physical ? range.Scale(1.0 - pct / 100.0) : range)
            .Add(to, converted);
    }

    /// <summary>Replaces an auto-populated (non-scripted) boss with a <c>Scripted = true</c> boss carrying the full retail
    /// skill list, or spawns it fresh if absent. Preserves existing <c>Scripted = true</c> bosses on re-entry.</summary>
    internal static async Task<(uint Id, SpawnedMonster Monster)?> EnsureScriptedBoss(
        Player player, string varietyId, byte level, (int X, int Y) fallbackAt, float fallbackRotation,
        int[] skillEffects, Func<MonsterObject, MonsterObject>? shape = null)
    {
        // Check if a scripted boss of this variety already exists (e.g. re-entry into Starfall Crater).
        foreach (var (existingId, existing) in player.Area.Monsters.ToArray())
        {
            if (existing.Variety?.Id == varietyId && existing.Scripted)
                return (existingId, existing);
        }

        // Remove any auto-populated non-scripted instance of this boss so ordinary AI never conflicts.
        var spawnAt = fallbackAt;
        float rotation = fallbackRotation;
        foreach (var (existingId, existing) in player.Area.Monsters.ToArray())
        {
            if (existing.Variety?.Id != varietyId) continue;
            spawnAt = existing.Walk.Position(AreaTime(player));
            if (player.Instance.World.MonsterOrigins.TryGetValue(existingId, out var origin))
                rotation = origin.Object.Rotation;
            await DespawnMonster(player, existingId);
        }

        if (Monsters.Find(varietyId) is not { Supported: true } variety) return null;
        var skills = BuildBossSkills(player, variety, level, skillEffects);
        var spot = WalkableNear(player, spawnAt);
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, spot.X, spot.Y,
            log: true, alone: false, unrisen: false, rotation: rotation,
            ownedSkills: skills, scripted: true, placement: MonsterPlacement.Established,
            instanceOwned: true, shape: shape);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var boss)) return null;
        return (id, boss);
    }

    internal static async Task DespawnMonster(Player player, uint id)
    {
        if (player.Area.Monsters.Remove(id, out var m))
        {
            m.Dead = true;
            m.Life = 0;
            m.ActionGeneration++;
        }
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        foreach (var member in ActivePlayers(player))
            await SendWorldRemoval(PlayerOf(member), id, null);
    }

    internal static async Task EnsureBossBar(Player runner, uint bossId, SpawnedMonster boss)
    {
        if (boss.Variety is not { BossHealthBar: true }) return;
        boss.BossBarShown = true;
        foreach (var p in ActivePlayers(runner))
            if (MonsterShown(p, bossId) && !MonsterAsleep(p, bossId))
                await ShowBossLifeBar(bossId, boss, p);
    }

    internal static int ComponentIndex(IReadOnlyList<string> components, string name)
    {
        for (int i = 0; i < components.Count; i++)
            if (components[i] == name) return i;
        return -1;
    }

    internal static async Task SetBossStates(Player player, uint bossId, params (string Name, int Value)[] changes)
    {
        if (!player.Area.Monsters.TryGetValue(bossId, out var boss) || boss.States is not { } current || boss.Variety is not { } variety)
            return;
        foreach (var (name, value) in changes) current = current.Set(name, value);
        boss.States = current;
        if (player.Instance.World.MonsterOrigins.TryGetValue(bossId, out var origin))
            player.Instance.World.MonsterOrigins[bossId] = origin with { Object = origin.Object with { StateMachine = current } };
        int comp = ComponentIndex(variety.ComponentsOrBase, "StateMachine");
        if (comp < 0) return;
        foreach (var member in ActivePlayers(player))
        {
            if (!member.Area.Monsters.TryGetValue(bossId, out var m) || m.Asleep) continue;
            await SendWorldRecord(member, bossId, InstanceEntities.StateMachineUpdate(bossId, (byte)comp, current), null);
        }
    }

    internal static async Task SetBossTransition(Player player, uint bossId, byte state)
    {
        if (!player.Area.Monsters.TryGetValue(bossId, out var boss) || boss.Variety is not { } variety) return;
        int comp = ComponentIndex(variety.ComponentsOrBase, "Transitionable");
        if (comp < 0) return;
        if (player.Instance.World.MonsterOrigins.TryGetValue(bossId, out var origin))
            player.Instance.World.MonsterOrigins[bossId] = origin with { Object = origin.Object with { TransitionState = state } };
        foreach (var member in ActivePlayers(player))
        {
            if (!member.Area.Monsters.TryGetValue(bossId, out var m) || m.Asleep) continue;
            await SendWorldRecord(member, bossId, InstanceEntities.TransitionableUpdate(bossId, (byte)comp, state), null);
        }
    }

    internal static void SetBossInvulnerable(Player player, uint bossId, SpawnedMonster boss, bool on)
    {
        boss.DamageImmune = on;
        if (on)
            player.Area.Buffs.Set(bossId, new(InvulnerabilityBuffRow, bossId));
        else
            player.Area.Buffs.Clear(bossId, InvulnerabilityBuffRow, bossId);
    }

    internal static async Task MoveBoss(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) goal, double now)
    {
        int generation = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        await SendMonsterAction(runner, bossId, boss, generation, () =>
        {
            double at = AreaTime(runner);
            var here = boss.Walk.Position(at);
            boss.Walk.Order(goal.X, goal.Y, at);
            return InstanceProtocol.ActionStarted(bossId, here.X, here.Y, goal.X, goal.Y, MoveSkill, runner.NextActionSequence(), runner.MonsterOrderFlags);
        }, null);
        boss.BusyUntil = Math.Max(boss.BusyUntil, now + 0.50);
    }

    internal static async Task TeleportBoss(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) dest, double now)
    {
        var safe = WalkableNear(runner, dest);
        boss.Walk.Teleport(safe.X, safe.Y, now);
        foreach (var member in ActivePlayers(runner))
            await SendWorldRecord(PlayerOf(member), bossId, InstanceProtocol.Teleport(bossId, safe.X, safe.Y), null);
    }

    /// <summary>Plays a scripted boss skill action (<c>0x01ab</c> + <c>0x01ac</c>) toward <paramref name="aim"/>.</summary>
    internal static async Task<bool> PlayBossSkill(Player runner, uint bossId, SpawnedMonster boss,
        int[] effects, int effect, (int X, int Y) aim, double now, double busySeconds, uint? targetObjectId = null)
    {
        var pos = boss.Walk.Position(now);
        boss.Walk.Order(pos.X, pos.Y, now);
        int generation = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        boss.BusyUntil = Math.Max(boss.BusyUntil, now + Math.Max(0.35, busySeconds));
        ushort slot = SkillSlot(effects, effect);
        return await SendMonsterAction(runner, bossId, boss, generation,
            () => [.. InstanceProtocol.ActionStarted(bossId, pos.X, pos.Y, aim.X, aim.Y, slot, runner.NextActionSequence(), 0x0408,
                       targetObjectId: targetObjectId),
                   .. InstanceProtocol.ActionEnded(bossId)],
            null);
    }

    /// <summary>Fires a real projectile/melee swing through <see cref="MonsterSwing"/> (non-blocking).</summary>
    internal static void LaunchBossSwing(Player runner, uint bossId, SpawnedMonster boss, MonsterAttack attack, double now,
        bool area = false, double radius = 0, double recoverySeconds = 0.25)
    {
        var pos = boss.Walk.Position(now);
        boss.Walk.Order(pos.X, pos.Y, now);
        int action = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        boss.BusyUntil = Math.Max(boss.BusyUntil, now + Math.Max(0.35, attack.Seconds) + recoverySeconds);
        _ = RunBossSwingSafe(runner, bossId, boss, action, attack, area, radius);
    }

    static async Task RunBossSwingSafe(Player runner, uint bossId, SpawnedMonster boss, int action, MonsterAttack attack, bool area, double radius)
    {
        try { await MonsterSwing(runner, bossId, boss, action, attack, area, radius); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[settlers-bosses] swing 0x{bossId:x} failed: {ex.Message}"); }
    }

    /// <summary>Sends a retail boss voice line (<c>S->C 0x01c7</c>) chosen randomly from matching <c>NPCTextAudio</c> ids.</summary>
    internal static async Task Speak(Player runner, uint bossId, ushort npcRow, string[] lineIds)
    {
        if (lineIds.Length == 0 || VoiceLines.Count == 0) return;
        string pick = lineIds.Length == 1 ? lineIds[0] : lineIds[AreaRng(runner).Next(lineIds.Length)];
        if (!VoiceLines.TryGetValue(pick, out ushort audioRow)) return;
        byte[] packet = InstanceNpcs.TextAudio(bossId, npcRow, audioRow);
        foreach (var member in ActivePlayers(runner))
            await Send(member, [.. packet], null);
    }

    // ---- Combat Hit & Ground Degen Helpers (Public Gateway APIs only) ----

    internal static async Task HitPlayer(Player target, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        Func<(int X, int Y), bool> inside, Func<bool> stillValid, Func<Player, Task>? onPlayerHitAsync = null)
    {
        MonsterHitOutcome? outcome = null;
        await SendWorldRecord(target, sourceId, [], null, atGate: () =>
        {
            if (!CurrentGameWork || !stillValid() || target.Closed || target.LeavingArea || target.Paused || InGracePeriod(target)) return null;
            double now = Math.Max(AreaTime(target), ProjectileWorldTime(target));
            foreach (var g in target.Golems.All.Values.Concat(target.Reservations.Fighters.Values).Concat(target.Summons.Fighters.Values).ToArray())
                if (!g.Monster.Dead && inside(g.Monster.Walk.Position(now)))
                    GolemTakeHitLocked(target, g, sourceId, source, attack, now, area: true);
            if (target.PlayerLife == 0 || !inside(target.Walk.Position(now))) return null;
            outcome = CommitMonsterHitLocked(target, sourceId, source, attack, now, target.Walk.Position(now),
                source.Walk.Position(now), false, target.NextActionSequence());
            return outcome?.HitPacket;
        });
        if (outcome is not null)
        {
            await PresentMonsterHit(target, sourceId, outcome);
            if (onPlayerHitAsync is not null) await onPlayerHitAsync(target);
        }
    }

    internal static async Task HitPlayers(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        Func<(int X, int Y), bool> inside, Func<bool> stillValid, Func<Player, Task>? onPlayerHitAsync = null)
    {
        foreach (var p in ActivePlayers(runner))
            await HitPlayer(p, sourceId, source, attack, inside, stillValid, onPlayerHitAsync);
    }

    internal static Task HitCircle(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) center, double radius, Func<bool> stillValid, Func<Player, Task>? onPlayerHitAsync = null) =>
        HitPlayers(runner, sourceId, source, attack, at => Near(at, center, radius), stillValid, onPlayerHitAsync);

    internal static Task HitRing(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) center, double innerRadius, double outerRadius, Func<bool> stillValid, Func<Player, Task>? onPlayerHitAsync = null) =>
        HitPlayers(runner, sourceId, source, attack, at =>
        {
            double d = Distance(at, center);
            return d >= innerRadius && d <= outerRadius;
        }, stillValid, onPlayerHitAsync);

    internal static Task HitCone(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) origin, double facingAngle, double halfAngleRad, double maxRange, Func<bool> stillValid,
        Func<Player, Task>? onPlayerHitAsync = null) =>
        HitPlayers(runner, sourceId, source, attack, at =>
        {
            double d = Distance(at, origin);
            if (d <= 4.0) return true;
            if (d > maxRange) return false;
            double angle = Math.Atan2(at.Y - origin.Y, at.X - origin.X);
            return Math.Abs(Math.IEEERemainder(angle - facingAngle, Math.Tau)) <= halfAngleRad;
        }, stillValid, onPlayerHitAsync);

    internal static Task HitSegment(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) from, (int X, int Y) to, double halfWidth, Func<bool> stillValid, Func<Player, Task>? onPlayerHitAsync = null)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lenSq = dx * dx + dy * dy;
        return HitPlayers(runner, sourceId, source, attack, at =>
        {
            if (lenSq < 1.0) return Near(at, from, halfWidth);
            double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / lenSq, 0.0, 1.0);
            double px = from.X + dx * t, py = from.Y + dy * t;
            double ex = at.X - px, ey = at.Y - py;
            return ex * ex + ey * ey <= halfWidth * halfWidth;
        }, stillValid, onPlayerHitAsync);
    }

    // ---- Visual Effects & QuestObject Helpers ----

    internal static async Task<uint> SpawnTimedEffect(Player runner, List<TimedEffect> list, (int X, int Y) at,
        string aoPath, double endsAt, float scale = 1f, float rotation = 0f, bool fixedRotation = true)
    {
        uint id = runner.TakeObjectId();
        var obj = new EffectObject(at.X, at.Y, aoPath)
        {
            Scale = scale,
            Rotation = rotation,
            Flags0 = fixedRotation ? EffectObject.FixedRotation : (byte)0,
        };
        var blob = obj.Blob();
        list.Add(new(id, at, endsAt, aoPath, scale));
        foreach (var member in ActivePlayers(runner))
            await SendWorldCreate(member, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), blob),
                null, wake: true, awaitBirth: false);
        return id;
    }

    internal static async Task ExpireEffects(Player runner, List<TimedEffect> list, double now)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].EndsAt > now) continue;
            uint id = list[i].Id;
            list.RemoveAt(i);
            foreach (var member in ActivePlayers(runner))
                await SendWorldRemoval(PlayerOf(member), id, null);
        }
    }

    internal static async Task ClearEffects(Player runner, List<TimedEffect> list)
    {
        var copy = list.ToArray();
        list.Clear();
        foreach (var fx in copy)
            foreach (var member in ActivePlayers(runner))
                await SendWorldRemoval(PlayerOf(member), fx.Id, null);
    }

    internal static async Task<bool> CreateQuestObj(Player player, uint id, string type, int x, int y, float rotation,
        string? model = null, byte transitionState = 1, bool blocked = false, StateMachineData? states = null,
        bool hideMinimap = false, float? height = null)
    {
        if (ObjectInfo(type) is not { } info) return false;
        string? effectiveModel = info.OwnModel && (string.IsNullOrEmpty(model) || model == info.AnimatedModel) ? null : model;
        var obj = new QuestObject(x, y, info.Components, rotation, height, effectiveModel,
            TransitionState: transitionState, Blocked: blocked)
        {
            StateMachine = states,
            HideMinimapIcon = hideMinimap,
        };
        if (!await SendWorldCreate(PlayerOf(player), id,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), obj.Blob()), null))
            return false;
        await SendWorldRecord(PlayerOf(player), id, InstanceProtocol.EnterWakeZone(id));
        return true;
    }

    /// <summary>Creates <c>Metadata/MiscellaneousObjects/Settlers/SettlersBossPortal</c> via <see cref="AreaTransitionObject"/>
    /// (which natively serializes its <c>AreaTransition</c> + trailing <c>StateMachine</c> <c>boss_aggro</c> component).</summary>
    internal static async Task<bool> CreateBossPortal(Player player, uint id, int x, int y, float rotation, bool open)
    {
        var sm = BaseStates(SettlersBossPortalType)?.Set("boss_aggro", open ? 1 : 2);
        var portal = new AreaTransitionObject(x, y, KingsmarchAreaHash, Rotation: rotation)
        {
            Kind = 3, // SideAreaReturn
            StateMachine = sm,
        };
        if (!await SendWorldCreate(PlayerOf(player), id,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(SettlersBossPortalType), portal.Blob()), null))
            return false;
        await SendWorldRecord(PlayerOf(player), id, InstanceProtocol.EnterWakeZone(id));
        return true;
    }

    internal static async Task UpdateObjStates(Player runner, uint id, string type, StateMachineData states)
    {
        if (QuestObjectTypes.Find(type) is not { } info) return;
        int idx = ComponentIndex(info.Components, "StateMachine");
        if (idx < 0) return;
        foreach (var member in ActivePlayers(runner))
            await SendWorldRecord(PlayerOf(member), id, InstanceEntities.StateMachineUpdate(id, (byte)idx, states), null);
    }

    internal static async Task UpdateObjTransition(Player runner, uint id, string type, byte state, bool? blocked = null)
    {
        if (QuestObjectTypes.Find(type) is not { } info) return;
        int tIdx = QuestObject.TransitionableIndex(info.Components);
        foreach (var member in ActivePlayers(runner))
        {
            if (tIdx >= 0)
                await SendWorldRecord(PlayerOf(member), id, InstanceEntities.TransitionableUpdate(id, (byte)tIdx, state), null);
            if (blocked is bool b)
                await SendWorldRecord(PlayerOf(member), id, InstanceEntities.BlockageUpdate(id, b), null);
        }
    }

    internal static void SetLineBarrier(Player player, uint objectId, int x, int y, float rotation, int halfWidth, int halfThickness, bool blocked)
    {
        if (!blocked)
        {
            if (player.DoorBarriers.Remove(objectId)) player.UpdateBarrier();
            return;
        }
        if (player.Layout.Walkable is not { } walk) return;
        var cells = new HashSet<(int X, int Y)>();
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        for (int u = -halfWidth; u <= halfWidth; u++)
            for (int v = -halfThickness; v <= halfThickness; v++)
            {
                int cx = x + (int)Math.Round(u * cos - v * sin);
                int cy = y + (int)Math.Round(u * sin + v * cos);
                if (walk(cx, cy)) cells.Add((cx, cy));
            }
        if (cells.Count > 0)
        {
            player.DoorBarriers[objectId] = cells;
            player.UpdateBarrier();
        }
    }

    internal static void SetDiscBarrier(Player player, uint objectId, int x, int y, int radius, bool blocked)
    {
        if (!blocked)
        {
            if (player.DoorBarriers.Remove(objectId)) player.UpdateBarrier();
            return;
        }
        var cells = new HashSet<(int X, int Y)>();
        int r2 = radius * radius;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                if (dx * dx + dy * dy <= r2)
                    cells.Add((x + dx, y + dy));
        if (cells.Count > 0)
        {
            player.DoorBarriers[objectId] = cells;
            player.UpdateBarrier();
        }
    }

    /// <summary>Creates or updates the arena's <c>SettlersBossPortal</c> (<c>boss_aggro=1</c> open, <c>2</c> closed) and
    /// handles clicking it to return to Kingsmarch.</summary>
    internal static async Task<bool> TryUseSettlersBossPortal(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt,
        uint portalId, (int X, int Y) portalAt, bool isOpen)
    {
        if (portalId == 0 || use.TargetId != portalId) return false;
        if (!isOpen)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        const int reach = 12;
        if (!TryBeginObjectWalk(player, requestedAt, portalAt.X, portalAt.Y, reach, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, portalAt.X, portalAt.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: portalId),
                null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (PlayerActionCurrent(player, action) && player.PendingTransfer is null
            && PlayerWalk.InWalkUpRange(stop, portalAt, reach))
        {
            await ChangeArea(player, new PendingTransfer(KingsmarchAreaHash, 0, false),
                "settlers boss portal to Kingsmarch");
        }
        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
        return true;
    }

    // ---- Boss Loot Helpers (Gold, Boss Uniques, Runegrafts, Rare/Currency) ----

    internal static async Task<int> DropBossGold(Player owner, (int X, int Y) at, int minGold, int maxGold, bool ownerOnly)
    {
        if (!GameEvents.AllowsLoot(owner)) return 0;
        var catalog = ItemsFor(owner);
        if (catalog.Find(ItemDrops.GoldId) is not { Supported: true } goldType) return 0;
        var rng = AreaRng(owner);
        int total = (int)Math.Round(rng.Next(minGold, maxGold + 1) * SettlersBossCallbacks.BossRewardMultiplier());
        if (total <= 0) return 0;

        int maxStack = goldType.MaxStack > 0 ? Math.Min(goldType.MaxStack, ushort.MaxValue) : 10000;
        int piles = Math.Clamp((total + maxStack - 1) / maxStack, 4, 12);
        int perPile = Math.Max(1, total / piles);
        int remaining = total;

        for (int i = 0; i < piles && remaining > 0; i++)
        {
            int stack = i == piles - 1 ? Math.Min(remaining, maxStack) : Math.Min(perPile, Math.Min(remaining, maxStack));
            remaining -= stack;
            var item = ItemCatalog.Create(goldType) with { StackSize = (ushort)stack };
            await DropItemOnGround(owner, at, item, i, ownerOnly);
        }
        return total;
    }

    internal static async Task<bool> TryDropBossUnique(Player owner, (int X, int Y) at, int areaLevel,
        IReadOnlyList<(string Name, int Weight)> pool, double dropChance, int scatterIndex)
    {
        if (!GameEvents.AllowsLoot(owner) || pool.Count == 0) return false;
        var rng = AreaRng(owner);
        double effectiveChance = Math.Clamp(dropChance * SettlersBossCallbacks.BossRewardMultiplier(), 0.0, 1.0);
        if (rng.NextDouble() >= effectiveChance) return false;

        int totalWeight = pool.Sum(p => p.Weight);
        int roll = rng.Next(Math.Max(1, totalWeight));
        string chosen = pool[^1].Name;
        foreach (var (name, weight) in pool)
        {
            if ((roll -= weight) < 0) { chosen = name; break; }
        }

        var uniques = UniquesFor(owner);
        if (uniques.Find(chosen) is not { } def || uniques.Create(def, rng) is not { } item) return false;
        item = item with { ItemLevel = (byte)Math.Clamp(areaLevel + 2, 1, 100), Identified = false };
        await DropItemOnGround(owner, at, item, scatterIndex, ownerOnly: false);
        return true;
    }

    internal static async Task<int> DropRunegrafts(Player owner, (int X, int Y) at, int areaLevel, int minCount, int maxCount, int scatterBase)
    {
        if (!GameEvents.AllowsLoot(owner) || ValidRunegraftIds.Length == 0) return 0;
        var catalog = ItemsFor(owner);
        var rng = AreaRng(owner);
        int count = rng.Next(minCount, maxCount + 1);
        int dropped = 0;
        for (int i = 0; i < count; i++)
        {
            string id = ValidRunegraftIds[rng.Next(ValidRunegraftIds.Length)];
            if (catalog.Find(id) is not { Supported: true } type) continue;
            var item = ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(areaLevel, 1, 100) };
            await DropItemOnGround(owner, at, item, scatterBase + i, ownerOnly: false);
            dropped++;
        }
        return dropped;
    }

    internal static async Task DropItemOnGround(Player owner, (int X, int Y) origin, Item item, int scatterIndex, bool ownerOnly)
    {
        item = GameEvents.PrepareGroundItem(owner, item);
        double angle = scatterIndex * 2.399963229728653 + 0.5;
        double dist = 4.0 + (scatterIndex % 4) * 2.5;
        var spot = owner.Layout.Near((origin.X + (int)Math.Round(Math.Cos(angle) * dist),
            origin.Y + (int)Math.Round(Math.Sin(angle) * dist)));
        var ground = new WorldItemObject(spot.X, spot.Y, item);
        uint id = owner.Instance.Ground.Add(ground, owner, shared: !ownerOnly);
        if (ownerOnly)
        {
            await SendGroundItem(owner, id, ground, "settlers boss drop", justDropped: true);
        }
        else
        {
            foreach (var member in ActivePlayers(owner))
                await SendGroundItem(member, id, ground, "settlers boss drop", justDropped: true);
        }
    }

    /// <summary>Sets both the character QuestFlag and the retail LeagueQuestFlag for all players in the boss arena.</summary>
    internal static async Task GrantBossKillFlags(Player runner, string questFlag, int leagueQuestFlag)
    {
        foreach (var member in ActivePlayers(runner))
        {
            if (!member.Character.HasQuestFlag(questFlag))
                await SetQuestFlags(member, [questFlag]);
            if (leagueQuestFlag > 0 && !member.Character.LeagueQuestFlags.Contains(leagueQuestFlag))
                await SetLeagueQuestFlags(member, [leagueQuestFlag]);
        }
    }
}
