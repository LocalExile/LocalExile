using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The two kill-driven effects of the belt: Rampage's rarity per streak and Soul Eater's eaten souls.
public sealed class RampageAndSoulEaterTests
{
    [Fact]
    public void Rampage_rarity_counts_whole_groups_of_kills()
    {
        Assert.Equal(0, Game.RampageRarity(0, 15));
        Assert.Equal(0, Game.RampageRarity(14, 15));
        Assert.Equal(1, Game.RampageRarity(15, 15));
        Assert.Equal(6, Game.RampageRarity(100, 15));
        Assert.Equal(66, Game.RampageRarity(1000, 15));
        Assert.Equal(0, Game.RampageRarity(100, 0));

        // A tier crossing feeds both the tier executor and the "on Rampage threshold" item mods once.
        var session = new RampageSession();
        for (int i = 0; i < 15; i++)
            session.Kill(i * 0.1, new object(), enabled: true, alive: true, active: true, hostile: true);
        Assert.Equal(new[] { 15 }, session.TakeThresholds());
        Assert.Empty(session.TakeThresholds());
        Assert.Equal(new[] { 15 }, session.TakeTiers());
    }

    [Fact]
    public void Soul_eater_gains_caps_and_decays_after_four_seconds()
    {
        var souls = new SoulEaterState();
        Assert.Equal(0, souls.Stacks(0));
        Assert.Equal(1, souls.Soul(0));
        Assert.Equal(2, souls.Soul(0.1));
        Assert.Equal(2, souls.Stacks(4.0));   // still Recently
        Assert.Equal(1, souls.Stacks(4.6));   // one lost after 4.1 s idle
        Assert.Equal(1, souls.Stacks(5.0));

        var full = new SoulEaterState();
        for (int i = 0; i < 60; i++) full.Soul(10);
        Assert.Equal(SoulEaterState.MaximumStacks, full.Stacks(10));
        Assert.Equal(SoulEaterState.MaximumStacks - 1, full.Stacks(14.5));

        souls.Clear();
        Assert.Equal(0, souls.Stacks(20));
        Assert.False(souls.Enabled);
    }

    // Soul Ascension: a hit on a rare or unique enemy eats one soul, at most once per the item's 500 ms cooldown,
    // without the flask source's kill radius; the shared pool still caps and decays.
    [Fact]
    public void Soul_ascension_hit_souls_respect_the_cooldown_and_the_cap()
    {
        var souls = new SoulEaterState { HitFed = true, HitCooldownSeconds = 0.5 };
        Assert.Equal(0, souls.Stacks(0));
        Assert.True(souls.TryHitSoul(10));
        Assert.False(souls.TryHitSoul(10.4));
        Assert.True(souls.TryHitSoul(10.5));
        Assert.Equal(2, souls.Stacks(10.5));

        // At the cap the cooldown still passes (the item's "no more than once"), but no soul is added.
        var capped = new SoulEaterState { HitFed = true, HitCooldownSeconds = 0.5 };
        capped.SetMaximum(SoulEaterState.MaximumStacks - 10);
        for (int i = 0; i < 60; i++) capped.TryHitSoul(i * 0.5);
        Assert.Equal(SoulEaterState.MaximumStacks - 10, capped.Stacks(30));

        // Without the hit source a hit eats nothing (the flask source feeds on kills instead).
        Assert.False(new SoulEaterState { HitCooldownSeconds = 0.5 }.TryHitSoul(10));

        souls.Clear();
        Assert.False(souls.TryHitSoul(20));
    }

    // The unique's own stat enables the source: equipping the gloves turns on the hit-fed soul pool, 500 ms apart,
    // and its -10..10 roll moves the 45-soul base maximum.
    [Fact]
    public void Soul_ascension_enables_the_hit_source_with_its_maximum_roll()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-ascension-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "ascension", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "AscensionChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var gloves = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Soul Ascension", new Random(7)));
            var sum = ItemModStats.Sum([gloves], Program.Items.Mods);
            Assert.True(sum.ContainsKey("gain_soul_eater_stack_on_hit_vs_unique_cooldown_ms"), "item stats: " + string.Join(",", sum.Keys));
            int rolled = sum.GetValueOrDefault("soul_eater_maximum_stacks_+");
            Assert.InRange(rolled, -10, 10);
            Game.ItemsOf(conn).Add(PlayerInventories.Gloves, 0, 0, gloves);
            var built = Game.CharacterPlayerStats(conn);
            Assert.True(built.ContainsKey(Program.Stats.Key("gain_soul_eater_stack_on_hit_vs_unique_cooldown_ms")!.Value), "built stats: " + string.Join(",", sum.Keys));
            Assert.True(conn.SoulEater.HitFed);
            Assert.False(conn.SoulEater.FlaskFed);
            Assert.Equal(SoulEaterState.AscensionCooldownSeconds, conn.SoulEater.HitCooldownSeconds, 6);
            Assert.Equal(SoulEaterState.MaximumStacks + rolled, conn.SoulEater.Limit);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
