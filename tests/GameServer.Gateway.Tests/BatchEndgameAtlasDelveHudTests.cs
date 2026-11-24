using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class BatchEndgameAtlasDelveHudTests
{
    [Fact]
    public async Task Atlas_scry_voidstone_delta_uncomplete_and_divine_font_crafting_work()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var data = Program.WorldOf(fixture.Player).CharacterData;
        var chateau = AtlasProgress.Find("Chateau")!;
        var alleyways = AtlasProgress.Find("Alleyways")!;

        // Atlas Scrying persistence, Uncomplete, and Voidstone 0x01a5 region-bit delta mapping.
        Assert.Null(AtlasProgress.ScryTarget(data, chateau));
        Assert.True(AtlasProgress.SetScry(data, chateau, alleyways));
        Assert.Equal(alleyways.Id, AtlasProgress.ScryTarget(data, chateau));

        Assert.True(AtlasProgress.Complete(data, [chateau]));
        Assert.True(AtlasProgress.CompleteBonus(data, [chateau]));
        Assert.Contains(chateau.Id, AtlasProgress.Completed(data));
        Assert.True(AtlasProgress.Uncomplete(data, chateau));
        Assert.DoesNotContain(chateau.Id, AtlasProgress.Completed(data));
        Assert.DoesNotContain(chateau.Id, AtlasProgress.BonusCompleted(data));
        Assert.False(AtlasProgress.Uncomplete(data, chateau));

        Assert.Equal([0x01, 0xa5, 0x02, 0x01], AtlasProgress.VoidstoneDeltaMessage(0, true));
        Assert.Equal([0x01, 0xa5, 0x01, 0x00], AtlasProgress.VoidstoneDeltaMessage(1, false));
        Assert.Equal([0x01, 0xa5, 0x03, 0x01], AtlasProgress.VoidstoneDeltaMessage(2, true));
        Assert.Equal([0x01, 0xa5, 0x00, 0x00], AtlasProgress.VoidstoneDeltaMessage(3, false));

        // Labyrinth Divine Font (S->C 0x0246 / 0x02a2, C->S 0x0247 / 0x02a3) in Eternal Ascendancy chamber.
        var outbox = new List<byte[]>();
        var labLayout = new AreaLayout(1, "EndGame_Labyrinth_boss_3_end", 300, 300, 50, 50, (_, _) => true, []);
        var labPlayer = new Player(labLayout, 1, 1)
        {
            Character = { Name = "LabFont" + Guid.NewGuid().ToString("N")[..8] },
            Instance = new(),
            PlayerLife = 1000,
            Connection =
            {
                PlayerSent = true,
                Outbox = (bytes, _) => { outbox.Add([.. bytes]); return Task.CompletedTask; },
            },
            LeagueModule = fixture.Player.LeagueModule,
        };
        labPlayer.Clock.Start();

        // Refused before Izaro is beaten.
        await QuestobjectsRules.UseDivineFont(labPlayer);
        Assert.Empty(outbox);

        // Once Izaro is beaten, Eternal font offers options with 2 remaining uses via S->C 0x0246.
        labPlayer.Instance.SetState(IzaroFights.Beaten);
        await QuestobjectsRules.UseDivineFont(labPlayer);
        byte[] openPacket = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftOptionsId);
        byte optionCount = openPacket[2];
        Assert.True(optionCount >= 2);
        Assert.Equal((byte)2, openPacket[^1]);
        ushort firstHash = BinaryPrimitives.ReadUInt16BigEndian(openPacket.AsSpan(3, 2));

        // Put a Fireball gem in PlayerInventories.CraftingBench (row 16 = StrMasterCrafting).
        var (fireballType, _) = Program.Items.FindGem("Fireball")!.Value;
        var items = Game.ItemsOf(labPlayer);
        var gem = items.Put(PlayerInventories.CraftingBench, new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(fireballType)));

        // Option 0 is transfigure-gem-of-same-colour: sends S->C 0x02a2 candidate list; repeating 0x0247 must NOT reroll.
        outbox.Clear();
        await QuestobjectsRules.UseDivineFontOption(labPlayer, firstHash);
        byte[] resultList = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftResultOptionsListId);
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(resultList.AsSpan(2, 8)) >= 1UL);

        outbox.Clear();
        await QuestobjectsRules.UseDivineFontOption(labPlayer, firstHash);
        byte[] repeatedList = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftResultOptionsListId);
        Assert.Equal(resultList, repeatedList);

        // Swapping the gem in CraftingBench before 0x02a3 must be refused without spending a use.
        var removedOriginal = items.Remove(PlayerInventories.CraftingBench, gem.Id)!;
        var decoy = items.Put(PlayerInventories.CraftingBench, new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(fireballType)));
        outbox.Clear();
        await QuestobjectsRules.PickDivineFontResult(labPlayer, 0);
        Assert.DoesNotContain(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceItems.InventoryChangeId);
        byte[] refusedClear = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftResultOptionsListId);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(refusedClear.AsSpan(2, 8)));

        // Restoring the exact source gem allows 0x02a3 to swap and spend 1 use.
        items.Remove(PlayerInventories.CraftingBench, decoy.Id);
        items.Put(PlayerInventories.CraftingBench, removedOriginal);
        outbox.Clear();
        await QuestobjectsRules.PickDivineFontResult(labPlayer, 0);
        Assert.Contains(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceItems.InventoryChangeId);
        byte[] afterPick = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftOptionsId);
        Assert.Equal((byte)1, afterPick[^1]);
        var swapped = Assert.Single(items.Items(PlayerInventories.CraftingBench));
        Assert.NotEqual(gem.Item, swapped.Item);

        // Quality option (use_x_gemcutter) parses single-quoted ScriptArgument (e.g. '8'/'15'), boosts quality, and spends the final use.
        ushort qualityHash = 0;
        int expectedQualityAdd = 0;
        if (ClientTables.Has("LabyrinthCraftOptions"))
        {
            var offered = new HashSet<ushort>();
            for (int i = 0; i < optionCount; i++)
                offered.Add(BinaryPrimitives.ReadUInt16BigEndian(openPacket.AsSpan(3 + i * 2, 2)));
            foreach (var row in ClientTables.Table("LabyrinthCraftOptions"))
                if (row.Text("Script") == "use_x_gemcutter" && offered.Contains((ushort)(row.IntOrNull("HASH16") ?? 0)))
                {
                    qualityHash = (ushort)row.Int("HASH16");
                    string rawArg = (row.Text("ScriptArgument") ?? "").Trim().Trim('\'');
                    Assert.True(int.TryParse(rawArg, out expectedQualityAdd));
                    break;
                }
        }
        Assert.NotEqual((ushort)0, qualityHash);
        Assert.True(expectedQualityAdd > 2);
        byte qualityBefore = swapped.Item.Quality;
        outbox.Clear();
        await QuestobjectsRules.UseDivineFontOption(labPlayer, qualityHash);
        byte[] afterQuality = Assert.Single(outbox, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceLabyrinth.CraftOptionsId);
        Assert.Equal((byte)0, afterQuality[^1]);
        var qualityGem = Assert.Single(items.Items(PlayerInventories.CraftingBench));
        Assert.Equal((byte)Math.Min(20, qualityBefore + expectedQualityAdd), qualityGem.Item.Quality);
    }

    [Fact]
    public async Task League_achievements_advertise_and_award_active_sets_only()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        Game.LoadAchievementsData();
        try
        {
            var standardLeague = new LeagueRuntime(new League("Standard", "Standard", false, false),
                new HashSet<string>(["Game"]));
            var allflameLeague = new LeagueRuntime(new League("Allflame", "Allflame", false, false),
                new HashSet<string>(["Game"]));
            var ruthlessAllflameLeague = new LeagueRuntime(
                new League("Ruthless Allflame", "Allflame", false, false, Form: "ruthless") { DataSet = "HardMode" },
                new HashSet<string>(["Game"]));

            static Player MakePlayer(AreaLayout layout, LeagueRuntime league, List<byte[]> outbox)
            {
                var p = new Player(layout, 1, 1)
                {
                    Character =
                    {
                        Account = "league-ach-" + Guid.NewGuid().ToString("N"),
                        Name = "Ach" + Guid.NewGuid().ToString("N")[..8],
                    },
                    Instance = new(),
                    PlayerLife = 1000,
                    Connection =
                    {
                        PlayerSent = true,
                        Outbox = (bytes, _) => { outbox.Add([.. bytes]); return Task.CompletedTask; },
                    },
                    LeagueModule = league,
                };
                p.Clock.Start();
                return p;
            }

            List<byte[]> stdOut = [], afOut = [], rafOut = [];
            var stdPlayer = MakePlayer(fixture.Player.Layout, standardLeague, stdOut);
            var afPlayer = MakePlayer(fixture.Player.Layout, allflameLeague, afOut);
            var rafPlayer = MakePlayer(fixture.Player.Layout, ruthlessAllflameLeague, rafOut);

            // Permanent leagues advertise set 2 only; active challenge leagues add set 79 (standard) or 80 (Ruthless).
            Assert.Equal([InstanceAchievements.PermanentAchievementSetId], Game.ActiveAchievementSets(stdPlayer));
            Assert.Equal([InstanceAchievements.PermanentAchievementSetId, InstanceAchievements.DefaultLeagueChallengeSetId],
                Game.ActiveAchievementSets(afPlayer));
            Assert.Equal([InstanceAchievements.PermanentAchievementSetId, InstanceAchievements.DefaultRuthlessChallengeSetId],
                Game.ActiveAchievementSets(rafPlayer));

            // EnterMessages emits S->C 0x0126 for every active set.
            var stdEnter = Game.AchievementEnterMessages(stdPlayer);
            Assert.Single(stdEnter);
            Assert.Equal(InstanceAchievements.PermanentAchievementSetId, stdEnter[0].Bytes[2]);

            var afEnter = Game.AchievementEnterMessages(afPlayer);
            Assert.Equal(2, afEnter.Count);
            Assert.Equal([InstanceAchievements.PermanentAchievementSetId, InstanceAchievements.DefaultLeagueChallengeSetId],
                afEnter.Select(m => m.Bytes[2]).ToArray());

            // Set 79 challenge item is refused on Standard and awarded with set 79 bitset update on Allflame.
            Assert.False(await Game.AwardAchievementItem(stdPlayer, "DeepwaterBeginnerChallenge1_1"));
            Assert.Empty(stdOut);

            Assert.True(await Game.AwardAchievementItem(afPlayer, "DeepwaterBeginnerChallenge1_1"));
            Assert.Contains(afOut, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementItemCompleteId);
            Assert.Contains(afOut, p => BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceAchievements.AchievementProgressId
                && p[2] == InstanceAchievements.DefaultLeagueChallengeSetId);
        }
        finally
        {
            Game.ResetAchievementCache();
        }
    }
}
