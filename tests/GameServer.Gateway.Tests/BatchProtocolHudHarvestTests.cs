using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Harvest;
using GameServer.Modules.M_questobjects;
using GameServer.Modules.Social;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class BatchProtocolHudHarvestTests
{
    static readonly byte[] RealOpening = Convert.FromHexString("000F00000000000E76240850BFE2B4956AD1D6B07F000001000000004001");
    static readonly byte[] SampleTick = [0x00, 0x1b, 0x01, 0x00, 0x00, 0x00, 0x02];

    static byte[] BuildFourNarrowStr(ushort opcode, string a, string b, string c, string d)
    {
        var w = new LoginWriter().U16(opcode);
        foreach (string s in new[] { a, b, c, d })
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            w.U16((ushort)utf8.Length).Bytes(utf8);
        }
        return w.ToArray();
    }

    static void AssertSplitAndEveryCut(byte[] message, ushort expectedId)
    {
        byte[] coalesced = [.. message, .. SampleTick];
        var full = InstanceProtocol.SplitClientMessages(coalesced, out byte[] partial, out byte[] unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, full.Count);
        Assert.Equal(expectedId, full[0].Id);
        Assert.Equal(message, full[0].Bytes);
        Assert.Equal(InstanceProtocol.TickId, full[1].Id);
        Assert.Equal(SampleTick, full[1].Bytes);

        for (int cut = 1; cut < message.Length; cut++)
        {
            var first = InstanceProtocol.SplitClientMessages(message.AsSpan(0, cut), out byte[] p1, out byte[] u1);
            Assert.Empty(first);
            Assert.Empty(u1);
            Assert.Equal(cut, p1.Length);

            byte[] recombined = [.. p1, .. message[cut..], .. SampleTick];
            var second = InstanceProtocol.SplitClientMessages(recombined, out byte[] p2, out byte[] u2);
            Assert.Empty(p2);
            Assert.Empty(u2);
            Assert.Equal(2, second.Count);
            Assert.Equal(expectedId, second[0].Id);
            Assert.Equal(message, second[0].Bytes);
            Assert.Equal(InstanceProtocol.TickId, second[1].Id);
        }
    }

    [Fact]
    public void Protocol_framing_opening_siphash_area_hashes_login_migration_and_hud_signals_match_client_contract()
    {
        // 1. Variable-length C->S framing: 0x0130, 0x0138 (4 narrow UTF-8 strings), 0x0279, 0x027f, 0x02a5, and 0xF7E0.
        AssertSplitAndEveryCut(BuildFourNarrowStr(0x0130, "pack_1", "café", "", "EUR"), 0x0130);
        AssertSplitAndEveryCut(BuildFourNarrowStr(0x0138, "order", "item_a", "USD", "note_✓"), 0x0138);
        AssertSplitAndEveryCut([0x02, 0x79, 0x03, 0x0a, 0x0b, 0x0c], 0x0279);
        AssertSplitAndEveryCut([0x02, 0x7f, 0x01, 0x02, 0x05, 0x09], 0x027f);
        byte[] lantern = [0x02, 0xa5, 0x00, 0x00, 0x10, 0x01, 0x02,
            0x01, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x22,
            0x02, 0x00, 0x00, 0x00, 0x33, 0x00, 0x00, 0x00, 0x44];
        AssertSplitAndEveryCut(lantern, 0x02a5);
        byte[] modFrame = [0xf7, 0xe0, 0x00, 0x00, 0x00, 0x05, 0xde, 0xad, 0xbe, 0xef, 0x42];
        AssertSplitAndEveryCut(modFrame, InstanceProtocol.ModEnvelopeId);

        byte[] oversizeMod = [0xf7, 0xe0, 0x00, 0x01, 0x00, 0x01, 0x00];
        var oversize = InstanceProtocol.SplitClientMessages(oversizeMod, out byte[] overPartial, out byte[] overUnknown);
        Assert.Empty(oversize);
        Assert.Empty(overPartial);
        Assert.Equal(oversizeMod, overUnknown);

        // 2. C->S 0x0E SipHash-2-4 digest verification against the live 3.29 capture, per-ticket Data8, tamper and clock wrap.
        var real = InstanceProtocol.TryReadOpening(RealOpening)!;
        Assert.Equal("76240850BFE2B4956AD1D6B0", Convert.ToHexString(InstanceProtocol.ComputeOpeningDigest(new byte[8], 0x6AD1D6B0u)));
        Assert.True(InstanceProtocol.VerifyOpeningDigest(real, new byte[8], nowUnixSeconds: 0x6AD1D6B0u));
        Assert.False(InstanceProtocol.VerifyOpeningDigest(real, new byte[8], nowUnixSeconds: 0x6AD1D6B0u + 301, maxSkewSeconds: 300));
        byte[] tampered = [.. real.Digest];
        tampered[0] ^= 0x01;
        Assert.False(InstanceProtocol.VerifyOpeningDigest(real with { Digest = tampered }, new byte[8]));

        var keyring = InstanceKeyring.Random();
        byte[] data8A = keyring.Data8(42);
        byte[] data8B = keyring.Data8(43);
        Assert.Equal(8, data8A.Length);
        Assert.NotEqual(data8A, data8B);
        Assert.NotEqual(keyring.Key(42)[..8], data8A);

        byte[] transfer27 = InstanceTransfer.ConnectInstance("127.0.0.1", 6112, 42, 0x235c, keyring.Key(42), data8A);
        Assert.Equal(data8A, transfer27[^8..]);

        // uint32 wraparound: clientTime = 10, server now = uint.MaxValue - 5 (delta = +16s, well inside 300s).
        var wrapped = new InstanceOpening(42, InstanceProtocol.ComputeOpeningDigest(data8A, 10u), 0x7F000001u, 0, 0x40, 1);
        Assert.True(InstanceProtocol.VerifyOpeningDigest(wrapped, data8A, nowUnixSeconds: uint.MaxValue - 5L, maxSkewSeconds: 300));
        Assert.False(InstanceProtocol.VerifyOpeningDigest(wrapped, data8B, nowUnixSeconds: uint.MaxValue - 5L, maxSkewSeconds: 300));

        // 3. C->S 0x7d AreaGenerated tile/doodad hash parsing and S->C 0xd3 stash affinities round-trip.
        var (tileHash, doodadHash) = InstanceProtocol.ReadAreaGenerated(Convert.FromHexString("007D43E6886D8EA8DD43"));
        Assert.Equal((1139181677u, 2393431363u), (tileHash, doodadHash));

        var aff = new InstanceStash.SetAffinities(5, InstanceStash.Guild, [(3, true), (7, false)]);
        byte[] affWire = aff.Encode();
        Assert.Equal(InstanceStash.EncodeSetAffinities(5, InstanceStash.Guild, aff.Changes), affWire);
        var affDecoded = InstanceStash.ReadSetAffinities(affWire);
        Assert.Equal((ushort)5, affDecoded.Tab);
        Assert.Equal(InstanceStash.Guild, affDecoded.Kind);
        Assert.Equal([(3, true), (7, false)], affDecoded.Changes);

        // 4. Login C->S 0x19 -> S->C 0x1a LeaguesWithProgress even-length (source, parent) pairs & login 0x14 Data8.
        var standard = new League("Standard", "Standard", false, false);
        var voidLeague = new League(HardcoreDeathRules.VoidLeague, HardcoreDeathRules.VoidLeague, false, false) { Creatable = false };
        var hardcore = new League("Hardcore", "Standard", true, false) { Parent = "Standard" };
        var ruthless = new League("Ruthless", "Standard", false, false, Form: "ruthless") { Parent = "Standard", DataSet = "HardMode" };
        var ssfStandard = new League("SSF Standard", "Standard", false, true) { Parent = "Standard" };
        var endedTemp = new League("EndedTemp", "Standard", false, false) { Parent = "Standard", Ended = true };
        var voidEvent = new League("VoidEvent", "Standard", false, false) { Parent = HardcoreDeathRules.VoidLeague };
        var leagues = new LeagueCatalog([standard, voidLeague, hardcore, ruthless, ssfStandard, endedTemp, voidEvent]);

        var accounts = new AccountService(new JsonAccountStore());
        var acc = accounts.Register("migrator", "pw1234").Value!;
        var unauth = new LoginServerSession(accounts) { Leagues = leagues, Keyring = keyring };
        Assert.Empty(unauth.Handle(new EmptyMessage(0x19)));

        accounts.CreateCharacter(acc.Id, "StdOnly", CharacterClass.Witch, inLeague: "Standard");
        accounts.CreateCharacter(acc.Id, "VoidOnly", CharacterClass.Ranger, inLeague: "VoidEvent");
        accounts.CreateCharacter(acc.Id, "HcOnly", CharacterClass.Duelist, inLeague: "Hardcore");
        var loginReq = new AccountLogin(2, "migrator", new byte[32], AccountService.ClientPasswordDigest("pw1234"), new byte[32], false, false, 0x40, 0);
        var session = new LoginServerSession(accounts) { Leagues = leagues, Keyring = keyring };
        session.Handle(loginReq);
        // Plain Hardcore -> Standard moves on death, never as a manual "Migrate All" pair.
        var emptyProgress = Assert.IsType<LeaguesWithProgress>(Assert.Single(session.Handle(new EmptyMessage(0x19))));
        Assert.Empty(emptyProgress.Leagues);

        // Login 0x14 carries per-ticket Data8 matching keyring.Data8(ticket).
        var info14 = Assert.IsType<InstanceInfo>(Assert.Single(session.Handle(new U8U32(0, 0))));
        Assert.Equal(keyring.Data8(info14.U32_38), info14.Data8);

        accounts.CreateCharacter(acc.Id, "SsfChar", CharacterClass.Shadow, inLeague: "SSF Standard");
        accounts.CreateCharacter(acc.Id, "SsfCharTwo", CharacterClass.Templar, inLeague: "SSF Standard");
        accounts.CreateCharacter(acc.Id, "RuthlessChar", CharacterClass.Scion, inLeague: "Ruthless");
        accounts.CreateCharacter(acc.Id, "EndedChar", CharacterClass.Marauder, inLeague: "EndedTemp");
        var progress = Assert.IsType<LeaguesWithProgress>(Assert.Single(session.Handle(new EmptyMessage(0x19))));
        Assert.Equal(0, progress.Leagues.Count % 2);
        Assert.Equal(["EndedTemp", "Standard", "Ruthless", "Standard", "SSF Standard", "Standard"], progress.Leagues.Select(l => l.Name).ToArray());

        // 5. Achievements (0x124-0x127), Timers (0x162/0x163), Bandit Vote (0xbe), and PvP Queue Bar (0x118/0x119) wire contracts.
        Assert.Equal([0x01, 0x24, 0x00, 0x16], InstanceAchievements.AchievementComplete(22));
        Assert.Equal([0x01, 0x25, 0x03, 0xdd], InstanceAchievements.AchievementItemComplete(989));
        Assert.Equal([0x01, 0x27, 0x03, 0xdd], InstanceAchievements.AchievementItemFailed(989));
        byte[] bitset = new byte[4];
        InstanceAchievements.WriteBitField(bitset, bitOffset: 6, bitWidth: 5, value: 0b10110u);
        Assert.Equal((byte)0b1000_0000, bitset[0]);
        Assert.Equal((byte)0b0000_0101, bitset[1]);
        byte[] prog126 = InstanceAchievements.AchievementProgress(InstanceAchievements.PermanentAchievementSetId, bitset);
        Assert.Equal(11, prog126.Length);
        Assert.Equal(0x0126, BinaryPrimitives.ReadUInt16BigEndian(prog126));
        Assert.Equal(InstanceAchievements.PermanentAchievementSetId, prog126[2]);
        Assert.Equal((ushort)6, BinaryPrimitives.ReadUInt16BigEndian(prog126.AsSpan(3, 2)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(prog126.AsSpan(5, 2)));
        Assert.Equal(bitset, prog126[7..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceAchievements.AchievementProgress(2, new byte[InstanceAchievements.MaxBitsetBytes + 1]));

        Assert.Equal([0x01, 0x63, 0x11, 0x00, 0x00, 0x00, 0x5a], InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Deepwater, 90));
        Assert.Equal([0x01, 0x63, 0x01, 0x00, 0x00, 0x00, 0x00], InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Generic));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceTimers.MissionTimer(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceTimers.MissionTimer(19, 10));
        Assert.Equal([0x01, 0x62, 0x00, 0x00, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00], InstanceTimers.CountdownTimer(5, 1));
        Assert.Equal([0x01, 0x62, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00], InstanceTimers.HideCountdownTimer(1));

        byte[] voteWire = InstanceNpcs.OpenVote(BanditChoices.Oak.VoteType, answered: true, localVoter: true,
            [new("Alice", InstanceNpcs.VoteStateHelpBandit), new("Bob", InstanceNpcs.VoteStateWaitingToEnterArea)]);
        var vr = new LoginReader(voteWire);
        Assert.Equal(InstanceNpcs.VoteDisplayId, vr.U16());
        Assert.Equal((byte)(BanditChoices.Oak.VoteType + 1), vr.U8());
        Assert.True(vr.Bool());
        Assert.True(vr.Bool());
        Assert.Equal((byte)2, vr.U8());
        Assert.Equal("Alice", vr.WStr());
        Assert.Equal(InstanceNpcs.VoteStateHelpBandit, vr.U8());
        Assert.Equal("Bob", vr.WStr());
        Assert.Equal(InstanceNpcs.VoteStateWaitingToEnterArea, vr.U8());
        Assert.Equal(voteWire.Length, vr.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceNpcs.OpenVote(0, false, true,
            Enumerable.Range(0, 256).Select(i => new InstanceNpcs.VoteEntry($"P{i}", InstanceNpcs.VoteStateWaitingForVote)).ToArray()));

        byte[] pvpWire = InstancePvp.QueueStatus(3, "Sarn Arena", isChallenge: true, showWithdraw: false, nextMatchTimeUnix: 0x0102030405060708UL);
        var pr = new LoginReader(pvpWire);
        Assert.Equal(InstancePvp.QueueStatusId, pr.U16());
        Assert.Equal((byte)3, pr.U8());
        Assert.Equal("Sarn Arena", pr.WStr());
        Assert.True(pr.Bool());
        Assert.False(pr.Bool());
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(pr.Fixed(8)));
        Assert.Equal(pvpWire.Length, pr.Position);
        byte[] hideA = InstancePvp.QueueHide();
        byte[] hideB = InstancePvp.QueueHide();
        Assert.Equal([0x01, 0x19], hideA);
        Assert.NotSame(hideA, hideB);
    }

    [Fact]
    public async Task Achievements_bandit_party_votes_pvp_queue_and_harvest_recipes_execute_end_to_end()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        Game.ResetAchievementCache();
        try
        {
            // 1. Permanent Achievements: bit packing, persistence, idempotency, SC/HC gating, multi-item parent completion, 0x127 fail, 0x126 sync.
            var achOutbox = new List<byte[]>();
            var player = new Player(fixture.Player.Layout, 1, 1)
            {
                Character =
                {
                    Account = "ach-acct-" + Guid.NewGuid().ToString("N"),
                    Name = "AchChar" + Guid.NewGuid().ToString("N")[..8],
                },
                Instance = new(),
                PlayerLife = 1000,
                Connection =
                {
                    PlayerSent = true,
                    Outbox = (bytes, _) => { achOutbox.Add([.. bytes]); return Task.CompletedTask; },
                },
                LeagueModule = fixture.Player.LeagueModule,
            };
            player.Clock.Start();

            Assert.True(await Game.AwardAchievementItem(player, "KillBrutus"));
            Assert.False(await Game.AwardAchievementItem(player, "KillBrutus")); // idempotent
            Assert.Equal([InstanceAchievements.AchievementItemCompleteId, InstanceAchievements.AchievementProgressId, InstanceAchievements.AchievementCompleteId],
                achOutbox.Select(p => BinaryPrimitives.ReadUInt16BigEndian(p)).ToArray());
            Assert.Equal((ushort)989, BinaryPrimitives.ReadUInt16BigEndian(achOutbox[0].AsSpan(2, 2)));
            Assert.Equal((ushort)22, BinaryPrimitives.ReadUInt16BigEndian(achOutbox[2].AsSpan(2, 2)));
            byte[] packed = Game.BuildAchievementProgressBitset(player);
            Assert.Equal(26, packed.Length);
            Assert.NotEqual(0, packed[6 >> 3] & (1 << (6 & 7))); // KillBrutus bitOffset = 6

            // SC/HC gating on softcore connection: Level60HC refused, Level60 awarded.
            Assert.False(await Game.AwardAchievementItem(player, "Level60HC"));
            Assert.True(await Game.AwardAchievementItem(player, "Level60"));

            // Multi-item achievement SideWithBandits (parent row 56): only completes when all 3 items are done.
            achOutbox.Clear();
            Assert.True(await Game.AwardAchievementItem(player, "SideWithAlira"));
            Assert.DoesNotContain(achOutbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementCompleteId);
            Assert.True(await Game.AwardAchievementItem(player, "SideWithKraityn"));
            Assert.DoesNotContain(achOutbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementCompleteId);
            Assert.True(await Game.AwardAchievementItem(player, "SideWithOak"));
            Assert.Contains(achOutbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementCompleteId
                && BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2)) == 56);

            achOutbox.Clear();
            await Game.FailAchievementItem(player, "KillBrutus");
            await Game.SendAchievementProgress(player);
            Assert.Equal([InstanceAchievements.AchievementItemFailedId, InstanceAchievements.AchievementProgressId],
                achOutbox.Select(p => BinaryPrimitives.ReadUInt16BigEndian(p)).ToArray());

            // Quest flag mappings: a8q23-KilledBosses (not a8q1-KilledBoss) awards KillLunarisSolaris;
            // a10q2-KilledBoss does NOT award CompleteAct10 (EndGameAvailable does).
            await Game.CheckQuestFlagAchievements(player, ["a8q1-KilledBoss", "a10q2-KilledBoss"]);
            Assert.DoesNotContain("KillLunarisSolaris", Game.CompletedAchievementItems(player));
            Assert.DoesNotContain("CompleteAct10", Game.CompletedAchievementItems(player));
            await Game.CheckQuestFlagAchievements(player, ["a8q23-KilledBosses", Quests.EndGameAvailable]);
            Assert.Contains("KillLunarisSolaris", Game.CompletedAchievementItems(player));
            Assert.Contains("CompleteAct10", Game.CompletedAchievementItems(player));

            // 2. Multi-player Party Bandit Votes (S->C 0xbe / C->S 0xbd): WaitingToEnterArea, partial answer, OnAreaGenerated, OnPlayerLeaving, mixed vs unanimous resolution.
            using var questReg = Quests.Register("bandit-party-test", QuestObjectsModule.Acts());
            var oak = BanditChoices.Oak;
            var oakLayout = new AreaLayout(1, oak.Area, 300, 300, 50, 50, (_, _) => true, []);
            var townLayout = new AreaLayout(2, "1_2_town", 300, 300, 50, 50, (_, _) => true, []);
            var voteLeague = new LeagueRuntime(new League("bandit-party-test", "Standard", false, false),
                new HashSet<string>(["bandit-party-test", "Game"]));

            Player MakeVoter(string name, AreaLayout layout, InstanceState inst, List<byte[]> log)
            {
                var c = new Player(layout, 1, 1)
                {
                    Character = { Name = name },
                    Instance = inst,
                    PlayerLife = 1000,
                    Connection =
                    {
                        PlayerSent = true,
                        Outbox = (bytes, _) => { log.Add([.. bytes]); return Task.CompletedTask; },
                    },
                    LeagueModule = voteLeague,
                };
                c.Clock.Start();
                inst.AddPlayer(c);
                return c;
            }

            void SpawnOak(InstanceState inst, Player host, uint leaderId)
            {
                var variety = Game.Monsters.Find(oak.Variety)!;
                var monster = new SpawnedMonster(oak.Name, host.NewMonsterWalk(leaderId, 52, 50, 0, 0, unique: true), 20, 8, 500)
                {
                    Hostile = true,
                    Dead = false,
                    Aggro = false,
                    Life = 500,
                    Variety = variety,
                    Level = 20,
                };
                inst.World.Area.Monsters[leaderId] = monster;
                inst.World.MonsterOrigins[leaderId] = new(0, new MonsterObject(52, 50, 500, ["Positioned", "NPC", "Life"]) { HasDialogue = true }, 0, 20, MonsterRarity.Unique);
            }

            var oakNpc = Game.Npcs.Find(oak.Variety)!;
            var voteTalk = Game.Npcs.TalksOf(oakNpc).First(t => t.Script.Contains("BanditCallVote", StringComparison.Ordinal));
            var speaker = new SpawnedNpc(oakNpc, 52, 50);

            // Mixed vote: Alice Help + Bob Kill + Carol enters then leaves -> triggers BanditFight and closes dialogue.
            var mixedInstance = new InstanceState();
            var townInstance = new InstanceState();
            List<byte[]> aliceLog = [], bobLog = [], carolLog = [];
            var alice = MakeVoter("VoteAlice" + Guid.NewGuid().ToString("N")[..6], oakLayout, mixedInstance, aliceLog);
            var bob = MakeVoter("VoteBob" + Guid.NewGuid().ToString("N")[..6], oakLayout, mixedInstance, bobLog);
            string carolName = "VoteCarol" + Guid.NewGuid().ToString("N")[..6];
            var carolTown = MakeVoter(carolName, townLayout, townInstance, carolLog);
            Player activeCarol = carolTown;
            SpawnOak(mixedInstance, alice, 7001);

            using (BanditChoices.RegisterPartyMembers(_ => [alice, bob, activeCarol]))
            {
                Assert.True(await BanditChoices.OpenVote(alice, 7001, speaker, voteTalk));
                byte[] aliceOpen = aliceLog.Last(p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceNpcs.VoteDisplayId);
                var openReader = new LoginReader(aliceOpen);
                Assert.Equal(InstanceNpcs.VoteDisplayId, openReader.U16());
                Assert.Equal((byte)(oak.VoteType + 1), openReader.U8());
                Assert.False(openReader.Bool()); // not yet answered
                Assert.True(openReader.Bool());  // local voter
                Assert.Equal((byte)3, openReader.U8());
                var initialStates = new Dictionary<string, byte>();
                for (int i = 0; i < 3; i++) initialStates[openReader.WStr()] = openReader.U8();
                Assert.Equal(InstanceNpcs.VoteStateWaitingForVote, initialStates[alice.Character.Name!]);
                Assert.Equal(InstanceNpcs.VoteStateWaitingForVote, initialStates[bob.Character.Name!]);
                Assert.Equal(InstanceNpcs.VoteStateWaitingToEnterArea, initialStates[carolName]);

                // Alice votes Help -> still waiting for Bob and Carol.
                await BanditChoices.VoteAnswer(alice, help: true);
                Assert.False(mixedInstance.HasState("QuestObjects.BanditFight"));
                byte[] afterAlice = aliceLog.Last(p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceNpcs.VoteDisplayId);
                Assert.Equal((byte)1, afterAlice[3]); // answered == true for Alice

                // Area-transfer socket close (LeavingArea == true + "disconnected") must NOT evict Carol's WaitingToEnterArea slot.
                carolTown.LeavingArea = true;
                await BanditChoices.OnPlayerLeaving(carolTown, "disconnected");
                Assert.False(mixedInstance.HasState("QuestObjects.BanditFight"));

                // Carol arrives in the bandit area -> transitions to WaitingForVote.
                var carolInOak = MakeVoter(carolName, oakLayout, mixedInstance, carolLog);
                activeCarol = carolInOak;
                await BanditChoices.OnAreaGenerated(carolInOak);
                byte[] carolOpen = carolLog.Last(p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceNpcs.VoteDisplayId);
                Assert.Equal((byte)0, carolOpen[3]); // answered == false
                Assert.Equal((byte)1, carolOpen[4]); // localVoter == true

                // Bob votes Kill, then Carol leaves -> remaining eligible voters have all voted -> resolves to Fight!
                await BanditChoices.VoteAnswer(bob, help: false);
                Assert.False(mixedInstance.HasState("QuestObjects.BanditFight"));
                await BanditChoices.OnPlayerLeaving(carolInOak, "left the area");
                Assert.True(mixedInstance.HasState("QuestObjects.BanditFight"));
                Assert.Contains(oak.Ally, CharacterSaves.Load(alice.Character.Name!)?.QuestFlags ?? []);
                Assert.DoesNotContain(oak.Ally, CharacterSaves.Load(bob.Character.Name!)?.QuestFlags ?? []);
                Assert.Equal(InstanceNpcs.CloseVote(), aliceLog.Last(p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceNpcs.VoteDisplayId));
                Assert.Contains(bobLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceEntities.ComponentUpdateId);

                // Mixed-vote kill credit guard: allied Alice never receives oak.Killed or Oak's amulet when Bob kills Oak.
                Assert.Empty(BanditChoices.FilterKillFlags(alice, [oak.Killed]));
                Assert.Equal([oak.Killed], BanditChoices.FilterKillFlags(bob, [oak.Killed]));
                Assert.Empty(BanditChoices.FilterKillDrops(alice, oak.Variety, [BanditChoices.AmuletId(oak)]));
                Assert.Equal([BanditChoices.AmuletId(oak)], BanditChoices.FilterKillDrops(bob, oak.Variety, [BanditChoices.AmuletId(oak)]));
            }

            // Party leave/kick resolves an open vote blocked on a WaitingToEnterArea member without deadlock.
            var kickInstance = new InstanceState();
            var kickTownInstance = new InstanceState();
            List<byte[]> k1Log = [], k2Log = [];
            var k1 = MakeVoter("KickHost" + Guid.NewGuid().ToString("N")[..6], oakLayout, kickInstance, k1Log);
            var k2Town = MakeVoter("KickAway" + Guid.NewGuid().ToString("N")[..6], townLayout, kickTownInstance, k2Log);
            SpawnOak(kickInstance, k1, 7003);
            Player[] kickParty = [k1, k2Town];
            using (BanditChoices.RegisterPartyMembers(_ => kickParty))
            {
                Assert.True(await BanditChoices.OpenVote(k1, 7003, speaker, voteTalk));
                await BanditChoices.VoteAnswer(k1, help: true);
                Assert.DoesNotContain(oak.Ally, CharacterSaves.Load(k1.Character.Name!)?.QuestFlags ?? []);
                kickParty = [k1];
                await BanditChoices.OnPartyChanged(k2Town);
                Assert.Contains(oak.Ally, CharacterSaves.Load(k1.Character.Name!)?.QuestFlags ?? []);
                Assert.Equal(InstanceNpcs.CloseVote(), k1Log.Last(p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceNpcs.VoteDisplayId));
            }

            // Unanimous Help vote leaves BanditFight unset and grants both voters Oak's Ally flag.
            var helpInstance = new InstanceState();
            List<byte[]> h1Log = [], h2Log = [];
            var h1 = MakeVoter("HelpOne" + Guid.NewGuid().ToString("N")[..6], oakLayout, helpInstance, h1Log);
            var h2 = MakeVoter("HelpTwo" + Guid.NewGuid().ToString("N")[..6], oakLayout, helpInstance, h2Log);
            SpawnOak(helpInstance, h1, 7002);
            using (BanditChoices.RegisterPartyMembers(_ => [h1, h2]))
            {
                Assert.True(await BanditChoices.OpenVote(h1, 7002, speaker, voteTalk));
                await BanditChoices.VoteAnswer(h1, help: true);
                await BanditChoices.VoteAnswer(h2, help: true);
                Assert.False(helpInstance.HasState("QuestObjects.BanditFight"));
                Assert.Contains(oak.Ally, CharacterSaves.Load(h1.Character.Name!)?.QuestFlags ?? []);
                Assert.Contains(oak.Ally, CharacterSaves.Load(h2.Character.Name!)?.QuestFlags ?? []);
            }

            // 3. PvP Queue Bar (S->C 0x118/0x119, C->S 0x11a/0x18d): Enqueue, Leave(withdraw), unqueued Leave, and Leaving cleanup.
            aliceLog.Clear();
            bobLog.Clear();
            await PvpQueue.Enqueue(alice, "Sarn Arena", otherPlayers: 1, isChallenge: true, showWithdraw: true, nextMatchTimeUnix: 1700000000UL, opponent: bob);
            await PvpQueue.Enqueue(bob, "Sarn Arena", otherPlayers: 1, isChallenge: true, showWithdraw: false, nextMatchTimeUnix: 1700000000UL, opponent: alice);
            Assert.True(PvpQueue.IsQueued(alice));
            Assert.Equal("Sarn Arena", PvpQueue.StatusOf(alice)!.Value.QueueName);
            Assert.Contains(aliceLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstancePvp.QueueStatusId);

            aliceLog.Clear();
            bobLog.Clear();
            await PvpQueue.Leave(alice, withdraw: true);
            Assert.False(PvpQueue.IsQueued(alice));
            Assert.False(PvpQueue.IsQueued(bob));
            Assert.Contains(aliceLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstancePvp.QueueHideId);
            Assert.Contains(bobLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstancePvp.QueueHideId);
            Assert.Contains(bobLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == 0x0017);

            // Unqueued Leave still emits 0x119 so stale bars close cleanly.
            aliceLog.Clear();
            await PvpQueue.Leave(alice, withdraw: false);
            Assert.Single(aliceLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstancePvp.QueueHideId);

            // Leaving notifies linked opponent.
            bobLog.Clear();
            await PvpQueue.Enqueue(alice, "Duel", isChallenge: true, opponent: bob);
            await PvpQueue.Enqueue(bob, "Duel", isChallenge: true, opponent: alice);
            bobLog.Clear();
            await PvpQueue.Leaving(alice);
            Assert.False(PvpQueue.IsQueued(bob));
            Assert.Contains(bobLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstancePvp.QueueHideId);
            Assert.Contains(bobLog, p => BinaryPrimitives.ReadUInt16BigEndian(p) == 0x0017);

            // 4. Harvest Horticrafting Station (all 74/74 HarvestCraftOptions rows supported) & Facetor's Lens end-to-end.
            var catalog = Program.Items;
            var rolls = Game.RollsFor(player);
            var harvest = new HarvestCrafting(catalog, rolls, hardMode: false);
            var ruthlessHarvest = new HarvestCrafting(catalog, rolls, hardMode: true);
            Assert.Equal(74, harvest.All.Count);
            Assert.All(harvest.All, r => Assert.True(r.Supported, $"Recipe {r.Id} must be supported."));
            HarvestCrafting.Recipe RecipeById(HarvestCrafting h, string id) => h.All.Single(r => r.Id == id);
            var rng = new Random(1337);

            // Quality enchants: LifeBodyEnchant on Body Armour, WeaponRangeEnchant refuses Bow and succeeds on One Hand Sword.
            var plate = ItemCatalog.Create(catalog.Find("Plate Vest")!) with { ItemLevel = 70, Rarity = ItemRarity.Rare };
            var enchantedBody = harvest.ApplyHarvest(RecipeById(harvest, "LifeBodyEnchant").Hash, plate, rng);
            Assert.NotNull(enchantedBody?.Item);
            Assert.Contains(enchantedBody!.Item!.Mods, m => m.Section == ModSection.Enchant
                && catalog.Mods.ByHash(m.Hash)?.Id == "HarvestAlternateArmourQualityIncreasedLife");
            var bow = ItemCatalog.Create(catalog.Find("Crude Bow")!) with { ItemLevel = 70 };
            var sword = ItemCatalog.Create(catalog.Find("Rusted Sword")!) with { ItemLevel = 70 };
            var rangeRecipe = RecipeById(harvest, "WeaponRangeEnchant");
            Assert.Null(harvest.ApplyHarvest(rangeRecipe.Hash, bow, rng));
            Assert.NotNull(harvest.ApplyHarvest(rangeRecipe.Hash, sword, rng)?.Item);

            // Essence exchange: stack-proportional cost, tier preservation, corrupted tier 8 supported, Remnant of Corruption refused.
            var essenceRecipe = RecipeById(harvest, "RerollEssences");
            var screaming = ItemCatalog.Create(catalog.Find("Screaming Essence of Greed")!) with { StackSize = 3 };
            var swappedEssence = harvest.ApplyHarvest(essenceRecipe.Hash, screaming, rng);
            Assert.NotNull(swappedEssence?.Item);
            Assert.NotEqual(screaming.TypeHash, swappedEssence!.Item!.TypeHash);
            Assert.Equal((ushort)3, swappedEssence.Item.StackSize);
            Assert.Equal(essenceRecipe.Costs[0].Amount * 3, swappedEssence.Costs[0].Amount);
            Assert.Null(harvest.ApplyHarvest(essenceRecipe.Hash, ItemCatalog.Create(catalog.Find("Remnant of Corruption")!), rng));
            var insanity = ItemCatalog.Create(catalog.Find("Essence of Insanity")!);
            Assert.NotNull(harvest.ApplyHarvest(essenceRecipe.Hash, insanity, rng)?.Item);

            // ExchangeSet: Timeless Emblem 100x basetype cost scaling, Timeless Splinter stack scaling (25x), Tainted Oil/Catalyst refused,
            // and RerollFossils refuses stacks > 10 (set minimum MaxStack) without destroying fossils.
            var timelessRecipe = RecipeById(harvest, "RerollTimelessSplinters");
            var karuiEmblem = ItemCatalog.Create(catalog.Find("Timeless Karui Emblem")!);
            var swappedEmblem = harvest.ApplyHarvest(timelessRecipe.Hash, karuiEmblem, rng);
            Assert.NotNull(swappedEmblem?.Item);
            Assert.NotEqual(karuiEmblem.TypeHash, swappedEmblem!.Item!.TypeHash);
            Assert.Equal((ushort)1, swappedEmblem.Item.StackSize);
            Assert.Equal(timelessRecipe.Costs[0].Amount * 100, swappedEmblem.Costs[0].Amount);
            Assert.Null(harvest.ApplyHarvest(timelessRecipe.Hash, karuiEmblem with { StackSize = 2 }, rng));
            var karuiSplinter = ItemCatalog.Create(catalog.Find("Timeless Karui Splinter")!) with { StackSize = 25 };
            var swappedSplinter = harvest.ApplyHarvest(timelessRecipe.Hash, karuiSplinter, rng);
            Assert.NotNull(swappedSplinter?.Item);
            Assert.NotEqual(karuiSplinter.TypeHash, swappedSplinter!.Item!.TypeHash);
            Assert.Equal((ushort)25, swappedSplinter.Item.StackSize);
            Assert.Equal(timelessRecipe.Costs[0].Amount * 25, swappedSplinter.Costs[0].Amount);
            Assert.Null(harvest.ApplyHarvest(RecipeById(harvest, "RerollOils").Hash, ItemCatalog.Create(catalog.Find("Tainted Oil")!), rng));
            Assert.NotNull(harvest.ApplyHarvest(RecipeById(harvest, "RerollOils").Hash, ItemCatalog.Create(catalog.Find("Clear Oil")!), rng)?.Item);
            Assert.Null(harvest.ApplyHarvest(RecipeById(harvest, "RerollCatalysts").Hash, ItemCatalog.Create(catalog.Find("Tainted Catalyst")!), rng));
            Assert.NotNull(harvest.ApplyHarvest(RecipeById(harvest, "RerollCatalysts").Hash, ItemCatalog.Create(catalog.Find("Turbulent Catalyst")!), rng)?.Item);
            var fossilRecipe = RecipeById(harvest, "RerollFossils");
            var dense20 = ItemCatalog.Create(catalog.Find("Dense Fossil")!) with { StackSize = 20 };
            Assert.Null(harvest.ApplyHarvest(fossilRecipe.Hash, dense20, rng));
            Assert.Contains("10", harvest.RefusalHint(fossilRecipe.Hash, dense20) ?? "");
            var dense10 = dense20 with { StackSize = 10 };
            var swappedFossil = harvest.ApplyHarvest(fossilRecipe.Hash, dense10, rng);
            Assert.NotNull(swappedFossil?.Item);
            Assert.Equal((ushort)10, swappedFossil!.Item!.StackSize);
            Assert.Equal(fossilRecipe.Costs[0].Amount * 10, swappedFossil.Costs[0].Amount);

            // ApplyItemCraft guards: NoInfluence rejects Eldritch-implicit items; non-equipment/jewel domains (Flasks, Maps) rejected.
            var augmentFire = harvest.All.First(r => r.Operation == HarvestCrafting.Operation.AugmentTag && r.NoInfluence);
            var reforgeFire = harvest.All.First(r => r.Operation == HarvestCrafting.Operation.ReforgeTag && r.Tag == "fire");
            var exarchMod = catalog.Mods.All.First(m => m.GenerationType == ItemRolls.ExarchImplicit);
            var eldritchPlate = rolls.Roll(plate, ItemRarity.Rare, rng) with
            {
                Mods = [.. plate.Mods, exarchMod.Roll(ModSection.Implicit, rng)],
            };
            Assert.True(rolls.HasEldritchImplicit(eldritchPlate));
            Assert.Null(harvest.ApplyHarvest(augmentFire.Hash, eldritchPlate, rng));
            var rareFlask = rolls.Roll(ItemCatalog.Create(catalog.Find("Small Life Flask")!) with { ItemLevel = 70 }, ItemRarity.Magic, rng) with { Rarity = ItemRarity.Rare };
            Assert.Null(harvest.ApplyHarvest(reforgeFire.Hash, rareFlask, rng));
            Assert.Null(harvest.ApplyHarvest(augmentFire.Hash, rareFlask, rng));

            // Divination card reroll & gamble (1/1 refused, destructive 0-card outcome distinguished from refusal).
            var humility = ItemCatalog.Create(catalog.Find("Humility")!) with { StackSize = 1 };
            var rerolledCard = harvest.ApplyHarvest(RecipeById(harvest, "RerollDivinationCards").Hash, humility, rng);
            Assert.NotNull(rerolledCard?.Item);
            Assert.NotEqual(humility.TypeHash, rerolledCard!.Item!.TypeHash);
            var gambleRecipe = RecipeById(harvest, "DivinationCardGamble");
            Assert.Null(harvest.ApplyHarvest(gambleRecipe.Hash, ItemCatalog.Create(catalog.Find("The Void")!) with { StackSize = 1 }, rng));
            bool sawDestroyed = false, sawKept = false;
            for (int seed = 0; seed < 40 && !(sawDestroyed && sawKept); seed++)
            {
                var outcome = harvest.ApplyHarvest(gambleRecipe.Hash, humility, new Random(seed));
                Assert.NotNull(outcome);
                if (outcome!.Item is null) sawDestroyed = true;
                else sawKept = true;
            }
            Assert.True(sawDestroyed && sawKept, "DivinationCardGamble must support both 0-card destruction and non-empty stack outcomes.");

            // Influence reroll & BossInfluence1 (ilvl >= 68 with guaranteed influence mod).
            var ringType = catalog.Find("Coral Ring")!;
            var shaperRing67 = rolls.Roll(ItemCatalog.Create(ringType) with { ItemLevel = 67, Influences = ItemInfluence.Shaper }, ItemRarity.Rare, rng);
            var shaperRing75 = rolls.Roll(ItemCatalog.Create(ringType) with { ItemLevel = 75, Influences = ItemInfluence.Shaper }, ItemRarity.Rare, rng);
            var rerollInf = harvest.ApplyHarvest(RecipeById(harvest, "RerollInfluenceType").Hash, shaperRing75, rng);
            Assert.NotNull(rerollInf?.Item);
            Assert.NotEqual(ItemInfluence.Shaper, rerollInf!.Item!.Influences);
            Assert.NotEqual(ItemInfluence.None, rerollInf.Item.Influences);
            var bossInfRecipe = RecipeById(harvest, "BossInfluence1");
            Assert.Null(harvest.ApplyHarvest(bossInfRecipe.Hash, shaperRing67, rng));
            var forgedInf = harvest.ApplyHarvest(bossInfRecipe.Hash, shaperRing75, rng);
            Assert.NotNull(forgedInf?.Item);
            Assert.Contains(forgedInf!.Item!.Mods, m => m.Section == ModSection.Explicit
                && catalog.Mods.ByHash(m.Hash) is { } mod && mod.WeightFor(ringType.Tags) == 0);

            // Corrupted map reroll (refuses uncorrupted, Vestigial, Tier 17, and The Shaper's Realm; ApplyItemCraft refuses maps).
            var mapType = catalog.Find("MapKeyTier5") ?? catalog.Find("Map (Tier 5)")!;
            var uncorruptedMap = rolls.Roll(ItemCatalog.Create(mapType) with { ItemLevel = 72 }, ItemRarity.Rare, rng);
            var corruptedMap = uncorruptedMap with { Corrupted = true };
            var mapRecipe = RecipeById(harvest, "RerollCorruptedMap");
            Assert.Null(harvest.ApplyHarvest(reforgeFire.Hash, uncorruptedMap, rng));
            Assert.Null(harvest.ApplyHarvest(mapRecipe.Hash, uncorruptedMap, rng));
            Assert.Null(harvest.ApplyHarvest(mapRecipe.Hash, corruptedMap with { Vestigial = true }, rng));
            if (catalog.Find("Metadata/Items/Maps/MapWorldsSanctuary") is { } t17Type)
                Assert.Null(harvest.ApplyHarvest(mapRecipe.Hash, rolls.Roll(ItemCatalog.Create(t17Type) with { ItemLevel = 84 }, ItemRarity.Rare, rng) with { Corrupted = true }, rng));
            if (catalog.Find("Metadata/Items/Maps/MapWorldsShapersRealm") is { } shaperMapType)
                Assert.Null(harvest.ApplyHarvest(mapRecipe.Hash, ItemCatalog.Create(shaperMapType) with { ItemLevel = 84, Rarity = ItemRarity.Rare, Corrupted = true, Identified = true }, rng));
            var rerolledMap = harvest.ApplyHarvest(mapRecipe.Hash, corruptedMap, rng);
            Assert.NotNull(rerolledMap?.Item);
            Assert.True(rerolledMap!.Item!.Corrupted);
            Assert.Equal(ItemRarity.Rare, rerolledMap.Item.Rarity);

            // Ruthless support gem transform (GameMode == 2): refused in Standard, works in Ruthless.
            var gemRecipe = RecipeById(ruthlessHarvest, "ReforgeSupportGemRespectColour");
            var (supportType, _) = catalog.FindGem("Added Fire Damage Support")!.Value;
            var supportGem = ItemCatalog.Create(supportType) with
            {
                Quality = 12,
                GemExperience = catalog.Gem(supportType)!.ExperienceFor(10),
            };
            Assert.Null(harvest.ApplyHarvest(gemRecipe.Hash, supportGem, rng));
            var swappedGem = ruthlessHarvest.ApplyHarvest(gemRecipe.Hash, supportGem, rng);
            Assert.NotNull(swappedGem?.Item);
            Assert.NotEqual(supportGem.TypeHash, swappedGem!.Item!.TypeHash);
            Assert.Equal((byte)12, swappedGem.Item.Quality);
            Assert.Equal(catalog.Gem(supportType)!.Colour, catalog.Gem(catalog.ByHash(swappedGem.Item.TypeHash)!)!.Colour);

            // GemExperienceLens -> Facetor's Lens (StoredExperience wire encoding + CurrencyEffects.ApplyFacetorsLens).
            var lensRecipe = RecipeById(harvest, "GemExperienceLens");
            var (fireballType, _) = catalog.FindGem("Fireball")!.Value;
            var fireballGemInfo = catalog.Gem(fireballType)!;
            int maxXp = fireballGemInfo.ExperienceFor(fireballGemInfo.MaxLevel);
            var uncorruptedFireball = ItemCatalog.Create(fireballType) with { GemExperience = maxXp };
            Assert.Null(harvest.ApplyHarvest(lensRecipe.Hash, uncorruptedFireball, rng));
            var lensOutcome = harvest.ApplyHarvest(lensRecipe.Hash, uncorruptedFireball with { Corrupted = true }, rng);
            Assert.NotNull(lensOutcome?.Item);
            var lens = lensOutcome!.Item!;
            Assert.Equal(maxXp / 2, lens.GemExperience);
            Assert.Contains("StoredExperience", lens.Components);
            byte[] lensPayload = lens.Payload();
            Assert.Equal((uint)lens.GemExperience, BinaryPrimitives.ReadUInt32LittleEndian(lensPayload.AsSpan(lensPayload.Length - 4, 4)));

            var freshFireball = ItemCatalog.Create(fireballType) with { GemExperience = 0 };
            var boosted = CurrencyEffects.ApplyFacetorsLens(freshFireball, fireballGemInfo, lens.GemExperience);
            Assert.NotNull(boosted);
            Assert.Equal(lens.GemExperience, boosted!.GemExperience);
            Assert.True(fireballGemInfo.Level(boosted.GemExperience) > 1);
            // Applying another full lens caps at maxXp (never -1, which encodes corrupted MaxLevel + 1).
            var capped = CurrencyEffects.ApplyFacetorsLens(boosted, fireballGemInfo, maxXp);
            Assert.NotNull(capped);
            Assert.Equal(maxXp, capped!.GemExperience);
            Assert.Equal(fireballGemInfo.MaxLevel, fireballGemInfo.Level(capped.GemExperience));
            Assert.Null(CurrencyEffects.ApplyFacetorsLens(capped, fireballGemInfo, 1000));
            Assert.Null(CurrencyEffects.ApplyFacetorsLens(freshFireball with { Corrupted = true }, fireballGemInfo, 1000));
        }
        finally
        {
            Game.ResetAchievementCache();
        }
    }
}
