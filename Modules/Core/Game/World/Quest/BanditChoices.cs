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
    const string Fight = "QuestObjects.BanditFight";
    const int ChoiceRange = 16;
    const string AfterFight = "AfterBanditFight";
    sealed class Vote { public uint Leader; }
    static readonly ConditionalWeakTable<Player, Vote> Votes = new();
    public static readonly IReadOnlyList<Bandit> All = [Kraityn, Alira, Oak];

    public static string AmuletId(Bandit bandit) => AmuletPath + bandit.Amulet;

    public static Bandit? In(Player player) => player.Layout.Id switch
    {
        "1_2_4" => Kraityn,
        "1_2_9" => Alira,
        "1_2_12" => Oak,
        _ => null,
    };

    static bool Solo(Player player) => player.PartySize == 1
        && !player.Instance.PlayersExcept(player.Id).Any(p => !p.Closed);

    static bool Finished(Func<string, bool> has) => has(Delivered) || has(EramirReward) || All.Any(b => has(b.Reward));

    static bool Available(Player player, Bandit bandit) =>
        QuestsOf(player).OnMonsterKilled(bandit.Area, bandit.Variety).Contains(bandit.Killed);

    public static bool Held(Player player, Bandit bandit) =>
        Available(player, bandit) && Solo(player) && Npcs.Find(bandit.Variety) is not null && !player.Character.HasQuestFlag(bandit.Killed)
        && (player.Character.HasQuestFlag(bandit.Reward) || !Finished(player.Character.HasQuestFlag) && !player.Instance.HasState(Fight));

    // This session flag is needed by NPCTalk's hand-in rows. Project it only in the allied
    // leader's area so it does not suppress either rival's first conversation.
    public static void AddDialogueFlags(Player player, HashSet<string> flags)
    {
        if (In(player) is { } bandit && flags.Contains(bandit.Ally)) flags.Add(AfterFight);
    }

    static SpawnedMonster? Leader(Player player, Bandit bandit, uint id)
    {
        var leader = player.Area.Monsters.GetValueOrDefault(id);
        return leader is { Hostile: true, Dead: false, Life: > 0, Aggro: false } && leader.Variety?.Id == bandit.Variety
            && Held(player, bandit) ? leader : null;
    }

    static SpawnedMonster? NearbyLeader(Player player, Bandit bandit, uint id)
    {
        if (player.Closed || player.LeavingArea || player.PendingTransfer is not null || player.PlayerLife == 0 || HardcoreProgressBlocked(player))
            return null;
        double now = ProjectileWorldTime(player);
        var leader = Leader(player, bandit, id);
        return leader is not null && Dist(player.Walk.Position(now), leader.Walk.Position(now)) <= ChoiceRange + leader.Walk.Size
            ? leader : null;
    }

    public static SpawnedNpc? Speaker(Player player, uint id)
    {
        if (In(player) is not { } bandit || Npcs.Find(bandit.Variety) is not { } npc) return null;
        if (!player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) || !origin.Object.HasDialogue) return null;
        if (Leader(player, bandit, id) is not { } leader) return null;
        var (x, y) = leader.Walk.Position(ProjectileWorldTime(player));
        if (!player.Npcs.TryGetValue(id, out var speaker)) player.Npcs[id] = speaker = new(npc, x, y);
        return speaker;
    }

    static bool CallsVote(NpcCatalog.Talk talk) => talk.Script.Contains("BanditCallVote", StringComparison.Ordinal);

    // Intercept these rows before the generic NPCTalk engine applies their flags. The amulets and reward
    // must be handled by one inventory transaction, and the vote's temporary flags must not enter the save.
    public static async Task<bool> ChooseDialogue(Player player, uint id, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (In(player) is not { } bandit || npc.Npc.Id != bandit.Variety || !(CallsVote(talk) || talk.Index == bandit.HandInIndex))
            return false;
        bool allowed = NearbyLeader(player, bandit, id) is not null && !Finished(player.Character.HasQuestFlag);
        if (!allowed || !talk.Available(Quests.Names(PlayerQuestFlags(player)))) return true;
        if (talk.Index == bandit.HandInIndex)
        {
            await HandIn(player, id, bandit, talk);
            return true;
        }

        if (talk.TextAudio.Count == 0)
            return await OpenVote(player, id, npc, talk);

        player.InNpcTalk = true;
        player.NpcTalkScript = talk;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        await Send(player, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name}'s rivals");
        return true;
    }

    public static async Task<bool> OpenVote(Player player, uint id, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (In(player) is not { } bandit || npc.Npc.Id != bandit.Variety || !CallsVote(talk)) return false;
        bool allowed = NearbyLeader(player, bandit, id) is not null && !Finished(player.Character.HasQuestFlag);
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcTalkScript = null;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        await Send(player, InstanceNpcs.Talk(0), $"0x0084 close {bandit.Name}'s dialogue");
        if (allowed)
        {
            Votes.GetOrCreateValue(player).Leader = id;
            await Send(player, InstanceNpcs.OpenVote(bandit.VoteType), $"0x00be {bandit.Name} choice");
        }
        return true;
    }

    public static async Task VoteAnswer(Player player, bool help)
    {
        if (In(player) is not { } bandit || !Votes.TryGetValue(player, out var vote)) return;
        uint id = vote.Leader;
        Votes.Remove(player);
        await player.Character.QuestGate.WaitAsync();
        try
        {
            QuestFlagCommit? set = null, clear = null;
            int dialogueComponent = -1;
            if (NearbyLeader(player, bandit, id) is { } leader && !Finished(player.Character.HasQuestFlag))
            {
                if (help)
                {
                    string[] previous = [.. All.Where(b => b != bandit).SelectMany(b => new[] { b.Ally, b.Started })];
                    string[] chosen = [bandit.Ally, bandit.Started];
                    Saves.Atomically(() =>
                    {
                        clear = CommitQuestFlags(player, previous, on: false);
                        set = CommitQuestFlags(player, chosen);
                    });
                }
                else
                {
                    clear = CommitQuestFlags(player, [bandit.Ally, bandit.Started], on: false);
                    player.Instance.SetState(Fight);
                    leader.EntranceGrace = false;
                    player.Npcs.Remove(id);
                    if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    {
                        dialogueComponent = origin.Object.Components.ToList().IndexOf("NPC");
                        player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { HasDialogue = false } };
                    }
                }
            }
            await Send(player, InstanceNpcs.CloseVote(), $"0x00be close {bandit.Name}'s choice");
            if (dialogueComponent >= 0)
                await SendWorldRecord(player, id,
                    InstanceNpcs.DialogueAvailability(id, (byte)dialogueComponent, false), $"0x02bc {bandit.Name} stops talking and fights");
            if (clear is not null) await PublishQuestFlags(player, clear, on: false);
            if (set is not null) await PublishQuestFlags(player, set);
        }
        finally { player.Character.QuestGate.Release(); }
    }

    static async Task HandIn(Player player, uint id, Bandit bandit, NpcCatalog.Talk talk)
    {
        await player.Character.QuestGate.WaitAsync();
        try
        {
            QuestFlagCommit? set = null, clear = null;
            Claim? claimed;
            string reply;
            if (NearbyLeader(player, bandit, id) is null) return;
            (claimed, reply) = ClaimReward(player, bandit);
            if (claimed is not null)
            {
                set = CommitQuestFlags(player, claimed.Flags);
                clear = CommitQuestFlags(player, claimed.Consumed.Cleared, on: false);
            }
            if (claimed is null)
            {
                if (player.Commands is { } chat) await chat.Reply(reply);
                return;
            }
            var items = ItemsOf(player);
            foreach (var row in claimed.Consumed.Removed.GroupBy(r => r.Row))
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(r => r.Item.Id)], []), "0x00bf bandit quest turn-in");
            if (clear is not null) await PublishQuestFlags(player, clear, on: false);
            if (set is not null) await PublishQuestFlags(player, set);
            player.InNpcTalk = true;
            player.NpcFinishFlag = null;
            player.NpcFinishLeagueFlag = null;
            player.NpcTalkScript = null;
            await Send(player, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name} accepts the amulets");
        }
        finally { player.Character.QuestGate.Release(); }
    }

    internal sealed record Claim(QuestItems.Consumed Consumed, string[] Flags);

    // The amulets and the permanent reward are committed together. The Apex is the leader's own
    // "Get the Apex" row (NPCTalk, reward offer 17), as with Eramir, so a full inventory never blocks the hand-in.
    internal static (Claim? Result, string Reply) ClaimReward(Player player, Bandit bandit)
    {
        var character = CharacterName(player);
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        string[] rivals = [.. All.Where(b => b != bandit).Select(AmuletId)];
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
            string[] rewardFlags = [bandit.Reward, Delivered];
            var consumed = QuestItems.Consume(character, items, item => QuestFlagsOf(player, item), rewardFlags);
            if (consumed is null) return (null, "Take the amulets out of the trade window first.");
            return (new(consumed, rewardFlags), $"{bandit.Name} accepted both amulets.");
        });
    }
}
