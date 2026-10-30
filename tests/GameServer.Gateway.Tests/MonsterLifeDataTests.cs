using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MonsterLifeDataTests
{
    [Fact]
    public void Catalog_binds_life_to_inherited_base_type_and_fails_closed_for_old_or_partial_data()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"localexile-life-{Guid.NewGuid():n}");
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        string typesFile = Path.Combine(folder, "object-types.json");
        string varietiesFile = Path.Combine(tables, "MonsterVarieties.json");
        try
        {
            var configurations = new object?[]
            {
                new { corpse_usable = true, prevent_corpse_sinking = true, corpse_use_veto = false },
                new { corpse_usable = false, prevent_corpse_sinking = false, corpse_use_veto = false },
                new { corpse_usable = true, prevent_corpse_sinking = false, corpse_use_veto = true },
                new { corpse_usable = true }, null,
            };
            File.WriteAllText(typesFile, JsonSerializer.Serialize(new { types = configurations.Select((life, i) =>
                new { type = $"Base{i}", components = MonsterObject.BaseComponents, read = MonsterObject.BaseComponents.Length,
                    basic_actions = new[] { "Move", "Emerge" }, transitionable = new { num_states = 3 }, life }) }));
            File.WriteAllText(varietiesFile, JsonSerializer.Serialize(configurations.Select((_, i) => new
            {
                Id = $"DistinctVariety{i}", Name = $"Monster{i}", BaseMonsterTypeIndex = $"Base{i}", AOFiles = Array.Empty<string>(),
                LifeMultiplier = 100, MonsterTypesKey = 0, MovementSpeed = 30, ObjectSize = 3, MaxAgroRange = 60,
                GrantedEffectsKeys = new[] { 406, 5590 }, AttackSpeed = 1170, MaximumAttackDistance = 8,
                DamageMultiplier = 100, ExperienceMultiplier = 100,
            })));
            var catalog = new MonsterCatalog(folder);
            Assert.Null(catalog.Error);
            Assert.True(catalog.Find("0")!.LifeData.IsUsableAsCorpse(0));
            Assert.Equal(3, catalog.Find("0")!.LifeData.Flags);
            foreach (string row in new[] { "1", "2", "3", "4" })
                Assert.False(catalog.Find(row)!.LifeData.IsUsableAsCorpse(0));
            Assert.False(catalog.Find("1")!.LifeData.CorpseUsable);
            Assert.True(catalog.Find("2")!.LifeData.CorpseUseVeto);
            Assert.Null(catalog.Find("3")!.LifeData.CorpseUseVeto);
            Assert.Null(catalog.Find("4")!.LifeData.CorpseUsable);
            Assert.All(Enumerable.Range(0, 5), i =>
            {
                Assert.Equal(3, catalog.Find(i.ToString())!.TransitionStates);
                Assert.Equal(new[] { 406, 5590 }, catalog.Find(i.ToString())!.GrantedEffects);
            });
        }
        finally
        {
            File.Delete(varietiesFile);
            File.Delete(typesFile);
            Directory.Delete(tables);
            Directory.Delete(folder);
        }
    }

}
