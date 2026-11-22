using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class VaalAnimateWeaponTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time = 1;
        public double Now(ClientState conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            Time += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Ordinary_and_Vaal_weapons_keep_separate_limits()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Vaal Animate Weapon", 1));
        var sword = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!);
        uint Drop() => f.Conn.Instance.Ground.Add(new(100, 100, sword));
        uint ordinary = await f.Summon("AnimateWeapon", Drop());
        Assert.NotEqual(0u, ordinary);
        var skill = Game.GemSkillEffects(f.Conn).Single(g => Program.Skills.EffectId(g.Effect) == "VaalAnimateWeapon");
        for (int i = 0; i < 6; i++) Drop();
        await Game.ReleaseVaalMinions("test", f.Stream, f.Ciphers, f.Conn, skill.Id, (100, 100), () => true);
        Assert.Contains(ordinary, f.Conn.Summons.Fighters.Keys);
        Assert.Equal(6, f.Conn.Summons.Fighters.Count);
        var stats = Game.CharacterPlayerStats(f.Conn);
        Assert.Equal(1, stats[Program.Stats.Key("number_of_active_animated_weapons")!.Value]);
        Assert.Equal(5, stats[Program.Stats.Key("number_of_active_vaal_animated_weapons")!.Value]);
    }

    [Fact]
    public async Task Vaal_cast_consumes_only_eligible_sources_and_summons_temporary_unique_weapons()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock();
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            f.Equip(("Vaal Animate Weapon", 20));
            var skill = Game.GemSkillEffects(f.Conn).Single(g => Game.SkillsFor(f.Conn).EffectId(g.Effect) == "VaalAnimateWeapon");
            var gem = Game.SupportedSkill(f.Conn, skill.Id);
            var sword = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!);
            var bow = ItemCatalog.Create(Program.Items.Find("Crude Bow")!);
            var sockets = Enumerable.Repeat(new ItemSocket(SocketColour.White, null), 6).ToArray();
            uint Drop(Item item, int x = 100) => f.Conn.Instance.Ground.Add(new(x, 100, item));
            uint a = Drop(sword), b = Drop(sword, 102), ranged = Drop(bow), unknown = Drop(sword with { Identified = false });
            uint six = Drop(sword with { Sockets = sockets }), distant = Drop(sword, 160);
            uint hidden = f.Conn.Instance.Ground.Add(new(100, 100, sword), new object(), shared: false);
            var blades = new BladefallTrarthusState(0, 0, 0) { Ended = true };
            blades.Blades.Add((900, (103, 100), 0, 10));
            blades.Blades.Add((901, (104, 100), 4, 10));
            blades.Blades.Add((902, (105, 100), 0, .5));
            f.Conn.LingeringBladeSources.Add(blades);

            Game.RefreshVaalSouls(f.Conn);
            for (int i = 0; i < 50; i++) Assert.True(f.Conn.VaalSouls.Gain());
            bool ready = Game.TryBeginSkillCooldownUse(f.Conn, skill.Id, skill.Effect, null, false, () => f.Conn.ActionCount++, out _);
            Assert.True(ready);
            Assert.True(await Game.CastGemSkill("test", f.Stream, f.Ciphers, f.Conn, skill.Id, (100, 100), (100, 100)));
            Assert.Equal(0u, Assert.Single(f.Conn.VaalSouls.Read()).Current);
            foreach (var id in new[] { a, b }) Assert.Null(f.Conn.Instance.Ground.Get(id));
            foreach (var id in new[] { ranged, unknown, six, distant, hidden }) Assert.NotNull(f.Conn.Instance.Ground.Get(id));
            Assert.Equal(new uint[] { 901, 902 }, blades.Blades.Select(blade => blade.Id));
            Assert.Equal(3, f.Conn.Summons.Fighters.Count);
            foreach (var fighter in f.Conn.Summons.Fighters.Values)
            {
                var item = fighter.Equipment[2];
                var type = Program.Items.ByHash(item.TypeHash)!;
                Assert.True(fighter.Weapon is { Melee: true });
                Assert.Equal(ItemRarity.Unique, item.Rarity);
                Assert.InRange(ItemModStats.LevelRequirement(item, type.DropLevel, Program.Items.Mods), 40, 100);
            }

            clock.Time += 16;
            await Game.SummonsTick("test", f.Stream, f.Ciphers, f.Conn, clock.Time);
            Assert.Empty(f.Conn.Summons.Fighters);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }
}
