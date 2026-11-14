using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class GolemTests
{
    [Fact]
    public void AllPlayableGolemsHaveNativeSkillsAndScaleAtTheirActorLevel()
    {
        var catalog = new GolemCatalog(); var skills = new SkillCatalog(); var monsters = new MonsterCatalog();
        Assert.Equal(17, catalog.All.Count);
        Assert.Null(catalog.Find("SummonLightningGolemAltY"));
        var conn = new ClientState(new(1, "golem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        foreach (var definition in catalog.All)
        {
            var variety = monsters.Find(definition.Variety);
            Assert.NotNull(variety); Assert.True(variety.Supported);
            var own = skills.EffectStats(definition.Effect, 20, 20).ToDictionary(p => p.Item1, p => p.Item2);
            int Get(string name) => own.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(70, Get("base_display_minion_actor_level"));
            Assert.Equal(definition.Hordes ? 3 : 1, Get("base_number_of_golems_allowed"));
            Assert.Equal(definition.Hordes || definition.Special && definition.Kind != "Stone", definition.Buff == 0);
            var golem = new GolemState.Golem(1, null!, definition, 1) { Own = own };
            var abilities = Game.GolemAbilities(conn, variety, 70, golem, new Dictionary<uint, int>());
            var native = Game.GolemNativeSkills(conn, 70, abilities);
            Assert.Equal(variety.GrantedEffects!.Count, abilities.Count);
            Assert.Equal(abilities.Count, native.Count);
            Assert.Equal(abilities.Count, abilities.Select(a => a.Skill).Distinct().Count());
            Assert.Contains(abilities, a => a.Attack.Damage.MaxTotal > 0);
            Assert.All(abilities, a => Assert.True(a.Attack.Seconds > 0));
            foreach (var (source, target) in definition.BuffStats)
            { Assert.NotNull(Program.Stats.Key(source)); Assert.NotNull(Program.Stats.Key(target)); }
        }
        // One-row monster spells must use actor level, not leave the row's level-3 values in a level-70 golem.
        var low = catalog.Skill(skills, 5025, 34); var high = catalog.Skill(skills, 5025, 70);
        uint cold = Program.Stats.Key("spell_minimum_base_cold_damage")!.Value;
        Assert.True(high.Stats[cold] > low.Stats[cold] * 3);
        var cone = catalog.Skill(skills, 5056, 34);
        Assert.Equal(1, cone.Level);
        Assert.Equal(133, cone.Stats[Program.Stats.Key("spell_minimum_base_fire_damage")!.Value]);
        Assert.Equal(20, catalog.Skill(skills, 5056, 70).Level);
        Assert.Equal(522, catalog.Skill(skills, 4336, 3).Stats[Program.Stats.Key("base_chaos_damage_to_deal_per_minute")!.Value]);
        var flame = catalog.Find("SummonFlameGolem")!;
        var minion = new GolemState.Golem(1, null!, flame, 1);
        var varietyFlame = monsters.Find(flame.Variety)!;
        var baseline = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        foreach (string kind in new[] { "global", "attack", "spell" })
            foreach (string bound in new[] { "minimum", "maximum" })
                minion.Own[Program.Stats.Key($"{kind}_{bound}_added_fire_damage")!.Value] = 100;
        var added = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        Assert.All(added, a => Assert.Equal(baseline.Single(b => b.Effect == a.Effect).Attack.Damage[DamageType.Fire].Min + 100,
            a.Attack.Damage[DamageType.Fire].Min));
        Assert.Equal(3, GolemCatalog.Limit(1, n => n == "number_of_golems_allowed" ? 2 : 0));
        Assert.Equal(4, GolemCatalog.Limit(1, n => n == "base_number_of_golems_allowed" ? 3 : 0));
        Assert.Equal(8, GolemCatalog.Limit(3, n => n switch { "number_of_golems_allowed" => 1, "double_golem_limit" => 1, _ => 0 }));
        var stone = catalog.Find("SummonStoneGolem")!;
        Assert.Equal(70, GolemCatalog.BuffEffect(stone, n => n == "golem_buff_effect_+%" ? 50 : 0,
            n => n == "golem_buff_effect_+%" ? 20 : 0, 1));
        // Recovery buffs have conditional fields absent from ordinary golem buff packets.
        var safeguarding = new GolemState.Golem(0x4000000, null!, catalog.Find("SummonStoneGolemAltY")!, 1);
        safeguarding.OwnerBuff[Program.Stats.Key("melee_damage_removed_from_stone_golem_before_life_or_es_%")!.Value] = 18;
        byte[] guard = Game.GolemOwnerBuffPacket(7, 592, safeguarding);
        Assert.Equal(33, guard.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 18, 0, 0, 0, 0 }, guard[23..]);
    }

    [Fact]
    public async Task GolemsReplaceWithoutStackingBuffsTakeDamageReviveAndLoseTheirSource()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-golems-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "golem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "GolemRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("golem-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Golems.Restored = true;
        foreach (string stat in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
        try
        {
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Summon Flame Golem");
            var definition = Game.GolemData.Find("SummonFlameGolem")!;
            uint first = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(0u, first);
            uint next = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(first, next); Assert.Single(conn.Golems.All); Assert.False(conn.Monsters.ContainsKey(first));
            uint increased = Program.Stats.Key("damage_+%")!.Value;
            Assert.Equal(20, conn.Golems.OwnerStats[increased]); Assert.Single(conn.Golems.Buffs);
            Game.SaveGolems(conn);
            var saved = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.ModuleData(conn.Character, "Game.Golems")!);
            Assert.Single(saved!); Assert.Equal(definition.Id, saved![0].Effect);

            var golem = conn.Golems.All[next];
            var attacker = new SpawnedMonster("enemy", new(100, 100, 1), 100, 8, 1000);
            var spell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1000000, 1000000)) { Spell = true };
            // Fixed extreme values isolate the support multipliers from random crits and integer rounding.
            uint army = Program.Stats.Key("support_minion_totem_resistance_elemental_damage_+%_final")!.Value;
            var variety = Assert.IsType<MonsterCatalog.Variety>(golem.Monster.Variety);
            double FireDamage() => Game.GolemAbilities(conn, variety, golem.Monster.Level, golem, golem.Player)
                .Sum(a => a.Attack.Damage[DamageType.Fire].Max);
            double fire = FireDamage();
            Assert.True(fire > 0);
            golem.Own[army] = 100;
            Assert.Equal(2 * fire, FireDamage(), 5);
            golem.Own.Remove(army);

            uint shield = Program.Stats.Key("support_minion_defensive_stance_minion_damage_taken_+%_final")!.Value;
            golem.Own[shield] = -100;
            uint life = golem.Monster.Life;
            Game.GolemTakeHitLocked(conn, golem, 0x08000000, attacker, spell, 2);
            Assert.Equal(life, golem.Monster.Life);
            golem.Own.Remove(shield);
            Assert.True(Game.GolemTakeHitLocked(conn, golem, 0x08000000, attacker, spell, 3));
            Assert.True(golem.Monster.Dead); Assert.Equal(0u, golem.Monster.Life); Assert.Equal(5, golem.ReviveAt);
            await Game.RefreshGolemBuffs("test", wire.Stream, wire.Ciphers, conn);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased)); Assert.Empty(conn.Golems.Buffs);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 4.9);
            Assert.True(golem.Monster.Dead);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5);
            Assert.Single(conn.Golems.All); Assert.False(conn.Golems.All.Values.Single().Monster.Dead);
            Assert.Equal(20, conn.Golems.OwnerStats[increased]);

            uint worn = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id;
            int experience = Program.Items.Gem(Program.Items.ByHash(gem.TypeHash)!)!.ExperienceFor(20);
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, worn, item => item with
            { Sockets = [new(SocketColour.White, gem with { GemExperience = experience })] });
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9);
            Assert.Equal(70, conn.Golems.All.Values.Single().Monster.Level);
            Assert.Equal(29, conn.Golems.OwnerStats[increased]);
            var auraFighter = conn.Golems.All.Values.Single();
            var auraAt = auraFighter.Monster.Walk.Position(0);
            var allyHaste = new AuraCatalog.Aura(1, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000);
            var ally = new InstancePlayer(0x2000abcd, 0, new("Ally", 0, auraAt.X, auraAt.Y, 100, 100), auraAt.X, auraAt.Y, 100)
                { Auras = [new(allyHaste, 1000, 100)] };
            conn.Instance.PublishPlayer(ally);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Single(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9.6);
            Assert.Equal(20, auraFighter.Auras["base_movement_velocity_+%"]);
            Assert.Equal(30, auraFighter.Auras["attack_speed_+%"]);
            conn.Instance.RemovePlayer(ally.Id);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Empty(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 10.2);
            Assert.Empty(auraFighter.Auras); // Leaving the aura's instance retires all its cached minion bonuses.
            var living = conn.Golems.All.Values.Single();
            var position = living.Monster.Walk.Position(0);
            var target = new SpawnedMonster("golem target", new(position.X + 2, position.Y, 0), 100, 8, 1000000);
            conn.Monsters[0x08000001] = target;
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 10.3);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 11.3);
            Assert.True(target.Life < target.MaxLife);
            var smallHit = spell with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) };
            uint before = living.Monster.Life;
            Assert.True(Game.GolemTakeHitLocked(conn, living, 0x08000000, attacker, smallHit, 12));
            Assert.True(living.Monster.Life < before);

            // A death reported by another damage executor still schedules the same two-second return.
            attacker.BusyUntil = double.PositiveInfinity; attacker.ActionGeneration = 1;
            conn.Monsters[0x08000000] = attacker;
            conn.Golems.IncomingHits.Add(new(0x08000000, attacker, living, spell, 1));
            living.Monster.Dead = true; living.Monster.Life = 0;
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 12);
            Assert.Equal(14, living.ReviveAt);
            Assert.Equal(12, attacker.BusyUntil); Assert.Empty(conn.Golems.IncomingHits);

            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 13);
            Assert.Empty(conn.Golems.All); Assert.Empty(conn.Golems.Buffs);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased));
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 15);
            Assert.Empty(conn.Golems.All);

            // Reservation minions use the same native abilities/contact pipeline, without acquiring golem limits,
            // owner buffs or the local golem auto-resummon rule. Every permanent gem must release its reservation.
            foreach (string effectId in new[] { "Haste", "PurityOfElements", "PurityOfFire", "PurityOfIce", "PurityOfLightning",
                "Clarity", "Vitality", "Precision", "Determination", "Grace", "Discipline", "Anger", "Hatred", "Wrath",
                "Zealotry", "Malevolence", "BloodAndSand", "FleshAndStone", "Pride", "ArcticArmour", "TempestShield",
                "HeraldOfAsh", "HeraldOfIce", "HeraldOfThunder", "HeraldOfPurity", "HeraldOfAgony",
                "SummonSkitterbots", "TemporalRift" })
            {
                var type = Program.Items.All.FirstOrDefault(t => Program.Items.Gem(t) is { IsSupport: false } data
                    && Program.Skills.EffectId(data.Variant(0).Effect) == effectId);
                Assert.True(type is not null, "Missing reservation gem: " + effectId);
                var reservationGem = ItemCatalog.Create(type!);
                var wornWeapon = weapon with { Sockets = [new(SocketColour.White, reservationGem)] };
                Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wornWeapon);
                var active = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == effectId);
                var reservation = Game.SupportedReservation(conn, active.Id);
                Assert.NotNull(reservation);
                Assert.True(await Game.ToggleAura("test", wire.Stream, wire.Ciphers, conn, active.Id, active.Effect,
                    active.Name, reservation!.Value, active.Level, active.Quality), effectId);
                Assert.Single(conn.Auras); Assert.True(Game.ReservedMana(conn) > 0, effectId);
                var aura = conn.Auras[active.Id];
                if (effectId == "TempestShield")
                    Assert.Equal(Convert.FromHexString("000000020000000000000000"),
                        AuraCatalog.StartPackets(aura, 0x1000, null).Last()[^12..]);
                if (effectId == "TemporalRift")
                    Assert.Equal(Convert.FromHexString("000000020000002A00000064"),
                        AuraCatalog.StartPackets(aura, 0x1000, null).Last()[^12..]);
                if (effectId is "HeraldOfIce" or "HeraldOfThunder" or "TempestShield")
                    Assert.True(Game.ReservationHitPacket(conn, aura).MaxTotal > 0, effectId);
                if (effectId == "HeraldOfPurity")
                {
                    Game.ReservationKill(new(conn, target, "test", position));
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Contains(conn.Reservations.Fighters.Values, f => f.Definition.Kind == "Purity");
                }
                if (effectId == "SummonSkitterbots")
                {
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Equal(2, conn.Reservations.Fighters.Count);
                }
                await Game.DisableReservations("test", wire.Stream, wire.Ciphers, conn, effectId);
                Assert.Empty(conn.Auras); Assert.Equal(0, Game.ReservedMana(conn));
                Assert.Empty(conn.Reservations.Fighters);
                Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            }
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
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
