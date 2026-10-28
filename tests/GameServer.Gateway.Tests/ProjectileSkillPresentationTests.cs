using System.Text;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static ProjectileUse CapturePresentation(Fixture f, ProjectileRuntimeFixture runtime, string effect)
    {
        var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn.Character!)!).Single(g => g.Effect == f.Effect(effect));
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
    [InlineData("KineticBlast", 1, "Metadata/Effects/Microtransactions/Spells/cluster_burst/celestial/kb_extra_01.ao")]
    [InlineData("KineticBlastAltX", 1, "Metadata/Effects/Microtransactions/Spells/cluster_burst/celestial/kb_extra_01.ao")]
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

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    public async Task Fireball_wall_collision_commits_area_damage_and_publishes_selected_native_explosion(int variation)
    {
        using var f = new Fixture("Fireball");
        string area = "projectile-wall-" + Guid.NewGuid().ToString("N");
        string stem = Path.Combine("data", "walkability", area + "_seed" + f.Conn.Seed);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        try
        {
            File.WriteAllText(stem + ".json", "{\"w\":128,\"h\":128}");
            byte[] cells = Enumerable.Repeat((byte)1, 128 * 128).ToArray();
            for (int y = 0; y < 128; y++) cells[y * 128 + 60] = 0;
            File.WriteAllBytes(stem + ".projectile.bin", cells);
            f.Conn.Use(f.Conn.Layout with { Id = area });
            var target = new SpawnedMonster("wall neighbor", new(56, 38, 0), 0, 2, 10000);
            f.Conn.Monsters.Add(42, target);
            if (variation != 0) WearVariation(f, "Fireball", variation);
            await using var runtime = new ProjectileRuntimeFixture(f.Conn);
            var use = CapturePresentation(f, runtime, "Fireball");
            ProjectileEvent? explosion = null;
            var shot = (ProjectileOffense)use.Payload!;
            var presentation = shot.Presentation!;
            use = use with { Payload = shot with { Presentation = presentation with { EventEffect = value =>
            {
                if (value.Kind == ProjectileEventKind.Area) explosion = value;
                return presentation.EventEffect!(value);
            } } } };
            runtime.Release(use with { Profile = use.Profile with { Speed = 100, Range = 80 } });
            await runtime.Read();
            runtime.Advance(.4);
            Assert.Empty(runtime.Session.Runtime.Projectiles);
            Assert.True(target.Life < 10000);
            string ao = variation == 0 ? "Metadata/Effects/Spells/fireball/explode_new2.ao"
                : "Metadata/Effects/Microtransactions/Spells/fireball/stygian/aoe.ao";
            float scale = (float)(use.Profile.EndArea!.Radius * (250d / 23) / Program.Art.ArtRadius(ao)!.Value);
            byte[] bytes = await runtime.Read();
            Assert.NotNull(explosion);
            Assert.Equal(59.5, explosion.Position.X, 6);
            Assert.True(bytes.AsSpan().IndexOf(new EffectObject((int)Math.Round(explosion.Position.X), 32, ao)
                { Scale = scale }.Blob()) >= 0);
        }
        finally
        {
            File.Delete(stem + ".projectile.bin");
            File.Delete(stem + ".json");
        }
    }
}
