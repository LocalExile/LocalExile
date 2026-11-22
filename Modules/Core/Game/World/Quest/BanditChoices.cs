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

    sealed class VoteParticipant(string characterName, ClientState? conn, byte state, bool eligible)
    {
        public string CharacterName { get; } = characterName;
        public ClientState? Conn { get; set; } = conn;
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
        public List<ClientState> Observers { get; } = [];
    }

    sealed record VoteBroadcast(Bandit Bandit, bool IsSolo, VoteParticipant[] Participants, ClientState[] Recipients);

    sealed class PartyMembersRegistration(Func<ClientState, IReadOnlyList<ClientState>> provider) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref partyMembersProvider, null, provider);
    }

    // VoteGate guards ActiveVotes and every PartyVote's fields; never nest it with Monsters or QuestGate.
    static readonly object VoteGate = new();
    static readonly RuleTable<InstanceState, PartyVote> ActiveVotes = new();
    static volatile Func<ClientState, IReadOnlyList<ClientState>>? partyMembersProvider;
    public static readonly IReadOnlyList<Bandit> All = [Kraityn, Alira, Oak];

    /// <summary>Registers a provider returning human party members for a player (including members outside the bandit
    /// area, who appear as <see cref="InstanceNpcs.VoteStateWaitingToEnterArea"/> until they enter). Dispose the
    /// returned handle when the registering module unloads.</summary>
    public static IDisposable RegisterPartyMembers(Func<ClientState, IReadOnlyList<ClientState>> provider)
    {
        partyMembersProvider = provider;
        return new PartyMembersRegistration(provider);
    }

    public static string AmuletId(Bandit bandit) => AmuletPath + bandit.Amulet;

    public static Bandit? In(ClientState conn) => conn.Layout.Id switch
    {
        "1_2_4" => Kraityn,
        "1_2_9" => Alira,
        "1_2_12" => Oak,
        _ => null,
    };

    static bool Finished(Func<string, bool> has) => has(Delivered) || has(EramirReward) || All.Any(b => has(b.Reward));

    static bool Available(ClientState conn, Bandit bandit) =>
        QuestsOf(conn).OnMonsterKilled(bandit.Area, bandit.Variety).Contains(bandit.Killed);

    static bool CanHoldFor(ClientState conn, Bandit bandit) =>
        Available(conn, bandit) && !conn.HasQuestFlag(bandit.Killed)
        && (conn.HasQuestFlag(bandit.Reward) || !Finished(conn.HasQuestFlag));

    static bool CanVote(ClientState conn, Bandit bandit) =>
        !conn.Controlled && !conn.Closed && !HardcoreProgressBlocked(conn)
        && Available(conn, bandit) && !conn.HasQuestFlag(bandit.Killed) && !Finished(conn.HasQuestFlag);

    static bool HasActiveVote(InstanceState? instance) =>
        instance is not null && ActiveVotes.TryGetValue(instance, out var session) && !session.Resolved;

    public static bool Held(ClientState conn, Bandit bandit)
    {
        if (Npcs.Value.Find(bandit.Variety) is null) return false;
        var instance = conn.Instance;
        if (instance?.HasState(Fight) == true) return false;
        if (HasActiveVote(instance)) return true;
        if (CanHoldFor(conn, bandit)) return true;
        if (instance is null) return false;
        foreach (var peer in instance.Presence.PlayersExcept(conn.PeerId))
            if (peer.Owner is { Controlled: false, Closed: false, LeavingArea: false } other
                && ReferenceEquals(other.Instance, instance) && CanHoldFor(other, bandit))
                return true;
        return false;
    }

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
            && Npcs.Value.Find(bandit.Variety) is not null && conn.Instance?.HasState(Fight) != true
            && CanHoldFor(conn, bandit) ? leader : null;
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
        if (conn.Instance is not { } instance || !instance.World.MonsterOrigins.TryGetValue(id, out var origin) || !origin.Object.HasDialogue) return null;
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
            if (HasActiveVote(conn.Instance)) return true;
            await HandIn(conn, id, bandit, talk);
            return true;
        }

        if (talk.TextAudio.Count == 0)
            return await OpenVote(conn, id, npc, talk);

        conn.InNpcTalk = true;
        conn.NpcTalkScript = talk;
        conn.NpcFinishFlag = null;
        conn.NpcFinishLeagueFlag = null;
        await SendTo(conn, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name}'s rivals");
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
        conn.NpcFinishLeagueFlag = null;
        await SendTo(conn, InstanceNpcs.Talk(0), $"0x0084 close {bandit.Name}'s dialogue");
        if (!allowed || conn.Instance is not { } instance) return true;

        // Gather eligibility outside VoteGate so VoteGate and Monsters are never nested.
        var humans = InstanceHumans(instance, include: conn);
        var eligibleHumans = humans.Select(h => (Conn: h, Eligible: CanVote(h, bandit))).ToList();
        var inAreaNames = new HashSet<string>(humans.Select(CharacterName), StringComparer.OrdinalIgnoreCase);
        var waitingOutside = WaitingPartyMembers(conn, instance, inAreaNames, bandit);
        bool callerEligible = eligibleHumans.FirstOrDefault(e => ReferenceEquals(e.Conn, conn)).Eligible;

        VoteBroadcast broadcast;
        lock (VoteGate)
        {
            if (ActiveVotes.TryGetValue(instance, out var existing) && !existing.Resolved)
            {
                EnsureObserverLocked(existing, conn);
                if (FindParticipantLocked(existing, conn) is { } existingParticipant)
                {
                    existingParticipant.Conn = conn;
                    if (existingParticipant.State == InstanceNpcs.VoteStateWaitingToEnterArea && callerEligible)
                        existingParticipant.State = InstanceNpcs.VoteStateWaitingForVote;
                }
                else if (callerEligible)
                {
                    existing.Participants.Add(new(CharacterName(conn), conn, InstanceNpcs.VoteStateWaitingForVote, eligible: true));
                    if (existing.Participants.Count > 1) existing.IsSolo = false;
                }
                broadcast = SnapshotBroadcastLocked(existing, instance, include: conn);
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
                ActiveVotes.AddOrUpdate(instance, session);
                broadcast = SnapshotBroadcastLocked(session, instance, include: conn);
            }
        }

        await BroadcastVote(broadcast);
        return true;
    }

    public static async Task VoteAnswer(ClientState conn, bool help)
    {
        if (conn.Controlled || In(conn) is not { } bandit || conn.Instance is not { } instance)
            return;

        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, ClientState[] Recipients)? resolve = null;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            var participant = FindParticipantLocked(session, conn);
            if (participant is null || !participant.Eligible || participant.State != InstanceNpcs.VoteStateWaitingForVote)
                return;
            participant.Conn = conn;
            participant.State = help ? InstanceNpcs.VoteStateHelpBandit : InstanceNpcs.VoteStateKillBandit;
            PruneWaitingOutsideLocked(session, instance);

            if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants],
                    CollectRecipientsLocked(session, instance, include: conn));
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance, include: conn);
            }
        }

        if (broadcast is not null)
        {
            await BroadcastVote(broadcast);
            return;
        }
        if (resolve is { } r)
            await ResolveVote(instance, conn, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
    }

    /// <summary>Updates an active instance Bandit vote when a player finishes entering the bandit area.</summary>
    public static async Task OnAreaGenerated(ClientState conn)
    {
        if (conn.Controlled || conn.Closed || conn.LeavingArea || conn.Instance is not { } instance
            || In(conn) is not { } bandit || !HasActiveVote(instance))
            return;

        // Precompute eligibility outside VoteGate so VoteGate and Monsters are never nested.
        bool eligible = CanVote(conn, bandit);

        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, ClientState[] Recipients)? resolve = null;
        ClientState[]? cancelRecipients = null;

        lock (VoteGate)
        {
            if (!ActiveVotes.TryGetValue(instance, out var session) || session.Resolved) return;
            EnsureObserverLocked(session, conn);
            string name = CharacterName(conn);
            var existing = FindParticipantLocked(session, name);
            if (existing is not null)
            {
                existing.Conn = conn;
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
                session.Participants.Add(new(name, conn, InstanceNpcs.VoteStateWaitingForVote, eligible: true));
            }

            if (CollectRecipientsLocked(session, instance, include: conn).Length > 1 || session.Participants.Count > 1)
                session.IsSolo = false;

            PruneWaitingOutsideLocked(session, instance);

            if (!session.Participants.Any(p => p.Eligible))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                cancelRecipients = CollectRecipientsLocked(session, instance, include: conn);
            }
            else if (AllEligibleVotedLocked(session))
            {
                session.Resolved = true;
                ActiveVotes.Remove(instance);
                resolve = (session.Bandit, session.LeaderId, session.IsSolo, [.. session.Participants],
                    CollectRecipientsLocked(session, instance, include: conn));
            }
            else
            {
                broadcast = SnapshotBroadcastLocked(session, instance, include: conn);
            }
        }

        if (cancelRecipients is not null) await CloseVoteForAll(cancelRecipients, bandit);
        else if (resolve is { } r) await ResolveVote(instance, null, r.Bandit, r.LeaderId, r.IsSolo, r.Participants, r.Recipients);
        else if (broadcast is not null) await BroadcastVote(broadcast);
    }

    /// <summary>Updates or resolves an active Bandit vote when a participant leaves the area or disconnects.</summary>
    public static async Task OnPlayerLeaving(ClientState conn, string why = "left the area")
    {
        if (conn.Controlled) return;
        string name = CharacterName(conn);

        if (conn.Instance is { } instance && HasActiveVote(instance))
            await RemoveFromSession(instance, conn, name, leavingInstance: true);

        // A disconnect from another area releases any WaitingToEnterArea slot in an open bandit vote.
        // An area transition ("left the area") is not dropped here because the player may be entering the bandit area.
        if (string.Equals(why, "disconnected", StringComparison.OrdinalIgnoreCase))
        {
            InstanceState[] otherInstances;
            lock (VoteGate)
                otherInstances = [.. ActiveVotes
                    .Where(e => !e.Value.Resolved && !ReferenceEquals(e.Key, conn.Instance))
                    .Select(e => e.Key)];
            foreach (var otherInstance in otherInstances)
                await RemoveFromSession(otherInstance, conn, name, leavingInstance: false);
        }
    }

    static async Task RemoveFromSession(InstanceState instance, ClientState leaving, string name, bool leavingInstance)
    {
        VoteBroadcast? broadcast = null;
        (Bandit Bandit, uint LeaderId, bool IsSolo, VoteParticipant[] Participants, ClientState[] Recipients)? resolve = null;
        ClientState[]? cancelRecipients = null;
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
                changed |= session.Participants.RemoveAll(p => ReferenceEquals(p.Conn, leaving)
                    || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase)) > 0;
                closeLeaving = !leaving.Closed;
            }
            else
            {
                changed |= session.Participants.RemoveAll(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea
                    && (ReferenceEquals(p.Conn, leaving) || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase))) > 0;
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

    static async Task ResolveVote(InstanceState instance, ClientState? trigger, Bandit bandit, uint leaderId, bool isSolo,
        VoteParticipant[] participants, ClientState[] recipients)
    {
        List<(ClientState Voter, QuestFlagCommit? Clear, QuestFlagCommit? Set)> commits = [];
        int dialogueComponent = -1;

        lock (instance.World.Monsters)
        {
            var leader = instance.World.Monsters.GetValueOrDefault(leaderId);
            bool validLeader = isSolo && trigger is not null
                ? NearbyLeader(trigger, bandit, leaderId) is not null && !Finished(trigger.HasQuestFlag)
                : leader is { Hostile: true, Dead: false, Life: > 0, Aggro: false } && leader.Variety?.Id == bandit.Variety
                    && Npcs.Value.Find(bandit.Variety) is not null && !instance.HasState(Fight);

            if (validLeader)
            {
                List<(ClientState Voter, byte State)> resolved = [];
                foreach (var p in participants)
                {
                    if (!p.Eligible || p.State is not (InstanceNpcs.VoteStateHelpBandit or InstanceNpcs.VoteStateKillBandit))
                        continue;
                    if (p.Conn is not { Closed: false, LeavingArea: false } voter || !ReferenceEquals(voter.Instance, instance))
                        continue;
                    if (HardcoreProgressBlocked(voter) || Finished(voter.HasQuestFlag) || voter.HasQuestFlag(bandit.Killed))
                        continue;
                    resolved.Add((voter, p.State));
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
                            lock (recipient.Npcs) recipient.Npcs.Remove(leaderId);
                        if (instance.World.MonsterOrigins.TryGetValue(leaderId, out var origin))
                        {
                            dialogueComponent = origin.Object.Components.ToList().IndexOf("NPC");
                            instance.World.MonsterOrigins[leaderId] = origin with { Object = origin.Object with { HasDialogue = false } };
                        }
                    }
                }
            }
        }

        await CloseVoteForAll(recipients, bandit);

        if (dialogueComponent >= 0)
        {
            foreach (var recipient in recipients)
            {
                if (recipient.Closed || recipient.LeavingArea) continue;
                var (remote, stream, ciphers) = recipient.Wire;
                if (stream is not null)
                {
                    bool hasObject = ReferenceEquals(recipient, trigger)
                        || recipient.MonsterViews.ContainsKey(leaderId) && !recipient.AsleepMonsters.Contains(leaderId);
                    if (!hasObject) continue;
                    await SendWorldRecord(remote, stream, ciphers, recipient, leaderId,
                        InstanceNpcs.DialogueAvailability(leaderId, (byte)dialogueComponent, false),
                        $"0x02bc {bandit.Name} stops talking and fights");
                }
                else
                {
                    await SendTo(recipient, InstanceNpcs.DialogueAvailability(leaderId, (byte)dialogueComponent, false),
                        $"0x02bc {bandit.Name} stops talking and fights");
                }
            }
        }

        foreach (var (voter, clear, set) in commits)
        {
            if (voter.Closed || voter.LeavingArea || (clear is null && set is null)) continue;
            var (remote, stream, ciphers) = voter.Wire;
            if (stream is null) continue;
            await voter.QuestGate.WaitAsync();
            try
            {
                if (clear is not null) await PublishQuestFlags(remote, stream, ciphers, voter, clear, on: false);
                if (set is not null) await PublishQuestFlags(remote, stream, ciphers, voter, set);
            }
            finally { voter.QuestGate.Release(); }
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
            var own = broadcast.Participants.FirstOrDefault(p => ReferenceEquals(p.Conn, recipient)
                || string.Equals(p.CharacterName, name, StringComparison.OrdinalIgnoreCase));
            bool localVoter = own is { Eligible: true };
            bool answered = own is { Eligible: true, State: InstanceNpcs.VoteStateKillBandit or InstanceNpcs.VoteStateHelpBandit };
            // Fresh packet per recipient: wire encryption mutates the array in place.
            await SendTo(recipient, InstanceNpcs.OpenVote(broadcast.Bandit.VoteType, answered, localVoter, voters),
                $"0x00be {broadcast.Bandit.Name} party vote ({voters.Length} voter(s))");
        }
    }

    static async Task CloseVoteForAll(ClientState[] recipients, Bandit bandit)
    {
        foreach (var recipient in recipients)
        {
            if (recipient.Closed) continue;
            await SendTo(recipient, InstanceNpcs.CloseVote(), $"0x00be close {bandit.Name}'s choice");
        }
    }

    static VoteBroadcast SnapshotBroadcastLocked(PartyVote session, InstanceState instance,
        ClientState? include = null, ClientState? exclude = null) =>
        new(session.Bandit, session.IsSolo,
            [.. session.Participants.Select(p => new VoteParticipant(p.CharacterName, p.Conn, p.State, p.Eligible))],
            CollectRecipientsLocked(session, instance, include, exclude));

    static bool AllEligibleVotedLocked(PartyVote session) =>
        !session.Participants.Any(p => p.Eligible
            && p.State is InstanceNpcs.VoteStateWaitingForVote or InstanceNpcs.VoteStateWaitingToEnterArea);

    static VoteParticipant? FindParticipantLocked(PartyVote session, ClientState conn)
    {
        string name = CharacterName(conn);
        return session.Participants.FirstOrDefault(p => ReferenceEquals(p.Conn, conn))
            ?? FindParticipantLocked(session, name);
    }

    static VoteParticipant? FindParticipantLocked(PartyVote session, string characterName) =>
        session.Participants.FirstOrDefault(p => string.Equals(p.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));

    static void EnsureObserverLocked(PartyVote session, ClientState conn)
    {
        if (conn.Controlled || conn.Closed || conn.LeavingArea) return;
        string name = CharacterName(conn);
        session.Observers.RemoveAll(o => o.Closed || o.LeavingArea
            || string.Equals(CharacterName(o), name, StringComparison.OrdinalIgnoreCase));
        session.Observers.Add(conn);
    }

    static void PruneWaitingOutsideLocked(PartyVote session, InstanceState instance, ClientState? exclude = null)
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
                && !(instanceNumber != 0 && p.Conn?.TransferredTo?.Instance == instanceNumber));
            return;
        }
        session.Participants.RemoveAll(p => p.State == InstanceNpcs.VoteStateWaitingToEnterArea
            && (p.Conn is null || p.Conn.Closed || ReferenceEquals(p.Conn, exclude)));
    }

    static List<ClientState> InstanceHumans(InstanceState instance, ClientState? include = null, ClientState? exclude = null)
    {
        List<ClientState> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void TryAdd(ClientState? candidate)
        {
            if (candidate is not { Controlled: false, Closed: false, LeavingArea: false }
                || ReferenceEquals(candidate, exclude) || !ReferenceEquals(candidate.Instance, instance))
                return;
            if (names.Add(CharacterName(candidate))) result.Add(candidate);
        }

        TryAdd(include);
        foreach (var peer in instance.Presence.PlayersExcept(include?.PeerId ?? 0)
            .Where(p => p.Owner is not null)
            .OrderBy(p => CharacterName(p.Owner!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id))
            TryAdd(peer.Owner);
        return result;
    }

    static ClientState[] CollectRecipientsLocked(PartyVote session, InstanceState instance,
        ClientState? include = null, ClientState? exclude = null)
    {
        var list = InstanceHumans(instance, include, exclude);
        var names = new HashSet<string>(list.Select(CharacterName), StringComparer.OrdinalIgnoreCase);
        void TryAdd(ClientState? candidate)
        {
            if (candidate is not { Controlled: false, Closed: false, LeavingArea: false }
                || ReferenceEquals(candidate, exclude) || !ReferenceEquals(candidate.Instance, instance))
                return;
            if (names.Add(CharacterName(candidate))) list.Add(candidate);
        }
        foreach (var participant in session.Participants) TryAdd(participant.Conn);
        foreach (var observer in session.Observers) TryAdd(observer);
        return [.. list];
    }

    static List<ClientState> WaitingPartyMembers(ClientState conn, InstanceState instance, HashSet<string> inAreaNames, Bandit bandit)
    {
        if (partyMembersProvider is not { } provider) return [];
        List<ClientState> waiting = [];
        foreach (var member in provider(conn)
            .Where(m => !m.Controlled && !m.Closed && !ReferenceEquals(m.Instance, instance))
            .OrderBy(CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            if (!CanVote(member, bandit)) continue;
            if (inAreaNames.Add(CharacterName(member))) waiting.Add(member);
        }
        return waiting;
    }

    static async Task SendTo(ClientState conn, byte[] bytes, string? log)
    {
        if (conn.Closed) return;
        try
        {
            if (conn.Outbox is { } outbox) { await outbox(bytes, log); return; }
            var (remote, stream, ciphers) = conn.Wire;
            if (stream is not null) await Send(remote, stream, ciphers, bytes, log);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
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
            foreach (var row in claimed.Consumed.Removed.GroupBy(r => r.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(r => r.Item.Id)], []), "0x00bf bandit quest turn-in");
            if (clear is not null) await PublishQuestFlags(remote, stream, ciphers, conn, clear, on: false);
            if (set is not null) await PublishQuestFlags(remote, stream, ciphers, conn, set);
            conn.InNpcTalk = true;
            conn.NpcFinishFlag = null;
            conn.NpcFinishLeagueFlag = null;
            conn.NpcTalkScript = null;
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(id, (byte)talk.Index), $"0x0084 {bandit.Name} accepts the amulets");
        }
        finally { conn.QuestGate.Release(); }
    }

    internal sealed record Claim(QuestItems.Consumed Consumed, string[] Flags);

    // The amulets and the permanent reward are committed together. The Apex is the leader's own
    // "Get the Apex" row (NPCTalk, reward offer 17), as with Eramir, so a full inventory never blocks the hand-in.
    internal static (Claim? Result, string Reply) ClaimReward(ClientState conn, Bandit bandit)
    {
        var character = CharacterName(conn);
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
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
            var consumed = QuestItems.Consume(character, items, item => QuestFlagsOf(conn, item), rewardFlags);
            if (consumed is null) return (null, "Take the amulets out of the trade window first.");
            return (new(consumed, rewardFlags), $"{bandit.Name} accepted both amulets.");
        });
    }
}
