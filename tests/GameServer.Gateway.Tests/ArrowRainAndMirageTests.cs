using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modules.Survivors;

namespace GameServer.Gateway.Tests;

public class ArrowRainAndMirageTests
{
    [Theory]
    [InlineData("Blink Arrow", true)]
    [InlineData("Mirror Arrow", false)]
    public async Task Arrow_clone_and_owner_land_at_their_respective_positions(string name, bool blink)
    {
        foreach (string variant in new[] { name, name + " of Bombarding Clones", name + " of Prismatic Clones" })
        {
            using var b = new Battle(variant);
            var c = b.Player;
            c.Clock.Start();
            var gem = Game.GemSkillEffects(c).Single();
            Assert.False(Game.LandsAtAim(c, gem.Effect));
            var origin = c.Walk.Position(Game.AreaTime(c));
            var aim = (origin.X + 24, origin.Y);
            Assert.True(Game.TryBeginSkillCooldownUse(c, gem.Id, gem.Effect, Game.CooldownForSkill(c, gem.Id), false,
                () => c.ActionCount++, out var paid));
            Assert.True(await Game.CastGemSkill(c, gem.Id, origin, aim, cooldownUse: paid));
            var clone = Assert.Single(c.Summons.Fighters.Values);
            Assert.Equal(blink ? origin : aim, clone.Monster.Walk.Position(Game.AreaTime(c)));
            Assert.Equal(blink ? aim : origin, c.Walk.Position(Game.ProjectileWorldTime(c)));
            if (variant.Contains("Bombarding", StringComparison.Ordinal))
            {
                var rain = Assert.Single(clone.Abilities);
                Assert.Equal(9511, rain.Effect); Assert.Null(rain.Projectile);
                typeof(Game).GetMethod("GolemAbilityContactLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [c, clone,
                        new GolemState.Action(rain, 200, b.Enemy, (120, 100), 0, clone.Monster.ActionGeneration), 1d]);
                Assert.Equal(11, clone.Pulses.Count);
                Assert.All(clone.Pulses, p => Assert.True(p.Next >= 1.35));
                clone.Gear[Stat.AccuracyRating] = 1000000;
                uint life = b.Enemy.Life;
                Game.GolemStepLocked(c, clone, 1.36);
                Assert.True(b.Enemy.Life < life);
                Assert.Equal(100000u, b.Ally.Life);
            }
            if (variant.Contains("Prismatic", StringComparison.Ordinal))
            {
                Assert.Equal(306, Assert.Single(clone.Abilities).Effect);
                clone.Gear[Stat.GlobalMinimumAddedFireDamage] = 100;
                clone.Gear[Stat.GlobalMaximumAddedFireDamage] = 100;
                clone.Gear[Stat.GlobalMinimumAddedChaosDamage] = 100;
                clone.Gear[Stat.GlobalMaximumAddedChaosDamage] = 100;
                clone.Own[Stat.BaseChanceToPoisonOnHitPct] = 100;
                foreach (var element in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
                {
                    var attack = Assert.Single(Game.GolemAbilities(c, clone.Monster.Variety!, clone.Monster.Level, clone, clone.Player, element));
                    Assert.True(attack.Attack.Damage.MaxTotal > 0);
                    Assert.All(attack.Attack.Damage.Components, p => Assert.Equal(element, p.Type));
                    var ailmentTarget = new SpawnedMonster("ailment target", new(120, 100, 0), 100, 3, 100000) { Hostile = true };
                    typeof(Game).GetMethod("MinionAilmentsLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [c, attack, 999u, ailmentTarget, true, 1d, 1d]);
                    Assert.False(ailmentTarget.Poisons.Poisoned);
                    Assert.Equal(element == DamageType.Fire, ailmentTarget.Ignites.Burning);
                }
            }
        }
    }

    [Fact]
    public void Explosive_arrow_detonates_at_the_fuse_cap_or_first_fuse_deadline_and_only_final_ensnaring_hits_tether()
    {
        static ProjectileUse Capture(Battle battle)
        {
            var gem = Game.GemSkillEffects(battle.Player).Single();
            return Game.CaptureGemProjectile(battle.Player, battle.Session, gem.Id, gem, (180, 100),
                Game.CaptureSkillCriticalRoll(battle.Player, gem.Id));
        }

        using (var b = new Battle("Explosive Arrow"))
        {
            var use = Capture(b);
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            var fuse = Assert.IsType<Game.ExplosiveFuseSpec>(shot.ExplosiveFuse);
            Assert.Equal(20, fuse.Limit);
            Assert.Equal(ProjectileRuntime.Time(1), fuse.Duration);
            Assert.True(fuse.Damage.Hit[DamageType.Fire].Min > shot.Hit[DamageType.Fire].Min);
            var runtime = b.Session.Runtime;
            var witness = new SpawnedMonster("explosion witness", new(120, 110, 0), 0, 0, 100000) { Hostile = true };
            b.Player.Area.Monsters[202] = witness;
            var shortStack = use with { Payload = shot with { ExplosiveFuse = fuse with { Limit = 2 } } };
            runtime.Release(shortStack);
            runtime.AdvanceTo(ProjectileRuntime.Time(.2));
            Assert.True(b.Enemy.Life < 100000);
            Assert.Equal(100000u, witness.Life);
            runtime.Release(shortStack);
            runtime.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.True(witness.Life < 100000);
            uint life = witness.Life;
            runtime.AdvanceTo(ProjectileRuntime.Time(1.5));
            Assert.Equal(life, witness.Life);
            runtime.Release(use);
            runtime.AdvanceTo(ProjectileRuntime.Time(2.4));
            runtime.Release(use);
            runtime.AdvanceTo(ProjectileRuntime.Time(2.49));
            Assert.Equal(life, witness.Life);
            runtime.AdvanceTo(ProjectileRuntime.Time(2.8));
            Assert.True(witness.Life < life);
            Assert.Equal(100000u, b.Ally.Life);
        }

        using (var b = new Battle("Ensnaring Arrow"))
        {
            var use = Capture(b);
            var runtime = b.Session.Runtime;
            var profile = use.Profile with { Pierce = 1 };
            var last = new SpawnedMonster("terminal target", new(140, 100, 0), 0, 0, 100000) { Hostile = true };
            b.Player.Area.Monsters[202] = last;
            for (int i = 1; i <= 4; i++)
            {
                runtime.Release(use with { Profile = profile });
                runtime.AdvanceTo(ProjectileRuntime.Time(i * .25));
            }
            Assert.True(b.Enemy.Life < 100000);
            Assert.Equal(0, Game.EnsnaredProjectileAttackTaken(b.Enemy, 1));
            Assert.Equal(.6 * .6 * .6, Game.EnsnaredWalkFactor(last, 1), 9);
            Assert.Equal(15, Game.EnsnaredProjectileAttackTaken(last, 1));
            last.Walk.Teleport(190, 100, 1);
            runtime.AdvanceTo(ProjectileRuntime.Time(1.2));
            Assert.Equal(1, Game.EnsnaredWalkFactor(last, 1.2));
            Assert.Equal(0, Game.EnsnaredProjectileAttackTaken(last, 1.2));
        }
    }

    [Fact]
    public async Task Explosive_and_ensnaring_arrow_lifecycle_radius_quality_death_detonation_and_bleed()
    {
        static ProjectileUse CaptureSkill(Battle battle, string name)
        {
            var gem = Game.GemSkillEffects(battle.Player).Single(g => g.Name == name);
            return Game.CaptureGemProjectile(battle.Player, battle.Session, gem.Id, gem, (180, 100),
                Game.CaptureSkillCriticalRoll(battle.Player, gem.Id));
        }

        // 1. Uncreated-tether eviction never sends 0x02bd RemoveObject; Session.Invalidate() clears buffs, state and objects.
        using (var b = new Battle("Ensnaring Arrow"))
        {
            var packets = new List<byte[]>();
            var previousOutgoing = Program.Outgoing;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Program.Outgoing = (player, bytes) =>
            {
                if (ReferenceEquals(player, b.Player)) lock (packets) packets.Add(bytes.ToArray());
            };
            try
            {
                b.Session.Enqueue(() => gate.Task);

                var use = CaptureSkill(b, "Ensnaring Arrow");
                var runtime = b.Session.Runtime;
                var beforeIds = new HashSet<uint>(b.Player.ProjectileObjects);
                runtime.Release(use);
                runtime.AdvanceTo(ProjectileRuntime.Time(.2));
                var firstIds = b.Player.ProjectileObjects.Except(beforeIds).ToArray();
                Assert.True(firstIds.Length >= 2);
                // Fire 3 more hits before RunOutput processes the 1st tether's Create, evicting the uncreated 1st tether.
                for (int i = 2; i <= 4; i++)
                {
                    runtime.Release(use);
                    runtime.AdvanceTo(ProjectileRuntime.Time(i * .2));
                }
                var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                b.Session.Enqueue(() => { flushed.SetResult(); return Task.CompletedTask; });
                b.Player.Clock.Start();
                gate.SetResult();
                await Task.WhenAny(flushed.Task, b.Session.OutputCompleted);
                Assert.True(flushed.Task.IsCompleted, "RunOutput exited before flushing queued intents.");
                byte[][] sent;
                lock (packets) sent = [.. packets];
                Assert.NotEmpty(sent);
                foreach (uint id in firstIds)
                {
                    byte[] remove = InstanceProtocol.RemoveObject(id);
                    Assert.DoesNotContain(sent, p => p.AsSpan().IndexOf(remove) >= 0);
                }

                ushort tetheredRow = BuffRows.Find("tethered_debuff");
                Assert.True(tetheredRow != 0 && b.Player.Area.Buffs.HasRow(200, tetheredRow));
                Assert.True(Game.IsEnsnared(b.Enemy, .8));
                b.Session.Invalidate();
                await b.Session.OutputCompleted;
                Assert.False(Game.IsEnsnared(b.Enemy, .8));
                Assert.Equal(1, Game.EnsnaredWalkFactor(b.Enemy, .8));
                Assert.Equal(0, Game.EnsnaredProjectileAttackTaken(b.Enemy, .8));
                Assert.False(b.Player.Area.Buffs.HasRow(200, tetheredRow));
                Assert.Empty(b.Player.ProjectileObjects);
            }
            finally
            {
                gate.TrySetResult();
                Program.Outgoing = previousOutgoing;
            }
        }

        // 2. Terrain fuse, FuseArrow metadata, ExplosiveArrowRadiusPlusPct as increased AoE, ActiveSkillBaseRadiusPlus,
        //    Ignite Proliferation spread, no double-evasion, and lethal 2nd-fuse / DoT death detonation.
        using (var b = new Battle("Explosive Arrow", "Ignite Proliferation Support"))
        {
            var baseUse = CaptureSkill(b, "Explosive Arrow");
            Assert.Equal("Metadata/Projectiles/FuseArrow", baseUse.Profile.Id);
            var baseFuse = Assert.IsType<Game.ExplosiveFuseSpec>(Assert.IsType<ProjectileOffense>(baseUse.Payload).ExplosiveFuse);
            Assert.Equal(15, baseFuse.BaseRadius);
            Assert.Equal(1d, baseFuse.RadiusScale, 6);
            Assert.True(baseFuse.Damage.Ignite.Spread > 0);
            foreach (string variant in new[] { "ExplosiveArrow", "ExplosiveArrowAltX", "ExplosiveArrowAltY", "ExplosiveArrowRoyale" })
                Assert.Equal("Metadata/Projectiles/FuseArrow", Game.ProjectileDefinition(variant)?.Metadata);

            b.StatOverrides[Stat.ExplosiveArrowRadiusPlusPct] = 100;
            b.StatOverrides[Stat.ActiveSkillBaseRadiusPlus] = 4;
            var scaledFuse = Assert.IsType<Game.ExplosiveFuseSpec>(
                Assert.IsType<ProjectileOffense>(CaptureSkill(b, "Explosive Arrow").Payload).ExplosiveFuse);
            Assert.Equal(19, scaledFuse.BaseRadius);
            // 100% increased AoE scales radius by floor(100 * sqrt(2)) / 100 = 1.41, not 2.0 MORE radius.
            Assert.Equal(1.41, scaledFuse.RadiusScale, 6);
            b.StatOverrides.Remove(Stat.ExplosiveArrowRadiusPlusPct);
            b.StatOverrides.Remove(Stat.ActiveSkillBaseRadiusPlus);

            // Terrain fuse sticks in a wall and detonates after its fuse duration, even without evasion re-roll.
            b.StatOverrides.Remove(Stat.GlobalAlwaysHit);
            b.Player.ProjectileWalls.Add((115, 100));
            b.Enemy.Walk.Teleport(160, 100, 0);
            var evasiveWitness = new SpawnedMonster("evasive witness", new(115, 110, 0), 0, 0, 100000)
            {
                Hostile = true,
                EncounterStats = new Dictionary<uint, int> { [Stat.EvasionRating] = 1_000_000 }
            };
            b.Player.Area.Monsters[202] = evasiveWitness;
            var terrainUse = CaptureSkill(b, "Explosive Arrow");
            b.Session.Runtime.Release(terrainUse);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(.5));
            Assert.Equal(100000u, evasiveWitness.Life);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(1.3));
            Assert.True(evasiveWitness.Life < 100000u);
            b.Player.ProjectileWalls.Clear();
            b.StatOverrides[Stat.GlobalAlwaysHit] = 1;

            // Lethal 2nd fuse (below the 20-stack cap) detonates immediately on death rather than waiting 1 s.
            b.Enemy.Walk.Teleport(120, 100, 1.3);
            var nearWitness = new SpawnedMonster("near witness", new(120, 108, 0), 0, 0, 100000) { Hostile = true };
            b.Player.Area.Monsters[203] = nearWitness;
            b.StatOverrides[Stat.GlobalCannotCrit] = 1;
            var use = CaptureSkill(b, "Explosive Arrow");
            b.Session.Runtime.Release(use);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(1.5));
            uint firstHitDamage = b.Enemy.MaxLife - b.Enemy.Life;
            Assert.True(firstHitDamage > 0);
            Assert.Equal(100000u, nearWitness.Life);
            // Leave enemy alive with less than one arrow hit so the 2nd arrow kills it.
            b.Enemy.Life = Math.Max(1u, firstHitDamage / 2);
            b.Session.Runtime.Release(use);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(1.7));
            Assert.True(b.Enemy.Dead);
            Assert.True(nearWitness.Life < 100000u);
            uint afterSecondDeath = nearWitness.Life;
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(2.8));
            Assert.Equal(afterSecondDeath, nearWitness.Life);

            // External DoT death (bleed) immediately detonates an active fuse on the dying monster.
            var bleedVictim = new SpawnedMonster("bleed victim", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
            b.Player.Area.Monsters[204] = bleedVictim;
            b.Session.Runtime.Release(use);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(3.0));
            Assert.Equal(afterSecondDeath, nearWitness.Life);
            bleedVictim.Life = 50;
            Assert.True(bleedVictim.Bleeds.Add(3.0, 200, 5));
            b.Player.Area.ActiveBleedMonsters.Add(204);
            b.Session.AdvanceTo(ProjectileRuntime.Time(3.4));
            await Game.BleedMonsters(b.Player);
            Assert.True(bleedVictim.Dead);
            Assert.True(nearWitness.Life < afterSecondDeath);
            uint afterDotDetonation = nearWitness.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(4.5));
            Assert.Equal(afterDotDetonation, nearWitness.Life);
            Assert.Equal(100000u, b.Ally.Life);
        }

        // 3. Ensnaring Arrow quality (skill_buff_effect_+%), EnsnaringArrowAreaOfEffectPlusPct,
        //    projectile-attack-only gate (Rain of Arrows excluded, Split Arrow included), and moving-Bleed synergy.
        using (var b = new Battle("Ensnaring Arrow", "Rain of Arrows", "Split Arrow"))
        {
            var items = Game.ItemsOf(b.Player);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            var baseSpec = Assert.IsType<Game.EnsnareSpec>(
                Assert.IsType<ProjectileOffense>(CaptureSkill(b, "Ensnaring Arrow").Payload).Ensnare);
            Assert.Equal(28, baseSpec.Radius);
            Assert.Equal(15, baseSpec.AttackTaken);
            Assert.Equal(-40, baseSpec.NormalSlow);

            // 20% gem quality grants +20% skill_buff_effect_+% (scaling slow and attack projectile damage taken).
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = 20 } }, item.Sockets[1], item.Sockets[2]]
            });
            b.StatOverrides[Stat.EnsnaringArrowAreaOfEffectPlusPct] = 100;
            var qualitySpec = Assert.IsType<Game.EnsnareSpec>(
                Assert.IsType<ProjectileOffense>(CaptureSkill(b, "Ensnaring Arrow").Payload).Ensnare);
            Assert.Equal(39, qualitySpec.Radius);
            Assert.Equal(18, qualitySpec.AttackTaken);
            Assert.Equal(-48, qualitySpec.NormalSlow);
            b.StatOverrides.Remove(Stat.EnsnaringArrowAreaOfEffectPlusPct);

            // Fixed flat physical damage + no crit so hit deltas are exact.
            b.StatOverrides[Stat.GlobalCannotCrit] = 1;
            b.StatOverrides[Stat.AttackMinimumAddedPhysicalDamage] = 1000;
            b.StatOverrides[Stat.AttackMaximumAddedPhysicalDamage] = 1000;
            b.StatOverrides[Stat.NumberOfAdditionalProjectiles] = -1000;

            var control = new SpawnedMonster("untethered", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
            var tethered = new SpawnedMonster("tethered", new(140, 100, 0), 0, 0, 100000) { Hostile = true };
            b.Player.Area.Monsters.Clear();
            b.Player.Area.Monsters[210] = tethered;

            // Tether the stationary victim with 20% quality Ensnaring Arrow (+18% projectile attack damage taken).
            var snareUse = CaptureSkill(b, "Ensnaring Arrow");
            b.Session.Runtime.Release(snareUse);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.True(Game.IsEnsnared(tethered, .4));
            Assert.Equal(18, Game.EnsnaredProjectileAttackTaken(tethered, .4));

            // Rain of Arrows (non-projectile area attack) gets NO Ensnaring Arrow bonus.
            b.Player.Area.Monsters[209] = control;
            var fixedHit = DamagePacket.Of(DamageType.Physical, 1000, 1000);
            var rainUse = CaptureSkill(b, "Rain of Arrows");
            rainUse = rainUse with { Payload = Assert.IsType<ProjectileOffense>(rainUse.Payload) with { Hit = fixedHit, AreaHit = fixedHit } };
            uint controlBefore = control.Life, tetheredBefore = tethered.Life;
            b.Session.Runtime.Impact(rainUse, new(120, 100), new("RainOfArrows", 5));
            b.Session.Runtime.Impact(rainUse, new(140, 100), new("RainOfArrows", 5));
            Assert.Equal(1000u, controlBefore - control.Life);
            Assert.Equal(1000u, tetheredBefore - tethered.Life);

            // Split Arrow (projectile attack) DOES get the +18% Ensnaring Arrow bonus.
            var splitUse = CaptureSkill(b, "Split Arrow");
            splitUse = splitUse with
            {
                Payload = Assert.IsType<ProjectileOffense>(splitUse.Payload) with { Hit = fixedHit },
                Profile = splitUse.Profile with { Pierce = 1 }
            };
            controlBefore = control.Life;
            tetheredBefore = tethered.Life;
            b.Session.Runtime.Release(splitUse);
            b.Session.Runtime.AdvanceTo(ProjectileRuntime.Time(.8));
            Assert.Equal(1000u, controlBefore - control.Life);
            Assert.Equal(1180u, tetheredBefore - tethered.Life);

            // Moving-Bleed synergy: stationary ensnared monster takes 3x bleed damage and does not stack with Aggravate.
            Assert.Equal(1d, Game.BleedTaken(control, .8), 6);
            Assert.Equal(2d, Game.AggravatedExtra(control, .8), 6);
            Assert.Equal(3d, Game.BleedTaken(tethered, .8), 6);
            Assert.Equal(0d, Game.AggravatedExtra(tethered, .8), 6);
            Assert.True(tethered.Bleeds.Add(.8, 100, 5));
            b.Player.Area.ActiveBleedMonsters.Add(210);
            uint beforeBleed = tethered.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(1.8));
            await Game.BleedMonsters(b.Player);
            Assert.Equal(300u, beforeBleed - tethered.Life);
        }
    }

    sealed class Battle : IDisposable
    {
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly SpellTotemExecutionTests.Wire wire = new();
        readonly IDisposable exemptions;
        readonly IDisposable statSources;
        public readonly Dictionary<uint, int> StatOverrides = new() { [Stat.GlobalAlwaysHit] = 1 };
        public Func<InventoryItem, bool> ExemptItem = _ => true;
        public readonly Player Player;
        public readonly Game.ProjectileSession Session;
        public readonly SpawnedMonster Enemy = new("target", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
        public readonly SpawnedMonster Ally = new("friendly", new(120, 100, 0), 0, 0, 100000) { Hostile = false };

        public Battle(params string[] gems)
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            string name = "Rain" + Guid.NewGuid().ToString("N");
            Player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Connection = { LockstepClient = false, Session = new("rain", wire.Stream, wire.Ciphers) },
                Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
                PlayerLife = 1000,
                PlayerMana = 1000,
                Instance = new(),
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
            };
            exemptions = GameEvents.RequirementExemptions.Add("Game",
                p => ReferenceEquals(p, Player)
                    ? [.. Game.ItemsOf(p).EquipmentSnapshot().Where(e => ExemptItem(e.Item)).Select(e => e.Item.Id)]
                    : []);
            statSources = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, Player) || StatOverrides.Count == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                foreach (var (key, value) in StatOverrides) copy[key] = value;
                return copy;
            });
            Player.Clock.Stop();
            Player.Walk.Teleport(100, 100, 0);
            var items = Game.ItemsOf(Player);
            var sockets = gems.Select(gemName =>
            {
                var gem = Program.Items.FindGem(gemName)!.Value;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
            }).ToArray();
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            { Sockets = sockets, Links = [(byte)sockets.Length] });
            Player.Area.Monsters[200] = Enemy;
            Player.Area.Monsters[201] = Ally;
            Session = new Game.ProjectileSession(Player);
            Player.ProjectileRuntimeState = Session;
        }

        public void Dispose()
        {
            Session.Invalidate();
            Player.Closed = true;
            Session.OutputCompleted.GetAwaiter().GetResult();
            statSources.Dispose();
            exemptions.Dispose();
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
            PlayerInventories.Forget(Player.Character.Name!);
            wire.Dispose();
        }
    }

    static readonly MethodInfo CurrentRighteousFireSkillMethod = typeof(Game).GetMethod(
        "CurrentRighteousFireSkillLocked", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly MethodInfo RighteousFireEnemyDpsMethod = typeof(Game).GetMethod(
        "RighteousFireEnemyDps", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly MethodInfo RighteousFireTimedSupportFactorMethod = typeof(Game).GetMethod(
        "RighteousFireTimedSupportFactor", BindingFlags.NonPublic | BindingFlags.Static)!;

    static void BurnRighteousFire(Battle b, RighteousFireState rf, double seconds)
    {
        var c = b.Player;
        double now = Game.AreaTime(c);
        using var scope = new Game.PlayerStatScope(c, fresh: true);
        var stats = new Dictionary<uint, int>(Game.CharacterPlayerStats(c));
        var supported = (Game.SupportedGem?)CurrentRighteousFireSkillMethod.Invoke(null, [c, rf]);
        double perSecond = supported is { } gem
            ? (double)RighteousFireEnemyDpsMethod.Invoke(null, [c, rf, stats, gem.Stats])!
                * (double)RighteousFireTimedSupportFactorMethod.Invoke(null, [rf, now, seconds])!
            : 0;
        var dot = new SkillDamageContext(SkillHitKind.Spell, stats).OverTime(new DamageOverTime(DamageType.Fire, perSecond, seconds));
        var at = c.Walk.Position(now);
        foreach (var (_, m) in c.Area.Monsters)
        {
            if (!m.Hostile || m.Dead || m.Life == 0) continue;
            if (Game.Dist(m.Walk.Position(now), at) > rf.Radius) continue;
            var (damage, carry) = Game.MonsterBurn(m, dot, seconds, now);
            m.Burn = carry;
            if (damage > 0) m.Life = m.Life > (uint)damage ? m.Life - (uint)damage : 0;
        }
    }

    [Fact]
    public void Rain_lands_after_release_and_toxic_pods_damage_only_enemies()
    {
        var aim = new ProjectileVector(120, 100);
        var points = Game.RainLandings("RainOfArrows", aim, 18, 24, [aim + new ProjectileVector(2, 0)], new Random(3));
        Assert.Equal(18, points.Count);
        Assert.Equal(aim, points[0]);
        Assert.Equal(aim + new ProjectileVector(2, 0), points[1]);
        Assert.All(Game.RainLandings("BlastRain", aim, 6, 17, [], new Random(1)), p => Assert.InRange((p - aim).Length, 0, 20));
        using var b = new Battle("Toxic Rain");
        var gem = Game.GemSkillEffects(b.Player).Single();
        Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(1), 0);
        Assert.Equal(100000u, b.Enemy.Life);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        // Put the target under a known pod to verify the fractional DoT and the terminal burst.
        var use = Game.CaptureGemProjectile(b.Player, b.Session, gem.Id, gem, (120, 100), new(1));
        b.Session.PlaceToxicPod(use, aim, 18, 1, 100, 5, 30, null);
        Assert.InRange(Game.ToxicWalkFactor(b.Enemy), .7, .95);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Ally));
        uint landed = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(.95));
        Assert.True(b.Enemy.Life < landed);
        uint ticking = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(1.6));
        Assert.True(b.Enemy.Life < ticking);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Enemy));
        Assert.Equal(100000u, b.Ally.Life);
    }

    [Fact]
    public void Toxic_pods_use_native_linked_multipliers_and_the_actual_casters_restrictions()
    {
        static (long Damage, int More, int Mirage) Run(bool concentrated, bool mirage = false,
            bool linked = true, uint? prohibition = null)
        {
            using var b = concentrated
                ? new Battle("Toxic Rain", "Mirage Archer Support", "Concentrated Effect Support")
                : new Battle("Toxic Rain", "Mirage Archer Support");
            var items = Game.ItemsOf(b.Player);
            if (!linked)
                items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
                    item => item with { Links = [2, 1] });
            // Co-locate every pod and keep fractional damage visible without relying on random spread or hit rolls.
            b.StatOverrides[Stat.ActiveSkillBaseRadiusPlus] = -24;
            b.StatOverrides[Stat.DamageOverTimePlusPct] = 9900;
            if (prohibition is uint prohibitedStat) b.StatOverrides[prohibitedStat] = 1;
            var gem = Game.GemSkillEffects(b.Player).Single();
            var own = Game.SupportedSkill(b.Player, gem.Id).Stats;
            int more = own.GetValueOrDefault(Stat.SupportAreaConcentrateAreaDamagePlusPctFinal);
            int penalty = own[Stat.SupportMirageArcherDamagePlusPctFinal];
            Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(1), 0, mirage);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            uint landed = b.Enemy.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(.95));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(1000u, b.Player.PlayerMana);
            return (landed - b.Enemy.Life, more, penalty);
        }

        var baseline = Run(false);
        var supported = Run(true);
        Assert.True(baseline.Damage > 50);
        Assert.True(supported.More > 0);
        double expected = baseline.Damage * (1 + supported.More / 100d);
        Assert.InRange(supported.Damage, Math.Floor(expected) - 3, Math.Ceiling(expected) + 3);
        Assert.Equal(baseline.Damage, Run(true, linked: false).Damage);
        var proxy = Run(true, mirage: true);
        Assert.True(proxy.Mirage < 0);
        expected = supported.Damage * (1 + proxy.Mirage / 100d);
        Assert.InRange(proxy.Damage, Math.Floor(expected) - 3, Math.Ceiling(expected) + 3);
        foreach (uint ownerOnly in new[] { Stat.KeystoneAncestralBond, Stat.DealNoDamageYourself })
        {
            Assert.Equal(0, Run(true, prohibition: ownerOnly).Damage);
            Assert.Equal(proxy.Damage, Run(true, mirage: true, prohibition: ownerOnly).Damage);
        }
        foreach (uint prohibited in new[] { Stat.BaseDealNoChaosDamage, Stat.BaseDealNoDamage })
        {
            Assert.Equal(0, Run(true, prohibition: prohibited).Damage);
            Assert.Equal(0, Run(true, mirage: true, prohibition: prohibited).Damage);
        }
    }

    [Theory]
    [InlineData("Frenzy", 11610, 11600, 17410)]
    [InlineData("Tornado Shot", 10110, 10100, 15160)]
    [InlineData("Rain of Arrows", 4860, 4900, 7310)]
    [InlineData("Blast Rain", 3030, 3000, 4530)]
    [InlineData("Toxic Rain", 5120, 5100, 7670)]
    [InlineData("Split Arrow", 12120, 12120, 18180)]
    public async Task Attack_coefficients_separate_weapon_base_from_added_damage(string skill, int basis, int added, double expected)
    {
        using var b = new Battle(skill);
        var items = Game.ItemsOf(b.Player);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        {
            Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.Sockets[0].Gem!.TypeHash)!)!.ExperienceFor(2) } }]
        });
        var gem = Game.GemSkillEffects(b.Player).Single();
        Assert.Equal(2, gem.Level);
        b.StatOverrides[Stat.AttackMinimumAddedPhysicalDamage] = 5000;
        b.StatOverrides[Stat.AttackMaximumAddedPhysicalDamage] = 5000;
        var weapon = new SkillCatalog.Unarmed(1000, 10000, 10000, 80) { Bow = true };
        // Independently calculated from the level-2 table offsets: e.g. Frenzy 1610 / 1600 gives
        // 10000 * 1.161 + 5000 * 1.16 = 17410, rather than scaling the whole 15000 by 1.16.
        var inputs = Game.SupportedHitInputsFor(b.Player, gem.Id, weapon: weapon)!;
        Assert.Equal(expected, inputs.Raw[DamageType.Physical].Min, 6);
        Assert.Equal(expected, inputs.Raw[DamageType.Physical].Max, 6);
        Assert.Equal((basis, added), Game.SkillsFor(b.Player).DamageCoefficients(gem.Effect, gem.Level));
        Assert.Equal((10000, 10000), Game.SkillsFor(b.Player).DamageCoefficients(-1, 2));
        int tornado = Game.SkillsFor(b.Player).EffectRow("TornadoShot");
        Assert.Equal((10000, 10000), Game.SkillsFor(b.Player).DamageCoefficients(tornado, 1));

        var additions = new Dictionary<uint, int>
        {
            [Stat.AttackMinimumAddedPhysicalDamage] = 5000, [Stat.AttackMaximumAddedPhysicalDamage] = 5000,
            [Stat.GlobalMinimumAddedColdDamage] = 300, [Stat.GlobalMaximumAddedColdDamage] = 300,
            [Stat.AttackMinimumAddedFireDamageWithBow] = 400, [Stat.AttackMaximumAddedFireDamageWithBow] = 400,
            [Stat.Of("attack_minimum_added_fire_damage_with_bows")] = 300, [Stat.Of("attack_maximum_added_fire_damage_with_bows")] = 300,
            [Stat.MaximumMana] = 1000, [DamageStats.MaxManaAddedLightning] = 6,
            [Stat.MaximumLife] = 1000, [Stat.AddedFireDamageToAttacksEqualToPctMaximumLife] = 21,
        };
        int Get(uint name) => additions.GetValueOrDefault(name);
        var local = DamagePacket.Of(DamageType.Physical, 10000, 10000).Add(DamageType.Cold, new(1000, 1000));
        foreach (var localWeapon in new[]
        {
            weapon with { LocalDamage = local, LocalAdded = DamagePacket.Of(DamageType.Cold, 9999, 9999) },
            weapon with { LocalAdded = DamagePacket.Of(DamageType.Cold, 1000, 1000) },
        })
        {
            var packet = SupportedAttackDamage.Raw(localWeapon, Get, new(0), basis, added);
            Assert.Equal(expected, packet[DamageType.Physical].Min, 6);
            Assert.Equal((1000L * basis + 300L * added) / 10000d, packet[DamageType.Cold].Min, 6);
            Assert.Equal(910L * added / 10000d, packet[DamageType.Fire].Min, 6);
            Assert.Equal(60L * added / 10000d, packet[DamageType.Lightning].Min, 6);
            var ordinary = SupportedAttackDamage.Raw(localWeapon, Get, new(0));
            Assert.Equal(15000, ordinary[DamageType.Physical].Min);
            Assert.Equal(1300, ordinary[DamageType.Cold].Min);
            Assert.Equal(5000L * added / 10000d,
                SupportedAttackDamage.Raw(localWeapon, Get, new(0), 0, added)[DamageType.Physical].Min, 6);
        }
        // Attack coefficients never scale a spell's own base, and attack/pool/bow additions stay out of spells.
        var spell = SpellDamage.Packet(DamagePacket.Of(DamageType.Cold, 100, 100), Get, addedEffectiveness: 3);
        Assert.Equal(1000, spell[DamageType.Cold].Min);
        Assert.Equal(0, spell[DamageType.Physical].Min);
        Assert.Equal(0, spell[DamageType.Fire].Min);
        Assert.Equal(0, spell[DamageType.Lightning].Min);

        if (skill != "Rain of Arrows") return;
        // An equipped local affix builds a fixed 10000 physical weapon through ItemWeapons, not a substituted hit.
        var plain = Game.WeaponFor(b.Player);
        var affix = Program.Items.Mods.ById("LocalAddedPhysicalDamage1")!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        { Mods = [new ItemMod(ModSection.Explicit, affix.Hash, [10000 - plain.MinDamage, 10000 - plain.MaxDamage])] });
        Assert.Equal(10000, Game.WeaponFor(b.Player).LocalDamage![DamageType.Physical].Min);
        Assert.Equal(10000, Game.WeaponFor(b.Player).LocalDamage![DamageType.Physical].Max);
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.AreaDamagePlusPct] = 100;
        b.StatOverrides[Stat.NumberOfAdditionalProjectiles] = -1000;
        var use = Game.CaptureGemProjectile(b.Player, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(7310, shot.Hit.MinTotal, 6);
        Assert.Equal(7310, shot.Hit.MaxTotal, 6);
        Assert.Equal(14620, shot.AreaHit!.MinTotal, 6);
        var panel = Game.PlayerSkills(b.Player).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(14620, panel[Stat.MainHandMinimumPhysicalDamage]);
        Assert.Equal(14620, panel[Stat.MainHandMaximumPhysicalDamage]);
        Assert.Equal(1462000, panel[Stat.DisplayHundredTimesDamagePerSkillUse]);
        Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 0);
        b.StatOverrides[Stat.AttackMinimumAddedPhysicalDamage] = 0;
        b.StatOverrides[Stat.AttackMaximumAddedPhysicalDamage] = 0;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
        Assert.True(Game.SupportedHitFor(b.Player, gem.Id, suppress: true).MaxTotal < 14620);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        Assert.Equal(100000u - 14620, b.Enemy.Life);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Weapon_cold_per_dexterity_stays_with_its_source();
        Spellblade_takes_weapon_damage_without_global_attack_inputs();
        Spellblade_requires_an_active_one_hand_melee_weapon();
        Spellblade_transfers_each_active_weapon_before_spell_scaling();
        Accuracy_boots_use_resolved_skill_and_hand_inputs();
        Fan_of_Blades_reaches_projectile_count_and_attack_damage();
        Different_weapon_mastery_reaches_shared_damage();
        Physical_dot_taken_combines_real_curse_and_debuff_increases();
        Maim_settles_elapsed_bleeding_before_changing_target_damage();
        Monster_elemental_modifiers_reach_damage_over_time();
        Righteous_fire_uses_player_damage_modifiers();
        await Righteous_fire_uses_its_current_burning_damage_support();
        await Righteous_fire_combines_its_admitted_supports();
        await Righteous_fire_settles_its_timed_supports_between_skill_payments();
        Claw_and_dagger_projectiles_keep_the_released_hand_and_source();
    }

    static void Maim_settles_elapsed_bleeding_before_changing_target_damage()
    {
        foreach (bool projectile in new[] { false, true })
        foreach (bool previousMaim in new[] { false, true })
        {
            using var b = new Battle("Rain of Arrows", "Maim Support");
            var c = b.Player;
            var m = b.Enemy;
            var gem = Game.GemSkillEffects(c).Single();
            var own = Game.SupportedSkill(c, gem.Id);
            Assert.True(own.RequirementsMet);
            Assert.Equal(30, own.Stats[Stat.MaimOnHitPct]);
            Assert.Equal(10, own.Stats[Stat.SupportMaimedEnemiesPhysicalDamageTakenPlusPct]);
            void Apply(double at) => Game.HitDebuffsOnHitLocked(m, n => n == Stat.MaimOnHitPct ? 100
                : own.Stats.GetValueOrDefault(n),
                spell: false, critical: false, now: at, new Random(1));
            if (previousMaim) Apply(0); // Its expiry coincides with Rupture's at t=4.
            int dps = projectile ? 101 : 100; // Keep the fractional landing away from an integer damage boundary.
            Assert.True(m.Bleeds.Add(1, dps, 10)); // Controlled final DPS isolates target changes.
            c.Area.ActiveBleedMonsters.Add(200);
            m.Ruptures.Add(1, 25);
            m.Bleeds.SetExpiryRate(1, Game.BleedingExpiryRate(m, 1));
            double contact = 5;
            if (projectile)
            {
                // The native support supplies 30; guarantee only the chance, retaining its real target 10.
                b.StatOverrides[Stat.MaimOnHitPct] = 70;
                b.StatOverrides[Stat.GlobalCannotCrit] = 1;
                b.Session.AdvanceTo(ProjectileRuntime.Time(5));
                Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 5);
                b.Session.AdvanceTo(ProjectileRuntime.Time(5.35)); // Only the first, centered landing.
                Assert.True(m.Life < m.MaxLife);
                contact = m.MaimedUntil - Game.MaimSeconds;
                Assert.Equal(5.35, contact, 6);
                Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
                Assert.Equal(double.NegativeInfinity, b.Ally.MaimedUntil);
            }
            else
            {
                Apply(contact);
                Assert.Null(Game.BleedOnHitLocked(c, 200, m, new(0, 0, 0, 0, 0, 0), critical: false, now: contact));
            }
            Assert.Equal(10, Game.MaimedPhysicalTaken(m, contact));
            Assert.Empty(m.Ruptures.Active);
            // Only the old Maim can affect the three ruptured seconds; t=4 to contact has no Maim.
            double earned = 3 * dps * 1.25 * (previousMaim ? 1.1 : 1) + (contact - 4) * dps;
            Assert.Equal((int)Math.Floor(earned), m.Bleeds.Advance(contact, 1)); // First control: 475, not 522.

            // The new Maim still increases subsequent bleed damage, through the normal life-update tick.
            const double nextTick = 6.125;
            b.Session.AdvanceTo(ProjectileRuntime.Time(nextTick));
            uint before = m.Life, playerLife = c.PlayerLife;
            Game.BleedMonsters(c).GetAwaiter().GetResult();
            Assert.Equal((uint)(Math.Floor(earned + (nextTick - contact) * dps * 1.1) - Math.Floor(earned)), before - m.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
    }

    static void Physical_dot_taken_combines_real_curse_and_debuff_increases()
    {
        using var b = new Battle("Vulnerability", "Puncture", "Maim Support");
        var c = b.Player;
        var m = b.Enemy;
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Vulnerability");
        var own = Game.SupportedSkill(c, gem.Id);
        Assert.True(own.RequirementsMet);
        Assert.Equal(15, own.Stats[Stat.PhysicalDamageTakenPlusPct]);
        Assert.False(Game.ApplyCurseLocked(c, b.Ally, gem.Id, 0, aura: false));
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 0, aura: false));
        Assert.Equal(15, m.Effects[Stat.PhysicalDamageTakenPlusPct]);
        Assert.Equal(0, m.Mods.Effects[Stat.PhysicalDamageTakenPlusPct]);
        var curse = Assert.Single(m.Curses.All.Values);
        Assert.Equal((ushort)79, curse.Row);
        Assert.Equal(8, curse.Until);
        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 0, _ => 0, _ => 0, stated: 20);
        Assert.Equal(20, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 1));
        Assert.True(m.Bleeds.Add(0, 100, 5)); // Controlled final DPS isolates target scaling from attack rolls.
        c.Area.ActiveBleedMonsters.Add(200);
        void Tick(double to, uint expected)
        {
            uint before = m.Life, playerLife = c.PlayerLife;
            b.Session.AdvanceTo(ProjectileRuntime.Time(to));
            Game.BleedMonsters(c).GetAwaiter().GetResult();
            Assert.Equal(expected, before - m.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
        Tick(1, 135); // Before the fix this actual bleed tick dealt 138.
        m.Curses.Remove(c.Id, "Vulnerability");
        Assert.Equal(0, m.Effects[Stat.PhysicalDamageTakenPlusPct]);
        Tick(1.5, 60);
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 1.5, aura: false));
        Assert.Equal(15, m.Effects[Stat.PhysicalDamageTakenPlusPct]);

        var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Puncture");
        var supported = Game.SupportedSkill(c, attack.Id);
        Assert.Equal(30, supported.Stats[Stat.MaimOnHitPct]);
        Assert.Equal(10, supported.Stats[Stat.SupportMaimedEnemiesPhysicalDamageTakenPlusPct]);
        Assert.Equal(10, supported.Stats[Stat.SupportMaimChancePhysicalDamagePlusPctFinal]);
        // Guarantee only the chance; the admitted support supplies the target magnitude through the real hit handler.
        Game.HitDebuffsOnHitLocked(m, n => n == Stat.MaimOnHitPct ? 100
            : supported.Stats.GetValueOrDefault(n),
            spell: false, critical: false, now: 1.5, new Random(1));
        Assert.Equal(10, m.MaimedPhysicalTakenPct);
        Assert.Equal(5.5, m.MaimedUntil);
        Tick(2.5, 145); // Curse 15 + shock 20 + Maim 10; support's source-side MORE is not another target factor.
        foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos })
            Assert.Equal(1.2, Game.MonsterDotTaken(m, type, 2.5), 6);
        void Factor(double at, double expected) => Assert.Equal(expected, Game.MonsterDotTaken(m, DamageType.Physical, at), 6);
        m.PridePhysicalMore = 25;
        Factor(2.5, 1.8125); // (1 + .15 + .2 + .1) * 1.25, not an increased-taken contribution.
        m.TakenFromPlayer = (All: 0, Elemental: 100, Physical: 50);
        m.IntimidatedUntil = m.UnnervedUntil = 20;
        m.EncounterStats = new Dictionary<uint, int> { [Stat.RareMonsterModSpellDamageTakenPlusPctFinal] = -90 };
        Factor(2.5, 1.8125); // Elemental auras, Flesh and Stone and the hit-only debuffs do not alter physical DoT.
        Factor(4, 1.5625); // Shock expired, Maim and the curse remain.
        Factor(5.5, 1.4375); // Maim expired; the curse remains.
        m.Curses.Expire(9.5);
        Assert.Empty(m.Curses.All);
        Factor(9.5, 1.25);
        m.PridePhysicalMore = 0;
        m.TakenFromPlayer = default;
        m.EncounterStats = null;

        // New applications replace warm recipient stats and honor actual curse-effect scaling.
        b.StatOverrides[Stat.CurseEffectPlusPct] = 50;
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(22, m.Effects[Stat.PhysicalDamageTakenPlusPct]); // Truncated once by the existing curse consumer.
        m.TakenFromPlayer = (All: 20, Elemental: 0, Physical: 0);
        Factor(10, 1.42);
        m.Mods = new([], [], new MonsterModEffects(new Dictionary<uint, int> { [Stat.CurseEffectOnSelfPlusPct] = -50 }));
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(15, m.Effects[Stat.PhysicalDamageTakenPlusPct]);
        Factor(10, 1.35);
        b.StatOverrides.Remove(Stat.CurseEffectPlusPct);
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(7, m.Effects[Stat.PhysicalDamageTakenPlusPct]);
        Factor(10, 1.27);
        m.Curses.Clear();
        m.Mods = new([], [], new MonsterModEffects(new Dictionary<uint, int> { [Stat.Hexproof] = 1 }));
        Assert.False(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Factor(10, 1.2);
        m.Mods = MonsterModSet.None;
        m.TakenFromPlayer = default;

        void Taken(params (uint Stat, int Value)[] stats) =>
            m.EncounterStats = stats.ToDictionary(s => s.Stat, s => s.Value);
        Taken((Stat.BaseDamageTakenPlusPct, 50), (Stat.PhysicalDamageTakenPlusPct, -100));
        Factor(10, .5); // Clamp the combined bucket, not the typed contribution first.
        Taken((Stat.BaseDamageTakenPlusPct, 20), (Stat.PhysicalDamageTakenPlusPct, -150));
        Factor(10, 0);
        Assert.True(Game.TryMaimOnHitLocked(m, 100, 25, 10, new Random(17)));
        Taken((Stat.BaseDamageTakenPlusPct, 20), (Stat.PhysicalDamageTakenPlusPct, 15));
        Factor(10, 1.6);
        Factor(14, 1.35);
        Taken((Stat.BaseDamageTakenPlusPct, 20), (Stat.PhysicalDamageTakenPlusPct, 15),
            (Stat.MapMonsterDamageTakenPlusPctFinalFromAtlasKeystone, -40));
        Factor(14, .81);
        Taken((Stat.PhysicalDamageTakenPlusPct, 500), (Stat.BaseCannotBeDamaged, 1));
        Factor(14, 0);
    }

    static void Monster_elemental_modifiers_reach_damage_over_time()
    {
        // Precompiled Mods bind to this exact public signature, not to optional-argument source syntax.
        Assert.NotNull(typeof(Game).GetMethod(nameof(Game.MonsterDamageTaken),
            [typeof(SpawnedMonster), typeof(bool), typeof(double), typeof(bool), typeof(bool), typeof(bool)]));
        using var b = new Battle("Righteous Fire");
        var c = b.Player;
        var m = b.Enemy;
        c.Pools = Game.PoolsFor(c, 100);
        m.Walk.Teleport(110, 100, 0);
        b.Ally.Walk.Teleport(110, 100, 0);
        var fire = IzaroEffects.Of(IzaroStatues.Mechanic, 1, returning: false).Single(e => e.Element == 0);
        Assert.Equal("ToggleStatuesNormal_Fire", fire.Id);
        Assert.Equal((ushort)559, fire.Buff);
        Assert.Equal(-20, fire.Stats[Stat.FireDamageTakenPlusPct]);
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.Equal(-20, m.Effects[Stat.FireDamageTakenPlusPct]);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.ActivateRighteousFire(c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem).GetAwaiter().GetResult();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        double baseDps = (c.PlayerMaxLife + c.Pools.EnergyShield) * .7;
        void Burn(double factor)
        {
            m.Life = m.MaxLife;
            m.Burn = 0;
            uint playerLife = c.PlayerLife;
            BurnRighteousFire(b, rf, .5);
            Assert.Equal((int)Math.Floor(baseDps * factor * .5), (int)(m.MaxLife - m.Life));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
            Assert.DoesNotContain(201u, rf.Auras.Keys);
        }
        Burn(.8); // Real Normal statue: the original failure was 435 instead of 348.
        m.EncounterStats = null;
        Burn(1);
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Burn(.8);

        DamageType[] elements = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
        uint[] takenStats = [Stat.FireDamageTakenPlusPct, Stat.ColdDamageTakenPlusPct, Stat.LightningDamageTakenPlusPct];
        foreach (int tier in new[] { 1, 2, 3, 4 })
        foreach (bool returning in new[] { false, true })
        foreach (var effect in IzaroEffects.Of(IzaroStatues.Mechanic, tier, returning))
        {
            Assert.Equal(-20, effect.Stats[takenStats[effect.Element]]);
            m.EncounterStats = IzaroEffects.Sum([effect]);
            foreach (var type in elements)
                Assert.Equal(type == elements[effect.Element] ? .8 : 1, Game.MonsterDotTaken(m, type), 6);
            Assert.Equal(1, Game.MonsterDotTaken(m, DamageType.Physical));
            Assert.Equal(1, Game.MonsterDotTaken(m, DamageType.Chaos));
            Burn(effect.Element == 0 ? .8 : 1);
        }

        void Taken(params (uint Stat, int Value)[] stats) =>
            m.EncounterStats = stats.ToDictionary(s => s.Stat, s => s.Value);
        // Typed and general increases share one bucket, including signed reductions and the final zero clamp.
        Taken((Stat.BaseDamageTakenPlusPct, 30), (Stat.FireDamageTakenPlusPct, 20),
            (Stat.ColdDamageTakenPlusPct, -10), (Stat.LightningDamageTakenPlusPct, -50));
        Assert.Equal(1.5, Game.MonsterDotTaken(m, DamageType.Fire), 6);
        Assert.Equal(1.2, Game.MonsterDotTaken(m, DamageType.Cold), 6);
        Assert.Equal(.8, Game.MonsterDotTaken(m, DamageType.Lightning), 6);
        Assert.Equal(1.3, Game.MonsterDotTaken(m, DamageType.Physical), 6);
        Assert.Equal(1.3, Game.MonsterDotTaken(m, DamageType.Chaos), 6);
        Burn(1.5);
        Taken((Stat.BaseDamageTakenPlusPct, 50), (Stat.FireDamageTakenPlusPct, -100));
        Burn(.5);
        Taken((Stat.BaseDamageTakenPlusPct, 20), (Stat.FireDamageTakenPlusPct, -150));
        Burn(0);

        Taken((Stat.BaseDamageTakenPlusPct, 20), (Stat.FireDamageTakenPlusPct, -20));
        m.TakenFromPlayer = (All: 10, Elemental: 30, Physical: 0);
        Assert.Equal(1.4, Game.MonsterDotTaken(m, DamageType.Fire, 0), 6);
        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 0, _ => 0, _ => 0, stated: 20);
        Assert.Equal(1.6, Game.MonsterDotTaken(m, DamageType.Fire, 0), 6);
        Assert.Equal(1.4, Game.MonsterDotTaken(m, DamageType.Fire, 4), 6);
        m.TakenFromPlayer = default;
        // Use a time after the controlled shock expires for the remaining factor checks.
        void Factor(double expected) => Assert.Equal(expected, Game.MonsterDotTaken(m, DamageType.Fire, 4), 6);
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.BaseFireDamageResistancePct, 50));
        Factor(.4);
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.BaseFireDamageResistancePct, 200));
        Factor(.2);
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.BaseFireDamageResistancePct, 200),
            (Stat.AdditionalMaximumAllElementalResistancesPct, 10));
        Factor(.12);
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.BaseFireDamageResistancePct, -20));
        Factor(.96);
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.RareMonsterModSpellDamageTakenPlusPctFinal, -90));
        Factor(.8); // RF and ignites are not spell hits.
        Taken((Stat.FireDamageTakenPlusPct, -20), (Stat.MapMonsterDamageTakenPlusPctFinalFromAtlasKeystone, -40));
        Factor(.48);
        Taken((Stat.FireDamageTakenPlusPct, 500), (Stat.BaseCannotBeDamaged, 1));
        Factor(0);

        // The shared consumer also feeds ignites; changing the target does not replace the applied ignite.
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.True(m.Ignites.Add(4, 100, 4));
        Assert.Equal(80, m.Ignites.Advance(5, Game.MonsterDotTaken(m, DamageType.Fire, 5)));
        m.EncounterStats = null;
        Assert.Equal(100, m.Ignites.Advance(6, Game.MonsterDotTaken(m, DamageType.Fire, 6)));
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.Equal(80, m.Ignites.Advance(7, Game.MonsterDotTaken(m, DamageType.Fire, 7)));
    }

    sealed class ProjectileClock : IPlayerUpdateSchedule
    {
        public readonly Queue<Action> Steps = [];
        public double Now(Player player) => Game.ProjectileWorldTime(player);
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            Steps.Dequeue()();
            return Task.CompletedTask;
        }
    }

    static async Task SwapRighteousFireSocket(Battle b, uint host, int index, string name, byte quality = 0)
    {
        var items = Game.ItemsOf(b.Player);
        var gem = Program.Items.FindGem(name)!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index, Quality = quality });
        await Game.SocketGem(b.Player, new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host, (uint)index));
        items.Remove(PlayerInventories.Cursor, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
    }

    static async Task Righteous_fire_settles_its_timed_supports_between_skill_payments()
    {
        using var b = new Battle("Righteous Fire", "Lifetap Support", "Inspiration Support", "Tornado Shot", "Lifetap Support");
        var c = b.Player;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { ItemLevel = 80, Links = [3, 2] });
        c.Pools = Game.PoolsFor(c, 100);
        b.Enemy.Walk.Teleport(105, 100, 0);
        b.Ally.Walk.Teleport(105, 100, 0);
        var gems = Game.GemSkillEffects(c);
        var gem = gems.Single(g => g.Name == "Righteous Fire");
        var payer = gems.Single(g => g.Name == "Tornado Shot");
        var own = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal(10, own[Lifetap.DamageStat]);
        Assert.Equal(3, own[Stat.ElementalDamagePlusPctFinalPerRighteousCharge]);
        c.Clock.Start();
        await Game.OnUseObject(c, new(c.Id, gem.Id, 1, 0), Game.AreaTime(c));
        c.Clock.Stop();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        double origin = Math.Ceiling(Math.Max(c.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(c))) + 1;
        void At(double time) => b.Session.AdvanceTo(ProjectileRuntime.Time(origin + time));
        void Pay(uint mana, uint life)
        {
            Assert.True(Game.PayMana(c, payer.Id, out uint paidMana, out uint paidLife));
            Assert.Equal((mana, life), (paidMana, paidLife));
        }
        void Tick(double end, double factor)
        {
            At(end);
            AssertRighteousFireTick(b, rf, factor);
        }
        Task Swap(int slot, string name) => SwapRighteousFireSocket(b, host.Id, slot, name);
        var previous = Game.AreaUpdateContext.Value;
        var clock = new ProjectileClock();
        var updates = new PlayerUpdatesState(Game.CombatLifetime!, clock);
        Game.AreaUpdateContext.Value = updates;
        try
        {
            Tick(1, 1);
            At(2);
            Pay(0, 27);
            Assert.Equal(origin + 6, c.Lifetap.Until);
            Tick(2.25, 1.05); // First half of this interval preceded the life payment.
            Tick(2.75, 1.1);

            await Swap(4, "Inspiration Support");
            At(3.125);
            Pay(8, 0);
            At(3.375);
            Pay(8, 0);
            Assert.Equal(2, c.SupportGems.InspirationCharges);
            Tick(3.5, 1.1 * (.25 + .5 * 1.03 + .25 * 1.06));

            At(5);
            await Swap(4, "Lifetap Support");
            Pay(0, 27);
            Assert.Equal(origin + 9, c.Lifetap.Until);
            Tick(6.25, 1.1 * 1.06); // A refresh spans the former deadline.
            Tick(9.25, 1.05 * 1.06);
            Tick(10, 1.06);

            await Swap(4, "Inspiration Support");
            for (int i = 0; i < 13; i++) Pay(8, 0);
            Assert.Equal((5, 120L), (c.SupportGems.InspirationCharges, c.SupportGems.InspirationSpent));
            At(10.25);
            Pay(8, 0); // The 122-Mana threshold clears all five charges.
            Assert.Equal((0, 0L), (c.SupportGems.InspirationCharges, c.SupportGems.InspirationSpent));
            Tick(10.5, 1.075);

            At(11);
            Pay(8, 0);
            At(11.25);
            await Swap(2, "Arcane Surge Support");
            Tick(11.5, 1.015);
            At(11.75);
            await Swap(2, "Inspiration Support");
            Tick(12, 1.015);
            Assert.Same(rf, c.RighteousFire);

            At(21.25);
            c.Connection.PlayerSent = true;
            await Game.SendSupportGemBuffs(c);
            Assert.Equal(0, c.SupportGems.InspirationCharges);
            Tick(21.25, 1.015); // The player tick published expiry before settling damage.

            At(22);
            Pay(8, 0);
            At(32.25);
            Pay(8, 0); // No presentation tick since the previous charges expired.
            Assert.Equal(1, c.SupportGems.InspirationCharges);
            Tick(32.5, 1.015);

            At(33);
            await Swap(0, "Righteous Fire");
            Assert.Null(c.RighteousFire);
            gem = Game.GemSkillEffects(c).Single(g => g.Name == "Righteous Fire");
            await Game.ActivateRighteousFire(c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem);
            rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
            Tick(33.75, 1.03); // The new source starts while an existing Inspiration charge is active.

            // A payment arriving after [43, 43.5] settles only applies to the subsequent interval.
            Tick(43.5, 1);
            At(43.75);
            Assert.True(Game.PayMana(c, payer.Id, out uint laterMana, out _));
            Assert.Equal(8u, laterMana);
            Tick(44.25, 1.03);
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
            if (ReferenceEquals(c.PlayerUpdatesState, updates)) c.RetirePlayerUpdates();
            updates.Stop.Dispose();
        }
    }

    static int RighteousFireHalfSecondDamage(Player c, RighteousFireState rf, double factor)
    {
        double basis = (c.PlayerMaxLife * (double)rf.DamagePctPerMinute
            + c.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000;
        return (int)Math.Floor(basis * factor * .5);
    }

    static void AssertRighteousFireTick(Battle b, RighteousFireState rf, double factor)
    {
        var c = b.Player;
        b.Enemy.Burn = 0;
        uint before = b.Enemy.Life, playerLife = c.PlayerLife;
        BurnRighteousFire(b, rf, .5);
        Assert.Equal(RighteousFireHalfSecondDamage(c, rf, factor), (int)(before - b.Enemy.Life));
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(playerLife, c.PlayerLife);
    }

    static async Task Righteous_fire_combines_its_admitted_supports()
    {
        using var b = new Battle("Righteous Fire", "Swift Affliction Support", "Elemental Focus Support",
            "Concentrated Effect Support", "Efficacy Support", "Less Duration Support");
        var c = b.Player;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        {
            ItemLevel = 80,
            Sockets = item.Sockets.Select((socket, i) => i == 0 ? socket
                : socket with { Gem = socket.Gem! with { Quality = 20 } }).ToArray(),
        });
        c.Pools = Game.PoolsFor(c, 100);
        b.StatOverrides[Stat.FireDamagePlusPct] = 25;
        b.Enemy.Walk.Teleport(105, 100, 0);
        b.Ally.Walk.Teleport(105, 100, 0);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.SupportedGem Current() => Game.SupportedSkill(c, gem.Id);
        var start = Current();
        Assert.Equal(new[] { "SupportElementalFocus", "SupportConcentratedEffect", "SupportEfficacy" },
            start.Supports.Select(s => s.Effect.Id));
        Assert.Equal((20, 25, 15), (start.Stats[Stat.SupportGemElementalDamagePlusPctFinal],
            start.Stats[Stat.SupportAreaConcentrateAreaDamagePlusPctFinal],
            start.Stats[Stat.SupportEfficacyDamageOverTimePlusPctFinal]));
        Assert.Equal((10, 10, 10), (start.Stats[Stat.ElementalDamagePlusPct],
            start.Stats[Stat.AreaDamagePlusPct], start.Stats[Stat.DamageOverTimePlusPct]));
        Assert.NotEqual(0, start.Stats[Stat.CannotInflictStatusAilments]);
        c.Clock.Start(); // A paused instance refuses skill use.
        await Game.OnUseObject(c, new(c.Id, gem.Id, 1, 0), Game.AreaTime(c));
        c.Clock.Stop();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        void Tick(double factor) => AssertRighteousFireTick(b, rf, factor);
        async Task Replace(int index, string name, byte quality = 0)
        {
            await SwapRighteousFireSocket(b, host.Id, index, name, quality);
            Assert.Same(rf, c.RighteousFire);
        }
        Tick(1.55 * 1.2 * 1.25 * 1.15);
        await Replace(2, "Lifetap Support");
        var duration = Current();
        Assert.Contains(duration.Supports, s => s.Effect.Id == "SupportSwiftAffliction");
        Assert.Contains(duration.Supports, s => s.Effect.Id == "SupportLessDuration");
        Assert.Equal((25, 10), (duration.Stats[Stat.SupportRapidDecayDamageOverTimePlusPctFinal],
            duration.Stats[Stat.SupportReducedDurationDamagePlusPctFinal]));
        Assert.Equal(20, duration.Stats[Stat.DamageOverTimePlusPct]);
        Assert.False(Lifetap.ActiveAt(c, Game.ProjectileWorldTime(c)));
        Tick(1.55 * 1.25 * 1.15 * 1.25 * 1.1);

        foreach (var prohibition in new[] { "Brutality Support", "Void Manipulation Support" })
        {
            await Replace(2, prohibition);
            var forbidden = Current();
            Assert.NotEqual(0, forbidden.Stats[Stat.DealNoElementalDamage]);
            Assert.DoesNotContain(forbidden.Supports, s => s.Effect.Id is "SupportSwiftAffliction" or "SupportLessDuration");
            Tick(0);
        }
        await Replace(2, "Arcane Surge Support", 20);
        Assert.Equal(10, Current().Stats[Stat.SpellDamagePlusPct]);
        Game.GainArcaneSurgeLocked(c, Game.ProjectileWorldTime(c), 4000, 20, 30);
        Assert.True(Game.ArcaneSurgeActive(c, Game.ProjectileWorldTime(c)));
        Tick(1.55 * 1.25 * 1.15 * 1.25 * 1.1);

        // A trigger support can be attached after activation, while the same physical RF is still burning.
        foreach (var (name, stat, value, factor) in new[]
        {
            ("Cast when Damage Taken Support", Stat.CastOnDamageTakenDamagePlusPctFinal, -65, .35),
            ("Cast on Ward Break Support", Stat.CastOnWardBreakDamagePlusPctFinal, -30, 1d), // Spell damage only.
        })
        {
            await Replace(2, name);
            Assert.Equal(value, Current().Stats[stat]);
            Assert.NotNull(Game.TriggerOnlyReason(Current()));
            Tick(1.45 * 1.25 * 1.15 * factor);
        }
        await Replace(2, "Elemental Focus Support", 20);
        Tick(1.55 * 1.2 * 1.25 * 1.15);
        Assert.Equal((5400, 4200), (rf.BurnPctPerMinute, rf.EnergyShieldBurnPctPerMinute));
    }

    static async Task Righteous_fire_uses_its_current_burning_damage_support()
    {
        using var b = new Battle("Righteous Fire", "Burning Damage Support");
        var c = b.Player;
        c.Pools = Game.PoolsFor(c, 100);
        b.Enemy.Walk.Teleport(110, 100, 0);
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        var gem = Assert.Single(Game.GemSkillEffects(c));
        var supported = Game.SupportedSkill(c, gem.Id);
        Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportBurningDamage");
        uint burning = Stat.SupportBurningDamagePlusPctFinal;
        uint increased = Stat.BurnDamagePlusPct;
        b.Ally.Walk.Teleport(110, 100, 0);
        Assert.Equal(20, supported.Stats[burning]);
        await Game.ActivateRighteousFire(c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        var owner = Assert.IsType<GemSourceLifetimes.SourceLifetime>(rf.SourceLifetime);
        void Tick(double factor) => AssertRighteousFireTick(b, rf, factor);
        Tick(1.2);
        var socket = new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 1);
        await Game.UnsocketGem(c, socket);
        Assert.Same(rf, c.RighteousFire);
        Assert.True(c.GemSourceLifetimes.IsCurrent(owner));
        Assert.DoesNotContain(burning, Game.SupportedSkill(c, gem.Id).Stats.Keys);
        Tick(1);
        b.StatOverrides[Stat.FireDamagePlusPct] = 25;
        b.StatOverrides[burning] = 80; // Only a support attached to this skill supplies the more multiplier.
        Tick(1.25);
        var held = Assert.Single(items.Items(PlayerInventories.Cursor));
        items.Change(PlayerInventories.Cursor, held.Id, item => item with { Quality = 20 });
        await Game.SocketGem(c, socket);
        Assert.Same(rf, c.RighteousFire);
        var quality = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal((20, 10), (quality[burning], quality[increased]));
        Tick(1.35 * 1.2);
        var highLevel = Program.Items.FindGem("Burning Damage Support")!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0,
            ItemCatalog.Create(highLevel.Type) with { GemVariant = highLevel.Variant.Index, Quality = 20,
                GemExperience = Program.Items.Gem(highLevel.Type)!.ExperienceFor(20) });
        await Game.SocketGem(c, socket);
        Assert.Same(rf, c.RighteousFire);
        Assert.True(c.GemSourceLifetimes.IsCurrent(owner));
        var swapped = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal((34, 10), (swapped[burning], swapped[increased]));
        Tick(1.35 * 1.34);
        b.StatOverrides[Stat.BaseDealNoFireDamage] = 1;
        Tick(0);
        b.StatOverrides.Remove(Stat.BaseDealNoFireDamage);
        items.Remove(PlayerInventories.Cursor, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
        Assert.Equal((5400, 4200), (rf.BurnPctPerMinute, rf.EnergyShieldBurnPctPerMinute));

        var replacement = Program.Items.FindGem("Righteous Fire")!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0,
            ItemCatalog.Create(replacement.Type) with { GemVariant = replacement.Variant.Index });
        await Game.SocketGem(c, socket with { Socket = 0 });
        Assert.True(owner.Retired);
        Assert.Null(c.RighteousFire);
        Assert.Equal(gem.Id, Assert.Single(Game.GemSkillEffects(c)).Id);
        Tick(0);
    }

    static void Righteous_fire_uses_player_damage_modifiers()
    {
        using var b = new Battle("Righteous Fire");
        var c = b.Player;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            { Sockets = host.Item.Sockets, Links = [1] });
        items.Remove(PlayerInventories.Weapon1, host.Id);
        b.Enemy.Walk.Teleport(110, 100, 0);
        b.Ally.Walk.Teleport(110, 100, 0);
        c.Pools = Game.PoolsFor(c, 100);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.ActivateRighteousFire(c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem).GetAwaiter().GetResult();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        Assert.Equal(4200, rf.DamagePctPerMinute);
        Assert.Equal(4200, rf.EnergyShieldDamagePctPerMinute);
        Assert.Equal(5400, rf.BurnPctPerMinute);
        void Burn(double increase, double factor = 1)
        {
            c.Pools = Game.PoolsFor(c, 100);
            double basis = (c.PlayerMaxLife * (double)rf.DamagePctPerMinute
                + c.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000;
            uint playerLife = c.PlayerLife;
            b.Enemy.Life = b.Enemy.MaxLife;
            b.Enemy.Burn = 0;
            BurnRighteousFire(b, rf, .5);
            Assert.Equal((int)Math.Floor(basis * Math.Max(0, 100 + increase) / 100 * factor * .5),
                (int)(b.Enemy.MaxLife - b.Enemy.Life));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
        Burn(0);
        items.Add(6, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(17))!);
        int ringDamage = Game.CharacterPlayerStats(c)[Stat.DamagePlusPct];
        Assert.InRange(ringDamage, 10, 30);
        Burn(ringDamage);
        // A warmed snapshot must observe removing and restoring a real global source.
        var ring = Assert.Single(items.Items(6));
        items.Remove(6, ring.Id);
        Burn(0);
        items.Add(6, 0, 0, ring.Item);
        Burn(ringDamage);
        Assert.True(c.Pools.EnergyShield > 0);
        c.PlayerLife = Math.Max(2, c.PlayerLife / 2);
        Burn(ringDamage); // Current Life is not the base of the burn.

        var wandType = Program.Items.Find("Driftwood Wand")!;
        var plainWand = ItemCatalog.Create(wandType) with { ItemLevel = 86, Rarity = ItemRarity.Rare };
        var suffix = Program.Items.Mods.ById("GlobalDamageOverTimeMultiplier1h1")!;
        Assert.Contains(new ItemRolls(Program.Items).Pool(wandType, plainWand), p => p.Mod.Id == suffix.Id);
        var suffixRoll = suffix.Roll(ModSection.Explicit, new Random(17));
        int wandMultiplier = suffixRoll.Values[0];
        Assert.InRange(wandMultiplier, 7, 11);
        items.Add(PlayerInventories.Weapon1, 0, 0, plainWand with { Mods = [suffixRoll] });
        Assert.Equal(wandMultiplier, Game.CharacterPlayerStats(c)[Stat.DotMultiplierPlus]);
        Burn(ringDamage, factor: (100 + wandMultiplier) / 100d);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);

        var uniques = new UniqueCatalog(Program.Items);
        var blade = uniques.Create("Fleshrender", new Random(17))!;
        int genericMultiplier = blade.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id
            == "GlobalDamageOverTimeMultiplierUnique__1").Values[0];
        Assert.InRange(genericMultiplier, 20, 40);
        var bladeItem = items.Add(PlayerInventories.Weapon1, 0, 0, blade)!;
        Assert.Equal(genericMultiplier, Game.CharacterPlayerStats(c)[Stat.DotMultiplierPlus]);
        Burn(ringDamage, factor: (100 + genericMultiplier) / 100d);

        var blackflame = uniques.Create("Blackflame", new Random(17))!;
        int fireMultiplier = blackflame.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id
            == "FireDamageOverTimeMultiplierUnique__3").Values[0];
        Assert.InRange(fireMultiplier, 8, 12);
        items.Add(7, 0, 0, blackflame);
        Assert.Equal(fireMultiplier, Game.CharacterPlayerStats(c)[Stat.FireDotMultiplierPlus]);
        double both = (100 + genericMultiplier + fireMultiplier) / 100d;
        Burn(ringDamage, factor: both); // Multipliers add to each other, separately from the ring's increase.
        b.StatOverrides[Stat.FireDamagePlusPct] = 30;
        Burn(ringDamage + 30, factor: both);
        b.StatOverrides.Remove(Stat.FireDamagePlusPct);
        foreach (uint stat in new[] { Stat.IgniteDotMultiplierPlus, Stat.AilmentDotMultiplierPlus,
            Stat.CriticalAilmentDotMultiplierPlus, Stat.CriticalIgniteDotMultiplierPlus, Stat.PhysicalDotMultiplierPlus,
            Stat.ChaosDotMultiplierPlus, Stat.ColdDotMultiplierPlus })
            b.StatOverrides[stat] = 500;
        Burn(ringDamage, factor: both);
        b.StatOverrides[Stat.DotMultiplierPlus] = 7;
        Burn(ringDamage, factor: (100 + 7 + fireMultiplier) / 100d); // Explicit source override replaces the item input.
        b.StatOverrides[Stat.DotMultiplierPlus] = -150;
        Burn(ringDamage, factor: 0);
        b.StatOverrides.Remove(Stat.DotMultiplierPlus);
        b.StatOverrides[Stat.FireDotMultiplierPlus] = -10;
        Burn(ringDamage, factor: (100 + genericMultiplier - 10) / 100d);
        b.StatOverrides.Remove(Stat.FireDotMultiplierPlus);

        // Current equipment, including a real weapon-set swap, refreshes the live RF tick.
        b.ExemptItem = i => i.Id != bladeItem.Id;
        Assert.Equal(0, Game.CharacterPlayerStats(c).GetValueOrDefault(Stat.DotMultiplierPlus));
        Burn(ringDamage, factor: (100 + fireMultiplier) / 100d);
        b.ExemptItem = _ => true;
        Burn(ringDamage, factor: both);
        items.SwapWeaponSets();
        Burn(ringDamage, factor: (100 + fireMultiplier) / 100d);
        items.SwapWeaponSets();
        Burn(ringDamage, factor: both);
        b.StatOverrides[Stat.BaseDealNoFireDamage] = 1;
        Burn(ringDamage, factor: 0);
        b.StatOverrides.Remove(Stat.BaseDealNoFireDamage);
        b.StatOverrides[Stat.DeliriumAuraDamageOverTimePlusPctFinal] = 30;
        Burn(ringDamage, factor: both * 1.3);
        b.StatOverrides.Remove(Stat.DeliriumAuraDamageOverTimePlusPctFinal);
        b.Enemy.Mods = MonsterModSet.None with
            { Effects = new(new Dictionary<uint, int> { [Stat.BaseFireDamageResistancePct] = 50 }) };
        Burn(ringDamage, factor: both * .5); // Blackflame's ignite-only chaos rule does not change RF's damage type.
        Assert.False(b.Enemy.Ignites.Burning);
        b.Enemy.Mods = MonsterModSet.None;
        items.Remove(7, Assert.Single(items.Items(7)).Id);
        Burn(ringDamage, factor: (100 + genericMultiplier) / 100d);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        Burn(ringDamage);

        // The same generic bucket includes the real different-weapon mastery, once and additively with the ring.
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        const ushort node = 8872;
        var mastery = Program.Passives.EffectsFor(node).Single(e => e.Hash == 46493);
        Assert.Contains((Stat.DamagePlusPctWhileWieldingTwoDifferentWeaponTypes, 60), mastery.Stats);
        c.Character.Passives.Add(node);
        c.Character.MasteryEffects = new Dictionary<ushort, ushort>(c.Character.MasteryEffects) { [node] = mastery.Hash };
        Burn(ringDamage + 60);
        c.Character.Passives.Remove(node);
        c.Character.MasteryEffects = new Dictionary<ushort, ushort>();
        Burn(ringDamage);

        // Each eligible increase joins one additive bucket; spell, hit and ailment-only sources stay out.
        foreach (uint stat in new[] { Stat.FireDamagePlusPct, Stat.ElementalDamagePlusPct, Stat.DamageOverTimePlusPct,
            Stat.FireDamageOverTimePlusPct, Stat.BurnDamagePlusPct, Stat.AreaDamagePlusPct, Stat.RighteousFireDamagePlusPct })
        {
            b.StatOverrides[stat] = 25;
            Burn(ringDamage + 25);
            b.StatOverrides.Remove(stat);
        }
        b.StatOverrides[Stat.FireDamagePlusPct] = 30;
        b.StatOverrides[Stat.AreaDamagePlusPct] = 40;
        b.StatOverrides[Stat.DamageOverTimePlusPct] = -20;
        Burn(ringDamage + 50);
        foreach (uint stat in new[] { Stat.SpellDamagePlusPct, Stat.AttackDamagePlusPct, Stat.ProjectileDamagePlusPct,
            Stat.MeleeDamagePlusPct, Stat.IgniteDamagePlusPct, Stat.BaseAilmentDamagePlusPct, Stat.WeaponElementalDamagePlusPct })
            b.StatOverrides[stat] = 500;
        Burn(ringDamage + 50);
        b.StatOverrides[Stat.DamageOverTimePlusPct] = -500;
        Burn(ringDamage - 430);
        b.StatOverrides.Remove(Stat.FireDamagePlusPct);
        b.StatOverrides.Remove(Stat.AreaDamagePlusPct);
        b.StatOverrides.Remove(Stat.DamageOverTimePlusPct);

        foreach (uint stat in new[] { Stat.BaseDealNoDamage, Stat.DealNoElementalDamage,
            Stat.DealNoNonChaosDamage, Stat.KeystoneAncestralBond, Stat.DealNoDamageYourself })
        {
            b.StatOverrides[stat] = 1;
            Burn(ringDamage, factor: 0);
            b.StatOverrides.Remove(stat);
            Burn(ringDamage);
        }
        b.StatOverrides[Stat.SpellSkillsDealNoDamage] = 1;
        Burn(ringDamage, factor: 0);
        b.StatOverrides.Remove(Stat.SpellSkillsDealNoDamage);
        b.StatOverrides[Stat.DeliriumAuraDamageOverTimePlusPctFinal] = 30;
        Burn(ringDamage, factor: 1.3);
        b.StatOverrides.Remove(Stat.DeliriumAuraDamageOverTimePlusPctFinal);
        b.Enemy.Mods = MonsterModSet.None with
            { Effects = new(new Dictionary<uint, int> { [Stat.BaseFireDamageResistancePct] = 50 }) };
        Assert.Equal(.5, Game.MonsterDotTaken(b.Enemy, DamageType.Fire));
        b.StatOverrides[Stat.BaseReduceEnemyFireResistancePct] = 100;
        Burn(ringDamage, factor: .5); // Resistance applies; penetration does not apply to a DoT.
        // Fractional ticks share the existing carry rather than rounding away each small interval.
        b.Enemy.Life = b.Enemy.MaxLife;
        b.Enemy.Burn = 0;
        double dps = (c.PlayerMaxLife + c.Pools.EnergyShield) * .7 * (100 + ringDamage) / 100 * .5;
        for (int i = 0; i < 17; i++)
            BurnRighteousFire(b, rf, .00037);
        double total = dps * .00037 * 17;
        Assert.Equal((int)Math.Floor(total), (int)(b.Enemy.MaxLife - b.Enemy.Life));
        Assert.Equal(total - Math.Floor(total), b.Enemy.Burn, 6);
        b.Enemy.Walk.Teleport(150, 100, 0);
        Burn(ringDamage, factor: 0);
        Assert.DoesNotContain(200u, rf.Auras.Keys);
        Assert.DoesNotContain(201u, rf.Auras.Keys);
    }

    static void Different_weapon_mastery_reaches_shared_damage()
    {
        var uniques = new UniqueCatalog(Program.Items);
        Item Make(string name) => name is "Varunastra" or "Le Heup of All"
            ? uniques.Create(name, new Random(17))! : ItemCatalog.Create(Program.Items.Find(name)!);
        using var b = new Battle("Spectral Throw", "Fireball", "Bear Trap", "Essence Drain");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.SpellDamagePlusPct] = 0; // keep the wand's unrelated implicit out of the chaos control
        b.StatOverrides[Stat.AttackMinimumAddedChaosDamage] = 10;
        b.StatOverrides[Stat.AttackMaximumAddedChaosDamage] = 10;
        b.StatOverrides[Stat.SpellMinimumAddedChaosDamage] = 10;
        b.StatOverrides[Stat.SpellMaximumAddedChaosDamage] = 10;
        var items = Game.ItemsOf(c);
        var main = items.Items(PlayerInventories.Weapon1).Single();
        items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe") with
            { Sockets = main.Item.Sockets, Links = [1, 1, 1, 1] });
        items.Add(6, 0, 0, Make("Le Heup of All"));
        void Hands(string? mainName, string? offName)
        {
            foreach (var (row, name) in new (byte, string?)[]
                { (PlayerInventories.Weapon1, mainName), (PlayerInventories.Offhand1, offName) })
            {
                if (items.Items(row) is [var held]) items.Remove(row, held.Id);
                if (name is not null) items.Add(row, 0, 0, Make(name));
            }
            c.Pools = Game.PoolsFor(c, 100);
        }
        const ushort node = 8872;
        var effect = Program.Passives.EffectsFor(node).Single(e => e.Id == "DualWield3");
        Assert.Equal((ushort)46493, effect.Hash);
        Assert.Contains((Stat.DamagePlusPctWhileWieldingTwoDifferentWeaponTypes, 60), effect.Stats);
        void Select(bool enabled)
        {
            var masteries = new Dictionary<ushort, ushort>(c.Character.MasteryEffects);
            if (enabled) { c.Character.Passives.Add(node); masteries[node] = effect.Hash; }
            else { c.Character.Passives.Remove(node); masteries.Remove(node); }
            c.Character.MasteryEffects = masteries;
            c.Pools = Game.PoolsFor(c, 100);
        }
        Dictionary<uint, int> Stats() => Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Hands("Nailed Fist", "Rusted Sword");
        int ringDamage = Stats().GetValueOrDefault(Stat.DamagePlusPct);
        Assert.InRange(ringDamage, 10, 30);
        var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
        var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        var secondary = Game.GemSkillEffects(c).Single(g => g.Name == "Bear Trap");
        var drain = Game.GemSkillEffects(c).Single(g => g.Name == "Essence Drain");
        var drainBase = Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(c, drain.Id, drain.Effect));
        Assert.True(drainBase.DamagePerSecond > 0);
        var secondaryBase = Game.SupportedHitFor(c, secondary.Id, suppress: false);
        Assert.True(secondaryBase[DamageType.Physical].Min > 0);
        var poisonBase = Game.SupportedAilmentRows(c, attack.Id, "main_hand").ToDictionary(r => r.Stat, r => r.Value);
        var igniteBase = Game.SupportedAilmentRows(c, spell.Id, "spell").ToDictionary(r => r.Stat, r => r.Value);
        Assert.Contains(poisonBase, r => (Program.Stats.Name(r.Key) ?? "").Contains("poison") && r.Value > 0);
        Assert.Contains(igniteBase, r => (Program.Stats.Name(r.Key) ?? "").Contains("ignite") && r.Value > 0);
        Select(true);
        Assert.Equal(60, Stats()[Stat.DamagePlusPctWhileWieldingTwoDifferentWeaponTypes]);
        double factor = (100 + ringDamage + 60) / 100.0;
        Assert.Equal(10 * factor, Game.SupportedHitFor(c, attack.Id, suppress: false)[DamageType.Chaos].Min, 6);
        Assert.Equal(30 * factor, Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        double ratio = (100 + ringDamage + 60.0) / (100 + ringDamage);
        Assert.Equal(drainBase.DamagePerSecond * ratio,
            Game.EssenceDrainCast(c, drain.Id, drain.Effect)!.DamagePerSecond, 6);
        Assert.Equal(secondaryBase[DamageType.Physical].Min * ratio,
            Game.SupportedHitFor(c, secondary.Id, suppress: false)[DamageType.Physical].Min, 6);
        foreach (var (skill, hand, baseline) in new[] { (attack.Id, "main_hand", poisonBase), (spell.Id, "spell", igniteBase) })
            foreach (var (stat, value) in Game.SupportedAilmentRows(c, skill, hand))
                // Ailment panel values are whole damage per minute, rounded only when published.
                Assert.InRange(value, (int)Math.Floor(baseline[stat] * ratio) - 1, (int)Math.Ceiling(baseline[stat] * ratio) + 1);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal((int)(10 * factor), panel[Stat.MainHandMinimumChaosDamage]);

        foreach (var (mainName, offName, qualifies) in new (string?, string?, bool)[]
        {
            ("Nailed Fist", "Rusted Sword", true), ("Rusted Sword", "Nailed Fist", true),
            ("Nailed Fist", "Nailed Fist", false), ("Rusted Sword", "Rusted Spike", false),
            ("Rusted Spike", "Rusted Sword", false), ("Glass Shank", "Carving Knife", true),
            ("Carving Knife", "Glass Shank", true), ("Carving Knife", "Carving Knife", false),
            ("Driftwood Club", "Driftwood Sceptre", true), ("Driftwood Sceptre", "Driftwood Club", true),
            ("Driftwood Sceptre", "Driftwood Sceptre", false), ("Driftwood Wand", "Driftwood Wand", false),
            ("Rusted Sword", "Splintered Tower Shield", false), ("Corroded Blade", null, false),
            ("Rusted Sword", null, false), (null, null, false),
            ("Varunastra", null, true), ("Varunastra", "Splintered Tower Shield", true),
            ("Varunastra", "Rusted Sword", true), ("Rusted Sword", "Varunastra", true), ("Varunastra", "Varunastra", true),
        })
        {
            Hands(mainName, offName);
            int expected = ringDamage + (qualifies ? 60 : 0);
            Assert.Equal(expected, Stats().GetValueOrDefault(Stat.DamagePlusPct));
            Assert.Equal(expected, Stats().GetValueOrDefault(Stat.DamagePlusPct)); // warm snapshot, no repeated addition
            Assert.Equal(30 * (100 + expected) / 100.0,
                Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        }
        Hands(null, null);
        b.StatOverrides[Stat.UnarmedCountsAsDualWielding] = 1;
        Assert.Equal(1, Stats()[Stat.IsDualWielding]);
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Stat.DamagePlusPct));
        b.StatOverrides.Remove(Stat.UnarmedCountsAsDualWielding);

        Hands("Rusted Sword", "Imperial Claw");
        Assert.Equal(ringDamage + 60, Stats()[Stat.DamagePlusPct]);
        uint clawId = items.Items(PlayerInventories.Offhand1).Single().Id;
        b.ExemptItem = i => i.Id != clawId;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Null(Game.ActiveOffHand(c));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Stat.DamagePlusPct));
        b.ExemptItem = _ => true;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(ringDamage + 60, Stats()[Stat.DamagePlusPct]);
        Hands("Rusted Sword", "Splintered Tower Shield");
        items.Add(14, 0, 0, Make("Varunastra"));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Stat.DamagePlusPct));
        items.SwapWeaponSets();
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(ringDamage + 60, Stats()[Stat.DamagePlusPct]);
        items.SwapWeaponSets();
        Hands("Nailed Fist", "Rusted Sword");
        Select(false);
        Assert.Equal(0, Stats().GetValueOrDefault(Stat.DamagePlusPctWhileWieldingTwoDifferentWeaponTypes));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Stat.DamagePlusPct));
        Select(true);
        b.StatOverrides[Stat.DamagePlusPct] = 7;
        Assert.Equal(67, Stats()[Stat.DamagePlusPct]);
        b.StatOverrides.Remove(Stat.DamagePlusPct);
        Assert.Equal(ringDamage + 60, Stats()[Stat.DamagePlusPct]);

        items.Change(14, items.Items(14).Single().Id, _ => Make("Rusted Sword"));
        var released = Game.CaptureGemProjectile(c, b.Session, spell.Id, spell, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(released.Payload);
        Assert.Equal(30 * factor, shot.Hit[DamageType.Chaos].Min, 6);
        var drainUse = Game.CaptureGemProjectile(c, b.Session, drain.Id, drain, (120, 100), new(0));
        var drainShot = Assert.IsType<ProjectileOffense>(drainUse.Payload);
        var releasedDrain = Assert.IsType<MonsterEssenceDrain.Cast>(drainShot.EssenceDrain);
        Assert.Equal(drainBase.DamagePerSecond * ratio, releasedDrain.DamagePerSecond, 6);
        b.Session.Runtime.Release(drainUse);
        items.SwapWeaponSets();
        Select(false);
        Assert.Equal(30 * (100 + ringDamage) / 100.0,
            Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        Assert.Equal(30 * factor, shot.Hit[DamageType.Chaos].Min, 6);
        Assert.Equal(drainBase.DamagePerSecond, Game.EssenceDrainCast(c, drain.Id, drain.Effect)!.DamagePerSecond, 6);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        Assert.Same(releasedDrain, b.Enemy.EssenceDrain.Active);
        Assert.Equal(drainBase.DamagePerSecond * ratio, b.Enemy.EssenceDrain.Active!.DamagePerSecond, 6);
        uint minimum = (uint)drainShot.Hit.Components.Sum(part => Math.Floor(part.Range.Min));
        uint maximum = (uint)drainShot.Hit.Components.Sum(part => Math.Floor(part.Range.Max))
            + (uint)Math.Ceiling(releasedDrain.DamagePerSecond * .4);
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, minimum, maximum);
        Assert.True(b.Ally.EssenceDrain.Idle);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }

    static void Fan_of_Blades_reaches_projectile_count_and_attack_damage()
    {
        static Item Make(string name) => name == "Varunastra"
            ? new UniqueCatalog(Program.Items).Create(name, new Random(17))!
            : ItemCatalog.Create(Program.Items.Find(name)!);
        using var b = new Battle("Spectral Throw", "Fireball", "Double Strike", "Lightning Strike", "Kinetic Blast", "Ice Shot");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
        b.StatOverrides[Stat.AttackMinimumAddedLightningDamage] = 10;
        b.StatOverrides[Stat.AttackMaximumAddedLightningDamage] = 10;
        b.StatOverrides[Stat.AttackMinimumAddedChaosDamage] = 10;
        b.StatOverrides[Stat.AttackMaximumAddedChaosDamage] = 10;
        b.StatOverrides[Stat.DamagePlusPct] = 100;
        b.StatOverrides[Stat.SpellDamagePlusPct] = 0;
        var items = Game.ItemsOf(c);
        var hand = items.Items(PlayerInventories.Weapon1).Single();
        var activeType = Program.Items.ByHash(hand.Item.Sockets[0].Gem!.TypeHash)!;
        items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe") with
        {
            Sockets = [hand.Item.Sockets[0] with { Gem = hand.Item.Sockets[0].Gem! with
                { GemExperience = Program.Items.Gem(activeType)!.ExperienceFor(2) } }, .. hand.Item.Sockets.Skip(1)],
            Links = [1, 1, 1, 1, 1, 1],
        });
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Nailed Fist"));
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
        var fireball = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        var melee = Game.GemSkillEffects(c).Single(g => g.Name == "Double Strike");
        var hybrid = Game.GemSkillEffects(c).Single(g => g.Name == "Lightning Strike");
        var survivorsCatalog = SurvivorsSkills.SurvivorsCatalogFor(c);
        SurvivorsSkills.SurvivorsComputedCast SurvivorsCast(string name)
        {
            int index = survivorsCatalog.ToList().FindIndex(skill => skill.Name == name);
            Assert.True(index >= 0);
            var run = new SurvivorsRun();
            run.Learn(index);
            return Assert.IsType<SurvivorsSkills.SurvivorsComputedCast>(SurvivorsSkills.SurvivorsComputeCast(
                c, run, survivorsCatalog[index], 1, index, new([], [], 1, 20, 40)));
        }
        var type = Program.Items.Find("Large Cluster Jewel")!;
        var theme = ClusterJewels.FindTheme(type.Cluster!, "affliction_dagger_and_claw_damage")!;
        var fan = Program.Items.Mods.ById("AfflictionNotableFanofBlades_")!;
        var plain = ClusterJewels.Create(type, theme, 8, 2)!;
        Assert.Contains(new ItemRolls(Program.Items).Pool(type, plain), p => p.Mod.Id == fan.Id);
        var cluster = ClusterJewels.Create(type, theme, 8, 2, [fan])!;
        var jewel = items.Add(PlayerInventories.PassiveJewels, 20, 0, cluster)!;
        c.Character.Passives.Add(Program.Passives.JewelNodeForSlot(20)!.Value);
        var layout = ClusterJewelLayout.For(20, cluster)!;
        ushort notable = (ushort)(layout.Base + Assert.Single(layout.NotableOrders).Key);
        c.Pools = Game.PoolsFor(c, 100);
        var spellBase = Game.SupportedProjectileHit(c, fireball.Id, area: false);
        var meleeBase = Game.SupportedHitFor(c, melee.Id, suppress: false);
        var hybridMeleeBase = Game.SupportedHitFor(c, hybrid.Id, suppress: false);
        var hybridProjectileBase = Game.SupportedHitFor(c, hybrid.Id, suppress: false, projectile: true);
        var ailmentBase = Game.SupportedAilmentRows(c, gem.Id, "main_hand");
        Assert.Contains(ailmentBase, row => (Program.Stats.Name(row.Stat) ?? "").Contains("poison") && row.Value > 0);
        var survivorsBase = SurvivorsCast("Spectral Throw");
        var survivorsSpellBase = SurvivorsCast("Fireball");
        c.Character.ClusterPassives.Add(notable);
        var stats = Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(1, stats[Stat.AttackSkillAdditionalNumProjectilesWhileWieldingClawsDaggers]);
        Assert.Equal(20, stats[Stat.ProjectileAttackDamagePlusPctWithClawOrDagger]);
        Assert.Equal(2, gem.Level);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        // Level-two Spectral Throw: 10 * 102.6% * (100% + 100% + 20%), with one additional projectile.
        Assert.Equal((2, new DamageRange(22.572, 22.572)), (use.Profile.Count, shot.Hit[DamageType.Lightning]));
        Assert.Equal(spellBase.Components, Game.SupportedProjectileHit(c, fireball.Id, area: false).Components);
        Assert.Equal(meleeBase.Components, Game.SupportedHitFor(c, melee.Id, suppress: false).Components);
        Assert.Equal(hybridMeleeBase.Components, Game.SupportedHitFor(c, hybrid.Id, suppress: false).Components);
        Assert.Equal(hybridProjectileBase[DamageType.Lightning].Min * 1.1,
            Game.SupportedHitFor(c, hybrid.Id, suppress: false, projectile: true)[DamageType.Lightning].Min, 6);
        Assert.Equal(ailmentBase, Game.SupportedAilmentRows(c, gem.Id, "main_hand"));
        var survivors = SurvivorsCast("Spectral Throw");
        Assert.Equal(survivorsBase.Count + 1, survivors.Count);
        Assert.Equal(survivorsBase.Shot!.Hit[DamageType.Lightning].Min * 1.1,
            survivors.Shot!.Hit[DamageType.Lightning].Min, 6);
        Assert.Equal(survivorsBase.Shot.AreaHit![DamageType.Lightning].Min * 1.1,
            survivors.Shot.AreaHit![DamageType.Lightning].Min, 6);
        var survivorsSpell = SurvivorsCast("Fireball");
        Assert.Equal(survivorsSpellBase.Count, survivorsSpell.Count);
        Assert.Equal(survivorsSpellBase.Shot!.Hit.Components, survivorsSpell.Shot!.Hit.Components);

        foreach (var (mainName, offName, count, mainDamage, offDamage) in new (string, string?, int, double, double)[]
        {
            ("Nailed Fist", null, 2, 22.572, 0), ("Glass Shank", null, 2, 22.572, 0),
            ("Carving Knife", null, 2, 22.572, 0), ("Varunastra", null, 2, 22.572, 0),
            ("Rusted Sword", null, 1, 20.52, 0),
            ("Rusted Sword", "Nailed Fist", 2, 20.52, 22.572), ("Nailed Fist", "Rusted Sword", 2, 22.572, 20.52),
            ("Rusted Sword", "Varunastra", 2, 20.52, 22.572), ("Varunastra", "Rusted Sword", 2, 22.572, 20.52),
            ("Glass Shank", "Carving Knife", 2, 22.572, 22.572), ("Varunastra", "Varunastra", 2, 22.572, 22.572),
        })
        {
            items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make(mainName));
            if (items.Items(PlayerInventories.Offhand1) is [var off]) items.Remove(PlayerInventories.Offhand1, off.Id);
            if (offName is not null) items.Add(PlayerInventories.Offhand1, 0, 0, Make(offName));
            var captured = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
            Assert.Equal(count, captured.Profile.Count);
            Assert.Equal(mainDamage, Assert.IsType<ProjectileOffense>(captured.Payload).Hit[DamageType.Lightning].Min, 6);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(count, panel[Stat.TotalNumberOfProjectilesToFire]);
            Assert.Equal((int)mainDamage, panel[Stat.MainHandMinimumLightningDamage]);
            if (offName is not null)
                Assert.Equal(offDamage, Game.SupportedOffHandHit(c, gem.Id, Game.OffhandWeaponFor(c)!)[DamageType.Lightning].Min, 6);
            Assert.Equal(1, Game.CaptureGemProjectile(c, b.Session, fireball.Id, fireball, (120, 100), new(0)).Profile.Count);
        }
        items.Remove(PlayerInventories.Offhand1, items.Items(PlayerInventories.Offhand1).Single().Id);
        foreach (var (weapon, skill) in new[] { ("Driftwood Wand", "Kinetic Blast"), ("Crude Bow", "Ice Shot") })
        {
            items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make(weapon));
            var active = Game.GemSkillEffects(c).Single(g => g.Name == skill);
            var captured = Game.CaptureGemProjectile(c, b.Session, active.Id, active, (120, 100), new(0));
            Assert.Equal(1, captured.Profile.Count);
            var inputs = Game.SupportedHitInputsFor(c, active.Id)!;
            Assert.Equal(inputs.Raw[DamageType.Lightning].Min * 2,
                Assert.IsType<ProjectileOffense>(captured.Payload).Hit[DamageType.Lightning].Min, 6);
        }

        // The notable and body-socketed gems stay active when only the claw's Dexterity requirement fails.
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Imperial Claw"));
        b.ExemptItem = i => i.Id != hand.Id;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Null(Game.ActiveMainHand(c));
        stats = Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(1, stats[Stat.AttackSkillAdditionalNumProjectilesWhileWieldingClawsDaggers]);
        var inactivePanel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(1, inactivePanel[Stat.TotalNumberOfProjectilesToFire]);
        b.ExemptItem = _ => true;
        c.Pools = Game.PoolsFor(c, 100);
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Nailed Fist"));
        c.Character.ClusterPassives.Remove(notable);
        var refunded = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        Assert.Equal((1, new DamageRange(20.52, 20.52)),
            (refunded.Profile.Count, Assert.IsType<ProjectileOffense>(refunded.Payload).Hit[DamageType.Lightning]));
        c.Character.ClusterPassives.Add(notable);
        b.StatOverrides[Stat.NumberOfAdditionalProjectiles] = 1;
        var released = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var releasedShot = Assert.IsType<ProjectileOffense>(released.Payload);
        Assert.Equal((3, new DamageRange(22.572, 22.572)), (released.Profile.Count, releasedShot.Hit[DamageType.Lightning]));
        b.Session.Runtime.Release(released);
        items.Add(14, 0, 0, Make("Rusted Sword"));
        items.SwapWeaponSets();
        items.Remove(PlayerInventories.PassiveJewels, jewel.Id);
        b.StatOverrides.Remove(Stat.NumberOfAdditionalProjectiles);
        var removed = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        Assert.Equal((1, new DamageRange(20.52, 20.52)),
            (removed.Profile.Count, Assert.IsType<ProjectileOffense>(removed.Payload).Hit[DamageType.Lightning]));
        Assert.Equal((3, new DamageRange(22.572, 22.572)), (released.Profile.Count, releasedShot.Hit[DamageType.Lightning]));
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        // Damage rolls floor each type independently; this only checks at least one admitted projectile hit.
        uint minimum = (uint)releasedShot.Hit.Components.Sum(part => Math.Floor(part.Range.Min));
        uint maximum = (uint)releasedShot.Hit.Components.Sum(part => Math.Floor(part.Range.Max));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, minimum, maximum * 3);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }

    static void Claw_and_dagger_projectiles_keep_the_released_hand_and_source()
    {
        static Item Weapon(string name)
        {
            if (name != "Varunastra") return ItemCatalog.Create(Program.Items.Find(name)!);
            var uniques = new UniqueCatalog(Program.Items);
            return Assert.IsType<Item>(uniques.Create(uniques.Find(name)!, new Random(17)));
        }
        foreach (var (name, itemClass, other, mod, maximum, ineligible) in new[]
        {
            ("Nailed Fist", "Claw", "Glass Shank", "AbyssAddedLightningDamageWithClawsJewel1__", 19, "Imperial Claw"),
            ("Carving Knife", "Rune Dagger", "Nailed Fist", "AbyssAddedLightningDamageWithDaggersJewel1", 20, "Demon Dagger"),
            ("Varunastra", "One Hand Sword", "Rusted Sword", "AbyssAddedLightningDamageWithClawsJewel1__", 19, "Varunastra"),
        })
        {
            using var b = new Battle("Spectral Throw", "Fireball", "Spellblade Support", "Shield Charge");
            var c = b.Player;
            b.StatOverrides[Stat.GlobalCannotCrit] = 1;
            b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
            b.StatOverrides[Stat.LightningDamagePlusPct] = 100;
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            var activeType = Program.Items.ByHash(host.Item.Sockets[0].Gem!.TypeHash)!;
            var body = items.Add(PlayerInventories.BodyArmour, 0, 0, Weapon("Simple Robe") with
            {
                ItemLevel = 100,
                Sockets = [host.Item.Sockets[0] with { Gem = host.Item.Sockets[0].Gem! with
                    { GemExperience = Program.Items.Gem(activeType)!.ExperienceFor(2) } }, .. host.Item.Sockets.Skip(1)],
                Links = [1, 2, 1],
            })!;
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(name));
            items.Add(PlayerInventories.Offhand1, 0, 0, Weapon(other));
            items.Add(PlayerInventories.Weapon2, 0, 0, Weapon("Rusted Sword"));
            var jewel = Weapon("Murderous Eye Jewel") with
            {
                Rarity = ItemRarity.Magic,
                Mods = [new(ModSection.Explicit, Program.Items.Mods.ById(mod)!.Hash, [1, maximum])],
            };
            var belt = items.Add(PlayerInventories.Belt, 0, 0, Weapon("Stygian Vise") with
            { Sockets = [new(SocketColour.Abyss, jewel)] })!;
            var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
            var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
            Assert.Null(Game.WeaponRestricted(c, attack.Effect));
            Assert.Equal(itemClass, Game.WeaponFor(c).ItemClass);
            Assert.Equal((10_260, 10_260), Game.SkillsFor(c).DamageCoefficients(attack.Effect, attack.Level));
            // Native level-two Spectral Throw has base/added offsets 260: 102.6% before increased lightning.
            var raw = new DamageRange(1.026, maximum * 1026 / 1000d);
            var hit = new DamageRange(2.052, maximum * 2052 / 1000d);
            Assert.Equal(raw, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning]);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Lightning].Max);
            Assert.Equal(hit, Game.SupportedProjectileHit(c, attack.Id, area: false)[DamageType.Lightning]);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)hit.Min, panel[Stat.MainHandMinimumLightningDamage]);
            Assert.Equal((int)hit.Max, panel[Stat.MainHandMaximumLightningDamage]);
            Assert.Equal(0, panel.GetValueOrDefault(Stat.OffHandMaximumLightningDamage));
            var off = items.Items(PlayerInventories.Offhand1).Single();
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon(name));
            Assert.Equal(hit, Game.SupportedOffHandHit(c, attack.Id, Game.OffhandWeaponFor(c)!)[DamageType.Lightning]);
            var both = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)hit.Max, both[Stat.OffHandMaximumLightningDamage]);
            // The real shield override must not inherit the eligible weapon still held in the main hand.
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon("Splintered Tower Shield"));
            var charge = Game.GemSkillEffects(c).Single(g => g.Name == "Shield Charge");
            Assert.Null(Game.WeaponRestricted(c, charge.Effect));
            var shield = Assert.IsType<SkillCatalog.Unarmed>(Game.ShieldWeapon(c, charge.Id));
            Assert.Null(shield.ItemClass);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, charge.Id, weapon: shield)!.Raw[DamageType.Lightning].Max);
            var shieldPanel = Game.PlayerSkills(c).Single(s => s.Id == charge.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(0, shieldPanel.GetValueOrDefault(Stat.MainHandMaximumLightningDamage));
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon(other));
            uint spellblade = Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage;
            Assert.Equal(40, Game.SupportedSkill(c, spell.Id).Stats[spellblade]);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 1, 1, 1] });
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 2, 1] });

            // The jewel and body-socketed skill remain active when a high-level weapon becomes unusable.
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(ineligible));
            b.ExemptItem = i => i.Id != host.Id;
            c.Character.Experience = CharacterStats.ExperienceFor(50);
            c.Pools = Game.PoolsFor(c, 50);
            Assert.Null(Game.ActiveMainHand(c));
            Assert.Equal(2, Game.GemSkillEffects(c).Single(g => g.Id == attack.Id).Level);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning].Max);
            b.ExemptItem = _ => true;
            c.Character.Experience = CharacterStats.ExperienceFor(100);
            c.Pools = Game.PoolsFor(c, 100);
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(name));
            Assert.Equal(raw, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning]);

            var use = Game.CaptureGemProjectile(c, b.Session, attack.Id, attack, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(hit, shot.Hit[DamageType.Lightning]);
            Assert.Equal(1, use.Profile.Count);
            b.Session.Runtime.Release(use);
            items.SwapWeaponSets();
            Assert.Equal("One Hand Sword", Game.WeaponFor(c).ItemClass);
            Assert.Equal(attack.Id, Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw").Id);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning].Max);
            items.Remove(PlayerInventories.Belt, belt.Id);
            Assert.Equal(hit, shot.Hit[DamageType.Lightning]);
            // Advance only through the outgoing leg, before the existing Spectral Throw return begins.
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)hit.Min, (uint)hit.Max);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        }
    }

    static void Weapon_cold_per_dexterity_stays_with_its_source()
    {
        const uint minimum = Stat.AttacksWithThisWeaponMinimumAddedColdDamagePer10Dexterity;
        const uint maximum = Stat.AttacksWithThisWeaponMaximumAddedColdDamagePer10Dexterity;
        var affix = Program.Items.Mods.ById("AddedColdDamagePerDexterityUber1")!;
        Assert.Equal(new[] { minimum, maximum }, affix.Stats.Select(s => s.Id));
        Assert.All(affix.Stats, s => Assert.False(s.Local));
        var source = new ItemMod(ModSection.Explicit, affix.Hash, [2, 4]);
        using (var b = new Battle("Rain of Arrows"))
        {
            var c = b.Player;
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Mods = [source],
                Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with
                { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.Sockets[0].Gem!.TypeHash)!)!.ExperienceFor(2) } }]
            });
            var gem = Game.GemSkillEffects(c).Single();
            foreach (var (dex, groups) in new[] { (9, 0), (10, 1), (19, 1), (20, 2), (1000, 100) })
            {
                c.Pools = c.Pools with { Dexterity = dex };
                Assert.Equal(dex, Game.CharacterPlayerStats(c)[Stat.Dexterity]);
                var raw = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold];
                Assert.Equal(groups * 2 * .49, raw.Min, 6);
                Assert.Equal(groups * 4 * .49, raw.Max, 6);
            }
            var weapon = Game.WeaponFor(c);
            Assert.Equal(0, weapon.LocalDamage![DamageType.Cold].Max);
            var character = Game.CharacterPlayerStats(c);
            int Get(uint name) => character.GetValueOrDefault(name);
            Assert.Equal(new DamageRange(200, 400), SupportedAttackDamage.Raw(weapon, Get, new(0))[DamageType.Cold]);
            Assert.Equal(200, character[Stat.MainHandMinimumColdDamage]);
            Assert.Equal(400, character[Stat.MainHandMaximumColdDamage]);
            // A globally summed raw pair or an inactive set cannot supply another copy of this weapon's grant.
            b.StatOverrides[minimum] = 999;
            b.StatOverrides[maximum] = 999;
            var inactive = items.Add(PlayerInventories.Weapon2, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with { Mods = [source] });
            Assert.NotNull(inactive);
            Assert.Equal(98, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold].Min, 6);
            b.StatOverrides[Stat.GlobalCannotCrit] = 1;
            b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
            b.StatOverrides[Stat.AreaDamagePlusPct] = 100;
            b.StatOverrides[Stat.NumberOfAdditionalProjectiles] = -1000;
            var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(new DamageRange(98, 196), shot.Hit[DamageType.Cold]);
            Assert.Equal(new DamageRange(196, 392), shot.AreaHit![DamageType.Cold]);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(196, panel[Stat.MainHandMinimumColdDamage]);
            Assert.Equal(392, panel[Stat.MainHandMaximumColdDamage]);
            Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 0);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
            c.Pools = c.Pools with { Dexterity = 0 };
            Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold].Max);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.InRange(100000u - b.Enemy.Life, 196u, 392u);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [source] });
            b.ExemptItem = i => i.Id != host.Id;
            c.Character.Experience = CharacterStats.ExperienceFor(1);
            Game.CharacterStatSources(c, level: 1);
            Assert.Null(Game.ActiveMainHand(c));
            Assert.Equal(0, Game.WeaponFor(c).LocalSources?.GetValueOrDefault(minimum) ?? 0);
            b.ExemptItem = _ => true;
            c.Character.Experience = CharacterStats.ExperienceFor(100);
            Game.CharacterStatSources(c, level: 100);
            Assert.Equal(2, Game.WeaponFor(c).LocalSources!.GetValueOrDefault(minimum));
        }
        using (var b = new Battle("Double Strike", "Fireball", "Spellblade Support"))
        {
            var c = b.Player;
            c.Pools = c.Pools with { Dexterity = 1000 };
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
            { Sockets = item.Sockets, Links = item.Links, Mods = [source] });
            var off = items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!))!;
            var attack = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).IsAttack(g.Effect));
            var spell = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).IsSpell(g.Effect));
            uint spellblade = Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage;
            Assert.True(Game.SupportedSkill(c, spell.Id).Stats.GetValueOrDefault(spellblade) > 0);
            Assert.True(Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Cold].Max > 0);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Cold].Max);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Cold].Max);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
            items.Change(PlayerInventories.Offhand1, off.Id, item => item with { Mods = [source] });
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Cold].Max);
            Assert.True(Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Cold].Max > 0);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Cold].Max);
        }
    }

    static void Spellblade_takes_weapon_damage_without_global_attack_inputs()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
        b.StatOverrides[Stat.BaseDealNoFireDamage] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var local = Program.Items.Mods.ById("LocalAddedLightningDamage1")!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
        {
            Rarity = ItemRarity.Magic, Sockets = item.Sockets, Links = item.Links,
            Mods = [new(ModSection.Explicit, local.Hash, [1, 5])],
        });
        var gem = Game.GemSkillEffects(c).Single();
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage]);
        Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);
        var localContribution = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.True(localContribution.Max > 0);
        int PanelMaximum() => Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!
            .Single(s => s.Key == Stat.MainHandMaximumLightningDamage).Value;
        int localPanel = PanelMaximum();

        // This real ring prefix adds lightning to attacks, not to the weapon or to Spellblade's spell.
        var attackOnly = Program.Items.Mods.ById("AddedLightningDamage1")!;
        Assert.All(attackOnly.Stats, stat => Assert.False(stat.Local));
        var ring = items.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Coral Ring")!) with
        { Rarity = ItemRarity.Magic, Mods = [new(ModSection.Explicit, attackOnly.Hash, [1, 5])] })!;
        var character = Game.CharacterPlayerStats(c);
        Assert.Equal(1, character[Stat.AttackMinimumAddedLightningDamage]);
        Assert.Equal(5, character[Stat.AttackMaximumAddedLightningDamage]);
        Assert.Equal(new DamageRange(2, 10), Game.AttackHit(Game.WeaponFor(c), character, melee: true, accuracy: new(Game.PlayerAccuracy(c, character)))[DamageType.Lightning]);
        Assert.Equal(localContribution, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(localPanel, PanelMaximum());
        items.Remove(6, ring.Id);

        // Circle of Regret's generic global lightning already enters an unsupported spell once.
        var generic = new UniqueCatalog(Program.Items).Create("Circle of Regret", new Random(17))!;
        var circle = items.Add(6, 0, 0, generic)!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1] });
        var globalContribution = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.True(globalContribution.Max > 0);
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [2] });
        var expected = localContribution.Add(globalContribution);
        Assert.Equal(expected, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal((int)expected.Max, PanelMaximum());
        Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);

        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(expected, shot.Hit[DamageType.Lightning]);
        Assert.Equal(expected, shot.AreaHit![DamageType.Lightning]);
        Assert.Equal(expected.Min, shot.Hit.MinTotal);
        Assert.Equal(expected.Max, shot.Hit.MaxTotal);
        b.Session.Runtime.Release(use);
        items.Remove(6, circle.Id);
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1] });
        Assert.Equal(0, Game.SupportedHit(c, gem.Id)[DamageType.Lightning].Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)expected.Min, (uint)expected.Max);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expected, shot.Hit[DamageType.Lightning]);
    }

    static void Spellblade_requires_an_active_one_hand_melee_weapon()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
        b.StatOverrides[Stat.BaseDealNoFireDamage] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        // Keep the spell and support active independently of the equipped weapon and its requirements.
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = host.Item.Sockets, Links = host.Item.Links });
        items.Remove(PlayerInventories.Weapon1, host.Id);
        var gem = Game.GemSkillEffects(c).Single();
        DamageRange Lightning() => Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.Equal(0, Lightning().Max);

        var local = Program.Items.Mods.ById("LocalAddedLightningDamage1")!;
        Item Weapon(string name) => ItemCatalog.Create(Program.Items.Find(name)!) with
        { Rarity = ItemRarity.Magic, Mods = [new(ModSection.Explicit, local.Hash, [1, 5])] };
        var main = items.Add(PlayerInventories.Weapon1, 0, 0, Weapon("Rusted Sword"))!;
        var transferred = Lightning();
        Assert.True(transferred.Max > 0);
        foreach (var (name, eligible) in new[]
        {
            ("Nailed Fist", true), ("Glass Shank", true), ("Carving Knife", true), ("Rusted Sword", true),
            ("Rusted Spike", true), ("Rusted Hatchet", true), ("Driftwood Club", true), ("Driftwood Sceptre", true),
            ("Corroded Blade", false), ("Stone Axe", false), ("Driftwood Maul", false),
            ("Gnarled Branch", false), ("Iron Staff", false), ("Driftwood Wand", false), ("Crude Bow", false),
        })
        {
            items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon(name));
            Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
            Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);
            var expected = eligible ? transferred : DamageRange.Zero;
            Assert.True(expected == Lightning(), $"{name}: expected {expected}, actual {Lightning()}");
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)expected.Max, panel.GetValueOrDefault(Stat.MainHandMaximumLightningDamage));
        }

        items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon("Rusted Sword"));
        var shield = items.Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!))!;
        Assert.Equal(transferred, Lightning());
        items.Remove(PlayerInventories.Offhand1, shield.Id);
        items.Add(PlayerInventories.Weapon2, 0, 0, Weapon("Gnarled Branch"));
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(transferred, shot.Hit[DamageType.Lightning]);
        Assert.Equal(transferred, shot.AreaHit![DamageType.Lightning]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        Assert.Equal(0, Lightning().Max);
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)transferred.Min, (uint)transferred.Max);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(transferred, shot.Hit[DamageType.Lightning]);
        items.SwapWeaponSets();
        Assert.Equal(transferred, Lightning());

        items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon("Jewelled Foil"));
        b.ExemptItem = i => i.Id != main.Id;
        c.Character.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.Null(Game.ActiveMainHand(c));
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage]);
        Assert.Equal(0, Lightning().Max);
        b.ExemptItem = _ => true;
        c.Character.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(transferred, Lightning());
    }

    static void Spellblade_transfers_each_active_weapon_before_spell_scaling()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
        b.StatOverrides[Stat.BaseDealNoFireDamage] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = host.Item.Sockets, Links = host.Item.Links })!;
        items.Remove(PlayerInventories.Weapon1, host.Id);
        var gem = Game.GemSkillEffects(c).Single();
        Item Weapon(string name, string mod, int min, int max) => ItemCatalog.Create(Program.Items.Find(name)!) with
        {
            ItemLevel = 100, Rarity = ItemRarity.Magic,
            Mods = [new(ModSection.Explicit, Program.Items.Mods.ById(mod)!.Hash, [min, max])],
        };
        items.Add(PlayerInventories.Weapon1, 0, 0, Weapon("Rusted Sword", "LocalAddedColdDamage2", 7, 14));
        var off = items.Add(PlayerInventories.Offhand1, 0, 0,
            Weapon("Rusted Sword", "LocalAddedLightningDamage2", 2, 25))!;
        DamageRange Cold() => Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold];
        // 40% support * 60% per hand: floor each endpoint, then apply Fireball's 300% effectiveness.
        Assert.Equal(new DamageRange(0, 18), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(3, 9), Cold());
        items.Change(PlayerInventories.Offhand1, off.Id,
            _ => Weapon("Rusted Sword", "LocalAddedColdDamage2", 9, 16));
        // Separate floors: (1+2, 3+3) * 3. Combining hands before rounding would produce 9-21.
        Assert.Equal(new DamageRange(9, 18), Cold());
        void SupportLevel(int level) => items.Change(PlayerInventories.BodyArmour, body.Id, item =>
        {
            var sockets = item.Sockets.ToArray();
            var support = sockets[1].Gem!;
            sockets[1] = sockets[1] with { Gem = support with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(support.TypeHash)!)!.ExperienceFor(level) } };
            return item with { Sockets = sockets };
        });
        SupportLevel(20);
        Assert.Equal(135, Game.SupportedSkill(c, gem.Id).Stats[Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage]);
        Assert.Equal(new DamageRange(36, 69), Cold());
        SupportLevel(1);

        items.Remove(PlayerInventories.Offhand1, off.Id);
        Assert.Equal(new DamageRange(6, 15), Cold());
        var shield = items.Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!))!;
        Assert.Equal(new DamageRange(6, 15), Cold());
        items.Remove(PlayerInventories.Offhand1, shield.Id);
        off = items.Add(PlayerInventories.Offhand1, 0, 0,
            Weapon("Jewelled Foil", "LocalAddedColdDamage2", 9, 16))!;
        b.ExemptItem = i => i.Id != off.Id;
        c.Character.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.Null(Game.ActiveOffHand(c));
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Stat.SupportSpellWeaponDamageGainPctOfOneHandMeleeWeaponDamageAsAddedSpellDamage]);
        Assert.Equal(new DamageRange(6, 15), Cold());
        b.ExemptItem = _ => true;
        c.Character.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(new DamageRange(9, 18), Cold());

        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        items.Add(PlayerInventories.Offhand2, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        b.StatOverrides[Stat.ColdDamagePlusPct] = 100;
        var expected = new DamageRange(18, 36);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(18, panel[Stat.MainHandMinimumColdDamage]);
        Assert.Equal(36, panel[Stat.MainHandMaximumColdDamage]);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(expected, shot.Hit[DamageType.Cold]);
        Assert.Equal(expected, shot.AreaHit![DamageType.Cold]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
        Assert.Equal(0, Cold().Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, 18u, 36u);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expected, shot.Hit[DamageType.Cold]);
    }

    static void Accuracy_boots_use_resolved_skill_and_hand_inputs()
    {
        using var b = new Battle("Kinetic Blast", "Additional Accuracy Support", "Fireball", "Spellblade Support");
        var c = b.Player;
        b.StatOverrides[Stat.GlobalCannotCrit] = 1;
        b.StatOverrides[Stat.BaseDealNoPhysicalDamage] = 1;
        b.StatOverrides[Stat.SpellDamagePlusPct] = 0;
        b.StatOverrides[Stat.AccuracyRatingPlusPct] = 100;
        b.StatOverrides[Stat.AttackSpeedPlusPctPer200AccuracyRating] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var sockets = host.Item.Sockets.ToArray();
        foreach (int index in new[] { 0, 1 })
        {
            var socketGem = sockets[index].Gem!;
            sockets[index] = sockets[index] with { Gem = socketGem with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(socketGem.TypeHash)!)!.ExperienceFor(2) } };
        }
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { ItemLevel = 100, Sockets = sockets, Links = [2, 2] })!;
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(456, Game.PlayerAccuracy(c));
        Assert.Equal(3, c.Pools.AttackSpeedPct); // This existing stat grants speed per complete 150 accuracy.
        Item Boots(string name, string mod) => ItemCatalog.Create(Program.Items.Find(name)!) with
        { ItemLevel = 100, Mods = [new(ModSection.Implicit, Program.Items.Mods.ById(mod)!.Hash, [-25, 1, 6])] };
        var boots = items.Add(PlayerInventories.Boots, 0, 0,
            Boots("Cloudwhisper Boots", "AddedLightningDamagePerAccuracyReducedAccuracyImplicitR1_"))!;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(342, Game.PlayerAccuracy(c));
        Assert.Equal(2, c.Pools.AttackSpeedPct);
        b.StatOverrides.Remove(Stat.AttackSpeedPlusPctPer200AccuracyRating);
        c.Pools = Game.PoolsFor(c, 100);
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Kinetic Blast");
        var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        Assert.Equal(2, gem.Level);
        Assert.Equal(100, Game.SupportedSkill(c, gem.Id).Stats[Stat.AccuracyRating]);
        Assert.Equal(492, Game.SupportedAccuracy(c, gem.Id).Rating); // (228 + 100) * 2 * .75.
        Assert.Equal(new DamageRange(2.82, 16.92), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
        // An eligible Spellblade weapon still does not transfer this global attack-only grant.
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [item.Sockets[0], item.Sockets[1] with { Gem = item.Sockets[1].Gem! with { Quality = 20 } }, .. item.Sockets.Skip(2)] });
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(20, Game.SupportedSkill(c, gem.Id).Stats[Stat.AccuracyRatingPlusPct]);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating); // floor(328 * 2.2 * .75).
        var player = Game.CharacterPlayerStats(c);
        int Get(uint name) => player.GetValueOrDefault(name);
        foreach (var (rating, expected) in new[]
        {
            (199, new DamageRange(0, 0)), (200, new DamageRange(1, 6)),
            (399, new DamageRange(1, 6)), (400, new DamageRange(2, 12)),
        })
        {
            // Target modifiers and cannot-evade do not supply extra rating to the grant.
            var accuracy = new AttackAccuracyProfile(rating, MoreVsUnique: 100, MoreInCloseRange: 100, CannotBeEvaded: true);
            Assert.Equal(expected, SupportedAttackDamage.Raw(Game.WeaponFor(c), Get, accuracy)[DamageType.Lightning]);
            Assert.Equal(expected.Scale(1.5), SupportedAttackDamage.Raw(Game.WeaponFor(c), Get, accuracy, 20_000, 15_000)[DamageType.Lightning]);
        }
        Item AccurateWand() => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        {
            ItemLevel = 100, Rarity = ItemRarity.Magic,
            Mods = [new(ModSection.Explicit, Program.Items.Mods.ById("LocalIncreasedAccuracy5")!.Hash, [153])],
        };
        items.Add(PlayerInventories.Weapon2, 0, 0, AccurateWand());
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating);
        items.Add(PlayerInventories.Offhand1, 0, 0, AccurateWand());
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating);
        var off = Game.OffhandWeaponFor(c)!;
        Assert.Equal(793, Game.WeaponAccuracyFor(c, off, Game.CharacterPlayerStats(c), Game.SupportedSkill(c, gem.Id).Stats));
        // An off-hand swing (main's hand alternation) keeps the boots' less-accuracy factor.
        Assert.Equal(793, Game.SupportedAccuracy(c, gem.Id, AttackHand.Off).Rating);
        Assert.Equal(571, Game.HandAccuracy(c, AttackHand.Off, Game.CharacterPlayerStats(c))); // floor((228 + 153) * 2 * .75)
        Assert.Equal(new DamageRange(4.23, 25.38), Game.SupportedHitInputsFor(c, gem.Id, weapon: off)!.Raw[DamageType.Lightning]);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(541, panel[Stat.MainHandAccuracyRating]);
        Assert.Equal(793, panel[Stat.OffHandAccuracyRating]);
        Assert.Equal(16, panel[Stat.MainHandMaximumLightningDamage]);
        Assert.Equal(25, panel[Stat.OffHandMaximumLightningDamage]);
        items.Change(PlayerInventories.Boots, boots.Id,
            _ => Boots("Stormrider Boots", "AddedLightningDamagePerAccuracyReducedAccuracyImplicitR3"));
        b.ExemptItem = i => i.Id != boots.Id;
        c.Character.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.True(Game.IsInactive(c, boots.Id));
        Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning].Max);
        b.ExemptItem = _ => true;
        c.Character.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(new DamageRange(2.82, 16.92), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        b.StatOverrides[Stat.LightningDamagePlusPct] = 100;
        var expectedHit = new DamageRange(5.64, 33.84);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(541, shot.AccuracyProfile!.Value.Rating);
        Assert.Equal(expectedHit, shot.Hit[DamageType.Lightning]);
        Assert.Equal(expectedHit, shot.AreaHit![DamageType.Lightning]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single(g => g.Name == "Kinetic Blast").Id);
        Assert.Equal(793, Game.SupportedAccuracy(c, gem.Id).Rating);
        Assert.Equal(new DamageRange(4.23, 25.38), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        // Preserve the fraction until both more multipliers are applied: floor(381 * .75 * .7) = 200, not 199.
        b.StatOverrides.Remove(Stat.AccuracyRatingPlusPct);
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 1, 2] });
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(285, Game.PlayerAccuracy(c));
        // The fixture's clock is paused; seed the timed effect without exercising live-action admission.
        var effects = Game.ResourceEffectsOf(c);
        Assert.True(Game.ResourceEffectsCurrent(c, effects));
        Assert.True(effects.PlayerBlind.Inflict(Game.ResourceEffectsTime(c), 10, effectPercent: 50, immune: false));
        Assert.Equal(200, Game.PlayerAccuracy(c));
        Assert.Equal(200, Game.SupportedAccuracy(c, gem.Id).Rating);
        Assert.Equal(new DamageRange(1.41, 8.46), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        items.Remove(PlayerInventories.Boots, boots.Id);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning].Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, 5u, 33u);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expectedHit, shot.Hit[DamageType.Lightning]);
    }

    [Theory]
    [InlineData("Split Arrow", "Crude Bow")]
    [InlineData("Power Siphon", "Driftwood Wand")]
    public void Barrage_support_paces_attacks_using_the_resolved_projectile_count(string skill, string weapon)
    {
        using var b = new Battle(skill, "Barrage Support");
        var items = Game.ItemsOf(b.Player);
        var bow = items.Items(PlayerInventories.Weapon1).Single();
        items.Change(PlayerInventories.Weapon1, bow.Id, item => ItemCatalog.Create(Program.Items.Find(weapon)!) with
        { Sockets = item.Sockets, Links = item.Links });
        var gem = Game.GemSkillEffects(b.Player).Single();
        uint penaltyKey = Stat.SupportBarrageAttackTimePlusPctPerProjectileFired;
        uint countKey = Stat.TotalNumberOfProjectilesToFire;
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [1, 1] });
        double baseline = Game.SupportedAttackSeconds(b.Player, gem.Id);
        int increased = Game.SupportedMainHandAttackSpeedPct(b.Player, gem.Id);
        float nativeSpeed = (increased + 100) * (float)Game.Supports.AttackSpeedMultiplier(gem.Effect, gem.Level);
        int baselineSpeed = (int)MathF.Round(nativeSpeed - 100, MidpointRounding.AwayFromZero);
        Assert.Equal(0, Game.SupportedSkill(b.Player, gem.Id).Stats.GetValueOrDefault(penaltyKey));
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [2] });
        int initialCount = 0;
        foreach (int extra in new[] { 0, 2 })
        {
            b.StatOverrides[Stat.NumberOfAdditionalProjectiles] = extra;
            var stats = new Dictionary<uint, int>(Game.SupportedStats(b.Player, gem.Id));
            int penalty = stats[penaltyKey];
            Assert.True(penalty > 0);
            Game.ApplyProjectilePanelStats(gem.Effect, stats);
            int count = stats[countKey];
            Assert.True(count > extra + 1);
            if (extra == 0) initialCount = count;
            Assert.Equal(initialCount + extra, count);
            int speed = (int)MathF.Round(nativeSpeed / (1 + penalty * count / 100f) - 100,
                MidpointRounding.AwayFromZero);
            double expected = baseline * (baselineSpeed + 100) / (speed + 100);
            Assert.Equal(expected, Game.SupportedAttackSeconds(b.Player, gem.Id), 6);
        }
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [1, 1] });
        Assert.Equal(baseline, Game.SupportedAttackSeconds(b.Player, gem.Id), 6);
    }

    [Fact]
    public void Mirage_attacks_bypass_only_owner_restrictions_and_end_with_their_source()
    {
        foreach (var (ownerKey, prohibited) in new (uint, uint[])[]
        {
            (Stat.KeystoneAncestralBond, []), (Stat.DealNoDamageYourself, []),
            (Stat.KeystoneAncestralBond, [Stat.BaseDealNoPhysicalDamage, Stat.BaseDealNoColdDamage]),
            (Stat.DealNoDamageYourself, [Stat.BaseDealNoDamage]),
        })
        {
            using var b = new Battle("Ice Shot", "Mirage Archer Support");
            b.Enemy.Walk.Teleport(170, 100, 0);
            uint increase = Stat.ProjectileDamagePlusPct;
            b.StatOverrides[ownerKey] = 1;
            b.StatOverrides[increase] = 37;
            foreach (uint stat in prohibited) b.StatOverrides[stat] = 1;
            var gem = Game.GemSkillEffects(b.Player).Single();
            var use = Game.CaptureGemProjectile(b.Player, b.Session, gem.Id, gem, (170, 100), new(1));
            var owner = Assert.IsType<ProjectileOffense>(use.Payload);
            b.Session.Schedule(0, () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
            ProjectileOffense? proxy = null;
            for (int tenth = 5; tenth <= 48 && proxy is null; tenth++)
            {
                b.Session.AdvanceTo(ProjectileRuntime.Time(tenth / 10d));
                proxy = b.Session.Runtime.Projectiles.Select(p => p.Use.Payload).OfType<ProjectileOffense>()
                    .FirstOrDefault(p => p.ActualCaster is not null);
            }
            Assert.NotNull(proxy);
            var caster = Assert.IsAssignableFrom<IReadOnlyDictionary<uint, int>>(proxy!.ActualCaster);
            Assert.Equal(0, caster.GetValueOrDefault(ownerKey));
            Assert.Equal(37, caster[increase]);
            foreach (uint stat in prohibited) Assert.Equal(1, caster[stat]);
            Assert.Null(proxy.MirageSource);
            Assert.Null(proxy.CriticalTrigger);
            int penalty = owner.SkillStats[Stat.SupportMirageArcherDamagePlusPctFinal];
            Assert.Equal(owner.Hit.MaxTotal * (1 + penalty / 100d), proxy.Hit.MaxTotal, 6);
            b.Session.AdvanceTo(ProjectileRuntime.Time(4.8));
            if (prohibited.Length == 0) Assert.True(b.Enemy.Life < b.Enemy.MaxLife);
            else Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
            uint expired = b.Enemy.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(9));
            Assert.Equal(expired, b.Enemy.Life);
            Assert.Equal(1, Game.CharacterPlayerStats(b.Player)[ownerKey]);
            Assert.Equal(1000u, b.Player.PlayerMana);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        }
        foreach (bool replace in new[] { false, true })
        {
            using var b = new Battle("Ice Shot", "Mirage Archer Support");
            b.StatOverrides[Stat.KeystoneAncestralBond] = 1;
            var gem = Game.GemSkillEffects(b.Player).Single();
            b.Session.Schedule(0, () => true, () => Game.CaptureGemProjectile(b.Player, b.Session,
                gem.Id, gem, (120, 100), new(1)));
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            var items = Game.ItemsOf(b.Player);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => replace ? item with
            {
                Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem("Ice Shot")!.Value.Type)), item.Sockets[1]],
            } : item with { Links = [1, 1] });
            Assert.Equal(gem.Id, Game.GemSkillEffects(b.Player).Single().Id);
            b.Session.AdvanceTo(ProjectileRuntime.Time(9));
            Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
        }
    }
}
