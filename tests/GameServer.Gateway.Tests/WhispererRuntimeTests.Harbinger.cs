using GameServer.Core.Protocol.Instance;
using GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

public sealed partial class WhispererRuntimeTests
{
    internal static async Task CombinedHarbingerGameplay()
    {
        string? previous = Environment.GetEnvironmentVariable("Game_VERIFIED_HARBINGER");
        Environment.SetEnvironmentVariable("Game_VERIFIED_HARBINGER", "1");
        try
        {
            var definition = new AscendancyCatalog().Definitions.Single(d => d.Name == "Harbinger");
            byte classRow = (byte)Enumerable.Range(0, 7).First(row => Program.Skills.ClassFor(row).IntegerId == definition.BaseClass);
            using var h = new Harness(definition, classRow);
            var c = h.Conn;
            c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 1000;
            c.PlayerStats[Key("virtual_strength_for_requirements")] = 1000;
            var origin = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
            var gameplay = new HarbingerGameplay(() => origin.AddSeconds(h.Time.At));
            using var grants = GameEvents.GrantedSkillExecutable.Add(h.Provider.Id, gameplay.Executable);
            using var stats = GameEvents.PlayerStatSources.Add(h.Provider.Id, gameplay.StatsFor);
            using var casts = GameEvents.SkillCastAt.Add(h.Provider.Id, gameplay.Cast);
            using var ticks = GameEvents.CompanionTick.Add(h.Provider.Id, gameplay.Tick);
            ushort[] PathTo(ushort target)
            {
                var queue = new Queue<ushort>(); queue.Enqueue(definition.StartNode);
                var parents = new Dictionary<ushort, ushort> { [definition.StartNode] = definition.StartNode };
                while (queue.TryDequeue(out ushort node))
                    foreach (ushort adjacent in definition.Nodes[node].Adjacent)
                        if (parents.TryAdd(adjacent, node)) queue.Enqueue(adjacent);
                var result = new List<ushort>();
                for (ushort node = target; node != definition.StartNode; node = parents[node]) result.Add(node);
                return [.. result];
            }
            h.Build([.. new ushort[] { 20105, 60867, 18859, 34822 }.SelectMany(PathTo).Distinct()]);
            c.PlayerSent = c.PlayerObjectCreated = true;
            c.Clock.Start();
            Assert.True(h.Policy.Supports(definition.Identity));
            AscendancyPolicies.RequirePlayable(c, definition.Identity);
            var catalog = new HarbingerCompanionCatalog(skills: Game.SkillsFor(c));
            foreach (var source in catalog.All)
            {
                var skill = Assert.Single(Game.GemSkillEffects(c), g => g.Effect == source.SummonEffect);
                Assert.True(Game.TryBeginSkillCooldownUse(c, skill.Id, skill.Effect,
                    Game.CooldownFor(c, skill.Effect, skill.Level, skill.Quality), false, () => c.ActionCount++, out var use));
                Assert.True(await Game.CastGemSkill("Harbinger", h.Wire, h.Ciphers, c, skill.Id, (100, 100), (100, 100),
                    actionToken: use?.ActionToken ?? c.ActionCount, cooldownUse: use));
                int before = c.Minions.All.Count;
                await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
                Assert.Equal(before, c.Minions.All.Count);
                h.Time.At += 2;
                await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
                Assert.Equal(before + 1, c.Minions.All.Count);
            }
            Assert.All(c.Minions.All, m => { Assert.False(m.Fights); Assert.Equal(1u, m.Monster.MaxLife); Assert.False(m.Monster.Hostile); });
            h.Time.At += 30;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            h.Time.At += 1;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.True(Game.PlayerActionFactor(c) > 1);
            var countStats = Game.CharacterStatSources(c).Stats;
            foreach (var source in catalog.All)
                if (source.CountStat is uint count) Assert.Equal(1, countStats.GetValueOrDefault(count));
            var time = Assert.Single(c.Minions.All, m => m.Kind == "Time");
            time.Monster.Walk.Teleport(200, 200, h.Time.At);
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.True(Game.Dist(time.Monster.Walk.Position(h.Time.At), c.Walk.Position(h.Time.At)) < 12);

            // Accepted curse casts use the actual socket source and normal payment; reflected self suppression
            // keeps the original running budget. The aura uses the existing reservation owner.
            var temporal = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemTemporalChains")!);
            h.Items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
                { Sockets = [new(SocketColour.White, temporal)], Links = [1] });
            var curse = Assert.Single(Game.GemSkillEffects(c), g => Game.SkillsFor(c).EffectId(g.Effect) == "TemporalChains");
            var enemy = new SpawnedMonster("curse recipient", new(104, 100, 0), 0, 0, 1000);
            c.Monsters[42] = enemy;
            c.PlayerStats[Key("reflect_curses")] = 1;
            c.ActionCount++;
            Assert.True(await Game.CastGemSkill("Harbinger", h.Wire, h.Ciphers, c, curse.Id, (100, 100), (104, 100), actionToken: c.ActionCount));
            Assert.Equal(1, enemy.ReceivedActionFactor);
            h.Time.At += 2;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.True(enemy.ReceivedActionFactor < 1);

            Assert.Null(await Game.ChangePassives("Harbinger", h.Wire, h.Ciphers, c, [60867], false, "companion source refund", refundPoints: true));
            Assert.DoesNotContain((ushort)60867, CharacterSaves.Load(c.Character!)!.MainAscendancy!.Allocated);
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.DoesNotContain(c.Minions.All, m => m.Kind == "Time");
            Assert.True(Game.CharacterStatSources(c).Stats[Key("buff_time_passed_+%_other_than_temporal_chains")] < 0);
            c.PlayerStats[Key("unaffected_by_curses")] = 1;
            Assert.Equal(0, Game.CharacterStatSources(c).Stats.GetValueOrDefault(Key("temporal_chains_action_speed_+%_final")));
            c.PlayerStats.Remove(Key("unaffected_by_curses"));
            Assert.True(Game.CharacterStatSources(c).Stats[Key("temporal_chains_action_speed_+%_final")] < 0);
            var auraGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemTemporalChains")!);
            var blasphemy = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemBlasphemy")!);
            h.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
                { Sockets = [new(SocketColour.White, auraGem), new(SocketColour.White, blasphemy)], Links = [2] });
            var aura = Assert.Single(Game.GemSkillEffects(c), g => Game.SkillsFor(c).EffectId(g.Effect) == "TemporalChains" && Game.SupportedSkill(c, g.Id).Supports.Any(s => s.Effect.Id == "SupportBlasphemy"));
            Assert.Equal((0, 0), Game.SupportedReservation(c, aura.Id));
            Assert.True(await Game.ToggleAura("Harbinger", h.Wire, h.Ciphers, c, aura.Id, aura.Effect, aura.Name, (0, 0), aura.Level, aura.Quality));
            Assert.Contains(aura.Id, c.Auras.Keys);
            h.Time.At += .1;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.True(enemy.ReceivedActionFactor < 1);

            // Source removal is observed by a stat read before the next minion step.
            var channelGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemIncinerate")!);
            h.Items.Add(PlayerInventories.Gloves, 0, 0, ItemCatalog.Create(Program.Items.Find("Wool Gloves")!) with
                { Sockets = [new(SocketColour.White, channelGem)], Links = [1] });
            var channel = Assert.Single(Game.GemSkillEffects(c), g => Game.SkillsFor(c).EffectId(g.Effect) == "Incinerate");
            c.ActionCount++;
            Assert.True(await Game.CastGemSkill("Harbinger", h.Wire, h.Ciphers, c, channel.Id, (100, 100), (104, 100), actionToken: c.ActionCount));
            Assert.NotNull(c.PaidChannel);
            Assert.True(Game.PaidChannelCurrent(c, c.PaidChannel));
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            h.Time.At += .8;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.Equal(-20, Game.CharacterStatSources(c).Stats.GetValueOrDefault(Key(IncomingResourceDefences.TakenStat)));
            c.ChannelSkill = 0; c.PaidChannel = null;
            Assert.Equal(-20, Game.CharacterStatSources(c).Stats.GetValueOrDefault(Key(IncomingResourceDefences.TakenStat)));

            c.MainAscendancy = new(definition.Identity, [], AscendancyRules.AwardKeys);
            var cleared = Game.CharacterStatSources(c).Stats;
            Assert.DoesNotContain(catalog.All.Where(s => s.CountStat.HasValue), s => cleared.GetValueOrDefault(s.CountStat!.Value) != 0);
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.Empty(c.Minions.All);

            // Generic category clocks preserve BASE budgets and cannot revive an expired effect.
            var charges = new Charges.State(); var limits = new Charges.Limits(3, 0, 10000);
            charges.Gain(ChargeType.Endurance, 1, 0, limits);
            charges.SetExpiryRate(2, .5);
            Assert.Equal(18, charges.ExpiresAt(ChargeType.Endurance));
            Assert.Equal(8, Assert.Single(charges.Carry(2)).Remaining);
            charges.SetExpiryRate(19, .25); Assert.Equal(0, charges.Held(ChargeType.Endurance));
            var fortify = new FortificationState(); fortify.GrantExplicitStacks(0, 4);
            fortify.SetExpiryRate(2, .5); Assert.Equal(10, fortify.Read(2).NextExpiry);
            Assert.Equal(8, fortify.StackSeconds);
            Assert.Equal(4, Assert.Single(fortify.Carry(2).Grants).Remaining);
            var ignite = new MonsterIgnites(); ignite.Add(0, 10, 4); ignite.SetExpiryRate(2, .5);
            Assert.Equal(6, ignite.Until); Assert.Equal(60, ignite.Advance(6, 1));

            // PendingTransfer has been consumed before Closing; successful Auth handoff retains summons
            // even when the source closes before the destination attaches its roster lease.
            var departing = h.NewConnection("handoff" + Guid.NewGuid().ToString("N"), classRow: classRow);
            departing.MainAscendancy = new(definition.Identity, PathTo(20105), AscendancyRules.AwardKeys);
            departing.ProjectileRuntimeState = h.Time;
            departing.PlayerSent = departing.PlayerObjectCreated = true;
            departing.Pools = Game.PoolsFor(departing, 68);
            departing.PlayerLife = departing.PlayerMaxLife; departing.PlayerMana = departing.Pools.Mana;
            departing.Clock.Start();
            var summon = Assert.Single(Game.GemSkillEffects(departing), g => g.Effect == catalog.All.Single(s => s.Node == 20105).SummonEffect);
            Assert.True(Game.TryBeginSkillCooldownUse(departing, summon.Id, summon.Effect,
                Game.CooldownFor(departing, summon.Effect, summon.Level, summon.Quality), false, () => departing.ActionCount++, out var summonUse));
            Assert.True(await Game.CastGemSkill("Harbinger", h.Wire, h.Ciphers, departing, summon.Id, (100, 100), (100, 100),
                actionToken: summonUse?.ActionToken ?? departing.ActionCount, cooldownUse: summonUse));
            h.Time.At += 2;
            await Game.PlayerMinionsTick("Harbinger", h.Wire, h.Ciphers, departing, h.Time.At);
            Assert.Single(departing.Minions.All);
            var selection = departing.MainAscendancy;
            var transfer = new PendingTransfer(1, 1, false, AtSpawn: true);
            Assert.True(departing.TryBeginAreaChange(transfer, out _));
            await Game.Transfer("Harbinger", h.Wire, h.Ciphers, departing, transfer,
                _ => Task.FromResult(new ControlResponse { Ok = true, Ticket = 912, Area = 1, Instance = 1 }));
            Assert.Null(departing.PendingTransfer); Assert.True(departing.TransferAccepted);
            gameplay.Close(departing);
            Assert.Empty(departing.Minions.All);
            var arriving = h.NewConnection(departing.Character!, 912, classRow: classRow);
            arriving.MainAscendancy = selection;
            arriving.ProjectileRuntimeState = h.Time;
            // Real admission builds pools before InitializeCharacterVitals restores the initial zero Life.
            arriving.PlayerLife = 0;
            _ = Game.CharacterStatSources(arriving);
            arriving.Pools = Game.PoolsFor(arriving, 68);
            arriving.PlayerLife = arriving.PlayerMaxLife; arriving.PlayerMana = arriving.Pools.Mana;
            arriving.PlayerSent = arriving.PlayerObjectCreated = true;
            await gameplay.Tick("Harbinger", h.Wire, h.Ciphers, arriving, h.Time.At);
            Assert.Single(arriving.Minions.All);
            gameplay.Close(arriving); // Logout without an accepted transfer must clear the retained roster.
            h.Provider.Stop(_ => { });
            Assert.True(c.Closed);
            await gameplay.Tick("Harbinger", h.Wire, h.Ciphers, c, h.Time.At);
            Assert.Empty(c.Minions.All);
            gameplay.Close(c);
        }
        finally { Environment.SetEnvironmentVariable("Game_VERIFIED_HARBINGER", previous); }
    }
}
