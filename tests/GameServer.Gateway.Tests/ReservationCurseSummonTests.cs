using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ReservationCurseSummonTests
{
    [Fact]
    public void LifeReservationIsSeparateCappedAndCursesReplaceWithoutMutatingMods()
    {
        var pools = new CharacterStats.Pools(90, 1200, 800, 2, 0, LifeReservationEfficiencyPct: 100);
        var life = new AuraCatalog.Aura(1, 0, "Vitality", "Vitality", 1, 153, 200, 0, ReservesLife: true);
        var mana = life with { SkillId = 2, ReservesLife = false, ManaFlat = 0, ManaPercent = 5000 };
        Assert.Equal(100, AuraCatalog.RequiredLife(pools, [life, mana]));
        Assert.Equal(400, AuraCatalog.RequiredMana(pools, [life, mana]));
        Assert.Equal(67, AuraCatalog.RequiredLife(pools, [life with { ReservationEfficiencyPct = 100 }]));
        var conn = new ClientState(new(1, "reserve-test", 100, 100, 10, 10, null, []), 1, 1) { Pools = pools, PlayerLife = 1200 };
        conn.Auras[1] = life;
        Assert.Equal(1100u, conn.PlayerUnreservedLife);
        double carry = 0;
        Assert.Equal(1100u, PetrifiedBlood.RecoverLife(conn, 1000, 500, ref carry));
        Assert.Empty(AuraCatalog.Restore(["Vitality"], [life with { ManaFlat = 2400 }], pools));
        var curses = new MonsterCurses();
        var original = new MonsterModEffects(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = 20 });
        MonsterCurses.Curse Curse(uint caster, string effect, int value, double at) => new(caster, effect, 1, 71, (ushort)at, at, 20, false, false,
            new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = value });
        curses.Put(Curse(1, "Flammability", -36, 1), 1);
        Assert.Equal(-16, curses.Effects(original).Resistance(DamageType.Fire));
        curses.Put(Curse(2, "Flammability", -20, 2), 1);
        Assert.Equal(-16, curses.Effects(original).Resistance(DamageType.Fire));
        curses.Put(Curse(1, "ElementalWeakness", -30, 3), 1);
        Assert.DoesNotContain((1u, "Flammability"), curses.All.Keys);
        Assert.Equal(-30, curses["base_fire_damage_resistance_%"]);
        Assert.Single(curses.All);
        curses.Put(Curse(2, "Flammability", -20, 4), 2);
        Assert.Equal(-50, curses["base_fire_damage_resistance_%"]);
        Assert.Equal(20, original.Resistance(DamageType.Fire));
        curses.Expire(20);
        Assert.Empty(curses.All); Assert.Same(original, curses.Effects(original));
        Assert.Equal(14, CurseCatalog.All.Count); Assert.False(CurseCatalog.Hex("AssassinsMark"));
        var skitter = new GolemState.Golem(1, null!, new(10825, "SummonSkitterbots", "SkitterCold", "", 0, [], false, false, false), 1)
            { Own = Program.Skills.EffectStats(10825, 20).ToDictionary(p => p.Item1, p => p.Item2) };
        var ailments = new MonsterElementalAilments();
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        Assert.Equal(11, ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 0));
        Assert.Equal(17, ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 0));
    }

    [Fact]
    public async Task SkeletonsUseTheirOwnLimitExpireAndHaveNativeCombatSkills()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-summons-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "summon-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "SummonRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100), Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("summon-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        conn.Summons.Restored = conn.Golems.Restored = true;
        foreach (string s in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(s)!.Value] = 1000;
        try
        {
            var catalog = new SummonCatalog(); var monsters = new MonsterCatalog();
            foreach (var definition in catalog.All.Where(d => d.Variety.Length > 0 && d.Family != "Clone"))
            {
                var variety = monsters.Find(definition.Variety);
                Assert.True(variety is { Supported: true }, definition.Id + " " + definition.Variety);
                var effect = Program.Skills.EffectRow(definition.Id);
                Assert.True(effect >= 0);
                var g = new GolemState.Golem(1, null!, new(effect, definition.Id, definition.Family, definition.Variety, 0, [], false, false, false), 1);
                var abilities = Game.GolemAbilities(conn, variety!, 70, g, new Dictionary<uint, int>());
                Assert.NotEmpty(Game.GolemNativeSkills(conn, 70, abilities));
            }
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonSkeletons")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == "SummonSkeletons");
            var skeleton = catalog.Find("SummonSkeletons")!;
            int limit = Game.SummonLimit(conn, skeleton, Game.SupportedSkill(conn, source.Id));
            Assert.Equal(5, limit);
            for (int i = 0; i < limit + 2; i++) Assert.NotEqual(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, source.Id, skeleton, (100, 100)));
            Assert.Equal(limit, conn.Summons.Fighters.Count); Assert.Empty(conn.Golems.All);
            Assert.All(conn.Summons.Fighters.Values, g => Assert.Contains(g.Abilities, a => a.Attack.Damage.MaxTotal > 0));
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds + 21);
            Assert.Empty(conn.Summons.Fighters);
            ushort Equip(string effect, params string[] supports)
            {
                Item Gem(string name)
                {
                    var type = Program.Items.FindGem(name)!.Value;
                    var item = ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index };
                    return item with { GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
                }
                var sockets = new[] { effect }.Concat(supports).Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray();
                uint host = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id;
                Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, host, item => item with { Sockets = sockets, Links = [(byte)sockets.Length] });
                return Game.GemSkillEffects(conn).First().Id;
            }
            ushort curseSource = Equip("Flammability", "Blasphemy Support");
            var victim = new SpawnedMonster("curse-target", new(100, 100, 1), 100, 8, 100000)
                { Hostile = true, Variety = monsters.Find("Metadata/Monsters/Zombies/Zombie"), Level = 70 };
            conn.Monsters[0x800abcd] = victim;
            Assert.True(Game.ApplyCurseLocked(conn, victim, curseSource, 1, true));
            Assert.Equal(-27, victim.Effects["base_fire_damage_resistance_%"]);
            Assert.Equal(0, victim.Mods.Effects["base_fire_damage_resistance_%"]);
            Assert.Single(victim.Curses.All);
            victim.Curses.Clear();

            ushort zombieSource = Equip("Raise Zombie");
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100)));
            victim.Life = 0; victim.Dead = true;
            uint zombie = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100), 0x800abcd);
            Assert.NotEqual(0u, zombie); Assert.True(victim.CorpseConsumed);
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100), 0x800abcd));
            var fighter = conn.Summons.Fighters[zombie];
            Assert.True(Game.GolemTakeHitLocked(conn, fighter, 0x800abce, new("attacker", new(100, 100, 1), 100, 8, 1000),
                new(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1000000, 1000000)) { Spell = true }, 2));
            Assert.True(fighter.Monster.Dead); Assert.True(double.IsPositiveInfinity(fighter.ReviveAt));
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 3);
            Assert.Empty(conn.Summons.Fighters);

            ushort animated = Equip("Animate Weapon");
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, animated, catalog.Find("AnimateWeapon")!, (100, 100)));
            var dropped = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!));
            uint ground = conn.Instance.Ground.Add(dropped);
            uint sword = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, animated, catalog.Find("AnimateWeapon")!, (100, 100), ground);
            Assert.NotEqual(0u, sword); Assert.Null(conn.Instance.Ground.Get(ground));
            Assert.NotNull(conn.Summons.Fighters[sword].Weapon);
            Assert.Single(conn.Summons.Fighters[sword].Equipment);

            var relicDefinition = catalog.Find("SummonHolyRelic")!;
            var relic = new GolemState.Golem(0x400ab00, new("relic", new(100, 100, 1), 100, 8, 1000),
                new(9793, relicDefinition.Id, relicDefinition.Family, relicDefinition.Variety, 0, [], false, false, false), 1)
                { Own = Program.Skills.EffectStats(9793, 20).ToDictionary(p => p.Item1, p => p.Item2) };
            var nova = new GolemState.Ability(9927, 0x4000, "RelicTriggeredNova", new(0x4000, 1, 30, DamagePacket.Empty),
                Program.Skills.EffectStats(9927, 20).ToDictionary(p => p.Item1, p => p.Item2), 30, .3, 1, .5);
            Game.HolyRelicRegenerationLocked(conn, relic, nova, 5);
            Assert.Equal(9, conn.Summons.HolyRegenerationUntil);
            Assert.Equal(9275, conn.Summons.HolyRegenerationPerMinute);
            var nativeRegen = conn.Minions.OutputQueue.Last(o => o.Log == "Holy Relic regeneration").Packet;
            // 0x1c1 fixed header + duration + empty list. This non-recovery row has no GrantedStats payload.
            Assert.Equal(31, nativeRegen.Length);
            Assert.Equal(new byte[4], nativeRegen[^4..]);

            var mine = new PlacedSkills.Placed(1, 1, 1, (100, 100), true, 0, 5, DamagePacket.Empty, new Dictionary<uint, int>(), 6, 0, false, 0);
            Assert.Equal(6, PlacedSkills.Reserved(pools: conn.Pools, [mine], life: false));
            mine.SkitterTriggered = true;
            conn.Placed.All[1] = mine;
            Game.DetonateMinesLocked(conn, 4);
            Assert.Equal(4, mine.DetonateAt);
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!); lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly TcpClient writer = new(); readonly TcpClient reader; readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false); public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult(); drain = Task.Run(async () => { try { var b = new byte[65536]; while (await reader.GetStream().ReadAsync(b) > 0) { } } catch (Exception e) when (e is IOException or ObjectDisposedException) { } });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}
