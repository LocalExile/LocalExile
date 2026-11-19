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
        var conn = new Player(new(1, "golem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
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
        // Added Chaos must not make a skeleton's native QuickDodge eligible for the attack controller.
        // The same rule applies to zero-base utility spells without an explicit no-damage stat.
        var summons = new SummonCatalog();
        foreach (var (summon, utility, hit) in new[] {
            ("SummonSkeletons", "MonsterQuickDodgeRunUnarmed", "Melee"),
            ("SummonHolyRelic", "RelicTeleport", "RelicTriggeredNova") })
        {
            var definition = summons.Find(summon)!;
            var nativeDefinition = new GolemCatalog.Definition(skills.EffectRow(summon), summon, definition.Family,
                definition.Variety, 0, [], false, false, false);
            var fighter = new GolemState.Golem(1, null!, nativeDefinition, 1);
            var variety = monsters.Find(definition.Variety)!;
            var beforeAdded = Game.GolemAbilities(conn, variety, 70, fighter, new Dictionary<uint, int>());
            Assert.Equal(0d, beforeAdded.Single(a => a.Id == utility).Attack.Damage.MaxTotal);
            Assert.True(beforeAdded.Single(a => a.Id == hit).Attack.Damage.MaxTotal > 0);
            // SupportedSkill projects global added damage into both the attack and spell forms.
            foreach (string kind in new[] { "global", "attack", "spell" })
                foreach (string bound in new[] { "minimum", "maximum" })
                    fighter.Own[Program.Stats.Key($"{kind}_{bound}_added_chaos_damage")!.Value] = 100;
            var afterAdded = Game.GolemAbilities(conn, variety, 70, fighter, new Dictionary<uint, int>());
            Assert.Equal(0d, afterAdded.Single(a => a.Id == utility).Attack.Damage.MaxTotal);
            Assert.Equal(beforeAdded.Single(a => a.Id == hit).Attack.Damage[DamageType.Chaos].Min + 100,
                afterAdded.Single(a => a.Id == hit).Attack.Damage[DamageType.Chaos].Min);
        }
        Assert.Equal(3, GolemCatalog.Limit(1, n => n == "number_of_golems_allowed" ? 2 : 0));
        Assert.Equal(4, GolemCatalog.Limit(1, n => n == "base_number_of_golems_allowed" ? 3 : 0));
        Assert.Equal(8, GolemCatalog.Limit(3, n => n switch { "number_of_golems_allowed" => 1, "double_golem_limit" => 1, _ => 0 }));
        var stone = catalog.Find("SummonStoneGolem")!;
        Assert.Equal(70, GolemCatalog.BuffEffect(stone, n => n == "golem_buff_effect_+%" ? 50 : 0,
            n => n == "golem_buff_effect_+%" ? 20 : 0, 1));
        var shielded = new GolemState.Golem(1, null!, flame, 1);
        var raw = Game.GolemPools.Calculate(monsters, varietyFlame, 70, shielded,
            new Dictionary<uint, int>(), new(new Dictionary<string, int>()));
        int nativeChaos = HitMitigation.Resistance(monsters, varietyFlame, 70, DamageType.Chaos).Value;
        var converted = Game.GolemPools.Calculate(monsters, varietyFlame, 70, shielded,
            new Dictionary<uint, int>(), new(new Dictionary<string, int>
            {
                ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                ["base_chaos_damage_resistance_%"] = 50 - nativeChaos
            }));
        Assert.False(raw.Fatal); Assert.True(raw.MaximumLife > 0);
        var justBelow = Game.GolemPools.Calculate(monsters, varietyFlame, 70, shielded,
            new Dictionary<uint, int>(), new(new Dictionary<string, int>
            {
                ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                ["base_chaos_damage_resistance_%"] = 49 - nativeChaos
            }));
        Assert.False(justBelow.Fatal); Assert.True(justBelow.MaximumLife > 0);
        Assert.True(justBelow.MaximumEnergyShield > 0);
        Assert.True(converted.Fatal); Assert.Equal(0u, converted.MaximumLife);
        Assert.Equal(0, converted.MaximumEnergyShield);
        var pool = shielded.ShieldPool;
        pool.SetMaximum(300, refill: true);
        Assert.Equal(0, pool.Damage(100, bypass: false, 10));
        var carried = pool.Capture(10.5);
        var replacementPool = new EnergyShield(); replacementPool.SetMaximum(250);
        replacementPool.Restore(carried, 50);
        Assert.Equal(200u, replacementPool.Current);
        Assert.False(replacementPool.RechargeReady(51.49, _ => 0));
        Assert.True(replacementPool.RechargeReady(51.5, _ => 0));
        Assert.Equal(50, replacementPool.Damage(50, bypass: true, 52));
        Assert.Equal(200u, replacementPool.Current);
        // Recovery buffs have conditional fields absent from ordinary golem buff packets.
        var safeguarding = new GolemState.Golem(0x4000000, null!, catalog.Find("SummonStoneGolemAltY")!, 1);
        safeguarding.OwnerBuff[Program.Stats.Key("melee_damage_removed_from_stone_golem_before_life_or_es_%")!.Value] = 18;
        byte[] guard = Game.GolemOwnerBuffPacket(7, 592, safeguarding);
        Assert.Equal(33, guard.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 18, 0, 0, 0, 0 }, guard[23..]);
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
