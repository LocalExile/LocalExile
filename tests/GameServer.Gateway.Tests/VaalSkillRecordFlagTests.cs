using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulCompositionTests
{
    [Theory]
    [InlineData("VaalFireball", true)]
    [InlineData("VaalHaste", true)]
    [InlineData("Fireball", false)]
    [InlineData("PlayerMelee", false)]
    [InlineData("SupportFasterProjectiles", false)]
    public void Both_real_catalog_lookups_set_resource_flags_only_for_actual_Vaal_definitions(string effect, bool enabled)
    {
        int row = Program.Skills.EffectRow(effect);
        Assert.True(row >= 0);
        var byName = Assert.IsType<GrantedSkill>(Program.Skills.Find(0x103, effect, 1));
        var byRow = Assert.IsType<GrantedSkill>(Program.Skills.FindByRow(0x103, row, 1));
        Assert.Equal(enabled, byName.VaalResourcesEnabled);
        Assert.Equal(enabled, byRow.VaalResourcesEnabled);
        Assert.Equal(byName.EffectsPerLevelRow, byRow.EffectsPerLevelRow);
        Assert.Equal(enabled, Program.Skills.VaalBudget(row, 1) is not null);
    }

    [Fact]
    public async Task Worn_socketed_records_keep_bit1_through_no_souls_zero_removal_and_zero_budget()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        f.Gems("SkillGemVaalFireball", "SkillGemVaalHaste", "SupportGemFasterProjectiles"); f.Links(3);
        Assert.Single(Fireball(f.Conn).Supports);
        await AssertCurrentFlagPacket(f, w, [new(87, 30), new(239, 50)], [new(87, 0, 30), new(239, 0, 50)]);
        Assert.Equal(new VaalSoulBudget(87, 30, 0), f.Conn.SentVaalSouls[87]);
        Assert.All(f.Conn.VaalSouls.Read(), b => Assert.Equal(0u, b.Current));

        foreach (string value in new[] { "1", "0", "off" })
        {
            await Command(f, w, $"/set_stat virtual_vaal_skill_require_no_souls_per_use {value}");
            await AssertCurrentFlagPacket(f, w);
            Assert.Equal(value == "1" ? 0u : 30u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Capacity);
            Assert.All(f.Conn.VaalSouls.Read(), b => Assert.Equal(0u, b.Current));
        }
        await Command(f, w, "/set_stat virtual_non_aura_vaal_skill_soul_requirement_multipler -100");
        await AssertCurrentFlagPacket(f, w);
        Assert.Equal(new VaalSoulBudget(87, 0, 0), f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87));
    }

    [Fact]
    public async Task Inactive_worn_host_grants_no_Vaal_record_and_reactivation_reacquires_flag_without_balance()
    {
        using var f = new VaalSoulGatewayTests.Fixture(); using var w = new VaalSoulGatewayTests.Wire();
        Assert.True(f.Kill());
        await AssertCurrentFlagPacket(f, w, [new(87, 30)], [new(87, 1, 30)]);
        var host = Assert.Single(f.Items.Items(PlayerInventories.Weapon1));
        // A real affix raises the worn host's level requirement above this character's level.
        var mod = Program.Items.Mods.All.First(m => m.Domain == 1 && m.Level >= 60 && m.GenerationType is 1 or 2);
        f.Items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            { Mods = [mod.Roll(ModSection.Explicit, new Random(22))] });
        f.Conn.Pools = f.Conn.Pools with { Level = 1 };
        var removed = CurrentSkills(f.Conn).Where(s => s.Id != SkillCatalog.FirstSkillId).Select(s => s.Id).ToArray();
        Assert.Empty(removed); // Current requirements see the inactive host before cached pool publication.
        await AssertCurrentFlagPacket(f, w, updates: [new(87, 0, 0)]);
        Assert.Empty(f.Conn.VaalSouls.Read()); Assert.False(f.Kill());
        f.Conn.Pools = f.Conn.Pools with { Level = 100 };
        await AssertCurrentFlagPacket(f, w, [new(87, 30)], [new(87, 0, 30)]);
        Assert.Equal(new VaalSoulBudget(87, 30, 0), f.Budget);
    }

    static async Task AssertCurrentFlagPacket(VaalSoulGatewayTests.Fixture f, VaalSoulGatewayTests.Wire w,
        VaalSoulSeed[]? seeds = null, VaalSoulUpdate[]? updates = null)
    {
        var skills = CurrentSkills(f.Conn);
        var gone = f.Conn.ClientSkillIds.Except(skills.Select(s => s.Id)).Order().ToArray();
        var expected = InstanceEntities.PlayerSkillsChange(0x1000, skills, gone, seeds, updates);
        await (Task)Call("SendSkills", "vaal-record-flag", w.Stream, w.Ciphers, f.Conn);
        byte[] received = await w.Through(expected);
        int start = received.AsSpan().IndexOf(expected);
        Assert.True(start >= 0);
        AssertParsedFlags(received.AsSpan(start, expected.Length).ToArray(), skills);
    }

    static void AssertParsedFlags(byte[] packet, IReadOnlyList<GrantedSkill> skills)
    {
        var header = new LoginReader(packet);
        Assert.Equal(0x2ba, header.U16()); Assert.Equal(0x1000u, header.U32());
        Assert.Equal(0u, header.U32()); Assert.Equal(0, header.U16());
        byte[] blob = header.Blob(); Assert.False(header.Short); Assert.Equal(packet.Length, header.Position);
        using var r = new BinaryReader(new MemoryStream(blob));
        Assert.Equal(PlayerObject.ActorComponentIndex, r.ReadByte());
        Assert.Equal(0x20, r.ReadByte()); Assert.Equal(0, r.ReadByte());
        Assert.Equal(skills.Count, r.ReadByte()); int removed = r.ReadByte();
        uint haste = Program.Skills.Find(0, "VaalHaste", 1)!.EffectsPerLevelRow;
        foreach (var skill in skills)
        {
            Assert.Equal(skill.Id, r.ReadUInt16());
            ReadReference(r, skill.EffectsPerLevelRow); ReadReference(r, skill.StatSetsPerLevelRow);
            foreach (var (row, _) in skill.ExtraStatSets) ReadReference(r, row);
            byte expected = skill.EffectsPerLevelRow == 532 || skill.EffectsPerLevelRow == haste ? (byte)2 : (byte)0;
            Assert.Equal(expected == 2, skill.VaalResourcesEnabled);
            Assert.Equal(expected, r.ReadByte()); // Exact equality excludes parent bit0 and third bit2.
            Assert.Equal(skill.Supports.Count, r.ReadByte());
            foreach (var support in skill.Supports)
            {
                ReadReference(r, support.EffectsPerLevelRow); ReadReference(r, support.StatSetsPerLevelRow);
                foreach (uint row in support.ExtraStatSets) ReadReference(r, row);
                Assert.Equal(support.Source, r.ReadUInt32());
            }
            Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32());
            Assert.Equal(skill.CooldownMs, r.ReadUInt32()); Assert.Equal(0, r.ReadByte());
            SkipStats(r);
            foreach (var _ in skill.ExtraStatSets) SkipStats(r);
        }
        for (int i = 0; i < removed; i++) r.ReadUInt16();
        int seeds = r.ReadByte();
        for (int i = 0; i < seeds; i++) { r.ReadUInt32(); r.ReadUInt32(); }
        int updates = r.ReadByte();
        for (int i = 0; i < updates; i++) { r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); }
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
    }

    static void ReadReference(BinaryReader r, uint row)
    {
        Assert.Equal(0, r.ReadByte()); Assert.Equal(row, r.ReadUInt32());
    }

    static void SkipStats(BinaryReader r)
    {
        uint count = ReadCompact(r);
        for (uint i = 0; i < count; i++) { ReadCompact(r); ReadCompact(r); }
    }

    // Counts/keys and signed values share prefix lengths; only counts need a decoded unsigned value here.
    static uint ReadCompact(BinaryReader r)
    {
        byte first = r.ReadByte();
        int more = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        uint value = (uint)(first & (0x7f >> more));
        for (int i = 0; i < more; i++) value = (value << 8) | r.ReadByte();
        return value;
    }
}
