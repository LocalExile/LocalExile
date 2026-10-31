using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    sealed record SelectedMiscCase(string Active, string Support, string SupportEffect, string ActorSource,
        string OwnSource, string Alias, int Actor, int Own, int Quality);

    static SelectedMiscCase SelectedMiscSpec(string field) => field switch
    {
        "area" => new("SkillGemFireball", "SupportGemIncreasedAreaOfEffect", "SupportIncreasedAreaOfEffect",
            "base_skill_area_of_effect_+%", "base_skill_area_of_effect_+%", "skill_area_of_effect_+%", 20, 30, 0),
        "rarity" => new("SkillGemFireball", "SupportGemItemRarity", "SupportItemRarity",
            "base_item_found_rarity_+%", "base_killed_monster_dropped_item_rarity_+%",
            "display_skill_increased_item_rarity_+%", 10, 40, 0),
        "minion" => new("SkillGemRaiseZombie", "SupportGemMinionDamage", "SupportMinionDamage",
            "minion_damage_+%", "minion_damage_+%", "virtual_minion_damage_+%", 15, 10, 20),
        "fire-penetration" => PenetrationSpec("Fire"),
        "cold-penetration" => PenetrationSpec("Cold"),
        "lightning-penetration" => PenetrationSpec("Lightning"),
        "elemental-penetration" => ElementalPenetrationSpec,
        "attack-fire-penetration" => AttackPenetrationSpec("Fire"),
        "attack-cold-penetration" => AttackPenetrationSpec("Cold"),
        "attack-lightning-penetration" => AttackPenetrationSpec("Lightning"),
        "life-leech-spell" => LeechSpec("life", "spell"),
        "life-leech-attack" => LeechSpec("life", "attack"),
        "mana-leech" => LeechSpec("mana", "attack"),
        _ when field.StartsWith("es-leech:", StringComparison.Ordinal) => LeechSpec("energy_shield", field.Split(':')[1]),
        _ when field.StartsWith("ailment-duration:", StringComparison.Ordinal) => UnboundAilmentSpec(field, duration: true),
        _ when field.StartsWith("ailment-effect:", StringComparison.Ordinal) => UnboundAilmentSpec(field, duration: false),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    static SelectedMiscCase PenetrationSpec(string element) => new("SkillGemFireball",
        $"SupportGem{element}Penetration", $"Support{element}Penetration",
        $"base_reduce_enemy_{element.ToLowerInvariant()}_resistance_%",
        $"base_reduce_enemy_{element.ToLowerInvariant()}_resistance_%",
        $"reduce_enemy_{element.ToLowerInvariant()}_resistance_%", 10, 20, 0);

    static SelectedMiscCase AttackPenetrationSpec(string element) => PenetrationSpec(element) with
    {
        Active = "SkillGemHeavyStrike",
        Alias = $"main_hand_attack_damage_penetrates_{element.ToLowerInvariant()}_resist_%",
    };

    static SelectedMiscCase ElementalPenetrationSpec => new("SkillGemFireball",
        "SupportGemElementalPenetration", "SupportElementalPenetration",
        "base_penetrate_elemental_resistances_%", "reduce_enemy_elemental_resistance_%",
        "reduce_enemy_fire_resistance_%", 10, 14, 0);

    static SelectedMiscCase UnboundAilmentSpec(string field, bool duration)
    {
        string ailment = field.Split(':')[1];
        string source = duration ? "base_all_ailment_duration_+%" : "non_damaging_ailment_effect_+%";
        return new("SkillGemFireball", "SupportGemUnboundAilments", "SupportUnboundAilments", source, source,
            $"virtual_{ailment}_{(duration ? "duration" : "effect")}_+%", 10, 35, 0);
    }

    static uint SelectedMiscKey(string name) => Program.Stats.Key(name)!.Value;

    static SelectedMiscCase LeechSpec(string pool, string type)
    {
        bool attack = type == "attack";
        string support = pool == "energy_shield" ? "EnergyLeech" : pool == "life" ? "LifeLeech" : "ManaLeech";
        string source = pool == "mana" ? "attack_skill_mana_leech_from_any_damage_permyriad" : $"{pool}_leech_from_any_damage_permyriad";
        string alias = pool == "energy_shield" || attack ? $"virtual_{pool}_leech_from_{type}_damage_permyriad"
            : $"{pool}_leech_from_{type}_damage_permyriad";
        return new(attack ? "SkillGemHeavyStrike" : "SkillGemFireball",
            pool == "energy_shield" ? "SupportGemEnergyShieldLeech" : "SupportGem" + support, "Support" + support,
            $"base_{pool}_leech_from_{type}_damage_permyriad", source, alias, 10, pool == "energy_shield" ? 150 : 200, 0);
    }

    [Theory]
    [InlineData("area")]
    [InlineData("life-leech-spell")]
    [InlineData("life-leech-attack")]
    [InlineData("mana-leech")]
    [InlineData("es-leech:spell")]
    [InlineData("es-leech:physical")]
    [InlineData("es-leech:fire")]
    [InlineData("es-leech:cold")]
    [InlineData("es-leech:lightning")]
    [InlineData("es-leech:chaos")]
    [InlineData("rarity")]
    [InlineData("minion")]
    [InlineData("fire-penetration")]
    [InlineData("cold-penetration")]
    [InlineData("lightning-penetration")]
    [InlineData("elemental-penetration")]
    [InlineData("attack-fire-penetration")]
    [InlineData("attack-cold-penetration")]
    [InlineData("attack-lightning-penetration")]
    [InlineData("ailment-duration:ignite")]
    [InlineData("ailment-duration:scorch")]
    [InlineData("ailment-duration:chill")]
    [InlineData("ailment-duration:freeze")]
    [InlineData("ailment-duration:frostburn")]
    [InlineData("ailment-duration:shock")]
    [InlineData("ailment-duration:sap")]
    [InlineData("ailment-effect:scorch")]
    [InlineData("ailment-effect:chill")]
    [InlineData("ailment-effect:frostburn")]
    [InlineData("ailment-effect:shock")]
    [InlineData("ailment-effect:sap")]
    public void Selected_misc_real_supports_compose_once_and_follow_link_and_source_removal(string field)
    {
        var spec = SelectedMiscSpec(field);
        using var game = new SelectedMiscCharacter(spec);
        var baseline = game.Read();
        Assert.Equal(spec.Actor, baseline.Get(spec.Alias));
        Assert.Empty(baseline.Applied);
        game.AssertControls(spec.Actor);

        game.Socket();
        var linked = game.Read();
        game.AssertSupported(linked);
        Assert.Equal(spec.Actor + spec.Own, linked.Get(spec.Alias));
        Assert.Equal(baseline.Skill.Id, linked.Skill.Id);
        Assert.Equal(linked.Stats.OrderBy(s => s.Key), game.Read().Stats.OrderBy(s => s.Key));
        game.AssertControls(spec.Actor);

        game.Socket(linked: false);
        var unlinked = game.Read();
        Assert.Empty(unlinked.Applied);
        Assert.Empty(unlinked.Skill.Supports);
        Assert.Equal(spec.Actor, unlinked.Get(spec.Alias));
        Assert.Equal(linked.Skill.Id, unlinked.Skill.Id);
        game.AssertControls(spec.Actor);

        game.Socket();
        game.AssertSupported(game.Read());
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));
        game.Socket(support: false);
        Assert.Empty(game.Read().Applied);
        Assert.Equal(spec.Actor, game.Read().Get(spec.Alias));
        game.RemoveActor();
        // The selected record carries an explicit zero after both sources disappear.
        Assert.Equal(0, game.Read().Get(spec.Alias));
        Assert.Equal(0, game.Read().Get(spec.Alias));
        game.AssertControls(0);

        game.Socket(support: false, active: false);
        Assert.DoesNotContain(game.Gems(), g => g.Effect == game.ActiveEffect);
        Assert.DoesNotContain(game.ActorSkills(), s => s.Id == baseline.Skill.Id);
        Assert.Single(game.Gems());
        game.AssertControls(0);
    }

    [Theory]
    [InlineData("area")]
    [InlineData("life-leech-spell")]
    [InlineData("life-leech-attack")]
    [InlineData("mana-leech")]
    [InlineData("es-leech:spell")]
    [InlineData("es-leech:physical")]
    [InlineData("es-leech:fire")]
    [InlineData("es-leech:cold")]
    [InlineData("es-leech:lightning")]
    [InlineData("es-leech:chaos")]
    [InlineData("rarity")]
    [InlineData("minion")]
    [InlineData("fire-penetration")]
    [InlineData("cold-penetration")]
    [InlineData("lightning-penetration")]
    [InlineData("elemental-penetration")]
    [InlineData("attack-fire-penetration")]
    [InlineData("attack-cold-penetration")]
    [InlineData("attack-lightning-penetration")]
    [InlineData("ailment-duration:ignite")]
    [InlineData("ailment-duration:scorch")]
    [InlineData("ailment-duration:chill")]
    [InlineData("ailment-duration:freeze")]
    [InlineData("ailment-duration:frostburn")]
    [InlineData("ailment-duration:shock")]
    [InlineData("ailment-duration:sap")]
    [InlineData("ailment-effect:scorch")]
    [InlineData("ailment-effect:chill")]
    [InlineData("ailment-effect:frostburn")]
    [InlineData("ailment-effect:shock")]
    [InlineData("ailment-effect:sap")]
    public void Selected_misc_signed_cancellation_and_final_developer_zero_replace_the_full_selected_sum(string field)
    {
        var spec = SelectedMiscSpec(field);
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        game.AssertSupported(game.Read());
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));

        game.SetActor(-spec.Own);
        var cancelled = game.Read();
        game.AssertSupported(cancelled);
        Assert.Equal(0, cancelled.Get(spec.Alias));
        Assert.Equal(0, game.Read().Get(spec.Alias));
        game.AssertControls(-spec.Own);

        game.SetActor(spec.Actor);
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));
        foreach (int replacement in new[] { 0, 7 })
        {
            game.State.PlayerStats[SelectedMiscKey(spec.Alias)] = replacement;
            game.Refresh();
            var replaced = game.Read();
            game.AssertSupported(replaced);
            Assert.Equal(replacement, replaced.Get(spec.Alias));
            Assert.Equal(replacement, game.Read().Get(spec.Alias));
            game.AssertControls(replacement);
        }
        Assert.True(game.State.PlayerStats.Remove(SelectedMiscKey(spec.Alias)));
        game.Refresh();
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));
        game.AssertControls(spec.Actor);
        Assert.Equal(spec.Actor, game.State.PlayerStats[SelectedMiscKey(spec.ActorSource)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selected_elemental_support_matches_hit_and_clears_all_elemental_rows_under_prohibition(bool attack)
    {
        var spec = attack ? ElementalPenetrationSpec with { Active = "SkillGemHeavyStrike" } : ElementalPenetrationSpec;
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        // These raw general stats must compose with the support rather than be overwritten by it.
        game.State.PlayerStats[SelectedMiscKey("reduce_enemy_elemental_resistance_%")] = 7;
        game.State.PlayerStats[SelectedMiscKey("reduce_enemy_chaos_resistance_%")] = 11;
        game.State.PlayerStats[SelectedMiscKey("base_attack_damage_penetrates_elemental_resist_%")] = 5;
        game.Refresh();
        foreach (int prohibition in new[] { 0, 1, 0 })
        {
            game.State.PlayerStats[SelectedMiscKey(Penetration.ElementalProhibition)] = prohibition;
            game.Refresh();
            var view = game.Read();
            game.AssertSupported(view);
            var hit = Penetration.Of(Program.CharacterPlayerStats(game.State), Program.Stats, attack, view.Own);
            int general = prohibition == 0 ? 31 : 0;
            int hand = prohibition == 0 ? 36 : 0;
            foreach (string element in new[] { "fire", "cold", "lightning" })
            {
                Assert.Equal(general, view.Get($"reduce_enemy_{element}_resistance_%"));
                Assert.Equal(hand, view.Get($"main_hand_attack_damage_penetrates_{element}_resist_%"));
                Assert.Equal(hand, view.Get($"off_hand_attack_damage_penetrates_{element}_resist_%"));
            }
            Assert.Equal(new(attack ? hand : general, attack ? hand : general, attack ? hand : general, 11), hit);
            Assert.Equal(11, view.Get("main_hand_attack_damage_penetrates_chaos_resist_%"));
            Assert.Equal(11, view.Get("off_hand_attack_damage_penetrates_chaos_resist_%"));
            game.AssertControls(prohibition == 0 ? 17 : 0);
        }
        game.Socket(linked: false);
        var unlinked = game.Read();
        Assert.Empty(unlinked.Applied);
        Assert.Equal(17, unlinked.Get(spec.Alias));
        Assert.Equal(17, Penetration.Of(Program.CharacterPlayerStats(game.State), Program.Stats, false, unlinked.Own).Fire);
    }

    [Theory]
    [InlineData("Fire", GameServer.Core.World.DamageType.Fire)]
    [InlineData("Cold", GameServer.Core.World.DamageType.Cold)]
    [InlineData("Lightning", GameServer.Core.World.DamageType.Lightning)]
    public void Selected_penetration_matches_hit_sources_and_support_requirement_changes(string element,
        GameServer.Core.World.DamageType damageType)
    {
        var spec = PenetrationSpec(element);
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        game.State.PlayerStats[SelectedMiscKey("base_penetrate_elemental_resistances_%")] = 5;
        game.Refresh();

        void Check(int expected, bool supported)
        {
            var view = game.Read();
            Assert.Equal(expected, view.Get(spec.Alias));
            var actor = Program.CharacterPlayerStats(game.State);
            Assert.Equal(expected, Penetration.Of(actor, Program.Stats, attack: false, view.Own).Of(damageType));
            if (supported) game.AssertSupported(view);
            else Assert.Empty(view.Applied);
            Assert.Equal(15, game.Read(other: true).Get(spec.Alias));
        }

        Check(35, supported: true);
        game.SetQualifier(false);
        game.AssertHostInactive();
        game.SetQualifier(true);
        Check(35, supported: true);
        game.Socket(linked: false);
        Check(15, supported: false);
    }

    [Fact]
    public void Selected_attack_penetration_preserves_each_active_weapons_local_modifier()
    {
        var spec = AttackPenetrationSpec("Fire");
        using var game = new SelectedMiscCharacter(spec);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        ItemMod Local(string id, int value)
        {
            var mod = catalog.Mods.ById(id)!;
            var source = Assert.Single(mod.Stats);
            Assert.Equal("local_elemental_penetration_%", source.Id);
            Assert.True(source.Local);
            Assert.InRange(value, source.Min, source.Max);
            return new(ModSection.Explicit, mod.Hash, [value]);
        }
        var mainMod = Local("JunMaster2ElementalPenetrationWithAttacks1", 6);
        var offMod = Local("JunMaster2ElementalPenetrationWithAttacks2", 9);
        var inactiveMod = Local("LocalElementalPenetrationUnique__1", 30);
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, main.Id, item => item with { Mods = [mainMod] }));
        var off = inventory.Add(3, 0, 0, LevelPanelCarrier(catalog, "Rusted Sword", offMod));
        inventory.Add(14, 0, 0, LevelPanelCarrier(catalog, "Rusted Sword", inactiveMod));
        game.Socket();

        void Check(int selectedMain, int selectedOff, int actorMain, int actorOff)
        {
            var view = game.Read();
            Assert.Equal(selectedMain, view.Get(spec.Alias));
            Assert.Equal(selectedOff, view.Get("off_hand_attack_damage_penetrates_fire_resist_%"));
            var actor = Program.CharacterPlayerStats(game.State);
            Assert.Equal(actorMain, actor[SelectedMiscKey(spec.Alias)]);
            Assert.Equal(actorOff, actor[SelectedMiscKey("off_hand_attack_damage_penetrates_fire_resist_%")]);
            Assert.Equal(actorMain, game.Read(other: true).Get(spec.Alias));
            Assert.Equal(view.Stats.OrderBy(s => s.Key), game.Read().Stats.OrderBy(s => s.Key));
        }

        Check(36, 39, 16, 19);
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, main.Id, item => item with { Mods = [] }));
        game.Refresh();
        Check(30, 39, 10, 19);
        Assert.NotNull(inventory.Remove(3, off.Id));
        game.Refresh();
        Check(30, 30, 10, 10);
        game.Socket(linked: false);
        Check(10, 10, 10, 10);
    }

    [Fact]
    public void Selected_misc_Cyclone_radius_matches_combat_without_counting_the_selected_area_alias_twice()
    {
        var spec = SelectedMiscSpec("area") with { Active = "SkillGemCyclone" };
        using var game = new SelectedMiscCharacter(spec);
        void Check(int area, int radius, int radiusPercent, bool supported)
        {
            var view = game.Read();
            Assert.Equal(area, view.Get(spec.Alias));
            Assert.Equal(16, view.Get("active_skill_base_area_of_effect_radius"));
            Assert.Equal(radius, view.Get("active_skill_area_of_effect_radius"));
            Assert.Equal(radiusPercent, view.Get("total_skill_area_of_effect_radius_+%_including_final"));
            Assert.Equal(radius, ProgramValue<int>("SkillRadius", game.State, view.Own, false, game.ActiveEffect));
            Assert.Equal(area, game.Read().Get(spec.Alias));
            if (supported) game.AssertSupported(view);
            else Assert.Empty(view.Applied);
            game.AssertControls(spec.Actor);
        }
        Check(20, 17, 9, supported: false);
        game.Socket();
        Check(50, 19, 22, supported: true);
        game.Socket(linked: false);
        Check(20, 17, 9, supported: false);
        game.Socket();
        Check(50, 19, 22, supported: true);
        game.Socket(support: false);
        Check(20, 17, 9, supported: false);
    }

    [Fact]
    public void Selected_misc_incompatible_real_Minion_Damage_does_not_add_quality_or_MORE_to_Fireball()
    {
        var spec = SelectedMiscSpec("minion") with { Active = "SkillGemFireball" };
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        game.AssertQualified();
        var view = game.Read();
        Assert.Empty(view.Applied);
        Assert.Empty(view.Skill.Supports);
        Assert.Equal(0, view.Own.GetValueOrDefault(SelectedMiscKey(spec.OwnSource)));
        Assert.Equal(0, view.Own.GetValueOrDefault(SelectedMiscKey("support_minion_damage_+%_final")));
        Assert.Equal(15, view.Get(spec.Alias));
        Assert.Equal(15, game.Read().Get(spec.Alias));
        game.AssertControls(15);
    }

    [Fact]
    public void Selected_misc_support_requirement_failure_takes_the_host_down_and_recovery_restores_the_supported_skill()
    {
        var spec = SelectedMiscSpec("area");
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        var linked = game.Read();
        game.AssertSupported(linked);
        Assert.Equal(50, linked.Get(spec.Alias));
        game.SetQualifier(false);
        // The active gem still qualifies on its own, but the support it shares the item with does not: the item goes
        // inactive with every gem in it (19-gems.md).
        Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", game.State, game.ActiveGem));
        game.AssertHostInactive();

        game.SetQualifier(true);
        game.AssertQualified();
        game.AssertSupported(game.Read());
        Assert.Equal(50, game.Read().Get(spec.Alias));
        game.AssertControls(20);
    }

    sealed record SelectedMiscView(GrantedSkill Skill, Dictionary<uint, int> Stats,
        IReadOnlyDictionary<uint, int> Own, IReadOnlyList<GemSupports.Applied> Applied)
    {
        public int Get(string name) => Stats[SelectedMiscKey(name)];
    }

    sealed class SelectedMiscCharacter : IDisposable
    {
        static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        readonly GatewayCharacter _game;
        readonly SelectedMiscCase _spec;
        readonly ItemCatalog _catalog;
        readonly PlayerInventories _items;
        readonly Item _qualifier;
        readonly Item _other;
        uint? _qualifierId;
        public ClientState State => _game.State;
        public string Character => _game.Character;
        public uint HostId { get; }
        public Item ActiveGem { get; }
        public Item SupportGem { get; }
        public int ActiveEffect { get; }
        readonly int _otherEffect;

        public SelectedMiscCharacter(SelectedMiscCase spec)
        {
            _game = LevelPanelCharacter(100);
            _spec = spec;
            _catalog = Program.Items;
            _items = ProgramValue<PlayerInventories>("ItemsOf", State);
            // Actual 80..100 native mod on an isolated carrier, without a rarity implicit.
            // The carrier does not claim this unique modifier can be acquired on this base.
            var attributes = LevelPanelMod(_catalog, "AllAttributesUniqueAmulet8", "additional_all_attributes", 100);
            Assert.Equal((ushort)11855, attributes.Hash);
            _qualifier = LevelPanelCarrier(_catalog, "Coral Amulet", attributes);
            SetQualifier(true);
            _items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(_catalog.Find("Rusted Sword")!));
            ActiveGem = Gem(spec.Active);
            SupportGem = Gem(spec.Support, spec.Quality);
            _other = Gem("SkillGemFreezingPulse");
            ActiveEffect = Assert.Single(_catalog.GemEffects(ActiveGem));
            _otherEffect = Assert.Single(_catalog.GemEffects(_other));
            Assert.NotEqual(ActiveEffect, _otherEffect);
            HostId = _items.Add(PlayerInventories.BodyArmour, 0, 0,
                ItemCatalog.Create(_catalog.Find("Plate Vest")!, defenceBaseRoll: 0)).Id;
            SetActor(spec.Actor);
            Socket(support: false);
            AssertQualified();
        }

        Item Gem(string id, int quality = 0)
        {
            var type = _catalog.Find("Metadata/Items/Gems/" + id)!;
            Assert.NotNull(type);
            var gem = _catalog.Gem(type)!;
            Assert.NotNull(gem);
            Assert.InRange(quality, 0, 20);
            return ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(1), GemFlags = 4, Quality = (byte)quality };
        }

        public void Socket(bool linked = true, bool support = true, bool active = true)
        {
            Assert.NotNull(_items.Change(PlayerInventories.BodyArmour, HostId, host => host with
            {
                Sockets = [new(SocketColour.White, active ? ActiveGem : null),
                    new(SocketColour.White, support ? SupportGem : null), new(SocketColour.White, _other)],
                Links = linked ? [2, 1] : [1, 1, 1],
            }));
            Refresh();
        }

        public void SetActor(int value)
        {
            State.PlayerStats[SelectedMiscKey(_spec.ActorSource)] = value;
            Refresh();
        }

        public void RemoveActor()
        {
            Assert.True(State.PlayerStats.Remove(SelectedMiscKey(_spec.ActorSource)));
            Refresh();
        }

        public void SetQualifier(bool equipped)
        {
            if (equipped) _qualifierId = _items.Add(5, 0, 0, _qualifier).Id;
            else
            {
                Assert.NotNull(_items.Remove(5, _qualifierId!.Value));
                _qualifierId = null;
            }
            Refresh();
        }

        public void Refresh()
        {
            State.Pools = Program.PoolsFor(State, 100);
            State.PlayerLife = State.Pools.Life;
            State.PlayerMana = State.Pools.Mana;
        }

        public void AssertQualified()
        {
            Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", State, ActiveGem));
            string? supportFailure = ProgramValue<string?>("GemRequirementsUnmet", State, SupportGem);
            Assert.Null(supportFailure);
            Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", State, _other));
            Assert.DoesNotContain(HostId, Program.InactiveItems[Character]);
        }

        // A socketed gem the character does not qualify for makes its whole host inactive (19-gems.md): the support's
        // failure takes the active gem and the other gem in the same item off the skill list with it.
        public void AssertHostInactive()
        {
            Assert.NotNull(ProgramValue<string?>("GemRequirementsUnmet", State, SupportGem));
            Assert.Contains(HostId, Program.InactiveItems[Character]);
            Assert.DoesNotContain(Gems(), g => g.Effect == ActiveEffect || g.Effect == _otherEffect);
        }

        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)[] Gems() =>
            ((IEnumerable<(ushort, int, string, int, int, uint)>)typeof(Program).GetMethod("GemSkillEffects", Flags,
                binder: null, types: [typeof(ClientState)], modifiers: null)!.Invoke(null, [State])!).ToArray();

        public IReadOnlyList<GrantedSkill> ActorSkills() => ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", State);

        public SelectedMiscView Read(bool other = false)
        {
            var gem = Assert.Single(Gems(), g => g.Effect == (other ? _otherEffect : ActiveEffect));
            var skill = Assert.Single(ActorSkills(), s => s.Id == gem.Id);
            var supported = ProgramValue<object>("SupportedSkill", State, gem.Id);
            var own = (IReadOnlyDictionary<uint, int>)supported.GetType().GetProperty("Stats")!.GetValue(supported)!;
            var applied = (IReadOnlyList<GemSupports.Applied>)supported.GetType().GetProperty("Supports")!.GetValue(supported)!;
            return new(skill, skill.Stats!.ToDictionary(s => s.Key, s => s.Value), own, applied);
        }

        public void AssertSupported(SelectedMiscView view)
        {
            AssertQualified();
            Assert.Single(view.Skill.Supports);
            var support = Assert.Single(view.Applied);
            Assert.Equal(_spec.SupportEffect, support.Effect.Id);
            Assert.Equal(1, support.Gem.Level);
            Assert.Equal(_spec.Quality, support.Gem.Quality);
            Assert.Equal(_spec.Own, support.Stats.Single(s => s.Key == SelectedMiscKey(_spec.OwnSource)).Value);
            Assert.Equal(_spec.Own, view.Own[SelectedMiscKey(_spec.OwnSource)]);
            if (_spec.SupportEffect == "SupportMinionDamage")
            {
                Assert.Equal(25, view.Own[SelectedMiscKey("support_minion_damage_+%_final")]);
                Assert.Equal(25, support.Stats.Single(s => s.Key == SelectedMiscKey("support_minion_damage_+%_final")).Value);
            }
            if (_spec.SupportEffect == "SupportUnboundAilments")
            {
                // More ailment damage is separate from both increased duration and non-damaging ailment effect.
                Assert.Equal(10, view.Own[SelectedMiscKey("support_unbound_ailments_ailment_damage_+%_final")]);
                Assert.Equal(35, view.Own[SelectedMiscKey("base_all_ailment_duration_+%")]);
                Assert.Equal(35, view.Own[SelectedMiscKey("non_damaging_ailment_effect_+%")]);
            }
        }

        public void AssertControls(int expected)
        {
            Assert.Equal(expected, Program.CharacterPlayerStats(State).GetValueOrDefault(SelectedMiscKey(_spec.Alias)));
            var defaults = Assert.Single(ActorSkills(), s => s.Id == SkillCatalog.FirstSkillId);
            Assert.Equal(expected, defaults.Stats!.ToDictionary(s => s.Key, s => s.Value).GetValueOrDefault(SelectedMiscKey(_spec.Alias)));
            var other = Read(other: true);
            Assert.Equal(expected, other.Get(_spec.Alias));
            Assert.Empty(other.Applied);
            Assert.Empty(other.Skill.Supports);
        }

        public void Dispose()
        {
            Program.InactiveItems.TryRemove(Character, out _);
            Program.GemSkillIdsOf.TryRemove(Character, out _);
            _game.Dispose();
        }
    }
}
