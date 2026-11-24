using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BloodMagicTests
{
    [Theory]
    [InlineData("SkillGemBloodreap", 0, 25)]
    [InlineData("SkillGemBloodreap", 5, 50)]
    [InlineData("SkillGemExsanguinate", 0, 16)]
    [InlineData("SkillGemCorruptingFever", 0, 78)]
    public void Native_life_cost_is_required_before_admission_and_paid_once(string gemName, int charges, uint expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"native-life-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = SpellTotemExecutionTests.PlayerOf(gemName);
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gemName)!);
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new(SocketColour.White, gem)], Links = [1],
            });
            var skill = Game.GemSkillEffects(player).Single();
            player.BloodCharges = charges;
            player.PlayerLife = expected;
            Assert.False(Game.CanPaySkillCost(player, skill.Id));
            Assert.False(Game.PayMana(player, skill.Id, out _, out _));
            Assert.Equal(expected, player.PlayerLife);
            player.PlayerLife++;
            Assert.True(Game.CanPaySkillCost(player, skill.Id));
            Assert.True(Game.PayMana(player, skill.Id, out uint mana, out uint life));
            Assert.Equal(expected, life);
            Assert.Equal(0u, mana);
            Assert.Equal(1u, player.PlayerLife);
            Assert.Equal(1000u, player.PlayerMana);
        }
        finally
        {
            PlayerInventories.Forget(gemName);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Reap_gains_a_charge_only_when_no_hit_enemy_dies()
    {
        Assert.Equal(1, BloodCharges.Settle(2, 1, 1, 5));
        Assert.Equal(3, BloodCharges.Settle(2, 0, 2, 5));
        Assert.Equal(2, BloodCharges.Settle(2, 0, 0, 5));
    }

    // Blood Magic removes all Mana and grants 10% more maximum Life (ReminderTextKeystoneBloodMagic).
    [Fact]
    public void Blood_magic_keystone_removes_mana_and_raises_life()
    {
        var stats = new Dictionary<uint, int>
        {
            [Stat.BaseMaximumLife] = 100,
            [Stat.MaximumLifePlusPct] = 50,
            [Stat.BaseMaximumMana] = 50,
        };
        var without = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, Game.PassiveBonus(stats, default, default));
        stats[Stat.KeystoneBloodMagic] = 1;
        var with = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, Game.PassiveBonus(stats, default, default));
        // (38 + 12 + 7 from strength + 100) x 150% = 235.5, then 10% more = 259.05.
        Assert.Equal(235u, without.Life);
        Assert.Equal(259u, with.Life);
        Assert.True(without.Mana > 0);
        Assert.Equal(0u, with.Mana);
        // A second "more" Life multiplier (Life Mastery) stacks multiplicatively: 235.5 x 1.1 x 1.1 = 284.955 -> 284.
        stats[Stat.LifeMasteryCountMaximumLifePlusPctFinal] = 10;
        var withMastery = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, Game.PassiveBonus(stats, default, default));
        Assert.Equal(284u, withMastery.Life);
    }

    // With Blood Magic an aura's Mana reservation is taken from Life instead, Petrified Blood's too.
    [Fact]
    public async Task Blood_magic_keystone_reserves_life()
    {
        using var f = new MinionUtilityTests.Fixture();
        bool bloodMagic = false;
        using var sources = GameEvents.PlayerStatSources.Add("Game", (p, stats) => !bloodMagic || !ReferenceEquals(p, f.Player)
            ? stats : new Dictionary<uint, int>(stats) { [Stat.KeystoneBloodMagic] = 1 });

        ushort purity = f.Equip(("Purity of Fire", 1)).Single();
        var mana = Game.SupportedReservationCost(f.Player, purity)!.Value;
        Assert.False(Game.ReservationContext(f.Player, purity).Life);
        Assert.True(mana.ManaPercent > 0);
        bloodMagic = true;
        f.Player.PlayerSkillsCache = null;
        var life = Game.SupportedReservationCost(f.Player, purity)!.Value;
        Assert.True(Game.ReservationContext(f.Player, purity).Life);
        Assert.Equal((0, 0, mana.ManaFlat, mana.ManaPercent),
            (life.ManaFlat, life.ManaPercent, life.LifeFlat, life.LifePercent));

        ushort id = f.Equip(("Petrified Blood", 1)).Single();
        var petrified = Game.SupportedSkill(f.Player, id);
        var cost = Game.SupportedReservationCost(f.Player, id)!.Value;
        Assert.True(await Game.TogglePetrifiedBlood(f.Player, id, petrified.Effect, "Petrified Blood", cost,
            petrified.Level, petrified.Quality));
        Assert.True(f.Player.PetrifiedBlood.ReservesLife);
        Assert.Equal(cost.LifePercent, f.Player.PetrifiedBlood.ManaPercent);
        Assert.True(Game.ReservedLife(f.Player) > 0);

        // Refunded and allocated in the passive tree, which pauses the game: the paused frame carries the new maximum
        // Life and Mana (0x1b3) and the Mana pool (0x1b1) at once, not at the resume.
        var player = f.Player;
        var area = player.Area;
        var updates = new PlayerUpdatesState(f.Lifetime, new FixedSchedule());
        player.PlayerUpdatesState = updates;
        Assert.True(player.PlayerUpdatesWorker.TryStart(player, updates.Rules, null, updates, out updates.Generation));
        await Program.BeginWorldFrames(player, Program.FrameMs / 1000d);
        // Register the Stats system as Game.StartStats does so EndPausedFrame steps StepPlayerStats inside OnFrame
        // (Area.Owned = true -> synchronous ShareInFrame, no off-frame SendRecordsAsync race).
        area.Systems.Add("stats", AreaPhase.Stats, _ => Game.StepPlayerStats(player), source: f.Lifetime);
        var statBatches = new List<(uint Key, int Value)[]>();
        var sent = new List<byte[]>();
        var written = AreaStats.Written;
        var outgoing = Program.Outgoing;
        AreaStats.Written = (a, entity, list) =>
        {
            written?.Invoke(a, entity, list);
            if (ReferenceEquals(a, area) && entity == player.Id) lock (statBatches) statBatches.Add([.. list]);
        };
        Program.Outgoing = (p, bytes) => { outgoing?.Invoke(p, bytes); if (ReferenceEquals(p, player)) lock (sent) sent.Add(bytes.ToArray()); };
        try
        {
            player.Clock.Stop();
            await player.Frames.EndPausedFrame(player);
            foreach (bool allocated in new[] { false, true })
            {
                lock (statBatches) statBatches.Clear();
                lock (sent) sent.Clear();
                bloodMagic = allocated;
                Game.RecomputePoolsAndSpeed(player);
                await Game.SendCharacterStats(player, PlayerStatChange.Build);
                await Game.SendPlayerMana(player);
                await player.Frames.EndPausedFrame(player);
                (uint Key, int Value)[][] batches;
                lock (statBatches) batches = [.. statBatches];
                var flatStats = batches.SelectMany(b => b).ToArray();
                Assert.Equal(allocated ? 0 : (int)player.Pools.Mana, flatStats.Last(s => s.Key == Stat.MaximumMana).Value);
                Assert.Equal((int)player.Pools.Life, flatStats.Last(s => s.Key == Stat.MaximumLife).Value);
                var manaBatch = batches.Last(b => b.Any(s => s.Key == Stat.MaximumMana));
                lock (sent)
                {
                    Assert.Contains(sent, p => p.AsSpan().SequenceEqual(InstanceEntities.StatsUpdate(player.Id, manaBatch)));
                    Assert.Contains(sent, p => p.AsSpan().SequenceEqual(InstanceProtocol.LifeUpdate(player.Id, player.PlayerMana,
                        AreaLife.ReservedMana?.Invoke(player) ?? 0, AreaLife.ManaPool, orderKey: uint.MaxValue)));
                }
            }
            Assert.Equal(0u, player.Pools.Mana);
        }
        finally { AreaStats.Written = written; Program.Outgoing = outgoing; }
    }

    sealed class FixedSchedule : IPlayerUpdateSchedule
    {
        public double Now(Player player) => 1;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    // The Blood Magic keystone turns a skill's whole Mana cost into a Life cost: Tornado Shot's 9 Mana is paid as
    // 9 Life, Mana stays full, and no Lifetap buff comes with it (that buff belongs to the support gem).
}
