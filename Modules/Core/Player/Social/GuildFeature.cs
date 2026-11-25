using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>Persistent guild membership, lifecycle (<c>C→S 0x0140</c>–<c>0x014f</c> / <c>S→C 0x0146</c>–<c>0x014c</c>),
/// <c>&amp;</c> guild chat, shared guild hideouts and online guild peer lookup.</summary>
public static class GuildFeature
{
    const string StoreAccountKey = "__guilds__";
    const string StoreDataKey = "social.guilds.v1";
    const int MaxNameLength = 30;
    const int MinTagLength = 3;
    const int MaxTagLength = 6;
    const int MaxStatusLength = 200;
    const int MaxMembers = 250;
    const int MaxInvitesPerTarget = 20;
    const int MaxInvitesPerGuild = 50;

    sealed record SavedMember(string Account, ulong JoinedAt, byte Rank);
    sealed record SavedGuild(ulong Id, string Name, string Tag, string Status, ulong CreatedAt, string? HideoutArea, List<SavedMember> Members);
    sealed record SavedRegistry(ulong NextGuildId, List<SavedGuild> Guilds);
    sealed record PendingInvite(ushort Index, ulong GuildId, string TargetAccount, string InviterAccount);

    sealed class GuildState
    {
        public ulong Id;
        public string Name = "";
        public string Tag = "";
        public string Status = "";
        public ulong CreatedAt;
        public string? HideoutArea;
        public Dictionary<string, SavedMember> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class GuildDbCache
    {
        public readonly object Gate = new();
        public bool Loaded;
        public ulong NextGuildId = 1;
        public ushort NextInviteIndex = 1;
        public Dictionary<ulong, GuildState> Guilds { get; } = [];
        public Dictionary<string, ulong> AccountGuild { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ushort, PendingInvite> Invites { get; } = [];
    }

    static readonly ConditionalWeakTable<SaveDatabase, GuildDbCache> DbCaches = new();
    static readonly ConcurrentDictionary<string, Player> Online = new(StringComparer.OrdinalIgnoreCase);

    static GuildDbCache Cache => DbCaches.GetOrCreateValue(Saves.Db);

    static string AccountOf(Player player) => player.Character.Account ?? CharacterName(player);

    static void Register(Player player)
    {
        if (!player.Closed && !player.LeavingArea && player.Listed)
            Online[AccountOf(player)] = player;
    }

    static Player? FindOnlineByAccount(string account)
    {
        if (Online.TryGetValue(account, out var p) && !p.Closed && !p.LeavingArea && p.Listed) return p;
        return InGame.Keys.FirstOrDefault(c => c.Listed && !c.Closed && !c.LeavingArea
            && string.Equals(AccountOf(c), account, StringComparison.OrdinalIgnoreCase));
    }

    static Player? FindOnlineByNameOrAccount(string name) =>
        InGame.Keys.FirstOrDefault(c => c.Listed && !c.Closed && !c.LeavingArea
            && (string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(AccountOf(c), name, StringComparison.OrdinalIgnoreCase)))
        ?? (Online.TryGetValue(name, out var p) && !p.Closed && !p.LeavingArea && p.Listed ? p : null);

    static bool CanAct(Player player) =>
        Saves.IsOpen && !player.Closed && !player.LeavingArea && player.Listed;

    /// <summary>Clears in-memory runtime caches on module unload/hot-reload.</summary>
    public static void ResetRuntime()
    {
        Online.Clear();
        DbCaches.Clear();
    }

    /// <summary>Whether <paramref name="player"/> may manage shared guild stash tabs (Officer/Leader, or solo when not in a guild).</summary>
    public static bool CanManageStash(Player player)
    {
        if (player.Character.Ssf || !player.Listed) return false;
        if (!Saves.IsOpen) return true;
        string account = AccountOf(player);
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild))
                return true; // Not in a guild: solo per-account guild stash fallback.
            return guild.Members.TryGetValue(account, out var member)
                && member.Rank is InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer;
        }
    }

    static void EnsureLoadedLocked(GuildDbCache cache)
    {
        if (cache.Loaded) return;
        cache.Guilds.Clear();
        cache.AccountGuild.Clear();
        cache.NextGuildId = 1;
        if (Saves.IsOpen && AccountStash.For(StoreAccountKey, Items).ModuleData(StoreDataKey) is { Length: > 0 } json)
        {
            try
            {
                if (JsonSerializer.Deserialize<SavedRegistry>(json) is { } saved)
                {
                    cache.NextGuildId = Math.Max(1UL, saved.NextGuildId);
                    foreach (var sg in saved.Guilds ?? [])
                    {
                        if (sg.Id == 0 || string.IsNullOrWhiteSpace(sg.Name) || sg.Members is not { Count: > 0 }) continue;
                        var g = new GuildState
                        {
                            Id = sg.Id,
                            Name = sg.Name,
                            Tag = sg.Tag ?? "",
                            Status = sg.Status ?? "",
                            CreatedAt = sg.CreatedAt,
                            HideoutArea = sg.HideoutArea,
                        };
                        foreach (var m in sg.Members)
                        {
                            if (string.IsNullOrWhiteSpace(m.Account)) continue;
                            g.Members[m.Account] = m;
                            cache.AccountGuild[m.Account] = g.Id;
                        }
                        if (g.Members.Count > 0)
                        {
                            cache.Guilds[g.Id] = g;
                            if (g.Id >= cache.NextGuildId) cache.NextGuildId = g.Id + 1;
                        }
                    }
                }
            }
            catch (JsonException) { }
        }
        cache.Loaded = true;
    }

    static void PersistLocked(GuildDbCache cache)
    {
        if (!Saves.IsOpen) return;
        var snapshot = new SavedRegistry(cache.NextGuildId, [.. cache.Guilds.Values.OrderBy(g => g.Id).Select(g =>
            new SavedGuild(g.Id, g.Name, g.Tag, g.Status, g.CreatedAt, g.HideoutArea,
                [.. g.Members.Values.OrderBy(m => m.JoinedAt).ThenBy(m => m.Account, StringComparer.OrdinalIgnoreCase)]))]);
        string serialized = JsonSerializer.Serialize(snapshot);
        Saves.Atomically(() =>
        {
            Saves.Db.OnRollback(() => cache.Loaded = false);
            AccountStash.For(StoreAccountKey, Items).ChangeModuleData(StoreDataKey, _ => serialized);
        });
    }

    static List<InstanceGuilds.Member> SnapshotMembersLocked(GuildState guild, GuildDbCache cache)
    {
        var list = new List<InstanceGuilds.Member>(guild.Members.Count);
        foreach (var m in guild.Members.Values.OrderBy(m => m.JoinedAt).ThenBy(m => m.Account, StringComparer.OrdinalIgnoreCase))
            list.Add(new InstanceGuilds.Member(m.Account, m.JoinedAt, m.Rank));
        foreach (var inv in cache.Invites.Values.Where(i => i.GuildId == guild.Id))
            if (!guild.Members.ContainsKey(inv.TargetAccount))
                list.Add(new InstanceGuilds.Member(inv.TargetAccount, 0, InstanceGuilds.RankInvited));
        return list;
    }

    static List<Player> OnlineMembersLocked(GuildState guild, Player? exclude = null)
    {
        var result = new List<Player>();
        foreach (string account in guild.Members.Keys)
            if (FindOnlineByAccount(account) is { } peer && !ReferenceEquals(peer, exclude))
                result.Add(peer);
        return result;
    }

    static byte[] MemberKnownPacket(string account)
    {
        string status = FriendsFeature.StatusOf(account);
        return FindOnlineByAccount(account) is { } online
            ? InstanceSocial.KnownPlayer(account, CharacterName(online), online.Layout.Hash, PackedClass(online),
                online.Character.PortraitFrame, WornPortrait(online), status)
            : InstanceSocial.OfflinePlayer(account, status);
    }

    static async Task SendSafely(Player player, byte[] packet, string label)
    {
        if (player.Closed || player.LeavingArea || player.Connection.Outbox is not { } outbox) return;
        try { await outbox(packet, label); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    static async Task Reply(Player player, string message)
    {
        if (player.Commands is { } commands) await commands.Reply(message);
        else await SendSafely(player, InstanceChat.ChatLine(message), $"0x0017 guild reply: {message}");
    }

    public static IEnumerable<(string Log, byte[] Message)> Enter(Player player)
    {
        Register(player);
        if (!Saves.IsOpen || !player.Listed) yield break;
        string account = AccountOf(player);
        (string Name, string Tag, string Status, ulong CreatedAt, List<InstanceGuilds.Member> Members)? guildInfo = null;
        List<(ushort Index, string Name, string Tag, string Status, ulong CreatedAt, string Inviter)> pendingInvites = [];
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (cache.AccountGuild.TryGetValue(account, out ulong gid) && cache.Guilds.TryGetValue(gid, out var guild))
                guildInfo = (guild.Name, guild.Tag, guild.Status, guild.CreatedAt, SnapshotMembersLocked(guild, cache));
            foreach (var inv in cache.Invites.Values.Where(i => string.Equals(i.TargetAccount, account, StringComparison.OrdinalIgnoreCase)))
                if (cache.Guilds.TryGetValue(inv.GuildId, out var g))
                    pendingInvites.Add((inv.Index, g.Name, g.Tag, g.Status, g.CreatedAt, inv.InviterAccount));
        }
        if (guildInfo is { } info)
        {
            foreach (var m in info.Members)
                yield return ($"0x00a7 guild member {m.Account}", MemberKnownPacket(m.Account));
            yield return ($"0x0148 guild info \"{info.Name}\" ({info.Members.Count} member(s))",
                InstanceGuilds.GuildInfo(info.Name, info.Tag, info.Status, info.CreatedAt, info.Members));
        }
        foreach (var inv in pendingInvites)
            yield return ($"0x0146 pending guild invite #{inv.Index} from \"{inv.Name}\"",
                InstanceGuilds.InviteNotify(inv.Index, inv.Name, inv.Tag, inv.Status, inv.CreatedAt, inv.Inviter));
    }

    public static async Task AfterEntry(Player player)
    {
        if (!CanAct(player) || !Saves.IsOpen) return;
        Register(player);
        string account = AccountOf(player);
        List<Player> peers;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)) return;
            peers = OnlineMembersLocked(guild, exclude: player);
        }
        foreach (var peer in peers)
            await SendSafely(peer, MemberKnownPacket(account), $"0x00a7 guild member online {account}");
    }

    public static async Task Leaving(Player player)
    {
        string account = AccountOf(player);
        Online.TryRemove(new KeyValuePair<string, Player>(account, player));
        if (player.LeavingArea || player.TransferAccepted || !Saves.IsOpen) return;
        List<Player> peers;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)) return;
            peers = OnlineMembersLocked(guild, exclude: player);
        }
        string status = FriendsFeature.StatusOf(account);
        foreach (var peer in peers)
            await SendSafely(peer, InstanceSocial.OfflinePlayer(account, status), $"0x00a7 guild member offline {account}");
    }

    /// <summary>C→S 0x0140: create a new guild with the sender as Leader.</summary>
    public static async Task Create(Player player, string rawName)
    {
        if (!CanAct(player)) return;
        Register(player);
        string account = AccountOf(player);
        string name = ChatWire.Clean(rawName).Trim();
        if (name.Length == 0) { await Reply(player, "Guild name cannot be empty."); return; }
        if (name.Length > MaxNameLength) { await Reply(player, "The guild name is too long."); return; }

        GuildState? created = null;
        List<InstanceGuilds.Member> members = [];
        string? error = null;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (cache.AccountGuild.ContainsKey(account))
                error = "This account is already in a guild.";
            else if (cache.Guilds.Values.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                error = "A guild with that name already exists.";
            else
            {
                ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                created = new GuildState
                {
                    Id = cache.NextGuildId++,
                    Name = name,
                    Tag = "",
                    Status = "",
                    CreatedAt = now,
                    HideoutArea = HideoutCatalog.DefaultArea,
                };
                created.Members[account] = new SavedMember(account, now, InstanceGuilds.RankLeader);
                cache.Guilds[created.Id] = created;
                cache.AccountGuild[account] = created.Id;
                PersistLocked(cache);
                members = SnapshotMembersLocked(created, cache);
            }
        }
        if (error is not null || created is null) { await Reply(player, error ?? "Failed to create guild."); return; }

        Console.WriteLine($"[gateway] <= 0x0140 guild create: {account} created \"{created.Name}\" (id {created.Id})");
        await SendSafely(player, MemberKnownPacket(account), $"0x00a7 guild creator {account}");
        await SendSafely(player, InstanceGuilds.GuildInfo(created.Name, created.Tag, created.Status, created.CreatedAt, members),
            $"0x0148 guild info \"{created.Name}\"");
    }

    /// <summary>C→S 0x0141: invite another online player to the sender's guild (Officer or Leader).</summary>
    public static async Task Invite(Player player, string rawTarget)
    {
        if (!CanAct(player)) return;
        Register(player);
        string inviter = AccountOf(player);
        string targetName = rawTarget.Trim();
        var target = FindOnlineByNameOrAccount(targetName);
        if (target is null) { await Reply(player, "Player not found."); return; }
        string targetAccount = AccountOf(target);
        if (string.Equals(inviter, targetAccount, StringComparison.OrdinalIgnoreCase))
        {
            await Reply(player, "You cannot invite yourself to a guild.");
            return;
        }
        if (FriendsFeature.IsIgnoring(target.Character.Account, inviter)) return;

        ushort inviteIndex;
        string gName, gTag, gStatus;
        ulong gCreatedAt;
        List<Player> guildPeers;
        string? error = null;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(inviter, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)
                || !guild.Members.TryGetValue(inviter, out var senderMember))
                error = "Account is not a member of this guild.";
            else if (senderMember.Rank is not (InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer))
                error = "You must be a guild officer or leader to perform this action.";
            else if (cache.AccountGuild.ContainsKey(targetAccount))
                error = "This account is already in a guild.";
            else if (guild.Members.Count >= MaxMembers)
                error = "The guild is full and can not have any more members added.";
            else if (cache.Invites.Values.Any(i => i.GuildId == gid && string.Equals(i.TargetAccount, targetAccount, StringComparison.OrdinalIgnoreCase)))
                error = "That player has already been invited to your guild.";
            else if (cache.Invites.Values.Count(i => string.Equals(i.TargetAccount, targetAccount, StringComparison.OrdinalIgnoreCase)) >= MaxInvitesPerTarget)
                error = "This character is already invited to too many guilds.";
            else if (cache.Invites.Values.Count(i => i.GuildId == gid) >= MaxInvitesPerGuild)
                error = "The guild has too many pending invitations.";
            if (error is not null)
            {
                inviteIndex = 0; gName = gTag = gStatus = ""; gCreatedAt = 0; guildPeers = [];
            }
            else
            {
                var targetGuild = cache.Guilds[cache.AccountGuild[inviter]];
                inviteIndex = cache.NextInviteIndex++;
                if (cache.NextInviteIndex == 0) cache.NextInviteIndex = 1;
                while (cache.Invites.ContainsKey(inviteIndex))
                {
                    inviteIndex++;
                    if (inviteIndex == 0) inviteIndex = 1;
                }
                cache.Invites[inviteIndex] = new PendingInvite(inviteIndex, targetGuild.Id, targetAccount, inviter);
                gName = targetGuild.Name; gTag = targetGuild.Tag; gStatus = targetGuild.Status; gCreatedAt = targetGuild.CreatedAt;
                guildPeers = OnlineMembersLocked(targetGuild);
            }
        }
        if (error is not null) { await Reply(player, error); return; }

        Console.WriteLine($"[gateway] <= 0x0141 guild invite #{inviteIndex}: {inviter} -> {targetAccount} (\"{gName}\")");
        await SendSafely(target, InstanceGuilds.InviteNotify(inviteIndex, gName, gTag, gStatus, gCreatedAt, inviter),
            $"0x0146 guild invite #{inviteIndex} from \"{gName}\"");
        foreach (var peer in guildPeers)
        {
            await SendSafely(peer, MemberKnownPacket(targetAccount), $"0x00a7 invited guild member {targetAccount}");
            await SendSafely(peer, InstanceGuilds.MemberUpsert(targetAccount, 0, InstanceGuilds.RankInvited),
                $"0x014a guild invitee {targetAccount}");
        }
    }

    /// <summary>C→S 0x0142: accept a pending guild invite by index.</summary>
    public static async Task AcceptInvite(Player player, ushort inviteIndex)
    {
        if (!CanAct(player)) return;
        Register(player);
        string account = AccountOf(player);
        string gName = "", gTag = "", gStatus = "";
        ulong gCreatedAt = 0, joinedAt = 0;
        List<InstanceGuilds.Member> members = [];
        List<Player> existingPeers = [];
        List<ushort> expiredOwnInvites = [];
        string? error = null;

        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.Invites.TryGetValue(inviteIndex, out var invite)
                || !string.Equals(invite.TargetAccount, account, StringComparison.OrdinalIgnoreCase))
                error = "The guild invitation is no longer valid.";
            else if (cache.AccountGuild.ContainsKey(account))
                error = "This account is already in a guild.";
            else if (!cache.Guilds.TryGetValue(invite.GuildId, out var guild))
            {
                cache.Invites.Remove(inviteIndex);
                error = "The specified guild was not found.";
            }
            else if (guild.Members.Count >= MaxMembers)
                error = "The guild is full and can not have any more members added.";
            else
            {
                cache.Invites.Remove(inviteIndex);
                foreach (var otherInv in cache.Invites.Values.Where(i => string.Equals(i.TargetAccount, account, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    cache.Invites.Remove(otherInv.Index);
                    expiredOwnInvites.Add(otherInv.Index);
                }
                joinedAt = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                guild.Members[account] = new SavedMember(account, joinedAt, InstanceGuilds.RankMember);
                cache.AccountGuild[account] = guild.Id;
                PersistLocked(cache);
                gName = guild.Name; gTag = guild.Tag; gStatus = guild.Status; gCreatedAt = guild.CreatedAt;
                members = SnapshotMembersLocked(guild, cache);
                existingPeers = OnlineMembersLocked(guild, exclude: player);
            }
        }
        if (error is not null) { await Reply(player, error); return; }

        Console.WriteLine($"[gateway] <= 0x0142 guild accept #{inviteIndex}: {account} joined \"{gName}\"");
        foreach (ushort expired in expiredOwnInvites)
            await SendSafely(player, InstanceGuilds.InviteExpired(expired), $"0x0147 expire guild invite #{expired}");
        foreach (var m in members)
            await SendSafely(player, MemberKnownPacket(m.Account), $"0x00a7 guild member {m.Account}");
        await SendSafely(player, InstanceGuilds.GuildInfo(gName, gTag, gStatus, gCreatedAt, members),
            $"0x0148 guild info \"{gName}\"");
        foreach (var peer in existingPeers)
        {
            await SendSafely(peer, MemberKnownPacket(account), $"0x00a7 new guild member {account}");
            await SendSafely(peer, InstanceGuilds.MemberUpsert(account, joinedAt, InstanceGuilds.RankMember),
                $"0x014a guild member joined {account}");
        }
    }

    /// <summary>C→S 0x0143: decline a pending guild invite by index.</summary>
    public static async Task DeclineInvite(Player player, ushort inviteIndex)
    {
        if (!CanAct(player)) return;
        Register(player);
        string account = AccountOf(player);
        List<Player> guildPeers = [];
        bool removed = false;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (cache.Invites.TryGetValue(inviteIndex, out var invite)
                && string.Equals(invite.TargetAccount, account, StringComparison.OrdinalIgnoreCase))
            {
                cache.Invites.Remove(inviteIndex);
                removed = true;
                if (cache.Guilds.TryGetValue(invite.GuildId, out var guild))
                    guildPeers = OnlineMembersLocked(guild);
            }
        }
        if (!removed) return;
        Console.WriteLine($"[gateway] <= 0x0143 guild decline #{inviteIndex}: {account}");
        await SendSafely(player, InstanceGuilds.InviteExpired(inviteIndex), $"0x0147 declined guild invite #{inviteIndex}");
        foreach (var peer in guildPeers)
            await SendSafely(peer, InstanceGuilds.MemberRemove(account), $"0x014b guild invite declined {account}");
    }

    /// <summary>C→S 0x0144: change a guild member's rank (Initiate/Member/Officer/Leader).</summary>
    public static async Task SetMemberRank(Player player, string rawTarget, byte newRank)
    {
        if (!CanAct(player)) return;
        Register(player);
        string actor = AccountOf(player);
        string targetAccount = rawTarget.Trim();
        if (newRank is not (InstanceGuilds.RankMember or InstanceGuilds.RankOfficer or InstanceGuilds.RankLeader or InstanceGuilds.RankInitiate))
            return;

        List<(string Account, ulong JoinedAt, byte Rank)> updates = [];
        List<Player> guildPeers = [];
        string? error = null;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(actor, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)
                || !guild.Members.TryGetValue(actor, out var actorMember))
                error = "Account is not a member of this guild.";
            else if (!guild.Members.TryGetValue(targetAccount, out var targetMember))
                error = "Account is not a member of this guild.";
            else if (string.Equals(actor, targetMember.Account, StringComparison.OrdinalIgnoreCase))
                error = "You cannot change your own guild rank.";
            else if (newRank == InstanceGuilds.RankLeader)
            {
                if (actorMember.Rank != InstanceGuilds.RankLeader)
                    error = "You must be the guild leader to perform this action.";
                else
                {
                    var promoted = targetMember with { Rank = InstanceGuilds.RankLeader };
                    var demoted = actorMember with { Rank = InstanceGuilds.RankOfficer };
                    guild.Members[promoted.Account] = promoted;
                    guild.Members[demoted.Account] = demoted;
                    PersistLocked(cache);
                    updates.Add((promoted.Account, promoted.JoinedAt, promoted.Rank));
                    updates.Add((demoted.Account, demoted.JoinedAt, demoted.Rank));
                    guildPeers = OnlineMembersLocked(guild);
                }
            }
            else if (actorMember.Rank == InstanceGuilds.RankLeader)
            {
                var updated = targetMember with { Rank = newRank };
                guild.Members[updated.Account] = updated;
                PersistLocked(cache);
                updates.Add((updated.Account, updated.JoinedAt, updated.Rank));
                guildPeers = OnlineMembersLocked(guild);
            }
            else if (actorMember.Rank == InstanceGuilds.RankOfficer)
            {
                bool targetManageable = targetMember.Rank is InstanceGuilds.RankMember or InstanceGuilds.RankInitiate;
                bool rankAllowed = newRank is InstanceGuilds.RankMember or InstanceGuilds.RankInitiate;
                if (!targetManageable || !rankAllowed)
                    error = "You must be the guild leader to perform this action.";
                else
                {
                    var updated = targetMember with { Rank = newRank };
                    guild.Members[updated.Account] = updated;
                    PersistLocked(cache);
                    updates.Add((updated.Account, updated.JoinedAt, updated.Rank));
                    guildPeers = OnlineMembersLocked(guild);
                }
            }
            else
                error = "You must be a guild officer or leader to perform this action.";
        }
        if (error is not null) { await Reply(player, error); return; }

        Console.WriteLine($"[gateway] <= 0x0144 guild rank: {actor} set {targetAccount} -> {newRank}");
        foreach (var peer in guildPeers)
            foreach (var (acc, joined, rank) in updates)
                await SendSafely(peer, InstanceGuilds.MemberUpsert(acc, joined, rank),
                    $"0x014a guild rank {acc}={rank}");
    }

    /// <summary>C→S 0x0145: leave the guild (empty or self) or kick a member / cancel an invite.</summary>
    public static async Task LeaveOrKick(Player player, string rawTarget)
    {
        if (!CanAct(player)) return;
        Register(player);
        string actor = AccountOf(player);
        string targetName = rawTarget.Trim();
        bool isSelf = targetName.Length == 0
            || string.Equals(targetName, actor, StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetName, CharacterName(player), StringComparison.OrdinalIgnoreCase);

        Player? kickedOnline = null;
        string removedAccount = "";
        SavedMember? promotedLeader = null;
        List<Player> remainingPeers = [];
        List<(Player Target, ushort InviteIndex)> expiredInvites = [];
        bool wasMember = false;
        string? error = null;

        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(actor, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)
                || !guild.Members.TryGetValue(actor, out var actorMember))
            {
                error = "Account is not a member of this guild.";
            }
            else if (isSelf)
            {
                wasMember = true;
                removedAccount = actorMember.Account;
                guild.Members.Remove(removedAccount);
                cache.AccountGuild.Remove(removedAccount);
                if (guild.Members.Count == 0)
                {
                    cache.Guilds.Remove(gid);
                    foreach (var inv in cache.Invites.Values.Where(i => i.GuildId == gid).ToList())
                    {
                        cache.Invites.Remove(inv.Index);
                        if (FindOnlineByAccount(inv.TargetAccount) is { } invPeer)
                            expiredInvites.Add((invPeer, inv.Index));
                    }
                }
                else
                {
                    if (actorMember.Rank == InstanceGuilds.RankLeader)
                    {
                        var successor = guild.Members.Values
                            .OrderBy(m => m.Rank == InstanceGuilds.RankOfficer ? 0 : m.Rank == InstanceGuilds.RankMember ? 1 : 2)
                            .ThenBy(m => m.JoinedAt)
                            .First();
                        promotedLeader = successor with { Rank = InstanceGuilds.RankLeader };
                        guild.Members[promotedLeader.Account] = promotedLeader;
                    }
                    remainingPeers = OnlineMembersLocked(guild);
                }
                PersistLocked(cache);
                kickedOnline = player;
            }
            else
            {
                if (actorMember.Rank is not (InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer))
                {
                    error = "You must be a guild officer or leader to perform this action.";
                }
                else if (guild.Members.TryGetValue(targetName, out var targetMember))
                {
                    bool officerForbidden = actorMember.Rank == InstanceGuilds.RankOfficer
                        && targetMember.Rank is InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer;
                    if (officerForbidden)
                        error = "You must be the guild leader to perform this action.";
                    else
                    {
                        wasMember = true;
                        removedAccount = targetMember.Account;
                        guild.Members.Remove(removedAccount);
                        cache.AccountGuild.Remove(removedAccount);
                        PersistLocked(cache);
                        kickedOnline = FindOnlineByAccount(removedAccount);
                        remainingPeers = OnlineMembersLocked(guild);
                    }
                }
                else if (cache.Invites.Values.FirstOrDefault(i => i.GuildId == gid
                    && string.Equals(i.TargetAccount, targetName, StringComparison.OrdinalIgnoreCase)) is { } pending)
                {
                    cache.Invites.Remove(pending.Index);
                    removedAccount = pending.TargetAccount;
                    if (FindOnlineByAccount(pending.TargetAccount) is { } invPeer)
                        expiredInvites.Add((invPeer, pending.Index));
                    remainingPeers = OnlineMembersLocked(guild);
                }
                else
                {
                    error = "Account is not a member of this guild.";
                }
            }
        }
        if (error is not null) { await Reply(player, error); return; }

        Console.WriteLine($"[gateway] <= 0x0145 guild {(isSelf ? "leave" : "kick")}: {actor} removed {removedAccount}");
        foreach (var (invPeer, idx) in expiredInvites)
            await SendSafely(invPeer, InstanceGuilds.InviteExpired(idx), $"0x0147 guild invite #{idx} cancelled");
        if (wasMember && kickedOnline is not null)
            await SendSafely(kickedOnline, InstanceGuilds.GuildLeft(), $"0x0149 guild left ({removedAccount})");
        foreach (var peer in remainingPeers)
        {
            await SendSafely(peer, InstanceGuilds.MemberRemove(removedAccount), $"0x014b guild member removed {removedAccount}");
            if (promotedLeader is { } next)
                await SendSafely(peer, InstanceGuilds.MemberUpsert(next.Account, next.JoinedAt, next.Rank),
                    $"0x014a guild leader promoted {next.Account}");
        }
    }

    /// <summary>C→S 0x014d: update guild status message (Officer or Leader).</summary>
    public static Task SetStatus(Player player, string rawStatus) =>
        UpdateDetails(player, rawStatus, requireLeader: false, field: "status");

    /// <summary>C→S 0x014e: rename guild (Leader only).</summary>
    public static Task Rename(Player player, string rawName) =>
        UpdateDetails(player, rawName, requireLeader: true, field: "name");

    /// <summary>C→S 0x014f: set or clear guild tag (Leader only).</summary>
    public static Task SetTag(Player player, string rawTag) =>
        UpdateDetails(player, rawTag, requireLeader: true, field: "tag");

    static async Task UpdateDetails(Player player, string rawValue, bool requireLeader, string field)
    {
        if (!CanAct(player)) return;
        Register(player);
        string actor = AccountOf(player);
        string value = ChatWire.Clean(rawValue).Trim();
        if (field == "name" && value.Length is 0 or > MaxNameLength)
        {
            await Reply(player, value.Length == 0 ? "Guild name cannot be empty." : "The guild name is too long.");
            return;
        }
        if (field == "tag" && value.Length != 0 && (value.Length < MinTagLength || value.Length > MaxTagLength))
        {
            await Reply(player, value.Length < MinTagLength ? "Guild tag must be a minimum of 3 characters." : "The guild tag is too long.");
            return;
        }
        if (field == "status" && value.Length > MaxStatusLength)
        {
            await Reply(player, "This guild status message is too long.");
            return;
        }

        string gName = "", gTag = "", gStatus = "";
        ulong gCreatedAt = 0;
        List<Player> guildPeers = [];
        string? error = null;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(actor, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)
                || !guild.Members.TryGetValue(actor, out var actorMember))
                error = "Account is not a member of this guild.";
            else if (requireLeader && actorMember.Rank != InstanceGuilds.RankLeader)
                error = "You must be the guild leader to perform this action.";
            else if (!requireLeader && actorMember.Rank is not (InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer))
                error = "You must be a guild officer or leader to perform this action.";
            else if (field == "name" && cache.Guilds.Values.Any(g => g.Id != gid && string.Equals(g.Name, value, StringComparison.OrdinalIgnoreCase)))
                error = "A guild with that name already exists.";
            else if (field == "tag" && value.Length > 0 && cache.Guilds.Values.Any(g => g.Id != gid && string.Equals(g.Tag, value, StringComparison.OrdinalIgnoreCase)))
                error = "That guild tag is already in use.";
            else
            {
                if (field == "name") guild.Name = value;
                else if (field == "tag") guild.Tag = value;
                else guild.Status = value;
                PersistLocked(cache);
                gName = guild.Name; gTag = guild.Tag; gStatus = guild.Status; gCreatedAt = guild.CreatedAt;
                guildPeers = OnlineMembersLocked(guild);
            }
        }
        if (error is not null) { await Reply(player, error); return; }

        Console.WriteLine($"[gateway] guild {field} updated by {actor}: \"{value}\"");
        foreach (var peer in guildPeers)
            await SendSafely(peer, InstanceGuilds.DetailsChanged(gName, gTag, gStatus, gCreatedAt),
                $"0x014c guild details changed ({field})");
    }

    /// <summary>Deliver a <c>&amp;</c> guild chat line to every online member of the sender's guild.</summary>
    public static async Task<bool> DeliverChat(Player player, string text, IReadOnlyList<ChatLineItem>? items)
    {
        if (!Saves.IsOpen) return false;
        Register(player);
        string account = AccountOf(player);
        string tag;
        List<Player> peers;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild))
                return false;
            tag = guild.Tag;
            peers = OnlineMembersLocked(guild);
        }
        string name = CharacterName(player);
        string sender = InstanceChat.Sender(TypedChannel.Guild, name);
        bool delivered = false;
        foreach (var peer in peers)
        {
            if (peer is not { HasClient: true, Closed: false, Connection.Outbox: { } outbox }) continue;
            if (GameEvents.GetChatAvailability(peer).Dnd) continue;
            if (!ReferenceEquals(peer, player) && FriendsFeature.IsIgnoring(peer.Character.Account, account)) continue;
            try
            {
                await outbox(InstanceChat.ChatLine(text, sender, items: items, guildTag: tag),
                    $"0x0017 guild chat line from {name}");
                delivered = true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        return delivered;
    }

    public static string? StorageKeyOf(Player player)
    {
        if (!Saves.IsOpen || player.Character.Ssf) return null;
        string account = AccountOf(player);
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            return cache.AccountGuild.TryGetValue(account, out ulong gid) ? $"g:{gid}" : null;
        }
    }

    public static string? TagOf(Player player)
    {
        if (!Saves.IsOpen) return null;
        string account = AccountOf(player);
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            return cache.AccountGuild.TryGetValue(account, out ulong gid) && cache.Guilds.TryGetValue(gid, out var guild)
                ? guild.Tag : null;
        }
    }

    public static IReadOnlyList<Player> PeersOf(Player player)
    {
        if (!Saves.IsOpen) return [];
        string account = AccountOf(player);
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild))
                return [];
            return OnlineMembersLocked(guild, exclude: player);
        }
    }

    public static bool SameGuild(Player a, Player b)
    {
        if (!Saves.IsOpen) return false;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            return cache.AccountGuild.TryGetValue(AccountOf(a), out ulong ga)
                && cache.AccountGuild.TryGetValue(AccountOf(b), out ulong gb) && ga == gb;
        }
    }

    public static ushort? HideoutHashOf(Player player)
    {
        if (!Saves.IsOpen) return null;
        string account = AccountOf(player);
        string? areaId;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild))
                return null;
            areaId = guild.HideoutArea;
        }
        return HideoutCatalog.Selected(areaId)?.Hash16;
    }

    /// <summary>C→S 0x0254 (bit 15 set): select the guild's hideout template (Officer or Leader).</summary>
    public static async Task<bool> SelectGuildHideout(Player player, int row)
    {
        if (!CanAct(player) || player.PendingTransfer is not null || !Saves.IsOpen) return false;
        Register(player);
        var hideout = HideoutCatalog.Hideouts.FirstOrDefault(h => h.Row == row);
        if (hideout is null) return false;

        string account = AccountOf(player);
        string? error = null;
        var cache = Cache;
        lock (cache.Gate)
        {
            EnsureLoadedLocked(cache);
            if (!cache.AccountGuild.TryGetValue(account, out ulong gid) || !cache.Guilds.TryGetValue(gid, out var guild)
                || !guild.Members.TryGetValue(account, out var member))
                error = "Account is not a member of this guild.";
            else if (member.Rank is not (InstanceGuilds.RankLeader or InstanceGuilds.RankOfficer))
                error = "You do not have permission to modify the Guild Hideout.";
            else
            {
                guild.HideoutArea = hideout.AreaId;
                PersistLocked(cache);
            }
        }
        if (error is not null) { await Reply(player, error); return true; }

        player.TalkingTo = 0;
        player.InNpcTalk = false;
        ushort personalHash = global::Game.HomeHideout(player)?.Hash16 ?? 0;
        await SendSafely(player, InstanceNpcs.Talk(0), "0x0084 close guild hideout dialogue");
        await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.PlayerHideoutsUpdate(player.Id, personalHash, hideout.Hash16),
            $"0x02bc selected guild hideout: {hideout.Name}");
        Console.WriteLine($"[gateway] {CharacterName(player)} selected guild hideout row {hideout.Row}: {hideout.AreaId}");
        await global::Game.ChangeArea(player, new PendingTransfer(hideout.AreaHash16, 0, false, AtWaypoint: true),
            $"Helena: Select Guild Hideout ({hideout.Name})");
        return true;
    }
}
