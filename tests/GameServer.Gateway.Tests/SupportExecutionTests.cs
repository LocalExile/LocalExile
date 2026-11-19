using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

public sealed class SupportExecutionTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);

    static Player Connection(string name)
    {
        var conn = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        return conn;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
    static ushort Equip(Player conn, string active, string support)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with
        {
            Sockets = [new(SocketColour.White, Gem(active)), new(SocketColour.White, Gem(support))], Links = [2],
        });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Second_wind_reads_native_supports_and_recovers_stored_uses_in_order()
    {
        string path = Path.Combine(Path.GetTempPath(), $"supports-cooldown-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SecondWindAcceptance");
        try
        {
            ushort id = Equip(conn, "SkillGemDash", "SupportGemSecondWind");
            var gem = Game.SupportedSkill(conn, id);
            var resolved = Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality);
            Assert.True(resolved is not null, string.Join(", ", gem.Stats.Select(s => $"{Program.Stats.Name(s.Key)}={s.Value}")));
            var plan = resolved!.Value;
            // PoEDB: one additional NON-INSTANT use, level-one 24% reduced recovery, 200% cost multiplier.
            Assert.Equal(2, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            Assert.Equal(200, gem.Supports.Single().CostMultiplier);
            var state = new SingleUseCooldownState(0, plan.RecoveryRate);
            Assert.True(state.TryUse(0, plan));
            state.Refresh(.25, plan.RecoveryRate);
            double progress = state.RemainingBaseMilliseconds;
            Assert.True(state.TryUse(.25, plan)); Assert.Equal(progress, state.RemainingBaseMilliseconds, 6);
            Assert.False(state.TryUse(.25, plan)); Assert.Equal(2, state.SpentUses);
            // Native replacement contains two entries, and the second has a full base-time budget.
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate, state.SpentUses)]);
            var single = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate)]);
            Assert.Equal(single.Length + 16, bytes.Length);
            Assert.Equal((float)(progress / 1000), BitConverter.ToSingle(bytes, bytes.Length - 32));
            Assert.Equal(plan.BaseMilliseconds / 1000f, BitConverter.ToSingle(bytes, bytes.Length - 16));
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan.RecoveryRate);
            Assert.True(state.Ready); Assert.Equal(1, state.SpentUses);
            Assert.True(state.TryUse(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan));
            // Rate changes settle progress first, and reducing capacity never refunds a spent use.
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate) + .5, 2);
            state.Capacity(1); Assert.False(state.Ready);
            state.Capacity(2); Assert.False(state.Ready); // Re-linking cannot refund either spent use.
            state.Refresh(100, 2); Assert.True(state.FullyRecovered);
            var zero = new SingleUseCooldown(1000, -100, 2);
            Assert.True(state.TryUse(100, zero)); Assert.True(state.TryUse(100, zero));
            state.Refresh(1000, 0); Assert.False(state.Ready);
            // Instant skills receive recovery and cost modifiers, but never the extra use.
            id = Equip(conn, "SkillGemFrostblink", "SupportGemSecondWind");
            gem = Game.SupportedSkill(conn, id);
            plan = Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality)!.Value;
            Assert.Equal(1, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            // Native Flame Dash already has three charges; Second Wind grants the fourth.
            id = Equip(conn, "SkillGemFlameDash", "SupportGemSecondWind");
            gem = Game.SupportedSkill(conn, id);
            Assert.Equal(4, Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality)!.Value.Uses);
            // PoEDB's level-20 penalty (-5) and 20% quality recovery (+5) cancel in the installed native data.
            var qualityStats = Game.SkillsFor(conn).EffectStats(Game.SkillsFor(conn).EffectRow("SupportSecondWind"), 20, 20)
                .ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(0, qualityStats[Stat.BaseCooldownSpeedPlusPct]);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }

}
