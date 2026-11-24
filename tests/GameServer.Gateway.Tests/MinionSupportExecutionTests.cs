using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionSupportExecutionTests
{
    static void Invoke(string name, params object[] arguments) =>
        typeof(Game).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);

    static async Task<GolemState.Golem> Skeleton(AnimatedAdmissionTests.Fixture f, params string[] supports)
    {
        var items = Game.ItemsOf(f.Player);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        Item Gem(string name)
        {
            var type = Program.Items.FindGem(name)!.Value;
            return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
                GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        }
        var names = new[] { "Summon Skeletons" }.Concat(supports).ToArray();
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!) with
        { Sockets = names.Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(), Links = [(byte)names.Length] });
        ushort source = Game.GemSkillEffects(f.Player).Single().Id;
        uint id = await Game.SummonFighter(f.Player, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        var fighter = f.Player.Summons.Fighters[id];
        fighter.Monster.Walk.Teleport(100, 100, 0);
        Invoke("RefreshGolemLocked", f.Player, fighter, Game.CharacterPlayerStats(f.Player), (double?)0d);
        fighter.Monster.Mods = fighter.Monster.Mods with { Effects = new(new Dictionary<uint, int>
            { [Stat.AccuracyRating] = 1000000, [Stat.Of("never_crit")] = 1,
                [Stat.SupportMeleeSplashDamagePlusPctFinalForSplash] = fighter.Own.GetValueOrDefault(Stat.SupportMeleeSplashDamagePlusPctFinalForSplash) }) };
        f.Player.Walk.Teleport(100, 100, 0);
        return fighter;
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }

    [Fact]
    public void Living_lightning_resolves_its_own_chain_and_duration_supports()
    {
        using var f = new TriggeredSummonTests.Fixture();
        f.Equip(("Shock Nova", 1), ("Living Lightning Support", 1),
            ("Chain Support", 1), ("More Duration Support", 1));
        ushort skill = Game.GemSkillEffects(f.Player).Single(g => Program.Skills.EffectId(g.Effect) == "ShockNova").Id;
        var source = Game.SupportedSkill(f.Player, skill);
        Assert.Contains(source.Supports, support => support.Effect.Id == "SupportLivingLightning");
        Assert.DoesNotContain(source.Supports, support => support.Effect.Id == "SupportChain");
        Assert.Contains(source.LivingLightningSupports, support => support.Effect.Id == "SupportChain");
        Assert.Contains(source.LivingLightningSupports, support => support.Effect.Id == "SupportMoreDuration");

        var sourceStats = new Dictionary<uint, int>(source.Stats) { [Stat.SpellDamagePlusPct] = 777 };
        var lightning = Game.LivingLightningGem(f.Player, source with { Stats = sourceStats });
        Assert.False(lightning.Stats.ContainsKey(Stat.SpellDamagePlusPct));
        Assert.True(lightning.Stats.GetValueOrDefault(Stat.NumberOfChains) > 3);
        Assert.True(SkillDurationModifiers.Seconds(lightning.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            _ => 0, lightning.Stats.GetValueOrDefault) > 5);
    }

    [Fact]
    public async Task Multistrike_repeats_after_recovery_and_ruthless_counts_uses_per_minion()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var time = new Clock(); var before = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, time);
        try
        {
            var fighter = await Skeleton(f, "Multistrike Support", "Ruthless Support");
            fighter.Gear[Stat.GlobalMinimumAddedChaosDamage] = fighter.Gear[Stat.GlobalMaximumAddedChaosDamage] = 100;
            fighter.Gear[Stat.BaseDealNoPhysicalDamage] = 1;
            fighter.Own[Stat.BaseChanceToPoisonOnHitPct] = 100;
            fighter.Abilities = Game.GolemAbilities(f.Player, fighter.Monster.Variety!, fighter.Monster.Level, fighter, fighter.Player);
            var ability = fighter.Abilities.First(a => !a.Attack.Spell && Program.Skills.IsMelee(a.Effect));
            var enemy = new SpawnedMonster("target", new(105, 100, 0), 100, 3, 1000000) { Hostile = true };
            f.Player.Area.Monsters[999] = enemy;
            double Poison(GolemState.Ability hit)
            {
                var target = new SpawnedMonster("poison target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
                Invoke("MinionAilmentsLocked", f.Player, fighter, hit, fighter.Own, 998u, target, false, time.Time, 1d, false, 0);
                return target.Poisons.TotalDps(time.Time);
            }
            double originalPoison = Poison(ability);
            Assert.True(originalPoison > 0);
            double original = ability.Attack.Damage.MaxTotal;
            Assert.True(original > 0);
            int Own(uint n) => fighter.Own.GetValueOrDefault(n);
            Assert.Equal(2, Own(Stat.BaseMeleeAttackRepeatCount));
            for (int use = 0; use < 3; use++)
            {
                Invoke("GolemReserveLocked", f.Player, fighter, ability, 999u, enemy, (105, 100), time.Time);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    Invoke("GolemActionRecords", f.Player, new PlayerMinions.Output([], null, fighter.Id, fighter.Monster,
                        PlayerMinions.OutputKind.GolemAction, Action: fighter.Monster.ActionGeneration));
                    var pending = fighter.Pending!;
                    double ruthless = use == 2 ? 1 + Own(Stat.SupportRuthlessBigHitDamagePlusPctFinal) / 100d : 1;
                    double extra = repeat == 0 ? 1 : 1 + Own(repeat == 1 ? Stat.MultistrikeDamagePlusPctFinalOnFirstRepeat
                        : Stat.MultistrikeDamagePlusPctFinalOnSecondRepeat) / 100d;
                    Assert.Equal(original * ruthless * extra, pending.Ability.Attack.Damage.MaxTotal, 5);
                    double ailmentRuthless = use == 2 ? 1 + Own(Stat.SupportRuthlessBlowAilmentDamageFromMeleeHitsPlusPctFinal) / 100d : 1;
                    Assert.Equal(originalPoison * ailmentRuthless * extra, Poison(pending.Ability), 5);
                    Assert.Equal(repeat, pending.RepeatIndex);
                    time.Time = pending.At;
                    uint life = enemy.Life;
                    Game.GolemStepLocked(f.Player, fighter, time.Time);
                    Assert.True(enemy.Life < life);
                    if (repeat < 2)
                    {
                        Assert.Null(fighter.Pending);
                        Assert.NotNull(fighter.Repeat);
                        time.Time = fighter.Monster.BusyUntil;
                        Game.GolemStepLocked(f.Player, fighter, time.Time);
                        Assert.NotNull(fighter.Pending);
                    }
                    else { Assert.Null(fighter.Repeat); time.Time = fighter.Monster.BusyUntil; }
                }
            }
        }
        finally { Game.AreaUpdateContext.Value = before; }
    }

    [Fact]
    public async Task Melee_splash_hits_nearby_enemies_once_with_its_own_multiplier()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var fighter = await Skeleton(f, "Melee Splash Support");
        var original = fighter.Abilities.First(a => !a.Attack.Spell && Program.Skills.IsMelee(a.Effect));
        var ability = original with { Attack = original.Attack with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) } };
        fighter.Abilities = [ability];
        var enemies = new[] { (999u, 105, 100, true), (998u, 108, 100, true), (997u, 160, 100, true), (996u, 108, 100, false) };
        foreach (var (id, x, y, hostile) in enemies)
            f.Player.Area.Monsters[id] = new("target", new(x, y, 0), 100, 3, 10000) { Hostile = hostile };
        Invoke("GolemAbilityContactLocked", f.Player, fighter,
            new GolemState.Action(ability, 999, f.Player.Area.Monsters[999], (105, 100), 0, fighter.Monster.ActionGeneration), 0d);
        Assert.Equal(9900u, f.Player.Area.Monsters[999].Life);
        Assert.Equal(9940u, f.Player.Area.Monsters[998].Life);
        Assert.Equal(10000u, f.Player.Area.Monsters[997].Life);
        Assert.Equal(10000u, f.Player.Area.Monsters[996].Life);
    }

    [Fact]
    public async Task Spell_echo_repeats_spectre_spell_and_launches_spiral_nova_with_returning_projectiles()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var time = new Clock();
        var before = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, time);
        try
        {
            var items = Game.ItemsOf(f.Player);
            foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
            Item Gem(string name)
            {
                var type = Program.Items.FindGem(name)!.Value;
                return ItemCatalog.Create(type.Type) with
                {
                    GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20)
                };
            }
            var names = new[] { "Raise Spectre", "Spell Echo Support", "Returning Projectiles Support" };
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!) with
            {
                Sockets = names.Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray(),
                Links = [(byte)names.Length]
            });
            ushort source = Game.GemSkillEffects(f.Player).Single().Id;
            var defilerVariety = Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantMapBossStandalone_AtlasUber")!;
            uint id = await Game.SummonFighter(f.Player, source,
                Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: defilerVariety.Id);
            var fighter = f.Player.Summons.Fighters[id];
            fighter.Monster.Walk.Teleport(100, 100, 0);
            Invoke("RefreshGolemLocked", f.Player, fighter, Game.CharacterPlayerStats(f.Player), (double?)0d);
            fighter.Abilities = Game.GolemAbilities(f.Player, defilerVariety, 84, fighter, Game.CharacterPlayerStats(f.Player));
            var spell = Assert.Single(fighter.Abilities, a => a.Id == "RevenantBossSpellProjectile");
            Assert.Equal(9, spell.Projectile!.Count);
            Assert.Equal(50, spell.Projectile.SpiralAngle);
            Assert.Equal(750, spell.Projectile.SpiralTimeMs);
            Assert.Equal(1, Game.SkillsFor(f.Player).MonsterProjectile(spell.Effect)!.Count);
            var enemy = new SpawnedMonster("target", new(130, 100, 0), 100, 3, 1_000_000) { Hostile = true };
            f.Player.Area.Monsters[999] = enemy;

            var session = Game.ProjectilesFor(f.Player);
            Invoke("GolemReserveLocked", f.Player, fighter, spell, 999u, enemy, (130, 100), time.Time);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                Invoke("GolemActionRecords", f.Player, new PlayerMinions.Output([], null, fighter.Id, fighter.Monster,
                    PlayerMinions.OutputKind.GolemAction, Action: fighter.Monster.ActionGeneration));
                var pending = fighter.Pending!;
                Assert.Equal(repeat, pending.RepeatIndex);
                time.Time = pending.At;
                Game.GolemStepLocked(f.Player, fighter, time.Time);
                using (Game.ProjectileMutation(f.Player, advance: false))
                {
                    var shots = new Dictionary<long, long>();
                    for (int step = 1; step <= 100; step++)
                    {
                        session.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(time.Time + step * 0.01));
                        foreach (var shot in session.Runtime.Projectiles.Where(p => p.CreatedAt >= GameServer.Core.Projectiles.ProjectileRuntime.Time(time.Time)))
                        {
                            Assert.True(shot.Profile.Returns);
                            shots.TryAdd(shot.Id, shot.CreatedAt);
                        }
                    }
                    Assert.Equal(9, shots.Count);
                    Assert.Equal(9, shots.Values.Distinct().Count());
                }
                if (repeat == 0)
                {
                    Assert.NotNull(fighter.Repeat);
                    time.Time = Math.Max(time.Time + 1.0, fighter.Monster.BusyUntil);
                    Game.GolemStepLocked(f.Player, fighter, time.Time);
                    Assert.NotNull(fighter.Pending);
                }
                else
                {
                    Assert.Null(fighter.Repeat);
                }
            }
            Assert.True(enemy.Life < 1_000_000u);
        }
        finally { Game.AreaUpdateContext.Value = before; }
    }
}
