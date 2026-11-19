using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

// In a solo encounter the leader waits for the native dialogue and vote. Allegiance belongs to the
// character, while a challenge belongs to the instance.
public static class BanditChoices
{
    public sealed record Bandit(string Area, string Variety, string Name, string Amulet, string Kind, int Quest, byte VoteType, int HandInIndex)
    {
        public string Killed { get; } = $"a2b-Killed{Kind}Bandit";
        public string Ally { get; } = $"a2b-{Kind}Ally";
        public string Started { get; } = $"a2q{Quest}-ReceivedQuest";
        public string Reward { get; } = $"a2q{Quest}-QuestRewardTaken";
    }

    public static readonly Bandit Kraityn = new("1_2_4", "Metadata/Monsters/BanditLeaderDexInt/BanditLeaderKraityn", "Kraityn", "DexAmulet", "Dex", 1, 2, 0);
    public static readonly Bandit Alira = new("1_2_9", "Metadata/Monsters/BanditLeaderInt/BanditLeaderAlira", "Alira", "IntAmulet", "Int", 2, 0, 2);
    public static readonly Bandit Oak = new("1_2_12", "Metadata/Monsters/BanditLeaderStrDex/BanditLeaderOak", "Oak", "StrAmulet", "Str", 3, 1, 2);

    const string AmuletPath = "Metadata/Items/QuestItems/BanditAmulets/";
    const string EramirReward = "a2q7-QuestRewardTaken";
    const string Delivered = "a2b-DeliveredAmulets";
    const string SeenApex = "a2b-SeenApexReward";
    const string Fight = "QuestObjects.BanditFight";
    const int ChoiceRange = 16;
    const string AfterFight = "AfterBanditFight";
    sealed class Vote { public uint Leader; }
    static readonly ConditionalWeakTable<ClientState, Vote> Votes = new();
    public static readonly IReadOnlyList<Bandit> All = [Kraityn, Alira, Oak];

    public static string AmuletId(Bandit bandit) => AmuletPath + bandit.Amulet;

    public static Bandit? In(ClientState conn) => conn.Layout.Id switch
    {
        "1_2_4" => Kraityn,
        "1_2_9" => Alira,
        "1_2_12" => Oak,
        _ => null,
    };

    static bool Solo(ClientState conn) => conn.PartySize == 1
        && !conn.Instance.Presence.PlayersExcept(conn.PeerId).Any(p => p.Owner is { Closed: false });

    static bool Finished(Func<string, bool> has) => has(Delivered) || has(EramirReward) || All.Any(b => has(b.Reward));

    static bool Available(ClientState conn, Bandit bandit) =>
        QuestsOf(conn).OnMonsterKilled(bandit.Area, bandit.Variety).Contains(bandit.Killed);

    public static bool Held(ClientState conn, Bandit bandit) =>
        Available(conn, bandit) && Solo(conn) && Npcs.Value.Find(bandit.Variety) is not null && !conn.HasQuestFlag(bandit.Killed)
        && (conn.HasQuestFlag(bandit.Reward) || !Finished(conn.HasQuestFlag) && !conn.Instance.HasState(Fight));

    // This session flag is needed by NPCTalk's hand-in rows. Project it only in the allied
    // leader's area so it does not suppress either rival's first conversation.
    public static void AddDialogueFlags(ClientState conn, HashSet<string> flags)
    {
        if (In(conn) is { } bandit && flags.Contains(bandit.Ally)) flags.Add(AfterFight);
    }

    static SpawnedMonster? Leader(ClientState conn, Bandit bandit, uint id)
    {
        var leader = conn.Monsters.GetValueOrDefault(id);
        return leader is { Hostile: true, Dead: false, Life: > 0, Aggro: false } && leader.Variety?.Id == bandit.Variety
            && Held(conn, bandit) ? leader : null;
    }

    static SpawnedMonster? NearbyLeader(ClientState conn, Bandit bandit, uint id)
    {
        if (conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0 || HardcoreProgressBlocked(conn))
            return null;
        double now = ProjectileWorldTime(conn);
        var leader = Leader(conn, bandit, id);
        return leader is not null && Dist(conn.Walk.Position(now), leader.Walk.Position(now)) <= ChoiceRange + leader.Walk.Size
            ? leader : null;
    }

    public static SpawnedNpc? Speaker(ClientState conn, uint id)
    {
        if (In(conn) is not { } bandit || Npcs.Value.Find(bandit.Variety) is not { } npc) return null;
        if (!conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) || !origin.Object.HasDialogue) return null;
        lock (conn.Monsters)
        {
            if (Leader(conn, bandit, id) is not { } leader) return null;
            var (x, y) = leader.Walk.Position(ProjectileWorldTime(conn));
            lock (conn.Npcs)
            {
                if (!conn.Npcs.TryGetValue(id, out var speaker)) conn.Npcs[id] = speaker = new(npc, x, y);
                return speaker;
            }
        }
    }

    static bool CallsVote(NpcCatalog.Talk talk) => talk.Script.Contains("BanditCallVote", StringComparison.Ordinal);

    // Intercept these rows before the generic NPCTalk engine applies their flags. The amulets and reward
    // must be handled by one inventory transaction, and the vote's temporary flags must not enter the save.
    public static async Task<bool> ChooseDialogue(ClientState conn, uint id, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (In(conn) is not { } bandit || npc.Npc.Id != bandit.Variety || !(CallsVote(talk) || talk.Index == bandit.HandInIndex))
            return false;
        bool allowed;
        lock (conn.Monsters)
            allowed = NearbyLeader(conn, bandit, id) is not null && !Finished(conn.HasQuestFlag);
        if (!allowed || !talk.Available(Quests.Names(PlayerQuestFlags(conn)))) return true;
        if (talk.Index == bandit.HandInIndex)
        {
            await HandIn(conn, id, bandit, talk);
            return true;
        }

        if (talk.TextAudio.Count == 0)
            return await OpenVote(conn, id, npc, talk);

        conn.InNpcTalk = true;
        conn.NpcTalkScript = talk;
        conn.NpcFinishFlag = null;
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name}'s rivals");
        return true;
    }

    public static async Task<bool> OpenVote(ClientState conn, uint id, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (In(conn) is not { } bandit || npc.Npc.Id != bandit.Variety || !CallsVote(talk)) return false;
        bool allowed;
        lock (conn.Monsters)
            allowed = NearbyLeader(conn, bandit, id) is not null && !Finished(conn.HasQuestFlag);
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        conn.NpcTalkScript = null;
        conn.NpcFinishFlag = null;
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), $"0x0084 close {bandit.Name}'s dialogue");
        if (allowed)
        {
            Votes.GetOrCreateValue(conn).Leader = id;
            await Send(remote, stream, ciphers, InstanceNpcs.OpenVote(bandit.VoteType), $"0x00be {bandit.Name} choice");
        }
        return true;
    }

    public static async Task VoteAnswer(ClientState conn, bool help)
    {
        if (In(conn) is not { } bandit || !Votes.TryGetValue(conn, out var vote)) return;
        uint id = vote.Leader;
        Votes.Remove(conn);
        var (remote, stream, ciphers) = conn.Wire;
        await conn.QuestGate.WaitAsync();
        try
        {
            QuestFlagCommit? set = null, clear = null;
            int dialogueComponent = -1;
            lock (conn.Monsters)
            {
                if (NearbyLeader(conn, bandit, id) is { } leader && !Finished(conn.HasQuestFlag))
                {
                    if (help)
                    {
                        string[] previous = [.. All.Where(b => b != bandit).SelectMany(b => new[] { b.Ally, b.Started })];
                        string[] chosen = [bandit.Ally, bandit.Started];
                        Saves.Atomically(() =>
                        {
                            clear = CommitQuestFlags(conn, previous, on: false);
                            set = CommitQuestFlags(conn, chosen);
                        });
                    }
                    else
                    {
                        clear = CommitQuestFlags(conn, [bandit.Ally, bandit.Started], on: false);
                        conn.Instance.SetState(Fight);
                        leader.EntranceGrace = false;
                        lock (conn.Npcs) conn.Npcs.Remove(id);
                        if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        {
                            dialogueComponent = origin.Object.Components.ToList().IndexOf("NPC");
                            conn.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { HasDialogue = false } };
                        }
                    }
                }
            }
            await Send(remote, stream, ciphers, InstanceNpcs.CloseVote(), $"0x00be close {bandit.Name}'s choice");
            if (dialogueComponent >= 0)
                await SendWorldRecord(remote, stream, ciphers, conn, id,
                    InstanceNpcs.DialogueAvailability(id, (byte)dialogueComponent, false), $"0x02bc {bandit.Name} stops talking and fights");
            if (clear is not null) await PublishQuestFlags(remote, stream, ciphers, conn, clear, on: false);
            if (set is not null) await PublishQuestFlags(remote, stream, ciphers, conn, set);
        }
        finally { conn.QuestGate.Release(); }
    }

    static async Task HandIn(ClientState conn, uint id, Bandit bandit, NpcCatalog.Talk talk)
    {
        var (remote, stream, ciphers) = conn.Wire;
        await conn.QuestGate.WaitAsync();
        try
        {
            QuestFlagCommit? set = null, clear = null;
            Claim? claimed;
            string reply;
            lock (conn.Monsters)
            {
                if (NearbyLeader(conn, bandit, id) is null) return;
                (claimed, reply) = ClaimReward(conn, bandit);
                if (claimed is not null)
                {
                    set = CommitQuestFlags(conn, claimed.Flags);
                    clear = CommitQuestFlags(conn, claimed.Consumed.Cleared, on: false);
                }
            }
            if (claimed is null)
            {
                if (conn.Commands is { } chat) await chat.Reply(reply);
                return;
            }
            var items = ItemsOf(conn);
            var removed = claimed.Consumed.Removed.ToLookup(r => r.Row, r => r.Item.Id);
            const byte main = PlayerInventories.MainInventory;
            var added = claimed.Apex?.AddedItems ?? [];
            foreach (byte row in removed.Select(r => r.Key).Concat(added.Count > 0 ? [main] : Array.Empty<byte>()).Distinct())
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row),
                    [.. removed[row]], row == main ? added : []), "0x00bf bandit quest turn-in");
            if (clear is not null) await PublishQuestFlags(remote, stream, ciphers, conn, clear, on: false);
            if (set is not null) await PublishQuestFlags(remote, stream, ciphers, conn, set);
            conn.InNpcTalk = true;
            conn.NpcFinishFlag = null;
            conn.NpcTalkScript = null;
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name} accepts the amulets");
        }
        finally { conn.QuestGate.Release(); }
    }

    internal sealed record Claim(QuestItems.Consumed Consumed, PlayerInventories.Stacking? Apex, string[] Flags);
    sealed class NoRoomForApex : Exception;

    // Plan the exchange before changing either inventory or flags. The amulets, the Apex, and the
    // permanent reward are then committed together.
    internal static (Claim? Result, string Reply) ClaimReward(ClientState conn, Bandit bandit)
    {
        var character = CharacterName(conn);
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        if (catalog.Find(AmuletPath + "CombinedAmulet") is not { Supported: true, Quest: { Grant: { } haveApex } apexQuest } apex)
            return (null, "The quest reward is unavailable.");
        string[] rivals = [.. All.Where(b => b != bandit).Select(AmuletId)];
        try
        {
            return Saves.Atomically<(Claim?, string)>(() =>
            {
                var flags = (CharacterSaves.Load(character)?.QuestFlags ?? []).ToHashSet();
                if (Finished(flags.Contains)) return (null, "You have already completed the bandit quest.");
                if (!flags.Contains(bandit.Ally) || flags.Contains(bandit.Killed))
                    return (null, $"Offer to help {bandit.Name} before handing in the amulets.");
                var carried = QuestItems.Carried(items)
                    .Select(it => (it.Row, it.Item, Type: catalog.ByHash(it.Item.Item.TypeHash)))
                    .Where(it => it.Type?.Quest?.Remove == Delivered)
                    .DistinctBy(it => it.Type!.Quest).ToList();
                if (!rivals.All(id => carried.Any(it => it.Type!.Id == id)))
                    return (null, "You need to be carrying both rivals' amulets.");
                var taking = carried.Select(it => it.Item.Id).ToHashSet();
                if (items.OfferedInPlace().Any(it => taking.Contains(it.Item.Id)))
                    return (null, "Take the amulets out of the trade window first.");

                var apexItem = ItemCatalog.Create(apex);
                bool needsApex = !QuestRewardCatalog.Taken(null, apexQuest, flags.Contains);
                if (needsApex && !items.Fits(PlayerInventories.MainInventory, apexItem, ShapeOf, taking))
                    return (null, "Free space in your main inventory for the Apex and try again.");

                string[] rewardFlags = [bandit.Reward, Delivered, SeenApex];
                var consumed = QuestItems.Consume(character, items, item => QuestFlagsOf(conn, item), rewardFlags);
                if (consumed is null || consumed.Removed.Count < rivals.Length) throw new NoRoomForApex();
                PlayerInventories.Stacking? placed = null;
                if (needsApex)
                {
                    placed = QuestRewardCatalog.Claim(character, null, apexQuest, items, apexItem, ShapeOf);
                    if (placed is null) throw new NoRoomForApex();
                    rewardFlags = [.. rewardFlags, haveApex];
                }
                return (new(consumed, placed, rewardFlags), $"{bandit.Name} accepted both amulets. Your reward is ready and you can continue to the Caverns.");
            });
        }
        catch (NoRoomForApex)
        {
            return (null, "The exchange could not complete. Free space in your main inventory and try again.");
        }
    }
}
