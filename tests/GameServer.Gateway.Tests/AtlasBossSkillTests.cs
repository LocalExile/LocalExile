namespace GameServer.Gateway.Tests;

// The Atlas pinnacle bosses played one granted effect as a plain swing, because nothing took their kit: their varieties
// list it in GrantedEffectsKeys and the client can only play an action whose skill id its actor owns (AtlasBosses.cs).
public class AtlasBossSkillTests
{
    // The four rows that fight with Sirus' kit; the standalone arenas grant everything but the two teleports.
    static readonly string[] SirusVarieties =
    [
        AtlasBosses.Sirus, AtlasBosses.SirusGauntlet, AtlasBosses.SirusStandalone, AtlasBosses.SirusStandaloneMirage,
    ];

    [Fact]
    public void The_bosses_kits_are_the_effects_their_varieties_granted()
    {
        var skills = new SkillCatalog();
        var monsters = new MonsterCatalog();
        var shaper = monsters.Find(AtlasBosses.Shaper);
        var elder = monsters.Find(AtlasBosses.Elder);
        Assert.NotNull(shaper);
        Assert.NotNull(elder);
        Assert.True(AtlasBosses.IsShaper(shaper));
        Assert.True(AtlasBosses.IsElder(elder));
        Assert.False(AtlasBosses.IsBoss(monsters.Find("Metadata/Monsters/Zombie/Zombie")));

        Assert.Equal("ShaperMelee", skills.EffectId(AtlasBosses.BasicEffect(shaper!)));
        Assert.Equal("Melee", skills.EffectId(AtlasBosses.BasicEffect(elder!)));
        // Each id in the kit is one of the boss's own granted effects and carries a clip the actor can play.
        foreach (int effect in AtlasBosses.ShaperEffects) Assert.Contains(effect, shaper!.GrantedEffects!);
        foreach (int effect in AtlasBosses.ElderEffects) Assert.Contains(effect, elder!.GrantedEffects!);
        Assert.Equal("AtlasBossCelestialBeam", skills.EffectId(6093));
        Assert.Equal("ElderSiphon", skills.EffectId(9058));

        // Sirus never swings a weapon: his record's own skill is the beam blast, and every id of his kit is granted too.
        foreach (string id in SirusVarieties)
        {
            var sirus = monsters.Find(id);
            Assert.NotNull(sirus);
            Assert.True(AtlasBosses.IsSirus(sirus));
            Assert.True(AtlasBosses.IsBoss(sirus));
            Assert.False(AtlasBosses.MeleeLike(sirus!));
            Assert.Equal(AtlasBosses.SirusBeamBlastEffect, AtlasBosses.BasicEffect(sirus!));
            foreach (int effect in AtlasBosses.SirusEffects) Assert.Contains(effect, sirus!.GrantedEffects!);
        }
        // AtlasExilesBoss5 is a phased arena (MapBossFights): WorldAreas lists the throne row first and the walking row as
        // the held-back last boss, so a freshly opened arena contains only the throne — it has to be Sirus too, or the
        // player walks into an arena whose only boss does nothing.
        var throne = monsters.Find(AtlasBosses.SirusThrone);
        Assert.NotNull(throne);
        Assert.True(AtlasBosses.IsSirus(throne));
        Assert.True(AtlasBosses.IsSirusSeated(throne));
        Assert.False(AtlasBosses.IsSirusSeated(monsters.Find(AtlasBosses.Sirus)));
        // The throne row grants no effects of its own: the kit it plays is the controller's, which is why it needs the
        // actor list from SirusEffects and not from its own row.
        Assert.Empty(throne!.GrantedEffects!);

        // Sirus' effect rows carry their base physical damage as a fraction of his own, not as a flat hit: the corridor
        // beam is 1.6-2.4 of it, so a 10 base becomes 16-24 (the fractions are why the row cannot be read as the hit).
        var corridor = AtlasBosses.SirusDamage(AtlasBosses.SirusCorridorBlastEffect, 10, 10);
        Assert.Equal(16, corridor.MinTotal);
        Assert.Equal(24, corridor.MaxTotal);
    }

    [Fact]
    public void The_three_bosses_never_share_a_skill_id_and_none_is_a_gem_id()
    {
        var monsters = new MonsterCatalog();
        var shaper = monsters.Find(AtlasBosses.Shaper)!;
        var elder = monsters.Find(AtlasBosses.Elder)!;
        var sirus = monsters.Find(AtlasBosses.Sirus)!;
        var ids = new List<ushort>();
        foreach (int effect in AtlasBosses.ShaperEffects) ids.Add(AtlasBosses.Skill(shaper, effect));
        foreach (int effect in AtlasBosses.ElderEffects) ids.Add(AtlasBosses.Skill(elder, effect));
        foreach (int effect in AtlasBosses.SirusEffects) ids.Add(AtlasBosses.Skill(sirus, effect));

        // A gem id here makes the client start that gem's action instead of the boss's clip (0xc0000005, live 2026-10-31).
        foreach (ushort id in ids)
        {
            Assert.True(id >= 0x4000, $"skill id 0x{id:x4} is in the player's range");
            Assert.False(id is >= 0x0103 and <= 0x0142, $"skill id 0x{id:x4} is a gem id");
        }
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // The families are what keeps them apart: the Shaper and the Elder stand in one arena together.
        Assert.All(AtlasBosses.SirusEffects, e => Assert.InRange(AtlasBosses.Skill(sirus, e), 0x4020, 0x403f));
    }
}
