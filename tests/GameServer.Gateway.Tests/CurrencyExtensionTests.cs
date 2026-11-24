using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>The currency added after the first pass: the map chisels (CurrencyItems Action add_map_alt_quality), the two
/// Eldritch orbs and Orb of Unmaking's admission.</summary>
public class CurrencyExtensionTests
{
    static ItemRolls Rolls => new(Program.Items);

    static ItemCatalog.BaseType Chisel => Program.Items.Find(CurrencyEffects.CartographersChisel)!;
    static ItemCatalog.BaseType Map => Program.Items.Find("Metadata/Items/Maps/MapWorldsSanctuary")!;
    static ItemCatalog.BaseType Helmet => Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!;

    static Item Rare(ItemCatalog.BaseType type) => ItemCatalog.Create(type) with
    {
        Rarity = ItemRarity.Rare,
        NameWords = [111u, 222u],
        ItemLevel = 70,
    };

    [Fact]
    public void A_chisel_adds_map_quality_and_refuses_anything_else()
    {
        byte qualityType = Program.Items.AlternateQualityOf(Chisel)!.Value;
        var map = ItemCatalog.Create(Map);
        var once = CurrencyEffects.Chisel(map, Map, qualityType);
        Assert.NotNull(once);
        Assert.Equal(20, once!.Quality);
        Assert.Equal(20, CurrencyEffects.Chisel(map with { Rarity = ItemRarity.Unique }, Map, qualityType)!.Quality);
        Assert.Equal(qualityType, once.AlternateQualityType);
        Assert.Equal(20, CurrencyEffects.Chisel(map with { Quality = 20, AlternateQualityType = qualityType }, Map, qualityType) is null ? 20 : 0);

        // Its table action is the one the effect dispatcher claims, and a chisel is not a map.
        Assert.Equal("add_map_alt_quality", Program.Items.CurrencyAction(Chisel));
        Assert.Null(CurrencyEffects.Chisel(ItemCatalog.Create(Helmet), Helmet, qualityType));
        Assert.True(CurrencyEffects.TryAction(Program.Items, Rolls, map, Chisel, new Random(1), out var viaTable));
        Assert.NotNull(viaTable);
    }

    [Fact]
    public void Eldritch_orbs_need_an_eldritch_implicit_and_keep_it()
    {
        var armour = Rare(Helmet);
        Assert.False(Rolls.HasEldritchImplicit(armour));
        Assert.Null(CurrencyEffects.EldritchChaos(armour, Rolls, new Random(1)));
        Assert.Null(CurrencyEffects.EldritchExalted(armour, Rolls, new Random(1)));

        var ichor = Program.Items.Find(CurrencyEffects.LesserEldritchIchor)!;
        Assert.NotNull(ichor);
        var marked = Rolls.EldritchIchor(armour, Helmet, 1, new Random(1));
        Assert.NotNull(marked);
        Assert.True(Rolls.HasEldritchImplicit(marked!));
        var implicitMod = marked.Mods.Where(m => m.Section == ModSection.Implicit).ToList();
        Assert.NotEmpty(implicitMod);

        var chaos = CurrencyEffects.EldritchChaos(marked, Rolls, new Random(1));
        Assert.NotNull(chaos);
        Assert.Equal(implicitMod, chaos!.Mods.Where(m => m.Section == ModSection.Implicit).ToList());
        Assert.Equal(ItemRarity.Rare, chaos.Rarity);
    }
    static string? EldritchIdOrNull(Item item, int influence) =>
        item.Mods.Where(m => m.Section == ModSection.Implicit)
            .Select(m => Program.Items.Mods.ByHash(m.Hash))
            .FirstOrDefault(m => m?.GenerationType == influence)?.Id;

    [Fact]
    public void Orb_of_conflict_raises_one_eldritch_implicit_and_lowers_the_other()
    {
        var armour = Rare(Helmet);
        var eater2 = Rolls.EldritchIchor(armour, Helmet, 2, new Random(1));
        Assert.NotNull(eater2);
        var pair22 = Rolls.EldritchEmber(eater2!, Helmet, 2, new Random(1));
        Assert.NotNull(pair22);
        Assert.True(Rolls.HasEldritchImplicit(pair22!));
        Assert.Equal(2, pair22!.Mods.Count(m => m.Section == ModSection.Implicit));

        // One implicit alone is refused, and the table action is the one the dispatcher claims.
        Assert.Null(CurrencyEffects.Conflict(eater2!, Rolls, new Random(1)));
        var conflict = Program.Items.Find(CurrencyEffects.OrbOfConflict)!;
        Assert.Equal("conflict_orb", Program.Items.CurrencyAction(conflict));
        Assert.True(CurrencyEffects.TryAction(Program.Items, Rolls, pair22, conflict, new Random(1), out var viaTable));
        Assert.NotNull(viaTable);

        // Tier 2/2 pair: one rises to tier 3, the other lowers to tier 1, and both implicits remain.
        var from22 = CurrencyEffects.Conflict(pair22, Rolls, new Random(1));
        Assert.NotNull(from22);
        Assert.Equal(ItemRarity.Rare, from22!.Rarity);
        Assert.Equal(2, from22.Mods.Count(m => m.Section == ModSection.Implicit));
        string eater22Before = EldritchIdOrNull(pair22, ItemRolls.EaterImplicit)!;
        string exarch22Before = EldritchIdOrNull(pair22, ItemRolls.ExarchImplicit)!;
        string eater22After = EldritchIdOrNull(from22, ItemRolls.EaterImplicit)!;
        string exarch22After = EldritchIdOrNull(from22, ItemRolls.ExarchImplicit)!;
        Assert.Equal(eater22Before[..^1], eater22After[..^1]);
        Assert.Equal(exarch22Before[..^1], exarch22After[..^1]);
        Assert.True((eater22After.EndsWith("3") && exarch22After.EndsWith("1"))
            || (eater22After.EndsWith("1") && exarch22After.EndsWith("3")));

        // Tier 1/1 pair: one rises to tier 2 and the lowered tier 1 (Lesser) implicit is removed.
        var eater1 = Rolls.EldritchIchor(armour, Helmet, 1, new Random(1));
        Assert.NotNull(eater1);
        var pair11 = Rolls.EldritchEmber(eater1!, Helmet, 1, new Random(1));
        Assert.NotNull(pair11);
        var from11 = CurrencyEffects.Conflict(pair11!, Rolls, new Random(1));
        Assert.NotNull(from11);
        Assert.Equal(1, from11!.Mods.Count(m => m.Section == ModSection.Implicit));
        string? eater11After = EldritchIdOrNull(from11, ItemRolls.EaterImplicit);
        string? exarch11After = EldritchIdOrNull(from11, ItemRolls.ExarchImplicit);
        Assert.True(
            (eater11After is not null && exarch11After is null && eater11After == EldritchIdOrNull(pair11!, ItemRolls.EaterImplicit)![..^1] + "2")
            || (exarch11After is not null && eater11After is null && exarch11After == EldritchIdOrNull(pair11!, ItemRolls.ExarchImplicit)![..^1] + "2"));

        // Tier 6/6 pair has no valid move and is refused.
        var tier6Eater = Program.Items.Mods.All.First(m => m.GenerationType == ItemRolls.EaterImplicit && m.Id.EndsWith("EldritchImplicit6"));
        var tier6Exarch = Program.Items.Mods.All.First(m => m.GenerationType == ItemRolls.ExarchImplicit && m.Id.EndsWith("EldritchImplicit6"));
        var pair66 = armour with
        {
            Mods = [tier6Eater.Roll(ModSection.Implicit, new Random(1)), tier6Exarch.Roll(ModSection.Implicit, new Random(1))],
        };
        Assert.Null(CurrencyEffects.Conflict(pair66, Rolls, new Random(1)));
    }

    [Fact]
    public void Utility_flask_mana_cost_sign_flips_and_cospris_triggers_by_swinging_hand()
    {
        // Utility flask "reduced Mana Cost of Skills during Effect": negative local stat flips to positive BaseManaCostMinusPct and scales.
        Assert.Equal(UtilityFlasks.Support.Modelled, UtilityFlasks.SupportOf(Stat.BaseManaCostMinusPct));
        var flaskSources = UtilityFlasks.DuringEffectSources(
            new Dictionary<uint, int> { [Stat.LocalFlaskSkillManaCostPlusPctDuringFlaskEffect] = -25 }, 1.5);
        Assert.Contains(flaskSources, s => s is { Stat: Stat.BaseManaCostMinusPct, Value: 37 });

        // Cospri's Malice: non-Cospri weapons return null without scanning; swinging hand(s) gate Main, Off, and Both.
        string path = Path.Combine(Path.GetTempPath(), $"cospri-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = new Player(new(1, "cospri", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Cospri" + Guid.NewGuid().ToString("N") },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        using var waived = TestRequirements.Waive(player);
        player.Clock.Start();
        try
        {
            var cospriMod = Program.Items.Mods.ById("CastSocketedColdSkillsOnCriticalStrikeUnique__1")!.Roll(ModSection.Explicit, new Random(1));
            Item MakeSword(string gemName, bool cospri) => ItemCatalog.Create(Program.Items.Find("Jewelled Foil")!) with
            {
                Mods = cospri ? [cospriMod] : [],
                Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gemName)!))],
                Links = [1],
            };
            var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            {
                Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemCleave")!))],
                Links = [1],
            };
            Game.ItemsOf(player).Add(PlayerInventories.BodyArmour, 0, 0, robe);
            var ordinary = Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, MakeSword("SkillGemFrostBolt", cospri: false));
            var cleave = Game.GemSkillEffects(player).Single(s => s.Name == "Cleave");

            // Non-Cospri weapon does not trigger on any hand.
            Assert.Null(Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Main));
            Assert.Null(Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Both));

            // Cospri in off hand only: Main returns null, Off and Both capture only the off-hand sword.
            var offCospri = Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0, MakeSword("SkillGemFreezingPulse", cospri: true));
            Assert.Null(Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Main));
            var offOnly = Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Off);
            Assert.NotNull(offOnly);
            Assert.Single(offOnly!);
            Assert.Equal(offCospri.Id, offOnly![0].Spells[0].Gem.Host);
            Assert.Contains("melee critical strike", Game.TriggerOnlyReason(offOnly[0].Spells[0].Gem) ?? "");

            // Dual-wielding two Cospri's Malice: Main captures main, Off captures off, Both captures and releases both.
            Game.ItemsOf(player).Remove(PlayerInventories.Weapon1, ordinary.Id);
            var mainCospri = Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, MakeSword("SkillGemFrostBolt", cospri: true));
            var mainOnly = Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Main);
            Assert.NotNull(mainOnly);
            Assert.Single(mainOnly!);
            Assert.Equal(mainCospri.Id, mainOnly![0].Spells[0].Gem.Host);

            var bothHands = Game.CaptureItemCriticalTriggerSource(player, cleave.Id, AttackHand.Both);
            Assert.NotNull(bothHands);
            Assert.Equal(2, bothHands!.Length);
            Assert.Equal(mainCospri.Id, bothHands[0].Spells[0].Gem.Host);
            Assert.Equal(offCospri.Id, bothHands[1].Spells[0].Gem.Host);

            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var writer = new System.Net.Sockets.TcpClient();
            writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            player.Connection.Session = new("cospri-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false));
            var session = new Game.ProjectileSession(player);
            try
            {
                Assert.False(Game.ReleaseItemCriticalTrigger(player, session, bothHands, critical: false, hostile: true, (20, 0), 10));
                Assert.False(Game.ReleaseItemCriticalTrigger(player, session, bothHands, critical: true, hostile: false, (20, 0), 10));
                Assert.True(Game.ReleaseItemCriticalTrigger(player, session, bothHands, critical: true, hostile: true, (20, 0), 10));
                session.AdvanceTo(0);
                var releasedSkills = session.Runtime.Projectiles.Select(p => ((ProjectileOffense)p.Use.Payload!).SourceSkill).ToHashSet();
                Assert.Contains(bothHands[0].Spells[0].Skill, releasedSkills);
                Assert.Contains(bothHands[1].Spells[0].Skill, releasedSkills);
            }
            finally { session.Invalidate(); player.Connection.WorldOutput.Close(); }
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
