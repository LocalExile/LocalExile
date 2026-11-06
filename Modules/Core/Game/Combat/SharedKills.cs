using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

// A kill of the instance's shared monsters (SharedMonsters.cs) happens on the host's connection, whoever landed the last
// hit. Every other living player in the instance near enough gets their own share of it: the experience for their level
// (and their gems'), the kill's quest flags and a quest kill's level, sent on their own connection by their own loop.
static partial class Game
{
    internal static void ShareKill(ClientState host, uint id, SpawnedMonster m, MonsterCatalog.Variety variety, (int X, int Y) at)
    {
        var presence = host.Instance.Presence;
        if (!host.Instance.World.MonsterOrigins.ContainsKey(id)) return; // a player's minion or ally
        var monster = ModMonster(m, at);
        foreach (var peer in presence.PlayersExcept(host.PeerId))
        {
            if (peer.Owner is not { } other || other.Closed || other.LeavingArea || peer.Life == 0
                || Dist(at, (peer.X, peer.Y)) > ExperienceRange) continue;
            other.KillShares.Enqueue(async (remote, stream, ciphers) =>
            {
                if (HardcoreProgressBlocked(other)) return;
                await SetQuestFlags(remote, stream, ciphers, other, QuestsOf(other).OnMonsterKilled(other.Layout.Id, variety.Id));
                // The kill's quest items: each player gets their own (none for one who has it or is past that quest).
                await DropQuestItemsForKill(remote, stream, ciphers, other, variety.Id, at, $"{m.Label} (party kill)");
                if (!m.NoRewards && Monsters.BaseExperience(variety, m.Level) is int baseXp)
                {
                    uint xp = Program.ServerHooks.Experience(PlayerOf(other), monster,
                        Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, other.Pools.Level, m.Level));
                    await GainGemExperience(remote, stream, ciphers, other, xp);
                    await GainExperience(remote, stream, ciphers, other, xp, $"party kill: {m.Rarity} {m.Label} (by {CharacterName(host)})");
                }
                if (QuestsOf(other).LevelAfterKill(other.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
                {
                    uint needed = CharacterStats.LevelStarts[level - 1], have;
                    lock (other.Monsters) have = other.Experience;
                    if (have < needed) await GainExperience(remote, stream, ciphers, other, needed - have, $"quest: {m.Label} killed, level {level}");
                }
            });
        }
    }

    // An ignite, bleed or poison killed the monster on the runner's tick: the kill is the one's whose hit landed last,
    // on their own connection (their client gets the corpse, their kill effects and rewards; the others their shares).
    internal static async Task DotKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m)
    {
        if (m.LastHitBy is { } by && !ReferenceEquals(by, conn) && !by.Closed && !by.LeavingArea && ReferenceEquals(by.Instance, conn.Instance))
        {
            by.KillShares.Enqueue((r, s, c) => MonsterKilled(r, s, c, by, id, m));
            return;
        }
        await MonsterKilled(remote, stream, ciphers, conn, id, m);
    }

    // To every other client in the instance that has the object (and has it awake), through its own send gate. A
    // client without it cannot read a message about it: an object's component update (0x2ba) is read by the object's
    // components, and a buried Gravel Eater's rise (0x2ba Transitionable) sent to a client it was never created on broke
    // its stream ("Unable to deserialise packet", live 2026-11-05 01:15:27). SyncInstanceMonsters creates it there.
    // Emerge includes known, non-sleeping waiting views before the active-monster snapshot catches up.
    // That path holds the shared Monsters lock, as do the snapshot's MonsterViews writes.
    internal static void ToOthers(ClientState conn, uint id, byte[] bytes, bool includeWaiting = false)
    {
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Owner is { Outbox: { } outbox } other && !other.Closed && !other.LeavingArea
                && (includeWaiting
                    ? other.MonsterViews.ContainsKey(id) && !other.AsleepMonsters.Contains(id)
                    : Array.BinarySearch(other.AwakeMonsters, id) >= 0))
                // Each send encrypts in place, potentially after waiting for its recipient's gate.
                _ = outbox((byte[])bytes.Clone(), null);
    }

    // This player's frame: the shares of the host's kills.
    internal static async Task ApplyKillShares(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        while (conn.KillShares.TryDequeue(out var share)) await share(remote, stream, ciphers);
    }
}
