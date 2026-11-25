using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SkillBarOptionTests
{
    [Fact]
    public async Task Binding_the_second_copy_of_a_gem_echoes_its_own_skill_id()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Fireball", 1), ("Fireball", 5));
        var copies = Game.GemSkillEffects(player).Where(g => Program.Skills.EffectId(g.Effect) == "Fireball").ToArray();
        Assert.Equal(2, copies.Length);
        Assert.NotEqual(copies[0].Level, copies[1].Level);
        Assert.NotEqual(copies[0].Id, copies[1].Id);

        const byte slot = 4;
        ushort second = copies[1].Id;
        var messages = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (peer, bytes) =>
        {
            if (ReferenceEquals(peer, player)) messages.Add(bytes.ToArray());
        };
        try
        {
            byte[] request = [0, (byte)InstanceProtocol.BindSkillId, slot, (byte)(second >> 8), (byte)second];
            await Game.OnClientMessages([(InstanceProtocol.BindSkillId, request)], [], player);
            var bar = Assert.Single(messages, bytes => bytes.Length > 2 && bytes[0] == 0 && bytes[1] == InstanceProtocol.SkillBarId);
            ushort echoed = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bar.AsSpan(2 + slot * 2, 2));
            Assert.Equal(second, echoed);
        }
        finally { Program.Outgoing = outgoing; }

        string character = player.Character.Name!;
        var items = Game.ItemsOf(player);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var boundGem = Assert.IsType<Item>(host.Item.Sockets[1].Gem);
        Assert.NotNull(boundGem.SkillBarGemId);
        var mirror = Assert.IsType<Item>(Program.Rolls.Mirror(host.Item));
        Assert.Null(mirror.Sockets[1].Gem?.SkillBarGemId);
        Assert.NotNull(host.Item.Sockets[1].Gem?.SkillBarGemId);
        Assert.NotNull(items.Unsocket(PlayerInventories.Weapon1, host.Id, 1, items.NewItemId, out _));
        Game.FillSkillBar(player);
        Assert.Equal((ushort)0, BoundSkill(player));

        Assert.NotNull(items.Socket(PlayerInventories.Weapon1, host.Id, 0, Game.ShapeOf, items.NewItemId, out _));
        Assert.NotNull(items.Socket(PlayerInventories.Weapon1, host.Id, 1, Game.ShapeOf, items.NewItemId, out _));
        AssertSelectedLevel(player);

        PlayerInventories.Forget(character);
        var reentered = SpellTotemExecutionTests.PlayerOf(character);
        Assert.NotSame(items, Game.ItemsOf(reentered));
        AssertSelectedLevel(reentered);
        var reenteredItems = Game.ItemsOf(reentered);
        Assert.NotNull(reenteredItems.Unsocket(PlayerInventories.Weapon1, host.Id, 1, reenteredItems.NewItemId, out _));
        Assert.NotNull(reenteredItems.Remove(PlayerInventories.Cursor, Assert.Single(reenteredItems.Items(PlayerInventories.Cursor)).Id));
        Assert.True(Game.FillSkillBar(reentered));
        var filled = CharacterSaves.Load(character)!.SkillBar!;
        Assert.DoesNotContain(filled.Values, value => value.StartsWith("Gem/", StringComparison.Ordinal));
        Assert.Contains("Fireball", filled.Values);

        ushort BoundSkill(Player on) => Game.SkillBarIds(on, CharacterSaves.Load(character)!.SkillBar)[slot];

        void AssertSelectedLevel(Player on)
        {
            ushort id = BoundSkill(on);
            Assert.Equal(5, Game.GemSkillEffects(on).Single(g => g.Id == id).Level);
            Assert.Equal(5, Game.SupportedSkill(on, id).Level);
        }
    }

    [Fact]
    public async Task Manual_clear_survives_auto_placement_and_a_queued_refresh_keeps_the_latest_bind()
    {
        string character = "ManualBar" + Guid.NewGuid().ToString("N");
        CharacterSaves.SetSkillBar(character, 2, "IceShot");
        SkillBarLayout.Bind(character, 2, "");
        var save = CharacterSaves.Load(character)!;
        var hidden = SkillBarLayout.Unassigned(save);
        var added = SkillBarLayout.Fill(save.SkillBar!, "PlayerMelee",
            new[] { "IceShot", "SplitArrow" }.Where(s => !hidden.Contains(s)), _ => true);
        Assert.Contains((3, "SplitArrow"), added);
        Assert.DoesNotContain(added, s => s.Slot == 2 || s.Entry == "IceShot");
        SkillBarLayout.Bind(character, 5, "IceShot");
        Assert.DoesNotContain("IceShot", SkillBarLayout.Unassigned(CharacterSaves.Load(character)));
        var previous = Program.GameLogicOrNull;
        var outgoing = Program.Outgoing;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = SpellTotemExecutionTests.PlayerOf(character);
        using var wire = new SpellTotemExecutionTests.Wire();
        player.Connection.Session = new("skill-bar-test", wire.Stream, wire.Ciphers);
        var packets = new List<byte[]>();
        Program.Outgoing = (peer, bytes) => { if (ReferenceEquals(peer, player)) packets.Add(bytes.ToArray()); };
        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        try
        {
            SkillBarLayout.Bind(character, 2, "Action/Move");
            Assert.True(gate.Wait(0));
            Task refresh;
            try
            {
                refresh = Game.SendSkillBar(player);
                Assert.False(refresh.IsCompleted);
                SkillBarLayout.Bind(character, 2, "");
            }
            finally { gate.Release(); }
            await refresh;
            var bar = Assert.Single(packets, p => p.Length > 2 && p[0] == 0 && p[1] == 0x53);
            Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bar.AsSpan(6, 2)));
        }
        finally
        {
            Program.Outgoing = outgoing; Program.SetGameLogic(previous!);
            PlayerInventories.Forget(character);
        }
    }

    // C→S 0x4f is framed, saved per slot, and goes back in 0x53's raw (little-endian) +0x2a mask; a bar sent with 0 there
    // cleared the player's "Always Attack Without Moving".
    [Fact]
    public void Attack_in_place_is_saved_per_slot_and_sent_back_in_the_skill_bar()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-skill-bar-option-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            Assert.Equal(4, InstanceProtocol.KnownClientLength(InstanceProtocol.AttackInPlaceId));
            var (slot, on) = InstanceProtocol.ReadSlotOption(Convert.FromHexString("004F0301"));
            Assert.Equal((3, true), (slot, on));

            string character = $"SkillBarOption{Guid.NewGuid():N}";
            CharacterSaves.SetSkillBarOption(character, attackInPlace: true, slot, on);
            CharacterSaves.SetSkillBarOption(character, attackInPlace: true, 9, true);
            CharacterSaves.SetSkillBarOption(character, attackInPlace: true, 9, false);
            var save = CharacterSaves.Load(character)!;
            Assert.Equal((ushort)0x0008, save.AttackInPlaceSlots);

            byte[] bar = InstanceProtocol.SkillBar([], save.AttackInPlaceSlots ?? 0, 0x0102);
            Assert.Equal(2 + 26 + 4, bar.Length);
            Assert.Equal(new byte[] { 0x08, 0x00, 0x02, 0x01 }, bar[^4..]);
        }
        finally
        {
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
