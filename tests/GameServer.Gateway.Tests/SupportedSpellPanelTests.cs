using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SupportedSpellPanelTests
{
    [Theory]
    [InlineData(false, 1599, 2131, 12, 19, 0, 0)]
    [InlineData(true, 75979, 101305, 11, 18, 595, 893)] // added cold at Fireball's 300% effectiveness (11-skills.md)
    public void Historical_support_range_and_average_controls_remain_exact_when_critical_strikes_are_disabled(bool combined,
        int perUse, int dps, int fireMin, int fireMax, int coldMin, int coldMax)
    {
        using var game = new SpellCharacter();
        game.State.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        if (combined)
            game.Socket(("SupportGemControlledDestruction", 20), ("SupportGemLesserMultipleProjectiles", 20),
                ("SupportGemAddedColdDamage", 20));
        else game.Socket(("SupportGemControlledDestruction", 20));

        var spell = game.Read();

        // Historical native values used precritical averages. Preserve those controls explicitly without crits;
        // current critical-weighted averages have separate coverage and still require native validation.
        // Preserve the existing double/Math.Round projection at CD's half-cent boundary without adding an epsilon.
        Assert.Equal(perUse, spell.Get("display_hundred_times_damage_per_skill_use"));
        Assert.Equal(dps, spell.Get("hundred_times_damage_per_second"));
        Assert.Equal(fireMin, spell.Get("spell_minimum_fire_damage"));
        Assert.Equal(fireMax, spell.Get("spell_maximum_fire_damage"));
        Assert.Equal(coldMin, spell.Get("spell_minimum_cold_damage"));
        Assert.Equal(coldMax, spell.Get("spell_maximum_cold_damage"));
        AssertSpellRows(spell);
    }

    [Theory]
    [InlineData("SupportGemControlledDestruction", 20, "more")]
    [InlineData("SupportGemLesserMultipleProjectiles", 20, "less")]
    [InlineData("SupportGemAddedColdDamage", 20, "cold")]
    [InlineData("SupportGemAdditionalLevel", 3, "level")]
    public void Real_linked_supports_publish_the_same_spell_hit_ranges_as_combat(string support, int level, string change)
    {
        using var game = new SpellCharacter();
        var baseline = game.Read();
        game.Socket((support, level));

        var supported = game.Read();

        Assert.Single(supported.Skill.Supports);
        AssertSpellRows(supported);
        if (change == "more") Assert.True(supported.Hit.MaxTotal > baseline.Hit.MaxTotal);
        if (change == "less") Assert.True(supported.Hit.MaxTotal < baseline.Hit.MaxTotal);
        if (change == "cold") Assert.True(supported.Hit[DamageType.Cold].Min > 0);
        if (change == "level")
        {
            Assert.True(supported.EffectiveLevel > baseline.EffectiveLevel);
            Assert.True(supported.Hit.MaxTotal > baseline.Hit.MaxTotal);
        }
        Assert.Equal(1, supported.Get("casting_spell"));
    }

    [Fact]
    public void Multiple_real_supports_compose_in_the_final_spell_rows_and_leave_absent_types_explicitly_zero()
    {
        using var game = new SpellCharacter();
        game.Socket(("SupportGemControlledDestruction", 20), ("SupportGemLesserMultipleProjectiles", 20),
            ("SupportGemAddedColdDamage", 20));

        var supported = game.Read();

        Assert.Equal(3, supported.Skill.Supports.Count);
        Assert.True(supported.Hit[DamageType.Fire].Min > 0);
        Assert.True(supported.Hit[DamageType.Cold].Min > 0);
        AssertSpellRows(supported);
        foreach (string type in new[] { "physical", "lightning", "chaos" })
        {
            Assert.Equal(0, supported.Get($"spell_minimum_{type}_damage"));
            Assert.Equal(0, supported.Get($"spell_maximum_{type}_damage"));
        }
    }

    [Fact]
    public void Brutality_clears_every_spell_hit_bound_and_average_when_Fireball_has_no_physical_damage()
    {
        using var game = new SpellCharacter();
        Assert.True(game.Read().Hit.MinTotal > 0);
        game.Socket(("SupportGemBrutality", 20));

        var supported = game.Read();

        Assert.Single(supported.Skill.Supports);
        Assert.True(supported.Hit.IsZero);
        AssertSpellRows(supported);
        Assert.Equal(0, supported.Get("display_hundred_times_damage_per_skill_use"));
        Assert.Equal(0, supported.Get("hundred_times_damage_per_second"));
    }

    [Fact]
    public void Unlinking_and_removing_the_real_support_restore_the_original_spell_without_stale_damage_types()
    {
        using var game = new SpellCharacter();
        var baseline = game.Read();
        game.Socket(("SupportGemAddedColdDamage", 20));
        var linked = game.Read();
        Assert.True(linked.Hit[DamageType.Cold].Min > 0);
        game.Unlink();

        var unlinked = game.Read();

        Assert.Empty(unlinked.Skill.Supports);
        Assert.Equal(baseline.Skill.Id, unlinked.Skill.Id);
        Assert.Equal(baseline.Hit.MinTotal, unlinked.Hit.MinTotal, 8);
        Assert.Equal(baseline.Hit.MaxTotal, unlinked.Hit.MaxTotal, 8);
        AssertSpellRows(unlinked);
        Assert.Equal(0, unlinked.Get("spell_maximum_cold_damage"));
        game.Socket(("SupportGemAddedColdDamage", 20));
        Assert.True(game.Read().Hit[DamageType.Cold].Min > 0);
        game.Socket();
        var removed = game.Read();
        Assert.Empty(removed.Skill.Supports);
        AssertSpellRows(removed);
        Assert.Equal(baseline.Get("display_hundred_times_damage_per_skill_use"), removed.Get("display_hundred_times_damage_per_skill_use"));
        Assert.Equal(0, removed.Get("spell_maximum_cold_damage"));
        game.RemoveActiveGem();
        Assert.Empty(game.Gems());
        Assert.DoesNotContain(game.ActorSkills(), s => s.Id == removed.Skill.Id);
    }

    [Fact]
    public void Hitless_Righteous_Fire_keeps_weapon_damage_and_added_support_damage_out_of_spell_hit_rows()
    {
        using var game = new SpellCharacter("SkillGemRighteousFire");
        game.Socket(("SupportGemAddedColdDamage", 20));
        Assert.True(Program.CharacterPlayerStats(game.State)[Program.Stats.Key("main_hand_minimum_total_damage")!.Value] > 0);

        var spell = game.Read();

        Assert.True(spell.Hit.IsZero);
        Assert.Equal(1, spell.Get("casting_spell"));
        AssertSpellRows(spell);
        Assert.Equal(0, spell.Get("hundred_times_damage_per_second"));
        Assert.Equal(0, spell.Get("display_hundred_times_damage_per_skill_use"));
    }

    static void AssertSpellRows(SpellView view)
    {
        foreach (var (type, range) in new[]
        {
            ("total", new DamageRange(view.Hit.MinTotal, view.Hit.MaxTotal)),
            ("physical", view.Hit[DamageType.Physical]), ("fire", view.Hit[DamageType.Fire]),
            ("cold", view.Hit[DamageType.Cold]), ("lightning", view.Hit[DamageType.Lightning]),
            ("chaos", view.Hit[DamageType.Chaos]),
        })
        {
            // These are the native whole-number spell keys, including explicit zero fields after support filtering.
            Assert.Equal((int)Math.Floor(range.Min), view.Get($"spell_minimum_{type}_damage"));
            Assert.Equal((int)Math.Floor(range.Max), view.Get($"spell_maximum_{type}_damage"));
        }
        Assert.Equal(0, view.Get("virtual_extra_critical_rolls"));
        bool cannot = view.Stats.GetValueOrDefault(Program.Stats.Key("global_cannot_crit")!.Value) != 0;
        double factor = cannot ? 1 : 1 + view.Get("spell_critical_strike_chance") / 10000.0
            * (view.Get("spell_critical_strike_multiplier") / 100.0 - 1);
        Assert.Equal((int)Math.Round(50 * (view.Hit.MinTotal + view.Hit.MaxTotal) * factor),
            view.Get("display_hundred_times_damage_per_skill_use"));
        if (!view.Hit.IsZero)
            Assert.Equal((int)Math.Round(50 * (view.Hit.MinTotal + view.Hit.MaxTotal) * factor / view.CastSeconds),
                view.Get("hundred_times_damage_per_second"));
    }

    sealed record SpellView(GrantedSkill Skill, Dictionary<uint, int> Stats, DamagePacket Hit, int EffectiveLevel, double CastSeconds)
    {
        public int Get(string name) => Stats[Program.Stats.Key(name)!.Value];
    }

    sealed class SpellCharacter : IDisposable
    {
        static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        readonly string _name = "SupportPanel" + Guid.NewGuid().ToString("N");
        readonly string _active;
        readonly PlayerInventories _items;
        readonly uint _host;
        public ClientState State { get; }

        public SpellCharacter(string active = "SkillGemFireball")
        {
            TestSaves.Ensure();
            _active = active;
            CharacterSaves.Write(_name, new([], Experience: CharacterStats.ExperienceFor(100)));
            State = new(new(0, "SupportedSpellPanel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = _name, ClassRow = SkillCatalog.WitchRow };
            _items = PlayerInventories.For(_name, Program.Items);
            var attributes = Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == "additional_all_attributes");
            _items.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
                { Mods = [new(ModSection.Explicit, attributes.Hash, [1000])] });
            _items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            _host = _items.Add(PlayerInventories.BodyArmour, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Plate Vest")!, defenceBaseRoll: 0)).Id;
            Socket();
        }

        Item Gem(string id, int level = 1)
        {
            var type = Program.Items.Find("Metadata/Items/Gems/" + id)!;
            Assert.NotNull(type);
            var gem = Program.Items.Gem(type)!;
            Assert.InRange(level, 1, gem.MaxLevel);
            return ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(level) };
        }

        public void Socket(params (string Id, int Level)[] supports)
        {
            var gems = new[] { Gem(_active) }.Concat(supports.Select(s => Gem(s.Id, s.Level))).ToArray();
            Assert.NotNull(_items.Change(PlayerInventories.BodyArmour, _host, host => host with
                { Sockets = [.. gems.Select(g => new ItemSocket(SocketColour.White, g))], Links = [(byte)gems.Length] }));
            Refresh();
            foreach (var gem in gems)
                Assert.Null(typeof(Program).GetMethod("GemRequirementsUnmet", Flags)!.Invoke(null, [State, gem]));
        }

        public void Unlink()
        {
            Assert.NotNull(_items.Change(PlayerInventories.BodyArmour, _host, host => host with { Links = null }));
            Refresh();
        }

        public void RemoveActiveGem() => Assert.NotNull(_items.Change(PlayerInventories.BodyArmour, _host,
            host => host with { Sockets = [], Links = [] }));

        void Refresh()
        {
            State.Pools = Program.PoolsFor(State, 100);
            State.PlayerLife = State.Pools.Life;
            State.PlayerMana = State.Pools.Mana;
        }

        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)[] Gems() =>
            ((IEnumerable<(ushort, int, string, int, int, uint)>)typeof(Program).GetMethod("GemSkillEffects", Flags,
                binder: null, types: [typeof(ClientState)], modifiers: null)!.Invoke(null, [State])!).ToArray();

        public IReadOnlyList<GrantedSkill> ActorSkills() =>
            (IReadOnlyList<GrantedSkill>)typeof(Program).GetMethod("PlayerSkills", Flags)!.Invoke(null, [State])!;

        public SpellView Read()
        {
            var gem = Assert.Single(Gems());
            var skill = Assert.Single(ActorSkills(), s => s.Id == gem.Id);
            var hit = (DamagePacket)typeof(Program).GetMethod("SupportedHit", Flags)!.Invoke(null, [State, gem.Id])!;
            var supported = typeof(Program).GetMethod("SupportedSkill", Flags)!.Invoke(null, [State, gem.Id])!;
            int level = (int)supported.GetType().GetProperty("Level")!.GetValue(supported)!;
            int? ms = (int?)typeof(Program).GetMethod("CastMs", Flags)!.Invoke(null, [State, gem.Effect, gem.Id]);
            return new(skill, skill.Stats!.ToDictionary(s => s.Key, s => s.Value), hit, level, Math.Max(.001, (ms ?? 0) / 1000.0));
        }

        public void Dispose()
        {
            PlayerInventories.Forget(_name);
            Program.InactiveItems.TryRemove(_name, out _);
            Program.GemSkillIdsOf.TryRemove(_name, out _);
        }
    }
}
