using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

// A kill of the instance's shared monsters (SharedMonsters.cs) happens on the host's connection, whoever landed the last
// hit. Every other living player in the instance near enough gets their own share of it: the experience for their level
// (and their gems'), the kill's quest flags and a quest kill's level, sent on their own connection by their own loop.
public static partial class Game
{
    /// <summary>One member's share of a party kill's experience (<see cref="Progression.PartyMultiplier"/>).</summary>
    public sealed record PartyExperienceShare(Player Player, double Multiplier);

    /// <summary>
    /// The living players in the instance near enough to the kill (the presence: a private area is entered as the
    /// party's instance) and their shares of its experience. A dead, leaving or too distant player takes no share, as
    /// the game's experience range does. The wiki's Experience page: the monster's experience is multiplied by the
    /// party size and split by (level + 10)^2.71, capped at each player's solo experience.
    /// </summary>
    public static IReadOnlyList<PartyExperienceShare> PartyExperienceShares(Player host, (int X, int Y) at)
    {
        var members = new List<Player>();
        if (host.PlayerLife > 0 && Dist(at, host.Walk.Position(AreaTime(host))) <= ExperienceRange)
            members.Add(host);
        var seen = new HashSet<Player> { host };
        foreach (var peer in host.Instance?.Presence.PlayersExcept(host.Id) ?? [])
        {
            if (peer is not { } other || !seen.Add(other) || other.Closed || other.LeavingArea || !other.Connection.PlayerSent
                || peer.PlayerLife == 0 || Dist(at, peer.Walk.Position(AreaTime(peer))) > ExperienceRange) continue;
            members.Add(other);
        }
        if (members.Count == 0) return [];
        if (members.Count == 1) return [new(members[0], 1)];
        var levels = members.Select(m => m.Pools.Level).ToList();
        return [.. members.Select((m, i) => new PartyExperienceShare(m, Progression.PartyMultiplier(levels, i)))];
    }

    public static double PartyExperienceShareOf(IReadOnlyList<PartyExperienceShare> shares, Player player) =>
        shares.FirstOrDefault(s => ReferenceEquals(s.Player, player))?.Multiplier ?? 1;

    public static void ShareKill(Player host, uint id, SpawnedMonster m, MonsterCatalog.Variety variety,
        (int X, int Y) at, IReadOnlyList<PartyExperienceShare> shares)
    {
        if (!host.Instance.World.MonsterOrigins.ContainsKey(id)) return; // a player's minion or ally
        var monster = ModMonster(m, at);
        foreach (var share in shares)
        {
            if (ReferenceEquals(share.Player, host)) continue;
            var other = share.Player;
            other.KillShares.Enqueue(async _ =>
            {
                if (HardcoreProgressBlocked(other)) return;
                await SetQuestFlags(other, QuestFlagsOnKill(other, variety.Id));
                await CharacterVoiceLines.GrantKillFlags(other, other.Layout.Id, variety.Id);
                // The kill's quest items: each player gets their own (none for one who has it or is past that quest).
                await DropQuestItemsForKill(other, variety.Id, at, $"{m.Label} (party kill)");
                if (!MonsterGivesNoRewards(m))
                {
                    var (xp, gemXp) = ComputeKillExperience(other, m, variety, monster, share.Multiplier);
                    if (gemXp > 0) await GainGemExperience(other, gemXp);
                    if (xp > 0) await GainExperience(other, xp, $"party kill: {m.Rarity} {m.Label} (by {CharacterName(host)})");
                    // Retail party parity: every living party member within kill range receives kill flask charges.
                    await GainKillCharges(other, m.Rarity, $"party kill: {m.Rarity} {m.Label}");
                }
                if (QuestsOf(other).LevelAfterKill(other.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
                {
                    uint needed = CharacterStats.LevelStarts[level - 1], have;
                    have = other.Character.Experience;
                    if (have < needed) await GainExperience(other, needed - have, $"quest: {m.Label} killed, level {level}");
                }
            });
        }
    }

    // The 0x1b1 death kind of a monster's life after damage over time: DeathFromDegen at life 0.
    // shortcut: a variety whose DeathFromDegen clip frees its body later than its Death clip (five Faridun Flesh Hulks,
    // 166 against 150 ms) keeps the plain death, since the corpse is timed by the Death clip (KeepCorpseLocked); send
    // the kind for them too once the corpse takes the time of the clip that plays.
    public static byte DotDeathKind(SpawnedMonster m, uint life) =>
        life == 0 && !(m.Variety is { } variety && Monsters.DegenDeathClipDiffers(variety.Id))
            ? InstanceProtocol.DeathKindDegen : InstanceProtocol.DeathKindNormal;

    // An ignite, bleed or poison killed the monster on the runner's tick: the kill is the one's whose hit landed last,
    // on their own connection (their client gets the corpse, their kill effects and rewards; the others their shares).
    public static async Task DotKilled(Player player, uint id,
        SpawnedMonster m)
    {
        if (m.LastHitBy is { } by && !ReferenceEquals(by, player) && !by.Closed && !by.LeavingArea && ReferenceEquals(by.Instance, player.Instance))
        {
            by.KillShares.Enqueue(_ => MonsterKilled(by, id, m, byHit: false));
            return;
        }
        await MonsterKilled(player, id, m, byHit: false);
    }

    // To every other client in the instance that has the object (and has it awake), through its own send gate. A
    // client without it cannot read a message about it: an object's component update (0x2bc) is read by the object's
    // components, and a buried Gravel Eater's rise (0x2bc Transitionable) sent to a client it was never created on broke
    // its stream ("Unable to deserialise packet", live 2026-11-05 01:15:27). SyncInstanceMonsters creates it there.
    public static void ToOthers(Player player, uint id, byte[] bytes)
    {
        // In the area's frame the kill's records are the area's already (AreaDelta): every client has them.
        if (player.Area.Owned) return;
        foreach (var peer in player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
            if (peer is { Connection.Outbox: { } outbox } other && !other.Closed && !other.LeavingArea)
                // Each send encrypts in place, potentially after waiting for its recipient's gate.
                _ = outbox((byte[])bytes.Clone(), null);
    }

    // This player's frame: the shares of the host's kills.
    public static async Task ApplyKillShares(Player player)
    {
        while (player.KillShares.TryDequeue(out var share)) await share(player);
    }
}
