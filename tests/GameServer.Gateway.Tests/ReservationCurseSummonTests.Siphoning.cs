using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static void SiphoningFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "Siphoning" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var priorContext = Game.AreaUpdateContext.Value;
        var updates = new PlayerUpdatesState(lifetime, schedule);
        Game.AreaUpdateContext.Value = updates;
        var conn = new ClientState(template.Layout, 1, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "siphoning", ClassRow = 1,
            League = "Standard", LeagueForm = "Standard", Instance = new(), PlayerLife = 1,
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Pools = new(100, 1, 1, 0, 0),
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, 5, 6, 7 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
            ItemMod Mod(string id)
            {
                var mod = Program.Items.Mods.ById(id)!;
                return new(ModSection.Explicit, mod.Hash, [.. mod.Stats.Select(s => s.Min)]);
            }
            // Disintegrator's base is absent from this client. Use the actual SQL mod rows on a supported diagnostic
            // staff, without adding a counterfeit Disintegrator factory profile or replacing chance/condition inputs.
            var item = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Staves/Staff1")!) with
            {
                Influences = ItemInfluence.Elder | ItemInfluence.Shaper,
                Mods = [Mod("SiphoningChargeOnSkillUseUnique__1"), Mod("MaximumSiphoningChargePerElderOrShaperItemUnique__1"),
                    Mod("PhysicalDamageToAttacksPerSiphoningChargeUnique__1"), Mod("LifeLeechPerSiphoningChargeUnique__1"),
                    Mod("NonChaosDamageAddedAsChaosPerSiphoningChargeUnique__1"), Mod("AdditionalPhysicalDamageReductionPerSiphoningChargeUnique__1"),
                    Mod("DamageTakenPerSiphoningChargeOnSkillUseUnique__1")],
            };
            var staff = inventory.Add(PlayerInventories.Weapon1, 0, 0, item);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life; conn.PlayerMana = conn.Pools.Mana;
            int Stat(string n) => Program.Stats.Key(n) is uint key ? Game.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
            Assert.Equal(1, Stat("virtual_maximum_siphoning_charges")); // A dual-influenced item counts once.
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Game.SiphoningSkillUsedLocked(conn, 100, new AspectRoll(99));
            Assert.Equal(0, conn.Siphoning.Held);
            Game.SiphoningSkillUsedLocked(conn, 100, new AspectRoll(0));
            Assert.Equal(1, Stat("current_siphoning_charges"));
            Assert.Equal(110, conn.Siphoning.Until);
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                Assert.Equal(20, added[DamageType.Physical].Min);
                Assert.Equal(26, added[DamageType.Physical].Max);
            }
            var final = Game.CharacterPlayerStats(conn);
            Assert.Equal(4, DamagePacket.Of(DamageType.Physical, 100, 100)
                .Convert(DamageStats.Conversion(final, Program.Stats, attack: true))[DamageType.Chaos].Min);
            Assert.Equal(20, Game.HitLeechSources(final, attack: true).Life[DamageType.Physical].Min);
            Assert.Equal(2, Game.PlayerDamageReduction(conn, final).Physical);
            Assert.Equal(0, ChargeEffects.Reduction(n => Program.Stats.Key(n) is uint key ? final.GetValueOrDefault(key) : 0).Physical);
            uint before = conn.PlayerLife;
            schedule.At = 102;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(conn, schedule.At, final));
            Assert.Equal(before - 300, conn.PlayerLife);
            schedule.At = 104.1;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(conn, schedule.At, final));
            schedule.At = 105;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(conn, schedule.At, final));
            Assert.Equal(1, Stat("current_siphoning_charges")); // Charges outlive the four-second degeneration window.
            Game.SiphoningSkillUsedLocked(conn, 105, new AspectRoll(99));
            Assert.True(Game.SiphoningDegenerationActive(conn)); // A failed gain still refreshes skill recency.
            uint recoveryKey = Program.Stats.Key("life_recovery_per_minute")!.Value;
            Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(recoveryKey, 0)]),
                Game.PlayerStatsUpdateAtGate(conn, [(recoveryKey, 600)]));
            conn.EnergyShield.SetMaximum(1000); conn.EnergyShield.Recover(1000);
            schedule.At = 106;
            uint life = conn.PlayerLife;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(conn, 106, final));
            Assert.Equal(life, conn.PlayerLife);
            Assert.Equal(850u, conn.EnergyShield.Current); // Hit-only reduction and armour do not reduce this DoT.
            var ring = inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!)
                with { Influences = ItemInfluence.Elder });
            Assert.Equal(2, Stat("virtual_maximum_siphoning_charges"));
            Game.SiphoningSkillUsedLocked(conn, 106, new AspectRoll(0));
            Assert.Equal(2, Stat("current_siphoning_charges"));
            inventory.Remove(6, ring.Id);
            Assert.Equal(1, Stat("virtual_maximum_siphoning_charges"));
            Assert.Equal(1, Stat("current_siphoning_charges"));
            schedule.At = 116;
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Assert.False(Game.SiphoningDegenerationActive(conn));
            Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(recoveryKey, 600)]),
                Game.PlayerStatsUpdateAtGate(conn, [(recoveryKey, 600)]));
            Game.ChargesOnDeath(conn); conn.PlayerLife = conn.Pools.Life;
            Game.SiphoningSkillUsedLocked(conn, 116, new AspectRoll(0));
            var owner = Assert.IsType<ResourceEffectsState>(conn.ResourceEffectsState);
            owner.Freeze(116);
            conn.LeavingArea = true; conn.ProjectileEpoch++;
            Assert.Equal(0, Stat("current_siphoning_charges"));
            owner.Resume(116); conn.LeavingArea = false;
            Assert.Equal(1, Stat("current_siphoning_charges")); // A refused transition cannot erase the frozen charge.
            schedule.At = 117;
            owner.Freeze(117);
            Game.CarryResourceEffects(conn, conn.Ticket);
            Game.RestoreResourceEffects(conn);
            schedule.At = 200;
            Assert.Equal(1, Stat("current_siphoning_charges")); // Loading cannot consume remaining duration.
            Game.ActivateResourceEffects(conn, 200);
            Assert.Equal(209, conn.Siphoning.Until);
            Assert.Equal(203, conn.Siphoning.UsedUntil);
            conn.EnergyShield.Empty();
            before = conn.PlayerLife;
            schedule.At = 201;
            double manaCarry = 0;
            Assert.True(Game.CompleteResourceRecoveryLocked(conn, 200, 201, final, 0,
                conn.PlayerMana, conn.Pools.Mana, ref manaCarry));
            Assert.Equal(before - 300, conn.PlayerLife); // One pre-transfer and one post-loading second; no loading damage.
            schedule.At = 203.5;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(conn, schedule.At, final));
            Assert.False(Game.SiphoningDegenerationActive(conn));
            conn.ProjectileEpoch++;
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Assert.False(Game.SiphoningDegenerationActive(conn));
            Game.SiphoningSkillUsedLocked(conn, 203.5, new AspectRoll(0));
            Game.ChargesOnDeath(conn);
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Game.SiphoningSkillUsedLocked(conn, 203.5, new AspectRoll(0));
            conn.Siphoning.Rules = new object();
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Game.SiphoningSkillUsedLocked(conn, 203.5, new AspectRoll(0));
            inventory.Remove(PlayerInventories.Weapon1, staff.Id);
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Assert.Equal(0, Stat("virtual_maximum_siphoning_charges"));
            schedule.At = 204;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(conn, 204, Game.CharacterPlayerStats(conn)));
            Assert.Empty(conn.PlayerStats);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, item);
            Game.SiphoningSkillUsedLocked(conn, 204, new AspectRoll(0));
            conn.PlayerLife = 1; conn.EnergyShield.Empty();
            schedule.At = 205;
            Assert.False(Game.CompleteResourceRecoveryLocked(conn, 204, 205, Game.CharacterPlayerStats(conn),
                0, conn.PlayerMana, conn.Pools.Mana, ref manaCarry));
            Assert.Equal(0u, conn.PlayerLife);
            Assert.Equal(0, Stat("current_siphoning_charges"));
            Assert.False(Game.SiphoningDegenerationActive(conn));
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = priorContext;
            PlayerInventories.Forget(character);
        }
    }
}
