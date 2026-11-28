using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MontregulZombieTests
{
    static Item MakeGem(string name, int level = 20)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with
        {
            GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
        };
    }

    static ushort PutBodySkillGems(Player player, params string[] names)
    {
        var body = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets = names.Select(n => new ItemSocket(SocketColour.White, MakeGem(n))).ToArray(),
            Links = [(byte)names.Length],
        };
        var items = Game.ItemsOf(player);
        foreach (var existing in items.Items(PlayerInventories.BodyArmour).ToList())
            items.Remove(PlayerInventories.BodyArmour, existing.Id);
        items.Put(PlayerInventories.BodyArmour, new(100, 0, 0, body));
        return Game.GemSkillEffects(player).First(g => Program.Skills.EffectId(g.Effect) == "RaiseZombie").Id;
    }

    static bool ContainsStatUpdate(byte[] packet, uint entityId, uint stat, int value) =>
        packet.Length > 13 && packet[0] == 1 && packet[1] == 0xb3
        && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == entityId
        && packet.AsSpan(13).IndexOf(InstanceEntities.StatsUpdate(0, [(stat, value)]).AsSpan(13)) >= 0;

    static SpawnedMonster SpawnHostile(Player player, uint id, (int X, int Y) at, uint maxLife, uint life)
    {
        var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        var m = new SpawnedMonster("Zombie", new PlayerWalk(at.X, at.Y, 8), 100, 3, maxLife)
        {
            ObjectId = id,
            Variety = zombie,
            Level = 20,
            Life = life,
            Hostile = true,
            Rarity = MonsterRarity.Normal,
        };
        player.Area.Monsters[id] = m;
        return m;
    }

    [Fact]
    public async Task Montregul_s_grasp_scales_zombies_refreshes_live_and_explodes_hit_kills()
    {
        await VerifyScalingWireSizeAndLiveRefresh();
        await VerifyHitKillExplosionAndGuards();
    }

    static async Task VerifyScalingWireSizeAndLiveRefresh()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var items = Game.ItemsOf(player);
        items.Remove(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id);
        ushort raiseId = PutBodySkillGems(player, "Raise Zombie");
        var zombieDef = Game.SummonData.Find("RaiseZombie")!;
        var zombieSkill = Game.SupportedSkill(player, raiseId)!;
        int baseLimit = Game.SummonLimit(player, zombieDef, zombieSkill);
        Assert.True(baseLimit >= 3);

        var packets = new List<byte[]>();
        var previousOutgoing = Program.Outgoing;
        Program.Outgoing = (p, bytes) =>
        {
            if (ReferenceEquals(p.Connection.Session.Ciphers, f.Ciphers)) packets.Add(bytes.ToArray());
        };
        try
        {
            uint corpse0 = player.TakeObjectId();
            var corpse = SpawnHostile(player, corpse0, (100, 100), maxLife: 100, life: 0);
            corpse.Dead = true;

            uint z0Id = await Game.SummonFighter(player, raiseId, zombieDef, (100, 100), corpse0);
            Assert.NotEqual(0u, z0Id);
            var z0 = player.Summons.Fighters[z0Id];
            uint baseLife = z0.Monster.MaxLife;
            int baseChaosRes = z0.Monster.Effects[Stat.BaseChaosDamageResistancePct];
            int baseEleRes = z0.Monster.Effects[Stat.BaseResistAllElementsPct];
            double baseAttackMax = z0.Abilities.First(a => !a.Attack.Spell && a.Attack.Damage.MaxTotal > 0).Attack.Damage.MaxTotal;
            Assert.Equal(0, z0.Monster.Effects.ActorScalePct);
            Assert.DoesNotContain(player.MinionOrigins[z0Id].Object.Stats ?? [], s => s.Key == Stat.ActorScalePlusPct);

            // Dual-wielding two Mon'tregul's Grasp reduces zombie limit by 100% -> 0; single halves it.
            var montregul1 = new UniqueCatalog(Program.Items).Create("Mon'tregul's Grasp", new Random(7))!;
            var montregul2 = new UniqueCatalog(Program.Items).Create("Mon'tregul's Grasp", new Random(9))!;
            items.Put(PlayerInventories.Weapon1, new(101, 0, 0, montregul1));
            Assert.Equal(baseLimit / 2, Game.SummonLimit(player, zombieDef, Game.SupportedSkill(player, raiseId)));
            items.Put(PlayerInventories.Offhand1, new(102, 0, 0, montregul2));
            Assert.Equal(0, Game.SummonLimit(player, zombieDef, Game.SupportedSkill(player, raiseId)));
            items.Remove(PlayerInventories.Offhand1, 102);

            // Freshly spawned zombie with Mon'tregul's Grasp already equipped sends ActorScalePlusPct = 25 in 0x02bb create blob.
            uint corpse1 = player.TakeObjectId();
            var corpseB = SpawnHostile(player, corpse1, (105, 100), maxLife: 100, life: 0);
            corpseB.Dead = true;
            packets.Clear();
            var preSkill = Game.SupportedSkill(player, raiseId)!;
            Assert.True(preSkill.RequirementsMet, "preSkill RequirementsMet");
            Assert.Null(Game.GemSkillInstanceDisabled(player, preSkill.Effect, raiseId));
            Assert.True(Game.SummonLimit(player, zombieDef, preSkill) >= 1, "preSkill SummonLimit");
            uint z1Id = await Game.SummonFighter(player, raiseId, zombieDef, (105, 100), corpse1);
            Assert.NotEqual(0u, z1Id);
            var z1 = player.Summons.Fighters[z1Id];
            var z1Origin = player.MinionOrigins[z1Id];
            var playerStats = Game.CharacterPlayerStats(player);
            int rolledChaos = playerStats[Stat.ZombieChaosElementalDamageResistancePct];
            int rolledPhysMore = playerStats[Stat.ZombiePhysicalDamagePlusPctFinal];
            Assert.InRange(rolledChaos, 25, 30);
            Assert.InRange(rolledPhysMore, 100, 125);
            Assert.Equal(25, z1.Monster.Effects.ActorScalePct);
            uint scaledFlatLife = (uint)Math.Round(5000 * (1 + z1.Own.GetValueOrDefault(Stat.ActiveSkillMinionLifePlusPctFinal) / 100d));
            Assert.Equal(baseLife + scaledFlatLife, z1.Monster.MaxLife);
            Assert.Equal(baseChaosRes + rolledChaos, z1.Monster.Effects[Stat.BaseChaosDamageResistancePct]);
            Assert.Equal(baseEleRes + rolledChaos, z1.Monster.Effects[Stat.BaseResistAllElementsPct]);
            double scaledAttackMax = z1.Abilities.First(a => !a.Attack.Spell && a.Attack.Damage.MaxTotal > 0).Attack.Damage.MaxTotal;
            Assert.InRange(scaledAttackMax, baseAttackMax * (1 + rolledPhysMore / 100d) - 1, baseAttackMax * (1 + rolledPhysMore / 100d) + 1);
            Assert.Contains(z1Origin.Object.Stats ?? [], s => s.Key == Stat.ActorScalePlusPct && s.Value == 25);
            Assert.Contains(packets, p => p.AsSpan().IndexOf(InstanceEntities.CreateObject(z1Id, z1Origin.Type, z1Origin.Object.Blob(), headerU32: uint.MaxValue)) >= 0);

            // Live unequip -> re-equip during world frames emits 0x01b3 SetStats(ActorScalePlusPct) and refreshes MinionOrigins + life.
            await Program.BeginWorldFrames(player, .033);
            packets.Clear();
            items.Remove(PlayerInventories.Weapon1, 101);
            await Game.SummonsTick(player, 1.0);
            player.Area.Defer(_ => player.Area.Stats.Publish());
            player.Area.Step(1, .033);
            await Program.EndWorldFrame(player, (.033, .066), player.Area.Delta.Take());

            Assert.Equal(0, z1.Monster.Effects.ActorScalePct);
            Assert.Equal(0, player.Area.Stats.WrittenValue(z1Id, Stat.ActorScalePlusPct));
            Assert.Contains(packets, p => ContainsStatUpdate(p, z1Id, Stat.ActorScalePlusPct, 0));
            Assert.DoesNotContain(player.MinionOrigins[z1Id].Object.Stats ?? [], s => s.Key == Stat.ActorScalePlusPct);
            Assert.Equal(baseLife, z1.Monster.MaxLife);

            packets.Clear();
            items.Put(PlayerInventories.Weapon1, new(101, 0, 0, montregul1));
            await Game.SummonsTick(player, 1.6);
            player.Area.Defer(_ => player.Area.Stats.Publish());
            player.Area.Step(2, .066);
            await Program.EndWorldFrame(player, (.066, .099), player.Area.Delta.Take());

            Assert.Equal(25, z1.Monster.Effects.ActorScalePct);
            Assert.Equal(25, player.Area.Stats.WrittenValue(z1Id, Stat.ActorScalePlusPct));
            Assert.Contains(packets, p => ContainsStatUpdate(p, z1Id, Stat.ActorScalePlusPct, 25));
            Assert.Contains(player.MinionOrigins[z1Id].Object.Stats ?? [], s => s.Key == Stat.ActorScalePlusPct && s.Value == 25);
            Assert.Equal(baseLife + scaledFlatLife, z1.Monster.MaxLife);
            items.Remove(PlayerInventories.Weapon1, 101);

            // Replica Earendel's Embrace applies self-burn to zombies via InfernalLegionSelfDamage.
            var earendel = new UniqueCatalog(Program.Items).Create("Replica Earendel's Embrace", new Random(3))!;
            items.Put(PlayerInventories.Weapon1, new(104, 0, 0, earendel));
            await Game.SummonsTick(player, 2.2);
            Assert.True(Game.InfernalLegionSelfDamage(z1, elapsed: 1.0, taken: 1.0) > 0);
        }
        finally
        {
            Program.Outgoing = previousOutgoing;
        }
    }

    static async Task VerifyHitKillExplosionAndGuards()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var items = Game.ItemsOf(player);
        items.Remove(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id);
        ushort raiseId = PutBodySkillGems(player, "Raise Zombie");
        var montregul = new UniqueCatalog(Program.Items).Create("Mon'tregul's Grasp", new Random(7))!;
        items.Put(PlayerInventories.Weapon1, new(201, 0, 0, montregul));

        uint zombieId = await Game.SummonFighter(player, raiseId, Game.SummonData.Find("RaiseZombie")!,
            (100, 100), restoredVariety: "Metadata/Monsters/Zombies/Zombie");
        var zombie = player.Summons.Fighters[zombieId];
        var attack = zombie.Abilities.First(a => !a.Attack.Spell && a.Attack.Damage.MaxTotal > 0);

        // Corpse source monster killed by zombie's hit explodes for 50% of MaxLife (1000 -> 500 base fire damage) onto bystander,
        // kills low-HP chainVictim, and does NOT chain a second explosion from chainVictim's explosion death.
        uint targetId = player.TakeObjectId();
        var target = SpawnHostile(player, targetId, (101, 100), maxLife: 1000, life: 1);
        uint bystanderId = player.TakeObjectId();
        var bystander = SpawnHostile(player, bystanderId, (105, 100), maxLife: 2000, life: 2000);
        uint chainVictimId = player.TakeObjectId();
        var chainVictim = SpawnHostile(player, chainVictimId, (106, 100), maxLife: 1000, life: 100);

        player.Minions.OutputQueue.Clear();
        Assert.True(Game.GolemHitLocked(player, zombie, attack, targetId, target, now: 5.0));

        Assert.True(target.CorpseConsumed);
        Assert.DoesNotContain(targetId, player.Area.Monsters.Keys);
        Assert.Contains(player.Minions.OutputQueue,
            o => o.RemovesObject && o.WorldId == targetId && o.Log == "zombie kill explosion removes corpse");
        var explosionArt = Assert.Single(player.Minions.OutputQueue,
            o => o.CreatesObject && o.Log == "zombie fire kill explosion");
        Assert.Contains(player.Minions.OutputQueue,
            o => !o.CreatesObject && !o.RemovesObject && o.WorldId == explosionArt.WorldId
                && o.Packet.AsSpan().SequenceEqual(InstanceProtocol.EnterWakeZone(explosionArt.WorldId!.Value)));
        Assert.Equal(1500u, bystander.Life);
        Assert.Equal(0u, chainVictim.Life);
        Assert.False(chainVictim.CorpseConsumed);
        Assert.Contains(chainVictimId, player.Area.Monsters.Keys);

        // An already-consumed/shattered corpse killed by a zombie's hit does not explode onto nearby enemies.
        uint consumedTargetId = player.TakeObjectId();
        var consumedTarget = SpawnHostile(player, consumedTargetId, (101, 100), maxLife: 1000, life: 1);
        consumedTarget.CorpseConsumed = true;
        uint bystanderBeforeConsumed = bystander.Life;
        Assert.True(Game.GolemHitLocked(player, zombie, attack, consumedTargetId, consumedTarget, now: 5.5));
        Assert.Equal(bystanderBeforeConsumed, bystander.Life);

        // Brutality Support prohibits zombie fire explosion damage (0 dealt to bystander), while still consuming the killed enemy.
        ushort brutalRaiseId = PutBodySkillGems(player, "Raise Zombie", "Brutality Support");
        var zombieBrutal = player.Summons.Fighters[await Game.SummonFighter(player, brutalRaiseId,
            Game.SummonData.Find("RaiseZombie")!, (100, 100), restoredVariety: "Metadata/Monsters/Zombies/Zombie")];
        var brutalAttack = zombieBrutal.Abilities.First(a => !a.Attack.Spell && a.Attack.Damage.MaxTotal > 0);

        uint target2Id = player.TakeObjectId();
        var target2 = SpawnHostile(player, target2Id, (101, 100), maxLife: 1000, life: 1);
        uint bystanderBefore = bystander.Life;
        Assert.True(Game.GolemHitLocked(player, zombieBrutal, brutalAttack, target2Id, target2, now: 6.0));
        Assert.True(target2.CorpseConsumed);
        Assert.Equal(bystanderBefore, bystander.Life);

        // Minion Instability (secondary explosion) killing an enemy does NOT trigger Mon'tregul's Grasp kill explosion.
        ushort miRaiseId = PutBodySkillGems(player, "Raise Zombie");
        player.Character.Passives.Add(18663); // Minion Instability
        var miSkill = Game.SupportedSkill(player, miRaiseId)!;
        Assert.True(miSkill.RequirementsMet, "miSkill RequirementsMet");
        Assert.Null(Game.GemSkillInstanceDisabled(player, miSkill.Effect, miRaiseId));
        Assert.True(Game.SummonLimit(player, Game.SummonData.Find("RaiseZombie")!, miSkill) >= 1, "miSkill SummonLimit");
        uint miZombieId = await Game.SummonFighter(player, miRaiseId,
            Game.SummonData.Find("RaiseZombie")!, (100, 100), restoredVariety: "Metadata/Monsters/Zombies/Zombie");
        Assert.NotEqual(0u, miZombieId);
        var miZombie = player.Summons.Fighters[miZombieId];
        await Game.SummonsTick(player, 7.0);
        miZombie.Monster.Walk.Teleport(100, 100, 7.0);
        miZombie.Monster.Life = 1;
        uint instTargetId = player.TakeObjectId();
        var instTarget = SpawnHostile(player, instTargetId, (112, 100), maxLife: 1000, life: 1);
        uint farFromZombieId = player.TakeObjectId();
        var farFromZombie = SpawnHostile(player, farFromZombieId, (128, 100), maxLife: 2000, life: 2000);
        Assert.True(Game.MinionInstabilityLocked(player, miZombie, 7.0));
        Assert.Equal(0u, instTarget.Life);
        Assert.False(instTarget.CorpseConsumed);
        Assert.Contains(instTargetId, player.Area.Monsters.Keys);
        Assert.Equal(2000u, farFromZombie.Life);
    }
}
