using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class NightbladeAndBlessingsTests
{
    [Fact]
    public void Nightblade_grants_elusive_on_crit_and_scales_base_crit_and_decaying_multiplier()
    {
        using var f = new Fixture(withSession: false);
        var c = f.Player;
        var items = Game.ItemsOf(c);
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!) with
        {
            Sockets = [new(SocketColour.White, Gem("Dual Strike")), new(SocketColour.White, Gem("Nightblade Support"))],
            Links = [2],
        });
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Glass Shank")!));
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets = [new(SocketColour.White, Gem("Dual Strike"))],
            Links = [1],
        });

        var skills = Game.GemSkillEffects(c).ToArray();
        var supported = skills.Single(s => Game.SupportedSkill(c, s.Id).Stats.GetValueOrDefault(Stat.SupportedByNightblade) != 0);
        var plain = skills.Single(s => Game.SupportedSkill(c, s.Id).Stats.GetValueOrDefault(Stat.SupportedByNightblade) == 0);
        var own = Game.SupportedSkill(c, supported.Id).Stats;
        int elusiveBaseCrit = own.GetValueOrDefault(Stat.AdditionalCriticalStrikeChancePermyriadWhileAffectedByElusive);
        int nightbladeMult = own.GetValueOrDefault(Stat.NightbladeElusiveGrantsCriticalStrikeMultiplierPlusToSupportedSkills);
        Assert.True(elusiveBaseCrit > 0);
        Assert.True(nightbladeMult > 0);

        c.Pools = Game.PoolsFor(c, c.Pools.Level);
        Dictionary<uint, int> OffHandRows(ushort skillId, int effect)
        {
            var stats = Game.CharacterPlayerStats(c);
            var skillStats = Game.SupportedSkill(c, skillId).Stats;
            var empty = new Dictionary<uint, int>();
            return Game.OffHandPanelStats(c, stats, empty, empty, 0, skillStats, effect)!
                .ToDictionary(r => r.Stat, r => r.Value);
        }

        int baseMainCrit = Game.SupportedCriticalChance(c, supported.Id);
        int baseMainMult = Game.SupportedCriticalMultiplier(c, supported.Id);
        var baseOff = OffHandRows(supported.Id, supported.Effect);
        int baseOffCrit = baseOff[Stat.OffHandCriticalStrikeChance];
        int baseOffMult = baseOff[Stat.OffHandCriticalStrikeMultiplier];
        int plainMainCrit = Game.SupportedCriticalChance(c, plain.Id);
        int plainMainMult = Game.SupportedCriticalMultiplier(c, plain.Id);

        // Prime Elusive state so Rules and Epoch match, then land a Nightblade-supported attack critical strike.
        Game.CharacterStatSources(c);
        var target = new SpawnedMonster("target", new(105, 100, 0), 0, 1, 10000);
        Game.ChargesOnHit(c, target, critical: true, spell: false, supported.Effect, supported.Level, supported.Quality,
            melee: true, primary: true, skillStats: own);

        var elusive = Game.ElusiveOf(c);
        Assert.True(Game.ElusiveFactor(c) > 1.0); // Nightblade adds Elusive effect on top of 100% base.
        Assert.Equal(nightbladeMult, elusive.NightbladeMultiplier);
        c.Pools = Game.PoolsFor(c, c.Pools.Level);

        int fullExpectedBonus = (int)Math.Floor(nightbladeMult * Game.ElusiveFactor(c) + 1e-9);
        Assert.True(Game.SupportedCriticalChance(c, supported.Id) > baseMainCrit);
        Assert.Equal(baseMainMult + fullExpectedBonus, Game.SupportedCriticalMultiplier(c, supported.Id));
        var activeOff = OffHandRows(supported.Id, supported.Effect);
        Assert.True(activeOff[Stat.OffHandCriticalStrikeChance] > baseOffCrit);
        Assert.Equal(baseOffMult + fullExpectedBonus, activeOff[Stat.OffHandCriticalStrikeMultiplier]);
        // An unlinked copy of the skill receives neither Nightblade's base crit nor its crit multiplier.
        Assert.Equal(plainMainCrit, Game.SupportedCriticalChance(c, plain.Id));
        Assert.Equal(plainMainMult, Game.SupportedCriticalMultiplier(c, plain.Id));

        // Decay scales the Nightblade critical multiplier linearly on both hands and avoids IEEE-754 floor underflow.
        f.Clock.Time = 2.0;
        double decayedFactor = Game.ElusiveFactor(c);
        Assert.True(decayedFactor > 0 && decayedFactor < elusive.Initial / 100.0);
        int decayedBonus = (int)Math.Floor(nightbladeMult * decayedFactor + 1e-9);
        Assert.True(decayedBonus < fullExpectedBonus);
        Assert.Equal(baseMainMult + decayedBonus, Game.SupportedCriticalMultiplier(c, supported.Id));
        Assert.Equal(baseOffMult + decayedBonus, OffHandRows(supported.Id, supported.Effect)[Stat.OffHandCriticalStrikeMultiplier]);

        // Exact decimal boundary check: 100 * 0.29 = 28.999999999999996 in IEEE-754 double, which must floor to 29.
        elusive.Started = 10.0;
        elusive.CarriedAge = 0;
        elusive.Initial = 100;
        elusive.Rate = 20;
        elusive.Minimum = 0;
        elusive.RisingSeconds = 0;
        elusive.NightbladeMultiplier = 100;
        f.Clock.Time = 13.55; // effect = 100 - 20 * 3.55 = 29 => factor = 0.29
        Assert.Equal(baseMainMult + 29, Game.SupportedCriticalMultiplier(c, supported.Id));
        Assert.Equal(baseOffMult + 29, OffHandRows(supported.Id, supported.Effect)[Stat.OffHandCriticalStrikeMultiplier]);

        // Non-Nightblade Elusive grants Nightblade's "while affected by Elusive" base crit, not its crit multiplier.
        elusive.Clear();
        var elusiveMod = Program.Items.Mods.ById("ElusiveOnCriticalStrikeUnique__1")!;
        items.Add(PlayerInventories.Ring1, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = [new(ModSection.Explicit, elusiveMod.Hash, [100])],
        });
        f.Clock.Time = 20.0;
        c.Pools = Game.PoolsFor(c, c.Pools.Level);
        Game.GainElusiveOnCrit(c);
        Assert.True(Game.ElusiveFactor(c) > 0);
        Assert.Null(Game.ElusiveOf(c).NightbladeMultiplier);
        Assert.True(Game.SupportedCriticalChance(c, supported.Id) > baseMainCrit);
        Assert.Equal(baseMainMult, Game.SupportedCriticalMultiplier(c, supported.Id));
        var nonNightbladeOff = OffHandRows(supported.Id, supported.Effect);
        Assert.True(nonNightbladeOff[Stat.OffHandCriticalStrikeChance] > baseOffCrit);
        Assert.Equal(baseOffMult, nonNightbladeOff[Stat.OffHandCriticalStrikeMultiplier]);
    }

    [Fact]
    public async Task Eternal_and_guardians_blessing_manage_reservations_minion_dot_and_pool_cleanup()
    {
        using var f = new Fixture(withSession: true);
        var c = f.Player;
        var items = Game.ItemsOf(c);
        var efficiencyMod = Program.Items.Mods.ById("ReservationEfficiencyUnique__3__")!;
        items.Add(PlayerInventories.Ring1, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = [new(ModSection.Explicit, efficiencyMod.Hash, [100])],
        });

        // Part 1: Eternal Blessing — 0 reservation, suspends ordinary mana auras (including same aura, stance, Petrified Blood),
        // preserves Arrogance life reservation, and restores suspended reservations when ended.
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets =
            [
                new(SocketColour.White, Gem("Wrath")),
                new(SocketColour.White, Gem("Flesh and Stone")),
                new(SocketColour.White, Gem("Petrified Blood")),
                new(SocketColour.White, Gem("Determination")),
                new(SocketColour.White, Gem("Arrogance Support")),
            ],
            Links = [1, 1, 1, 2],
        });
        items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets =
            [
                new(SocketColour.White, Gem("Wrath")),
                new(SocketColour.White, Gem("Eternal Blessing Support")),
            ],
            Links = [2],
        });
        c.Pools = Game.PoolsFor(c, c.Pools.Level);
        c.PlayerLife = c.Pools.Life;
        c.PlayerMana = c.Pools.Mana;

        var allSkills = Game.GemSkillEffects(c).ToArray();
        var ordinaryWrath = allSkills.Single(s => Program.Skills.EffectId(s.Effect) == "Wrath"
            && Game.SupportedSkill(c, s.Id).Stats.GetValueOrDefault(Stat.SkillIsBlessingSkill) == 0);
        var eternalWrath = allSkills.Single(s => Program.Skills.EffectId(s.Effect) == "Wrath"
            && Game.SupportedSkill(c, s.Id).Stats.GetValueOrDefault(Stat.SkillIsBlessingSkill) != 0);
        var fleshAndStone = allSkills.Single(s => Program.Skills.EffectId(s.Effect) == "FleshAndStone");
        var petrifiedBlood = allSkills.Single(s => Program.Skills.EffectId(s.Effect) == PetrifiedBlood.EffectId);
        var arroganceDetermination = allSkills.Single(s => Program.Skills.EffectId(s.Effect) == "Determination");

        async Task<bool> ToggleAuraSkill((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem) =>
            await Game.ToggleAura(c, gem.Id, gem.Effect, gem.Name,
                Game.SupportedReservationCost(c, gem.Id)!.Value, gem.Level, gem.Quality);

        // Start ordinary Wrath, Flesh and Stone (toggled to Sand stance), Petrified Blood, and Arrogance Determination.
        Assert.True(await ToggleAuraSkill(ordinaryWrath));
        Assert.True(Game.TryPlanStanceLocked(c, fleshAndStone.Id, fleshAndStone.Effect, f.Clock.Time, out var plan1));
        Game.ApplyStancePlanLocked(c, fleshAndStone.Id, plan1!, ++c.ActionCount, c.ProjectileEpoch);
        Assert.True(Game.TryPlanStanceLocked(c, fleshAndStone.Id, fleshAndStone.Effect, f.Clock.Time, out var plan2));
        Game.ApplyStancePlanLocked(c, fleshAndStone.Id, plan2!, ++c.ActionCount, c.ProjectileEpoch);
        Assert.Equal(StanceCatalog.SandSelection, c.Stance);
        Assert.True(await Game.TogglePetrifiedBlood(c, petrifiedBlood.Id, petrifiedBlood.Effect, petrifiedBlood.Name,
            Game.SupportedReservationCost(c, petrifiedBlood.Id)!.Value, petrifiedBlood.Level, petrifiedBlood.Quality));
        Assert.True(await ToggleAuraSkill(arroganceDetermination));
        Assert.True(c.Auras[arroganceDetermination.Id].ReservesLife);
        Assert.True(c.PetrifiedBlood.Active);
        Assert.True(Game.ReservedMana(c) > 0);

        // Eternal Blessing Wrath succeeds even while ordinary mana-reserving Wrath is active, reserves 0 mana,
        // suspends ordinary Wrath / Flesh and Stone / Petrified Blood, reconciles stance, and keeps Arrogance Determination.
        var eternalCost = Game.SupportedReservationCost(c, eternalWrath.Id)!.Value;
        Assert.Equal((0, 0, 0, 0), (eternalCost.ManaFlat, eternalCost.ManaPercent, eternalCost.LifeFlat, eternalCost.LifePercent));
        Assert.True(await ToggleAuraSkill(eternalWrath));
        Assert.Contains(eternalWrath.Id, c.Auras.Keys);
        Assert.Contains(arroganceDetermination.Id, c.Auras.Keys);
        Assert.DoesNotContain(ordinaryWrath.Id, c.Auras.Keys);
        Assert.DoesNotContain(fleshAndStone.Id, c.Auras.Keys);
        Assert.False(c.PetrifiedBlood.Active);
        Assert.Equal(StanceCatalog.BloodSelection, c.Stance);
        Assert.Equal(0, Game.ReservedMana(c));
        Assert.False(await ToggleAuraSkill(ordinaryWrath));
        Assert.False(await Game.TogglePetrifiedBlood(c, petrifiedBlood.Id, petrifiedBlood.Effect, petrifiedBlood.Name,
            Game.SupportedReservationCost(c, petrifiedBlood.Id)!.Value, petrifiedBlood.Level, petrifiedBlood.Quality));

        // Ending Eternal Blessing restores the suspended ordinary mana reservations while keeping Arrogance Determination.
        Assert.True(await ToggleAuraSkill(eternalWrath));
        Assert.DoesNotContain(eternalWrath.Id, c.Auras.Keys);
        Assert.Contains(arroganceDetermination.Id, c.Auras.Keys);
        Assert.Contains(fleshAndStone.Id, c.Auras.Keys);
        Assert.Equal(StanceCatalog.SandSelection, c.Stance);
        Assert.True(c.PetrifiedBlood.Active);

        await Game.DisableReservations(c, "all");
        Assert.Empty(c.Auras);

        // Part 2: Guardian's Blessing — rejects unlinked minions, accepts linked Golem / Summon / Herald of Purity,
        // deals physical DoT to the minion, and ends aura + rebuilds pools & movement speed on death and duration expiry.
        foreach (var slot in new[] { PlayerInventories.BodyArmour, PlayerInventories.Helmet })
            foreach (var p in items.Items(slot).ToArray()) items.Remove(slot, p.Id);

        items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new(SocketColour.White, Gem("Summon Chaos Golem"))],
            Links = [1],
        });
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets =
            [
                new(SocketColour.White, Gem("Haste")),
                new(SocketColour.White, Gem("Guardian's Blessing Support")),
                new(SocketColour.White, Gem("Summon Chaos Golem")),
                new(SocketColour.White, Gem("Summon Skeletons")),
                new(SocketColour.White, Gem("Herald of Purity")),
            ],
            Links = [5],
        });
        c.Pools = Game.PoolsFor(c, c.Pools.Level);
        c.ApplyMovementSpeed();
        int baseMovePct = c.Pools.MovementVelocityPct;
        double baseWalkSpeed = c.Walk.Speed;

        var guardianSkills = Game.GemSkillEffects(c).ToArray();
        var guardianHaste = guardianSkills.Single(s => Program.Skills.EffectId(s.Effect) == "Haste");
        var unlinkedGolem = guardianSkills.Single(s => Program.Skills.EffectId(s.Effect) == "SummonChaosGolem"
            && Game.SupportedSkill(c, s.Id).Supports.All(sup => sup.Effect.Id != "SupportGuardiansBlessingMinion"));
        var linkedGolem = guardianSkills.Single(s => Program.Skills.EffectId(s.Effect) == "SummonChaosGolem"
            && Game.SupportedSkill(c, s.Id).Supports.Any(sup => sup.Effect.Id == "SupportGuardiansBlessingMinion"));
        var linkedSkeleton = guardianSkills.Single(s => Program.Skills.EffectId(s.Effect) == "SummonSkeletons");
        var linkedPurity = guardianSkills.Single(s => Program.Skills.EffectId(s.Effect) == "HeraldOfPurity");

        // Unlinked golem does NOT satisfy Guardian's Blessing.
        uint unlinkedId = await Game.SummonGolem(c, unlinkedGolem.Id, Game.GolemData.Find("SummonChaosGolem")!, (100, 100));
        Assert.NotEqual(0u, unlinkedId);
        Assert.False(await ToggleAuraSkill(guardianHaste));
        Assert.Empty(c.Auras);
        Invoke("RemoveGolemLocked", c, c.Golems.All[unlinkedId], f.Clock.Time, true);

        // Linked golem sustains Guardian's Blessing, takes physical DoT, and its death ends the aura + cleans up pools.
        uint golemId = await Game.SummonGolem(c, linkedGolem.Id, Game.GolemData.Find("SummonChaosGolem")!, (100, 100));
        var golem = c.Golems.All[golemId];
        Assert.True(await ToggleAuraSkill(guardianHaste));
        Assert.Contains(guardianHaste.Id, c.Auras.Keys);
        Assert.Equal(0, Game.ReservedMana(c));
        Assert.True(c.Pools.MovementVelocityPct > baseMovePct);
        Assert.True(c.Walk.Speed > baseWalkSpeed);

        long golemTotalBefore = (long)golem.Monster.Life + golem.ShieldPool.Current;
        f.Clock.Time += 1.0;
        Game.GolemDotsLocked(c, golem, f.Clock.Time - 1.0, f.Clock.Time);
        Assert.True((long)golem.Monster.Life + golem.ShieldPool.Current < golemTotalBefore);

        Game.GolemDieLocked(c, golem, f.Clock.Time);
        Assert.Empty(c.Auras);
        Assert.Equal(baseMovePct, c.Pools.MovementVelocityPct);
        Assert.Equal(baseWalkSpeed, c.Walk.Speed, 9);
        // ReconcileOwnAuras also cleans up dirty pools even when Auras.Count == 0.
        c.Pools = c.Pools with { MovementVelocityPct = baseMovePct + 50 };
        c.AuraStatsDirty = true;
        Game.ReconcileOwnAuras(c);
        Assert.Equal(baseMovePct, c.Pools.MovementVelocityPct);
        Invoke("RemoveGolemLocked", c, golem, f.Clock.Time, true);

        // Linked duration summon (Summon Skeletons) sustains Guardian's Blessing, takes DoT, and expiry ends the aura + rebuilds pools.
        uint skeletonId = await Game.SummonFighter(c, linkedSkeleton.Id, Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        var skeleton = c.Summons.Fighters[skeletonId];
        Assert.True(await ToggleAuraSkill(guardianHaste));
        Assert.True(c.Pools.MovementVelocityPct > baseMovePct);
        long skeletonTotalBefore = (long)skeleton.Monster.Life + skeleton.ShieldPool.Current;
        f.Clock.Time += 1.0;
        Game.GolemDotsLocked(c, skeleton, f.Clock.Time - 1.0, f.Clock.Time);
        Assert.True((long)skeleton.Monster.Life + skeleton.ShieldPool.Current < skeletonTotalBefore);

        f.Clock.Time = c.Summons.Until[skeletonId] + 0.1;
        await Game.SummonsTick(c, f.Clock.Time);
        Assert.Empty(c.Summons.Fighters);
        Assert.Empty(c.Auras);
        Assert.Equal(baseMovePct, c.Pools.MovementVelocityPct);
        Assert.Equal(baseWalkSpeed, c.Walk.Speed, 9);

        // Linked Herald of Purity Sentinel (player.Reservations.Fighters) sustains Guardian's Blessing, takes DoT, and expiry ends the aura.
        Assert.True(await ToggleAuraSkill(linkedPurity));
        await (Task)typeof(Game).GetMethod("SummonReservationFighter", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [c, c.Auras[linkedPurity.Id], "Purity"])!;
        var sentinel = Assert.Single(c.Reservations.Fighters.Values);
        Assert.True(await ToggleAuraSkill(guardianHaste));
        Assert.Contains(guardianHaste.Id, c.Auras.Keys);
        Assert.True(c.Pools.MovementVelocityPct > baseMovePct);

        long sentinelTotalBefore = (long)sentinel.Monster.Life + sentinel.ShieldPool.Current;
        f.Clock.Time += 1.0;
        Game.GolemDotsLocked(c, sentinel, f.Clock.Time - 1.0, f.Clock.Time);
        Assert.True((long)sentinel.Monster.Life + sentinel.ShieldPool.Current < sentinelTotalBefore);

        f.Clock.Time = c.Reservations.Until[sentinel.Id] + 0.1;
        await (Task)typeof(Game).GetMethod("ReservationFightersTick", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [c, f.Clock.Time])!;
        Assert.Empty(c.Reservations.Fighters);
        Assert.DoesNotContain(guardianHaste.Id, c.Auras.Keys);
        Assert.Contains(linkedPurity.Id, c.Auras.Keys);
        Assert.Equal(baseMovePct, c.Pools.MovementVelocityPct);
        Assert.Equal(baseWalkSpeed, c.Walk.Speed, 9);
    }

    static void Invoke(string name, params object[] arguments) =>
        typeof(Game).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);

    static Item Gem(string name)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with
        {
            GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20),
        };
    }

    sealed class TestClock : IPlayerUpdateSchedule
    {
        public double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }

    sealed class Fixture : IDisposable
    {
        readonly string save = Path.Combine(Path.GetTempPath(), $"nightblade-blessings-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly PlayerUpdatesState? previousAreaUpdate = Game.AreaUpdateContext.Value;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly Task drain;
        readonly IDisposable requirements;
        readonly Game.ProjectileSession? session;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly TestClock Clock = new();
        public readonly Player Player;

        public Fixture(bool withSession)
        {
            Saves.Open(save);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Game.AreaUpdateContext.Value = new(lifetime, Clock);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            Player = new(new(1, "nightblade-blessings", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "Blessing" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
                Connection = { PlayerSent = true, Session = new("nightblade-blessings", writer.GetStream(), Ciphers) },
                Instance = new(),
                PlayerLife = 5000,
                PlayerMana = 2000,
                Pools = new(100, 5000, 2000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            Player.Walk.Teleport(100, 100, 0);
            Player.Summons.Restored = Player.Golems.Restored = true;
            requirements = TestRequirements.Waive(Player);
            if (withSession)
            {
                session = new(Player);
                Player.ProjectileRuntime = session;
            }
        }

        public void Dispose()
        {
            Player.Closed = true;
            session?.Invalidate();
            requirements.Dispose();
            reader.Dispose();
            writer.Dispose();
            listener.Stop();
            drain.GetAwaiter().GetResult();
            PlayerInventories.Forget(Player.Character.Name!);
            Game.AreaUpdateContext.Value = previousAreaUpdate;
            Program.SetGameLogic(previous!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            TestEnvironment.ReleaseSaves(save);
        }
    }
}
