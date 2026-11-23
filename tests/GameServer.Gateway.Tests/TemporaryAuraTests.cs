using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class TemporaryAuraTests
{
    [Fact]
    public void Item_blessing_pays_a_cost_instead_of_reserving_and_ends_with_its_source()
    {
        using var f = new Fixture();
        var c = f.Conn;
        c.Pools = c.Pools with { Mana = 2000 }; c.PlayerMana = 2000;
        var blessing = Program.Items.Mods.ById("SupportedByBlessingSupportUnique__1")!;
        var boots = ItemCatalog.Create(Program.Items.Find("Iron Greaves")!) with
        {
            Mods = [new(ModSection.Explicit, blessing.Hash, [25])],
            Sockets = [new(SocketColour.White, Gem("Wrath")), new(SocketColour.White, Gem("More Duration"))], Links = [2],
        };
        var host = Game.ItemsOf(c).Add(PlayerInventories.Boots, 0, 0, boots);
        var skill = Game.GemSkillEffects(c).Single();
        var own = Game.SupportedSkill(c, skill.Id);
        Assert.True(Game.IsBlessing(own));
        Assert.Null(Game.ToggledReservation(c, skill.Id));
        uint mana = c.PlayerMana;
        int cost = Game.SupportedStats(c, skill.Id)[Program.Stats.Key("mana_cost")!.Value];
        Assert.True(cost > mana / 2); // The original 50% plus the support's flat cost and multipliers.
        f.Set("base_mana_reservation_efficiency_+%", 300);
        Assert.Equal(cost, Game.SupportedStats(c, skill.Id)[Program.Stats.Key("mana_cost")!.Value]);
        Assert.True(Game.CastMs(c, skill.Effect, skill.Id) >= 500);
        Assert.True(Game.PayMana(c, skill.Id, out uint paid, out uint life));
        Assert.Equal((uint)cost, paid); Assert.Equal(0u, life); Assert.Equal(mana - paid, c.PlayerMana);
        f.Set("keystone_eldritch_battery", 1); c.PlayerMana = 1;
        c.EnergyShield.SetMaximum(4000, refill: true);
        Assert.True(Game.PayMana(c, skill.Id, out paid, out life));
        Assert.Equal(0u, paid); Assert.Equal(1u, c.PlayerMana);
        Assert.Equal(4000u - (uint)cost, c.EnergyShield.Current);
        var aura = Game.StartTemporaryAuraLocked(c, skill.Id, "Wrath", AuraCatalog.BuffRow("Wrath"), 10, true);
        Assert.Equal(0, AuraCatalog.RequiredMana(c.Pools, c.Auras.Values));
        Assert.True(Game.CharacterPlayerStats(c).GetValueOrDefault(Program.Stats.Key("attack_minimum_added_lightning_damage")!.Value) > 0);
        Game.StartTemporaryAuraLocked(c, skill.Id, "Wrath", aura.BuffRow, 10, true);
        Assert.Single(c.Auras);
        Game.ItemsOf(c).Remove(PlayerInventories.Boots, host.Id);
        Game.ExpireTemporaryAuras(c);
        Assert.Empty(c.Auras);
        Assert.Equal(0, Game.CharacterPlayerStats(c).GetValueOrDefault(Program.Stats.Key("attack_minimum_added_lightning_damage")!.Value));
    }

    [Fact]
    public void Smite_hits_each_normal_target_once_and_grants_a_temporary_buff_only_on_a_hit()
    {
        using var f = new Fixture();
        var c = f.Conn;
        Game.ItemsOf(c).Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
            { Sockets = [new(SocketColour.White, Gem("Smite"))], Links = [1] });
        f.Set("attack_minimum_added_physical_damage", 100);
        f.Set("attack_maximum_added_physical_damage", 100);
        var skill = Game.GemSkillEffects(c).Single();
        void Release() => Game.ReleaseSmite(c, f.Session, skill.Id, skill,
            (100, 100), (108, 100), 41, new SkillCriticalRoll(10000), 1, 0, 1);
        Release(); Assert.Empty(c.Auras); // Empty ground draws lightning but grants no buff.
        var near = new SpawnedMonster("near", new(108, 100, 0), 0, 1, 1000000);
        var far = new SpawnedMonster("far", new(125, 100, 0), 0, 1, 1000000);
        c.Area.Monsters[41] = near; c.Area.Monsters[42] = far;
        var melee = Game.SupportedHitFor(c, skill.Id, false, area: false);
        var lightning = Game.SupportedHitInputsFor(c, skill.Id, area: true)!;
        Assert.True(Game.SupportedHitInputsFor(c, skill.Id, area: false)!.Melee);
        Assert.False(lightning.Melee); Assert.True(lightning.Area);
        Release();
        Assert.InRange(near.MaxLife - near.Life, (uint)Math.Floor(melee.MinTotal), (uint)Math.Ceiling(melee.MaxTotal));
        Assert.True(far.Life < far.MaxLife);
        var aura = Assert.Single(c.Auras).Value;
        Assert.Equal((ushort)1171, aura.BuffRow);
        uint added = Stat.AttackMinimumAddedLightningDamage;
        Assert.True(Game.CharacterPlayerStats(c).GetValueOrDefault(added) > 0);
        var state = Game.TemporaryAuraStates.GetOrCreateValue(c);
        state.Active[skill.Id] = state.Active[skill.Id] with { Until = -1 };
        Game.ExpireTemporaryAuras(c);
        Assert.Empty(c.Auras);
        Assert.Equal(0, Game.CharacterPlayerStats(c).GetValueOrDefault(added));
    }

    static Item Gem(string name)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
    }

    sealed class NoCritical : Random { public override int Next(int maxValue) => maxValue - 1; }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"temporary-aura-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpClient writer = new(), reader;
        readonly SemaphoreSlim gate;
        readonly Dictionary<uint, int> extraStats = [];
        readonly IDisposable requirements, statsHook;
        public readonly Player Conn;
        public readonly Game.ProjectileSession Session;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Fixture()
        {
            Saves.Open(path); Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            writer.Connect((IPEndPoint)listener.LocalEndpoint); reader = listener.AcceptTcpClient(); listener.Stop();
            Conn = new(new(1, "temporary-test", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "Temporary" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
                Connection = { PlayerSent = true, Session = new("temporary-test", Stream, Ciphers) },
                PlayerLife = 2000, PlayerMana = 1000, Instance = new(),
                Pools = new(100, 2000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
                CriticalRandom = new NoCritical(),   // the hits' damage ranges assume no critical strike
            };
            Conn.Walk.Teleport(100, 100, 0);
            requirements = TestRequirements.Waive(Conn);
            statsHook = GameEvents.Stats.Add("Game", sheet =>
            {
                if (ReferenceEquals(sheet.Player, Conn))
                    foreach (var (k, v) in extraStats) sheet.Add(k, v);
            });
            Set("global_always_hit", 1);
            gate = Program.SendGates.GetValue(Ciphers, _ => new(1, 1)); Assert.True(gate.Wait(0));
            Session = new(Conn); Conn.ProjectileRuntime = Session;
        }
        public void Set(string name, int value) => extraStats[Program.Stats.Key(name)!.Value] = value;
        public void Dispose()
        {
            Conn.Closed = true; Session.Invalidate(); gate.Release(); writer.Dispose(); reader.Dispose();
            statsHook.Dispose(); requirements.Dispose();
            PlayerInventories.Forget(Conn.Character.Name!); Program.SetGameLogic(previous!);
            lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime; TestEnvironment.ReleaseSaves(path);
        }
    }
}
