using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static void SiphoningFixture(Player template, ModuleLifetime lifetime)
    {
        string character = "Siphoning" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var priorContext = Game.AreaUpdateContext.Value;
        var updates = new PlayerUpdatesState(lifetime, schedule);
        Game.AreaUpdateContext.Value = updates;
        var player = new Player(template.Layout, 1, 1)
        {
            Character = { Name = character, Id = Guid.NewGuid(), Account = "siphoning", ClassRow = 1, League = "Standard", LeagueForm = "Standard", Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            PlayerLife = 1,
            Connection = { PlayerSent = true, PlayerObjectCreated = true },
            PlayerUpdatesState = updates,
            LeagueModule = template.LeagueModule,
            Pools = new(100, 1, 1, 0, 0)
        };
        try
        {
            var inventory = Game.ItemsOf(player);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, 5, 6, 7 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
            ItemMod Mod(string id)
            {
                var mod = Program.Items.Mods.ById(id)!;
                return new(ModSection.Explicit, mod.Hash, [.. mod.Stats.Select(s => s.Min)]);
            }
            // Retain the original minimum-roll diagnostic's exact lifecycle arithmetic. The now-available canonical
            // factory source is also bound below, with its original rolled values and qualification.
            var item = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Staves/Staff1")!) with
            {
                Influences = ItemInfluence.Elder | ItemInfluence.Shaper,
                Mods = [Mod("SiphoningChargeOnSkillUseUnique__1"), Mod("MaximumSiphoningChargePerElderOrShaperItemUnique__1"),
                    Mod("PhysicalDamageToAttacksPerSiphoningChargeUnique__1"), Mod("LifeLeechPerSiphoningChargeUnique__1"),
                    Mod("NonChaosDamageAddedAsChaosPerSiphoningChargeUnique__1"), Mod("AdditionalPhysicalDamageReductionPerSiphoningChargeUnique__1"),
                    Mod("DamageTakenPerSiphoningChargeOnSkillUseUnique__1")],
            };
            var staff = inventory.Add(PlayerInventories.Weapon1, 0, 0, item);
            player.Pools = Game.PoolsFor(player, 100);
            player.PlayerLife = player.Pools.Life; player.PlayerMana = player.Pools.Mana;
            int Stat(uint n) => n is var key ? Game.CharacterPlayerStats(player).GetValueOrDefault(key) : 0;
            Assert.Equal(1, Stat(global::Stat.VirtualMaximumSiphoningCharges)); // A dual-influenced item counts once.
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Game.SiphoningSkillUsedLocked(player, 100, new AspectRoll(99));
            Assert.Equal(0, player.Siphoning.Held);
            Game.SiphoningSkillUsedLocked(player, 100, new AspectRoll(0));
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges));
            Assert.Equal(110, player.Siphoning.Until);
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                Assert.Equal(20, added[DamageType.Physical].Min);
                Assert.Equal(26, added[DamageType.Physical].Max);
            }
            var final = Game.CharacterPlayerStats(player);
            Assert.Equal(4, DamagePacket.Of(DamageType.Physical, 100, 100)
                .Convert(DamageStats.Conversion(final, Program.Stats, attack: true))[DamageType.Chaos].Min);
            Assert.Equal(20, Game.HitLeechSources(final, attack: true).Life[DamageType.Physical].Min);
            Assert.Equal(2, Game.PlayerDamageReduction(player, final).Physical);
            Assert.Equal(0, ChargeEffects.Reduction(n => final.GetValueOrDefault(n)).Physical);
            uint before = player.PlayerLife;
            schedule.At = 102;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(player, schedule.At, final));
            Assert.Equal(before - 300, player.PlayerLife);
            schedule.At = 104.1;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(player, schedule.At, final));
            schedule.At = 105;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(player, schedule.At, final));
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges)); // Charges outlive the four-second degeneration window.
            Game.SiphoningSkillUsedLocked(player, 105, new AspectRoll(99));
            Assert.True(Game.SiphoningDegenerationActive(player)); // A failed gain still refreshes skill recency.
            uint recoveryKey = global::Stat.LifeRecoveryPerMinute;
            Assert.Equal(InstanceEntities.StatsUpdate(player.Id, [(recoveryKey, 0)]),
                Game.PlayerStatsUpdateAtGate(player, [(recoveryKey, 600)]));
            player.EnergyShield.SetMaximum(1000); player.EnergyShield.Recover(1000);
            schedule.At = 106;
            uint life = player.PlayerLife;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(player, 106, final));
            Assert.Equal(life, player.PlayerLife);
            Assert.Equal(850u, player.EnergyShield.Current); // Hit-only reduction and armour do not reduce this DoT.
            var ring = inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!)
                with { Influences = ItemInfluence.Elder });
            Assert.Equal(2, Stat(global::Stat.VirtualMaximumSiphoningCharges));
            Game.SiphoningSkillUsedLocked(player, 106, new AspectRoll(0));
            Assert.Equal(2, Stat(global::Stat.CurrentSiphoningCharges));
            inventory.Remove(6, ring.Id);
            Assert.Equal(1, Stat(global::Stat.VirtualMaximumSiphoningCharges));
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges));
            schedule.At = 116;
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Assert.False(Game.SiphoningDegenerationActive(player));
            Assert.Equal(InstanceEntities.StatsUpdate(player.Id, [(recoveryKey, 600)]),
                Game.PlayerStatsUpdateAtGate(player, [(recoveryKey, 600)]));
            Game.ChargesOnDeath(player); player.PlayerLife = player.Pools.Life;
            Game.SiphoningSkillUsedLocked(player, 116, new AspectRoll(0));
            var owner = Assert.IsType<ResourceEffectsState>(player.ResourceEffectsState);
            owner.Freeze(116);
            player.LeavingArea = true; player.ProjectileEpoch++;
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            owner.Resume(116); player.LeavingArea = false;
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges)); // A refused transition cannot erase the frozen charge.
            schedule.At = 117;
            owner.Freeze(117);
            Game.CarryResourceEffects(player, player.Ticket);
            Game.RestoreResourceEffects(player);
            schedule.At = 200;
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges)); // Loading cannot consume remaining duration.
            Game.ActivateResourceEffects(player, 200);
            Assert.Equal(209, player.Siphoning.Until);
            Assert.Equal(203, player.Siphoning.UsedUntil);
            player.EnergyShield.Empty();
            before = player.PlayerLife;
            schedule.At = 201;
            double manaCarry = 0;
            Assert.True(Game.CompleteResourceRecoveryLocked(player, 200, 201, final, 0,
                player.PlayerMana, player.Pools.Mana, ref manaCarry));
            Assert.Equal(before - 300, player.PlayerLife); // One pre-transfer and one post-loading second; no loading damage.
            schedule.At = 203.5;
            Assert.Equal(300, Game.AdvanceSiphoningDamageLocked(player, schedule.At, final));
            Assert.False(Game.SiphoningDegenerationActive(player));
            player.ProjectileEpoch++;
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Assert.False(Game.SiphoningDegenerationActive(player));
            Game.SiphoningSkillUsedLocked(player, 203.5, new AspectRoll(0));
            Game.ChargesOnDeath(player);
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Game.SiphoningSkillUsedLocked(player, 203.5, new AspectRoll(0));
            player.Siphoning.Rules = new object();
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Game.SiphoningSkillUsedLocked(player, 203.5, new AspectRoll(0));
            inventory.Remove(PlayerInventories.Weapon1, staff.Id);
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Assert.Equal(0, Stat(global::Stat.VirtualMaximumSiphoningCharges));
            schedule.At = 204;
            Assert.Equal(0, Game.AdvanceSiphoningDamageLocked(player, 204, Game.CharacterPlayerStats(player)));
            inventory.Add(PlayerInventories.Weapon1, 0, 0, item);
            Game.SiphoningSkillUsedLocked(player, 204, new AspectRoll(0));
            player.PlayerLife = 1; player.EnergyShield.Empty();
            schedule.At = 205;
            Assert.False(Game.CompleteResourceRecoveryLocked(player, 204, 205, Game.CharacterPlayerStats(player),
                0, player.PlayerMana, player.Pools.Mana, ref manaCarry));
            Assert.Equal(0u, player.PlayerLife);
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            Assert.False(Game.SiphoningDegenerationActive(player));

            foreach (var equipped in inventory.Items(PlayerInventories.Weapon1)) inventory.Remove(PlayerInventories.Weapon1, equipped.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(PlayerInventories.Belt, 0, 0, uniques.Create("Perandus Blazon", new Random(504))!);
            var canonical = uniques.Create("Disintegrator", new Random(504))!;
            Assert.NotNull(canonical); Assert.Equal("Metadata/Items/Weapons/TwoHandWeapons/Staves/Staff17", Program.Items.ByHash(canonical.TypeHash)!.Id);
            // A legal diagnostic source state: this dual-influenced item contributes one maximum charge.
            var canonicalStaff = inventory.Add(PlayerInventories.Weapon1, 0, 0, canonical with { Influences = ItemInfluence.Elder | ItemInfluence.Shaper });
            player.Pools = Game.PoolsFor(player, 100); player.PlayerLife = player.Pools.Life; schedule.At = 300;
            Assert.False(Game.IsInactive(player, canonicalStaff.Id));
            int physicalMin = Stat(global::Stat.AttackAndSpellMinimumAddedPhysicalDamagePerSiphoningCharge);
            int physicalMax = Stat(global::Stat.AttackAndSpellMaximumAddedPhysicalDamagePerSiphoningCharge);
            Assert.InRange(physicalMin, 20, 24); Assert.InRange(physicalMax, 26, 32);
            Game.SiphoningSkillUsedLocked(player, 300, new AspectRoll(0));
            Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell)[DamageType.Physical];
                Assert.Equal(physicalMin, added.Min); Assert.Equal(physicalMax, added.Max);
            }
            player.EnergyShield.Empty(); before = player.PlayerLife; schedule.At = 301;
            Assert.Equal(150, Game.AdvanceSiphoningDamageLocked(player, 301, Game.CharacterPlayerStats(player)));
            Assert.Equal(before - 150, player.PlayerLife);
            player.Character.Experience = CharacterStats.ExperienceFor(1); player.Pools = Game.PoolsFor(player, 1);
            Assert.True(Game.IsInactive(player, canonicalStaff.Id)); Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges));
            player.Character.Experience = CharacterStats.ExperienceFor(100); player.Pools = Game.PoolsFor(player, 100);
            Game.SiphoningSkillUsedLocked(player, 301, new AspectRoll(0)); Assert.Equal(1, Stat(global::Stat.CurrentSiphoningCharges));
            inventory.Remove(PlayerInventories.Weapon1, canonicalStaff.Id);
            Assert.Equal(0, Stat(global::Stat.CurrentSiphoningCharges)); Assert.Equal(0, Stat(global::Stat.VirtualMaximumSiphoningCharges));
        }
        finally
        {
            player.Closed = true;
            Game.AreaUpdateContext.Value = priorContext;
            PlayerInventories.Forget(character);
        }
    }
}
