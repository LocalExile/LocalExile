using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SkillScalingTests
{
    static (Player Player, IDisposable? StatsHook) Equipped(int increasedArea, params string[] gems)
    {
        var player = SpellTotemExecutionTests.PlayerOf("Scaling" + Guid.NewGuid().ToString("N"));
        IDisposable? hook = increasedArea != 0
            ? GameEvents.Stats.Add("Game", sheet => { if (ReferenceEquals(sheet.Player, player)) sheet.Add(SkillArea.BaseIncreased, increasedArea); })
            : null;
        var sockets = gems.Select(name => new ItemSocket(SocketColour.White,
            ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type) with { GemVariant = Program.Items.FindGem(name)!.Value.Variant.Index })).ToArray();
        Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            { Sockets = sockets, Links = [.. sockets.Select(_ => (byte)1)] });
        return (player, hook);
    }

    static int Own(Game.SupportedGem gem, uint key) => gem.Stats.GetValueOrDefault(key);

    [Fact]
    public void Player_increased_area_counts_once_for_curses_and_arrow_rain_and_more_area_scales_through_the_root()
    {
        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            foreach (int increased in new[] { 0, 50, 100 })
            {
                var (conn, hook) = Equipped(increased, "Frostbite", "Rain of Arrows");
                try
                {
                    // PoB calcRadius: floor(base × floor(100 × √(1 + increased)) / 100), the player's base form once.
                    int Expected(int baseRadius) => (int)Math.Floor(baseRadius * Math.Floor(100 * Math.Sqrt(1 + increased / 100.0)) / 100);
                    var skills = Game.GemSkillEffects(conn).ToDictionary(g => Game.SkillsFor(conn).EffectId(g.Effect)!);
                    var curse = Game.SupportedSkill(conn, skills["Frostbite"].Id);
                    Assert.Equal(Expected(22 + Own(curse, Stat.ActiveSkillBaseRadiusPlus)),
                        Game.CurseRadius(conn, skills["Frostbite"].Id, false, curse, Game.CharacterPlayerStats(conn)));
                    var rain = Game.SupportedSkill(conn, skills["RainOfArrows"].Id);
                    var (radius, spread) = Game.RainArea(conn, "RainOfArrows", rain.Effect, rain.Stats, Game.CharacterPlayerStats(conn));
                    Assert.Equal(Expected(10 + Own(rain, Stat.ActiveSkillBaseRadiusPlus)), radius);
                    Assert.Equal(Expected(24 + Own(rain, Stat.ActiveSkillBaseRadiusPlus)), spread);
                }
                finally { hook?.Dispose(); PlayerInventories.Forget(conn.Character.Name!); }
            }
        }
        finally { Program.SetGameLogic(previous!); }
        // Fist of War's 59% more area grows the radius by its square root (×1.26), not by 1.59.
        Assert.Equal(Math.Sqrt(1.59), SkillArea.RadiusMultiplier(59), 6);
        Assert.Equal(1, SkillArea.RadiusMultiplier(0));
        Assert.Equal(38, (int)Math.Round(30 * SkillArea.RadiusMultiplier(59)));
    }

    [Fact]
    public void Skill_duration_takes_more_duration_the_duration_masteries_and_warcry_duration()
    {
        static Func<uint, int> Stats(params (uint Key, int Value)[] stats) => key => stats.Where(s => s.Key == key).Sum(s => s.Value);
        var none = Stats();
        var more = Stats((Stat.SupportMoreDurationSkillEffectDurationPlusPctFinal, 39));
        Assert.Equal(1.39, SkillDurationModifiers.Seconds(1000, none, more), 6);
        Assert.Equal(1.1, SkillDurationModifiers.Seconds(1000, Stats((Stat.PassiveMasteryMoreSkillEffectDurationPlusPctFinal, 10)), none), 6);
        Assert.Equal(0.9, SkillDurationModifiers.Seconds(1000, Stats((Stat.PassiveMasteryLessSkillEffectDurationPlusPctFinal, -10)), none), 6);
        // warcry_duration_+% is a warcry's only, added to its increased duration before the more multipliers.
        var player = Stats((Stat.WarcryDurationPlusPct, 50), (Stat.SkillEffectDurationPlusPct, 50), (Stat.PassiveMasteryMoreSkillEffectDurationPlusPctFinal, 10));
        Assert.Equal(1.5 * 1.39 * 1.1, SkillDurationModifiers.Seconds(1000, player, more), 6);
        Assert.Equal(2 * 1.39 * 1.1, SkillDurationModifiers.Seconds(1000, player, more, SkillDurationModifiers.Kinds.Warcry), 6);

        // A warcry gem's sent skill_effect_duration takes it; a non-warcry's does not.
        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var (conn, hook) = Equipped(0, "Seismic Cry", "Earthquake");
        using var warcryHook = GameEvents.Stats.Add("Game", sheet => { if (ReferenceEquals(sheet.Player, conn)) sheet.Add(Stat.WarcryDurationPlusPct, 50); });
        try
        {
            int Sent(string effect)
            {
                var gem = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == effect);
                var stats = Game.PlayerSkills(conn).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
                Assert.True(Own(Game.SupportedSkill(conn, gem.Id), Stat.BaseSkillEffectDuration) > 0);
                return (int)Math.Round(stats[Stat.SkillEffectDuration]
                    / (double)Own(Game.SupportedSkill(conn, gem.Id), Stat.BaseSkillEffectDuration) * 100);
            }
            Assert.Equal(150, Sent("SeismicCry"));
            Assert.Equal(100, Sent("Earthquake"));
        }
        finally { hook?.Dispose(); Program.SetGameLogic(previous!); PlayerInventories.Forget(conn.Character.Name!); }
    }
}
