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
            conn.LockstepClient = true;
            Assert.Equal(effectId == "Fireball" ? 1.5 / 1.4 : .5,
                Game.LockstepActionHold(conn, skill.Id, 0x8001), 6);
            // A target changes the aim, not the native cast duration or the held spell's repeat gate.
            foreach (uint? target in new uint?[] { null, 0x3001 })
            {
                conn.LockstepSkill.Press((110, 100), target, skill.Id, 0x400, repeats: true);
                conn.LockstepBusyUntil = 0;
                Game.HoldLockstepSkill(conn, 0x8001, 10, 10.033);
                Assert.Equal(10 + Game.LockstepActionHold(conn, skill.Id, 0x8001), conn.LockstepBusyUntil, 6);
                if (effectId == "Fireball")
                {
                    Assert.NotNull(conn.LockstepCastWindow);
                    conn.LockstepHold.Press((120, 100), 0x2909, 0x401);
                    Assert.True(Game.LockstepPlayerFree(conn, 10.01)); // Before the first cast's contact.
                    Assert.False(Game.LockstepPlayerFree(conn, 10 + .75 / 1.4 + .01)); // Early in the repeat.
                    Assert.True(Game.LockstepPlayerFree(conn, conn.LockstepBusyUntil - .001)); // Late in the final cast.
                    conn.LockstepHold.Clear();
                }
            }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

    [Theory]
    [InlineData(0, .22)]
    [InlineData(2000, .22 / 21)]
    [InlineData(-100, double.NaN)]
    public void Spell_channels_use_cast_speed_without_the_new_action_frame_limit(int speed, double seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-channel-speed-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "channel-speed", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "ChannelSpeed", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireBeam")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new ItemSocket(SocketColour.White, gem)],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            conn.PlayerStats[Program.Stats.Key("cast_speed_+%")!.Value] = speed;
            var skill = Game.GemSkillEffects(conn).Single();
            foreach (bool lockstep in new[] { false, true })
            {
                conn.LockstepClient = lockstep;
                if (double.IsNaN(seconds)) Assert.Null(Game.SupportedChannelSeconds(conn, skill.Id));
                else Assert.Equal(seconds, Game.SupportedChannelSeconds(conn, skill.Id)!.Value, 6);
                // Attack channels retain the weapon timing; cast speed must not change it.
                Assert.Equal(Game.SupportedAttackSecondsFor(conn, SkillCatalog.FirstSkillId, null, clampToTick: false),
                    Game.SupportedChannelSeconds(conn, SkillCatalog.FirstSkillId));
                conn.LockstepSkill.Clear();
                conn.LockstepSkill.Press((110, 100), 0x3001, skill.Id, 0x400, repeats: false);
                Assert.True(conn.LockstepSkill.Due(free: true));
                Game.BeginPaidChannel(conn, skill.Id, skill.Effect, 0, 0x8001);
                Assert.Equal(lockstep ? (ushort?)0x8001 : null, conn.LockstepSkill.Channel);
                conn.LockstepSkill.Release();
                Assert.Equal(lockstep ? (ushort?)0x8001 : null, conn.LockstepSkill.ChannelReleased());
                if (lockstep)
                {
                    conn.LockstepBusyUntil = 10.65;
                    conn.LockstepCastWindow = new(10, .65, .17, .46, 0x4923); // Freezing Pulse's action.
                    conn.LockstepSkill.Press((110, 100), null, skill.Id, 0x401, repeats: false);
                    Assert.True(Game.LockstepPlayerFree(conn, 10.1)); // A different spell may replace it early.
                    Assert.False(Game.LockstepPlayerFree(conn, 10.3));
                    Assert.False(Game.LockstepPlayerFree(conn, 10.5)); // The late window is only for Move.
                    conn.LockstepSkill.Press((110, 100), null, skill.Id, 1, repeats: false);
                    Assert.False(Game.LockstepPlayerFree(conn, 10.1));
                    conn.LockstepSkill.Press((110, 100), null, skill.Id, 0x401, repeats: false);
                    Assert.True(conn.LockstepSkill.Due(free: true));
                    Assert.False(Game.LockstepPlayerFree(conn, 10.1)); // A consumed request does not cancel again.
                    conn.LockstepSkill.Press((110, 100), null, skill.Id, 0x401, repeats: false);
                    conn.LockstepCastWindow = conn.LockstepCastWindow.Value with
                        { ActionHash = Game.SkillsFor(conn).ActionHash(skill.Effect) };
                    Assert.False(Game.LockstepPlayerFree(conn, 10.1)); // The same action cannot restart early.
                }
            }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

}
