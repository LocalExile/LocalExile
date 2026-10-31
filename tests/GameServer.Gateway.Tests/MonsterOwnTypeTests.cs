using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>A MonsterVarieties row whose Id is itself an object type (Metadata/Monsters/KitavaBoss/KitavaFinal.ot) is
/// written with that type's components, not its BaseMonsterTypeIndex's (Kitava1): the client builds the object from
/// the Id's own .ot, and KitavaFinal with Kitava1's Transitionable byte showed "Buffer Underflow" (live 2026-10-30).</summary>
public sealed class MonsterOwnTypeTests
{
    [Fact]
    public void A_variety_with_its_own_type_takes_that_types_components()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"localexile-owntype-{Guid.NewGuid():n}");
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        try
        {
            string[] states = ["boss_life_bar", "disable_life_bar", "global_first_aggro"];
            object Type(string type, params string[] extra) => new
            {
                type, components = MonsterObject.BaseComponents.Concat(extra).ToArray(), read = MonsterObject.BaseComponents.Length + extra.Length,
                shared_states = states.Select(s => new object[] { s, 0 }).ToArray(), shared_timers = Array.Empty<object>(),
                transitionable = new { num_states = 2 },
            };
            File.WriteAllText(Path.Combine(folder, "object-types.json"), JsonSerializer.Serialize(new
            {
                types = new[]
                {
                    Type("Metadata/Monsters/KitavaBoss/Kitava1", "StateMachine", "Transitionable"),
                    Type("Metadata/Monsters/KitavaBoss/KitavaFinal", "StateMachine"),
                },
            }));
            object Row(string id) => new
            {
                Id = id, Name = "Kitava, the Insatiable", BaseMonsterTypeIndex = "Metadata/Monsters/KitavaBoss/Kitava1", AOFiles = Array.Empty<string>(),
                LifeMultiplier = 100, MonsterTypesKey = 0, MovementSpeed = 8, ObjectSize = 3, MaxAgroRange = 60,
                GrantedEffectsKeys = Array.Empty<int>(), AttackSpeed = 1500, MaximumAttackDistance = 8, DamageMultiplier = 100, ExperienceMultiplier = 100,
            };
            File.WriteAllText(Path.Combine(tables, "MonsterVarieties.json"), JsonSerializer.Serialize(new[]
            {
                Row("Metadata/Monsters/KitavaBoss/Kitava1"), Row("Metadata/Monsters/KitavaBoss/KitavaFinal"), Row("Metadata/Monsters/KitavaBoss/Kitava1Standalone"),
            }));
            var catalog = new MonsterCatalog(folder);
            Assert.Equal("StateMachine", catalog.Find("1")!.ComponentsOrBase[^1]);
            Assert.DoesNotContain("Transitionable", catalog.Find("1")!.ComponentsOrBase);
            Assert.True(catalog.Find("1")!.Supported);
            // A row without a type of its own keeps its base's (Kitava1Standalone → Kitava1), as before.
            Assert.Equal("Transitionable", catalog.Find("2")!.ComponentsOrBase[^1]);
            Assert.Equal("Transitionable", catalog.Find("0")!.ComponentsOrBase[^1]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
