using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modules.Shrines;

namespace GameServer.Gateway.Tests;

public sealed class ProtocolCombatDeltaTests
{
    static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;

    static Player MakePlayer(PartyFollowerTests.Runtime runtime, InstanceState instance, CaptureWire wire, string name)
    {
        var layout = new AreaLayout(1, "proto-combat-test", 300, 300, 100, 100, (_, _) => true, []);
        var player = new Player(layout, 1, 1)
        {
            Instance = instance,
            Character = { Name = name },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Pools = new(1, 1000, 1000, 1000, 0),
            Connection =
            {
                LockstepClient = false,
                PlayerSent = true,
                Session = new("proto-combat-test", wire.Stream, wire.ServerCiphers),
            },
        };
        Program.InGame.TryAdd(player, 0);
        return player;
    }

    [Fact]
    public async Task Shrine_claim_and_predictive_channel_end_emit_native_world_updates()
    {
        ShrineRegistry.RegisterDefaults();
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new CaptureWire();
        var instance = new InstanceState();
        var player = MakePlayer(runtime, instance, wire, "ProtoShrineChannel" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Shrine claim emits 0x2bc Shrine component update with taker id instead of removing/recreating.
            Assert.Equal(1, await ShrineManager.SpawnAroundPlayer(player, ["Acceleration"]));
            uint shrineId = uint.Parse(instance.ContentState["Shrines/id/112/100"]);
            var accel = ShrineCatalog.Find("Acceleration")!;
            player.Walk.Teleport(112, 100, 0);
            Assert.True(await ShrineManager.Use(player, new(shrineId, 0, 1, 0), 0));

            // 2. Predictive C->S 0x0199 ChannelEnd emits S->C 0x01ae ChannelFinished.
            player.ChannelSkill = 0x0104;
            player.ChannelSkillSequence = 0x0007;
            byte[] endReq = new LoginWriter().U16(InstanceProtocol.ChannelEndId).ToArray();
            await Game.OnClientMessages([(InstanceProtocol.ChannelEndId, endReq)], [], player);

            // 3. Divine Ire channel emits live Stat.DivineTempestCurrentNumberOfStages (0x233f) and clears on end.
            int divineIreEffect = Program.Skills.EffectRow("DivineIre");
            Assert.True(Game.IsDivineIre(player, divineIreEffect));
            player.ChannelSkill = 0x0105;
            player.ChannelSkillSequence = 0x0009;
            player.ActionCount = 42;
            player.NextChannelHitAt = Game.AreaTime(player) + 10;
            player.LastChannelUpdateAt = Game.AreaTime(player);
            // Force Running() to exit immediately after start stage publication (zero real-time wait).
            player.PlayerLife = 0;
            await Game.RunChannel(player, 0x0105, (0x0105, divineIreEffect, "Divine Ire", 1, 0, 0u), 42, (112, 100));

            // The first channel's finally can run after the next release.
            await Game.EmitPredictiveChannelFinished(player, 0, 0x0007);
            await Game.EmitPredictiveChannelFinished(player, player.ChannelGeneration, 0x0009);

            byte[] sent = wire.FinishAndDecrypt();
            byte[] expectedShrineUpdate = InstanceEntities.ShrineUpdate(shrineId, (byte)accel.Row, player.Id, claimed: true);
            byte[] unexpectedShrineRemoval = InstanceProtocol.RemoveObject(shrineId);
            byte[] expectedChannelEnd = InstanceProtocol.ChannelFinished(player.Id, 0x0007);
            byte[] expectedDivineStage1 = InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, 1)]);
            byte[] expectedDivineClear = InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, 0)]);

            Assert.True(ContainsSequence(sent, expectedShrineUpdate), "expected 0x02bc Shrine claim update");
            Assert.False(ContainsSequence(sent, unexpectedShrineRemoval), "normal shrine claim must not remove/recreate");
            Assert.True(ContainsSequence(sent, expectedChannelEnd), "expected 0x01ae ChannelFinished for predictive release");
            int endAt = sent.AsSpan().IndexOf(expectedChannelEnd);
            Assert.False(ContainsSequence(sent.AsSpan(endAt + expectedChannelEnd.Length), expectedChannelEnd));
            Assert.True(ContainsSequence(sent, expectedDivineStage1), "expected 0x01b3 Divine Ire stage 1 stat");
            Assert.True(ContainsSequence(sent, expectedDivineClear), "expected 0x01b3 Divine Ire stage reset to 0");
        }
        finally
        {
            await ShrineManager.LeaveArea(player);
            player.Closed = true;
            Program.InGame.TryRemove(player, out _);
            ShrineRegistry.RegisterDefaults();
        }
    }

    [Fact]
    public async Task Flame_dash_segment_flask_charges_and_item_use_result_emit_native_deltas()
    {
        string save = Path.Combine(Path.GetTempPath(), $"proto-combat-deltas-{Guid.NewGuid():N}.db");
        string name = "ProtoCombatDeltas" + Guid.NewGuid().ToString("N");
        try
        {
            Saves.Open(save);
            using var runtime = new PartyFollowerTests.Runtime();
            using var wire = new CaptureWire();
            var instance = new InstanceState();
            var player = MakePlayer(runtime, instance, wire, name);
            try
            {
                var items = Game.ItemsOf(player);
                const byte main = PlayerInventories.MainInventory;
                foreach (var existing in items.Items(main).ToArray()) items.Remove(main, existing.Id);
                foreach (var existing in items.Items(PlayerInventories.Flask).ToArray()) items.Remove(PlayerInventories.Flask, existing.Id);

                // 1. Flame Dash S->C 0x02c8 ground segment helper matches 3.29.3c ActionUpdate layout.
                int flameDashEffect = Program.Skills.EffectRow("FlameDash");
                Assert.True(Game.IsFlameDashEffect(Program.Skills.EffectId(flameDashEffect)));
                byte[] segment = Game.FlameDashGroundSegment(player, 0x0104, flameDashEffect, 0x0003, (100, 100), (120, 100));
                ushort expectedHash = Program.Skills.ActionHash(flameDashEffect)!.Value;
                Assert.Equal(InstanceProtocol.ActionUpdateId, BinaryPrimitives.ReadUInt16BigEndian(segment));
                Assert.Equal(expectedHash, BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(12)));
                Assert.Equal((ushort)0x0104, BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(14)));
                Assert.Equal((ushort)0x0003, BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(16)));
                Assert.Equal(0, segment[18]);
                Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(19)));
                Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(21)));
                Assert.Equal(120, BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(29)));

                // 2. Flask charge gain emits incremental S->C 0x00c6, while tincture state flip still emits S->C 0x00bf.
                var flaskType = Program.Items.All.First(t => t.Class == "LifeFlask" && t.Supported);
                var flask = items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(flaskType) with { Charges = 0 });
                await Game.GainFlaskCharges(player, 5, "delta test");
                int newCharges = items.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;
                Assert.True(newCharges > 0);
                var tinctureType = Program.Items.All.First(t => t.Class == "Tincture");
                var tincture = items.Add(PlayerInventories.Flask, 1, 0, ItemCatalog.Create(tinctureType));
                await Game.SendTinctureBarInactive(player, [tincture.Id]);

                // 3. C->S 0x0057 item click emits companion S->C 0x0174 ItemUseResult (1 on success, 0 on refusal).
                var deckType = Program.Items.Find(Game.StackedDeck)!;
                var deck = items.Add(main, 0, 0, ItemCatalog.Create(deckType) with { StackSize = 2 });
                uint mainInvId = PlayerInventories.InventoryId(main);
                byte[] click = new LoginWriter().U16(InstanceItems.UseItemClickId).U32(mainInvId).U32(deck.Id).U8(0).ToArray();
                await Game.OnClientMessages([(InstanceItems.UseItemClickId, click)], [], player);
                var reformType = Program.Items.All.First(t => BanditChoices.IsBookOfReform(t.Id));
                var reform = items.Add(main, 1, 0, ItemCatalog.Create(reformType));
                byte[] refusedClick = new LoginWriter().U16(InstanceItems.UseItemClickId).U32(mainInvId).U32(reform.Id).U8(0).ToArray();
                await Game.OnClientMessages([(InstanceItems.UseItemClickId, refusedClick)], [], player);

                byte[] sent = wire.FinishAndDecrypt();
                Assert.True(ContainsSequence(sent, InstanceItems.FlaskCharges(flask.Id, (uint)newCharges)),
                    "expected 0x00c6 incremental FlaskCharges update");
                Assert.False(ContainsSequence(sent, InstanceItems.FlaskCharges(tincture.Id, 0)),
                    "tincture deactivation must not downgrade to 0x00c6");
                byte[] flaskBarChangePrefix = new LoginWriter().U16(InstanceItems.InventoryChangeId)
                    .U8(0).U32(PlayerInventories.InventoryId(PlayerInventories.Flask)).ToArray();
                Assert.True(ContainsSequence(sent, flaskBarChangePrefix),
                    "expected 0x00bf full flask bar update for tincture deactivation");
                Assert.True(ContainsSequence(sent, InstanceItems.ItemUseResult(mainInvId, deck.Id, 1)),
                    "expected 0x0174 ItemUseResult(1) reply for accepted C->S 0x0057");
                Assert.True(ContainsSequence(sent, InstanceItems.ItemUseResult(mainInvId, reform.Id, 0)),
                    "expected 0x0174 ItemUseResult(0) reply for refused C->S 0x0057");
            }
            finally
            {
                player.Closed = true;
                Program.InGame.TryRemove(player, out _);
            }
        }
        finally
        {
            PlayerInventories.Forget(name);
            TestEnvironment.ReleaseSaves(save);
        }
    }
}
