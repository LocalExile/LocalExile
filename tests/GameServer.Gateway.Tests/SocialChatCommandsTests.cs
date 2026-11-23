using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modules.M_commands;
using GameServer.Modules.Social;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class SocialChatCommandsTests
{
    static (List<byte[]> Messages, Func<byte[], string?, Task> Outbox) Capture()
    {
        var messages = new List<byte[]>();
        Task Outbox(byte[] bytes, string? log)
        {
            lock (messages) messages.Add((byte[])bytes.Clone());
            return Task.CompletedTask;
        }
        return (messages, Outbox);
    }

    static ushort Id(byte[] m) => BinaryPrimitives.ReadUInt16BigEndian(m);
    static List<byte[]> WithId(List<byte[]> messages, ushort id) => [.. messages.Where(m => m.Length >= 2 && Id(m) == id)];

    [Fact]
    public async Task Social_handlers_party_disconnect_safety_and_chat_trade_framing_work()
    {
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-social-chat-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        using var runtime = new PartyFollowerTests.Runtime();
        var instance = new InstanceState();
        using var wire = new PartyFollowerTests.Wire();
        var store = new SqliteAccountStore(Saves.Db);
        var accA = new Account(Guid.NewGuid(), "SocialAccA", "", "", DateTimeOffset.UtcNow);
        var accB = new Account(Guid.NewGuid(), "SocialAccB", "", "", DateTimeOffset.UtcNow);
        store.AddAccount(accA);
        store.AddAccount(accB);
        store.AddCharacter(new(Guid.NewGuid(), accA.Id, "SocialAlpha", CharacterClass.Witch, 12, "Standard",
            AccountService.StartingArea, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        store.AddCharacter(new(Guid.NewGuid(), accB.Id, "SocialBeta", CharacterClass.Ranger, 14, "Standard",
            AccountService.StartingArea, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var layout = new AreaLayout(0x235c, "1_1_town", 300, 300, 100, 100, null, []);
        var alpha = new Player(layout, 1, 1) { Instance = instance, Character = { Account = accA.Name, Name = "SocialAlpha" } };
        var beta = new Player(layout, 1, 1) { Instance = instance, Character = { Account = accB.Name, Name = "SocialBeta" } };
        alpha.PlayerLife = beta.PlayerLife = 1000;
        alpha.Connection.PlayerSent = beta.Connection.PlayerSent = true;
        alpha.Connection.Session = new("alpha-test", wire.Stream, wire.Ciphers);
        beta.Connection.Session = new("beta-test", wire.Stream, wire.Ciphers);
        Program.InGame.TryAdd(alpha, 0);
        Program.InGame.TryAdd(beta, 0);
        var (alphaMsgs, alphaOutbox) = Capture();
        var (betaMsgs, betaOutbox) = Capture();
        var alphaReplies = new List<string>();
        var betaReplies = new List<string>();
        alpha.Connection.Outbox = alphaOutbox;
        beta.Connection.Outbox = betaOutbox;
        alpha.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = s => { alphaReplies.Add(s); return Task.CompletedTask; } };
        beta.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = s => { betaReplies.Add(s); return Task.CompletedTask; } };

        try
        {
            // 1. Party invite accept after inviter disconnected must not throw ObjectDisposedException.
            await PartyRoster.InviteHuman(alpha, beta.Character.Name!);
            uint inviteParty = PartyRoster.PartyOf(alpha);
            Assert.NotEqual(0u, inviteParty);
            alpha.Connection.Outbox = (_, _) => throw new ObjectDisposedException("NetworkStream");
            alpha.Closed = true;
            betaMsgs.Clear();
            await PartyRoster.AcceptHuman(beta, inviteParty);
            Assert.Equal(0u, PartyRoster.PartyOf(beta));
            Assert.Single(WithId(betaMsgs, InstanceSocial.PartyRemoveId));
            await PartyRoster.DisconnectHuman(alpha, "disconnected");

            // Restore alpha for public party, status, ignore, hideout invite and duel checks.
            alpha.Closed = false;
            alpha.Connection.Outbox = alphaOutbox;
            alphaMsgs.Clear();
            betaMsgs.Clear();

            // 2. Public party (0x98): solo party persists with description, clearing description disbands.
            await PartyRoster.SetPublicParty(alpha, "Act 1 Clear", 0);
            uint publicParty = PartyRoster.PartyOf(alpha);
            Assert.NotEqual(0u, publicParty);
            Assert.Equal("Act 1 Clear", PartyRoster.PublicDescription(publicParty));
            var partyInfo = Assert.Single(WithId(alphaMsgs, InstanceSocial.PartyInfoId));
            var pr = new LoginReader(partyInfo);
            Assert.Equal(InstanceSocial.PartyInfoId, pr.U16());
            Assert.Equal(publicParty, pr.U32());
            Assert.Equal("Act 1 Clear", pr.WStr());
            await PartyRoster.SetPublicParty(alpha, "", 0);
            Assert.Equal(0u, PartyRoster.PartyOf(alpha));

            // 3. Social status (0xca) persists and broadcasts in S->C 0xa7 second wstr.
            alphaMsgs.Clear();
            await FriendsFeature.SetStatus(alpha, "Trading maps");
            Assert.Equal("Trading maps", FriendsFeature.StatusOf(accA.Name));
            var known = Assert.Single(WithId(alphaMsgs, InstanceSocial.KnownPlayerId));
            var kr = new LoginReader(known);
            Assert.Equal(InstanceSocial.KnownPlayerId, kr.U16());
            Assert.Equal(accA.Name, kr.WStr());
            Assert.Equal("Trading maps", kr.WStr());

            // 4. Ignore list (0x80 list == 1) persists across stash cache eviction and toggles cleanly.
            await FriendsFeature.Change(alpha, ("SocialBeta", 0x0001));
            Assert.True(FriendsFeature.IsIgnoring(accA.Name, "SocialBeta"));
            Assert.True(FriendsFeature.IsIgnoring(accA.Name, accB.Name));
            AccountStash.Forget(accA.Name);
            Assert.Contains(accB.Name, AccountStash.For(accA.Name, Program.Items).ModuleData("social.ignore") ?? "");
            await FriendsFeature.Change(alpha, ("SocialBeta", 0x0101));
            Assert.False(FriendsFeature.IsIgnoring(accA.Name, accB.Name));

            // 5. Hideout invite (0x179) grants visit access and duel challenge (0x155) sends S->C 0x157.
            Assert.False(FriendsFeature.CanVisitHideout(beta, alpha));
            await FriendsFeature.InviteToHideout(alpha, "SocialBeta");
            Assert.True(FriendsFeature.CanVisitHideout(beta, alpha));
            betaMsgs.Clear();
            await FriendsFeature.ChallengeDuel(alpha, "SocialBeta");
            var duel = Assert.Single(WithId(betaMsgs, InstanceSocial.DuelChallengeNotifyId));
            var dr = new LoginReader(duel);
            Assert.Equal(InstanceSocial.DuelChallengeNotifyId, dr.U16());
            Assert.Equal("SocialAlpha", dr.WStr());
            Assert.Equal((byte)0, dr.U8());

            // 6. Chat flags, chat link readers (0x12/0x13/0x14), Ctrl+right-click trade (0xb4), and EnterPersonal.
            byte[] autoReply = InstanceChat.ChatLine("Away", "@SocialAlpha", InstanceChat.AutoReplyFlag);
            var cr = new LoginReader(autoReply);
            Assert.Equal(InstanceChat.ChatLineId, cr.U16());
            Assert.Equal("@SocialAlpha", cr.WStr());
            Assert.Equal("Away", cr.WStr());
            Assert.Equal("", cr.WStr());
            Assert.Equal(InstanceChat.AutoReplyFlag, cr.U16());
            Assert.Equal((ushort)0x4000, InstanceChat.AutoReplyFlag);
            Assert.Equal((ushort)0x8000, InstanceChat.ForceLocalFlag);
            Assert.Equal((ushort)0x0004, InstanceChat.RedTextFlag);

            byte[] linkItem = new LoginWriter().U16(InstanceChat.LinkItemId).U8(2).U32(0x10000001).U32(77).U8(1).ToArray();
            Assert.Equal(new InstanceChat.LinkItem(2, 1, 77, 1), InstanceChat.ReadLinkItem(linkItem));
            byte[] linkOther = new LoginWriter().U16(InstanceChat.LinkOtherId).U32(42).U8(1).U8(3).ToArray();
            Assert.Equal(new InstanceChat.LinkOther(42, 1, 3), InstanceChat.ReadLinkOther(linkOther));
            byte[] linkAnother = new LoginWriter().U16(InstanceChat.LinkAnotherId).U32(99).U8(4).ToArray();
            Assert.Equal(new InstanceChat.LinkAnother(99, 4), InstanceChat.ReadLinkAnother(linkAnother));

            byte[] ctrlMove = new LoginWriter().U16(InstanceNpcs.TradeCtrlMoveId).U32(55).U32(0x00000001).U8(0x34).U8(0x12).ToArray();
            Assert.Equal(12, InstanceProtocol.KnownClientLength(InstanceNpcs.TradeCtrlMoveId));
            Assert.Equal((55u, 1u, (ushort)0x1234), InstanceNpcs.ReadTradeCtrlMove(ctrlMove));
            foreach (ushort variableId in new[]
            {
                InstanceSocial.PublicPartyId, InstanceSocial.SocialStatusId,
                InstanceSocial.DuelChallengeId, InstanceSocial.HideoutInviteId,
            })
                Assert.NotNull(ClientPackets.Find(variableId));

            // 7. Retail slash command helpers (/remaining and /whois) work against post-1040 Player/GameArea state.
            Assert.Equal("0 monsters remain.", CommandsFeature.DescribeRemaining(alpha));
            Assert.Contains("SocialAlpha", CommandsFeature.DescribeWhois("SocialAlpha"));
            Assert.Contains("Lioneye's Watch", CommandsFeature.DescribeWhois("SocialAlpha"));

            var registry = new InstanceRegistry { PersonalAreas = new HashSet<ushort> { 0xabcd } };
            var guestInst = registry.EnterPersonal(0xabcd, "SocialBeta", "SocialAlpha", "Standard", solo: false);
            Assert.NotNull(guestInst);
            Assert.Equal("SocialAlpha", guestInst!.Owner);
            Assert.Same(guestInst, registry.Enter(0xabcd, "SocialAlpha", "Standard"));
            Assert.Null(registry.EnterPersonal(0xabcd, "SocialBeta", "SocialAlpha", "SSF Standard", solo: true));
        }
        finally
        {
            foreach (var c in new[] { alpha, beta })
            {
                await PartyRoster.LeaveHuman(c, PartyRoster.PartyOf(c));
                c.Closed = true;
                Program.InGame.TryRemove(c, out _);
            }
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    [Fact]
    public void Login_change_password_rename_character_and_death_counter_persist_across_saves()
    {
        string folder = Directory.CreateTempSubdirectory("localexile-login-actions-").FullName;
        string path = Path.Combine(folder, "characters.db");
        try
        {
            Saves.Open(path);
            var store = new SqliteAccountStore(Saves.Db);
            var accounts = new AccountService(store);
            var acc = accounts.Register("loginuser", "oldpw123").Value!;
            accounts.CreateCharacter(acc.Id, "OldExile", CharacterClass.Witch);
            accounts.CreateCharacter(acc.Id, "TakenName", CharacterClass.Ranger);
            CharacterSaves.Write("OldExile", new([], Experience: 777));

            // Death counter persists distinct epochs in CharacterSaves.ModuleData without double-counting.
            var deaths = new CharacterDeaths();
            var player = new Player(new(0x235c, "1_1_town", 60, 60, 10, 10, null, []), 1, 1)
                { Character = { Account = acc.Name, Name = "OldExile" } };
            Assert.Equal("You have died 0 times.", CharacterDeaths.Describe(deaths.Count(player)));
            player.DeathNotificationEpoch = 1;
            Assert.Equal("You have died 1 time.", CharacterDeaths.Describe(deaths.Count(player)));
            Assert.Equal(1, deaths.Count(player)); // Same epoch is not double-counted.
            player.DeathNotificationEpoch = 2;
            Assert.Equal("You have died 2 times.", CharacterDeaths.Describe(deaths.Count(player)));

            // Login with Remember Password (Flag94) to verify credential invalidation on 0x0a password change.
            byte[] machineA = Enumerable.Repeat((byte)3, 32).ToArray();
            byte[] machineB = Enumerable.Repeat((byte)4, 32).ToArray();
            var session = new LoginServerSession(accounts);
            var loginReplies = session.Handle(new AccountLogin(2, "loginuser", machineA,
                AccountService.ClientPasswordDigest("oldpw123"), machineB, true, false, 0x40, 0));
            var loginOk = Assert.IsType<LoginResult>(loginReplies[0]);
            Assert.Equal(LoginServerSession.ResultOk, loginOk.Result);

            // Change password (0x0a -> 0x0b): wrong old digest and empty new digest fail; valid digest succeeds.
            byte[] oldDigest = AccountService.ClientPasswordDigest("oldpw123");
            byte[] newDigest = AccountService.ClientPasswordDigest("newpw456");
            var badOld = Assert.IsType<U16Message>(Assert.Single(session.Handle(
                new TwoDigests(AccountService.ClientPasswordDigest("wrong"), newDigest))));
            Assert.Equal((LoginServerSession.ChangePasswordResultId, LoginServerSession.ResultInvalidPassword), (badOld.MessageId, badOld.Value));
            var emptyNew = Assert.IsType<U16Message>(Assert.Single(session.Handle(new TwoDigests(oldDigest, new byte[32]))));
            Assert.Equal(LoginServerSession.ResultInvalidPassword, emptyNew.Value);

            var changed = Assert.IsType<U16Message>(Assert.Single(session.Handle(new TwoDigests(oldDigest, newDigest))));
            Assert.Equal((LoginServerSession.ChangePasswordResultId, LoginServerSession.ResultOk), (changed.MessageId, changed.Value));
            Assert.Equal(LoginServerSession.ResultInvalidPassword, Assert.IsType<LoginResult>(
                new LoginServerSession(accounts).Handle(new AccountLogin(2, "loginuser", machineA, oldDigest, machineB, false, false, 0x40, 0))[0]).Result);
            Assert.Equal(LoginServerSession.ResultInvalidPassword, Assert.IsType<LoginResult>(
                new LoginServerSession(accounts).Handle(new AccountLogin(2, "loginuser", machineA, loginOk.Digest, machineB, false, true, 0x40, 0))[0]).Result);
            Assert.Equal(LoginServerSession.ResultOk, Assert.IsType<LoginResult>(
                new LoginServerSession(accounts).Handle(new AccountLogin(2, "loginuser", machineA, newDigest, machineB, false, false, 0x40, 0))[0]).Result);

            // Rename character (0x3d -> 0x15 + 0x3e): validation errors and atomic save rename.
            var tooShort = Assert.IsType<U16Bool>(Assert.Single(session.Handle(new TwoWStringsBool("OldExile", "ab", false))));
            Assert.Equal((LoginServerSession.ResultCharacterNameTooShort, false), (tooShort.Value, tooShort.Flag));
            var taken = Assert.IsType<U16Bool>(Assert.Single(session.Handle(new TwoWStringsBool("OldExile", "TakenName", true))));
            Assert.Equal((LoginServerSession.ResultCharacterNameTaken, true), (taken.Value, taken.Flag));
            var missing = Assert.IsType<U16Bool>(Assert.Single(session.Handle(new TwoWStringsBool("NoSuchChar", "FreshName", false))));
            Assert.Equal(LoginServerSession.ResultCharacterDoesNotExist, missing.Value);

            var renamed = session.Handle(new TwoWStringsBool("OldExile", "NewExile", true));
            Assert.Equal(2, renamed.Count);
            var list = Assert.IsType<List15>(renamed[0]);
            var result = Assert.IsType<U16Bool>(renamed[1]);
            Assert.Equal((LoginServerSession.ResultOk, true), (result.Value, result.Flag));
            Assert.Contains(list.Entries, e => e.Name == "NewExile");
            Assert.DoesNotContain(list.Entries, e => e.Name == "OldExile");

            var savedAfter = CharacterSaves.Load("NewExile");
            Assert.NotNull(savedAfter);
            Assert.Equal(777u, savedAfter!.Experience);
            Assert.Equal("2", CharacterSaves.ModuleData("NewExile", CharacterDeaths.SaveKey));
            Assert.Null(CharacterSaves.Load("OldExile"));
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
