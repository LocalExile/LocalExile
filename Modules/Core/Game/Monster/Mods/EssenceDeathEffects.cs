using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string EssenceExplosiveInsect = "Metadata/Monsters/InsectMinion/EssenceInsectWildExplosiveColossal";
    public const string EssenceInsectDeathStat = "essence_delirium_spawn_monster_on_death_variation";
    public const string EssenceInsectDeathMod = "EssenceInsectSummonedOnDeath";
    const string EssenceInsectEffect = "InsectSuicideExplosionHuge";
    // SuicideExplosion::execute (3.29.3d, 141a462cc): floor(15 * radius_percentage / 100).
    const int EssenceInsectBaseRadius = 15;
    public sealed record EssenceDeathDefinition(int Variety, int Count);
    public sealed class EssenceDeathSpawn(InstanceState instance, uint parent, byte level, (int X, int Y) at,
        EssenceDeathDefinition definition)
    {
        public InstanceState Instance { get; } = instance;
        public uint Parent { get; } = parent;
        public byte Level { get; } = level;
        public (int X, int Y) At { get; } = at;
        public EssenceDeathDefinition Definition { get; } = definition;
        internal ClientState? Claim;
        internal int Created;
    }
    sealed class EssenceDeathQueue { public readonly List<EssenceDeathSpawn> Pending = []; }
    static readonly RuleTable<InstanceState, EssenceDeathQueue> EssenceDeathQueues = new();
    static readonly RuleTable<SpawnedMonster, EssenceDeathSpawn> EssenceDeathReceipts = new();

    public static EssenceDeathDefinition? EssenceDeathDefinitionFor(int variation)
    {
        if (variation <= 0 || ClientTables.Table("SummonedSpecificMonstersOnDeath").Row(variation - 1) is not { } row
            || row.IntOrNull("MonsterVarietiesKey") is not int variety || Monsters.Find(variety.ToString())?.Id != EssenceExplosiveInsect)
            return null;
        return new(variety, Math.Max(1, row.Int("__col4")));
    }

    public static bool QueueEssenceDeathSpawnLocked(ClientState conn, uint id, SpawnedMonster source)
    {
        if (!CurrentGameWork || !source.Hostile || !source.Dead && source.Life > 0 || conn.Instance is not { } instance
            || !ReferenceEquals(conn.Monsters.GetValueOrDefault(id), source)
            || EssenceDeathReceipts.TryGetValue(source, out _) || source.Effects[EssenceInsectDeathStat] is not (> 0 and var variation)
            || EssenceDeathDefinitionFor(variation) is not { } definition) return false;
        var spawn = new EssenceDeathSpawn(instance, id, source.Level, source.Walk.Position(AreaTime(conn)), definition);
        EssenceDeathReceipts.Add(source, spawn);
        EssenceDeathQueues.GetOrCreateValue(instance).Pending.Add(spawn);
        return true;
    }

    // The instance keeps an accepted death if its killer leaves before the native create. A runner claims it only
    // until that connection leaves or loses the world; birth and acknowledgement share SpawnMonster's send gate.
    public static IReadOnlyList<EssenceDeathSpawn> ClaimEssenceDeathSpawnsLocked(ClientState conn)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || !IsMonsterRunner(conn)
            || conn.Instance is not { } instance || !EssenceDeathQueues.TryGetValue(instance, out var queue)) return [];
        List<EssenceDeathSpawn> result = [];
        foreach (var spawn in queue.Pending)
        {
            if (spawn.Claim is { Closed: false, LeavingArea: false } claim && ReferenceEquals(claim.Instance, spawn.Instance)
                && IsMonsterRunner(claim)) continue;
            spawn.Claim = conn;
            result.Add(spawn);
        }
        return result;
    }

    public static bool EssenceDeathSpawnCurrentLocked(ClientState conn, EssenceDeathSpawn spawn) =>
        CurrentGameWork && !conn.Closed && !conn.LeavingArea && ReferenceEquals(spawn.Instance, conn.Instance)
        && IsMonsterRunner(conn) && ReferenceEquals(spawn.Claim, conn) && spawn.Created < spawn.Definition.Count;

    public static void CompleteEssenceDeathSpawnLocked(ClientState conn, EssenceDeathSpawn spawn, bool admitted)
    {
        if (!ReferenceEquals(spawn.Claim, conn) || spawn.Created >= spawn.Definition.Count) return;
        if (admitted && ++spawn.Created >= spawn.Definition.Count && EssenceDeathQueues.TryGetValue(spawn.Instance, out var queue))
            queue.Pending.Remove(spawn);
        if (!admitted) spawn.Claim = null;
    }

    public static async Task TickEssenceDeathSpawns(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        IReadOnlyList<EssenceDeathSpawn> due;
        lock (conn.Monsters) due = ClaimEssenceDeathSpawnsLocked(conn);
        foreach (var spawn in due)
        {
            try
            {
                if (Monsters.Find(spawn.Definition.Variety.ToString()) is not { Supported: true } variety) continue;
                while (true)
                {
                    lock (conn.Monsters) if (!EssenceDeathSpawnCurrentLocked(conn, spawn)) break;
                    uint created = await SpawnMonster(remote, stream, ciphers, conn, variety, spawn.Level, MonsterRarity.Normal,
                        spawn.At.X, spawn.At.Y, alone: true, unrisen: false,
                        ownedAdmission: () => EssenceDeathSpawnCurrentLocked(conn, spawn),
                        owned: (_, child) =>
                        {
                            child.SummonerId = spawn.Parent; // not linked to the already-dead parent's Life
                            child.Aggro = true;
                            CompleteEssenceDeathSpawnLocked(conn, spawn, true);
                        });
                    if (created == 0) break;
                    Console.WriteLine($"[map-content] essence death source=0x{spawn.Parent:x} spawn=0x{created:x}: {variety.Id}");
                }
            }
            finally
            {
                lock (conn.Monsters) CompleteEssenceDeathSpawnLocked(conn, spawn, false);
            }
        }
    }

    static (int Effect, Dictionary<uint, int> Stats) EssenceInsectStats(ClientState conn, byte level)
    {
        int effect = SkillsFor(conn).EffectRow(EssenceInsectEffect);
        return (effect, GolemData.Skill(SkillsFor(conn), effect, level).Stats);
    }

    public static MonsterAttack EssenceInsectAttack(ClientState conn, byte level, ushort skillId, MonsterModEffects mods)
    {
        var (effect, stats) = EssenceInsectStats(conn, level);
        int Get(string n) => Stats.Key(n) is uint k ? stats.GetValueOrDefault(k) : 0;
        var damage = DamagePacket.Of(DamageType.Fire, Get("secondary_minimum_base_fire_damage"), Get("secondary_maximum_base_fire_damage"));
        double seconds = SkillsFor(conn).CastTimeMs(effect) / 1000d;
        double reach = 6 * Math.Max(0, 1 + Get("spell_maximum_action_distance_+%") / 100d);
        return WithMonsterMods(new(skillId, seconds, reach, damage) { Spell = true, Secondary = true, SkillSpeed = (0, 0) }, mods);
    }

    public static IReadOnlyList<(uint, int)> EssenceInsectSkillStats(ClientState conn, MonsterAttack attack,
        IReadOnlyList<(uint, int)> own, MonsterModEffects? effects = null)
    {
        var (_, raw) = EssenceInsectStats(conn, 1);
        var stats = new Dictionary<uint, int>(raw);
        int Get(string n) => (Stats.Key(n) is uint k ? stats.GetValueOrDefault(k) : 0) + (effects?[n] ?? 0);
        stats[Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value] = SkillArea.RadiusPercent(Get, spell: true);
        stats[Stats.Key("secondary_minimum_base_fire_damage")!.Value] = (int)Math.Floor(attack.Damage[DamageType.Fire].Min);
        stats[Stats.Key("secondary_maximum_base_fire_damage")!.Value] = (int)Math.Floor(attack.Damage[DamageType.Fire].Max);
        foreach (var (key, value) in own) stats[key] = value;
        return [.. stats.Select(s => (s.Key, s.Value))];
    }

    public static int EssenceInsectRadius(ClientState conn, MonsterModEffects? effects = null)
    {
        var (_, stats) = EssenceInsectStats(conn, 1);
        return SkillArea.Radius(EssenceInsectBaseRadius,
            n => (Stats.Key(n) is uint k ? stats.GetValueOrDefault(k) : 0) + (effects?[n] ?? 0), spell: true);
    }
    public sealed record EssenceInsectBlast(InstanceState Instance, (int X, int Y) At, int Radius);
    sealed record EssenceInsectContact(uint Id, SpawnedMonster Monster, MonsterAttack Attack, int Action);
    sealed class EssenceInsectContacts { public readonly List<EssenceInsectContact> Pending = []; }
    static readonly RuleTable<InstanceState, EssenceInsectContacts> InsectContacts = new();

    static void QueueEssenceInsectContactLocked(ClientState conn, uint id, SpawnedMonster insect, MonsterAttack attack, int action)
    {
        insect.BusyUntil = double.PositiveInfinity;
        InsectContacts.GetOrCreateValue(conn.Instance).Pending.Add(new(id, insect, attack, action));
    }

    public static async Task TickEssenceInsectContacts(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        EssenceInsectContact[] pending;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.Paused || !IsMonsterRunner(conn)
                || conn.Instance is not { } instance || !InsectContacts.TryGetValue(instance, out var contacts)
                || contacts.Pending.Count == 0) return;
            pending = [.. contacts.Pending];
            contacts.Pending.Clear();
        }
        foreach (var contact in pending)
            try { await ExplodeEssenceInsect(remote, stream, ciphers, conn, contact.Id, contact.Monster, contact.Attack, contact.Action); }
            finally
            {
                lock (conn.Monsters)
                    if (!contact.Monster.Dead && contact.Monster.ActionGeneration == contact.Action
                        && double.IsPositiveInfinity(contact.Monster.BusyUntil)) contact.Monster.BusyUntil = AreaTime(conn);
            }
    }

    public static EssenceInsectBlast? CommitEssenceInsectExplosionLocked(ClientState conn, uint id, SpawnedMonster insect, int action)
    {
        if (insect.Variety?.Id != EssenceExplosiveInsect || conn.Paused || !MonsterActionCurrent(conn, id, insect, action)
            || MonsterActionFactor(insect, AreaTime(conn)) <= 0) return null;
        var at = insect.Walk.Position(AreaTime(conn));
        insect.Life = 0;
        insect.Dead = true;
        insect.NoRewards = true; // self-destruction has no killer and earns no kill rewards
        insect.ActionGeneration++;
        insect.Walk.Teleport(at.X, at.Y, AreaTime(conn));
        return new(conn.Instance, at, EssenceInsectRadius(conn, insect.Mods.Effects));
    }

    public static bool InEssenceInsectBlast(EssenceInsectBlast blast, (int X, int Y) at) =>
        Math.Pow((double)at.X - blast.At.X, 2) + Math.Pow((double)at.Y - blast.At.Y, 2) <= (double)blast.Radius * blast.Radius;

    static async Task ExplodeEssenceInsect(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster insect, MonsterAttack attack, int action)
    {
        EssenceInsectBlast? blast = null;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, insect, action, () =>
        {
            blast = CommitEssenceInsectExplosionLocked(conn, id, insect, action);
            return blast is null ? null : [.. InstanceProtocol.ActionEnded(id),
                .. InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), .. InstanceProtocol.LifeUpdate(id, 0)];
        }, "Essence explosive insect detonates")) return;
        var accepted = blast!;
        var members = conn.Instance.PlayersExcept(conn.PeerId).Select(p => p.Owner).OfType<ClientState>().Prepend(conn).Distinct().ToArray();
        foreach (var member in members)
            await ArenaHazards.Hit(member, id, insect, attack, at => InEssenceInsectBlast(accepted, at),
                () => CurrentGameWork && ReferenceEquals(member.Instance, accepted.Instance));
        await SendRemainingMonsters(remote, stream, ciphers, conn);
    }
}
