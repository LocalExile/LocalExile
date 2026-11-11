using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class DropGenerationTests
{
    [Fact]
    public void Equipment_drop_accepts_eligible_same_slot_replacements_and_keeps_other_rolls()
    {
        var items = Program.Items;
        var rolls = new ItemRolls(items);
        var armour = items.All.First(t => t.Id == "Metadata/Items/Armours/BodyArmours/BodyStr1");
        var item = ItemCatalog.Create(armour) with { ItemLevel = 20 };
        bool replaced = false;
        for (int seed = 0; seed < 20; seed++)
        {
            var ordinary = rolls.Roll(item, ItemRarity.Magic, new(seed));
            var invalid = rolls.RollEquipmentDrop(item, ItemRarity.Magic, new(seed),
                (selected, pool, _) => pool.FirstOrDefault(p => p.Mod.GenerationType != selected.GenerationType).Mod?.Hash);
            Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(invalid));
            var changed = rolls.RollEquipmentDrop(item, ItemRarity.Magic, new(seed),
                (selected, pool, _) => pool.First(p => p.Mod.GenerationType == selected.GenerationType).Mod.Hash);
            replaced |= !ordinary.Mods.Select(m => m.Hash).SequenceEqual(changed.Mods.Select(m => m.Hash));
            Assert.All(changed.Mods.Where(m => m.Section == ModSection.Explicit),
                m => Assert.InRange(items.Mods.ByHash(m.Hash)!.Level, 0, 20));
        }
        Assert.True(replaced);
        foreach (string cls in new[] { "LifeFlask", "ManaFlask", "UtilityFlask", "HybridFlask", "Jewel", "AbyssJewel" })
        {
            var type = items.All.First(t => t.Class == cls && t.Supported && t.DropLevel > 0);
            var excluded = ItemCatalog.Create(type) with { ItemLevel = 86 };
            Assert.Equal(JsonSerializer.Serialize(rolls.Roll(excluded, ItemRarity.Magic, new(9))),
                JsonSerializer.Serialize(rolls.RollEquipmentDrop(excluded, ItemRarity.Magic, new(9),
                    (_, _, _) => throw new InvalidOperationException("Unexpected selector call"))));
        }
    }

    [Fact]
    public void Script_enable_disable_and_reload_release_both_loot_hooks()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-top-tier-" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(folder, "toptierdrops");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), """{"id":"toptierdrops","enabled":false}""");
        File.WriteAllText(Path.Combine(mod, "main.cs"), """
            public class TopTier : IMod
            {
                public void Load(IModContext mod) => GameEvents.DropGeneration.Add(mod.Manifest.Id, d => d.SelectEquipmentAffix = (selected, _, _) => selected.Hash);
            }
            """);
        try
        {
            using var host = new ModHost(folder, _ => { });
            host.Start(watch: false);
            var conn = new ClientState(new(1, "top-tier-test", 100, 100, 10, 10, null, []), 1, 1);
            Assert.Null(GameEvents.RunDropGeneration(conn, ItemDropSource.Monster).SelectEquipmentAffix);
            Assert.True(host.SetEnabled("toptierdrops", true));
            Assert.True(host.IsLoaded("toptierdrops"), string.Join(';', host.Mods().Select(m => m.Error)));
            Assert.NotNull(GameEvents.RunDropGeneration(conn, ItemDropSource.Monster).SelectEquipmentAffix);
            Assert.NotNull(GameEvents.RunDropGeneration(conn, ItemDropSource.Container).SelectEquipmentAffix);
            host.Reload("toptierdrops");
            Assert.NotNull(GameEvents.RunDropGeneration(conn, ItemDropSource.Monster).SelectEquipmentAffix);
            Assert.True(host.SetEnabled("toptierdrops", false));
            Assert.Null(GameEvents.RunDropGeneration(conn, ItemDropSource.Monster).SelectEquipmentAffix);
            Assert.Null(GameEvents.RunDropGeneration(conn, ItemDropSource.Container).SelectEquipmentAffix);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
