using GameServer.Core.Protocol.Instance;


sealed partial class VendorRecipes
{
    void CraftEquipment(List<Ingredient> pool, List<Item> rewards, List<string> recipes, Random rng,
        Func<string, int, bool> has, Func<string, int, bool> spend)
    {
        Ingredient? Find(Func<Ingredient, bool> predicate) => pool.FirstOrDefault(i => i.Left > 0 && predicate(i));
        bool Craftable(Ingredient i) => !i.Item.Corrupted && !i.Item.Mirrored && i.Item.FracturedCount == 0
            && i.Item.Rarity != ItemRarity.Unique && Empty(i.Item);
        void Consume(string recipe, params Ingredient[] entries)
        { foreach (var i in entries) i.Left--; recipes.Add(recipe); }
        Item NewBase(Ingredient i, ItemCatalog.BaseType type, ItemRarity rarity = ItemRarity.Normal) =>
            _rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = i.Item.ItemLevel }, rarity, rng);
        Item WithMod(Ingredient i, ModCatalog.Mod mod) => i.Item with
        {
            Rarity = ItemRarity.Magic, Identified = true, NameWords = [], FracturedCount = 0, Price = [],
            Mods = [.. ItemCatalog.Create(i.Type).Mods, mod.Roll(ModSection.Explicit, rng)],
        };

        // Iron ring + coloured gem; preserve the ring's ilvl, not the gem's fixed ilvl 1.
        foreach (var ring in pool.Where(i => i.Type.Name == "Iron Ring").ToList())
            if (ring.Left > 0 && Find(i => _catalog.Gem(i.Type) is { Colour: >= 1 and <= 3 }) is { } gem)
            {
                string name = _catalog.Gem(gem.Type)!.Colour switch { 1 => "Ruby Ring", 2 => "Topaz Ring", _ => "Sapphire Ring" };
                rewards.Add(NewBase(ring, Exact(name)!)); Consume("resistance ring", ring, gem);
            }
        foreach (var amulet in pool.Where(i => i.Type.Class == "Amulet").ToList())
        {
            var gems = new[] { 1, 2, 3 }.Select(c => Find(i => _catalog.Gem(i.Type)?.Colour == c)).ToArray();
            if (amulet.Left == 0 || gems.Any(i => i is null)) continue;
            rewards.Add(NewBase(amulet, Exact("Onyx Amulet")!)); Consume("Onyx Amulet", [amulet, .. gems.Select(i => i!)]);
        }
        foreach (var (a, b, output) in new[] { ("Amber Amulet", "Lapis Amulet", "Agate Amulet"),
                     ("Jade Amulet", "Lapis Amulet", "Turquoise Amulet"), ("Amber Amulet", "Jade Amulet", "Citrine Amulet") })
            while (Find(i => i.Type.Name == a) is { } first && Find(i => i.Type.Name == b) is { } second && has("Orb of Transmutation", 1))
            {
                spend("Orb of Transmutation", 1);
                var source = first.Item.ItemLevel <= second.Item.ItemLevel ? first : second;
                rewards.Add(NewBase(source, Exact(output)!, (ItemRarity)Math.Min((int)first.Item.Rarity, (int)second.Item.Rarity)));
                Consume("hybrid attribute amulet", first, second);
            }
        while (true)
        {
            var rings = pool.Where(i => i.Left > 0 && i.Type.Name == "Two-Stone Ring").DistinctBy(i => i.Type.Hash).Take(3).ToArray();
            if (rings.Length != 3) break;
            rewards.Add(NewBase(rings.OrderBy(i => i.Item.ItemLevel).First(), Exact("Prismatic Ring")!)); Consume("Prismatic Ring", rings);
        }

        foreach (var boots in pool.Where(i => i.Type.Class == "Boots" && Craftable(i)))
        {
            if (boots.Left == 0 || !has("Orb of Augmentation", 1) || Find(i => i.Type.Name == "Quicksilver Flask") is not { } flask) continue;
            int old = boots.Item.Mods.Where(m => m.Section == ModSection.Explicit)
                .Select(m => (_catalog.Mods.ByHash(m.Hash), m)).Where(p => p.Item1 is not null)
                .SelectMany(p => p.Item1!.Stats.Select((s, n) => (s.Id, Value: p.m.Values[n])))
                .Where(s => s.Id == "base_movement_velocity_+%").Select(s => s.Value).DefaultIfEmpty(0).Max();
            int target = boots.Item.Rarity == ItemRarity.Normal ? 10 : old > 0 && old < 30 ? old + 5 : 0;
            if (target == 0 || _catalog.Mods.All.FirstOrDefault(m =>
            {
                var range = m.RangeAt(0);
                return m.Id.StartsWith("MovementVelocity") && m.Stats.Count == 1
                    && range.Min == target && range.Max == target && m.Domain == 1;
            }) is not { } mod) continue;
            rewards.Add(WithMod(boots, mod)); spend("Orb of Augmentation", 1); Consume("movement speed boots", boots, flask);
        }

        foreach (var weapon in pool.Where(i => Weapon(i.Type) && Craftable(i)))
        {
            if (weapon.Left == 0) continue;
            bool caster = weapon.Type.Class is "Wand" or "Sceptre" or "Staff" or "Rune Dagger";
            if (weapon.Item.Rarity == ItemRarity.Normal && caster)
                foreach (var (tag, name) in new[] { ("physical", "Lithomancer's"), ("fire", "Flame Shaper's"), ("cold", "Frost Singer's"),
                             ("lightning", "Thunderhand's"), ("chaos", "Mad Lord's") })
                {
                    var gems = QualitySubset(pool.Where(i => i.Left > 0 && i.Item.Quality > 0 && i.Type.Components.Contains("SkillGem") && i.Type.Tags.Contains(tag)).ToList());
                    var mod = _catalog.Mods.All.FirstOrDefault(m => m.Name == name && m.Domain == 1 && m.WeightFor(weapon.Type.Tags) > 0);
                    if (gems is not { Count: >= 2 } || mod is null) continue;
                    var rolled = WithMod(weapon, mod);
                    rolled = rolled with { Mods = rolled.Mods.Select(m => m.Hash == mod.Hash ? m with { Values = m.Values.Select(_ => 1).ToArray() } : m).ToArray() };
                    rewards.Add(rolled); Consume("+1 spell gem weapon", [weapon, .. gems]); break;
                }
            if (weapon.Left == 0) continue;
            if (has("Blacksmith's Whetstone", 1))
            {
                var sash = Find(i => i.Type.Name == "Rustic Sash" && i.Item.Rarity >= ItemRarity.Magic);
                bool spell = false;
                if (sash is null && (caster || weapon.Type.Class == "Dagger"))
                { sash = Find(i => i.Type.Name == "Chain Belt" && i.Item.Rarity >= ItemRarity.Magic); spell = true; }
                if (sash is not null)
                {
                    string id = (spell ? TwoHanded(weapon.Type) ? "SpellDamageOnTwoHandWeapon" : "SpellDamageOnWeapon" : "LocalIncreasedPhysicalDamagePercent") + (int)sash.Item.Rarity;
                    if (_catalog.Mods.ById(id) is { } mod)
                    {
                        rewards.Add(WithMod(weapon, mod)); spend("Blacksmith's Whetstone", 1);
                        Consume(spell ? "spell damage weapon" : "physical damage weapon", weapon, sash); continue;
                    }
                }
            }
            if (!has("Orb of Augmentation", 1)) continue;
            foreach (var (flaskName, stat) in new[] { ("Granite Flask", "local_minimum_added_physical_damage"), ("Ruby Flask", "local_minimum_added_fire_damage"),
                         ("Sapphire Flask", "local_minimum_added_cold_damage"), ("Topaz Flask", "local_minimum_added_lightning_damage") })
            {
                var flask = Find(i => i.Type.Name == flaskName);
                if (flask is null) continue;
                var tiers = _catalog.Mods.All.Where(m => m.Domain == 1 && m.GenerationType == 1 && m.Stats.Count == 2
                    && m.Stats[0].Id == stat && m.WeightFor(weapon.Type.Tags) > 0 && !m.EssenceOnly).OrderBy(m => m.Level).ToList();
                if (tiers.Count < 2) continue;
                var current = weapon.Item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => _catalog.Mods.ByHash(m.Hash))
                    .FirstOrDefault(m => m is not null && tiers.Contains(m));
                if (current is null && weapon.Item.Rarity != ItemRarity.Normal) continue;
                int index = current is null ? -1 : tiers.IndexOf(current);
                if (index == tiers.Count - 2) continue; // next-to-top tier cannot be upgraded by the recipe
                var next = tiers[index == tiers.Count - 1 ? 0 : index + 1];
                if (next.Level > weapon.Item.ItemLevel) continue;
                rewards.Add(WithMod(weapon, next)); spend("Orb of Augmentation", 1); Consume("flat attack damage weapon", weapon, flask); break;
            }
        }

        foreach (var jewel in pool.Where(i => i.Left > 0 && i.Type.Class is "Ring" or "Amulet" && Craftable(i)))
        {
            if (jewel.Item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare) || !has("Orb of Augmentation", 1)
                || Find(i => i.Type.Name == "Amethyst Flask") is not { } flask) continue;
            var current = jewel.Item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => _catalog.Mods.ByHash(m.Hash))
                .FirstOrDefault(m => m?.Id.StartsWith("ChaosResist") == true && m.Stats.Count == 1);
            if (current is null) continue;
            var tiers = _rolls.Pool(jewel.Type, 100, jewel.Item.Influences).Select(p => p.Mod).Where(m => m.Stats.Count == 1 && m.Stats[0].Id == current.Stats[0].Id)
                .OrderBy(m => m.Level).ToList();
            var next = tiers.SkipWhile(m => m.Hash != current.Hash).Skip(1).FirstOrDefault();
            if (next is null) continue;
            rewards.Add(WithMod(jewel, next)); spend("Orb of Augmentation", 1); Consume("chaos resistance jewellery", jewel, flask);
        }
    }

    void FlaskRecipes(List<Ingredient> pool, List<Item> rewards, List<string> recipes, Random rng,
        Func<string, int, bool> has, Func<string, int, bool> spend)
    {
        foreach (var group in pool.Where(i => i.Left > 0 && i.Type.Class is "LifeFlask" or "ManaFlask" or "HybridFlask")
                     .GroupBy(i => i.Item.TypeHash))
        {
            var list = group.ToList();
            var source = list[0].Type;
            var next = _types.Where(t => t.Supported && t.Class == source.Class && t.DropLevel > source.DropLevel)
                .OrderBy(t => t.DropLevel).FirstOrDefault();
            if (next is null) continue;
            while (list.Count >= 3)
            {
                var used = list.Take(3).ToArray();
                rewards.Add(_rolls.Roll(ItemCatalog.Create(next) with { ItemLevel = used.Min(i => i.Item.ItemLevel),
                    Quality = used.Min(i => i.Item.Quality) }, (ItemRarity)used.Min(i => (int)i.Item.Rarity), rng) with { Identified = false });
                foreach (var i in used) i.Left--;
                recipes.Add("flask base upgrade"); list.RemoveRange(0, 3);
            }
        }
        while (has("Orb of Fusing", 1))
        {
            var life = pool.FirstOrDefault(i => i.Left > 0 && i.Type.Class == "LifeFlask");
            var mana = pool.FirstOrDefault(i => i.Left > 0 && i.Type.Class == "ManaFlask");
            if (life is null || mana is null) break;
            int level = Math.Min(life.Item.ItemLevel, mana.Item.ItemLevel);
            var hybrid = _types.Where(t => t.Supported && t.Class == "HybridFlask" && t.DropLevel <= Math.Max(10, Math.Min(life.Type.DropLevel, mana.Type.DropLevel)))
                .OrderByDescending(t => t.DropLevel).FirstOrDefault();
            if (hybrid is null) break;
            life.Left--; mana.Left--; spend("Orb of Fusing", 1);
            rewards.Add(ItemCatalog.Create(hybrid) with { ItemLevel = (byte)level }); recipes.Add("hybrid flask");
        }
    }
}
