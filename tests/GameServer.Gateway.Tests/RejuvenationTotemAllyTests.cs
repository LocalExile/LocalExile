namespace GameServer.Gateway.Tests;

public sealed class RejuvenationTotemAllyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejuvenation_reaches_nearby_allies_and_only_the_vaal_aura_redirects_hits(bool vaal)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var owner = fixture.Player;
        owner.Use(new(1, "rejuvenation-witness", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        owner.Walk.Teleport(100, 100, 0);
        owner.Connection.PlayerObjectCreated = true;
        owner.ProjectileRuntimeState = fixture.Session;
        var peer = new Player(owner.Layout, 1, 1)
        {
            Character = { Name = "RejuvenationPeer", Experience = CharacterStats.ExperienceFor(20) },
            Instance = owner.Instance,
            PlayerLife = 100,
            Pools = new(20, 200, 100, 2, 0),
            LeagueModule = owner.LeagueModule
        };
        peer.Use(owner.Layout);
        peer.Clock.Start();
        _ = owner.Area;
        fixture.Equip((vaal ? "Vaal Rejuvenation Totem" : "Rejuvenation Totem", 1));
        var skill = Assert.Single(Game.GemSkillEffects(owner), gem =>
            Program.Skills.EffectId(gem.Effect) == (vaal ? "VaalRejuvenationTotem" : "RejuvenationTotem"));
        int Regen(Player player) => Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.BaseLifeRegenerationRatePerMinute);
        int baseline = Regen(peer);
        var variety = Game.TotemVariety(owner, skill.Effect);
        Assert.NotNull(variety);
        var id = await Game.CreateTotem(owner, skill.Id, skill.Effect, skill.Level, variety, (105, 100));
        Assert.NotNull(id);
        var totem = Assert.Single(owner.Totems.All);
        Assert.Equal(id, totem.Id);
        Assert.NotNull(totem.Aura);
        Assert.Equal(vaal ? 40 : 0, totem.Aura.HitRedirectPercent);
        int rate = Assert.Single(totem.Aura.Stats, x => x.Stat == Stat.BaseLifeRegenerationRatePerMinute).Value;
        peer.Walk.Teleport(totem.At.X, totem.At.Y, Game.AreaTime(peer));
        Game.TotemPresentation(peer);
        Assert.Equal(baseline + rate, Regen(peer));
        Assert.Equal(id, peer.Totems.SentAuraTotem);

        var minion = new SpawnedMonster("regeneration witness", new(totem.At.X, totem.At.Y, 0), 1, 0, 1000)
            { ObjectId = 0x8000511, OwnerId = peer.Id, Hostile = false, Level = 76, Variety = Game.Monsters.Find("Hungry Corpse")! };
        var definition = new GolemCatalog.Definition(-1, "test", "Skeleton", "test", 0, [], false, false, false);
        var fighter = new GolemState.Golem(minion.ObjectId, minion, definition, 0);
        peer.Summons.Fighters[fighter.Id] = fighter;
        peer.Area.Monsters[fighter.Id] = minion;
        Game.GolemStepLocked(peer, fighter, 0);
        uint lifeBefore = minion.Life = minion.MaxLife / 2;
        Game.GolemStepLocked(peer, fighter, 1);
        Assert.Equal(lifeBefore + (uint)(rate / 60), minion.Life);

        uint totemLife = totem.Life;
        Assert.Equal(vaal ? (6, 3) : (10, 5),
            Game.TotemsTakeHitLocked(peer, 10, 5, Game.AreaTime(peer), new Dictionary<uint, int>()));
        Assert.Equal(totemLife - (vaal ? 6u : 0u), totem.Life);

        peer.Walk.Teleport(totem.At.X + totem.Aura.Radius + 1, totem.At.Y, Game.AreaTime(peer));
        Game.TotemPresentation(peer);
        Assert.Equal(baseline, Regen(peer));
        Assert.Equal(0u, peer.Totems.SentAuraTotem);

        peer.Walk.Teleport(totem.At.X, totem.At.Y, Game.AreaTime(peer));
        Game.TotemPresentation(peer);
        Assert.Equal(baseline + rate, Regen(peer));
        totem.DiedAt = Game.AreaTime(owner);
        Game.TotemPresentation(owner);
        Game.TotemPresentation(peer);
        Assert.Equal(baseline, Regen(peer));
    }
}
