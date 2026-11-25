using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EquipmentAndUniqueFlaskTests
{
    static Item ModItem(string baseName, ItemRarity rarity, params (string ModId, int[] Values)[] mods)
    {
        var item = ItemCatalog.Create(Program.Items.Find(baseName)!);
        var built = new List<ItemMod>(item.Mods);
        foreach (var (modId, values) in mods)
        {
            var def = Assert.IsType<ModCatalog.Mod>(Program.Items.Mods.ById(modId));
            built.Add(new(ModSection.Explicit, def.Hash, values));
        }
        return item with { Rarity = rarity, Mods = built };
    }

    [Fact]
    public async Task Unique_and_map_flasks_apply_wise_oak_without_wire_leak_and_scale_charges()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock { At = 10 };
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        var overrides = new Dictionary<uint, int>
        {
            [Stat.BaseFireDamageResistancePct] = 40,
            [Stat.BaseColdDamageResistancePct] = 20,
            [Stat.BaseLightningDamageResistancePct] = 0,
        };
        using var statSub = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
        {
            if (!ReferenceEquals(p, player)) return s;
            var merged = new Dictionary<uint, int>(s);
            foreach (var (k, v) in overrides) merged[k] = merged.GetValueOrDefault(k) + v;
            return merged;
        });
        try
        {
            var items = Game.ItemsOf(player);
            foreach (var existing in items.Items(PlayerInventories.Flask).ToArray())
                items.Remove(PlayerInventories.Flask, existing.Id);

            var wiseOak = ModItem("Bismuth Flask", ItemRarity.Unique,
                ("FlaskElementalPenetrationOfHighestResistUnique__1", [8]),
                ("FlaskElementalDamageTakenOfLowestResistUnique__1", [-6])) with { Charges = 40 };
            items.Add(PlayerInventories.Flask, 0, 0, wiseOak);
            Game.RecomputePoolsAndSpeed(player);

            await Game.UseFlask(player, 0);

            var stats = Game.CharacterPlayerStats(player);
            Assert.Equal(8, stats.GetValueOrDefault(Stat.BaseReduceEnemyFireResistancePct));
            Assert.Equal(0, stats.GetValueOrDefault(Stat.BaseReduceEnemyColdResistancePct));
            Assert.Equal(0, stats.GetValueOrDefault(Stat.BaseReduceEnemyLightningResistancePct));
            Assert.Equal(-6, stats.GetValueOrDefault(Stat.LightningDamageTakenPlusPct));
            Assert.Equal(0, stats.GetValueOrDefault(Stat.FireDamageTakenPlusPct));
            Assert.Equal(0, stats.GetValueOrDefault(Stat.ColdDamageTakenPlusPct));

            // Wise Oak's local flask stats must never enter the player's StatMap or leak onto 0x01b3.
            Assert.False(stats.ContainsKey(Stat.LocalUniqueFlaskElementalPenetrationPctOfHighestUncappedResistanceType));
            Assert.False(stats.ContainsKey(Stat.LocalUniqueFlaskElementalDamageTakenPlusPctOfLowestUncappedResistanceType));
            var clientStats = Game.ClientStats(player, [.. Game.FinalPlayerStats(player)]);
            Assert.DoesNotContain(clientStats, s => s.Item1 == Stat.LocalUniqueFlaskElementalPenetrationPctOfHighestUncappedResistanceType);
            Assert.DoesNotContain(clientStats, s => s.Item1 == Stat.LocalUniqueFlaskElementalDamageTakenPlusPctOfLowestUncappedResistanceType);

            // Outside a map (MapFlaskChargeGain == 0), finite GainFlaskCharges still applies player charge gain multipliers.
            overrides[Stat.ChargesGainedPlusPct] = 100;
            Game.RecomputePoolsAndSpeed(player);
            var flaskRow = items.Items(PlayerInventories.Flask).Single();
            items.Change(PlayerInventories.Flask, flaskRow.Id, f => f with { Charges = 0 });
            await Game.GainFlaskCharges(player, 10, "player multiplier test");
            Assert.Equal(20, items.Items(PlayerInventories.Flask).Single().Item.Charges);
            overrides.Remove(Stat.ChargesGainedPlusPct);
            Game.RecomputePoolsAndSpeed(player);

            // Map less flask effect & reduced flask charges gained scale active flask stats and finite charge gains.
            var mapMods = new MapMods(new Dictionary<uint, int>
            {
                [Stat.MapPlayerFlaskEffectPlusPctFinal] = -50,
                [Stat.MapPlayerChargesGainedPlusPct] = -50,
            });
            player.Instance.OpenedWith(new("test", new(player.Layout.Hash, "MapTest", "MapTest", 1), mapMods));
            flaskRow = items.Items(PlayerInventories.Flask).Single();
            items.Change(PlayerInventories.Flask, flaskRow.Id, f => f with { Charges = 40 });
            await Game.UseFlask(player, 0);

            var mapStats = Game.CharacterPlayerStats(player);
            Assert.Equal(4, mapStats.GetValueOrDefault(Stat.BaseReduceEnemyFireResistancePct));
            Assert.Equal(-3, mapStats.GetValueOrDefault(Stat.LightningDamageTakenPlusPct));

            flaskRow = items.Items(PlayerInventories.Flask).Single();
            items.Change(PlayerInventories.Flask, flaskRow.Id, f => f with { Charges = 0 });
            await Game.GainFlaskCharges(player, 10, "map test");
            Assert.Equal(5, items.Items(PlayerInventories.Flask).Single().Item.Charges);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        var withoutMod = await SulphurGroundLinger(withMod: false, zealotry: true);
        var withMod = await SulphurGroundLinger(withMod: true, zealotry: true);
        var withoutAura = await SulphurGroundLinger(withMod: true, zealotry: false);
        var toggledOff = await SulphurGroundLinger(withMod: true, zealotry: true, toggleBeforePayment: true);
        var toggledOn = await SulphurGroundLinger(withMod: true, zealotry: false, toggleBeforePayment: true);
        var expired = await SulphurGroundLinger(withMod: true, zealotry: true, afterExpiry: true);
        Assert.Equal((false, false), withoutMod);
        Assert.Equal((true, false), withMod);
        Assert.Equal((false, false), withoutAura);
        Assert.Equal((false, false), toggledOff);
        Assert.Equal((true, false), toggledOn);
        Assert.Equal((false, false), expired);
    }

    static async Task<(bool FirstCheck, bool SecondCheck)> SulphurGroundLinger(bool withMod, bool zealotry,
        bool toggleBeforePayment = false, bool afterExpiry = false)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock { At = 10 };
        var updates = new PlayerUpdatesState(fixture.Lifetime, clock);
        player.PlayerUpdatesState = updates;
        Assert.True(player.PlayerUpdatesWorker.TryStart(player, updates.Rules, null, updates, out updates.Generation));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        try
        {
            player.Walk.Teleport(100, 100, clock.At);
            var items = Game.ItemsOf(player);
            const byte jewelSlot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(jewelSlot)!.Value);
            const string modId = "ZealotryConsecratedGroundEffectLingersForMsAfterLeavingTheArea";
            var jewel = ModItem("Prismatic Jewel", ItemRarity.Unique,
                withMod ? [(modId, new[] { 2000 })] : []);
            Assert.Null(Game.PassiveJewelRefusal(player, jewelSlot, 0, jewel));
            items.Add(PlayerInventories.PassiveJewels, jewelSlot, 0, jewel);
            ushort skill = fixture.Equip(("Zealotry", 1)).Single();
            var aura = Game.GemSkillEffects(player).Single();
            async Task ToggleZealotry()
            {
                Assert.True(await Game.ToggleAura(player, skill, aura.Effect, aura.Name,
                    Game.SupportedReservationCost(player, skill)!.Value, aura.Level, aura.Quality));
            }
            if (zealotry) await ToggleZealotry();
            Assert.Equal(zealotry, Game.AffectingAuras(player).Any(a => a.EffectId == "Zealotry"));
            Assert.Equal(withMod && zealotry ? 2000 : 0, Game.CharacterPlayerStats(player)
                .GetValueOrDefault(Stat.ConsecratedGroundEffectLingersForMsAfterLeavingTheAreaWhileAffectedByZealotry));

            foreach (var flask in items.Items(PlayerInventories.Flask).ToArray())
                items.Remove(PlayerInventories.Flask, flask.Id);
            var sulphur = ItemCatalog.Create(Program.Items.Find("Sulphur Flask")!) with { Charges = 60 };
            items.Add(PlayerInventories.Flask, 0, 0, sulphur);
            if (toggleBeforePayment)
            {
                await player.FlaskBarGate.WaitAsync();
                Task use;
                try
                {
                    use = Game.UseFlask(player, 0);
                    Assert.False(use.IsCompleted);
                    await ToggleZealotry();
                    Assert.Equal(!zealotry, Game.AffectingAuras(player).Any(a => a.EffectId == "Zealotry"));
                }
                finally { player.FlaskBarGate.Release(); }
                await use;
            }
            else await Game.UseFlask(player, 0);
            Assert.True(items.Items(PlayerInventories.Flask).Single().Item.Charges < sulphur.Charges);
            var ground = Assert.Single(player.FlaskGrounds.Grounds);
            Assert.Equal(FlaskGrounds.Kind.ConsecratedGround, ground.Kind);
            Game.AdvanceFlaskGrounds(player, updates);
            Assert.True(player.FlaskGrounds.Consecrated);
            Assert.Contains((Stat.LifeRegenerationRatePerMinutePct, 300), player.FlaskGrounds.PlayerStats());

            if (afterExpiry) clock.At = ground.Ends + 2;
            else
            {
                player.Walk.Teleport(160, 100, clock.At);
                Assert.False(ground.Covers(player.Walk.Position(clock.At)));
                Game.AdvanceFlaskGrounds(player, updates);
                clock.At += .5;
            }
            Game.AdvanceFlaskGrounds(player, updates);
            bool firstCheck = player.FlaskGrounds.Consecrated;
            Assert.Equal(firstCheck, player.FlaskGrounds.PlayerStats().Any(s => s.Stat == Stat.LifeRegenerationRatePerMinutePct));
            clock.At += 1.5;
            Game.AdvanceFlaskGrounds(player, updates);
            Console.WriteLine($"Sulphur ground, mod {withMod}, initial Zealotry {zealotry}, toggle {toggleBeforePayment}, " +
                $"delayed expiry {afterExpiry}: stored {ground.AdditionalLingerMs}ms, checks {firstCheck}, {player.FlaskGrounds.Consecrated}");
            return (firstCheck, player.FlaskGrounds.Consecrated);
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
            player.PlayerUpdatesWorker.Complete(updates.Rules, null, updates, updates.Generation);
            player.RetirePlayerUpdates();
        }
    }

    [Fact]
    public async Task Equipment_skills_and_doomfletch_apply_added_physical_flame_touched_dousing_and_spirit_charges()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock { At = 10 };
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        var overrides = new Dictionary<uint, int>
        {
            [Stat.GainNoInherentBonusFromStrength] = 1,
            [Stat.AttackMinimumAddedPhysicalDamage] = 20,
            [Stat.AttackMaximumAddedPhysicalDamage] = 20,
        };
        using var statSub = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
        {
            if (!ReferenceEquals(p, player)) return s;
            var merged = new Dictionary<uint, int>(s);
            foreach (var (k, v) in overrides) merged[k] = merged.GetValueOrDefault(k) + v;
            return merged;
        });
        try
        {
            var items = Game.ItemsOf(player);
            foreach (var existing in items.EquipmentSnapshot().ToArray())
                items.Remove(existing.Row, existing.Item.Id);
            foreach (var existing in items.Items(PlayerInventories.Flask).ToArray())
                items.Remove(PlayerInventories.Flask, existing.Id);

            // 1. Doomfletch scales BOTH weapon base physical and flat added physical damage across hit, panel, skill, and ignite paths.
            var fireballGem = Program.Items.FindGem("Fireball")!.Value;
            var fireballItem = ItemCatalog.Create(fireballGem.Type) with
            {
                GemVariant = fireballGem.Variant.Index,
                GemExperience = Program.Items.Gem(fireballGem.Type)!.ExperienceFor(1),
            };
            var doomfletch = ModItem("Short Bow", ItemRarity.Unique,
                ("LocalPhysicalDamageAddedAsEachElementUnique__1", [100])) with
            {
                Sockets = [new(SocketColour.White, fireballItem)],
                Links = [1],
            };
            items.Add(PlayerInventories.Weapon1, 0, 0, doomfletch);
            Game.RecomputePoolsAndSpeed(player);

            var weapon = Game.WeaponFor(player);
            var stats = Game.CharacterPlayerStats(player);
            var basePhys = weapon.LocalDamage![DamageType.Physical];
            var expectedRange = new DamageRange(basePhys.Min + 20, basePhys.Max + 20);
            var accuracy = new AttackAccuracyProfile(Game.PlayerAccuracy(player, stats));
            int Get(uint key) => stats.GetValueOrDefault(key);

            var hit = SupportedAttackDamage.Hit(weapon, Get, new HashSet<int> { 0, 2 }, accuracy);
            Assert.Equal(expectedRange, hit[DamageType.Physical]);
            Assert.Equal(expectedRange, hit[DamageType.Fire]);
            Assert.Equal(expectedRange, hit[DamageType.Cold]);
            Assert.Equal(expectedRange, hit[DamageType.Lightning]);

            var skillHit = AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, accuracy, 10_000, projectile: true);
            Assert.Equal(expectedRange, skillHit[DamageType.Fire]);
            Assert.Equal(expectedRange, skillHit[DamageType.Cold]);
            Assert.Equal(expectedRange, skillHit[DamageType.Lightning]);

            var weaponStats = Game.WeaponStats(player, weapon, stats).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal((int)Math.Floor(expectedRange.Min), weaponStats[Stat.MainHandMinimumFireDamage]);
            Assert.Equal((int)Math.Floor(expectedRange.Max), weaponStats[Stat.MainHandMaximumFireDamage]);
            Assert.Equal((int)Math.Floor(expectedRange.Min), stats[Stat.MainHandMinimumFireDamage]);
            Assert.Equal((int)Math.Floor(expectedRange.Max), stats[Stat.MainHandMaximumFireDamage]);
            Assert.Equal((int)Math.Floor(expectedRange.Min), stats[Stat.MainHandMinimumColdDamage]);
            Assert.Equal((int)Math.Floor(expectedRange.Min), stats[Stat.MainHandMinimumLightningDamage]);

            var ignite = Game.DefaultAttackIgnite(player, stats, weapon);
            Assert.Equal(expectedRange.Min * 0.9, ignite.Min, 3);
            Assert.Equal(expectedRange.Max * 0.9, ignite.Max, 3);

            // 2. Approaching Flames toggles Flame-Touched + Adrenaline + _while_burning, and a Dousing recovery flask dispels it.
            var boots = ModItem("Iron Greaves", ItemRarity.Unique,
                ("GrantsTouchOfFireUnique__1", []),
                ("FireDamageTakenFireTouchedUnique__1", [6000]),
                ("GainAdrenalineFireTouchedGainUnique__1", [1]),
                ("LoseAdrenalineFireTouchedLossUnique__1", [1]));
            items.Add(PlayerInventories.Boots, 0, 0, boots);
            player.PlayerSkillsCache = null;
            Game.RecomputePoolsAndSpeed(player);

            var flameSkill = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "ApproachingFlames");
            Assert.True(Game.TryBeginSkillCooldownUse(player, flameSkill.Id, flameSkill.Effect,
                Game.CooldownForSkill(player, flameSkill.Id), false, () => { }, out var cooldownTicket));
            Assert.True(await Game.CastGemSkill(player, flameSkill.Id, (100, 100), (100, 100), cooldownUse: cooldownTicket));

            var burningStats = Game.CharacterPlayerStats(player);
            Assert.Equal(1, burningStats.GetValueOrDefault(Stat.AffectedByTouchOfFire));
            Assert.True(player.Antiquarian.SustainedAdrenaline);
            Assert.True(burningStats.GetValueOrDefault(Stat.DamagePlusPct) >= 100);
            Assert.True(Game.ItemConditionFlags(player, clock.At)[Game.BurningItemCondition]);

            var dousingFlask = ModItem("Small Life Flask", ItemRarity.Magic,
                ("FlaskDispellsBurning1", [1])) with { Charges = 21 };
            items.Add(PlayerInventories.Flask, 0, 0, dousingFlask);
            player.PlayerLife = player.PlayerMaxLife;
            await Game.UseFlask(player, 0);

            var afterDouse = Game.CharacterPlayerStats(player);
            Assert.Equal(0, afterDouse.GetValueOrDefault(Stat.AffectedByTouchOfFire));
            Assert.False(player.Antiquarian.SustainedAdrenaline);
            Assert.False(Game.ItemConditionFlags(player, clock.At)[Game.BurningItemCondition]);

            // 3. Lightpoacher grants a Spirit Charge on kill and triggers Spirit Burst on cast, consuming the charge.
            var lightpoacher = ModItem("Great Crown", ItemRarity.Unique,
                ("LocalDisplayGrantLevelXSpiritBurstUnique__1", [20]),
                ("GainSpiritChargeOnKillChanceUnique__1", [100]),
                ("MaximumSpiritChargesPerAbyssJewelEquippedUnique__1", [1]),
                ("PhysAddedAsEachElementPerSpiritChargeUnique__1", [5]));
            items.Add(PlayerInventories.Helmet, 0, 0, lightpoacher);
            var stygian = ItemCatalog.Create(Program.Items.Find("Stygian Vise")!);
            var eye = ItemCatalog.Create(Program.Items.Find("Hypnotic Eye Jewel")!);
            items.Add(PlayerInventories.Belt, 0, 0, stygian with { Sockets = [stygian.Sockets[0] with { Gem = eye }] });
            player.PlayerSkillsCache = null;
            Game.RecomputePoolsAndSpeed(player);

            var spiritBurstSkill = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "SpiritBurst");
            var monster = new SpawnedMonster("zombie", new(115, 100, 1), 100, 8, 100) { Hostile = true, Level = 70 };
            Game.ChargesOnKill(player, monster);

            var chargedStats = Game.CharacterPlayerStats(player);
            Assert.Equal(1, chargedStats.GetValueOrDefault(Stat.CurrentSpiritCharges));
            Assert.True(chargedStats.GetValueOrDefault(Stat.PhysicalDamagePctToAddAsFire) >= 5);

            ushort fireballId = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Fireball").Id;
            Assert.True(await Game.CastGemSkill(player, fireballId, (100, 100), (160, 100)));
            clock.At += .05;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));

            Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CurrentSpiritCharges));
            Assert.Contains(fixture.Session.Runtime.Projectiles,
                p => p.Use.Payload is ProjectileOffense offense
                    && (offense.Label.Contains("Spirit", StringComparison.OrdinalIgnoreCase)
                        || p.Profile.Id.Contains("Spirit", StringComparison.OrdinalIgnoreCase)));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class CastClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
