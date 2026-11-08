using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class SkillBehaviourVariationTests
{
    // The King in the Mists' ritual (#497): the client builds a geometry channel from the GeometryAttack row whose Id is the
    // record's skill_behaviour_variation and takes that row's GeometryChannel. Without the stat it read Id 0, a row with no
    // channel, and crashed in GeometryChannelBase::StartChannelling. The value is the GrantedEffects row's SkillID.
    [Fact]
    public void Ritual_record_variation_names_its_geometry_channel()
    {
        var skills = new SkillCatalog();
        int ritual = skills.EffectRow("GSVoodooKingRitual");
        Assert.True(ritual >= 0);
        int variation = skills.BehaviourVariation(ritual);
        Assert.Equal(1116, variation);
        Assert.Equal(0, skills.BehaviourVariation(skills.EffectRow("MeleeAtAnimationSpeedUnique")));

        using var attacks = JsonDocument.Parse(File.ReadAllBytes("data/tables/GeometryAttack.json"));
        using var channels = JsonDocument.Parse(File.ReadAllBytes("data/tables/GeometryChannel.json"));
        int? Channel(int id) => attacks.RootElement.EnumerateArray().First(r => r.GetProperty("Id").GetInt32() == id)
            .GetProperty("GeometryChannel") is { ValueKind: JsonValueKind.Number } c ? c.GetInt32() : null;
        Assert.Null(Channel(0));
        Assert.Equal("GSVoodooKingRitual", channels.RootElement[Channel(variation)!.Value].GetProperty("Id").GetString());
    }
}
