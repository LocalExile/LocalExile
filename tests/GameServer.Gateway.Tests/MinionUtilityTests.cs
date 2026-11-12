using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
namespace GameServer.Gateway.Tests;
public sealed class MinionUtilityTests
{
    [Fact]
    public async Task Convocation_recalls_followers_clears_actions_and_limits_regeneration_to_its_duration()
    {
        using var f = new Fixture();
        ushort source = f.Equip(("Summon Holy Relic",20)).Single();
        uint id = await Game.SummonFighter("test",f.Stream,f.Ciphers,f.Conn,source,Game.SummonData.Find("SummonHolyRelic")!,(160,160));
        var relic=f.Conn.Summons.Fighters[id];
        source=f.Equip(("Summon Skeletons",20)).Single();
        uint skeleton=await Game.SummonFighter("test",f.Stream,f.Ciphers,f.Conn,source,Game.SummonData.Find("SummonSkeletons")!,(180,180));
        var stationary=f.Conn.Summons.Fighters[skeleton].Monster.Walk.Position(0);
        source=f.Equip(("Convocation",20)).Single();
        var gem=Game.SupportedSkill(f.Conn,source);
        relic.Monster.Life=relic.Monster.MaxLife/2;
        Game.RecallMinionsLocked(f.Conn,gem,10);
        Assert.True(Game.Dist(relic.Monster.Walk.Position(10),f.Conn.Walk.Position(10))<20);
        Assert.Equal(stationary,f.Conn.Summons.Fighters[skeleton].Monster.Walk.Position(10));
        Assert.Equal(12,relic.RecallUntil);Assert.Equal(99,relic.RecallRate);
        Assert.Null(relic.Pending);
        uint before = relic.Monster.Life;
        Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, relic, 13);
        Assert.InRange(relic.Monster.Life - before, (uint)(relic.Monster.MaxLife * .033) - 1, (uint)(relic.Monster.MaxLife * .033) + 1);
        uint healed = relic.Monster.Life;
        Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, relic, 14);
        Assert.Equal(healed, relic.Monster.Life);
        Assert.Contains(f.Conn.Minions.OutputQueue,o=>o.Log=="Convocation recall"&&o.TargetId==id);
        Assert.DoesNotContain(f.Conn.Minions.OutputQueue,o=>o.Log=="Convocation recall"&&o.TargetId==skeleton);
        Assert.Equal(3000,Program.Skills.CooldownMs(gem.Effect,gem.Level));
    }

    [Fact]
    public async Task Offerings_consume_at_most_five_corpses_replace_refresh_and_expire_their_combat_stats()
    {
        await VerifyTriggeredUtilities();
        using var f=new Fixture();
        ushort source=f.Equip(("Summon Holy Relic",20)).Single();
        uint id=await Game.SummonFighter("test",f.Stream,f.Ciphers,f.Conn,source,Game.SummonData.Find("SummonHolyRelic")!,(100,100));
        var fighter=f.Conn.Summons.Fighters[id];
        source=f.Equip(("Flesh Offering",20)).Single();var gem=Game.SupportedSkill(f.Conn,source);
        Assert.False(Game.StartOfferingLocked(f.Conn,source,gem,"FleshOffering",(100,100),0,1));
        var variety=new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie")!;
        void Corpses(int count)
        {
            for(int i=0;i<count;i++) { uint key=f.Conn.NextObjectId++; f.Conn.Monsters[key]=new("corpse",new(100,100,1),100,8,1000)
                {Hostile=true,Dead=true,Life=0,Variety=variety,Level=70}; }
        }
        Corpses(6);
        Assert.True(Game.StartOfferingLocked(f.Conn,source,gem,"FleshOffering",(100,100),0,1));
        Assert.Equal(5,f.Conn.Monsters.Values.Count(m=>m.CorpseConsumed));
        Assert.True(f.Conn.RecentEvents.Happened(StatConditions.ConsumedCorpse, 1));
        Assert.Equal(10,Assert.Single(f.Conn.Summons.Offerings).Until);
        Assert.Equal(30,fighter.Monster.Mods.Effects["attack_speed_+%"]);
        Assert.Equal(30,fighter.Monster.Mods.Effects["cast_speed_+%"]);
        ushort first=fighter.UtilityBuffs[295];
        Assert.True(Game.StartOfferingLocked(f.Conn,source,gem,"FleshOffering",(100,100),0,2));
        Assert.NotEqual(first,fighter.UtilityBuffs[295]);Assert.Single(f.Conn.Summons.Offerings);
        source=f.Equip(("Spirit Offering",20)).Single();gem=Game.SupportedSkill(f.Conn,source);Corpses(1);
        Assert.True(Game.StartOfferingLocked(f.Conn,source,gem,"SpiritOffering",(100,100),0,3));
        Assert.Equal(0,fighter.Monster.Mods.Effects["attack_speed_+%"]);
        Assert.Equal(148,fighter.Monster.Mods.Effects["critical_strike_chance_+%"]);
        Assert.Equal(39,fighter.Monster.Mods.Effects["base_critical_strike_multiplier_+"]);
        source=f.Equip(("Bone Offering",20)).Single();gem=Game.SupportedSkill(f.Conn,source);Corpses(1);
        Assert.True(Game.StartOfferingLocked(f.Conn,source,gem,"BoneOffering",(100,100),0,4));
        Assert.Equal(35,fighter.Monster.Mods.Effects["monster_base_block_%"]);
        Assert.Equal(34,fighter.Monster.Mods.Effects["base_spell_block_%"]);
        Assert.Equal(240,fighter.Monster.Mods.Effects["life_regeneration_rate_per_minute_%_if_blocked_recently"]);
        Game.MinionUtilityTick(f.Conn,10);
        Assert.Empty(f.Conn.Summons.Offerings);Assert.Empty(fighter.UtilityBuffs);
        Assert.Equal(0,fighter.Monster.Mods.Effects["monster_base_block_%"]);
        Assert.Contains(f.Conn.Minions.OutputQueue,o=>o.Log=="Offering expired");

        void Set(string key, int value) => f.Conn.PlayerStats[Program.Stats.Key(key)!.Value] = value;
        int Stat(string key) => Game.CharacterPlayerStats(f.Conn).GetValueOrDefault(Program.Stats.Key(key)!.Value);
        Set("offerings_also_buff_you", 1); Set("self_offering_effect_+%", -50);
        Set("offering_spells_effect_+%", 20); Set("allow_multiple_offerings", 1);
        f.Conn.Pools = Game.PoolsFor(f.Conn, 100);
        int attackBefore = f.Conn.Pools.AttackSpeedPct, spellBlockBefore = f.Conn.Pools.SpellBlockChance;
        foreach (var (name, effect, at) in new[] { ("Flesh Offering", "FleshOffering", 20),
            ("Spirit Offering", "SpiritOffering", 21), ("Bone Offering", "BoneOffering", 22) })
        {
            source = f.Equip((name, 20)).Single(); gem = Game.SupportedSkill(f.Conn, source); Corpses(1);
            Assert.True(Game.StartOfferingLocked(f.Conn, source, gem, effect, (100,100), 0, at));
        }
        Assert.Equal(3, f.Conn.Summons.PlayerOfferingBuffs.Count);
        Assert.Equal(attackBefore + 21, f.Conn.Pools.AttackSpeedPct); // 30 * (1 + .2 - .5), not 36 * .5.
        Assert.Equal(21, Stat("cast_speed_+%"));
        Assert.Equal(103, Stat("critical_strike_chance_+%"));
        Assert.Equal(27, f.Conn.Summons.PlayerOfferingStats[Program.Stats.Key("base_critical_strike_multiplier_+")!.Value]);
        Assert.Equal(24, f.Conn.Pools.BlockChance); Assert.Equal(spellBlockBefore + 23, f.Conn.Pools.SpellBlockChance);
        Assert.False(f.Conn.Summons.PlayerOfferingStats.ContainsKey(Program.Stats.Key("life_regeneration_rate_per_minute_%")!.Value));
        f.Conn.RecentEvents.Record(StatConditions.Blocked, 22);
        Game.MinionUtilityTick(f.Conn, 22.1);
        Assert.Equal(168, Stat("life_regeneration_rate_per_minute_%"));
        Set("allow_multiple_offerings", 0); Set("allow_2_offerings", 1);
        Game.MinionUtilityTick(f.Conn, 22.2);
        Assert.Equal(2, f.Conn.Summons.Offerings.Count);
        Assert.Equal(attackBefore, f.Conn.Pools.AttackSpeedPct);
        Set("allow_2_offerings", 0); Game.MinionUtilityTick(f.Conn, 22.3);
        Assert.Equal("BoneOffering", Assert.Single(f.Conn.Summons.Offerings).Effect);
        Assert.Equal(0, Stat("critical_strike_chance_+%"));
        Game.MinionUtilityTick(f.Conn, 26);
        Assert.Equal(0, Stat("life_regeneration_rate_per_minute_%"));
        Set("offerings_also_buff_you", 0); Game.MinionUtilityTick(f.Conn, 26.1);
        Assert.Empty(f.Conn.Summons.PlayerOfferingStats); Assert.Empty(f.Conn.Summons.PlayerOfferingBuffs);
        Assert.Equal(0, f.Conn.Pools.BlockChance); Assert.Single(f.Conn.Summons.Offerings);
        Set("offerings_also_buff_you", 1); Game.MinionUtilityTick(f.Conn, 26.2);
        Assert.Equal(24, f.Conn.Pools.BlockChance);
        f.Conn.PlayerLife = 0; Game.MinionUtilityTick(f.Conn, 26.3);
        Assert.Empty(f.Conn.Summons.Offerings); Assert.Empty(f.Conn.Summons.PlayerOfferingBuffs);
        Assert.Empty(f.Conn.Summons.PlayerOfferingStats); Assert.Equal(0, f.Conn.Pools.BlockChance);

    }
    static async Task VerifyTriggeredUtilities()
    {
        foreach (string name in new[] { "Convocation", "Flesh Offering", "Bone Offering", "Spirit Offering" })
        {
            using var f = new Fixture();
            ushort relicSource = f.Equip(("Summon Holy Relic",20)).Single();
            uint relic = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, relicSource,
                Game.SummonData.Find("SummonHolyRelic")!, (160,160));
            ushort source = f.Equip((name,20), ("Cast when Damage Taken Support",20)).First();
            uint host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host, item => item with { Links = [2] });
            f.Conn.PlayerStats[Program.Stats.Key("offering_skills_do_not_require_corpses")!.Value] = 1;
            var gem = Game.SupportedSkill(f.Conn, source);
            Assert.True(Game.TriggerPayloadAvailable(f.Conn, gem));
            int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
            uint mana = f.Conn.PlayerMana;
            uint cost = (uint)Game.SupportedStats(f.Conn, source)[Program.Stats.Key("mana_cost")!.Value];
            int action = f.Conn.ActionCount;
            var session = new Game.ProjectileSession("utility-trigger", f.Stream, f.Ciphers, f.Conn);
            try
            {
                Game.RecordDamageTakenTriggers(f.Conn, threshold, 1000);
                Game.ReleaseDamageTakenTriggers(f.Conn, session);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
                await done.Task;
                Assert.Equal(mana - cost, f.Conn.PlayerMana); Assert.Equal(action, f.Conn.ActionCount);
                if (name == "Convocation") Assert.True(f.Conn.Summons.Fighters[relic].RecallUntil > 0);
                else Assert.Equal(source, Assert.Single(f.Conn.Summons.Offerings).Source);
            }
            finally { session.Invalidate(); await session.OutputCompleted; }
        }
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
        internal ushort[] Equip(params (string Name, int Level)[] gems)
        {
            Item Gem((string Name, int Level) gem)
            {
                var type = Program.Items.FindGem(gem.Name)!.Value;
                return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(gem.Level) };
            }
            uint id = Game.ItemsOf(Conn).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(Conn).Change(PlayerInventories.Weapon1, id, item => item with
            { Sockets = gems.Select(g => new ItemSocket(SocketColour.White, Gem(g))).ToArray(),
                Links = Enumerable.Repeat((byte)1, gems.Length).ToArray() });
            return Game.GemSkillEffects(Conn).Select(g => g.Id).ToArray();
        }
        public void Dispose()
        {
            Conn.Closed = true; lifetime.Stop(_ => { });
            PlayerInventories.Forget(Conn.Character!); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previous!);
            reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); TestEnvironment.ReleaseSaves(save);
        }
    }
}
