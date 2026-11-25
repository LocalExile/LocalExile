using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class VenomGyreCatalogTests
{
    // 3.29 Projectiles.dat has no Metadata/Projectiles/VenomGyre row; the gem's row is ThrownWeapon, the
    // same row Spectral Throw uses (GrantedEffects VenomGyre row 10850, VaalVenomGyre row 12333;
    // Projectiles ThrownWeapon row 94, speed 1000). Without the fallback every press was refused as
    // having no projectile.
    [Fact]
    public void VenomGyreAndVaalVenomGyreResolveToThrownWeapon()
    {
        var skills = new SkillCatalog();
        int venomGyre = skills.EffectRow("VenomGyre");
        Assert.True(venomGyre >= 0);
        Assert.Equal(("Metadata/Projectiles/ThrownWeapon", 1000), skills.ProjectileFor(venomGyre));
        int vaalVenomGyre = skills.EffectRow("VaalVenomGyre");
        Assert.True(vaalVenomGyre >= 0);
        Assert.Equal(("Metadata/Projectiles/ThrownWeapon", 1000), skills.ProjectileFor(vaalVenomGyre));
    }

    [Fact]
    public void Per_type_critical_strike_families_follow_skill_tags_and_count_elemental_once()
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        var statOverrides = new Dictionary<uint, int>();
        IDisposable? statHook = null;
        IDisposable? reqHook = null;
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            var player = new Player(new(1, "crit-tags", 100, 100, 10, 10, null, []), 1, 1)
            {
                PlayerLife = 1000,
                Pools = new(60, 1000, 1000, int.MaxValue, 0, Strength: 500, Dexterity: 500, Intelligence: 500),
            };
            statHook = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, player) || statOverrides.Count == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                foreach (var (k, v) in statOverrides) copy[k] = v;
                return copy;
            });
            reqHook = GameEvents.RequirementExemptions.Add("Game",
                p => ReferenceEquals(p, player) ? [.. Game.ItemsOf(p).EquipmentSnapshot().Select(e => e.Item.Id)] : []);

            ItemSocket Gem(string name)
            {
                var g = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(g.Type) with { GemVariant = g.Variant.Index });
            }

            // Elemental Hit (Fire+Cold+Lightning) in Weapon1; Chaos (Bane/Essence Drain) + Totem (Fireball+Spell Totem) + Mine (Fireball+Blastchain Mine) in BodyArmour.
            var items = Game.ItemsOf(player);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            {
                Sockets = [Gem("Elemental Hit"), Gem("Essence Drain")],
                Links = [1, 1],
            });
            items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            {
                Sockets = [Gem("Fireball"), Gem("Spell Totem Support"), Gem("Ice Spear"), Gem("Blastchain Mine Support")],
                Links = [2, 2],
            });

            var effects = Game.GemSkillEffects(player);
            var elemHit = effects.First(s => Game.SkillsFor(player).EffectId(s.Effect) == "ElementalHit");
            var chaos = effects.First(s => Game.SkillsFor(player).EffectId(s.Effect) == "EssenceDrain");
            var totem = effects.First(s => Game.SkillsFor(player).EffectId(s.Effect) == "Fireball");
            var mine = effects.First(s => Game.SkillsFor(player).EffectId(s.Effect) == "IceSpear");

            int baseElemMult = Game.SupportedCriticalMultiplier(player, elemHit.Id);
            int baseChaosMult = Game.SupportedCriticalMultiplier(player, chaos.Id);
            int baseTotemCrit = Game.SupportedCriticalChance(player, totem.Id);
            int baseMineCrit = Game.SupportedCriticalChance(player, mine.Id);

            statOverrides[Stat.FireCriticalStrikeMultiplierPlus] = 10;
            statOverrides[Stat.ColdCriticalStrikeMultiplierPlus] = 20;
            statOverrides[Stat.LightningCriticalStrikeMultiplierPlus] = 30;
            statOverrides[Stat.ElementalCriticalStrikeMultiplierPlus] = 40;
            statOverrides[Stat.ChaosCriticalStrikeMultiplierPlus] = 50;
            statOverrides[Stat.TotemCriticalStrikeChancePlusPct] = 100;
            statOverrides[Stat.MineCriticalStrikeChancePlusPct] = 100;

            // Elemental Hit carries Fire+Cold+Lightning: each element adds once and Elemental adds ONCE (+10+20+30+40 = +100).
            Assert.Equal(baseElemMult + 100, Game.SupportedCriticalMultiplier(player, elemHit.Id));
            // Chaos skill takes only Chaos multiplier (+50), never Elemental.
            Assert.Equal(baseChaosMult + 50, Game.SupportedCriticalMultiplier(player, chaos.Id));
            // Totem and Mine skills double their base crit chance from +100% increased totem/mine crit chance.
            Assert.Equal(baseTotemCrit * 2, Game.SupportedCriticalChance(player, totem.Id));
            Assert.Equal(baseMineCrit * 2, Game.SupportedCriticalChance(player, mine.Id));
        }
        finally
        {
            reqHook?.Dispose();
            statHook?.Dispose();
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
