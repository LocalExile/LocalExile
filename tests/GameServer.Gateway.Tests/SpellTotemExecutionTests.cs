using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Core.Projectiles;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;
public sealed class SpellTotemExecutionTests
{
    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
    static ushort Equip(ClientState conn, string active, string support)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with
        {
            Sockets = [new(SocketColour.White, Gem(active)), new(SocketColour.White, Gem(support))], Links = [2],
        });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Native_spell_totem_admits_fireball_and_rejects_unimplemented_payloads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-totem-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SpellTotem");
        try
        {
            ushort skill = Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            var gem = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.TotemVariety(conn, gem.Effect));
            Assert.True(Game.ProjectilePathAvailable("test", conn, skill, gem.Effect));
            skill = Equip(conn, "SkillGemArc", "SupportGemSpellTotem");
            gem = Game.SupportedSkill(conn, skill);
            Assert.False(Game.ProjectilePathAvailable("test", conn, skill, gem.Effect));
            skill = Equip(conn, "SkillGemShockwaveTotem", "SupportGemSpellTotem");
            Assert.False(Game.ProjectilePathAvailable("test", conn, skill, Game.SupportedSkill(conn, skill).Effect));
            skill = Equip(conn, "SkillGemRejuvenationTotem", "SupportGemSpellTotem");
            Assert.NotNull(Game.TotemVariety(conn, Game.SupportedSkill(conn, skill).Effect));
            Assert.True(Game.ProjectilePathAvailable("test", conn, skill, Game.SupportedSkill(conn, skill).Effect));
            Assert.Equal(1000u, conn.PlayerMana);
        }
        finally { conn.Closed = true; PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path); }
    }
    [Fact]
    public async Task Totem_release_uses_its_entity_position_supported_damage_and_source_lifetime()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-totem-release-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SpellTotemRelease");
        conn.Clock.Stop();
        using var wire = new Wire();
        var session = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        try
        {
            ushort skill = Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            var gem = Game.GemSkillEffects(conn).Single();
            var spawned = await Game.CreateTotem("test", wire.Stream, wire.Ciphers, conn, skill, gem.Effect, gem.Level,
                Game.TotemVariety(conn, gem.Effect)!, (150, 80));
            Assert.NotNull(spawned);
            var placed = Assert.Single(conn.Totems.All);
            Assert.Equal((150, 80), placed.At); Assert.True(placed.MaxLife > 1);
            Assert.Equal(8, placed.Until - conn.Clock.Elapsed.TotalSeconds, 3);
            Assert.True(Game.SpellTotemCurrent(conn, placed));
            var totem = new TotemState.Totem(700, skill, (100, 20), 100, 8, null)
                { Effect = gem.Effect, SourceGem = gem.Gem, SourceHost = Game.SupportedSkill(conn, skill).Host, SourceItem = Game.SupportedSkill(conn, skill).SourceGem };
            conn.PlayerStats[StatKeys.SpellSkillsDealNoDamage] = 1;
            var use = Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem);
            Assert.Equal(700u, use.Emitter.Id); Assert.Equal(700u, use.Owner.Id);
            Assert.Equal(100, use.Origin.X); Assert.Equal(20, use.Origin.Y);
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.NotNull(shot.ActualCaster);
            Assert.True(new SkillDamageContext(SkillHitKind.Spell, shot.ActualCaster!).Hit(shot.Hit).MaxTotal > 0);
            Assert.Equal(Game.SupportedProjectileHit(conn, skill, false).MaxTotal * .45, shot.Hit.MaxTotal, 6);
            var ownerShot = Assert.IsType<ProjectileOffense>(Game.CaptureGemProjectile(conn, session, skill, gem,
                (130, 20), new(0)).Payload);
            Assert.Equal(ownerShot.Ignite.Max * .45, shot.Ignite.Max, 6);
            Assert.Equal(ownerShot.Ignite.CriticalMax * .45, shot.Ignite.CriticalMax, 6);
            Assert.Equal(1.25, Game.TotemCastSeconds(conn, skill)!.Value, 3);
            var proxy = Game.TotemProxy(conn, skill);
            Assert.NotNull(proxy);
            Assert.Equal(-40, proxy!.Stats!.Single(s => s.Key == Program.Stats.Key("cast_speed_+%")).Value);
            Assert.True(Game.SpellTotemCurrent(conn, totem));
            Assert.Equal(1000u, conn.PlayerMana); Assert.Equal(1000u, conn.PlayerLife);
            conn.Totems.Place(totem, 1, 0);
            var target = new SpawnedMonster("totem-target", new(125, 20, 0), 0, 0, 10000);
            conn.Monsters[900] = target;
            conn.Clock.Start();
            var actions = new List<byte>();
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.NotEmpty(actions); Assert.True(totem.NextCast > 1);
            conn.Clock.Stop(); actions.Clear();
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.Empty(actions); conn.Clock.Start();
            session.AdvanceTo(ProjectileRuntime.Time(3));
            Assert.True(target.Life < 10000, $"life={target.Life} closed={conn.Closed}");
            Assert.Equal(1000u, conn.PlayerMana);
            totem.NextCast = 0; target.Dead = true;
            conn.Monsters[901] = new("next-target", new(100, 45, 0), 0, 0, 10000);
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.NotEmpty(actions);

            var prohibited = Program.Stats.Key("base_deal_no_fire_damage")!.Value;
            conn.PlayerStats[prohibited] = 1;
            use = Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem);
            Assert.Equal(0, Assert.IsType<ProjectileOffense>(use.Payload).Hit.MaxTotal);
            conn.PlayerStats.Remove(prohibited);
            conn.LeavingArea = true; Assert.False(Game.SpellTotemCurrent(conn, totem)); conn.LeavingArea = false;
            conn.PlayerLife = 0; Assert.False(Game.SpellTotemCurrent(conn, totem)); conn.PlayerLife = 1000;

            // Replace the active gem in the same host/socket: wire id and TypeHash stay identical.
            var items = Game.ItemsOf(conn);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Sockets = [new(SocketColour.White, Gem("SkillGemFireball")), item.Sockets[1]],
            });
            var swapped = Game.GemSkillEffects(conn).Single();
            Assert.Equal(skill, swapped.Id); Assert.Equal(gem.Gem, swapped.Gem);
            Assert.Equal(totem.SourceHost, Game.SupportedSkill(conn, skill).Host);
            Assert.NotSame(totem.SourceItem, Game.SupportedSkill(conn, skill).SourceGem);
            Assert.False(Game.SpellTotemCurrent(conn, totem));
            var next = conn.Monsters[901];
            uint before = next.Life;
            session.AdvanceTo(ProjectileRuntime.Time(6));
            Assert.Equal(before, next.Life); // The pending old-source release was cancelled.
            Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            Assert.False(Game.SpellTotemCurrent(conn, totem));
            var state = new TotemState(); state.Place(totem, 1, 0);
            state.Place(new(701, skill, (90, 20), 50, 8, null), 1, 0);
            Assert.False(totem.Alive(0)); Assert.Single(state.Living(0));
            Assert.Equal((50, 0u), TotemState.Redirect(100, 100, 50));
            Assert.Single(state.Expire(8)); Assert.Empty(state.Living(8));
            var renewed = Game.SupportedSkill(conn, skill);
            var live = new TotemState.Totem(702, skill, (100, 20), 100, 100, null)
                { Effect = renewed.Effect, SourceGem = Game.GemSkillEffects(conn).Single().Gem,
                  SourceHost = renewed.Host, SourceItem = renewed.SourceGem };
            Assert.True(Game.SpellTotemCurrent(conn, live));
            lifetime.Stop(ex => throw ex);
            Assert.False(Game.SpellTotemCurrent(conn, live)); Assert.False(session.Active);
        }
        finally
        {
            session.Invalidate(); conn.Closed = true;
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}



