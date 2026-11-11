using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task CatCryFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "CatCry" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var priorContext = Game.AreaUpdateContext.Value;
        var updates = new PlayerUpdatesState(lifetime, schedule);
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 1, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "cat-cry", ClassRow = 1,
            League = "Standard", LeagueForm = "Standard", Instance = new(), PlayerLife = 1,
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("cat-cry", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0),
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Helmet, PlayerInventories.BodyArmour,
                         PlayerInventories.Weapon1, PlayerInventories.Boots, 5, 6, 7 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            var ring = uniques.Create("Le Heup of All", new Random(504))!;
            inventory.Add(6, 0, 0, ring);
            inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Farrul's Bite", new Random(504))!);
            var boots = inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Farrul's Chase", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life; conn.PlayerMana = conn.Pools.Mana;
            conn.Walk.Teleport(100, 100, schedule.At);
            var cat = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheCat");
            var cry = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == Game.CatStealthCryEffect);
            Assert.True(Game.SupportedSkill(conn, cry.Id).RequirementsMet);
            Assert.Equal(20, cry.Level);
            Assert.True(await Game.ToggleAura("cat-cry", wire.Stream, wire.Ciphers, conn, cat.Id, cat.Effect,
                cat.Name, Game.SupportedReservationCost(conn, cat.Id)!.Value, cat.Level, cat.Quality));
            Assert.Empty(Game.PendingCatCryLosses(conn));
            int action = conn.ActionCount;
            uint mana = conn.PlayerMana;
            bool sideEffect = false;
            Assert.False(Game.TryBeginSkillCooldownUse(conn, cry.Id, cry.Effect,
                Game.CooldownFor(conn, cry.Effect, cry.Level, 0), true, () => sideEffect = true, out _));
            Assert.False(sideEffect);
            Assert.False(await Game.CastGemSkill("cat-cry", wire.Stream, wire.Ciphers, conn, cry.Id, (100, 100), (100, 100)));
            Assert.Equal(action, conn.ActionCount); Assert.Equal(mana, conn.PlayerMana);
            conn.Monsters[0x504C] = new("cry rare", new(105, 100, 1), 100, 8, 100000) { Rarity = MonsterRarity.Rare };
            conn.Monsters[0x504D] = new("cry distant", new(180, 100, 1), 100, 8, 100000);
            var minion = new SpawnedMonster("cry minion", new(110, 100, 1), 100, 8, 1000)
                { Hostile = false, OwnerId = Program.PlayerId };
            conn.Monsters[0x504E] = minion;
            var golem = new GolemState.Golem(0x504E, minion,
                new(0, "", "Stone", "", 0, [], false, false, false), 1);
            var peer = new ClientState(template.Layout, 1, 1)
            {
                Instance = new(conn.Instance.Presence), PlayerLife = 1, PlayerSent = true,
                PlayerUpdatesState = updates, LeagueModule = template.LeagueModule,
            };
            Assert.Equal(0, Game.IntimidatingCryMovement(peer));
            conn.Instance.PublishPlayer(new(peer.PeerId, 0, null!, 110, 100, 1, peer));
            int withoutCry = conn.Pools.MovementVelocityPct;
            schedule.At = conn.Reservations.CatPhaseDeadline;
            Game.CatAspectReconcileLocked(conn, schedule.At);
            var loss = Assert.Single(Game.PendingCatCryLosses(conn));
            Game.CatAspectReconcileLocked(conn, schedule.At);
            Assert.Single(Game.PendingCatCryLosses(conn));
            var packet = Game.AdmitCatCryLossLocked(conn, loss)!;
            Assert.NotNull(packet);
            Assert.Equal(InstanceProtocol.InstantActionId, BinaryPrimitives.ReadUInt16BigEndian(packet));
            Assert.Equal(cry.Id, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
            Assert.Equal(0x80, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)));
            Assert.True(conn.PlayerMana < mana);
            Assert.Equal(action, conn.ActionCount);
            Assert.Equal(6, Game.IntimidatingCryMovement(conn));
            Assert.Equal(6, Game.IntimidatingCryMovement(peer));
            // The release grants a timed buff, so walking out of the cry radius does not revoke it.
            conn.Instance.PublishPlayer(new(peer.PeerId, 0, null!, 500, 100, 1, peer));
            Assert.Equal(6, Game.IntimidatingCryMovement(peer));
            // Boots lose their separate 15% Stealth speed on this transition, then gain the real 6% warcry buff.
            var bootStats = ItemModStats.Sum([boots.Item], Program.Items.Mods);
            int stealthSpeed = bootStats.GetValueOrDefault("movement_speed_+%_while_you_have_cats_stealth");
            conn.Pools = Game.PoolsFor(conn, 100);
            Assert.Equal(withoutCry - stealthSpeed + 6, conn.Pools.MovementVelocityPct);
            Assert.Equal(2, Game.IntimidatingCryExerts(conn));
            Assert.True(conn.Monsters[0x504C].TauntedUntil > schedule.At);
            Assert.True(conn.Monsters[0x504C].IntimidatedUntil > schedule.At);
            Assert.True(conn.Monsters[0x504D].TauntedUntil <= schedule.At);
            Assert.Equal(6, Game.MinionAuraStats(conn, golem)["base_movement_velocity_+%"]);
            Assert.Null(Game.AdmitCatCryLossLocked(conn, loss));
            Assert.False(Game.ConsumeIntimidatingCryExertLocked(conn, eligible: false));
            Assert.Equal(2, Game.IntimidatingCryExerts(conn));
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            Assert.True(Game.ConsumeIntimidatingCryExertLocked(conn, eligible: true));
            Assert.Equal(200, Game.WithIntimidatingCryExert(new(DoubleDamageChance: 100), true).RollOutgoingHit(hit, new Random(504)).MinTotal);
            Assert.Equal(300, Game.WithIntimidatingCryExert(new(TripleDamageChance: 100), true).RollOutgoingHit(hit, new Random(504)).MinTotal);
            Assert.True(Game.ConsumeIntimidatingCryExertLocked(conn, eligible: true));
            Assert.False(Game.ConsumeIntimidatingCryExertLocked(conn, eligible: true));
            schedule.At += 3.401;
            Assert.Equal(0, Game.IntimidatingCryMovement(conn));
            Assert.Equal(0, Game.IntimidatingCryMovement(peer));
            Assert.Equal(0, Game.MinionAuraStats(conn, golem)["base_movement_velocity_+%"]);
            // A retained loss cannot adopt boots removed and then reinserted.
            schedule.At = conn.Reservations.CatPhaseDeadline;
            Game.CatAspectReconcileLocked(conn, schedule.At);
            schedule.At = conn.Reservations.CatPhaseDeadline;
            Game.CatAspectReconcileLocked(conn, schedule.At);
            var stale = Game.PendingCatCryLosses(conn).Last();
            Assert.False(Game.CatCryLossCurrentLocked(conn, stale with { Epoch = conn.ProjectileEpoch + 1 }));
            Assert.False(Game.CatCryLossCurrentLocked(conn, stale with { Rules = new object() }));
            uint beforeUnpaid = conn.PlayerMana;
            conn.PlayerMana = 0;
            Assert.Null(Game.AdmitCatCryLossLocked(conn, stale));
            Assert.False(stale.Consumed);
            conn.PlayerMana = beforeUnpaid;
            inventory.Remove(PlayerInventories.Boots, boots.Id);
            Assert.False(Game.CatCryLossCurrentLocked(conn, stale));
            inventory.Add(PlayerInventories.Boots, 0, 0, boots.Item);
            Assert.False(Game.CatCryLossCurrentLocked(conn, stale));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = priorContext;
            PlayerInventories.Forget(character);
        }
    }
}
