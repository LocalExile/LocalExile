using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

// A kill of the instance's shared monsters (SharedMonsters.cs) happens on the host's connection, whoever landed the last
// hit. Every other living player in the instance near enough gets their own share of it: the experience for their level
// (and their gems'), the kill's quest flags and a quest kill's level, sent on their own connection by their own loop.
public static partial class Game
{
    /// <summary>One member's share of a party kill's experience (<see cref="Progression.PartyMultiplier"/>).</summary>
    public sealed record PartyExperienceShare(ClientState Conn, double Multiplier);

    /// <summary>
    /// The living players in the instance near enough to the kill (the presence: a private area is entered as the
    /// party's instance) and their shares of its experience. A dead, leaving or too distant player takes no share, as
    /// the game's experience range does. The wiki's Experience page: the monster's experience is multiplied by the
    /// party size and split by (level + 10)^2.71, capped at each player's solo experience.
    /// </summary>
    public static IReadOnlyList<PartyExperienceShare> PartyExperienceShares(ClientState host, (int X, int Y) at)
    {
        var members = new List<ClientState>();
        if (host.PlayerLife > 0 && Dist(at, host.Walk.Position(host.Clock.Elapsed.TotalSeconds)) <= ExperienceRange)
            members.Add(host);
        var seen = new HashSet<ClientState> { host };
        foreach (var peer in host.Instance?.Presence.PlayersExcept(host.PeerId) ?? [])
        {
            if (peer.Owner is not { } other || !seen.Add(other) || other.Closed || other.LeavingArea || !other.PlayerSent
                || peer.Life == 0 || Dist(at, (peer.X, peer.Y)) > ExperienceRange) continue;
            members.Add(other);
        }
        if (members.Count == 0) return [];
        if (members.Count == 1) return [new(members[0], 1)];
        var levels = members.Select(m => m.Pools.Level).ToList();
        return [.. members.Select((m, i) => new PartyExperienceShare(m, Progression.PartyMultiplier(levels, i)))];
    }

    public static double PartyExperienceShareOf(IReadOnlyList<PartyExperienceShare> shares, ClientState conn) =>
        shares.FirstOrDefault(s => ReferenceEquals(s.Conn, conn))?.Multiplier ?? 1;

    public static void ShareKill(ClientState host, uint id, SpawnedMonster m, MonsterCatalog.Variety variety,
        (int X, int Y) at, IReadOnlyList<PartyExperienceShare> shares)
    {
        if (!host.Instance.World.MonsterOrigins.ContainsKey(id)) return; // a player's minion or ally
        var monster = ModMonster(m, at);
        foreach (var share in shares)
        {
            if (ReferenceEquals(share.Conn, host)) continue;
            var other = share.Conn;
            other.KillShares.Enqueue(async (remote, stream, ciphers) =>
            {
                if (HardcoreProgressBlocked(other)) return;
                await SetQuestFlags(remote, stream, ciphers, other, QuestsOf(other).OnMonsterKilled(other.Layout.Id, variety.Id));
                // The kill's quest items: each player gets their own (none for one who has it or is past that quest).
                await DropQuestItemsForKill(remote, stream, ciphers, other, variety.Id, at, $"{m.Label} (party kill)");
                if (!m.NoRewards && Monsters.BaseExperience(variety, m.Level) is int baseXp)
                {
                    var (xp, gemXp) = KillExperience(PlayerOf(other), monster, baseXp, variety.ExperienceMultiplier, m.Rarity,
                        other.Pools.Level, m.Level, share.Multiplier);
                    await GainGemExperience(remote, stream, ciphers, other, gemXp);
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
    public static async Task DotKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m)
    {
        if (m.LastHitBy is { } by && !ReferenceEquals(by, conn) && !by.Closed && !by.LeavingArea && ReferenceEquals(by.Instance, conn.Instance))
        {
            by.KillShares.Enqueue((r, s, c) => MonsterKilled(r, s, c, by, id, m, byHit: false));
            return;
        }
        await MonsterKilled(remote, stream, ciphers, conn, id, m, byHit: false);
    }

    // To every other client in the instance that has the object (and has it awake), through its own send gate. A
    // client without it cannot read a message about it: an object's component update (0x2bc) is read by the object's
    // components, and a buried Gravel Eater's rise (0x2bc Transitionable) sent to a client it was never created on broke
    // its stream ("Unable to deserialise packet", live 2026-11-05 01:15:27). SyncInstanceMonsters creates it there.
    public static void ToOthers(ClientState conn, uint id, byte[] bytes)
    {
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Owner is { Outbox: { } outbox } other && !other.Closed && !other.LeavingArea
                && Array.BinarySearch(other.AwakeMonsters, id) >= 0)
                // Each send encrypts in place, potentially after waiting for its recipient's gate.
                _ = outbox((byte[])bytes.Clone(), null);
    }

    // This player's frame: the shares of the host's kills.
    public static async Task ApplyKillShares(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        while (conn.KillShares.TryDequeue(out var share)) await share(remote, stream, ciphers);
    }
}
