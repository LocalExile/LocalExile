using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class VaalSkillRecordFlagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Creation_and_update_parse_bit1_without_parent_fields_or_support_layout_changes(bool creation)
    {
        var vaal = new GrantedSkill(0x103, 532, 42) { VaalResourcesEnabled = true };
        var attack = new GrantedSkill(0x102, 4474, 4430);
        var ordinary = new GrantedSkill(0x104, 10, 20)
        {
            ExtraStatSets = [(21, [])],
            Supports = [new(30, 31, 5) { ExtraStatSets = [32] }]
        };
        GrantedSkill[] skills = [vaal, attack, ordinary];
        var player = new PlayerObject("x", 1, 2, 3, 4, 5, Skills: skills, Souls: [new(87, 30, 0)]);
        byte[] bytes = creation ? player.Blob() : InstanceEntities.PlayerSkillsChange(0x1000, skills, [0x105],
            [new(87, 30)], [new(87, 0, 30)]);
        using var r = new BinaryReader(new MemoryStream(bytes));
        // The minimal player's final fields are Actor flags and tasks; its preceding components are identical.
        r.BaseStream.Position = creation ? (player with { Skills = [], Souls = [] }).Blob().Length - 3 : 15;
        Assert.Equal(creation ? 0x40 : 0x20, r.ReadByte());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(3, r.ReadByte());
        if (!creation) Assert.Equal(1, r.ReadByte());
        byte actualFlag = ReadRecord(r, vaal, 0x02);
        ReadRecord(r, attack, 0);
        ReadRecord(r, ordinary, 0);
        if (!creation)
        {
            Assert.Equal(0x105, r.ReadUInt16());
            Assert.Equal(1, r.ReadByte());
            Assert.Equal(87u, r.ReadUInt32());
            Assert.Equal(30u, r.ReadUInt32());
        }
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(87u, r.ReadUInt32());
        Assert.Equal(creation ? 30u : 0u, r.ReadUInt32());
        Assert.Equal(creation ? 0u : 30u, r.ReadUInt32());
        if (creation) { Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(0, r.ReadByte()); }
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);

        // Literal primary model, NOT a native witness: 0x1421975f0..610 / 6e8..6f6 and 0x141ee7571..75ba.
        Assert.Equal((0, 0, 0), PrimaryConstructorSoulInputs(0, 30, 1, 100));
        Assert.Equal((30, 1, 100), PrimaryConstructorSoulInputs(actualFlag, 30, 1, 100));
    }

    static (int BaseSouls, int StoredUses, int Multiplier) PrimaryConstructorSoulInputs(
        byte wireFlags, int baseSouls, int storedUses, int multiplier)
    {
        int dil = (wireFlags & 2) == 0 && storedUses != 0 ? 1 : 0;
        int param13 = (((wireFlags >> 2) & 1) * 2) | dil;
        return (param13 & 1) != 0 ? (0, 0, 0) : (baseSouls, storedUses, multiplier);
    }

    static byte ReadRecord(BinaryReader r, GrantedSkill expected, byte flags)
    {
        Assert.Equal(expected.Id, r.ReadUInt16());
        Reference(r, expected.EffectsPerLevelRow);
        Reference(r, expected.StatSetsPerLevelRow);
        foreach (var (row, _) in expected.ExtraStatSets) Reference(r, row);
        byte actual = r.ReadByte();
        Assert.Equal(flags, actual); // bit0 is absent: support count must immediately follow, without six parent bytes.
        Assert.Equal(expected.Supports.Count, r.ReadByte());
        foreach (var support in expected.Supports)
        {
            Reference(r, support.EffectsPerLevelRow);
            Reference(r, support.StatSetsPerLevelRow);
            foreach (uint row in support.ExtraStatSets) Reference(r, row);
            Assert.Equal(support.Source, r.ReadUInt32());
        }
        Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(expected.CooldownMs, r.ReadUInt32()); Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadByte()); // empty own stats
        foreach (var _ in expected.ExtraStatSets) Assert.Equal(0, r.ReadByte());
        return actual;
    }

    static void Reference(BinaryReader r, uint row)
    {
        Assert.Equal(0, r.ReadByte()); Assert.Equal(row, r.ReadUInt32());
    }
}
