using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Social;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class CosmeticsSocialAndLoginTests
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

    static ushort PacketId(byte[] m) => BinaryPrimitives.ReadUInt16BigEndian(m);
    static List<byte[]> WithId(List<byte[]> messages, ushort id) => [.. messages.Where(m => m.Length >= 2 && PacketId(m) == id)];

    [Fact]
    public void Tiered_cosmetics_incremental_deltas_mannequins_and_hideout_in_use_round_trip()
    {
        // 1. Tiered MTX bytes in VisibleItem.Write: skin tier after SkinHash (+0x38) and effect tier after EffectHash (+0x60).
        var visible = new VisibleItem(Slot: 1, VisualIdentity: 0x1234)
        {
            SkinVisual = 0x2345,
            SkinHash = 0x55667788u,
            SkinTier = 2,
            EffectVisual = 0x3456,
            EffectHash = 0x99aabbccu,
            EffectTier = 3,
        };
        var vw = new BlobWriter();
        visible.Write(vw);
        byte[] vb = vw.ToArray();
        Assert.Equal(20, vb.Length);
        Assert.Equal((byte)1, vb[0]);                                            // Slot
        Assert.Equal((ushort)0x2345, BinaryPrimitives.ReadUInt16LittleEndian(vb.AsSpan(2)));
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32LittleEndian(vb.AsSpan(4)));
        Assert.Equal((byte)2, vb[8]);                                            // SkinTier (1-based)
        Assert.Equal((ushort)0x3456, BinaryPrimitives.ReadUInt16LittleEndian(vb.AsSpan(9)));
        Assert.Equal(0x99aabbccu, BinaryPrimitives.ReadUInt32LittleEndian(vb.AsSpan(11)));
        Assert.Equal((byte)3, vb[15]);                                           // EffectTier (1-based)

        // 2. Incremental MTX deltas (0x01ff-0x0205, 0x020c, 0x0210-0x0211) & C->S readers (0x01f7-0x020f).
        var worn = new InstanceMicrotransactions.Equipped(3, 123, 0x12345678u, 0x0102030405060708ul, 2);
        var extra = new InstanceMicrotransactions.ExtraSlot(7, 456, 0x87654321u, 0x1122334455667788ul, 1);
        var snapshot = InstanceMicrotransactions.Purchased([], [worn], [], [extra], 0x07);
        var parsed = InstanceMicrotransactions.ReadPurchased(snapshot);
        Assert.Single(parsed.Equipped);
        Assert.Equal((byte)2, parsed.Equipped[0].Option);
        Assert.NotNull(parsed.Extras);
        Assert.Single(parsed.Extras);
        Assert.Equal((7u, 456u, (byte)1), (parsed.Extras[0].Key, parsed.Extras[0].Id, parsed.Extras[0].Option));
        Assert.Equal((byte)0x07, parsed.Flags);

        Assert.Equal(InstanceMicrotransactions.WornSlotUpdateId, PacketId(InstanceMicrotransactions.WornSlotUpdate(worn)));
        Assert.Equal(InstanceMicrotransactions.ExtraSlotUpdateId, PacketId(InstanceMicrotransactions.ExtraSlotUpdate(extra)));
        Assert.Equal(InstanceMicrotransactions.ExtraSlotRemoveId, PacketId(InstanceMicrotransactions.ExtraSlotRemove(7)));
        byte[] upsertDefault = InstanceMicrotransactions.RecordUpsert(new InstanceMicrotransactions.Record(0x10u, 1));
        Assert.Equal(InstanceMicrotransactions.RecordUpsertId, PacketId(upsertDefault));
        Assert.Equal((byte)1, upsertDefault[^1]); // Default justReceived = true preserved for local shop grants.
        byte[] upsertSilent = InstanceMicrotransactions.RecordUpsert(new InstanceMicrotransactions.Record(0x10u, 1), justReceived: false);
        Assert.Equal((byte)0, upsertSilent[^1]);
        Assert.Equal(InstanceMicrotransactions.RecordRemoveId, PacketId(InstanceMicrotransactions.RecordRemove(1)));
        Assert.Equal(InstanceMicrotransactions.ReclaimQueryResultId, PacketId(InstanceMicrotransactions.ReclaimQueryEmpty()));
        Assert.Equal(InstanceMicrotransactions.RecordStatUpdateId, PacketId(InstanceMicrotransactions.RecordStatUpdate(42, 7)));
        Assert.Equal(InstanceMicrotransactions.RecordStatResetId, PacketId(InstanceMicrotransactions.RecordStatReset(42)));

        byte[] setFlag = new LoginWriter().U16(InstanceMicrotransactions.DisplayFlag1Id).U8(1).ToArray();
        Assert.True(InstanceMicrotransactions.ReadDisplayFlag(setFlag));
        Assert.Equal(3, ClientPackets.Length(InstanceMicrotransactions.DisplayFlag1Id, setFlag));

        byte[] setTier = new LoginWriter().U16(InstanceMicrotransactions.SlotTierId).U8(3).U8(2).ToArray();
        Assert.Equal(((byte)3, (byte)2), InstanceMicrotransactions.ReadSlotTier(setTier));
        Assert.Equal(4, ClientPackets.Length(InstanceMicrotransactions.SlotTierId, setTier));

        byte[] extraTier = new LoginWriter().U16(InstanceMicrotransactions.ExtraTierId).RawU32(7).U8(2).ToArray();
        Assert.Equal((7u, (byte)2), InstanceMicrotransactions.ReadExtraTier(extraTier));
        Assert.Equal(7, ClientPackets.Length(InstanceMicrotransactions.ExtraTierId, extraTier));

        // 3. Mannequin S->C 0x006a-0x006f & C->S 0x005f-0x0066 framing + readers + 0x7b kind 0x47.
        byte[] targetSnap = InstanceMicrotransactions.TargetSnapshot(99, [], [worn], [extra], 0x05);
        Assert.Equal(InstanceMicrotransactions.TargetSnapshotId, PacketId(targetSnap));
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32BigEndian(targetSnap.AsSpan(2)));
        Assert.Equal(InstanceMicrotransactions.TargetWornSlotUpdateId, PacketId(InstanceMicrotransactions.TargetWornSlotUpdate(99, worn)));
        Assert.Equal(InstanceMicrotransactions.TargetExtraSlotUpdateId, PacketId(InstanceMicrotransactions.TargetExtraSlotUpdate(99, extra)));
        Assert.Equal(InstanceMicrotransactions.TargetExtraSlotRemoveId, PacketId(InstanceMicrotransactions.TargetExtraSlotRemove(99, 7)));
        Assert.Equal(InstanceMicrotransactions.TargetDisplayFlagsId, PacketId(InstanceMicrotransactions.TargetDisplayFlags(99, 0x05)));
        Assert.Equal(InstanceMicrotransactions.TargetWeaponClassesId, PacketId(InstanceMicrotransactions.TargetWeaponClasses(99, 12, 13)));

        byte[] targetPick = new LoginWriter().U16(InstanceMicrotransactions.TargetSlotId).U32(99).U8(3).U32(123).U8(2).ToArray();
        Assert.Equal((99u, (byte)3, 123u, (byte)2), InstanceMicrotransactions.ReadTargetSlot(targetPick));
        Assert.Equal(12, ClientPackets.Length(InstanceMicrotransactions.TargetSlotId, targetPick));

        byte[] openUi = InstanceWaypoints.OpenUi(InstanceMicrotransactions.CosmeticsEquipScreenUi, 99);
        Assert.Equal(InstanceMicrotransactions.CosmeticsEquipScreenUi, openUi[2]);
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32BigEndian(openUi.AsSpan(3)));

        // 4. Hideout S->C 0x0214 in-use decoration record bits.
        byte[] inUse = InstanceHideoutEditing.InUseDecorations(0x1234, inUse: true, [10u, 20u]);
        var hr = new LoginReader(inUse);
        Assert.Equal(InstanceHideoutEditing.InUseDecorationsId, hr.U16());
        Assert.Equal((ushort)0x1234, hr.U16());
        Assert.True(hr.Bool());
        Assert.Equal((ushort)2, hr.U16());
        Assert.Equal(10u, hr.U32());
        Assert.Equal(20u, hr.U32());
    }

    [Fact]
    public async Task Public_party_noticeboard_pvp_queue_and_login_migration_round_trip()
    {
        // 1. Public party noticeboard (0xac -> 0xad, 0xa2 -> 0xa7+0xa9, noticeboard 0x95 join) and PvP queue trigger.
        TestEnvironment.OpenOwnSaves("social-noticeboard");
        using var runtime = new PartyFollowerTests.Runtime();
        var instance = new InstanceState();
        using var wire = new PartyFollowerTests.Wire();
        var leader = runtime.PlayerOf(instance, wire, "NoticeLeader");
        var viewer = runtime.PlayerOf(instance, wire, "NoticeViewer");
        var (leaderMsgs, leaderOut) = Capture();
        var (viewerMsgs, viewerOut) = Capture();
        leader.Connection.Outbox = leaderOut;
        viewer.Connection.Outbox = viewerOut;
        leader.Connection.Driven = viewer.Connection.Driven = null;
        try
        {
            await PartyRoster.SetPublicParty(leader, "Act 1 Mapping", 0);
            uint partyId = PartyRoster.PartyOf(leader);
            Assert.NotEqual(0u, partyId);

            viewerMsgs.Clear();
            await PartyRoster.SendPublicParties(viewer, 0);
            var listPkt = Assert.Single(WithId(viewerMsgs, InstanceSocial.PublicPartiesId));
            var lr = new LoginReader(listPkt);
            Assert.Equal(InstanceSocial.PublicPartiesId, lr.U16());
            Assert.Equal((ushort)1, lr.U16());
            Assert.Equal(partyId, lr.U32());
            Assert.Equal("Act 1 Mapping", lr.WStr());
            Assert.Equal((byte)1, lr.U8());

            viewerMsgs.Clear();
            await PartyRoster.InspectPublicParty(viewer, partyId);
            Assert.Single(WithId(viewerMsgs, InstanceSocial.KnownPlayerId));
            var infoPkt = Assert.Single(WithId(viewerMsgs, InstanceSocial.PartyInfoId));
            var ir = new LoginReader(infoPkt);
            Assert.Equal(InstanceSocial.PartyInfoId, ir.U16());
            Assert.Equal(partyId, ir.U32());

            // Noticeboard Join via C->S 0x95 without prior invite.
            await PartyRoster.AcceptHuman(viewer, partyId);
            Assert.True(PartyRoster.SameParty(leader, viewer));

            // PvP duel trigger (0x155 -> S->C 0x118 + 0x157, 0x156 accept -> S->C 0x119).
            leaderMsgs.Clear();
            viewerMsgs.Clear();
            await FriendsFeature.ChallengeDuel(leader, "NoticeViewer");
            Assert.Single(WithId(leaderMsgs, InstancePvp.QueueStatusId));
            Assert.Single(WithId(viewerMsgs, InstanceSocial.DuelChallengeNotifyId));
            leaderMsgs.Clear();
            viewerMsgs.Clear();
            await PvpQueue.RespondToChallenge(viewer, accepted: true);
            Assert.Single(WithId(leaderMsgs, InstancePvp.QueueHideId));
            Assert.Single(WithId(viewerMsgs, InstancePvp.QueueHideId));
        }
        finally
        {
            await PartyRoster.LeaveHuman(viewer, PartyRoster.PartyOf(viewer));
            await PartyRoster.LeaveHuman(leader, PartyRoster.PartyOf(leader));
            TestEnvironment.ReleaseOwnSaves();
        }

        // 2. Login private/past leagues, 0x1c pagination (including limit=0), 0x3f single-character migration, and 0x40 disabled reply.
        var store = new JsonAccountStore();
        var accounts = new AccountService(store);
        var acc = accounts.Register("migrator", "pw1234").Value!;
        var standard = new League("Standard", "Standard", false, false);
        var ssf = new League("SSF Standard", "Standard", false, true, SsfParent: "Standard") { Parent = "Standard", EntryOf = "Standard" };
        var past = new League("LegacyEvent", "LegacyEvent", false, false) { Parent = "Standard", Ended = true, Creatable = false };
        var priv = new League("SecretLeague", "SecretLeague", false, false) { Parent = "Standard", Private = true, Password = "open" };
        var catalog = new LeagueCatalog([standard, ssf, past, priv]);
        Assert.Single(catalog.PastLeagues);
        Assert.Equal("LegacyEvent", catalog.PastLeagues[0].Name);

        accounts.CreateCharacter(acc.Id, "SsfOne", CharacterClass.Witch, "SSF Standard");
        accounts.CreateCharacter(acc.Id, "PastOne", CharacterClass.Ranger, "LegacyEvent");
        accounts.CreateCharacter(acc.Id, "PastTwo", CharacterClass.Shadow, "LegacyEvent");

        var session = new LoginServerSession(accounts) { Leagues = catalog };
        var loginReplies = session.Handle(new AccountLogin(2, "migrator", new byte[32], AccountService.ClientPasswordDigest("pw1234"), new byte[32], false, false, 0x40, 0));
        var leagueList = Assert.IsType<LeagueList>(loginReplies[1]);
        var privEntry = Assert.Single(leagueList.Leagues, l => l.Name == "SecretLeague");
        Assert.True((privEntry.Flags0 & LeagueEntry.Private) != 0);
        Assert.True((privEntry.Flags2 & LeagueEntry.PrivateCounted) != 0);

        // Private league password check on 0x12.
        var badPw = Assert.IsType<CreateCharacterResult>(Assert.Single(session.Handle(new CreateCharacter("PrivFail", "SecretLeague", 0, 0, "IntDefault", new byte[32]))));
        Assert.Equal(LoginServerSession.ResultInvalidPassword, badPw.Result);
        var okPw = session.Handle(new CreateCharacter("PrivOk", "SecretLeague", 0, 0, "IntDefault", AccountService.ClientPasswordDigest("open")));
        Assert.Equal(LoginServerSession.ResultOk, Assert.IsType<CreateCharacterResult>(okPw[1]).Result);

        // C->S 0x1c private league pagination -> S->C 0x1b with Append = true (limit=0 returns empty).
        var emptyPage = Assert.IsType<LeagueList>(Assert.Single(session.Handle(new TwoU16(0, 0))));
        Assert.True(emptyPage.Append);
        Assert.Empty(emptyPage.Leagues);
        var page = Assert.IsType<LeagueList>(Assert.Single(session.Handle(new TwoU16(1, 0))));
        Assert.True(page.Append);
        Assert.Equal("SecretLeague", Assert.Single(page.Leagues).Name);

        // C->S 0x3f single-character migration -> 0x15 + 0x44 result 0.
        var migOne = session.Handle(new WStringMessage(LoginServerSession.MigrateCharacterId, "SsfOne"));
        Assert.Equal(2, migOne.Count);
        Assert.Equal(LoginServerSession.ResultOk, Assert.IsType<U16Message>(migOne[1]).Value);
        Assert.Equal("Standard", accounts.FindCharacter("SsfOne")!.League);

        // C->S 0x40 Migrate All -> 0x44 ActionTemporarilyDisabled (569) without mutating any characters.
        var migAll = session.Handle(new WStringMessage(LoginServerSession.MigrateLeagueAllId, "LegacyEvent"));
        Assert.Equal(LoginServerSession.ResultActionTemporarilyDisabled, Assert.IsType<U16Message>(Assert.Single(migAll)).Value);
        Assert.Equal("LegacyEvent", accounts.FindCharacter("PastOne")!.League);
        Assert.Equal("LegacyEvent", accounts.FindCharacter("PastTwo")!.League);
    }
}
