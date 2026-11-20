using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SkillBarOptionTests
{
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
        var conn = SpellTotemExecutionTests.Connection(character);
        using var wire = new SpellTotemExecutionTests.Wire();
        conn.Session = new("skill-bar-test", wire.Stream, wire.Ciphers);
        var packets = new List<byte[]>();
        Program.Outgoing = (player, bytes) => { if (ReferenceEquals(player, conn)) packets.Add(bytes.ToArray()); };
        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        try
        {
            SkillBarLayout.Bind(character, 2, "Action/Move");
            Assert.True(gate.Wait(0));
            Task refresh;
            try
            {
                refresh = Game.SendSkillBar(conn);
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
