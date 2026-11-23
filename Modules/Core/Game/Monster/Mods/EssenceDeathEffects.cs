using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string EssenceExplosiveInsect = "Metadata/Monsters/InsectMinion/EssenceInsectWildExplosiveColossal";
    public const string EssenceExplosiveCrawler = "Metadata/Monsters/InsectMinion/EssenceInsectWildExplosive";
    public const uint EssenceInsectDeathStat = Stat.EssenceDeliriumSpawnMonsterOnDeathVariation;
    public const string EssenceInsectDeathMod = "EssenceInsectSummonedOnDeath";
    public const uint SufferingDeathStat = Stat.EssenceSufferingSpawnMonsterOnDeathVariation;
    public const string SufferingArcticGolem = "Metadata/Monsters/RockGolem/EssenceArcticGolemSummoned";
    public const uint RemnantDeathStat = Stat.EssenceCorruptSpawnMonsterOnDeathVariation;
    public const string RemnantFleshmonster = "Metadata/Monsters/Essence/EssenceFleshmonsterSummonedOnDeath";
    const string EssenceInsectEffect = "InsectSuicideExplosionHuge";
    const string EssenceCrawlerEffect = "InsectSuicideExplosionLarge";
    // SuicideExplosion::execute (3.29.3d, 141a462cc): floor(15 * radius_percentage / 100).
    const int EssenceInsectBaseRadius = 15;
    public static bool IsEssenceExplosiveInsect(string? varietyId) =>
        varietyId is EssenceExplosiveInsect or EssenceExplosiveCrawler;
    public static string EssenceInsectEffectFor(string? varietyId) =>
        varietyId == EssenceExplosiveCrawler ? EssenceCrawlerEffect : EssenceInsectEffect;
    public sealed record EssenceDeathDefinition(int Variety, int Count)
    {
        /// <summary>A staged death spawn: the stat and variation the spawned monster carries so its own death brings the
        /// next stage (Anguish's rubble fractures 1st → 2nd → 3rd). Null for a one-stage spawn.</summary>
        public uint? NextStat { get; init; }
        public int NextVariation { get; init; }
    }
    public sealed class EssenceDeathSpawn(InstanceState instance, uint parent, byte level, (int X, int Y) at,
        EssenceDeathDefinition definition)
    {
        public InstanceState Instance { get; } = instance;
        public uint Parent { get; } = parent;
        public byte Level { get; } = level;
        public (int X, int Y) At { get; } = at;
        public EssenceDeathDefinition Definition { get; } = definition;
        internal Player? Claim;
        internal int Created;
    }
    sealed class EssenceDeathQueue { public readonly List<EssenceDeathSpawn> Pending = []; }
    static readonly RuleTable<InstanceState, EssenceDeathQueue> EssenceDeathQueues = new();
    static readonly RuleTable<SpawnedMonster, EssenceDeathSpawn> EssenceDeathReceipts = new();

    /// <summary>The essence on-death stats and the variety each accepts. Anguish's rubble stages chain through
    /// NextStat/NextVariation; Delirium's insect, Suffering's Arctic Golem and Remnant's Fleshmonsters are one stage.</summary>
    static readonly (uint Stat, string Variety)[] EssenceDeathSources =
    [
        (EssenceInsectDeathStat, EssenceExplosiveInsect),
        (SufferingDeathStat, SufferingArcticGolem),
        (RemnantDeathStat, RemnantFleshmonster),
        (Stat.EssenceAnguishSpawnMonsterOnDeathVariation, MapEssences.AnguishRubbleFirst),
        (Stat.EssenceAnguishSpawnMonsterOnDeathVariation, MapEssences.AnguishRubbleSecond),
        (Stat.EssenceAnguishSpawnMonsterOnDeathVariation, MapEssences.AnguishRubbleThird),
    ];

    public static EssenceDeathDefinition? EssenceDeathDefinitionFor(int variation, string? accept = null)
    {
        if (variation <= 0 || ClientTables.Table("SummonedSpecificMonstersOnDeath").Row(variation - 1) is not { } row
            || row.IntOrNull("MonsterVarietiesKey") is not int variety || (accept is not null && Monsters.Find(variety.ToString())?.Id != accept))
            return null;
        var definition = new EssenceDeathDefinition(variety, Math.Max(1, row.Int("__col4")));
        return accept == MapEssences.AnguishRubbleFirst
            ? definition with { NextStat = Stat.EssenceAnguishSpawnMonsterOnDeathVariation, NextVariation = 7 }
            : accept == MapEssences.AnguishRubbleSecond
                ? definition with { NextStat = Stat.EssenceAnguishSpawnMonsterOnDeathVariation, NextVariation = 8 }
                : definition;
    }

    public static bool QueueEssenceDeathSpawnLocked(Player player, uint id, SpawnedMonster source)
    {
        if (!CurrentGameWork || !source.Hostile || !source.Dead && source.Life > 0 || player.Instance is not { } instance
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), source)
            || EssenceDeathReceipts.TryGetValue(source, out _)) return false;
        // A multi-essence rare can carry more than one on-death summon (e.g. Suffering + Anguish + Delirium); queue
        // every matching source while recording one receipt so the same death is never queued twice.
        List<EssenceDeathDefinition>? definitions = null;
        foreach (var (stat, accept) in EssenceDeathSources)
            if (source.Effects[stat] is > 0 and var v && EssenceDeathDefinitionFor(v, accept) is { } d)
                (definitions ??= []).Add(d);
        if (definitions is null) return false;
        var at = source.Walk.Position(AreaTime(player));
        var queue = EssenceDeathQueues.GetOrCreateValue(instance).Pending;
        EssenceDeathSpawn? first = null;
        foreach (var definition in definitions)
        {
            var spawn = new EssenceDeathSpawn(instance, id, source.Level, at, definition);
            first ??= spawn;
            queue.Add(spawn);
        }
        EssenceDeathReceipts.Add(source, first!);
        return true;
    }

    // The instance keeps an accepted death if its killer leaves before the native create. A runner claims it only
    // until that connection leaves or loses the world; birth and acknowledgement share SpawnMonster's send gate.
    public static IReadOnlyList<EssenceDeathSpawn> ClaimEssenceDeathSpawnsLocked(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || !IsMonsterRunner(player)
            || player.Instance is not { } instance || !EssenceDeathQueues.TryGetValue(instance, out var queue)) return [];
        List<EssenceDeathSpawn> result = [];
        foreach (var spawn in queue.Pending)
        {
            if (spawn.Claim is { Closed: false, LeavingArea: false } claim && ReferenceEquals(claim.Instance, spawn.Instance)
                && IsMonsterRunner(claim)) continue;
            spawn.Claim = player;
            result.Add(spawn);
        }
        return result;
    }

    public static bool EssenceDeathSpawnCurrentLocked(Player player, EssenceDeathSpawn spawn) =>
        CurrentGameWork && !player.Closed && !player.LeavingArea && ReferenceEquals(spawn.Instance, player.Instance)
        && IsMonsterRunner(player) && ReferenceEquals(spawn.Claim, player) && spawn.Created < spawn.Definition.Count;

    public static void CompleteEssenceDeathSpawnLocked(Player player, EssenceDeathSpawn spawn, bool admitted)
    {
        if (!ReferenceEquals(spawn.Claim, player) || spawn.Created >= spawn.Definition.Count) return;
        if (admitted && ++spawn.Created >= spawn.Definition.Count && EssenceDeathQueues.TryGetValue(spawn.Instance, out var queue))
            queue.Pending.Remove(spawn);
        if (!admitted) spawn.Claim = null;
    }

    public static async Task TickEssenceDeathSpawns(Player player)
    {
        IReadOnlyList<EssenceDeathSpawn> due = ClaimEssenceDeathSpawnsLocked(player);
        foreach (var spawn in due)
        {
            try
            {
                if (Monsters.Find(spawn.Definition.Variety.ToString()) is not { Supported: true } variety) continue;
                var deathKit = IsEssenceExplosiveInsect(variety.Id) ? null : BossKits.SummonKit(player, variety, spawn.Level);
                while (true)
                {
                    if (!EssenceDeathSpawnCurrentLocked(player, spawn)) break;
                    uint created = await SpawnMonster(player, variety, spawn.Level, MonsterRarity.Normal,
                        spawn.At.X, spawn.At.Y, alone: true, unrisen: false, extraKit: deathKit,
                        ownedAdmission: () => EssenceDeathSpawnCurrentLocked(player, spawn),
                        owned: (_, child) =>
                        {
                            child.SummonerId = spawn.Parent; // not linked to the already-dead parent's Life
                            child.Aggro = true;
                            if (spawn.Definition.NextStat is { } next && spawn.Definition.NextVariation > 0)
                                child.EncounterStats = new Dictionary<uint, int> { [next] = spawn.Definition.NextVariation };
                            CompleteEssenceDeathSpawnLocked(player, spawn, true);
                        });
                    if (created == 0) break;
                    Console.WriteLine($"[map-content] essence death source=0x{spawn.Parent:x} spawn=0x{created:x}: {variety.Id}");
                }
            }
            finally
            {
                CompleteEssenceDeathSpawnLocked(player, spawn, false);
            }
        }
    }

    static (int Effect, Dictionary<uint, int> Stats) EssenceInsectStats(Player player, byte level, string effectId = EssenceInsectEffect)
    {
        int effect = SkillsFor(player).EffectRow(effectId);
        return (effect, GolemData.Skill(SkillsFor(player), effect, level).Stats);
    }

    public static MonsterAttack EssenceInsectAttack(Player player, byte level, ushort skillId, MonsterModEffects mods,
        string effectId = EssenceInsectEffect)
    {
        var (effect, stats) = EssenceInsectStats(player, level, effectId);
        int Get(uint k) => stats.GetValueOrDefault(k);
        var damage = DamagePacket.Of(DamageType.Fire, Get(Stat.SecondaryMinimumBaseFireDamage), Get(Stat.SecondaryMaximumBaseFireDamage));
        double seconds = mods.CastSeconds(SkillsFor(player).CastTimeMs(effect) / 1000d);
        double reach = 6 * Math.Max(0, 1 + Get(Stat.SpellMaximumActionDistancePlusPct) / 100d);
        return new(skillId, seconds, reach, mods.Hit(damage)) { Spell = true, Secondary = true };
    }

    public static IReadOnlyList<(uint, int)> EssenceInsectSkillStats(Player player, MonsterAttack attack,
        IReadOnlyList<(uint, int)> own, MonsterModEffects? effects = null, string effectId = EssenceInsectEffect)
    {
        var (_, raw) = EssenceInsectStats(player, 1, effectId);
        var stats = new Dictionary<uint, int>(raw);
        int Get(uint k) => stats.GetValueOrDefault(k) + (effects?[k] ?? 0);
        stats[Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal] = SkillArea.RadiusPercent(Get, spell: true);
        stats[Stat.SecondaryMinimumBaseFireDamage] = (int)Math.Floor(attack.Damage[DamageType.Fire].Min);
        stats[Stat.SecondaryMaximumBaseFireDamage] = (int)Math.Floor(attack.Damage[DamageType.Fire].Max);
        foreach (var (key, value) in own) stats[key] = value;
        return [.. stats.Select(s => (s.Key, s.Value))];
    }

    public static int EssenceInsectRadius(Player player, MonsterModEffects? effects = null, string effectId = EssenceInsectEffect)
    {
        var (_, stats) = EssenceInsectStats(player, 1, effectId);
        return SkillArea.Radius(EssenceInsectBaseRadius,
            k => stats.GetValueOrDefault(k) + (effects?[k] ?? 0), spell: true);
    }
    public sealed record EssenceInsectBlast(InstanceState Instance, (int X, int Y) At, int Radius);
    sealed record EssenceInsectContact(uint Id, SpawnedMonster Monster, MonsterAttack Attack, int Action);
    sealed class EssenceInsectContacts { public readonly List<EssenceInsectContact> Pending = []; }
    static readonly RuleTable<InstanceState, EssenceInsectContacts> InsectContacts = new();

    static void QueueEssenceInsectContactLocked(Player player, uint id, SpawnedMonster insect, MonsterAttack attack, int action)
    {
        insect.BusyUntil = double.PositiveInfinity;
        InsectContacts.GetOrCreateValue(player.Instance).Pending.Add(new(id, insect, attack, action));
    }

    public static async Task TickEssenceInsectContacts(Player player)
    {
        if (player.Closed || player.LeavingArea || player.Paused || !IsMonsterRunner(player)
            || player.Instance is not { } instance || !InsectContacts.TryGetValue(instance, out var contacts)
            || contacts.Pending.Count == 0) return;
        EssenceInsectContact[] pending = [.. contacts.Pending];
        contacts.Pending.Clear();
        foreach (var contact in pending)
            try { await ExplodeEssenceInsect(player, contact.Id, contact.Monster, contact.Attack, contact.Action); }
            finally
            {
                if (!contact.Monster.Dead && contact.Monster.ActionGeneration == contact.Action
                    && double.IsPositiveInfinity(contact.Monster.BusyUntil)) contact.Monster.BusyUntil = AreaTime(player);
            }
    }

    public static EssenceInsectBlast? CommitEssenceInsectExplosionLocked(Player player, uint id, SpawnedMonster insect, int action)
    {
        if (!IsEssenceExplosiveInsect(insect.Variety?.Id) || player.Paused || !MonsterActionCurrent(player, id, insect, action)
            || MonsterActionFactor(insect, AreaTime(player)) <= 0) return null;
        var at = insect.Walk.Position(AreaTime(player));
        insect.Life = 0;
        insect.Dead = true;
        insect.NoRewards = true; // self-destruction has no killer and earns no kill rewards
        insect.ActionGeneration++;
        insect.Walk.Teleport(at.X, at.Y, AreaTime(player));
        return new(player.Instance, at, EssenceInsectRadius(player, insect.Mods.Effects, EssenceInsectEffectFor(insect.Variety?.Id)));
    }

    public static bool InEssenceInsectBlast(EssenceInsectBlast blast, (int X, int Y) at) =>
        Math.Pow((double)at.X - blast.At.X, 2) + Math.Pow((double)at.Y - blast.At.Y, 2) <= (double)blast.Radius * blast.Radius;

    static async Task ExplodeEssenceInsect(Player player,
        uint id, SpawnedMonster insect, MonsterAttack attack, int action)
    {
        EssenceInsectBlast? blast = null;
        if (!await SendMonsterAction(player, id, insect, action, () =>
        {
            blast = CommitEssenceInsectExplosionLocked(player, id, insect, action);
            return blast is null ? null : [.. InstanceProtocol.ActionEnded(id),
                .. InstanceEntities.StatsUpdate(id, [(Stat.HideMiniLifeBar, 1)]), .. InstanceProtocol.LifeUpdate(id, 0)];
        }, "Essence explosive insect detonates")) return;
        var accepted = blast!;
        var members = player.Instance.PlayersExcept(player.Id).Prepend(player).Distinct().ToArray();
        foreach (var member in members)
            await ArenaHazards.Hit(member, id, insect, attack, at => InEssenceInsectBlast(accepted, at),
                () => CurrentGameWork && ReferenceEquals(member.Instance, accepted.Instance));
        await SendRemainingMonsters(player);
    }
}
