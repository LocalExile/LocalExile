using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class SpellEchoTests
{
    static int Speed(int increased, bool fishingRod, params (string Name, int Value)[] stats) =>
        Game.FinalCastSpeedPct(name => stats.FirstOrDefault(s => s.Name == name).Value, () => fishingRod, increased);

    // The client's cast_speed_+% (0x142709610) puts its combined "more" final (0x142708190) on the summed increases.
    [Fact]
    public void More_cast_speed_multiplies_as_the_clients_combined_final()
    {
        const string echo = "support_multicast_cast_speed_+%_final", fanaticism = "fanaticism_hand_cast_speed_+%_final",
            reels = "cast_speed_+%_final_while_holding_fishing_rod";
        Assert.Equal(40, Speed(0, false, (echo, 40)));
        Assert.Equal(82, Speed(30, false, (echo, 40)));
        Assert.Equal(12, Speed(0, false, (echo, 40), ("active_skill_cast_speed_+%_final", -20)));
        Assert.Equal(20, Speed(0, false, ("casting_spell", 1), (fanaticism, 20)));
        Assert.Equal(0, Speed(0, false, ("casting_spell", 1), ("is_not_hand_cast", 1), (fanaticism, 20)));
        Assert.Equal(0, Speed(0, false, (reels, 50)));
        Assert.Equal(50, Speed(0, true, (reels, 50)));
        Assert.Equal(-99, Speed(-50, false, ("active_skill_cast_speed_+%_final", -99)));
        Assert.Equal(-100, Speed(-100, false));
    }

    // Spell Echo (base_spell_repeat_count 1, 40% more cast speed at level 1) repeats Fireball once;
    // Ice Nova gets no repeat until its own repeated rings are modelled, but still casts faster.
    [Theory]
    [InlineData("Metadata/Items/Gems/SkillGemFireball", "Fireball", 1, 536)]
    [InlineData("Metadata/Items/Gems/SkillGemIceNova", "IceNova", 0, 500)]
    public void Spell_Echo_repeats_a_supported_spell_and_casts_it_faster(string gemType, string effectId, int repeats, int castMs)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-spell-echo-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "spell-echo", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "SpellEcho", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            var gem = ItemCatalog.Create(Program.Items.Find(gemType)!);
            var echo = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemMulticast")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new ItemSocket(SocketColour.White, gem), new ItemSocket(SocketColour.White, echo)], Links = [2],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var skill = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == effectId);
            Assert.Equal(repeats, Game.SpellRepeats(conn, skill.Id, skill.Effect));
            Assert.Equal(castMs, Game.CastMs(conn, skill.Effect, skill.Id)); // 750 and 700 ms at 40% more
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
