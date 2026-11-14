using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Arrogance Support (GrantedEffect 2046): "Supported Skills Reserve Life instead of Mana" is its stat set's implicit
// base_skill_reserve_life_instead_of_mana, its aura_effect_+% is 19 at level 20, and its reservation multiplier is 201%
// at level 20. The three inputs ToggleAura and ReconcileOwnAuras read must come out of the supported aura's own gem.
public sealed class ArroganceSupportTests
{
    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 2000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 2000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Gem(string name, int level)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with
        {
            GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
        };
    }

    static ushort Equip(ClientState conn, string active, params string[] supports)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sockets = new[] { active }.Concat(supports)
            .Select(n => new ItemSocket(SocketColour.White, Gem(n, 20))).ToArray();
        var sword = ItemCatalog.Create(Program.Items.Find(
            "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with { Sockets = sockets, Links = [(byte)sockets.Length] });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Arrogance_switches_its_aura_to_life_and_adds_its_increased_aura_effect()
    {
        string path = Path.Combine(Path.GetTempPath(), $"support-arrogance-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("ArroganceAcceptance");
        try
        {
            // The support's own level-20 values, as the client data stores them.
            var support = Game.SkillsFor(conn).EffectStats(2046, 20).ToDictionary(s => s.Item1, s => s.Item2);
            int Value(string name) => support.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(1, Value("base_skill_reserve_life_instead_of_mana"));
            Assert.Equal(19, Value("aura_effect_+%"));

            ushort grace = Equip(conn, "Grace", "Arrogance");
            var gem = Game.SupportedSkill(conn, grace);
            Assert.Equal(20, gem.Level);
            var applied = Assert.Single(gem.Supports);
            Assert.Equal("SupportArrogance", applied.Effect.Id);
            Assert.Equal(201, applied.CostMultiplier);

            // Grace reserves 50% of mana, so Arrogance's 201% makes it reserve 100.50% (10050 permyriad) of life:
            // the percentage is exact, not 10049 from a binary rounding of 2.01.
            var reservation = Game.SupportedReservation(conn, grace);
            Assert.NotNull(reservation);
            Assert.Equal(0, reservation!.Value.Flat);
            Assert.Equal(10050, reservation.Value.Percent);
            var context = Game.ReservationContext(conn, grace);
            Assert.True(context.Life);
            Assert.Equal(0, context.Efficiency);
            var auraSupport = Game.AuraSupport(conn, grace);
            Assert.Equal(19, auraSupport.EffectPct);
            Assert.True(auraSupport.AffectsSelf); // Generosity is what forbids affecting the caster, not Arrogance.

            // Without Arrogance the same Grace reserves 50% of mana and gets no support effect.
            ushort plain = Equip(conn, "Grace");
            Assert.Empty(Game.SupportedSkill(conn, plain).Supports);
            Assert.Equal((0, 5000), Game.SupportedReservation(conn, plain));
            Assert.False(Game.ReservationContext(conn, plain).Life);
            Assert.Equal(0, Game.AuraSupport(conn, plain).EffectPct);

            // The reservation an activated aura reports is taken from those inputs: 100.50% of the 2000 maximum life.
            var aura = new AuraCatalog.Aura(grace, gem.Effect, "Grace", "Grace", 1, AuraCatalog.BuffRow("Grace"),
                reservation.Value.Flat, reservation.Value.Percent, gem.Level, gem.Quality,
                ReservesLife: context.Life, ReservationEfficiencyPct: context.Efficiency);
            Assert.Equal(2010, AuraCatalog.RequiredLife(conn.Pools, [aura]));
            Assert.Equal(0, AuraCatalog.RequiredMana(conn.Pools, [aura]));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
