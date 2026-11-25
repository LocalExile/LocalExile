using GameServer.Core.Projectiles;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SecondarySkillExecutionTests
{
    [Fact]
    public async Task Paid_totem_uses_taunt_and_accepted_contacts_grant_owner_leech_and_surge()
    {
        await A_paid_Arc_totem_grants_its_owner_Arcane_Surge_only_after_contact();
        await Trolltimber_Spire_leeches_life_for_the_owner_from_paid_Arc_totem_hits();
        await Native_totem_summon_taunt_selects_the_placed_totem_for_nearby_enemies();
    }

    static async Task A_paid_Arc_totem_grants_its_owner_Arcane_Surge_only_after_contact()
    {
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("That Which Was Taken", new Random(17)));
        var mod = Program.Items.Mods.ById("AnimalCharmArcaneSurgeOnHit")!;
        foreach (bool equipped in new[] { false, true })
        {
            var clock = new TotemClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.Connection.LockstepClient = false;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.At);
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            player.Character.Passives.Add(31703);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel with
            { Mods = equipped ? [new(ModSection.Explicit, mod.Hash, [1])] : [] });
            ushort arc = Assert.Single(fixture.Equip(("Arc", 18), ("Spell Totem Support", 18)));
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, arc, (100, 100), (106, 100)));
                Assert.True(player.PlayerMana < mana);
                var totem = Assert.Single(player.Totems.Living(clock.At));
                Game.TickSpellTotemsLocked(player, []);
                Assert.False(player.SupportGems.ArcaneSurgeActiveAt(clock.At));
                const uint id = 910;
                var target = new SpawnedMonster("Arc totem witness", new(116, 100, 0), 0, 0, 10000)
                    { ObjectId = id, Hostile = true };
                player.Area.Monsters.Add(id, target);
                player.Area.Grid.Set(new(id), (116, 100));
                Game.TickSpellTotemsLocked(player, []);
                Assert.True(totem.NextCast > clock.At);
                clock.At = totem.NextCast;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                Assert.Equal(equipped, player.SupportGems.ArcaneSurgeActiveAt(clock.At));
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    static async Task Trolltimber_Spire_leeches_life_for_the_owner_from_paid_Arc_totem_hits()
    {
        var ordinary = await ArcTotemContact(null, enemy: true);
        Assert.True(ordinary.Damage > 0);
        Assert.Equal(0, ordinary.Recovered);
        void Equip(Player player)
        {
            var shield = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Trolltimber Spire", new Random(4)));
            var mod = Program.Items.Mods.ById("TotemLeechLifeToYouUnique__1")!;
            Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0,
                shield with { Mods = [new(ModSection.Explicit, mod.Hash, [50])] });
        }
        Assert.Equal(0, (await ArcTotemContact(Equip, enemy: false)).Recovered);
        var live = await ArcTotemContact(Equip, enemy: true);
        Assert.True(live.Damage > 0);
        Assert.InRange(live.Recovered, live.Damage * .005 - .1, live.Damage * .005 + .1);
    }

    static async Task Native_totem_summon_taunt_selects_the_placed_totem_for_nearby_enemies()
    {
        foreach (bool equipped in new[] { false, true })
        {
            var clock = new TotemClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Connection.LockstepClient = false;
            player.Walk.Teleport(100, 100, clock.At);
            ushort arc = Assert.Single(fixture.Equip(("Arc", 18), ("Spell Totem Support", 18)));
            var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("That Which Was Taken", new Random(17)));
            var mod = Program.Items.Mods.ById("AnimalCharmTotemTauntEnemiesWhenSummoned")!;
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            player.Character.Passives.Add(31703);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel with
            { Mods = equipped ? [new(ModSection.Explicit, mod.Hash, [2])] : [] });
            SpawnedMonster Add(uint id, (int X, int Y) at)
            {
                var monster = new SpawnedMonster("taunt witness", new(at.X, at.Y, 0), 0, 0, 10000)
                    { ObjectId = id, Hostile = true };
                player.Area.Monsters[id] = monster;
                player.Area.Grid.Set(new(id), at);
                return monster;
            }
            var near = Add(919, (115, 100));
            var distant = Add(920, (160, 100));
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, arc, (100, 100), (106, 100)));
                Assert.True(player.PlayerMana < mana);
                var totem = Assert.Single(player.Totems.Living(clock.At));
                var taunt = Game.DecoyTarget(near, clock.At);
                Assert.Equal(equipped, taunt.HasValue);
                if (taunt is { } active) Assert.Same(totem, active.Source);
                Assert.Null(Game.DecoyTarget(distant, clock.At));
                Assert.Null(Game.DecoyTarget(near, clock.At + 2.01));
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    static async Task<(uint Damage, double Recovered)> ArcTotemContact(Action<Player>? setup, bool enemy)
    {
        var clock = new TotemClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Connection.LockstepClient = false;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var items = Game.ItemsOf(player);
        uint weapon = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        items.Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        ushort arc = Assert.Single(fixture.Equip(("Arc", 18), ("Spell Totem Support", 18)));
        setup?.Invoke(player);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.SpellMinimumAddedLightningDamage] = values[Stat.SpellMaximumAddedLightningDamage] = 2500;
            }
            return values;
        });
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.RecomputePoolsAndSpeed(player);
            player.PlayerLife = player.PlayerMaxLife / 2;
            Game.ConfigureLeech(player, Game.CharacterPlayerStats(player).GetValueOrDefault, onLowLife: true);
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, arc, (100, 100), (106, 100)));
            Assert.True(player.PlayerMana < mana);
            var totem = Assert.Single(player.Totems.Living(clock.At));
            if (!enemy) return (0, Game.TakeLeechRecovery(player, clock.At + 1, false, true, true).Life);
            const uint id = 918;
            var target = new SpawnedMonster("Arc totem contact", new(116, 100, 0), 0, 0, 100000)
                { ObjectId = id, Hostile = true };
            player.Area.Monsters.Add(id, target);
            player.Area.Grid.Set(new(id), (116, 100));
            Game.TickSpellTotemsLocked(player, []);
            Assert.True(totem.NextCast > clock.At);
            clock.At = totem.NextCast;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            return (target.MaxLife - target.Life, Game.TakeLeechRecovery(player, clock.At + 3, false, true, true).Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class TotemClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    static ushort Equip(MinionUtilityTests.Fixture f, params string[] names)
    {
        f.Equip(names.Select(n => (n,4)).ToArray());
        var host=Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,i=>i with {Links=[(byte)names.Length]});
        return Game.GemSkillEffects(f.Player).Single().Id;
    }

    static IDisposable OverrideStat(Player player, uint stat, int value) =>
        GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
        {
            if (!ReferenceEquals(p, player)) return sources;
            var copy = new Dictionary<uint, int>(sources) { [stat] = value };
            return copy;
        });

    [Fact]
    public async Task Charged_supports_contribute_their_distinct_critical_stats_to_proxy_projectiles()
    {
        using var f = new MinionUtilityTests.Fixture();
        var session = Game.ProjectilesFor(f.Player);
        try
        {
            using var vsBlinded = OverrideStat(f.Player, Game.VsBlindedCriticalStat, 50);
            Assert.Equal(Capture("Blastchain Mine Support").CriticalChance,
                Capture("Blastchain Mine Support", "Charged Mines Support").CriticalChance);
            Assert.Equal(Capture("Trap Support").CriticalMultiplier,
                Capture("Trap Support", "Charged Traps Support").CriticalMultiplier);
            GrantPowerCharges(f.Player);
            var ordinaryMine = Capture("Blastchain Mine Support");
            var chargedMine = Capture("Blastchain Mine Support", "Charged Mines Support");
            var ordinaryTrap = Capture("Trap Support");
            var chargedTrap = Capture("Trap Support", "Charged Traps Support");
            int baseChance = Program.Skills.SpellCritChance(chargedMine.Effect, chargedMine.Level)!.Value;
            Assert.Equal(ordinaryMine.CriticalChance + baseChance * 40 / 100, chargedMine.CriticalChance);
            Assert.Equal(ordinaryMine.VsBlindedCriticalChance + baseChance * 40 / 100, chargedMine.VsBlindedCriticalChance);
            Assert.Equal(ordinaryMine.CriticalMultiplier, chargedMine.CriticalMultiplier);
            Assert.Equal(ordinaryTrap.CriticalChance, chargedTrap.CriticalChance);
            Assert.Equal(ordinaryTrap.CriticalMultiplier + 30, chargedTrap.CriticalMultiplier);
        }
        finally { session.Invalidate(); await session.OutputCompleted; }

        ProjectileOffense Capture(params string[] supports)
        {
            Equip(f, ["Fireball", .. supports]);
            var gem = Game.GemSkillEffects(f.Player).Single();
            var use = Game.CaptureGemProjectile(f.Player, session, gem.Id, gem, (120, 100), new(0), from: (100, 100));
            return Assert.IsType<ProjectileOffense>(use.Payload);
        }
    }

    [Fact]
    public void Direct_placed_hits_roll_critical_chance_and_apply_the_trap_multiplier()
    {
        var critical = new FixedCriticalRoll();
        using var f = new MinionUtilityTests.Fixture(critical);
        GrantPowerCharges(f.Player);
        Assert.Equal(850u, Detonate("Fire Trap", false));
        Assert.Equal(820u, Detonate("Fire Trap", false, "Charged Traps Support"));

        ushort ordinaryMine = Equip(f, "Stormblast Mine");
        critical.Roll = Game.SupportedCriticalChance(f.Player, ordinaryMine);
        Assert.Equal(900u, Detonate("Stormblast Mine", true));
        Assert.Equal(850u, Detonate("Stormblast Mine", true, "Charged Mines Support"));

        using var noCrit = OverrideStat(f.Player, Stat.GlobalCannotCrit, 1);
        critical.Roll = 0;
        Assert.Equal(900u, Detonate("Fire Trap", false, "Charged Traps Support"));

        uint Detonate(string name, bool mine, params string[] supports)
        {
            // Each comparison starts on fresh ground. Fire Trap adds damage against a burning target.
            f.Player.Placed.Grounds.Clear();
            ushort source = Equip(f, [name, .. supports]);
            var gem = Game.SupportedSkill(f.Player, source);
            var placed = new PlacedSkills.Placed(777, source, gem.Effect, (100, 100), mine, 0, 10,
                DamagePacket.Of(DamageType.Fire, 100, 100), gem.Stats, 0, 0, false, 0);
            f.Player.Area.Monsters[900] = new("victim", new(102, 100, 0), 0, 0, 1000) { Hostile = true };
            PlacedHit(f, placed);
            return f.Player.Area.Monsters[900].Life;
        }
    }

    static void GrantPowerCharges(Player player)
    {
        var charges = Game.ChargesOf(player);
        charges.Admit(0, [new ChargeEvent.Grant(ChargeType.Power, 2, "test")], _ => 0, () => default,
            alive: true, new Random(1));
        Assert.Equal(2, charges.Effective(ChargeType.Power));
        player.Pools = Game.PoolsFor(player, 100);
    }

    sealed class FixedCriticalRoll : Random
    {
        public int Roll { get; set; }
        public override int Next(int maxValue) => Roll;
    }

    static void PlacedHit(MinionUtilityTests.Fixture f, PlacedSkills.Placed placed)
    {
        var method = typeof(Game).GetMethod("PlacedDamageLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var records = Activator.CreateInstance(method.GetParameters()[^1].ParameterType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [f.Player, null], null);
        method.Invoke(null, [f.Player, placed, 1d, default(KeyValuePair<uint, SpawnedMonster>), records]);
    }

    [Fact]
    public void Bear_trap_hits_one_nearby_enemy_without_splashing_other_enemies_or_allies()
    {
        using var f = new MinionUtilityTests.Fixture(new FixedCriticalRoll { Roll = 9999 });
        ushort source = Equip(f, "Bear Trap"); var gem = Game.SupportedSkill(f.Player, source);
        var placed = new PlacedSkills.Placed(777, source, gem.Effect, (100, 100), false, 0, 10,
            DamagePacket.Of(DamageType.Physical, 100, 100), gem.Stats, 0, 0, false, 0);
        foreach (var (id, x, hostile) in new[] { (900u, 104, true), (901u, 102, true), (902u, 103, false), (903u, 140, true) })
            f.Player.Area.Monsters[id] = new("victim", new(x, 100, 0), 0, 0, 1000) { Hostile = hostile };
        PlacedHit(f, placed);
        Assert.Equal(900u, f.Player.Area.Monsters[901].Life);
        Assert.Equal(1000u, f.Player.Area.Monsters[900].Life); Assert.Equal(1000u, f.Player.Area.Monsters[902].Life);
        Assert.Equal(1000u, f.Player.Area.Monsters[903].Life);
        var victim = f.Player.Area.Monsters[901];
        Assert.True(victim.Walk.MovementBlocked(2.49));
        Assert.False(victim.Walk.MovementBlocked(2.5));
        Assert.Equal(1, Game.MonsterActionFactor(victim, 1.1));
        Assert.False(victim.Invulnerable);
        victim.Walk.Order(120, 100, 1.5);
        Assert.Equal((102, 100), victim.Walk.Destination);
        Assert.Equal(.2, Game.BearTrapWalkFactor(victim, 2.5), 8);
        Assert.Equal(.6, Game.BearTrapWalkFactor(victim, 4), 8);
        Assert.Equal(1, Game.BearTrapWalkFactor(victim, 5.5));
        Assert.Equal(5, Game.BearTrapRootSeconds(1000, 1000));
        Assert.Equal(.15, Game.BearTrapRootSeconds(10, 1000), 8);
        var packet = DamagePacket.Of(DamageType.Fire, 100, 100);
        victim.TakenFromPlayer = (20, 0, 0);
        Assert.Equal(120, Game.MonsterTakesMitigated(victim, packet, false, 2));
        Assert.Equal(145, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 2));
        Assert.Equal(120, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 5.5));
        victim.Ignites.Add(1, 1, 6);
        Game.InflictedLocked(f.Player, victim, Game.AilmentType.Ignite, 7);
        Game.KeepPerAilmentTypeTakenLocked(f.Player, victim, 10);
        Assert.Equal(130, Game.MonsterTakesMitigated(victim, packet, false, 2));
        Assert.Equal(155, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 2));
        Assert.Equal(130, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 5.5));
        var buffs = Game.MonsterAilmentChangesLocked(f.Player, 1.1);
        Assert.Contains(buffs, p => p.Log == "Bear Trap debuff");
        // The client walks by movement_velocity_+%, so the slow is set on Area.Stats and tracked on the monster.
        Assert.True(victim.MaimedMovementShown);
        Assert.Equal(-80, victim.MaimedMovementShownPct);
        Assert.DoesNotContain(Game.MonsterAilmentChangesLocked(f.Player, 1.2), p => p.Log == "Bear Trap debuff");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Player, 2.5), p => p.Log == "Bear Trap debuff changes");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Player, 5.5), p => p.Log == "Bear Trap debuff changes");
        Assert.Equal(1, victim.Ailments.WalkFactor);
        var immune = new SpawnedMonster("movement floor", new(100, 100, 20), 0, 0, 1000)
            { EncounterStats = new Dictionary<uint, int> { [Stat.MovementSpeedCannotBeReducedBelowBase] = 1 } };
        Game.BearTrapOnHitLocked(immune, 100, 1, _ => 0, Own);
        Assert.False(immune.Walk.MovementBlocked(1.1));
        Assert.Equal(1, Game.BearTrapWalkFactor(immune, 1.1));
        int Own(uint name) => gem.Stats.GetValueOrDefault(name);
    }
}
