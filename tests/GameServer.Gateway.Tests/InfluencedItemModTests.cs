using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class InfluencedItemModTests : IDisposable
{
    readonly string save = Path.Combine(Path.GetTempPath(), $"influenced-mods-{Guid.NewGuid():N}.db");
    readonly IGameLogic? previous = Program.GameLogicOrNull;
    readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    readonly Player player;
    readonly IDisposable exemptionSubscription;

    public InfluencedItemModTests()
    {
        Saves.Open(save);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string name = "Influenced" + Guid.NewGuid().ToString("N");
        player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = false },
            Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        player.Clock.Stop();
        player.Walk.Teleport(100, 100, 0);
        // The influenced mods' attribute requirements are not what these checks measure.
        exemptionSubscription = GameEvents.RequirementExemptions.Add("Game", p => ReferenceEquals(p, player)
            ? [.. Game.ItemsOf(p).EquipmentSnapshot().Select(e => e.Item.Id)] : []);
        player.Pools = Game.PoolsFor(player, 100);
    }

    public void Dispose()
    {
        exemptionSubscription.Dispose();
        player.Closed = true;
        var cleanupErrors = new List<Exception>();
        lifetime.Stop(cleanupErrors.Add);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previous!);
        PlayerInventories.Forget(player.Character.Name!);
        TestEnvironment.ReleaseSaves(save);
        Assert.Empty(cleanupErrors);
    }

    // Each influenced mod on a body armour against the same character without it: the stat its consumer reads moves by
    // the amount the mod's text gives.
    [Fact]
    public void Influenced_item_mods_reach_the_stats_their_consumers_read()
    {
        var plain = Wear();
        var worn = Wear(("AddedColdDamageToSpellsAndAttacksUber1__", [20, 40]));
        Assert.Equal(20, Delta(plain, worn, Stat.AttackMinimumAddedColdDamage));
        Assert.Equal(40, Delta(plain, worn, Stat.SpellMaximumAddedColdDamage));
        Assert.Equal(0, Delta(plain, worn, Stat.GlobalMinimumAddedColdDamage));

        worn = Wear(("ChanceToDodgeUber1", [5]));
        Assert.Equal(7, worn.Pools.SpellSuppressionChance - plain.Pools.SpellSuppressionChance);
        worn = Wear(("ReducedManaReservedUber1", [-5]));
        Assert.Equal(10, worn.Pools.ManaReservationEfficiencyPct - plain.Pools.ManaReservationEfficiencyPct);

        // "+#% Chance to Block Attack Damage if you have been Damaged by a Hit Recently" (being hit alone is not), then
        // "#% increased Damage per 1% Chance to Block" on top of that block.
        player.RecentEvents.Record(StatConditions.BeenHit, Game.AreaTime(player));
        worn = Wear(("ChanceToBlockIfDamagedRecentlyInfluence1_", [18]));
        Assert.Equal(0, worn.Pools.BlockChance - plain.Pools.BlockChance);
        player.RecentEvents.Record(Game.DamagedByHitEvent, Game.AreaTime(player));
        plain = Wear();
        worn = Wear(("ChanceToBlockIfDamagedRecentlyInfluence1_", [18]));
        Assert.Equal(18, worn.Pools.BlockChance - plain.Pools.BlockChance);
        var both = Wear(("ChanceToBlockIfDamagedRecentlyInfluence1_", [18]), ("DamagePerBlockChanceUber1_", [1]));
        Assert.Equal(both.Pools.BlockChance, Delta(worn, both, Stat.DamagePlusPct));
    }

    // The on-hit consumers, each against its own input.
    [Fact]
    public void Influenced_item_mods_apply_on_hit()
    {
        var monster = new SpawnedMonster("target", new(110, 100, 0), 70, 1, 1000) { Hostile = true, Rarity = MonsterRarity.Rare };
        var hit = new Dictionary<uint, int> { [Stat.CurseOnHitLevelDespair] = 5 };
        Game.ItemCursesOnHitLocked(player, monster, hit, 1.0);
        Assert.True(monster.Curses.All.TryGetValue((player.Id, "Despair"), out var despair));
        // A held curse is not re-sent within a second; a later hit reapplies it for its full duration.
        Game.ItemCursesOnHitLocked(player, monster, hit, 1.5);
        Assert.Equal(despair.Buff, monster.Curses.All[(player.Id, "Despair")].Buff);
        Game.ItemCursesOnHitLocked(player, monster, hit, 3.0);
        Assert.Equal(despair.Until + 2, monster.Curses.All[(player.Id, "Despair")].Until, 6);

        var crit = new Dictionary<uint, int> { [Stat.CriticalStrikeMultiplierVsEnemiesOnFullLifePlus] = 50 };
        int full = Game.CriticalMultiplierOn(monster, 150, crit);
        Assert.Equal(full, Game.CriticalMultiplierOn(monster, 200));
        monster.Life = 500;
        Assert.Equal(full - 50, Game.CriticalMultiplierOn(monster, 150, crit));

        var spellHit = new Dictionary<uint, int> { [Stat.ManaGainedOnSpellHit] = 3 };
        Assert.Equal(3, LeechSources.From(spellHit.GetValueOrDefault, attack: false).GainOnHit.Mana);
        Assert.Equal(0, LeechSources.From(spellHit.GetValueOrDefault, attack: true).GainOnHit.Mana);
    }

    sealed record Built(IReadOnlyDictionary<uint, int> Stats, CharacterStats.Pools Pools);

    Built Wear(params (string Id, int[] Values)[] mods)
    {
        var items = Game.ItemsOf(player);
        foreach (var old in items.Items(PlayerInventories.BodyArmour).ToArray()) items.Remove(PlayerInventories.BodyArmour, old.Id);
        var body = ItemCatalog.Create(Program.Items.Find("Plate Vest")!) with
        {
            Mods = [.. mods.Select(m => Program.Items.Mods.ById(m.Id)!.Roll(ModSection.Explicit, new Random(1)) with { Values = m.Values })],
        };
        items.Add(PlayerInventories.BodyArmour, 0, 0, body);
        player.Pools = Game.PoolsFor(player, 100);
        return new(new Dictionary<uint, int>(Game.CharacterPlayerStats(player)), player.Pools);
    }

    static int Delta(Built plain, Built worn, uint stat) =>
        worn.Stats.GetValueOrDefault(stat) - plain.Stats.GetValueOrDefault(stat);
}
