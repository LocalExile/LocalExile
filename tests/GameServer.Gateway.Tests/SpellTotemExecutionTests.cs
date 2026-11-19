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
    internal static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            LockstepClient = false, Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        return conn;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)
        ?? Program.Items.FindGem(name)!.Value.Type);
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
    public async Task Native_spell_totem_admits_projectiles_and_raging_spirits_and_rejects_unimplemented_payloads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-totem-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SpellTotem");
        try
        {
            await VerifySummonTotems();
            ushort skill = Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            var gem = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.TotemVariety(conn, gem.Effect));
            Assert.True(Game.ProjectilePathAvailable(conn, skill, gem.Effect));
            foreach (string active in new[] { "SkillGemFreezingPulse", "SkillGemSpark", "SkillGemIceSpear", "SkillGemFrostBolt" })
            {
                skill = Equip(conn, active, "SupportGemSpellTotem");
                gem = Game.SupportedSkill(conn, skill);
                Assert.NotNull(Game.TotemVariety(conn, gem.Effect));
                Assert.True(Game.ProjectilePathAvailable(conn, skill, gem.Effect));
            }
            skill = Equip(conn, "SkillGemArc", "SupportGemSpellTotem");
            gem = Game.SupportedSkill(conn, skill);
            Assert.True(Game.ProjectilePathAvailable(conn, skill, gem.Effect));
            skill = Equip(conn, "SkillGemShockwaveTotem", "SupportGemSpellTotem");
            Assert.True(Game.ProjectilePathAvailable(conn, skill, Game.SupportedSkill(conn, skill).Effect));
            skill = Equip(conn, "SkillGemRejuvenationTotem", "SupportGemSpellTotem");
            Assert.NotNull(Game.TotemVariety(conn, Game.SupportedSkill(conn, skill).Effect));
            Assert.True(Game.ProjectilePathAvailable(conn, skill, Game.SupportedSkill(conn, skill).Effect));
            Assert.Equal(1000u, conn.PlayerMana);
            skill = Equip(conn, "SkillGemSummonRagingSpirit", "SupportGemSpellTotem");
            var spirit = Game.GemSkillEffects(conn).Single();
            var spiritSource = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.TotemVariety(conn, spirit.Effect));
            Assert.True(Game.TotemPathAvailable(conn, skill, spirit.Effect));
            using (var spiritWire = new Wire())
            {
                var spiritSession = Game.ProjectilesFor(conn);
                var spiritTotem = new TotemState.Totem(778, skill, (100, 100), 100, 100, null)
                { Effect = spirit.Effect, SourceGem = spirit.Gem, SourceHost = spiritSource.Host, SourceItem = spiritSource.SourceGem };
                conn.Totems.Place(spiritTotem, 1, 0);
                conn.Monsters[901] = new("spirit-target", new(110, 100, 0), 0, 0, 10000) { Hostile = true };
                Game.TickSpellTotemsLocked(conn, new List<byte>());
                spiritSession.AdvanceTo(ProjectileRuntime.Time(spiritTotem.NextCast));
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                spiritSession.Enqueue(() => { released.SetResult(); return Task.CompletedTask; });
                await released.Task;
                Assert.Single(conn.Summons.Fighters);
                Assert.Equal(1000u, conn.PlayerMana);
                spiritSession.Invalidate();
            }
            foreach (var t in conn.Totems.All) t.DiedAt = 0;
            conn.Monsters.Clear();
            skill = Equip(conn, "SkillGemSpark", "SupportGemSpellTotem");
            conn.Clock.Start();
            using var wire = new Wire();
            var session = Game.ProjectilesFor(conn);
            try
            {
                var spark = Game.GemSkillEffects(conn).Single();
                var source = Game.SupportedSkill(conn, skill);
                var totem = new TotemState.Totem(777, skill, (100, 20), 100, 100, null)
                { Effect = spark.Effect, SourceGem = spark.Gem, SourceHost = source.Host, SourceItem = source.SourceGem };
                conn.Totems.Place(totem, 1, 0);
                conn.Monsters[900] = new("spark-target", new(120, 20, 0), 0, 0, 10000);
                var actions = new List<byte>();
                Game.TickSpellTotemsLocked(conn, actions);
                // Inspect the release itself. By two seconds a random Spark can already have hit or expired.
                Assert.True(totem.NextCast > 0);
                session.AdvanceTo(ProjectileRuntime.Time(totem.NextCast));
                var emitted = session.Runtime.Projectiles.Where(p => p.Use.Emitter.Id == 777).ToArray();
                Assert.NotEmpty(emitted);
                Assert.All(emitted, p => { Assert.NotNull(p.Profile.Legs); Assert.Equal(150, p.Profile.LegLeash);
                    var shot = Assert.IsType<ProjectileOffense>(p.Use.Payload);
                    Assert.False(shot.ClientPredicted);
                    Assert.Equal(Game.SkillsFor(conn).Clips(p.Profile.Id)!.Flight!.Index, shot.Presentation!.FlightClip);
                    Assert.Equal(100, p.Use.Origin.X); Assert.Equal(20, p.Use.Origin.Y); });
            }
            finally { session.Invalidate(); }
        }
        finally { conn.Closed = true; lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path); }
    }

    static async Task VerifySummonTotems()
    {
        foreach (string active in new[] { "Summon Skeletons", "Raise Zombie", "Raise Spectre",
            "Animate Weapon", "Animate Guardian", "Summon Stone Golem", "Summon Holy Relic" })
        {
            var conn = Connection("SummonTotem" + Guid.NewGuid().ToString("N"));
            using var wire = new Wire();
            var session = Game.ProjectilesFor(conn);
            try
            {
                ushort skill = Equip(conn, active, "SupportGemSpellTotem");
                var gem = Game.GemSkillEffects(conn).Single(); var source = Game.SupportedSkill(conn, skill);
                Assert.True(Game.IsTotemSkill(conn, skill));
                Assert.True(Game.TotemPathAvailable(conn, skill, gem.Effect));
                var summon = Game.SummonData.Find(Program.Skills.EffectId(gem.Effect));
                SpawnedMonster? corpse = null;
                if (summon?.Corpse == true)
                    conn.Monsters[901] = corpse = new("corpse", new(120, 100, 0), 0, 0, 1000)
                    { Hostile = true, Life = 0, Dead = true, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!, Level = 20 };
                if (corpse is not null)
                {
                    corpse.Ignites.Add(0, 1, 1000);
                    corpse.Ignites.Clear();
                }
                uint ground = 0;
                if (summon?.Family is "AnimatedWeapon" or "AnimatedGuardian")
                    ground = conn.Instance.Ground.Add(new(120, 100, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!)));
                var totem = new TotemState.Totem(777, skill, (100, 100), 100, 100, null)
                { Effect = gem.Effect, SourceGem = gem.Gem, SourceHost = source.Host, SourceItem = source.SourceGem };
                conn.Totems.Place(totem, 1, 0);
                Game.TickSpellTotemsLocked(conn, new List<byte>());
                Assert.True(totem.NextCast > 0);
                session.AdvanceTo(ProjectileRuntime.Time(totem.NextCast));
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
                await done.Task;
                int expected = active == "Summon Skeletons" ? 2 : 1;
                Assert.Equal(expected, conn.Summons.Fighters.Count + conn.Golems.All.Count);
                Assert.Equal(1000u, conn.PlayerMana);
                if (corpse is not null)
                {
                    Assert.True(corpse.CorpseConsumed);
                    Assert.Equal(0, conn.IgnitedCorpsesConsumed); // The totem consumed it, not the flask owner.
                    skill = Equip(conn, active, "SupportGemMinionDamage");
                    foreach (bool triggered in new[] { false, true })
                    {
                        uint corpseId = triggered ? 903u : 902u;
                        var playerCorpse = new SpawnedMonster("player corpse", new(120, 100, 0), 0, 0, 1000)
                        { Hostile = true, Life = 0, Dead = true, Variety = corpse.Variety, Level = 20 };
                        playerCorpse.Ignites.Add(0, 1, 1000);
                        playerCorpse.Ignites.Clear();
                        conn.Monsters[corpseId] = playerCorpse;
                        Assert.NotEqual(0u, await Game.SummonFighter(conn, skill, summon!, (120, 100), corpseId, triggered: triggered));
                        Assert.True(playerCorpse.CorpseConsumed);
                        Assert.Equal(triggered ? 2 : 1, conn.IgnitedCorpsesConsumed);
                    }
                }
                if (ground != 0) Assert.Null(conn.Instance.Ground.Get(ground));
                if (Program.Skills.CooldownMs(gem.Effect, gem.Level) > 0)
                {
                    ushort sequence = totem.Sequence;
                    totem.NextCast = 0;
                    Game.TickSpellTotemsLocked(conn, new List<byte>());
                    Assert.Equal(sequence, totem.Sequence);
                    Assert.NotNull(totem.Cooldown);
                    Assert.True(totem.Cooldown.RemainingSeconds > 0);
                }
            }
            finally { session.Invalidate(); conn.Closed = true; PlayerInventories.Forget(conn.Character!); }
        }
    }
    internal sealed class Wire : IDisposable
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



