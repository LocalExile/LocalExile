using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

// A solo encounter uses a local chat choice. Its allegiance is saved with the character, while opening
// a fight belongs to the current instance. QuestObjects registers the commands and the usual kill/drop rules.
public static class BanditChoices
{
    public sealed record Bandit(string Area, string Variety, string Name, string Amulet, string Kind, int Quest)
    {
        public string Killed { get; } = $"a2b-Killed{Kind}Bandit";
        public string Ally { get; } = $"a2b-{Kind}Ally";
        public string Started { get; } = $"a2q{Quest}-ReceivedQuest";
        public string Reward { get; } = $"a2q{Quest}-QuestRewardTaken";
    }

    public static readonly Bandit Kraityn = new("1_2_4", "Metadata/Monsters/BanditLeaderDexInt/BanditLeaderKraityn", "Kraityn", "DexAmulet", "Dex", 1);
    public static readonly Bandit Alira = new("1_2_9", "Metadata/Monsters/BanditLeaderInt/BanditLeaderAlira", "Alira", "IntAmulet", "Int", 2);
    public static readonly Bandit Oak = new("1_2_12", "Metadata/Monsters/BanditLeaderStrDex/BanditLeaderOak", "Oak", "StrAmulet", "Str", 3);

    const string AmuletPath = "Metadata/Items/QuestItems/BanditAmulets/";
    const string EramirReward = "a2q7-QuestRewardTaken";
    const string Delivered = "a2b-DeliveredAmulets";
    const string SeenApex = "a2b-SeenApexReward";
    const string Fight = "QuestObjects.BanditFight";
    const int ChoiceRange = 16;
    public const string Usage = "/bandit [help | fight | claim] (near a bandit leader, while playing solo)";
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
        Available(conn, bandit) && Solo(conn) && !conn.HasQuestFlag(bandit.Killed)
        && (conn.HasQuestFlag(bandit.Reward) || !Finished(conn.HasQuestFlag) && !conn.Instance.HasState(Fight));

    public static async Task Announce(ClientState conn)
    {
        if (In(conn) is { } bandit && Held(conn, bandit) && !Finished(conn.HasQuestFlag) && conn.Commands is { } commands)
            await commands.Reply($"Approach {bandit.Name} and type /bandit to see your options.");
    }

    public static async Task Command(ClientState conn, string[] args)
    {
        if (conn.Commands is not { } chat) return;
        var (remote, stream, ciphers) = conn.Wire;
        await conn.QuestGate.WaitAsync();
        try
        {
            if (In(conn) is not { } bandit || !Available(conn, bandit) || !Solo(conn))
            {
                await chat.Reply(Usage);
                return;
            }

            string action = args.Length switch { 0 => "", 1 => args[0].ToLowerInvariant(), _ => "invalid" };
            QuestFlagCommit? set = null, clear = null;
            Claim? claimed = null;
            string reply;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0 || HardcoreProgressBlocked(conn))
                    return;
                double now = ProjectileWorldTime(conn);
                var player = conn.Walk.Position(now);
                var leader = conn.Monsters.Values.FirstOrDefault(m => m.Variety?.Id == bandit.Variety && m.Hostile && m.Life > 0 && !m.Dead && !m.Aggro
                    && Dist(player, m.Walk.Position(now)) <= ChoiceRange + m.Walk.Size);
                if (Finished(conn.HasQuestFlag)) reply = "You have already completed the bandit quest.";
                else if (leader is null || !Held(conn, bandit)) reply = $"You need to be near {bandit.Name} before the fight starts.";
                else if (action == "help")
                {
                    string[] previous = [.. All.Where(b => b != bandit).SelectMany(b => new[] { b.Ally, b.Started })];
                    string[] chosen = [bandit.Ally, bandit.Started];
                    Saves.Atomically(() =>
                    {
                        CharacterSaves.ChangeQuestFlags(CharacterName(conn), [.. previous], false);
                        CharacterSaves.ChangeQuestFlags(CharacterName(conn), [.. chosen], true);
                    });
                    clear = CommitQuestFlags(conn, previous, on: false);
                    set = CommitQuestFlags(conn, chosen);
                    reply = $"You offered to help {bandit.Name}. Bring both rivals' amulets here and type /bandit claim. To withdraw your offer, type /bandit fight here.";
                }
                else if (action == "fight")
                {
                    clear = CommitQuestFlags(conn, [bandit.Ally, bandit.Started], on: false);
                    conn.Instance.SetState(Fight);
                    leader.EntranceGrace = false;
                    reply = $"You challenged {bandit.Name}.";
                }
                else if (action == "claim")
                {
                    (claimed, reply) = ClaimReward(conn, bandit);
                    if (claimed is not null)
                    {
                        set = CommitQuestFlags(conn, claimed.Flags);
                        clear = CommitQuestFlags(conn, claimed.Consumed.Cleared, on: false);
                    }
                }
                else reply = action.Length == 0
                    ? $"{bandit.Name}: /bandit help to offer help, /bandit fight to challenge, /bandit claim to turn in both rivals' amulets."
                    : Usage;
            }

            if (claimed is not null)
            {
                var items = ItemsOf(conn);
                var removed = claimed.Consumed.Removed.ToLookup(r => r.Row, r => r.Item.Id);
                const byte main = PlayerInventories.MainInventory;
                var added = claimed.Apex?.AddedItems ?? [];
                foreach (byte row in removed.Select(r => r.Key).Concat(added.Count > 0 ? [main] : Array.Empty<byte>()).Distinct())
                    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row),
                        [.. removed[row]], row == main ? added : []), "0x00bf bandit quest turn-in");
            }
            if (clear is not null) await PublishQuestFlags(remote, stream, ciphers, conn, clear, on: false);
            if (set is not null) await PublishQuestFlags(remote, stream, ciphers, conn, set);
            await chat.Reply(reply);
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
