using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class SpellEchoTests
{
    static int Speed(int increased, bool fishingRod, params (string Name, int Value)[] stats) =>
        Game.FinalCastSpeedPct(name => stats.FirstOrDefault(s => s.Name == name).Value, () => fishingRod, increased);

    // The client's cast_speed_+% (0x1427101c0) puts its combined "more" final (0x14270ed40) on the summed increases.
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

    // Level-one Spell Echo grants one repeat and 40% more cast speed to each linked active gem.
    [Theory]
    [InlineData("Fireball", "Frostbolt")]
    [InlineData("Ice Nova", "Ice Nova of Deep Freeze", "Ice Nova of Frostbolts", "Spark", "Freezing Pulse")]
    public void Spell_Echo_repeats_a_supported_spell_and_casts_it_faster(params string[] gemNames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-spell-echo-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "spell-echo", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "SpellEcho", ClassRow = 6, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var weapon = ItemCatalog.Create(Program.Items.Find(gemNames.Length > 2 ? "Gnarled Branch" : "Driftwood Wand")!);
            var sockets = gemNames.Append("Spell Echo Support").Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
            }).ToArray();
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon with { Sockets = sockets, Links = [(byte)sockets.Length] });
            if (gemNames.Length == 2)
                Game.ItemsOf(conn).Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Goathide Buckler")!));
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            conn.LockstepClient = true;
            var active = Game.GemSkillEffects(conn);
            foreach (var name in gemNames)
            {
                var skill = active.Single(g => g.Name == name);
                Assert.Equal(1, Game.SpellRepeats(conn, skill.Id, skill.Effect));
                uint repeatStat = Program.Stats.Key("spell_repeat_count")!.Value;
                Assert.Equal(1, Game.PlayerSkills(conn).Single(s => s.Id == skill.Id).Stats!.Single(s => s.Key == repeatStat).Value);
                int baseMs = name switch { "Fireball" or "Frostbolt" => 750, "Spark" => 600, "Freezing Pulse" => 650, _ => 700 };
                double castSeconds = baseMs / 1.4 / 1000;
                Assert.Equal((int)Math.Round(baseMs / 1.4), Game.CastMs(conn, skill.Effect, skill.Id));
                Assert.Equal(2 * castSeconds, Game.LockstepActionHold(conn, skill.Id, 0x8001), 6);
                // A target changes the aim, not the native cast duration or the held spell's repeat gate.
                foreach (uint? target in new uint?[] { null, 0x3001 })
                {
                    conn.LockstepSkill.Press((110, 100), target, skill.Id, 0x400, repeats: true);
                    conn.LockstepBusyUntil = 0;
                    Game.HoldLockstepSkill(conn, 0x8001, 10, 10.033);
                    Assert.Equal(10 + Game.LockstepActionHold(conn, skill.Id, 0x8001), conn.LockstepBusyUntil, 6);
                    if (name == "Ice Nova of Frostbolts")
                    {
                        conn.LockstepSequence = 1;
                        Game.ReleaseFrostboltNovaRepeats(conn, 0x8002, 1, 0);
                        Assert.Equal(11, conn.LockstepBusyUntil);
                        Game.ReleaseFrostboltNovaRepeats(conn, 0x8001, 1, 0);
                        Assert.Equal(10.5, conn.LockstepBusyUntil, 6);
                        Assert.Equal(.5, conn.LockstepCastWindow!.Value.Duration, 6);
                    }
                    if (name is "Fireball" or "Frostbolt" or "Spark" or "Freezing Pulse")
                    {
                        Assert.NotNull(conn.LockstepCastWindow);
                        // Both the Templar's staff and wand/shield ACT clips lock at 697 of 900 ms.
                        // The unused spell_projectile_wand rig clip has no lock and would admit Move at its 500 ms contact.
                        Assert.Equal(castSeconds * (1 + 697d / 900), conn.LockstepCastWindow.Value.LateFrom, 6);
                        conn.LockstepHold.Press((120, 100), 0x2909, 0x401);
                        Assert.Equal(Game.SkillsFor(conn).ActionHash(skill.Effect), conn.LockstepCastWindow.Value.ActionHash);
                        Assert.True(Game.LockstepPlayerFree(conn, 10.01)); // Before the first cast's contact.
                        Assert.False(Game.LockstepPlayerFree(conn, 10 + castSeconds + .01)); // Early in the repeat.
                        Assert.True(Game.LockstepPlayerFree(conn, conn.LockstepBusyUntil - .001)); // Late in the final cast.
                        conn.LockstepHold.Clear();
                    }
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
                var previousRules = Game.CombatLifetime;
                var rules = new ModuleLifetime("Game");
                Game.CombatLifetime = rules;
                try
                {
                    int generation = Game.BeginPaidChannel(conn, skill.Id, Program.Skills.EffectRow("LightningTendrils"), conn.ActionCount, 0x8001);
                    conn.LastChannelUpdateAt = -10;
                    Assert.True(Game.ChannelCurrent(conn, skill.Id, conn.ActionCount, generation));
                    Game.ChannelReleased(conn, 0);
                    Assert.False(Game.ChannelCurrent(conn, skill.Id, conn.ActionCount, generation));
                    generation = Game.BeginPaidChannel(conn, skill.Id, skill.Effect, conn.ActionCount, 0x8001);
                    conn.LastChannelUpdateAt = -10;
                    Assert.False(Game.ChannelCurrent(conn, skill.Id, conn.ActionCount, generation));
                }
                finally { rules.Stop(_ => { }); Game.CombatLifetime = previousRules; }
                Game.BeginPaidChannel(conn, skill.Id, skill.Effect, 0, 0x8001);
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
