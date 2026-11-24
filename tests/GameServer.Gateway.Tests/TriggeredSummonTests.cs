using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
namespace GameServer.Gateway.Tests;
public sealed class TriggeredSummonTests
{
    static async Task Drain(Game.ProjectileSession session)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
        await done.Task;
    }

    [Fact]
    public async Task Cwdt_releases_paid_summons_and_corpses_without_an_action_or_echo_and_rejects_stale_sources()
    {
        foreach (string name in new[] { "Summon Skeletons", "Summon Raging Spirit", "Raise Zombie", "Summon Stone Golem", "Desecrate" })
        {
            using var f = new Fixture();
            ushort source = f.Equip((name, 20), ("Cast when Damage Taken Support", 20), ("Spell Echo Support", 20)).First();
            var gem = Game.SupportedSkill(f.Player, source);
            Assert.True(Game.TriggerPayloadAvailable(f.Player, gem));
            if (name == "Raise Zombie")
            {
                var variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie")!;
                var at = f.Player.Walk.Position(0);
                f.Player.Area.Monsters[900] = new("corpse", new(at.X+20,at.Y,1),100,8,1000)
                    { Hostile = true, Dead = true, Life = 0, Variety = variety, Level = 70 };
            }
            int threshold = gem.Stats[Stat.CastOnDamageTakenThreshold];
            uint cost = (uint)Game.SupportedStats(f.Player, source)[Stat.ManaCost];
            int action = f.Player.ActionCount;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 1000);
            uint before = f.Player.PlayerMana;
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session);
            Assert.Equal(before - cost, f.Player.PlayerMana);
            await Drain(f.Session);
            Assert.Equal(action, f.Player.ActionCount);
            int expected = name == "Summon Skeletons" ? 3 : name == "Desecrate" ? 0 : 1;
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            if (name == "Raise Zombie") Assert.True(f.Player.Area.Monsters[900].CorpseConsumed);
            if (name == "Desecrate") Assert.Equal(5, f.Player.Summons.Corpses.Count);
            f.Player.PlayerMana = 0;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 2000);
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session); await Drain(f.Session);
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            Assert.Equal(0u, f.Player.PlayerMana);
            f.Player.PlayerMana = before;
            Game.RecordDamageTakenTriggers(f.Player, threshold, 3000);
            f.Equip((name, 20), ("Cast when Damage Taken Support", 20));
            Game.ReleaseDamageTakenTriggers(f.Player, f.Session); await Drain(f.Session);
            Assert.Equal(before, f.Player.PlayerMana);
            Assert.True(expected == f.Player.Summons.Fighters.Count + f.Player.Golems.All.Count, name);
            if (name == "Raise Zombie")
            {
                var zombie = Assert.Single(f.Player.Summons.Fighters).Value;
                uint restored = await Game.SummonFighter(f.Player, source,
                    Game.SummonData.Find("RaiseZombie")!, (100,100), restoredVariety: zombie.Monster.Variety!.Id, life: .5);
                Assert.NotEqual(0u, restored); // Restoring a persistent triggered minion is not a manual cast.
                Assert.InRange(f.Player.Summons.Fighters[restored].Monster.Life / (double)zombie.Monster.MaxLife, .49, .51);
            }
        }
    }

    [Fact]
    public async Task Coc_and_cwc_summon_once_and_a_queued_release_cannot_outlive_its_source()
    {
        foreach (bool channel in new[] { false, true })
        {
            using var f = new Fixture();
            var ids = f.Equip(("Cyclone",20), ("Summon Skeletons",20),
                (channel ? "Cast while Channelling Support" : "Cast On Critical Strike Support",20), ("Spell Echo Support",20));
            var driver = Game.GemSkillEffects(f.Player).Single(s => s.Id == ids[0]);
            var gem = Game.SupportedSkill(f.Player, ids[1]);
            uint cost = (uint)Game.SupportedStats(f.Player, ids[1])[Stat.ManaCost];
            uint mana = f.Player.PlayerMana; int action = f.Player.ActionCount;
            if (channel)
            {
                f.Player.ChannelSkill = driver.Id; f.Player.ChannelGeneration = 1; f.Player.LastChannelUpdateAt = 10000;
                f.Player.PaidChannel = new(driver.Id, driver.Effect, action, 1, Game.CombatLifetime!);
                Game.BeginChannelTriggers(f.Player, f.Session, driver.Id, action, 1, (20,0), 10);
                double interval = Game.SupportedSkill(f.Player, driver.Id).Stats[Stat.CastWhileChannellingTimeMs] / 1000d;
                Game.TickChannelTriggers(f.Player, f.Session, 10 + interval - .001);
                Assert.Equal(mana, f.Player.PlayerMana);
                Game.TickChannelTriggers(f.Player, f.Session, 10 + interval + .001);
            }
            else
            {
                var captured = Assert.IsType<Game.LinkedTriggerSource>(Game.CaptureCriticalTriggerSource(f.Player, driver.Id));
                Assert.False(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,false,true,(20,0),10));
                Assert.False(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,true,false,(20,0),10));
                Assert.Equal(mana, f.Player.PlayerMana);
                Assert.True(Game.ReleaseCriticalTrigger(f.Player,f.Session,captured,true,true,(20,0),10));
            }
            Assert.Equal(mana-cost, f.Player.PlayerMana); await Drain(f.Session);
            Assert.Equal(3, f.Player.Summons.Fighters.Count); Assert.Equal(action, f.Player.ActionCount);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Session.Enqueue(async () => { entered.SetResult(); await resume.Task; }); await entered.Task;
            try
            {
                if (channel) Game.TickChannelTriggers(f.Player, f.Session, 11);
                else Game.ReleaseCriticalTrigger(f.Player,f.Session,Game.CaptureCriticalTriggerSource(f.Player,driver.Id),true,true,(20,0),11);
                f.Equip(("Cyclone",20), ("Summon Skeletons",20),
                    (channel ? "Cast while Channelling Support" : "Cast On Critical Strike Support",20));
            }
            finally { resume.SetResult(); }
            await Drain(f.Session);
            Assert.Equal(3, f.Player.Summons.Fighters.Count); Assert.Equal(action, f.Player.ActionCount);
        }
    }

    [Fact]
    public async Task Desecrate_creates_corpses_with_nonzero_wire_life_then_zeroes_them()
    {
        using var f = new Fixture();
        ushort source = f.Equip(("Desecrate", 20)).First();
        var wire = new ConnectionCiphers(new byte[64], false);
        f.Player.Connection.LockstepClient = false;
        f.Player.Connection.Session = new("test", f.Stream, wire);
        var packets = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (player, bytes) => { if (ReferenceEquals(player.Connection.Session.Ciphers, wire)) packets.Add(bytes.ToArray()); };
        try
        {
            Assert.True(await Game.Desecrate(f.Player, source, (100, 100), () => true, triggered: true));
            Assert.Equal(5, f.Player.Summons.Corpses.Count);
            foreach (uint id in f.Player.Summons.Corpses)
            {
                var corpse = f.Player.Area.Monsters[id];
                var origin = f.Player.Instance.World.MonsterOrigins[id];
                Assert.True(corpse.Dead);
                Assert.Equal(0u, corpse.Life);
                Assert.Equal(1u, origin.Object.Life);
                byte[] expected =
                [
                    .. InstanceEntities.CreateObject(id, origin.Type, origin.Object.Blob()),
                    .. InstanceProtocol.EnterWakeZone(id),
                    .. InstanceProtocol.LifeUpdate(id, 0),
                ];
                Assert.Contains(packets, p => p.SequenceEqual(expected));
            }

            // Guard in SpawnMonster: an owned spawn with damagedLife: 0 still puts life 1 in 0x02bb and zeroes it after.
            var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            uint guarded = await Game.SpawnMonster(f.Player, zombie, 1, MonsterRarity.Normal,
                100, 100, damagedLife: 0, mods: MonsterModSet.None, unrisen: false,
                owned: (_, m) => { m.Dead = true; m.Life = 0; m.NoRewards = true; });
            Assert.NotEqual(0u, guarded);
            var guardedOrigin = f.Player.Instance.World.MonsterOrigins[guarded];
            Assert.Equal(1u, guardedOrigin.Object.Life);
            byte[] guardedExpected =
            [
                .. InstanceEntities.CreateObject(guarded, guardedOrigin.Type, guardedOrigin.Object.Blob()),
                .. InstanceProtocol.EnterWakeZone(guarded),
                .. InstanceProtocol.LifeUpdate(guarded, 0),
            ];
            Assert.Contains(packets, p => p.SequenceEqual(guardedExpected));

            // A lockstep party member first sees these actors after the caster has already created the corpses.
            using var joining = Game.InstallJoining();
            var peer = new Player(f.Player.Layout, 1, 2)
            {
                Instance = f.Player.Instance, PlayerLife = 100,
                Connection = { PlayerSent = true, LockstepClient = true, Session = new("peer", f.Stream, wire) },
            };
            await Program.BeginWorldFrames(peer, .033);
            packets.Clear();
            peer.Area.Defer(_ => Program.Joining?.Invoke(peer));
            peer.Area.Step(1, .033);
            await Program.EndWorldFrame(peer, (.033, .066), peer.Area.Delta.Take());
            foreach (uint id in f.Player.Summons.Corpses)
            {
                var origin = peer.Instance.World.MonsterOrigins[id];
                var create = InstanceEntities.CreateObject(id, origin.Type, origin.Object.Blob(), headerU32: uint.MaxValue);
                Assert.Contains(packets, p => p.SequenceEqual(create));
                Assert.DoesNotContain(packets, p => p.SequenceEqual(InstanceProtocol.LifeUpdate(id, 0)));
            }
            Assert.DoesNotContain(packets, p => p.Length >= 6 && p[0] == 2 && p[1] == 0xbb
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2)) == guarded);
            packets.Clear();
            peer.Area.Step(2, .066);
            await Program.EndWorldFrame(peer, (.066, .099), peer.Area.Delta.Take());
            foreach (uint id in f.Player.Summons.Corpses)
            {
                int wake = packets.FindIndex(p => p.SequenceEqual(InstanceProtocol.EnterWakeZone(id)));
                Assert.True(wake >= 0 && packets.FindIndex(p => p.SequenceEqual(InstanceProtocol.LifeUpdate(id, 0))) > wake);
            }

            // Consumed or expired corpses are not replicated to a subsequent joining party member.
            uint consumed = f.Player.Summons.Corpses.Peek();
            uint expired = f.Player.Summons.Corpses.Skip(1).First();
            peer.Area.Monsters[consumed].CorpseConsumed = true;
            peer.Area.Monsters.Remove(expired);
            peer.Instance.World.MonsterOrigins.TryRemove(expired, out _);
            var latePeer = new Player(f.Player.Layout, 1, 3)
            {
                Instance = f.Player.Instance, PlayerLife = 100,
                Connection = { PlayerSent = true, LockstepClient = true, Session = new("late-peer", f.Stream, wire) },
            };
            await Program.BeginWorldFrames(latePeer, .099);
            packets.Clear();
            latePeer.Area.Defer(_ => Program.Joining?.Invoke(latePeer));
            latePeer.Area.Step(3, .099);
            await Program.EndWorldFrame(latePeer, (.099, .132), latePeer.Area.Delta.Take());
            foreach (uint removed in new[] { consumed, expired })
            {
                Assert.DoesNotContain(packets, p => p.Length >= 6 && p[0] == 2 && p[1] == 0xbb
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2)) == removed);
            }
        }
        finally
        {
            Program.Outgoing = outgoing;
        }
    }

    [Fact]
    public async Task Crafted_trigger_wand_greys_out_skills_and_triggers_desecrate_offering_convocation_round_robin()
    {
        using var f = new Fixture();
        var wandMod = Program.Items.Mods.ById("JunMaster2TriggerSocketedSpellOnSkillUse1")!;
        Item Gem(string name)
        {
            var type = Program.Items.FindGem(name)!.Value;
            return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        }
        var wand = ItemCatalog.Create(Program.Items.Find("Prophecy Wand")!) with
        {
            Mods = [wandMod.Roll(ModSection.Explicit, new Random(1))],
            Sockets = new[] { "Desecrate", "Flesh Offering", "Convocation" }
                .Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(),
            Links = [1, 1, 1],
        };
        uint existingWeaponId = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single().Id;
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, existingWeaponId, _ => wand);
        // Equip an active skill in another slot (boots) to trigger the crafted weapon spells on skill use.
        var boots = ItemCatalog.Create(Program.Items.Find("Iron Greaves")!) with
        {
            Sockets = [new ItemSocket(SocketColour.Blue, Gem("Arc"))],
            Links = [1],
        };
        Game.ItemsOf(f.Player).Put(PlayerInventories.Boots, new(2, 0, 0, boots));

        var skills = Game.PlayerSkills(f.Player);
        var gemMap = Game.GemSkillEffects(f.Player).ToDictionary(g => Program.Skills.EffectId(g.Effect)!, g => g);

        string convocationEffect = gemMap.ContainsKey("Convocation") ? "Convocation" : "ConvocationOld";
        foreach (string triggered in new[] { "Desecrate", "FleshOffering", convocationEffect })
        {
            var entry = gemMap[triggered];
            var gs = skills.Single(s => s.Id == entry.Id);
            var stats = gs.Stats!.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(1, stats[Stat.SpellHasTriggerFromCraftedItemMod]);
            Assert.Equal(1, stats[Stat.SpellUncastableIfTriggerable]);
            Assert.Equal(1, stats[OwnAuraRestriction.SkillDisabled]);
            Assert.NotNull(Game.GemSkillInstanceDisabled(f.Player, entry.Effect, entry.Id));
        }

        // 3 consecutive active skill uses within 1s trigger Desecrate -> Flesh Offering -> Convocation round-robin
        // (each spell keeps its own 8s cooldown instead of a global 8s block).
        f.Player.ProjectileRuntime = f.Session;
        f.Player.PlayerMana = 1000;
        var arc = gemMap["Arc"];
        Game.RecordAcceptedGemUseAt(f.Player, arc.Effect, (120, 100), 100.0);
        await Drain(f.Session);
        Assert.Equal(5, f.Player.Summons.Corpses.Count);

        Game.RecordAcceptedGemUseAt(f.Player, arc.Effect, (120, 100), 100.2);
        await Drain(f.Session);
        Assert.Contains(f.Player.Summons.Offerings, o => o.Effect == "FleshOffering");
        Assert.Contains(f.Player.Area.Monsters.Values, m => m.CorpseConsumed);

        uint manaBeforeConvocation = f.Player.PlayerMana;
        Game.RecordAcceptedGemUseAt(f.Player, arc.Effect, (120, 100), 100.4);
        await Drain(f.Session);
        Assert.True(f.Player.PlayerMana < manaBeforeConvocation, "Convocation should trigger on the 3rd use within 1s");

        // A 4th use while all three are on their individual 8s cooldowns triggers nothing.
        uint manaAfterAllThree = f.Player.PlayerMana;
        Game.RecordAcceptedGemUseAt(f.Player, arc.Effect, (120, 100), 100.6);
        await Drain(f.Session);
        Assert.Equal(manaAfterAllThree, f.Player.PlayerMana);
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
        internal ModuleLifetime Lifetime => lifetime;
        internal readonly Game.ProjectileSession Session;
        internal readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        internal NetworkStream Stream => writer.GetStream();
        internal readonly Player Player = new(new(1, "animate", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Animate" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            PlayerLife = 10000,
            PlayerMana = 10000,
            Connection = { PlayerSent = true },
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("animate-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        internal Fixture(IPlayerUpdateSchedule? projectileSchedule = null)
        {
            Saves.Open(save); Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Player.Clock.Start(); Player.Summons.Restored = Player.Golems.Restored = true;
            TestRequirements.Waive(Player);
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
            Game.ItemsOf(Player).Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
            listener.Start(); var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
            Player.Connection.Session = new("test", Stream, Ciphers);
            drain = Task.Run(async () => { try { var bytes = new byte[65536]; while (await reader.GetStream().ReadAsync(bytes) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
            // The projectile outbox captures the schedule when the session is created.
            var previousUpdates = Game.AreaUpdateContext.Value;
            if (projectileSchedule is not null) Game.AreaUpdateContext.Value = new(lifetime, projectileSchedule);
            try { Session = new(Player); }
            finally { Game.AreaUpdateContext.Value = previousUpdates; }
        }
        internal Task<uint> Summon(string effect, uint target)
        {
            ushort source = Game.GemSkillEffects(Player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            return Game.SummonFighter(Player, source, Game.SummonData.Find(effect)!, (100, 100), target);
        }
        internal ushort[] Equip(params (string Name, int Level)[] gems)
        {
            Item Gem((string Name, int Level) gem)
            {
                var type = Program.Items.FindGem(gem.Name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(gem.Level) };
            }
            uint id = Game.ItemsOf(Player).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(Player).Change(PlayerInventories.Weapon1, id, item => item with
            { Sockets = gems.Select(g => new ItemSocket(SocketColour.White, Gem(g))).ToArray(),
                Links = [(byte)gems.Length] });
            return Game.GemSkillEffects(Player).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Session.Invalidate(); Session.OutputCompleted.GetAwaiter().GetResult();
            Player.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Player.Character.Name!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
