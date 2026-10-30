using System.Text;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static ProjectileUse CapturePresentation(Fixture f, ProjectileRuntimeFixture runtime, string effect)
    {
        var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn)!).Single(g => g.Effect == f.Effect(effect));
        return (ProjectileUse)Call("CaptureGemProjectile", f.Conn, runtime.Session, gem.Id, gem, (110, 32))!;
    }

    static void WearVariation(Fixture f, string skill, int variation)
    {
        var gem = Program.Items.Find($"Metadata/Items/Gems/SkillGem{skill}")!;
        uint key = Program.Stats.Key("skill_art_variation")!.Value;
        var record = Program.Mtx.Owned.First(r => Program.MtxEffects.FitsSkill(r.MtxTypeHash, gem, 0)
            && Program.MtxEffects.Of(r.MtxTypeHash)!.Stats.Any(s => s.Stat == key && s.Value == variation));
        CharacterSaves.SetSkillCosmetic(f.Conn.Character!, gem.Hash, 0, new(record.Id));
    }

    [Theory]
    [InlineData("KineticBlast", 0, "Metadata/Effects/Spells/cluster_burst/mini.ao")]
    public async Task Captured_KB_bursts_use_secondary_art_and_the_equipped_variation(string effect, int variation, string ao)
    {
        using var f = new Fixture("KineticBlast");
        if (effect != "KineticBlast")
        {
            var type = Program.Items.Find("Metadata/Items/Gems/SkillGemKineticBlast")!;
            var variant = Program.Items.Gem(type)!.Variants.Single(v => v.Effect == f.Effect(effect));
            f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
                { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { GemVariant = variant.Index } }] });
        }
        if (variation != 0) WearVariation(f, "KineticBlast", variation);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var use = CapturePresentation(f, runtime, effect);
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(ao, shot.AreaVisual);
        if (variation != 0) Assert.Contains("Celestial", use.Profile.Id);
        runtime.Release(use with { Profile = use.Profile with { Range = 20 } });
        await runtime.Read();
        runtime.Advance(.3);
        byte[] bytes = await runtime.Read();
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(ao)) >= 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("cluster_burst/big.ao")) < 0);
    }

}
