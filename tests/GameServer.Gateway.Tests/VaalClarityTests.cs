using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class VaalClarityTests
{
    [Fact]
    public async Task Vaal_Clarity_spends_souls_and_waives_only_mana_while_its_aura_reaches_a_player()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var caster = fixture.Player;
        caster.Connection.PlayerObjectCreated = true;
        caster.Walk.Teleport(100, 100, 0);
        caster.ProjectileRuntimeState = fixture.Session;
        fixture.Equip(("Vaal Clarity", 1), ("Fireball", 1));
        EquipRobe(caster, "Freezing Pulse", "Lifetap Support");

        var clarity = Assert.Single(Game.GemSkillEffects(caster), g => Program.Skills.EffectId(g.Effect) == "VaalClarity");
        ushort fireball = Assert.Single(Game.GemSkillEffects(caster), g => g.Name == "Fireball").Id;
        ushort pulse = Assert.Single(Game.GemSkillEffects(caster), g => g.Name == "Freezing Pulse").Id;
        var budget = Assert.NotNull(Program.Skills.VaalBudget(clarity.Effect, clarity.Level));
        var cooldown = Game.CooldownForSkill(caster, clarity.Id);
        caster.PlayerMana = 0;
        Assert.False(Game.CanPaySkillCost(caster, fireball));

        Game.RefreshVaalSouls(caster);
        Assert.False(Game.TryBeginSkillCooldownUse(caster, clarity.Id, clarity.Effect, cooldown, false, () => caster.ActionCount++, out _));
        for (int i = 0; i < budget.BaseCost; i++) Assert.True(caster.VaalSouls.Gain());
        Assert.True(Game.TryBeginSkillCooldownUse(caster, clarity.Id, clarity.Effect, cooldown, false,
            () => caster.ActionCount++, out var use));
        Assert.True(await Game.CastGemSkill(caster, clarity.Id, (100, 100), (100, 100), cooldownUse: use));
        Assert.Equal(0u, caster.VaalSouls.Read().Single(entry => entry.ActiveSkillsRow == budget.ActiveSkillsRow).Current);
        AssertFreeFireball(caster, fireball);
        uint lifeBefore = caster.PlayerLife;
        Assert.True(Game.PayMana(caster, pulse, out uint mana, out uint life));
        Assert.Equal(0u, mana);
        Assert.True(life > 0);
        Assert.Equal(lifeBefore - life, caster.PlayerLife);

        var ally = new Player(caster.Layout, 2, 1)
        {
            Instance = caster.Instance,
            Character = { Name = "VaalClarityAlly" + Guid.NewGuid().ToString("N"), Experience = caster.Character.Experience },
            PlayerLife = 10000,
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = caster.LeagueModule,
            Connection = { PlayerSent = true, PlayerObjectCreated = true },
        };
        TestRequirements.Waive(ally);
        try
        {
            EquipRobe(ally, "Fireball");
            ushort allyFireball = Assert.Single(Game.GemSkillEffects(ally), g => g.Name == "Fireball").Id;
            Assert.False(Game.CanPaySkillCost(ally, allyFireball));
            caster.Area.Grid.Set(new(caster.Id), (100, 100));
            caster.PeerTemplate = new(caster.Character.Name!, 0, 100, 100, caster.PlayerLife, caster.PlayerMana);
            Game.RefreshPeerScale(caster, 100);
            caster.Instance.AddPlayer(ally);

            void ReconcileAllyAt(int x)
            {
                ally.Walk.Teleport(x, 100, 0);
                caster.Area.Grid.Set(new(ally.Id), (x, 100));
                caster.Area.Zones.Update(moved: true);
                Game.WithOwnAuraRecords(ally, () => []);
            }

            ReconcileAllyAt(110);
            AssertFreeFireball(ally, allyFireball);
            ReconcileAllyAt(800);
            Assert.False(Game.CanPaySkillCost(ally, allyFireball));
            ReconcileAllyAt(110);
            AssertFreeFireball(ally, allyFireball);

            var state = Game.TemporaryAuraStates.GetOrCreateValue(caster);
            state.Active[clarity.Id] = state.Active[clarity.Id] with { Until = -1 };
            Game.ExpireTemporaryAuras(caster);
            Game.WithOwnAuraRecords(caster, () => []);
            Game.RefreshPeerScale(caster, 100);
            ReconcileAllyAt(110);
            Assert.False(Game.CanPaySkillCost(caster, fireball));
            Assert.False(Game.CanPaySkillCost(ally, allyFireball));
        }
        finally
        {
            caster.Instance.RemovePlayer(ally.Id);
            PlayerInventories.Forget(ally.Character.Name!);
        }
    }

    static void AssertFreeFireball(Player player, ushort fireball)
    {
        Assert.True(Game.PayMana(player, fireball, out uint mana, out uint life));
        Assert.Equal((0u, 0u), (mana, life));
    }

    static void EquipRobe(Player player, params string[] gemNames)
    {
        Item MakeGem(string name)
        {
            var gem = Program.Items.FindGem(name)!.Value;
            return ItemCatalog.Create(gem.Type, random: new(1)) with { GemVariant = gem.Variant.Index };
        }

        Game.ItemsOf(player).Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Simple Robe")!, random: new(1)) with
            {
                Sockets = gemNames.Select(name => new ItemSocket(SocketColour.White, MakeGem(name))).ToArray(),
                Links = [(byte)gemNames.Length],
            });
    }
}
