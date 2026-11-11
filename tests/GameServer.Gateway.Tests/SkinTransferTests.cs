using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class SkinTransferTests
{
    static ItemMod Mod(ItemCatalog items, string stat, int value) => items.Mods.All
        .First(m => m.Stats.Count == 1 && m.Stats[0].Id == stat)
        .With(ModSection.Explicit, [value], new Random(1));

    static string Saved(Item item, ItemCatalog items) => JsonSerializer.Serialize(CharacterSaves.ToSaved(item, items.Mods));

    [Fact]
    public void Damage_defences_and_globals_add_but_weapon_speed_and_critical_chance_stay_A()
    {
        using var mod = new AttachmentModFixture();
        byte[] native = Convert.FromHexString("02070000177300000008000000060000000100000002");
        Assert.Equal(new InstanceSkinTransfers.Bind(6003, 8, 6, 1, 2), InstanceSkinTransfers.ReadBind(native));
        byte[] reclaim = Convert.FromHexString("01220000000200000006");
        Assert.Equal(new InstanceSkinTransfers.Reclaim(2, 6), InstanceSkinTransfers.ReadReclaim(reclaim));
        for (int cut = 1; cut < native.Length; cut++)
        {
            Assert.Empty(InstanceProtocol.SplitClientMessages(native.AsSpan(0, cut), out var partial, out var unknown));
            Assert.Empty(unknown);
            byte[] next = [.. partial, .. native.AsSpan(cut).ToArray(), .. reclaim, 0x01, 0x53];
            Assert.Equal(new ushort[] { InstanceSkinTransfers.BindId, InstanceSkinTransfers.ReclaimId, InstanceProtocol.ExitToCharacterSelectId },
                InstanceProtocol.SplitClientMessages(next, out partial, out unknown).Select(m => m.Id));
            Assert.Empty(partial);
            Assert.Empty(unknown);
        }
        var items = Program.Items;
        var skills = new SkillCatalog("data");
        var wand = items.All.First(t => t.Class == "Wand" && t.Supported);
        var sword = items.All.First(t => t.Class == "One Hand Sword" && t.Supported);
        var fire = items.Mods.All.First(m => m.Stats.Count == 2
            && m.Stats[0].Id == "local_minimum_added_fire_damage" && m.Stats[1].Id == "local_maximum_added_fire_damage")
            .With(ModSection.Explicit, [10, 20], new Random(2));
        var a = ItemCatalog.Create(wand) with { Quality = 20, Mods = [Mod(items, "local_physical_damage_+%", 50),
            Mod(items, "local_attack_speed_+%", 20), Mod(items, "local_critical_strike_chance_+%", 50),
            Mod(items, "local_accuracy_rating", 10), Mod(items, "base_maximum_life", 60)] };
        var b = ItemCatalog.Create(sword) with { Quality = 10, Mods = [Mod(items, "local_physical_damage_+%", 100),
            Mod(items, "local_attack_speed_+%", 40), Mod(items, "local_critical_strike_chance_+%", 100),
            Mod(items, "local_accuracy_rating", 500), Mod(items, "base_maximum_life", 80), fire] };
        string beforeA = Saved(a, items), beforeB = Saved(b, items);
        var bound = a with { SkinTransferSource = b };
        Assert.Equal(60, SkinTransferStats.Sum([bound], items.Mods)["base_maximum_life"]);
        Assert.True(mod.Host.SetEnabled("equipmentfusion", true));
        mod.Host.Reload("equipmentfusion");
        Assert.True(EquipmentAttachmentPolicy.Current.GlobalModifiers);
        var wa = Game.WeaponIn(skills, items, 3, (a, wand))!;
        var wb = Game.WeaponIn(skills, items, 3, (b, sword))!;
        var combined = Game.WeaponIn(skills, items, 3, (bound, wand))!;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            Assert.Equal(wa.LocalDamage![type].Min + wb.LocalDamage![type].Min, combined.LocalDamage![type].Min, 8);
            Assert.Equal(wa.LocalDamage[type].Max + wb.LocalDamage[type].Max, combined.LocalDamage[type].Max, 8);
        }
        var noSpeed = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 0 };
        Assert.Equal(wa.AttackSeconds(noSpeed), combined.AttackSeconds(noSpeed), 9);
        var withSpeed = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 30 };
        Assert.Equal(wa.AttackSeconds(withSpeed), combined.AttackSeconds(withSpeed), 9);
        Assert.Equal(wa.Range, combined.Range);
        Assert.Equal(wa.WieldingWand, combined.WieldingWand);
        Assert.Equal(wa.Melee, combined.Melee);
        Assert.Equal(SkinTransferStats.Critical(a, items), SkinTransferStats.Critical(bound, items));
        var profile = Game.WeaponProfileFrom(row => row == PlayerInventories.Weapon1
            ? [new InventoryItem(1, 0, 0, bound)] : [], _ => false, items, true, () => false);
        Assert.Equal(SkinTransferStats.Critical(a, items), profile.Critical);
        Assert.Equal(510, profile.LocalAccuracy);
        Assert.Equal(510, combined.LocalSources!["local_accuracy_rating"]);
        Assert.True(profile.Wand);
        Assert.False(profile.Sword);
        Assert.Equal(60, ItemModStats.Sum([bound], items.Mods)["base_maximum_life"]); // upstream calculator stays item-only
        Assert.Equal(140, SkinTransferStats.Sum([bound], items.Mods)["base_maximum_life"]);
        var conditional = a with { SkinTransferSource = b with { Mods = [
            items.Mods.All.Single(m => m.Id == "WrathIncreasedLightningDamage").With(ModSection.Explicit, [40], new Random(2)),
            Mod(items, "additional_strength", 80)] } };
        uint lightning = Program.Stats.Key("lightning_damage_+%")!.Value;
        Assert.Equal(40, SkinTransferStats.Global([conditional], items.Mods, Program.Stats,
            new(Auras: new HashSet<string> { "wrath" })).GetValueOrDefault(lightning));
        Assert.Equal(0, SkinTransferStats.Global([conditional], items.Mods, Program.Stats,
            new(Auras: new HashSet<string>())).GetValueOrDefault(lightning));
        Assert.Equal(80L, SkinTransferStats.RawAttributes([conditional], items.Mods)["additional_strength"]);
        Assert.Equal(50, combined.LocalSources!["local_physical_damage_+%"]);
        var prohibited = bound with { SkinTransferSource = b with { Mods = [Mod(items, "local_weapon_no_physical_damage", 1)] } };
        Assert.Equal(wa.LocalDamage![DamageType.Physical], Game.WeaponIn(skills, items, 3, (prohibited, wand))!.LocalDamage![DamageType.Physical]);

        var armour = items.All.First(t => t.Class == "Body Armour" && items.Defences(t) is { ArmourMin: > 0 });
        var ca = ItemCatalog.Create(armour) with { Quality = 20, Mods = [Mod(items, "local_physical_damage_reduction_rating_+%", 50)] };
        var cb = ItemCatalog.Create(armour) with { Quality = 10, Mods = [Mod(items, "local_physical_damage_reduction_rating_+%", 100)] };
        Assert.Equal(SkinTransferStats.Defences(ca, items).Armour + SkinTransferStats.Defences(cb, items).Armour,
            SkinTransferStats.Defences(ca with { SkinTransferSource = cb }, items).Armour);
        Assert.Equal(beforeA, Saved(a, items));
        Assert.Equal(beforeB, Saved(b, items));
        Assert.Equal(a.Wire(), (bound with { SkinTransferSource = null }).Wire());
        var payload = bound.Payload();
        Assert.Equal(0x40, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10)));
        Assert.Equal(b.Payload(), payload.AsSpan(13, b.Payload().Length).ToArray()); // Base owns B, A's components stay separate
        byte[] root = [.. payload.AsSpan(0, 13).ToArray(), .. payload.AsSpan(13 + b.Payload().Length).ToArray()];
        root[10] &= 0xbf;
        Assert.Equal(a.Payload(), root);
        Assert.Null(new ItemRolls(items).Mirror(bound));
        Assert.True(mod.Host.SetEnabled("equipmentfusion", false));
        Assert.Equal(60, SkinTransferStats.Sum([bound], items.Mods)["base_maximum_life"]);
        var restored = Game.WeaponIn(skills, items, 3, (bound, wand))!;
        foreach (var type in DamageTypes.ConversionOrder)
            Assert.Equal(wa.LocalDamage![type], restored.LocalDamage![type]);
    }

    [Fact]
    public void Binding_reloads_switches_with_A_excludes_donor_skills_and_returns_B_atomically()
    {
        using var policy = EquipmentAttachmentPolicy.Use(new(true, true, true, true));
        string path = Path.Combine(Path.GetTempPath(), $"localexile-skin-transfer-{Guid.NewGuid():N}.db");
        string character = "SkinTransfer" + Guid.NewGuid().ToString("N");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var items = Program.Items;
        var conn = new ClientState(new(1, "skin-transfer", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = character, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100), Pools = new(100, 1000, 1000, int.MaxValue, 0,
                Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            CharacterSaves.SetInventories(character, [], null);
            var inventory = PlayerInventories.For(character, items);
            var wand = items.All.First(t => t.Class == "Wand" && t.Supported);
            var a = ItemCatalog.Create(wand) with { Mods = [Mod(items, "base_maximum_life", 60)] };
            var grant = items.Mods.All.First(m => m.GrantedEffects > 0 && m.Stats.Any(s => !s.Local));
            var trigger = items.Mods.All.First(m => m.Stats.Any(s => !s.Local && s.Id.Contains("trigger_a_socketed")));
            var gem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var b = ItemCatalog.Create(wand) with { Quality = 17, Mods = [Mod(items, "base_maximum_life", 80),
                grant.Roll(ModSection.Explicit, new Random(1)), trigger.Roll(ModSection.Explicit, new Random(2))],
                Sockets = [new(SocketColour.White, gem)], Links = [1] };
            string originalB = Saved(b, items), originalA = Saved(a, items);
            var target = inventory.Add(PlayerInventories.Weapon1, 0, 0, a);
            inventory.Add(PlayerInventories.Cursor, 0, 0, b);
            var lifeKey = Program.Stats.Key("base_maximum_life")!.Value;
            int initialLife = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(lifeKey);
            var binding = inventory.BindSkin(target.Id, Game.ShapeOf, out string why);
            Assert.NotNull(binding);
            Assert.Empty(inventory.Items(PlayerInventories.Cursor));
            Assert.Null(inventory.BindSkin(target.Id, Game.ShapeOf, out _));
            var contributions = SkinTransferStats.Sum([binding.Target.Item], items.Mods);
            Assert.Equal(140, contributions["base_maximum_life"]);
            Assert.Equal(initialLife + 80, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(lifeKey));
            foreach (var stat in trigger.Stats.Where(s => !s.Local && s.Id.Contains("trigger_a_socketed")))
                Assert.False(contributions.ContainsKey(stat.Id));
            Assert.Empty(inventory.SocketedGems());
            Assert.Empty(Game.GemSkillEffects(conn));
            Assert.Null(inventory.Offer(PlayerInventories.Weapon1, target.Id, 0, 0, Game.ShapeOf));
            var look = inventory.Equipped(i => new VisibleItem(0, i.VisualIdentity)).Single(i => i.Slot == 0);
            Assert.Equal(b.VisualIdentity, look.SkinVisual);
            inventory.SwapWeaponSets();
            Assert.Empty(inventory.EquipmentSnapshot());
            Assert.Equal(initialLife - 60, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(lifeKey));
            inventory.SwapWeaponSets();
            Assert.Same(binding.Target.Item, inventory.EquipmentSnapshot().Single().Item.Item);

            PlayerInventories.Forget(character);
            inventory = PlayerInventories.For(character, items);
            var loaded = inventory.Items(PlayerInventories.Weapon1).Single();
            Assert.Equal(originalA, Saved(loaded.Item with { SkinTransferSource = null }, items));
            Assert.Equal(originalB, Saved(loaded.Item.SkinTransferSource!, items));
            var occupied = inventory.Add(PlayerInventories.Cursor, 0, 0, a);
            Assert.Null(inventory.UnbindSkin(target.Id, out _));
            inventory.Remove(PlayerInventories.Cursor, occupied.Id);
            Saves.FailWrite = key => key == character;
            Assert.Throws<IOException>(() => inventory.UnbindSkin(target.Id, out _));
            Saves.FailWrite = null;
            inventory = PlayerInventories.For(character, items);
            Assert.NotNull(inventory.Items(PlayerInventories.Weapon1).Single().Item.SkinTransferSource);
            Assert.Empty(inventory.Items(PlayerInventories.Cursor));
            var removed = inventory.UnbindSkin(target.Id, out why);
            Assert.NotNull(removed);
            Assert.Equal(originalA, Saved(removed.Target.Item, items));
            Assert.Equal(originalB, Saved(removed.Source.Item, items));
            Assert.Single(inventory.Items(PlayerInventories.Cursor));
            Assert.Equal(60, ItemModStats.Sum([removed.Target.Item], items.Mods)["base_maximum_life"]);
            Assert.Equal(initialLife, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(lifeKey));
            Assert.Null(inventory.UnbindSkin(target.Id, out _));
            PlayerInventories.Forget(character);
            inventory = PlayerInventories.For(character, items);
            Assert.Null(inventory.Items(PlayerInventories.Weapon1).Single().Item.SkinTransferSource);
            Assert.Equal(originalB, Saved(inventory.Items(PlayerInventories.Cursor).Single().Item, items));
            var nativeB = inventory.Items(PlayerInventories.Cursor).Single();
            inventory.Remove(PlayerInventories.Cursor, nativeB.Id);
            inventory.Put(PlayerInventories.MainInventory, nativeB);
            Assert.Null(inventory.BindSkin(target.Id, PlayerInventories.MainInventory, PlayerInventories.MainInventory,
                nativeB.Id, Game.ShapeOf, out _)); // stale target inventory
            Assert.Null(inventory.BindSkin(target.Id, PlayerInventories.Weapon1, PlayerInventories.MainInventory,
                nativeB.Id + 1000, Game.ShapeOf, out _)); // stale donor
            var nativeBound = inventory.BindSkin(target.Id, PlayerInventories.Weapon1, PlayerInventories.MainInventory,
                nativeB.Id, Game.ShapeOf, out _);
            Assert.NotNull(nativeBound);
            Assert.Equal(PlayerInventories.MainInventory, nativeBound.SourceRow);
            Assert.Empty(inventory.Items(PlayerInventories.MainInventory));
            Assert.Equal(originalA, Saved(nativeBound.Target.Item with { SkinTransferSource = null }, items));
            Assert.Equal(originalB, Saved(nativeBound.Target.Item.SkinTransferSource!, items));
            Assert.Null(inventory.UnbindSkin(target.Id, out _, PlayerInventories.MainInventory));
            Assert.NotNull(inventory.Items(PlayerInventories.Weapon1).Single().Item.SkinTransferSource);
            Assert.Null(inventory.BindSkin(target.Id, PlayerInventories.Weapon1, PlayerInventories.MainInventory,
                nativeB.Id, Game.ShapeOf, out _)); // replay cannot consume another source
        }
        finally
        {
            Saves.FailWrite = null;
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class AttachmentModFixture : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "localexile-equipmentfusion-" + Guid.NewGuid().ToString("N"));
        internal ModHost Host { get; }
        internal AttachmentModFixture()
        {
            string mod = Path.Combine(_folder, "equipmentfusion");
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"), """{"id":"equipmentfusion","enabled":false}""");
            File.WriteAllText(Path.Combine(mod, "main.cs"), """
                public class TestMod : IMod
                {
                    public void Load(IModContext mod) => EquipmentAttachmentPolicy.Use(mod.Manifest.Id, new(true, true, true, true));
                }
                """);
            Host = new ModHost(_folder, _ => { });
            Host.Start(watch: false);
        }
        public void Dispose()
        {
            Host.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }

}
