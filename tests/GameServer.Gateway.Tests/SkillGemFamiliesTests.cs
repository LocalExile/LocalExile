using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The gem families the passive tree's family gem-level stats raise, classified from the current client's own metadata
// (GemTags stat columns and SkillGems MinionGlobalSkillLevelStat), and their composition with the global gem bonus
// through PassiveGemModifiers (Agents/docs/protocol/19-gems.md). Reads the extracted game tables.
public class SkillGemFamiliesTests
{
    static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["PassiveSkills", "Stats", "SkillGems", "GemEffects", "GemTags", "BaseItemTypes",
                "GrantedEffects", "GrantedEffectsPerLevel", "GrantedEffectStatSets", "GrantedEffectStatSetsPerLevel",
                "GrantedEffectQualityStats"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }

    static readonly ItemCatalog Items = new(Data() ?? "data");
    static readonly SkillCatalog Skills = new(Data() ?? "data");
    static readonly StatCatalog Stats = new(Data() ?? "data");

    // Data-backed: on a fresh clone without the tables the members are empty and the family stats stay off.
    static bool Available => Data() is not null;

    static int BaseRow(string name) => Items.FindGem(name)!.Value.Type.Row;

    static Item Gem(string name, int level = 1, int quality = 0)
    {
        var (type, variant) = Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type) with
        {
            GemExperience = Items.Gem(type)!.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality,
        };
    }

    // The Gems 1 chain exactly as GemSkillEffects runs it: the allocated nodes' stats, PassiveGemModifiers.From over
    // them, and the family predicate from the gem's SkillGems row (the base item type's row).
    static (int Level, int Quality) Effective(IEnumerable<ushort> nodes, Item gem)
    {
        var allocated = new PassiveTree(Data()!).Stats(nodes);
        var mods = PassiveGemModifiers.From(stat => Stats.Key(stat) is uint key ? allocated.GetValueOrDefault(key) : 0);
        int gemRow = Items.ByHash(gem.TypeHash)?.Row ?? -1;
        return mods.Apply(Items.GemLevel(gem), gem.Quality, family: stat => Skills.FamilyStatRaises(stat, gemRow));
    }

    [Fact]
    public void The_herald_stat_raises_the_herald_tag_gems()
    {
        if (!Available) return; // Real-client cross-check is optional on a fresh clone.
        var families = new SkillGemFamilies(Data()!);
        int row = (int)Stats.Key("herald_skill_gem_level_+")!.Value - 1;

        Assert.True(families.Raises(row, BaseRow("Herald of Ash")));
        Assert.True(families.Raises(row, BaseRow("Herald of Ice")));
        Assert.True(families.Raises(row, BaseRow("Herald of Thunder")));
        Assert.True(families.Raises(row, BaseRow("Herald of Agony")));
        Assert.True(families.Raises(row, BaseRow("Herald of Purity")));
        // The tag's support carries the stat too (Scornful Herald Support); PassiveGemModifiers keeps the bonus off it.
        Assert.True(families.Raises(row, BaseRow("Scornful Herald Support")));
        // No display-name guessing: nothing else carries the tag.
        Assert.False(families.Raises(row, BaseRow("Fireball")));
        Assert.False(families.Raises(row, BaseRow("Raise Zombie")));
        Assert.False(families.Raises(row, -1));
    }

    [Fact]
    public void The_vaal_stat_raises_the_vaal_gems_whole_including_both_granted_skills()
    {
        if (!Available) return;
        var families = new SkillGemFamilies(Data()!);
        int row = (int)Stats.Key("vaal_skill_gem_level_+")!.Value - 1;

        Assert.True(families.Raises(row, BaseRow("Vaal Fireball")));
        Assert.True(families.Raises(row, BaseRow("Vaal Ice Nova of Frostbolts")));
        // The two Vaal supports carry the tag; the support gate excludes them from the bonus.
        Assert.True(families.Raises(row, BaseRow("Vaal Sacrifice Support")));
        Assert.False(families.Raises(row, BaseRow("Fireball")));
        Assert.False(families.Raises(row, BaseRow("Herald of Ash")));

        // A Vaal gem grants two skills from one gem (its Vaal skill and its base skill); the classification is the
        // gem's SkillGems row, which both effects' levels are composed against — one boosted gem, two boosted skills.
        var vaal = Gem("Vaal Fireball", 20);
        int[] effects = [.. Items.GemEffects(vaal)];
        Assert.Equal([Skills.EffectRow("VaalFireball"), Skills.EffectRow("Fireball")], effects);
        Assert.True(Skills.FamilyStatRaises("vaal_skill_gem_level_+", BaseRow("Vaal Fireball")));
    }

    [Fact]
    public void The_link_stat_raises_the_link_tag_gems()
    {
        if (!Available) return;
        var families = new SkillGemFamilies(Data()!);
        int row = (int)Stats.Key("link_skill_gem_level_+")!.Value - 1;

        string[] links = ["Soul Link", "Flame Link", "Vampiric Link", "Protective Link", "Intuitive Link", "Destructive Link"];
        foreach (string name in links)
            Assert.True(families.Raises(row, BaseRow(name)));
        Assert.True(families.Raises(row, BaseRow("Frigid Bond Support"))); // a support: the bonus stays off it
        Assert.False(families.Raises(row, BaseRow("Herald of Ash")));
        Assert.False(families.Raises(row, BaseRow("Fireball")));
    }

    [Fact]
    public void The_raise_zombie_stat_comes_from_the_gems_own_minion_global_column()
    {
        if (!Available) return;
        var families = new SkillGemFamilies(Data()!);
        int row = (int)Stats.Key("raise_zombie_gem_level_+")!.Value - 1;

        Assert.True(families.Raises(row, BaseRow("Raise Zombie")));
        // A transfigured variant is the same SkillGems row: it stays in its gem's family.
        Assert.True(families.Raises(row, BaseRow("Raise Zombie of Slamming")));
        Assert.False(families.Raises(row, BaseRow("Raise Spectre")));
        Assert.False(families.Raises(row, BaseRow("Fireball")));
        // An unknown stat raises nothing.
        Assert.False(families.Raises(int.MaxValue, BaseRow("Raise Zombie")));
    }

    [Fact]
    public void The_four_Phrecia_nodes_grant_their_family_stat_and_lose_it_on_removal()
    {
        if (!Available) return;
        var tree = new PassiveTree(Data()!);
        int Herald() => tree.Stats([40058]).GetValueOrDefault((uint)21149);
        int Architect() => tree.Stats([45399]).GetValueOrDefault((uint)10858);
        int Paladin() => tree.Stats([34448]).GetValueOrDefault((uint)17985);
        int Puppeteer() => tree.Stats([18817]).GetValueOrDefault((uint)10397);

        Assert.Equal(3, Herald());
        Assert.Equal(3, Architect());
        Assert.Equal(5, Paladin());
        Assert.Equal(5, Puppeteer());
        // Without the node (or with another node) the stat is gone.
        Assert.Equal(0, tree.Stats([]).GetValueOrDefault((uint)21149));
        Assert.Equal(0, tree.Stats([54114]).GetValueOrDefault((uint)21149));
        Assert.Equal(0, tree.Stats([]).GetValueOrDefault((uint)10397));
    }

    [Fact]
    public void Apocalypse_boosts_a_herald_gems_effective_level_and_leaves_its_requirements_experience_and_quality_intrinsic()
    {
        if (!Available) return;
        var herald = Gem("Herald of Ash", 20, 17);
        var fireball = Gem("Fireball", 20, 17);
        int effect = Skills.EffectRow("HeraldOfAsh");

        var boosted = Effective([40058], herald);
        Assert.Equal((23, 17), boosted);
        var untouched = Effective([40058], fireball);
        Assert.Equal((20, 17), untouched);

        // The stored gem keeps its experience, quality and intrinsic level, and the requirements still come from that
        // level: the effective level exists only for the skill's stats, damage, reservation and recovery.
        Assert.Equal(20, Items.GemLevel(herald));
        Assert.Equal(Items.Gem(Items.FindGem("Herald of Ash")!.Value.Type)!.ExperienceFor(20), herald.GemExperience);
        Assert.Equal((byte)17, herald.Quality);
        Assert.NotEqual(Skills.PlayerLevelReq(effect, 23), Skills.PlayerLevelReq(effect, 20));
        Assert.Equal(Skills.PlayerLevelReq(effect, 20), Skills.PlayerLevelReq(effect, Items.GemLevel(herald)));
        // Without the node the gem is back at its intrinsic level (removal), and so is it after a reload of the catalogs.
        Assert.Equal((20, 17), Effective([], herald));
        Assert.Equal((23, 17), Effective([40058], herald));
    }

    [Fact]
    public void Chaos_Reigns_boosts_both_skills_of_a_vaal_gem_and_Flesh_Puppets_the_raise_zombie_levels()
    {
        if (!Available) return;
        var vaal = Gem("Vaal Fireball", 20, 0);
        var zombie = Gem("Raise Zombie", 20, 0);

        // The Architect's Chaos Reigns (+3 to Vaal skill gems): both of the gem's skills at the boosted level.
        var architect = new PassiveTree(Data()!).Stats([45399]);
        var mods = PassiveGemModifiers.From(stat => Stats.Key(stat) is uint key ? architect.GetValueOrDefault(key) : 0);
        Assert.Equal((23, 0), mods.Apply(Items.GemLevel(vaal), vaal.Quality,
            family: stat => Skills.FamilyStatRaises(stat, BaseRow("Vaal Fireball"))));

        // The Puppeteer's Flesh Puppets (+5 to Raise Zombie gems): the mana cost follows the boosted level's row.
        var puppeteer = new PassiveTree(Data()!).Stats([18817]);
        var zombieMods = PassiveGemModifiers.From(stat => Stats.Key(stat) is uint key ? puppeteer.GetValueOrDefault(key) : 0);
        var (level, _) = zombieMods.Apply(Items.GemLevel(zombie), zombie.Quality,
            family: stat => Skills.FamilyStatRaises(stat, BaseRow("Raise Zombie")));
        Assert.Equal(25, level);
        int zombieEffect = Skills.EffectRow("RaiseZombie");
        Assert.Equal(28, Skills.EffectStats(zombieEffect, 20).FirstOrDefault(kv => kv.Item1 == Stats.Key("base_mana_cost")).Item2);
        Assert.Equal(33, Skills.EffectStats(zombieEffect, level).FirstOrDefault(kv => kv.Item1 == Stats.Key("base_mana_cost")).Item2);
        Assert.Equal(20, Items.GemLevel(zombie));
    }
}
