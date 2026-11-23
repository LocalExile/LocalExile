using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SkillTypeEffectTests
{
    [Fact]
    public void Trap_critical_multiplier_only_applies_to_traps()
    {
        int Profile(string name, bool allocate)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (allocate && ReferenceEquals(owner, player))
                    values[Stat.TrapCriticalStrikeMultiplierPlus] = 35;
                return values;
            });
            ushort skill = Assert.Single(f.Equip((name, 1)));
            return Game.SupportedCriticalMultiplier(player, skill);
        }

        int trap = Profile("Ice Trap", allocate: false);
        Assert.Equal(trap + 35, Profile("Ice Trap", allocate: true));
        Assert.Equal(Profile("Ice Nova", allocate: false), Profile("Ice Nova", allocate: true));
    }

    [Fact]
    public void A_guaranteed_passive_awards_an_extra_soul_once_on_an_eligible_kill()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.ChanceToGainVaalSoulOnKillPct] = 100;
            return values;
        });
        f.Equip(("Vaal Fireball", 1));
        Game.RefreshVaalSouls(player);
        player.Walk.Teleport(100, 100, 0);

        SpawnedMonster Defeated(uint id)
        {
            var monster = new SpawnedMonster("soul target", new(102, 100, 0), 0, 0, 100)
            {
                Variety = Game.Monsters.Find("Sand Spitter"), Life = 0, Dead = true
            };
            player.Area.Monsters.Add(id, monster);
            return monster;
        }

        var kill = Defeated(720);
        Assert.True(Game.CollectVaalSoulOnKill(player, 720, kill));
        Assert.Equal(2, (int)Assert.Single(player.VaalSouls.Read()).Current);
        Assert.False(Game.CollectVaalSoulOnKill(player, 720, kill));
        player.VaalPreventionUntil = 100;
        Assert.False(Game.CollectVaalSoulOnKill(player, 721, Defeated(721)));
        Assert.Equal(2, (int)Assert.Single(player.VaalSouls.Read()).Current);
    }
}
