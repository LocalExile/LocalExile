using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

/// <summary>Death's Oath (Death Aura scaling, supports and aura effect), The Sundered Will (chaos aura effect), and
/// hex no-reservation on Blasphemy.</summary>
public class DeathsOathTests
{
    static readonly MethodInfo DeathAuraOwnStatsMethod = typeof(Game).GetMethod(
        "DeathAuraOwnStats", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly MethodInfo DeathAuraRadiusMethod = typeof(Game).GetMethod(
        "DeathAuraRadius", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly MethodInfo DeathAuraDpsMethod = typeof(Game).GetMethod(
        "DeathAuraDps", BindingFlags.NonPublic | BindingFlags.Static)!;

    static Item MakeGem(string name, int level = 20)
    {
        var gemType = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(gemType.Type) with
        {
            GemVariant = gemType.Variant.Index,
            GemExperience = Program.Items.Gem(gemType.Type)!.ExperienceFor(level),
        };
    }

    [Fact]
    public void Death_aura_scales_with_aura_effect_sundered_will_and_supports_not_spell_damage()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string character = "DeathsOath" + Guid.NewGuid().ToString("N");
        var player = runtime.PlayerOf(new(), wire, character);
        IDisposable? requirements = null;
        try
        {
            player.Character.Experience = CharacterStats.ExperienceFor(100);
            player.Pools = new(100, 3000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
            player.PlayerLife = 3000;
            player.PlayerMana = 1000;
            player.LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]));
            requirements = TestRequirements.Waive(player);

            var oath = Assert.IsType<Item>(Game.Uniques.Create("Death's Oath", new Random(1))) with
            {
                Sockets = [new(SocketColour.White, null), new(SocketColour.White, null)],
                Links = [1, 1],
            };
            Game.ItemsOf(player).Add(PlayerInventories.BodyArmour, 0, 0, oath);
            var grant = Assert.Single(Game.AllWornGrantedSkillsOf(player), g => g.EffectId == "DeathAura");

            var plainOwn = (IReadOnlyDictionary<uint, int>)DeathAuraOwnStatsMethod.Invoke(null, [player, grant])!;
            var emptyStats = new Dictionary<uint, int>();

            double baseDps = (double)DeathAuraDpsMethod.Invoke(null, [emptyStats, plainOwn, false])!;
            Assert.Equal(1600d, baseDps, 6);

            // Spell damage does not apply to Death Aura's DoT (DeathAura lacks spell_damage_modifiers_apply_to_skill_dot).
            var spellOnly = new Dictionary<uint, int> { [Stat.SpellDamagePlusPct] = 100 };
            Assert.Equal(1600d, (double)DeathAuraDpsMethod.Invoke(null, [spellOnly, plainOwn, false])!, 6);

            // Non-curse aura effect and The Sundered Will's chaos aura effect both scale Death Aura (capped at +150%).
            var sunderedStats = new Dictionary<uint, int>
            {
                [Stat.NonCurseAuraEffectPlusPct] = 20,
                [Stat.ChaosDamagePlusPct] = 200,
                [Stat.AdditiveChaosDamageModifiersApplyToChaosAuraEffectAtPctValue] = 30,
            };
            Assert.Equal(1600d * 3.0 * 1.80, (double)DeathAuraDpsMethod.Invoke(null, [sunderedStats, plainOwn, false])!, 4);

            var cappedStats = new Dictionary<uint, int>
            {
                [Stat.ChaosDamagePlusPct] = 1000,
                [Stat.AdditiveChaosDamageModifiersApplyToChaosAuraEffectAtPctValue] = 30,
            };
            Assert.Equal(1600d * 11.0 * 2.50, (double)DeathAuraDpsMethod.Invoke(null, [cappedStats, plainOwn, false])!, 4);

            // Malevolence (Delirium) DoT more multiplier applies to Death Aura.
            var malevolenceStats = new Dictionary<uint, int> { [Stat.DeliriumAuraDamageOverTimePlusPctFinal] = 20 };
            Assert.Equal(1600d * 1.20, (double)DeathAuraDpsMethod.Invoke(null, [malevolenceStats, plainOwn, false])!, 4);

            // Unlinked support gems socketed in Death's Oath support Death Aura (damage and area).
            int baseRadius = (int)DeathAuraRadiusMethod.Invoke(null, [player, grant, plainOwn, emptyStats])!;
            Assert.Equal(30, baseRadius);

            var supportedOath = oath with
            {
                Sockets =
                [
                    new(SocketColour.White, MakeGem("Void Manipulation Support", 20)),
                    new(SocketColour.White, MakeGem("Concentrated Effect Support", 20)),
                ],
                Links = [1, 1],
            };
            Game.ItemsOf(player).Remove(PlayerInventories.BodyArmour, grant.Host);
            Game.ItemsOf(player).Put(PlayerInventories.BodyArmour, new(grant.Host, 0, 0, supportedOath));
            var supportedOwn = (IReadOnlyDictionary<uint, int>)DeathAuraOwnStatsMethod.Invoke(null, [player, grant])!;
            double supportedDps = (double)DeathAuraDpsMethod.Invoke(null, [emptyStats, supportedOwn, false])!;
            Assert.Equal(1600d * 1.34 * 1.39, supportedDps, 4);
            int supportedRadius = (int)DeathAuraRadiusMethod.Invoke(null, [player, grant, supportedOwn, emptyStats])!;
            Assert.True(supportedRadius < baseRadius, $"Expected Concentrated Effect to reduce radius below {baseRadius}, got {supportedRadius}");
        }
        finally
        {
            player.PlayerLife = 0;
            requirements?.Dispose();
            PlayerInventories.Forget(character);
        }
    }

    [Fact]
    public void Sundered_will_scales_chaos_auras_not_heralds_and_hex_no_reservation_frees_blasphemy()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string character = "SunderedWill" + Guid.NewGuid().ToString("N");
        var player = runtime.PlayerOf(new(), wire, character);
        IDisposable? requirements = null, statHook = null;
        int despairFree = 0;
        try
        {
            player.Character.Experience = CharacterStats.ExperienceFor(100);
            player.Pools = new(100, 3000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
            player.PlayerLife = 3000;
            player.PlayerMana = 1000;
            player.LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]));
            requirements = TestRequirements.Waive(player);
            statHook = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, player) || despairFree == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                copy[Stat.DespairNoReservation] = 1;
                return copy;
            });

            var skills = Program.Skills;
            var sources = new Dictionary<uint, int>
            {
                [Stat.AdditiveChaosDamageModifiersApplyToChaosAuraEffectAtPctValue] = 30,
                [Stat.ChaosDamagePlusPct] = 200,
            };
            AuraCatalog.Aura Stamp(string effectId, bool herald = false) =>
                Game.WithCasterAuraStamps(player,
                    new AuraCatalog.Aura(1, skills.EffectRow(effectId), effectId, effectId, 1, AuraCatalog.BuffRow(effectId),
                        0, 3500, Level: 20, IsHerald: herald),
                    sources);

            Assert.Equal(60, Stamp("Envy").PerAuraEffectPct);
            Assert.Equal(60, Stamp("Despair").PerAuraEffectPct);
            Assert.Equal(0, Stamp("Haste").PerAuraEffectPct);
            Assert.Equal(0, Stamp("HeraldOfAgony", herald: true).PerAuraEffectPct);

            // Cap at +150%.
            sources[Stat.ChaosDamagePlusPct] = 1000;
            Assert.Equal(150, Stamp("Envy").PerAuraEffectPct);
            Assert.Equal(150, Stamp("Despair").PerAuraEffectPct);

            // Blasphemy + Despair: reserves mana normally, 0 with despair_no_reservation (Impresence).
            var helm = ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
            {
                Sockets =
                [
                    new(SocketColour.White, MakeGem("Despair", 20)),
                    new(SocketColour.White, MakeGem("Blasphemy Support", 20)),
                ],
                Links = [2],
            };
            Game.ItemsOf(player).Add(PlayerInventories.Helmet, 0, 0, helm);
            var despairSkill = Assert.Single(Game.GemSkillEffects(player), g => skills.EffectId(g.Effect) == "Despair");
            var paid = Game.SupportedReservationCost(player, despairSkill.Id)!.Value;
            Assert.True(paid.ManaPercent > 0);

            despairFree = 1;
            var free = Game.SupportedReservationCost(player, despairSkill.Id)!.Value;
            Assert.Equal(new SkillCatalog.ReservationCost(0, 0, 0, 0), free);
        }
        finally
        {
            player.PlayerLife = 0;
            statHook?.Dispose();
            requirements?.Dispose();
            PlayerInventories.Forget(character);
        }
    }
}
