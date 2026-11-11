using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AnimatedAdmissionTests
{
    [Fact]
    public void Equipment_admission_keeps_guardians_melee_and_weapon_variants_disjoint()
    {
        var conn = new ClientState(new(1, "admission", 1000, 1000, 100, 100, null, []), 1, 1);
        var gem = new Game.SupportedGem(0, 20, 0, [], new()
            { [Program.Stats.Key("animate_item_maximum_level_requirement")!.Value] = 100 });
        var catalog = new SummonCatalog();
        Item Make(string name) => ItemCatalog.Create(Program.Items.Find(name)!);
        var sword = Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1");
        var bow = Make("Crude Bow"); var wand = Make("Driftwood Wand");
        byte Slot(string effect, Item item) => Game.AnimatedItemSlot(conn, catalog.Find(effect)!, gem, item);
        Assert.Equal(2, Slot("AnimateGuardian", sword));
        Assert.Equal(1, Slot("AnimateGuardian", Make("Plate Vest")));
        Assert.Equal(0, Slot("AnimateGuardian", bow)); Assert.Equal(0, Slot("AnimateGuardian", wand));
        Assert.Equal(2, Slot("AnimateWeapon", sword)); Assert.Equal(0, Slot("AnimateWeapon", bow));
        Assert.Equal(0, Slot("AnimateWeaponAltY", sword));
        Assert.Equal(2, Slot("AnimateWeaponAltY", bow)); Assert.Equal(2, Slot("AnimateWeaponAltY", wand));
        var six = Enumerable.Repeat(new ItemSocket(SocketColour.White, null), 6).ToArray();
        Assert.Equal(0, Slot("AnimateWeapon", sword with { Sockets = six }));
        Assert.Equal(0, Slot("AnimateWeaponAltY", bow with { Sockets = six }));
        Assert.Equal(0, Slot("AnimateWeapon", sword with { Identified = false }));
        Assert.Equal(0, Slot("AnimateGuardian", sword with { Identified = false }));
        var limited = gem with { Stats = new() { [Program.Stats.Key("animate_item_maximum_level_requirement")!.Value] = 1 } };
        Assert.Equal(0, Game.AnimatedItemSlot(conn, catalog.Find("AnimateWeapon")!, limited, Make("Jewelled Foil")));
    }

    [Fact]
    public async Task Rejected_or_private_equipment_stays_on_the_ground_and_valid_equipment_is_taken_once()
    {
        using var f = new Fixture();
        Item Make(string name) => ItemCatalog.Create(Program.Items.Find(name)!);
        var sword = Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1");
        var bow = Make("Crude Bow");
        var six = Enumerable.Repeat(new ItemSocket(SocketColour.White, null), 6).ToArray();
        foreach (var (effect, item) in new[] { ("AnimateGuardian", bow), ("AnimateWeaponAltY", sword),
            ("AnimateWeapon", bow), ("AnimateWeapon", sword with { Sockets = six }),
            ("AnimateWeaponAltY", bow with { Sockets = six }), ("AnimateWeapon", sword with { Identified = false }) })
        {
            var drop = new WorldItemObject(100, 100, item); uint id = f.Conn.Instance.Ground.Add(drop);
            Assert.Equal(0u, await f.Summon(effect, id));
            Assert.Same(drop, f.Conn.Instance.Ground.Get(id)); Assert.Empty(f.Conn.Summons.Fighters);
            f.Conn.Instance.Ground.Take(id);
        }
        var hidden = new WorldItemObject(100, 100, sword);
        uint hiddenId = f.Conn.Instance.Ground.Add(hidden, new object(), shared: false);
        Assert.Equal(0u, await f.Summon("AnimateWeapon", hiddenId));
        Assert.Equal(0u, await f.Summon("AnimateWeapon", 0));
        Assert.Same(hidden, f.Conn.Instance.Ground.Get(hiddenId));
        var valid = new WorldItemObject(100, 100, sword); uint ground = f.Conn.Instance.Ground.Add(valid);
        uint animated = await f.Summon("AnimateWeapon", ground);
        Assert.NotEqual(0u, animated); Assert.Null(f.Conn.Instance.Ground.Get(ground));
        Assert.Equal(0u, await f.Summon("AnimateWeapon", ground));
        Assert.Single(f.Conn.Summons.Fighters);
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string save = Path.Combine(Path.GetTempPath(), $"animated-admission-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly Task drain;
        internal readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        internal NetworkStream Stream => writer.GetStream();
        internal readonly ClientState Conn = new(new(1, "animate", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Animate" + Guid.NewGuid().ToString("N"), Instance = new(), PlayerLife = 10000,
            PlayerMana = 10000, PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("animate-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture()
        {
            Saves.Open(save); Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Conn.Clock.Start(); Conn.Summons.Restored = Conn.Golems.Restored = true;
            foreach (string s in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                Conn.PlayerStats[Program.Stats.Key(s)!.Value] = 1000;
            Item Gem(string name)
            {
                var type = Program.Items.FindGem(name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
            }
            var host = ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!) with
            {
                Sockets = new[] { "Animate Weapon", "Animate Guardian", "Animate Weapon of Ranged Arms" }
                    .Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(), Links = [1, 1, 1]
            };
            Game.ItemsOf(Conn).Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
            listener.Start(); var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
            drain = Task.Run(async () => { try { var bytes = new byte[65536]; while (await reader.GetStream().ReadAsync(bytes) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
        }
        internal Task<uint> Summon(string effect, uint target)
        {
            ushort source = Game.GemSkillEffects(Conn).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            return Game.SummonFighter("test", Stream, Ciphers, Conn, source, Game.SummonData.Find(effect)!, (100, 100), target);
        }
        public void Dispose()
        {
            Conn.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Conn.Character!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
