using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The buffs an Allflame encounter's opener gets (the wiki: "until the character dies or leaves the area"; a
/// re-entry grants none again, as the chest stays opened): Craiceann's Call (Coral Nest, BuffDefinitions 2735, stat 23062:
/// a rare or unique kill brings a DeepwaterCrabBuffPack), Sailor's Lantern (Brinerot Plunder, 2733, stat 23047: a kill's
/// gold has a chance to be duplicated) and a Possession (Cursed Treasure: a random Grip, the Game module's
/// <see cref="MapTorment.PossessPlayer"/>). The chances are world.db's <c>deepwater_craiceann_chance</c> and
/// <c>deepwater_sailors_lantern_chance</c> (G); the data's CancelOnDeath is 0, so ending them at death is the server's (G).
/// Per kill and per stat build: nothing but a counter read while nobody holds one, else one table lookup.</summary>
internal static class DeepwaterBuffs
{
    public const ushort CraiceannsCall = 2735, SailorsLantern = 2733;
    const string CraiceannStat = "chance_%_to_spawn_crab_pack_on_kill_vs_rare_or_unique", SailorStat = "%_chance_to_duplicate_dropped_gold";
    const string CrabPack = "DeepwaterCrabBuffPack";

    sealed class Held
    {
        /// <summary>The buff instances, 0 when not held.</summary>
        public ushort Craiceann, Sailor;
        /// <summary>Where a crab pack is due (the corpses), spawned on the next tick, off the kill path.</summary>
        public readonly List<(int X, int Y)> Crabs = [];
    }

    static readonly ConditionalWeakTable<ClientState, Held> Holders = new();
    // How many buffs are held on the server: the per-kill and per-stat-build hooks return at once while none is.
    static int _held;

    internal static void ClearCache()
    {
        Holders.Clear();
        Volatile.Write(ref _held, 0);
    }

    static int Setting(string key) => DeepwaterLoot.Setting(key);

    /// <summary>An encounter's opener gets its buff (under the encounter gate, once per chest).</summary>
    public static async Task Grant(IModuleContext module, ClientState conn, DeepwaterEncounters.EncounterKind kind)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
        if (kind == DeepwaterEncounters.EncounterKind.CursedTreasure)
        {
            // Until death or leaving the area (G: no duration in the data; the Game module removes it at life 0 and on leaving).
            int? row = MapTorment.PossessPlayer(conn, null, double.PositiveInfinity);
            module.Log($"{CharacterName(conn)}: Cursed Treasure possession {(row is int r ? $"by spirit row {r}" : "not given (none fits or already held)")}");
            return;
        }
        bool coral = kind == DeepwaterEncounters.EncounterKind.CoralNest;
        var held = Holders.GetValue(conn, _ => new());
        ushort buff;
        lock (conn.Monsters)
        {
            lock (held)
            {
                if ((coral ? held.Craiceann : held.Sailor) != 0) return;
                buff = conn.NextBuff++;
                if (buff == 0) buff = conn.NextBuff++;
                if (coral) held.Craiceann = buff; else held.Sailor = buff;
                Interlocked.Increment(ref _held);
            }
        }
        string name = coral ? "Craiceann's Call" : "Sailor's Lantern";
        await global::Game.SendWorldRecord(PlayerOf(conn), PlayerId, InstanceEntities.AddBuff(PlayerId, buff, coral ? CraiceannsCall : SailorsLantern),
            $"0x01c1 {name} ({kind} opened)");
        module.Log($"{CharacterName(conn)}: {name} granted");
        await SendStats(conn, name);
    }

    /// <summary>Each tick: at life 0 the buffs end; otherwise spawn any queued crab packs.</summary>
    public static async Task Tick(IModuleContext module, ClientState conn)
    {
        if (Volatile.Read(ref _held) == 0 || !Holders.TryGetValue(conn, out var held) || conn.Closed || conn.LeavingArea) return;
        if (conn.PlayerLife == 0)
        {
            var ended = Take(held);
            if (ended.Count == 0) return;
            var player = PlayerOf(conn);
            foreach (ushort buff in ended)
                await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), "0x01c2 Allflame encounter buff ends (died)");
            module.Log($"{CharacterName(conn)}: Allflame encounter buffs end (died)");
            await SendStats(conn, "died");
            return;
        }
        (int X, int Y)[] crabs;
        lock (held)
        {
            crabs = held.Crabs.Count == 0 ? [] : [.. held.Crabs];
            held.Crabs.Clear();
        }
        foreach (var at in crabs) await SpawnCrabs(module, conn, at);
    }

    /// <summary>The player left the area: its buffs went with it (the next area does not show them again).</summary>
    public static void Forget(ClientState conn)
    {
        if (Volatile.Read(ref _held) == 0 || !Holders.TryGetValue(conn, out var held)) return;
        Take(held);
        Holders.Remove(conn);
    }

    static List<ushort> Take(Held held)
    {
        var ended = new List<ushort>(2);
        lock (held)
        {
            if (held.Craiceann != 0) ended.Add(held.Craiceann);
            if (held.Sailor != 0) ended.Add(held.Sailor);
            held.Craiceann = held.Sailor = 0;
            held.Crabs.Clear();
        }
        if (ended.Count > 0) Interlocked.Add(ref _held, -ended.Count);
        return ended;
    }

    /// <summary>PlayerStatSources: the held buffs' stats (the chances), so the sheet shows them.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> sources)
    {
        if (Volatile.Read(ref _held) == 0 || !Holders.TryGetValue(conn, out var held)) return sources;
        if (held.Craiceann != 0 && Stats.Key(CraiceannStat) is uint c) sources[c] = sources.GetValueOrDefault(c) + Setting("deepwater_craiceann_chance");
        if (held.Sailor != 0 && Stats.Key(SailorStat) is uint s) sources[s] = sources.GetValueOrDefault(s) + Setting("deepwater_sailors_lantern_chance");
        return sources;
    }

    /// <summary>GameEvents.MonsterKilled: a rare or unique killed by a Craiceann's Call holder queues a crab pack at the
    /// corpse (at <c>deepwater_craiceann_chance</c> percent, G 100).</summary>
    public static Task Killed(MonsterKill kill)
    {
        if (kill.Monster.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique) || Volatile.Read(ref _held) == 0
            || !Holders.TryGetValue(kill.Conn, out var held) || held.Craiceann == 0) return Task.CompletedTask;
        if (Random.Shared.Next(100) < Setting("deepwater_craiceann_chance"))
            lock (held) if (held.Craiceann != 0) held.Crabs.Add((kill.Monster.X, kill.Monster.Y));
        return Task.CompletedTask;
    }

    /// <summary>GameEvents.Drops: a Sailor's Lantern holder's kill drops twice the gold at
    /// <c>deepwater_sailors_lantern_chance</c> percent (G 25; "Gold dropped from slain enemies has a chance to be duplicated").</summary>
    public static void Drops(IModuleContext module, DropRoll roll)
    {
        if (Volatile.Read(ref _held) == 0 || !Holders.TryGetValue(roll.Conn, out var held) || held.Sailor == 0) return;
        if (Random.Shared.Next(100) >= Setting("deepwater_sailors_lantern_chance")) return;
        roll.Gold *= 2;
        module.Log($"{CharacterName(roll.Conn)}: Sailor's Lantern duplicates the gold of {roll.Monster.Name}");
    }

    // MonsterPacks 3160 (3-8 of CoconutCrabEncounter / NimbleCrabEncounter) at the corpse: normal, the area level,
    // aggressive (G: the data names only the pack).
    static async Task SpawnCrabs(IModuleContext module, ClientState conn, (int X, int Y) at)
    {
        if (Packs.Find(CrabPack) is not { } pack) return;
        var layout = conn.Layout;
        var members = AreaPopulation.AtPacks([(pack, at.X, at.Y, MonsterRarity.Normal)], (x, y) => layout.WalkableFor(x, y, 1),
            v => Monsters.Find(v.ToString()) is { Supported: true }, Random.Shared.Next());
        byte level = (byte)DeepwaterEncounters.AreaLevel(conn);
        var (remote, stream, ciphers) = conn.Wire;
        int spawned = 0;
        foreach (var m in members)
        {
            if (conn.Closed || conn.LeavingArea) break;
            if (Monsters.Find(m.Variety.ToString()) is not { Supported: true } variety) continue;
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, m.Rarity, m.X, m.Y, log: false,
                unrisen: false, alone: false, owned: (_, monster) => monster.Aggro = true);
            if (id != 0) spawned++;
        }
        module.Log($"{CharacterName(conn)}: Craiceann's Call brings {spawned} crabs at ({at.X}, {at.Y})");
    }

    static async Task SendStats(ClientState conn, string why)
    {
        lock (conn.Monsters)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        var w = conn.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Allflame buff: {why})");
    }
}
