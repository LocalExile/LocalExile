using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

// Solo and multi-player Bandit choices (S→C 0xbe / C→S 0xbd, 3.29.3c VoteDisplay 0x14026e170).
// Allegiance belongs to each character, while a vote session and fight challenge belong to the instance.
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

    sealed class VoteParticipant(string characterName, Player? player, byte state, bool eligible)
    {
        public string CharacterName { get; } = characterName;
        public Player? Player { get; set; } = player;
        public byte State { get; set; } = state;
        public bool Eligible { get; set; } = eligible;
    }

    sealed class PartyVote(Bandit bandit, uint leaderId, bool isSolo)
    {
        public Bandit Bandit { get; } = bandit;
        public uint LeaderId { get; } = leaderId;
        public bool IsSolo { get; set; } = isSolo;
        public volatile bool Resolved;
        public List<VoteParticipant> Participants { get; } = [];
        public List<Player> Observers { get; } = [];
    }

    sealed record VoteBroadcast(Bandit Bandit, bool IsSolo, VoteParticipant[] Participants, Player[] Recipients);

    sealed class PartyMembersRegistration(Func<Player, IReadOnlyList<Player>> provider) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref partyMembersProvider, null, provider);
    }

    // VoteGate guards ActiveVotes and every PartyVote's fields; never nest it with QuestGate.
    static readonly object VoteGate = new();
    static readonly RuleTable<InstanceState, PartyVote> ActiveVotes = new();
    static volatile Func<Player, IReadOnlyList<Player>>? partyMembersProvider;

    public static readonly IReadOnlyList<Bandit> All = [Kraityn, Alira, Oak];

    /// <summary>Registers a provider returning human party members for a player (including members outside the bandit
    /// area, who appear as <see cref="InstanceNpcs.VoteStateWaitingToEnterArea"/> until they enter). Dispose the
    /// returned handle when the registering module unloads.</summary>
    public static IDisposable RegisterPartyMembers(Func<Player, IReadOnlyList<Player>> provider)
    {
        partyMembersProvider = provider;
        return new PartyMembersRegistration(provider);
    }

    public static string AmuletId(Bandit bandit) => AmuletPath + bandit.Amulet;

    public static Bandit? In(Player player) => player.Layout.Id switch
    {
        "1_2_4" => Kraityn,
        "1_2_9" => Alira,
        "1_2_12" => Oak,
        _ => null,
    };

    static bool Finished(Func<string, bool> has) => has(Delivered) || has(EramirReward) || All.Any(b => has(b.Reward));

    static bool Available(Player player, Bandit bandit) =>
        QuestsOf(player).OnMonsterKilled(bandit.Area, bandit.Variety).Contains(bandit.Killed);

    static bool CanHoldFor(Player player, Bandit bandit) =>
        Available(player, bandit) && !player.Character.HasQuestFlag(bandit.Killed)
        && (player.Character.HasQuestFlag(bandit.Reward) || !Finished(player.Character.HasQuestFlag));

    static bool CanVote(Player player, Bandit bandit) =>
        !player.Controlled && !player.Closed && !HardcoreProgressBlocked(player)
        && Available(player, bandit) && !player.Character.HasQuestFlag(bandit.Killed) && !Finished(player.Character.HasQuestFlag);

    static bool HasActiveVote(InstanceState? instance) =>
        instance is not null && ActiveVotes.TryGetValue(instance, out var session) && !session.Resolved;

    public static bool Held(Player player, Bandit bandit)
    {
        if (Npcs.Find(bandit.Variety) is null) return false;
        var instance = player.Instance;
        if (instance?.HasState(Fight) == true) return false;
        if (HasActiveVote(instance)) return true;
        if (CanHoldFor(player, bandit)) return true;
        if (instance is null) return false;
        foreach (var other in instance.Presence.PlayersExcept(player.Id))
            if (other is { Controlled: false, Closed: false, LeavingArea: false }
                && ReferenceEquals(other.Instance, instance) && CanHoldFor(other, bandit))
                return true;
        return false;
    }

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
            && Npcs.Find(bandit.Variety) is not null && player.Instance?.HasState(Fight) != true
            && CanHoldFor(player, bandit) ? leader : null;
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
        if (player.Instance is not { } instance || !instance.World.MonsterOrigins.TryGetValue(id, out var origin) || !origin.Object.HasDialogue) return null;
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
            if (HasActiveVote(player.Instance)) return true;
            await HandIn(player, id, bandit, talk);
            return true;
        }

        if (talk.TextAudio.Count == 0)
            return await OpenVote(player, id, npc, talk);

        player.InNpcTalk = true;
        player.NpcTalkScript = talk;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        await SendTo(player, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name}'s rivals");
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
        await SendTo(player, InstanceNpcs.Talk(0), $"0x0084 close {bandit.Name}'s dialogue");
        if (!allowed || player.Instance is not { } instance) return true;

        var humans = InstanceHumans(instance, include: player);
        var eligibleHumans = humans.Select(h => (Player: h, Eligible: CanVote(h, bandit))).ToList();
        var inAreaNames = new HashSet<string>(humans.Select(CharacterName), StringComparer.OrdinalIgnoreCase);
        var waitingOutside = WaitingPartyMembers(player, instance, inAreaNames, bandit);
        bool callerEligible = eligibleHumans.FirstOrDefault(e => ReferenceEquals(e.Player, player)).Eligible;

        VoteBroadcast? broadcast = null;
        lock (VoteGate)
        {
            if (ActiveVotes.TryGetValue(instance, out var existing) && !existing.Resolved)
            {
                EnsureObserverLocked(existing, player);
                if (FindParticipantLocked(existing, player) is { } existingParticipant)
                {
                    existingParticipant.Player = player;
                    if (existingParticipant.State == InstanceNpcs.VoteStateWaitingToEnterArea && callerEligible)
                        existingParticipant.State = InstanceNpcs.VoteStateWaitingForVote;
                }
                else if (callerEligible)
                {
                    existing.Participants.Add(new(CharacterName(player), player, InstanceNpcs.VoteStateWaitingForVote, eligible: true));
                    if (existing.Participants.Count > 1) existing.IsSolo = false;
                }
                broadcast = SnapshotBroadcastLocked(existing, instance, include: player);
            }
            else
            {
                bool solo = humans.Count <= 1 && waitingOutside.Count == 0;
                var session = new PartyVote(bandit, id, solo);
                foreach (var (human, eligible) in eligibleHumans)
                {
                    EnsureObserverLocked(session, human);
                    if (eligible)
                        session.Participants.Add(new(CharacterName(human), human, InstanceNpcs.VoteStateWaitingForVote, eligible: true));
                }
                foreach (var member in waitingOutside)
                    session.Participants.Add(new(CharacterName(member), member, InstanceNpcs.VoteStateWaitingToEnterArea, eligible: true));
                if (session.Participants.Count > 0)
                {
                    ActiveVotes.AddOrUpdate(instance, session);
                    broadcast = SnapshotBroadcastLocked(session, instance, include: player);
                }
            }
        }

        if (broadcast is not null)
            await BroadcastVote(broadcast);
        return true;
    }

    public static async Task VoteAnswer(Player player, bool help)
    {
        if (player.Controlled || In(player) is not { } bandit || player.Instance is not { } instance)
            return;

        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, Player[] Recipients)? resolve = null;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            var participant = FindParticipantLocked(session, player);
            if (participant is null || !participant.Eligible || participant.State != InstanceNpcs.VoteStateWaitingForVote)
                return;
            participant.Player = player;
            participant.State = help ? InstanceNpcs.VoteStateHelpBandit : InstanceNpcs.VoteStateKillBandit;
            PruneWaitingOutsideLocked(session, instance);

            if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants],
                    CollectRecipientsLocked(session, instance, include: player));
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance, include: player);
            }
        }

        if (broadcast is not null)
        {
            await BroadcastVote(broadcast);
            return;
        }
        if (resolve is { } r)
            await ResolveVote(instance, player, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
    }

    /// <summary>Updates an active instance Bandit vote when a player finishes entering the bandit area.</summary>
    public static async Task OnAreaGenerated(Player player)
    {
        if (player.Controlled || player.Closed || player.LeavingArea || player.Instance is not { } instance
            || In(player) is not { } bandit || !HasActiveVote(instance))
            return;

        bool eligible = CanVote(player, bandit);

        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, Player[] Recipients)? resolve = null;
        Player[]? cancelRecipients = null;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            EnsureObserverLocked(session, player);
            string name = CharacterName(player);
            var existing = FindParticipantLocked(session, name);
            if (existing is not null)
            {
                existing.Player = player;
                if (existing.State == InstanceNpcs.VoteStateWaitingToEnterArea)
                {
                    if (eligible)
                    {
                        existing.State = InstanceNpcs.VoteStateWaitingForVote;
                        existing.Eligible = true;
                    }
                    else
                    {
                        session.Participants.Remove(existing);
                    }
                }
            }
            else if (eligible)
            {
                session.Participants.Add(new(name, player, InstanceNpcs.VoteStateWaitingForVote, eligible: true));
            }

            if (CollectRecipientsLocked(session, instance, include: player).Length > 1 || session.Participants.Count > 1)
                session.IsSolo = false;

            PruneWaitingOutsideLocked(session, instance);

            if (!session.Participants.Any(p => p.Eligible))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                cancelRecipients = CollectRecipientsLocked(session, instance, include: player);
            }
            else if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants],
                    CollectRecipientsLocked(session, instance, include: player));
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance, include: player);
            }
        }

        if (cancelRecipients is not null) await CloseVoteForAll(cancelRecipients, bandit);
        else if (resolve is { } r) await ResolveVote(instance, null, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
        else if (broadcast is not null) await BroadcastVote(broadcast);
    }

    /// <summary>Updates or resolves an active Bandit vote when a participant leaves the area or disconnects.</summary>
    public static async Task OnPlayerLeaving(Player player, string why = "left the area")
    {
        if (player.Controlled) return;
        string name = CharacterName(player);

        if (player.Instance is { } instance && HasActiveVote(instance))
            await RemoveFromSession(instance, player, name, leavingInstance: true);

        // A true disconnect from another area releases any WaitingToEnterArea slot in an open bandit vote.
        // An area transition closes the old socket with "disconnected" too, so never drop WaitingToEnterArea slots
        // while the player is transferring between areas.
        bool midTransfer = player.LeavingArea || player.PendingTransfer is not null
            || player.TransferAccepted || player.TransferredTo is not null;
        if (string.Equals(why, "disconnected", StringComparison.OrdinalIgnoreCase) && !midTransfer)
        {
            InstanceState[] otherInstances;
            lock (VoteGate)
                otherInstances = [.. ActiveVotes
                    .Where(e => !e.Value.Resolved && !ReferenceEquals(e.Key, player.Instance))
                    .Select(e => e.Key)];
            foreach (var otherInstance in otherInstances)
                await RemoveFromSession(otherInstance, player, name, leavingInstance: false);
        }
    }

    /// <summary>Prunes stale <see cref="InstanceNpcs.VoteStateWaitingToEnterArea"/> slots across active votes when a
    /// human leaves or is kicked from a party, broadcasting or resolving immediately so votes never deadlock.</summary>
    public static async Task OnPartyChanged(Player member)
    {
        if (member.Controlled) return;
        InstanceState[] instances;
        lock (VoteGate)
            instances = [.. ActiveVotes.Where(e => !e.Value.Resolved).Select(e => e.Key)];
        foreach (var instance in instances)
            await PruneSessionAfterPartyChange(instance);
    }

    static async Task PruneSessionAfterPartyChange(InstanceState instance)
    {
        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, Player[] Recipients)? resolve = null;
        Player[]? cancelRecipients = null;
        Bandit? bandit = null;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            bandit = session.Bandit;
            int before = session.Participants.Count;
            PruneWaitingOutsideLocked(session, instance);
            if (session.Participants.Count == before) return;

            var remainingRecipients = CollectRecipientsLocked(session, instance);
            if (remainingRecipients.Length == 0 || !session.Participants.Any(p => p.Eligible))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                cancelRecipients = remainingRecipients;
            }
            else if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants], remainingRecipients);
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance);
            }
        }

        if (cancelRecipients is not null && bandit is not null) await CloseVoteForAll(cancelRecipients, bandit);
        else if (resolve is { } r) await ResolveVote(instance, null, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
        else if (broadcast is not null) await BroadcastVote(broadcast);
    }

    /// <summary>Filters kill-granted quest flags so a player who allied with a bandit (e.g., in a mixed party vote)
    /// does not also receive that bandit's conflicting <c>Killed</c> flag if another party member kills the bandit.</summary>
    public static IReadOnlyList<string> FilterKillFlags(Player player, IReadOnlyList<string> flags)
    {
        if (flags.Count == 0 || In(player) is not { } bandit || !player.Character.HasQuestFlag(bandit.Ally))
            return flags;
        bool hasConflict = false;
        for (int i = 0; i < flags.Count; i++)
            if (flags[i] == bandit.Killed) { hasConflict = true; break; }
        if (!hasConflict) return flags;
        return [.. flags.Where(f => f != bandit.Killed)];
    }

    /// <summary>Filters kill-granted quest drops so a player who allied with a bandit does not receive that allied
    /// bandit's own amulet when another party member kills the bandit in a mixed-vote fight.</summary>
    public static IReadOnlyList<string> FilterKillDrops(Player player, string varietyId, IReadOnlyList<string> drops)
    {
        if (drops.Count == 0 || In(player) is not { } bandit || varietyId != bandit.Variety
            || !player.Character.HasQuestFlag(bandit.Ally))
            return drops;
        string amulet = AmuletId(bandit);
        bool hasConflict = false;
        for (int i = 0; i < drops.Count; i++)
            if (drops[i] == amulet) { hasConflict = true; break; }
        if (!hasConflict) return drops;
        return [.. drops.Where(d => d != amulet)];
    }

    static async Task RemoveFromSession(InstanceState instance, Player leaving, string name, bool leavingInstance)
    {
        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, Player[] Recipients)? resolve = null;
        Player[]? cancelRecipients = null;
        Bandit? bandit = null;
        bool closeLeaving = false;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            bandit = session.Bandit;
            bool changed = false;
            if (leavingInstance)
            {
                changed |= session.Observers.RemoveAll(o => ReferenceEquals(o, leaving)
                    || string.Equals(CharacterName(o), name, StringComparison.OrdinalIgnoreCase)) > 0;
                changed |= session.Participants.RemoveAll(p => ReferenceEquals(p.Player, leaving)
                    || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase)) > 0;
                closeLeaving = !leaving.Closed;
            }
            else
            {
                changed |= session.Participants.RemoveAll(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea
                    && (ReferenceEquals(p.Player, leaving) || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase))) > 0;
            }

            if (!changed) return;

            PruneWaitingOutsideLocked(session, instance, exclude: leaving);
            var remainingRecipients = CollectRecipientsLocked(session, instance, exclude: leaving);

            if (remainingRecipients.Length == 0 || !session.Participants.Any(p => p.Eligible))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                cancelRecipients = remainingRecipients;
            }
            else if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants], remainingRecipients);
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance, exclude: leaving);
            }
        }

        if (closeLeaving && bandit is not null)
            await SendTo(leaving, InstanceNpcs.CloseVote(), $"0x00be close {bandit.Name}'s choice (left area)");
        if (cancelRecipients is not null && bandit is not null) await CloseVoteForAll(cancelRecipients, bandit);
        else if (resolve is { } r) await ResolveVote(instance, null, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
        else if (broadcast is not null) await BroadcastVote(broadcast);
    }

    static async Task ResolveVote(InstanceState instance, Player? trigger, Bandit bandit, uint leaderId, bool isSolo,
        VoteParticipant[] participants, Player[] recipients)
    {
        List<(Player Voter, byte State)> candidates = [];
        foreach (var p in participants)
        {
            if (!p.Eligible || p.State is not (InstanceNpcs.VoteStateHelpBandit or InstanceNpcs.VoteStateKillBandit))
                continue;
            if (p.Player is not { Closed: false, LeavingArea: false } voter || !ReferenceEquals(voter.Instance, instance))
                continue;
            candidates.Add((voter, p.State));
        }

        // Acquire every voter's QuestGate in deterministic CharacterName order across commit and publish to avoid
        // deadlocks and prevent concurrent quest updates from interleaving between commit and publish.
        List<SemaphoreSlim> gates = [];
        foreach (var (voter, _) in candidates.OrderBy(c => CharacterName(c.Voter), StringComparer.Ordinal).ThenBy(c => c.Voter.Id))
            if (!gates.Any(g => ReferenceEquals(g, voter.Character.QuestGate)))
                gates.Add(voter.Character.QuestGate);

        int acquired = 0;
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync();
                acquired++;
            }

            List<(Player Voter, QuestFlagCommit? Clear, QuestFlagCommit? Set)> commits = [];
            int dialogueComponent = -1;

            var leader = instance.World.Area.Monsters.GetValueOrDefault(leaderId);
            bool validLeader = isSolo && trigger is not null
                ? NearbyLeader(trigger, bandit, leaderId) is not null && !Finished(trigger.Character.HasQuestFlag)
                : leader is { Hostile: true, Dead: false, Life: > 0, Aggro: false } && leader.Variety?.Id == bandit.Variety
                    && Npcs.Find(bandit.Variety) is not null && !instance.HasState(Fight);

            if (validLeader)
            {
                List<(Player Voter, byte State)> resolved = [];
                foreach (var (voter, state) in candidates)
                {
                    if (voter.Closed || voter.LeavingArea || !ReferenceEquals(voter.Instance, instance))
                        continue;
                    if (HardcoreProgressBlocked(voter) || Finished(voter.Character.HasQuestFlag) || voter.Character.HasQuestFlag(bandit.Killed))
                        continue;
                    resolved.Add((voter, state));
                }

                if (resolved.Count > 0)
                {
                    bool anyKill = resolved.Any(r => r.State == InstanceNpcs.VoteStateKillBandit);
                    string[] previous = [.. All.Where(b => b != bandit).SelectMany(b => new[] { b.Ally, b.Started })];
                    string[] chosen = [bandit.Ally, bandit.Started];

                    Saves.Atomically(() =>
                    {
                        foreach (var (voter, state) in resolved)
                        {
                            if (state == InstanceNpcs.VoteStateHelpBandit)
                            {
                                var clear = CommitQuestFlags(voter, previous, on: false);
                                var set = CommitQuestFlags(voter, chosen);
                                commits.Add((voter, clear, set));
                            }
                            else
                            {
                                var clear = CommitQuestFlags(voter, chosen, on: false);
                                commits.Add((voter, clear, null));
                            }
                        }
                    });

                    if (anyKill)
                    {
                        instance.SetState(Fight);
                        leader!.EntranceGrace = false;
                        foreach (var recipient in recipients)
                            recipient.Npcs.Remove(leaderId);
                        if (instance.World.MonsterOrigins.TryGetValue(leaderId, out var origin))
                        {
                            dialogueComponent = origin.Object.Components.ToList().IndexOf("NPC");
                            instance.World.MonsterOrigins[leaderId] = origin with { Object = origin.Object with { HasDialogue = false } };
                        }
                    }
                }
            }

            await CloseVoteForAll(recipients, bandit);

            if (dialogueComponent >= 0)
            {
                var areaSender = recipients.FirstOrDefault(r => !r.Closed && !r.LeavingArea && r.Area.Owned && r.Connection.WorldOutput.Started);
                if (areaSender is not null)
                {
                    await SendWorldRecord(areaSender, leaderId,
                        InstanceNpcs.DialogueAvailability(leaderId, (byte)dialogueComponent, false),
                        $"0x02bc {bandit.Name} stops talking and fights");
                }
                else
                {
                    foreach (var recipient in recipients)
                    {
                        if (recipient.Closed || recipient.LeavingArea) continue;
                        if (recipient.Connection.Connected)
                            await SendWorldRecord(recipient, leaderId,
                                InstanceNpcs.DialogueAvailability(leaderId, (byte)dialogueComponent, false),
                                $"0x02bc {bandit.Name} stops talking and fights");
                        else
                            await SendTo(recipient, InstanceNpcs.DialogueAvailability(leaderId, (byte)dialogueComponent, false),
                                $"0x02bc {bandit.Name} stops talking and fights");
                    }
                }
            }

            foreach (var (voter, clear, set) in commits)
            {
                if (voter.Closed || voter.LeavingArea || (clear is null && set is null)) continue;
                if (!voter.Connection.Connected) continue;
                if (clear is not null) await PublishQuestFlags(voter, clear, on: false);
                if (set is not null) await PublishQuestFlags(voter, set);
            }
        }
        finally
        {
            for (int i = acquired - 1; i >= 0; i--)
                gates[i].Release();
        }
    }

    static async Task BroadcastVote(VoteBroadcast broadcast)
    {
        if (broadcast.IsSolo && broadcast.Recipients.Length == 1)
        {
            await SendTo(broadcast.Recipients[0], InstanceNpcs.OpenVote(broadcast.Bandit.VoteType),
                $"0x00be {broadcast.Bandit.Name} choice");
            return;
        }

        InstanceNpcs.VoteEntry[] voters = [.. broadcast.Participants
            .Where(p => p.Eligible)
            .Select(p => new InstanceNpcs.VoteEntry(p.CharacterName, p.State))];

        foreach (var recipient in broadcast.Recipients)
        {
            if (recipient.Closed || recipient.LeavingArea) continue;
            string name = CharacterName(recipient);
            var own = broadcast.Participants.FirstOrDefault(p => ReferenceEquals(p.Player, recipient)
                || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase));
            bool localVoter = own is { Eligible: true };
            bool answered = own is { Eligible: true, State: InstanceNpcs.VoteStateKillBandit or InstanceNpcs.VoteStateHelpBandit };
            // Fresh packet per recipient: wire encryption mutates the array in place.
            await SendTo(recipient, InstanceNpcs.OpenVote(broadcast.Bandit.VoteType, answered, localVoter, voters),
                $"0x00be {broadcast.Bandit.Name} party vote ({voters.Length} voter(s))");
        }
    }

    static async Task CloseVoteForAll(Player[] recipients, Bandit bandit)
    {
        foreach (var recipient in recipients)
        {
            if (recipient.Closed) continue;
            await SendTo(recipient, InstanceNpcs.CloseVote(), $"0x00be close {bandit.Name}'s choice");
        }
    }

    static VoteBroadcast SnapshotBroadcastLocked(PartyVote session, InstanceState instance,
        Player? include = null, Player? exclude = null) =>
        new(session.Bandit, session.IsSolo,
            [.. session.Participants.Select(p => new VoteParticipant(p.CharacterName, p.Player, p.State, p.Eligible))],
            CollectRecipientsLocked(session, instance, include, exclude));

    static bool AllEligibleVotedLocked(PartyVote session) =>
        !session.Participants.Any(p => p.Eligible
            && p.State is InstanceNpcs.VoteStateWaitingForVote or InstanceNpcs.VoteStateWaitingToEnterArea);

    static VoteParticipant? FindParticipantLocked(PartyVote session, Player player)
    {
        string name = CharacterName(player);
        return session.Participants.FirstOrDefault(p => ReferenceEquals(p.Player, player))
            ?? FindParticipantLocked(session, name);
    }

    static VoteParticipant? FindParticipantLocked(PartyVote session, string characterName) =>
        session.Participants.FirstOrDefault(p => string.Equals(p.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));

    static void EnsureObserverLocked(PartyVote session, Player player)
    {
        if (player.Controlled || player.Closed || player.LeavingArea) return;
        string name = CharacterName(player);
        session.Observers.RemoveAll(o => o.Closed || o.LeavingArea
            || string.Equals(CharacterName(o), name, StringComparison.OrdinalIgnoreCase));
        session.Observers.Add(player);
    }

    static void PruneWaitingOutsideLocked(PartyVote session, InstanceState instance, Player? exclude = null)
    {
        if (!session.Participants.Any(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea)) return;
        var anchor = CollectRecipientsLocked(session, instance, exclude: exclude).FirstOrDefault();
        if (partyMembersProvider is { } provider && anchor is not null)
        {
            var members = provider(anchor);
            var liveParty = new HashSet<string>(
                members.Where(m => !m.Controlled && !m.Closed && !ReferenceEquals(m, exclude)).Select(CharacterName),
                StringComparer.OrdinalIgnoreCase);
            ulong instanceNumber = anchor.InstanceNumber;
            session.Participants.RemoveAll(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea
                && !liveParty.Contains(p.CharacterName)
                && !(instanceNumber != 0 && p.Player?.TransferredTo?.Instance == instanceNumber));
            return;
        }
        session.Participants.RemoveAll(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea
            && (p.Player is null || p.Player.Closed || ReferenceEquals(p.Player, exclude)));
    }

    static List<Player> InstanceHumans(InstanceState instance, Player? include = null, Player? exclude = null)
    {
        List<Player> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void TryAdd(Player? candidate)
        {
            if (candidate is not { Controlled: false, Closed: false, LeavingArea: false }
                || ReferenceEquals(candidate, exclude) || !ReferenceEquals(candidate.Instance, instance))
                return;
            if (names.Add(CharacterName(candidate))) result.Add(candidate);
        }

        TryAdd(include);
        foreach (var peer in instance.Presence.PlayersExcept(include?.Id ?? 0)
            .OrderBy(CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id))
            TryAdd(peer);
        return result;
    }

    static Player[] CollectRecipientsLocked(PartyVote session, InstanceState instance,
        Player? include = null, Player? exclude = null)
    {
        var list = InstanceHumans(instance, include, exclude);
        var names = new HashSet<string>(list.Select(CharacterName), StringComparer.OrdinalIgnoreCase);
        void TryAdd(Player? candidate)
        {
            if (candidate is not { Controlled: false, Closed: false, LeavingArea: false }
                || ReferenceEquals(candidate, exclude) || !ReferenceEquals(candidate.Instance, instance))
                return;
            if (names.Add(CharacterName(candidate))) list.Add(candidate);
        }
        foreach (var participant in session.Participants) TryAdd(participant.Player);
        foreach (var observer in session.Observers) TryAdd(observer);
        return [.. list];
    }

    static List<Player> WaitingPartyMembers(Player player, InstanceState instance, HashSet<string> inAreaNames, Bandit bandit)
    {
        if (partyMembersProvider is not { } provider) return [];
        List<Player> waiting = [];
        foreach (var member in provider(player)
            .Where(m => !m.Controlled && !m.Closed && !ReferenceEquals(m.Instance, instance))
            .OrderBy(CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            if (!CanVote(member, bandit)) continue;
            if (inAreaNames.Add(CharacterName(member))) waiting.Add(member);
        }
        return waiting;
    }

    static async Task SendTo(Player player, byte[] bytes, string? log)
    {
        if (player.Closed) return;
        try
        {
            if (player.Connection.Outbox is { } outbox) { await outbox(bytes, log); return; }
            if (player.Connection.Connected) await Send(player, bytes, log);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
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
