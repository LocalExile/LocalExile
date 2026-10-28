using System.IO.Compression;
using System.Text;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;

namespace GameServer.Core.Tests;

public class PathOfBuildingTests
{
    const string Build = """
        <PathOfBuilding>
          <Build level="95" className="Witch" ascendClassName="None" />
          <Tree activeSpec="2">
            <Spec classId="3" ascendClassId="0" nodes="1" />
            <Spec classId="3" ascendClassId="0" treeVersion="3_29" nodes="100,200,300"
                  masteryEffects="{200,456}">
              <Sockets><Socket nodeId="300" itemId="4" /><Socket nodeId="999" itemId="0" /></Sockets>
              <URL>https://www.pathofexile.com/passive-skill-tree/ignored-modern-spec-has-nodes</URL>
            </Spec>
          </Tree>
          <Items activeItemSet="8">
            <Item id="1">Rarity: NORMAL
        Driftwood Wand
        Sockets: B-B</Item>
            <Item id="2">Rarity: NORMAL
        Crude Bow</Item>
            <Item id="4">Rarity: NORMAL
        Cobalt Jewel</Item>
            <ItemSet id="1"><Slot name="Weapon 1" itemId="2" /></ItemSet>
            <ItemSet id="8" useSecondWeaponSet="true">
              <Slot name="Weapon 1" itemId="1" /><Slot name="Helmet" itemId="0" />
            </ItemSet>
          </Items>
          <Skills activeSkillSet="3">
            <SkillSet id="1"><Skill enabled="true" slot="Weapon 1">
              <Gem nameSpec="Unselected" level="1" quality="0" />
            </Skill></SkillSet>
            <SkillSet id="3"><Skill enabled="true" slot="Weapon 1">
              <Gem nameSpec="Fireball" skillId="Fireball" gemId="Metadata/Items/Gems/SkillGemFireball"
                   variantId="Fireball" level="20" quality="21" enabled="true" />
              <Gem nameSpec="Arcane Surge" level="10" quality="0" enabled="false" />
            </Skill></SkillSet>
          </Skills>
        </PathOfBuilding>
        """;

    [Fact]
    public void Reads_export_code_using_selected_tree_items_and_skills()
    {
        var build = PathOfBuilding.Parse(Encode(Build));
        Assert.Equal(CharacterClass.Witch, build.Class);
        Assert.Equal(95, build.Level);
        Assert.Equal("3_29", build.Tree.Version);
        Assert.Equal(new uint[] { 100, 200, 300 }, build.Tree.Nodes);
        Assert.Equal((ushort)456, build.Tree.Masteries[200]);
        Assert.Equal(new PathOfBuildingJewelSocket(300, 4), Assert.Single(build.Tree.Jewels));
        Assert.Equal(3, build.Items.Count);
        Assert.Equal(new PathOfBuildingSlot("Weapon 1", 1), Assert.Single(build.Slots));
        Assert.Contains("Sockets: B-B", build.Items[0].Text);
        Assert.True(build.UseSecondWeaponSet);
        var skill = Assert.Single(build.Skills);
        Assert.True(skill.Enabled);
        Assert.Equal("Weapon 1", skill.Slot);
        Assert.Equal(2, skill.Gems.Count);
        Assert.Equal(new PathOfBuildingGem("Fireball", "Metadata/Items/Gems/SkillGemFireball", "Fireball",
            "Fireball", 20, 21, true), skill.Gems[0]);
        Assert.False(skill.Gems[1].Enabled);
    }

    [Fact]
    public void Accepts_bom_whitespace_and_wrapped_unpadded_code()
    {
        var code = Encode(Build).TrimEnd('=');
        string wrapped = " \r\n" + string.Join("\n", code.Chunk(71).Select(c => new string(c))) + "\n";
        Assert.Equal(95, PathOfBuilding.Parse(wrapped).Level);
        Assert.Equal(95, PathOfBuilding.Parse("\uFEFF  " + Build).Level);
    }

    [Fact]
    public void Reads_legacy_direct_slots_skill_groups_and_spec_tree_url()
    {
        // URL v6: Witch, no ascendancy; nodes 100 and 200, no clusters; mastery effect 456 at node 200.
        var url = Convert.ToBase64String(new byte[] { 0, 0, 0, 6, 3, 0, 2, 0, 100, 0, 200, 0, 1, 1, 200, 0, 200 });
        var build = PathOfBuilding.Parse($$"""
            <PathOfBuilding><Build className="Witch" level="80" />
              <Spec><URL>https://www.pathofexile.com/passive-skill-tree/{{url}}</URL></Spec>
              <Items useSecondWeaponSet="true">
                <Item id="7"><![CDATA[Rarity: NORMAL
            Driftwood Wand]]></Item><Slot name="Weapon 1" itemId="7" />
              </Items>
              <Skills defaultGemLevel="20" defaultGemQuality="15">
                <Skill active="true"><Gem nameSpec="Fireball" /></Skill>
              </Skills>
            </PathOfBuilding>
            """);
        Assert.Equal(new uint[] { 100, 200 }, build.Tree.Nodes);
        Assert.Equal((ushort)456, build.Tree.Masteries[200]);
        Assert.True(build.UseSecondWeaponSet);
        Assert.Equal(7, Assert.Single(build.Slots).ItemId);
        var group = Assert.Single(build.Skills);
        Assert.True(group.Enabled);
        Assert.Equal(20, Assert.Single(group.Gems).Level);
        Assert.Equal(15, group.Gems[0].Quality);
    }

    [Theory]
    [InlineData("Scion", 0)]
    [InlineData("Marauder", 1)]
    [InlineData("Ranger", 2)]
    [InlineData("Witch", 3)]
    [InlineData("Duelist", 4)]
    [InlineData("Templar", 5)]
    [InlineData("Shadow", 6)]
    public void Maps_pob_class_ids_exactly(string className, int id)
    {
        var result = PathOfBuilding.Parse($"<PathOfBuilding><Build className='{className}' /></PathOfBuilding>");
        Assert.Equal(id, (int)result.Class);
        Assert.Equal(id, result.Tree.ClassId);
    }

    [Theory]
    [InlineData("<PathOfBuilding2><Build className='Witch' /></PathOfBuilding2>", "PoE 2")]
    [InlineData("<PathOfBuilding><Build className='Monk' /></PathOfBuilding>", "class")]
    [InlineData("<PathOfBuilding><Build className='3' /></PathOfBuilding>", "class")]
    [InlineData("<PathOfBuilding><Build className='Witch' level='101' /></PathOfBuilding>", "level")]
    [InlineData("<PathOfBuilding><Build className='Witch' level='0' /></PathOfBuilding>", "level")]
    [InlineData("<PathOfBuilding><Build className='Witch' level='abc' /></PathOfBuilding>", "level")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Build className='Ranger'/></PathOfBuilding>", "Duplicate Build")]
    [InlineData("<PathOfBuilding />", "Build")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Tree activeSpec='2'><Spec classId='3' ascendClassId='0' nodes='' /></Tree></PathOfBuilding>", "specification")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='2' ascendClassId='0' nodes='' /></PathOfBuilding>", "does not match")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='3' ascendClassId='0' nodes='2,2' /></PathOfBuilding>", "duplicate nodes")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='3' ascendClassId='0' nodes='1,nope' /></PathOfBuilding>", "node")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='3' ascendClassId='0' nodes='1' masteryEffects='{1,70000}' /></PathOfBuilding>", "out of range")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='3' ascendClassId='0' nodes='1' masteryEffects='{1,2},broken' /></PathOfBuilding>", "masteryEffects")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec classId='3' ascendClassId='0' nodes='1' masteryEffects='{1,2},{1,3}' /></PathOfBuilding>", "Duplicate mastery")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Items activeItemSet='2'><ItemSet id='1'/></Items></PathOfBuilding>", "ItemSet 2")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Skills activeSkillSet='2'><SkillSet id='1'/></Skills></PathOfBuilding>", "SkillSet 2")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Items><Slot name='Helmet' itemId='1'/></Items></PathOfBuilding>", "missing item")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Items><Item id='1'>A</Item><Item id='1'>B</Item></Items></PathOfBuilding>", "Duplicate item")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Items><Slot name='Helmet' itemId='0'/><Slot name='Helmet' itemId='0'/></Items></PathOfBuilding>", "Duplicate equipment")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Skills><Skill enabled='maybe'/></Skills></PathOfBuilding>", "expected true or false")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Skills><Skill><Gem nameSpec='Fireball' level='41'/></Skill></Skills></PathOfBuilding>", "level")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Skills><Skill><Gem nameSpec='Fireball' level='20' quality='-1'/></Skill></Skills></PathOfBuilding>", "quality")]
    [InlineData("<PathOfBuilding><Build className='Witch' /><Spec><URL>AAAABgMAAgBk</URL></Spec></PathOfBuilding>", "truncated")]
    [InlineData("<PathOfBuilding", "XML")]
    [InlineData("https://pobb.in/example", "sharing link")]
    [InlineData("!!!", "Base64")]
    [InlineData("eA==", "truncated")]
    [InlineData("", "empty")]
    public void Rejects_invalid_and_ambiguous_builds(string xml, string message)
    {
        Assert.Contains(message, Assert.Throws<FormatException>(() => PathOfBuilding.Parse(xml)).Message);
    }

    [Fact]
    public void Retains_unsupported_features_for_explicit_materializer_rejection()
    {
        var build = PathOfBuilding.Parse("""
            <PathOfBuilding><Build className="Witch" ascendClassName="Elementalist" />
              <Spec classId="3" ascendClassId="2" secondaryAscendClassId="1" nodes="100,70000">
                <Overrides><Override nodeId="100" dn="Tattoo" /></Overrides>
              </Spec>
              <Items><Item id="2" variant="3" variantAlt="1">Rarity: UNIQUE
            Example</Item><Slot name="Helmet" itemId="2" /></Items>
              <Skills><Skill enabled="true" imbuedSupport="Lifetap" source="Item">
                <Gem nameSpec="Fireball" level="20" /></Skill></Skills>
            </PathOfBuilding>
            """);
        Assert.Equal("Elementalist", build.Ascendancy);
        Assert.Equal(2, build.Tree.AscendancyId);
        Assert.Equal(1, build.Tree.SecondaryAscendancyId);
        Assert.True(build.Tree.HasOverrides);
        Assert.Contains(70000u, build.Tree.Nodes);
        Assert.True(Assert.Single(build.Items).HasAlternateVariants);
        Assert.Equal(3, build.Items[0].Variant);
        Assert.Equal("Lifetap", Assert.Single(build.Skills).ImbuedSupport);
    }

    [Fact]
    public void Rejects_external_entities_and_compressed_bombs()
    {
        const string entity = """
            <!DOCTYPE PathOfBuilding [<!ENTITY external SYSTEM "file:///C:/private.txt">]>
            <PathOfBuilding><Build className="Witch" />&external;</PathOfBuilding>
            """;
        Assert.Contains("DTD", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(entity)).Message);
        string bomb = "<PathOfBuilding>" + new string('x', PathOfBuilding.MaximumXmlBytes) + "</PathOfBuilding>";
        Assert.Contains("expanded build", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(Encode(bomb))).Message);
        Assert.Contains("limit", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(bomb)).Message);
    }

    [Fact]
    public void Reads_legacy_nil_default_settings_and_preserves_nondefault_game_choices()
    {
        var build = PathOfBuilding.Parse("""
            <PathOfBuilding><Build className="Witch" bandit="Alira" pantheonMajorGod="BrineKing"
                pantheonMinorGod="Ralakesh" />
              <Spec classId="3" ascendClassId="0" secondaryAscendClassId="nil" nodes="" />
              <Items><Item id="1">Rarity: NORMAL
            Driftwood Wand<ModRange id="1" range="0.25" /></Item></Items>
              <Skills defaultGemQuality="nil"><Skill><Gem nameSpec="Fireball" level="20"
                  qualityId="Alternate1" /></Skill></Skills>
            </PathOfBuilding>
            """);
        Assert.Equal(0, build.Tree.SecondaryAscendancyId);
        Assert.Equal(0, Assert.Single(Assert.Single(build.Skills).Gems).Quality);
        Assert.Equal("Alternate1", build.Skills[0].Gems[0].QualityId);
        Assert.True(Assert.Single(build.Items).HasLegacyModRanges);
        Assert.Equal(0.25, Assert.Single(build.Items).ModRanges[1]);
        Assert.Equal("Alira", build.Bandit);
        Assert.Equal("BrineKing", build.PantheonMajor);
        Assert.Equal("Ralakesh", build.PantheonMinor);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Invalid_saved_modifier_ranges_are_rejected(string range)
    {
        string xml = $"<PathOfBuilding><Build className=\"Witch\"/><Items><Item id=\"1\">Rarity: NORMAL\nDriftwood Wand<ModRange id=\"1\" range=\"{range}\"/></Item></Items></PathOfBuilding>";
        Assert.Contains("ModRange", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(xml)).Message);
    }

    [Fact]
    public void Duplicate_saved_modifier_range_indices_are_rejected()
    {
        string xml = "<PathOfBuilding><Build className=\"Witch\"/><Items><Item id=\"1\">Rarity: NORMAL\nDriftwood Wand<ModRange id=\"1\" range=\"0.5\"/><ModRange id=\"1\" range=\"0.2\"/></Item></Items></PathOfBuilding>";
        Assert.Contains("repeats ModRange", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(xml)).Message);
    }

    [Fact]
    public void Rejects_truncated_checksum_and_trailing_compressed_garbage()
    {
        byte[] compressed = Convert.FromBase64String(Encode(Build).Replace('-', '+').Replace('_', '/'));
        string truncated = Convert.ToBase64String(compressed[..^4]);
        string trailing = Convert.ToBase64String([.. compressed, 10, 20, 30, 40]);
        Assert.Contains("checksum", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(truncated)).Message);
        Assert.Contains("checksum", Assert.Throws<FormatException>(() => PathOfBuilding.Parse(trailing)).Message);
    }

    static string Encode(string xml)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.UTF8.GetBytes(xml));
        return Convert.ToBase64String(output.ToArray()).Replace('+', '-').Replace('/', '_');
    }
}
