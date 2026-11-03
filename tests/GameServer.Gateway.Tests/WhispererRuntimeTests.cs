using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Runtime.Loader;
using System.Collections;
using League = GameServer.Core.Accounts.League;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

public sealed partial class WhispererRuntimeTests
{
    static readonly ushort[] A = [17123, 4174, 1116, 22720, 27553, 50932, 15390, 26415];
    static readonly ushort[] B = [30419, 53715, 21095, 2266, 540, 863, 9982, 5888];
    static readonly ushort[] C = [17123, 4174, 1116, 22720, 24726, 23799, 9982, 5888];
    static readonly ushort[] Seam = [17123, 4174, 15390, 26415, 540, 863, 9982, 5888];
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static (FlaskEnchantments.Trigger Trigger, uint Item, byte Slot)[] FlaskSignals(ClientState conn)
    {
        return conn.FlaskAutomation.Events.Select(s => (s.Trigger, s.Item, s.Slot)).ToArray();
    }
    static double LifeRate(ClientState c) { _ = Game.CharacterPlayerStatsCore(c, out double life); return life; }
    static double ManaRate(ClientState c)
    {
        var stats = Game.CharacterPlayerStats(c);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return PanelStats.ManaPerMinute(PanelStats.WhileMoving(Get, c.Walk.Moving(c.Clock.Elapsed.TotalSeconds)), c.Pools.Mana) / 60;
    }
    static byte[] BlindStats(uint actor, int less) => InstanceEntities.StatsUpdate(actor,
        [(973u, less > 0 ? 1 : 0), (15023u, -less), (15024u, -less)]);
    static void Has(byte[] bytes, byte[] expected) => Assert.True(bytes.AsSpan().IndexOf(expected) >= 0,
        $"Missing packet {Convert.ToHexString(expected)}");

    [Fact]
    public async Task Actual_Whisperer_policy_sources_paid_resources_and_refunds_share_the_generic_engines()
    {
        var definition = Assert.Single(new AscendancyCatalog().Definitions,
            d => d.Identity.Family == AscendancyFamily.Phrecia && d.Name == "Whisperer");
        Assert.Equal(new(AscendancyFamily.Phrecia, "Deadeye", "3.29.3b"), definition.Identity);
        Assert.Equal((byte)2, definition.BaseClass); Assert.Equal(4, definition.TableRow);
        Assert.Equal((byte)2, definition.ClassNo); Assert.Equal((ushort)56856, definition.StartNode);
        Assert.Equal(19, definition.Nodes.Count);
        var absent = Harness.Connection("absent"); absent.MainAscendancy = new(definition.Identity);
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(absent, definition.Identity));
        Assert.True(AscendancyPolicies.Available(absent, definition.Identity with { Family = AscendancyFamily.Normal }));
        using var h = new Harness(definition); var c = h.Conn;
        var excluded = Harness.Connection("excluded"); excluded.MainAscendancy = new(definition.Identity);
        Assert.False(AscendancyPolicies.Available(excluded, definition.Identity));
        h.OptIn(false); Assert.False(h.Policy.Supports(definition.Identity));
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(c, definition.Identity));
        h.OptIn(true);
        Assert.False(h.Policy.Supports(definition.Identity with { Revision = "future" }));
        Assert.False(h.Policy.Supports(definition.Identity with { Id = "Whisperer" }));
        AscendancyPolicies.RequirePlayable(c, definition.Identity); // Real policy verifies the actual packed graph.
        Assert.Equal(1, AscendancyPolicies.AreaStats(c, definition.Identity)![21146]);
        Assert.Null(AscendancyPolicies.AreaStats(absent, definition.Identity with { Family = AscendancyFamily.Normal }));
        foreach (var build in new[] { A, B, C })
        {
            h.Build(build);
            var raw = Game.CharacterStatSources(c).Stats;
            foreach (ushort node in build)
                foreach (var (key, value) in AscendancyPolicies.NodeStats(c, definition, node, definition.Nodes[node].Stats))
                    Assert.NotEqual(0, raw.GetValueOrDefault(key));
        }
        h.Build(A); var skill = h.Fireball;
        var ranger = Game.SkillsFor(c).ClassFor(c.ClassRow);
        uint ManaForFinalDex() => (uint)decimal.Floor((ranger.BaseMaxMana + 6L * 68 + c.Pools.Dexterity) * 1.24m);
        Assert.Equal(ManaForFinalDex(), c.Pools.Mana); // Actual 4174 removes Intelligence and uses final Dexterity.
        c.PlayerStats[Key("dexterity_+%")] = 50; c.Pools = Game.PoolsFor(c, 68);
        Assert.Equal(ManaForFinalDex(), c.Pools.Mana);
        c.PlayerStats.Remove(Key("dexterity_+%")); c.Pools = Game.PoolsFor(c, 68);
        Assert.True(Game.PayMana(c, skill.Id, out uint doubled, out _)); Assert.Equal(10u, doubled);
        Assert.Equal(10, h.SkillRecordCost(skill.Id));
        Assert.Null(await Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [22720], false, "paid refund", refundPoints: true));
        Assert.Equal((ushort)195, c.RefundPoints);
        Assert.DoesNotContain((ushort)22720, CharacterSaves.Load(c.Character!)!.MainAscendancy!.Allocated);
        Assert.True(Game.PayMana(c, skill.Id, out uint ordinary, out _)); Assert.Equal(5u, ordinary);
        Assert.Equal(5, h.SkillRecordCost(skill.Id));
        Assert.Null(await Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [22720], true, "reallocate"));
        Assert.Equal(10, h.SkillRecordCost(skill.Id));

        h.Build(Seam);
        var state = Game.ResourceEffectsOf(c); state.Mana.Spent(199, 10);
        var poolsBeforeBlock = c.Pools;
        c.Pools = c.Pools with { Evasion = 0, BlockChance = 100, LifeGainedOnBlock = 0, ManaGainedOnBlock = 0 };
        c.IncomingHitRandom = new FixedDraw(0);
        var attack = new MonsterAttack(0x102, .4, 10, DamagePacket.Of(DamageType.Physical, 100, 100));
        var enemy = new SpawnedMonster("blocked", new(105, 100, 0), 0, 0, 1000) { Attack = attack };
        uint beforeBlock = c.PlayerMana;
        double blockAt = Game.ProjectileWorldTime(c);
        var blocked = Game.CommitMonsterHitLocked("Whisperer", c, 42, enemy, attack, blockAt, (100, 100), (105, 100), false)!;
        Assert.True(blocked.Blocked);
        await Game.PresentMonsterHit("Whisperer", h.Wire, h.Ciphers, c, 42, blocked);
        Assert.Single(FlaskSignals(c), s => s.Trigger == FlaskEnchantments.Trigger.Block);
        Assert.Equal(beforeBlock, c.PlayerMana); Assert.Equal(0, state.Mana.Take(blockAt + 4));
        Assert.Equal(199, state.Mana.SpentRemainder); c.Pools = poolsBeforeBlock;

        // Main's SavageHit is based on actual Life lost, after typed defensive Mana, not the raw hit size.
        c.Pools = c.Pools with { Life = 1000, Mana = 1000, Armour = 0, Evasion = 0, BlockChance = 0 };
        c.PlayerLife = c.PlayerMana = 1000; c.Ward.SetMaximum(0); c.EnergyShield.SetMaximum(0, false);
        c.IncomingHitRandom = new HighDraw();
        var heavy = attack with { Damage = DamagePacket.Of(DamageType.Physical, 200, 200) };
        double hitAt = Game.ProjectileWorldTime(c);
        var defended = Game.CommitMonsterHitLocked("Whisperer", c, 42, enemy, heavy, hitAt, (100, 100), (105, 100), false)!;
        Assert.False(defended.Blocked); Assert.Equal(1000u, defended.Life); Assert.Equal(800u, defended.Mana);
        Assert.DoesNotContain(FlaskSignals(c), s => s.Trigger == FlaskEnchantments.Trigger.SavageHit);
        Assert.Equal(199, state.Mana.SpentRemainder);
        c.PlayerMana = 0;
        var savage = Game.CommitMonsterHitLocked("Whisperer", c, 42, enemy, heavy, hitAt, (100, 100), (105, 100), false)!;
        Assert.Equal(800u, savage.Life); Assert.Equal(0u, savage.Mana);
        Assert.Single(FlaskSignals(c), s => s.Trigger == FlaskEnchantments.Trigger.SavageHit);
        c.Pools = poolsBeforeBlock;
        state.Mana.Clear(); state.Mana.Spent(199, 10);
        c.PlayerMana = 60; c.Ward.SetMaximum(50); c.EnergyShield.SetMaximum(30, true);
        var player = Game.CharacterPlayerStats(c);
        Assert.Equal(20, Game.DamagePlayerHitTyped(c, 100, 10, 10, 10, player));
        Assert.Equal(40u, c.PlayerMana); Assert.Equal(0u, c.EnergyShield.Current);
        Assert.Equal(199, state.Mana.SpentRemainder); // Defensive Mana is not a cost.
        Assert.Equal(14, state.Mana.Take(14)); // Post-Ward 70 includes the ES/Mana portions.
        state.Mana.Clear(); state.Mana.Spent(199, 10);
        state.Mana.Hit(20, 1, 20); Assert.Equal(0, state.Mana.Take(24));
        c.PlayerMana = c.Pools.Mana; Game.SettleFullManaLocked(c, 24);
        Assert.Equal(199, state.Mana.SpentRemainder);
        state.Mana.Hit(25, 4, 20); Assert.Equal(0, state.Mana.Take(29)); // Discarded .2 cannot combine with .8.
        state.Mana.Clear(); state.Mana.Spent(199, 10); c.Rage.Clear(); c.PlayerMana = 100;
        Assert.True(Game.PayMana(c, skill.Id, out uint paid, out _)); Assert.Equal(5u, paid);
        Assert.Equal(4, state.Mana.SpentRemainder);
        Assert.Equal(10, c.Rage.Read(0, new(NoInherentLoss: true)).Current);
        c.PlayerMana = 0; Assert.False(Game.PayMana(c, skill.Id, out _, out _)); Assert.Equal(4, state.Mana.SpentRemainder);
        Assert.Null(await Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [863], false, "spend source refund", refundPoints: true));
        Assert.Equal(0, state.Mana.SpentRemainder);

        h.Build(B); c.PlayerMana = c.Pools.Mana; c.PlayerLife -= 100;
        var manaType = Program.Items.Find("Small Mana Flask")!;
        foreach (var old in h.Items.Items(PlayerInventories.Flask).Where(i => i.X == 0).ToArray()) h.Items.Remove(PlayerInventories.Flask, old.Id);
        h.Items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(manaType) with { Charges = 24 });
        var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        var use = Game.UseFlask("Whisperer", h.Wire, h.Ciphers, c, 0);
        Assert.False(use.IsCompleted);
        // The accepted use keeps the old recipient snapshot while a real paid node refund is queued behind it.
        var refund = Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [53715], false, "flask source refund", refundPoints: true);
        Assert.DoesNotContain((ushort)53715, c.MainAscendancy.Allocated);
        gate.Release(); await Task.WhenAll(use, refund);
        var acceptedMana = h.Items.Items(PlayerInventories.Flask).Single(i => i.X == 0);
        Assert.Contains((FlaskEnchantments.Trigger.Adjacent, acceptedMana.Id, (byte)0), FlaskSignals(c));
        Assert.DoesNotContain(FlaskSignals(c), s => s.Trigger == FlaskEnchantments.Trigger.LifeFlask);
        var effect = Assert.Single(c.FlaskEffects.End(_ => true));
        Assert.True(effect.ManaPerSecond > 0, effect.ToString()); Assert.Equal(0, effect.Queue); Assert.True(effect.ManaStays);
        c.FlaskEffects.Start(effect); // Reuse the actually accepted effect and its captured extra recipient.
        var recovered = Game.AdvanceFlaskRecovery(c, effect.Started, effect.Ends);
        Assert.Equal(50, recovered.Mana, 7); Assert.Equal(50, recovered.Life, 7);
        foreach (var old in h.Items.Items(PlayerInventories.Flask).Where(i => i.X == 1).ToArray()) h.Items.Remove(PlayerInventories.Flask, old.Id);
        h.Items.Add(PlayerInventories.Flask, 1, 0, ItemCatalog.Create(Program.Items.Find("Small Life Flask")!) with { Charges = 21 });
        await Game.UseFlask("Whisperer", h.Wire, h.Ciphers, c, 1);
        var acceptedLife = h.Items.Items(PlayerInventories.Flask).Single(i => i.X == 1);
        Assert.Contains((FlaskEnchantments.Trigger.Adjacent, acceptedLife.Id, (byte)1), FlaskSignals(c));
        Assert.Contains((FlaskEnchantments.Trigger.LifeFlask, acceptedLife.Id, (byte)1), FlaskSignals(c));
        c.FlaskEffects.End(_ => true);
        var atEnd = effect with { Started = 0, Ends = 1, ManaPerSecond = 0, ManaAtEnd = 50 };
        Game.StartRecoveryEffectsLocked(c, [atEnd], copy: true);
        recovered = Game.AdvanceFlaskRecovery(c, 0, 1);
        Assert.Equal(50, recovered.Mana); Assert.Equal(0, recovered.Life);
        var queuedMana = effect with { Started = 10, Ends = 12, NoQueue = false, ManaStays = false };
        var queuedLife = effect with { Started = 10, Ends = 14, ManaPerSecond = 0, LifePerSecond = 20, NoQueue = false };
        Assert.Equal(2, queuedMana.Queue); Assert.Equal(1, queuedLife.Queue);
        Game.StartRecoveryEffectsLocked(c, [queuedLife, queuedMana], copy: true);
        recovered = Game.AdvanceFlaskRecovery(c, 10, 12);
        Assert.Equal(40 + recovered.Mana, recovered.Life, 7); Assert.Single(recovered.Ended);

        var combined = h.NewConnection("totem-resource", optional: false);
        combined.Pools = combined.Pools with { Mana = 1000 };
        combined.PlayerLife = 1000; combined.PlayerMana = 100;
        combined.Ward.SetMaximum(20); combined.EnergyShield.SetMaximum(30, true);
        var totem = new TotemState.Totem(77, 1, (100, 100), 1000, double.PositiveInfinity, null);
        combined.Totems.Place(totem, 1, 0);
        var combinedStats = new Dictionary<uint, int>
        {
            [Key(Game.TotemRedirectStat)] = 20,
            [Key("physical_damage_%_taken_from_mana_before_life")] = 100,
            [Key("damage_taken_goes_to_mana_%")] = 20,
        };
        var combinedState = Game.ResourceEffectsOf(combined);
        Assert.Equal(80, Game.DamagePlayerHitTyped(combined, 100, 50, 50, 10, combinedStats));
        Assert.Equal(960u, totem.Life); Assert.Equal(70u, combined.PlayerMana);
        Assert.Equal(0u, combined.EnergyShield.Current); Assert.True(combined.Ward.Broken);
        Assert.Equal(28, combinedState.Mana.Take(14)); // Totem first; post-Ward Recoup includes ES and Mana.
        Assert.Equal(0, combinedState.Mana.SpentRemainder);

    }

    [Fact]
    public async Task Shared_Blind_transfer_and_retirement_use_current_independent_state_without_wall_clock_waits()
    {
        var definition = Assert.Single(new AscendancyCatalog().Definitions,
            d => d.Identity.Family == AscendancyFamily.Phrecia && d.Name == "Whisperer");
        using var h = new Harness(definition); var c = h.Conn; h.Build(B);
        await QuestAndCollisionReloads(h);
        var (commands, replies) = h.CommandsFor(c);
        Assert.Contains(commands.Usages, command => command.Name == "blind");
        foreach (string invalid in new[] { "0", "61", "NaN" }) Assert.True(await commands.RunAsync("/blind " + invalid));
        Assert.All(replies, reply => Assert.StartsWith("Usage: /blind", reply)); replies.Clear();
        Assert.True(await commands.RunAsync("/blind")); Assert.Equal("Blind refused (immune or unavailable).", Assert.Single(replies));
        Assert.False(Game.InflictPlayerBlind(c, 4)); // Actual 5888 immunity.
        var target = new SpawnedMonster("Blind", new(105, 100, 0), 0, 0, 1000) { Level = 60, RampageBlindUntil = 6 };
        c.Monsters[42] = target;
        var source = Game.CharacterPlayerStats(c);
        Assert.Equal((100, 50, 4d), Game.BlindInputs(source));
        Assert.True(Game.InflictMonsterBlind(c, 42, target, 0, (100, 0, 4)));
        c.FlaskGrounds.SetBlinded(42, true, () => c.NextBuff++);
        Has(Game.BlindPresentation(c)!, BlindStats(42, 30));
        var state = Game.ResourceEffectsOf(c); ushort firstBuff = state.TargetBlind(42, target).Buff;
        h.Time.At = 1;
        Assert.True(Game.InflictMonsterBlind(c, 42, target, 1, (100, 0, 4)));
        var renewed = Game.BlindPresentation(c)!;
        Has(renewed, InstanceEntities.RemoveBuff(42, firstBuff));
        Assert.NotEqual(firstBuff, state.TargetBlind(42, target).Buff);
        Assert.Equal((20, 5d), state.TargetBlind(42, target).SentOwn);
        Assert.Equal((30, 0d), state.TargetBlind(42, target).Sent);
        c.BlindAura.Reconcile(new HashSet<uint> { 42 }, c.Monsters.ContainsKey, () => c.NextBuff++).ToArray();
        Assert.True(Game.Blinded(c, 42)); Assert.Equal(70, Game.UnderBlind(c, 42, 100));
        Assert.Equal(75, Game.CriticalChanceOn(c, 42, 25, 75));
        Assert.Equal(-10, Game.DamageTakenFromBlindedPct(c, 42,
            new Dictionary<uint, int> { [Key(Game.FromBlindedDamageTakenStat)] = -10 }));
        c.BlindAura.Reconcile(new HashSet<uint>(), c.Monsters.ContainsKey, () => c.NextBuff++).ToArray();
 // Merged value unchanged, own native timer renewed.
        int reads = 0;
        using (GameEvents.PlayerStatSources.Add("Game", (_, stats) => { reads++; return stats; }))
        {
            Game.BlindPresentation(c); int one = reads; reads = 0;
            for (uint id = 50; id < 82; id++)
            { c.Monsters[id] = new("smoke", new(105, 100, 0), 0, 0, 100); c.FlaskGrounds.SetBlinded(id, true, () => c.NextBuff++); }
            Game.BlindPresentation(c); Assert.Equal(one, reads);
        }
        int evasion = Game.Monsters.BaseEvasion(60)!.Value;
        int chance = DamageModel.AccuracyHitChance(evasion, 500);
        Assert.True(Game.RampageAttackHits(c, 42, target, 500, 1, new FixedDraw(chance), source));
        c.FlaskGrounds.SetBlinded(42, false, () => 0);
        state.TargetBlind(42, target).Read(5); target.RampageBlindUntil = 6;
        h.Time.At = 5; Has(Game.BlindPresentation(c)!, BlindStats(42, 20));
        h.Time.At = 6; Has(Game.BlindPresentation(c)!, BlindStats(42, 0));
        var removed = new SpawnedMonster("retired actor", new(105, 100, 0), 0, 0, 100);
        c.Monsters[90] = removed;
        Assert.True(Game.InflictMonsterBlind(c, 90, removed, 6, (100, 50, 4)));
        Game.BlindPresentation(c); ushort removedBuff = state.TargetBlind(90, removed).Buff;
        c.Monsters[90] = new("replacement actor", new(105, 100, 0), 0, 0, 100);
        var replaced = Game.BlindPresentation(c)!;
        Has(replaced, InstanceEntities.RemoveBuff(90, removedBuff)); Has(replaced, BlindStats(90, 0));
        Assert.Equal(0, state.TargetBlind(90, c.Monsters[90]).Read(6).Less);

        c.FlaskGrounds.SetBlinded(42, true, () => c.NextBuff++);
        Game.BlindPresentation(c); h.Wire.Drain(h.Receive);
        var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        var queued = Game.SendBlind("Whisperer", h.Wire, h.Ciphers, c);
        var refund = Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [5888], false, "Blind source refund", refundPoints: true);
        Assert.DoesNotContain((ushort)5888, c.MainAscendancy.Allocated);
        gate.Release(); await Task.WhenAll(queued, refund);
        var current = h.Wire.Drain(h.Receive);
        Has(current, BlindStats(42, 20)); Assert.True(current.AsSpan().IndexOf(BlindStats(42, 30)) < 0);

        // Capture real gem releases, refund their actual source, then land the admitted old snapshots.
        foreach (var (effectId, weaponName) in new[] { ("BurningArrow", "Crude Bow"), ("Fireball", "Driftwood Wand") })
        {
            h.Build(C);
            foreach (var item in h.Items.Items(PlayerInventories.Weapon1).ToArray()) h.Items.Remove(PlayerInventories.Weapon1, item.Id);
            var gemItem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + effectId)!);
            h.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(weaponName)!) with
                { Sockets = [new(SocketColour.White, gemItem)], Links = [1] });
            c.StatSourcesCacheState = null; c.Pools = Game.PoolsFor(c, 68) with { Accuracy = 500 };
            c.PlayerStats[Key("global_always_hit")] = 1; c.PlayerStats[Key("base_chance_to_ignite_%")] = 100;
            c.PlayerStats.Remove(Key("never_ignite"));
            foreach (var id in c.Monsters.Keys.Where(id => id != 42).ToArray()) c.Monsters.Remove(id);
            c.FlaskGrounds.SetBlinded(42, false, () => 0); target.RampageBlindUntil = 0;
            target.Walk.Teleport(130, 100, 0);
            var session = new Game.ProjectileSession("Whisperer", h.Wire, h.Ciphers, c); c.ProjectileRuntime = session;
            var gem = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == effectId);
            ProjectileUse Capture() => (ProjectileUse)typeof(Game).GetMethod("CaptureGemProjectile", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [c, session, gem.Id, gem, (180, 100), new SkillCriticalRoll(0), (ushort)0, 0u])!;
            try
            {
                var use = Capture(); var shot = Assert.IsType<ProjectileOffense>(use.Payload);
                Assert.Equal(effectId == "Fireball", shot.ClientPredicted);
                Assert.Equal((100, 50, 4d), shot.Blind); Assert.Equal(100, shot.Ignite.Chance); Assert.True(shot.Ignite.Min > 0);
                Assert.Null(await Game.ChangePassives("Whisperer", h.Wire, h.Ciphers, c, [5888], false, "release source refund", refundPoints: true));
                c.PlayerStats[Key("never_ignite")] = 1;
                var later = Assert.IsType<ProjectileOffense>(Capture().Payload);
                Assert.Equal(0, later.Blind.Chance); Assert.Equal(MonsterIgnites.Settings.None, later.Ignite);
                session.Schedule(session.Runtime.Now, () => true, () => use with { Profile = use.Profile with { Speed = 100 } });
                session.AdvanceTo(ProjectileRuntime.Time(1));
                Assert.Equal(30, Game.ResourceEffectsOf(c).TargetBlind(42, target).Read(1).Less);
                Assert.True(target.Ignites.Burning);
            }
            finally { session.Invalidate(); await session.OutputCompleted; c.ProjectileRuntimeState = h.Time; }
        }
        h.Build(A); h.Time.At = 10; state = Game.ResourceEffectsOf(c);
        foreach (var item in h.Items.Items(PlayerInventories.Offhand1).ToArray()) h.Items.Remove(PlayerInventories.Offhand1, item.Id);
        h.Items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        c.Pools = c.Pools with { Accuracy = 1000, AccuracyBase = 997, AccuracyPct = 7 };
        Dictionary<string, int> mainLocal = new() { ["local_accuracy_rating"] = 12 };
        Dictionary<string, int> offLocal = new() { ["local_accuracy_rating"] = 82 };
        Dictionary<uint, int> own = new() { [Key("accuracy_rating")] = 53, [Key("accuracy_rating_+%")] = 17 };
        int OffAccuracy() => Game.OffHandPanelStats(c, Game.CharacterPlayerStats(c), mainLocal, offLocal, 0, own)!
            .Single(s => s.Stat == "off_hand_accuracy_rating").Value;
        int offBefore = OffAccuracy(), selectedBefore = Game.SupportedAccuracy(c, h.Fireball.Id).Rating;
        Assert.True(Game.InflictPlayerBlind(c, 4));
        Assert.Equal(offBefore * 4 / 5, OffAccuracy());
        Assert.Equal(selectedBefore * 4 / 5, Game.SupportedAccuracy(c, h.Fireball.Id).Rating);
        state.Mana.Hit(10, 100, 20); state.Mana.Spent(199, 10);
        var transfer = new PendingTransfer(1, 1, false, AtSpawn: true);
        Assert.True(c.TryBeginAreaChange(transfer, out _)); Assert.Equal(10, c.RampageTransferAt);
        await Game.Transfer("Whisperer", h.Wire, h.Ciphers, c, transfer, _ =>
        {
            // Simulate five seconds of Auth loading through production discard, without sleeping or advancing a Stopwatch.
            Game.DiscardResourceLoadingRecovery(c, 15); state.PlayerBlind.Read(15);
            return Task.FromResult(new ControlResponse { Ok = true, Ticket = 123, Area = 1, Instance = 1 });
        });
        var wrong = h.NewConnection("wrong", 123); Game.RestoreResourceEffects(wrong);
        Assert.Null(wrong.ResourceEffectsState);
        var arriving = h.NewConnection(c.Character!, 123); Game.RestoreResourceEffects(arriving);
        var carried = Game.ResourceEffectsOf(arriving);
        Assert.True(carried.Loading); Assert.Equal(20, carried.PlayerBlind.Read(0).Less);
        Assert.Equal(199, carried.Mana.SpentRemainder);
        Game.ActivateResourceEffects(arriving, 100); Assert.Equal(20, carried.PlayerBlind.Read(103).Less);
        Assert.Equal(5, carried.Mana.Take(101)); Assert.Equal(15, carried.Mana.Take(104));
        Assert.Equal(0, carried.PlayerBlind.Read(104).Less);
        Game.RestoreResourceEffects(arriving); Assert.Equal(0, carried.Mana.Take(104));
        var failed = h.NewConnection("failed"); var failedState = Game.ResourceEffectsOf(failed);
        failed.ProjectileRuntimeState = new ManualRuntime { At = 10 };
        failedState.Mana.Hit(10, 100, 20); failedState.Mana.Spent(199, 10);
        var refused = new PendingTransfer(1, 1, false);
        Assert.True(failed.TryBeginAreaChange(refused, out _));
        await Game.Transfer("Whisperer", h.Wire, h.Ciphers, failed, refused, _ =>
        {
            Game.DiscardResourceLoadingRecovery(failed, 11);
            return Task.FromResult(new ControlResponse { Ok = false, Error = "controlled refusal" });
        });
        Assert.False(failed.LeavingArea); Assert.Null(failedState.TransferCheckpoint);
        Assert.Equal(0, failedState.Mana.Take(11)); Assert.Equal(199, failedState.Mana.SpentRemainder);
        Assert.Equal(5, failedState.Mana.Take(12));

        var live = h.NewConnection("death"); var effects = Game.ResourceEffectsOf(live);
        var world = live.RampageWorld;
        Assert.Same(effects, live.ResourceEffectsState); Assert.Same(world, live.OwnedCombatState);
        live.RetireResourceEffects(); Assert.Same(world, live.OwnedCombatState);
        effects = Game.ResourceEffectsOf(live); Assert.NotNull(live.ResourceEffectsState);
        effects.Mana.Hit(0, 1000, 100); effects.Mana.Spent(199, 10);
        effects.PlayerBlind.Inflict(0, 4, 0, false);
        live.PlayerLife = 1; live.PlayerMana = 0; live.EnergyShield.SetMaximum(0, false);
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        live.ElementalAilments.Apply(fire, fire, true, live.PlayerMaxLife, 0, _ => 0, new FixedDraw(0));
        live.ProjectileRuntimeState = new ManualRuntime();
        // Present an owned buff before death, then hold publication while the lethal frame retires its owner.
        Game.BlindPresentation(live); ushort dyingBuff = effects.PlayerBlind.Buff;
        await gate.WaitAsync();
        double carry = .5;
        Assert.False(Game.CompleteResourceRecoveryLocked(live, 0, 1, Game.CharacterPlayerStats(live), 100, 0, live.Pools.Mana, ref carry));
        Assert.Equal(0u, live.PlayerLife); Assert.Equal(0u, live.PlayerMana); Assert.Equal(0, carry);
        Assert.Null(live.ResourceEffectsState); Assert.Null(live.OwnedCombatState); Assert.Equal(0, effects.Mana.SpentRemainder);
        Assert.Equal(0, effects.PlayerBlind.Read(0).Less);
        var deathOutput = Game.SendBlind("Whisperer", h.Wire, h.Ciphers, live);
        Assert.False(deathOutput.IsCompleted); gate.Release(); await deathOutput;
        Has(h.Wire.Drain(h.Receive), InstanceEntities.RemoveBuff(Program.PlayerId, dyingBuff));

        var ordinary = h.NewConnection("ordinary", optional: false); ordinary.MainAscendancy = new();
        var normalState = Game.ResourceEffectsOf(ordinary); normalState.Mana.Spent(199, 10);
        h.Provider.Stop(ex => throw ex);
        Assert.False(ordinary.Closed); Assert.Same(normalState, ordinary.ResourceEffectsState);
        Assert.Equal(199, normalState.Mana.SpentRemainder); Assert.True(c.Closed);
        // Actual admission/replacement dispatch starts the same two production loops with manual scheduling.
        ordinary.PlayerStats[Key(ManaRecoveryRules.RagePer200)] = 10;
        ordinary.PlayerStats[Key("base_life_regeneration_rate_per_minute")] = 600;
        ordinary.PlayerStats[Key("base_mana_regeneration_rate_per_minute")] = 600;
        ordinary.PlayerSent = true; ordinary.Wire = ("Whisperer", h.Wire, h.Ciphers);
        ordinary.Pools = ordinary.Pools with { EnergyShield = 300 };
        ordinary.EnergyShield.SetMaximum(300, false); ordinary.PlayerLife = 500; ordinary.PlayerMana = 0;
        var runtime = new ManualRuntime(); ordinary.ProjectileRuntimeState = runtime;
        var oldSchedule = new ManualUpdates(runtime); Game.PlayerUpdatesSchedule = oldSchedule;
        var oldLogic = Program.Logic;
        Program.ActivatePlayerUpdates(ordinary);
        var oldUpdates = Assert.IsType<PlayerUpdatesState>(ordinary.PlayerUpdatesState);
        oldSchedule.Completion = oldUpdates.Completion;
        var oldFast = await oldSchedule.Next(Game.RecoveryStepMs);
        await oldSchedule.Next(Game.PlayerTickMs);
        await oldSchedule.OtherRoles();
        Program.ActivatePlayerUpdates(ordinary);
        Assert.Same(oldUpdates, ordinary.PlayerUpdatesState); Assert.Equal(1, oldSchedule.FastWaits);
        // The old writer commits while current, but its output remains queued across replacement.
        h.Wire.Drain(h.Receive); await gate.WaitAsync(); oldFast.Advance(1);
        await oldFast.Observed.Task;
        uint reloadLife, reloadMana, reloadEs;
        lock (ordinary.Monsters)
        {
            reloadLife = ordinary.PlayerLife; reloadMana = ordinary.PlayerMana; reloadEs = ordinary.EnergyShield.Current;
            Assert.True(reloadLife > 500); Assert.True(reloadMana > 0); Assert.True(reloadEs > 0);
        }
        h.Rules.Stop(ex => throw ex); Assert.Null(ordinary.ResourceEffectsState);
        Assert.False(Game.ResourceEffectsCurrent(ordinary, normalState));
        var replacementRules = new ModuleLifetime("Game"); var replacementSource = new ModuleLifetime("PhreciaAscendancies");
        var nextSchedule = new ManualUpdates(runtime) { At = 1 }; Game.PlayerUpdatesSchedule = nextSchedule;
        Game.CombatLifetime = replacementRules;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(replacementRules));
        var nextUpdates = Assert.IsType<PlayerUpdatesState>(ordinary.PlayerUpdatesState);
        nextSchedule.Completion = nextUpdates.Completion;
        var nextFast = await nextSchedule.Next(Game.RecoveryStepMs);
        var nextSlow = await nextSchedule.Next(Game.PlayerTickMs);
        oldLogic.ResumePlayerUpdates(ordinary); Program.ActivatePlayerUpdates(ordinary);
        Assert.Same(nextUpdates, ordinary.PlayerUpdatesState); Assert.Equal(1, nextSchedule.FastWaits);
        gate.Release(); await oldUpdates.Completion;
        await nextSchedule.OtherRoles();
        Assert.Empty(h.Wire.Drain(h.Receive)); Assert.False(ordinary.Closed);
        Assert.Equal(reloadLife, ordinary.PlayerLife); Assert.Equal(reloadMana, ordinary.PlayerMana);
        Assert.Equal(reloadEs, ordinary.EnergyShield.Current);
        ordinary.PlayerUpdatesWorker.Complete(h.Rules, null, oldUpdates, oldUpdates.Generation);
        Assert.True(Game.PlayerUpdatesCurrent(ordinary, nextUpdates));
        Assert.False(ordinary.PlayerUpdatesWorker.TryStart(ordinary, replacementRules, null, nextUpdates, out _));
        using (var replacement = AscendancyPolicies.Register(replacementSource, h.Policy))
        {
            var currentState = Game.ResourceEffectsOf(ordinary); currentState.Mana.Spent(7, 10);
            ordinary.ResourceEffectsLease.Complete(h.Rules, null, normalState, normalState.Generation);
            h.Rules.Stop(ex => throw ex);
            Assert.True(Game.ResourceEffectsCurrent(ordinary, currentState)); Assert.Equal(7, currentState.Mana.SpentRemainder);
            var ordinaryItems = Game.ItemsOf(ordinary);
            foreach (var old in ordinaryItems.Items(PlayerInventories.Flask).Where(i => i.X == 0).ToArray()) ordinaryItems.Remove(PlayerInventories.Flask, old.Id);
            ordinaryItems.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(Program.Items.Find("Small Mana Flask")!) with { Charges = 24 });
            await Game.UseFlask("Whisperer", h.Wire, h.Ciphers, ordinary, 0);
            var accepted = Assert.Single(ordinary.FlaskEffects.End(_ => true));
            accepted = accepted with { Started = 1, Ends = 1 + accepted.Ends - accepted.Started };
            Game.StartRecoveryEffectsLocked(ordinary, [accepted], copy: false);
            currentState.Mana.Hit(1, 1000, 20);
            double lifeRate = LifeRate(ordinary), manaRate = ManaRate(ordinary);
            var (normalCommands, normalReplies) = h.CommandsFor(ordinary);
            Assert.True(await normalCommands.RunAsync("/blind 4")); Assert.Equal("Blind applied.", Assert.Single(normalReplies));
            ushort activeBlind = currentState.PlayerBlind.Buff;
            Has(h.Wire.Drain(h.Receive), BlindStats(Program.PlayerId, 20));
            nextFast.Advance(2); nextFast = await nextSchedule.Next(Game.RecoveryStepMs);
            Assert.Equal(reloadLife + (uint)lifeRate, ordinary.PlayerLife);
            Assert.Equal(reloadMana + (uint)(manaRate + accepted.ManaPerSecond + 50), ordinary.PlayerMana);
            Assert.True(ordinary.EnergyShield.Current > reloadEs); // The actual replacement writer recharges ES too.
            nextSlow.Advance(2); nextSlow = await nextSchedule.Next(Game.PlayerTickMs);
            ordinary.PlayerLife = 500; ordinary.PlayerMana = 23; ordinary.EnergyShield.SetMaximum(100, true);
            ordinary.PlayerStats[Key(IncomingResourceDefences.TakenStat)] = -20;
            ordinary.PlayerStats[Key(IncomingResourceDefences.BypassStat)] = 50;
            ordinary.TrapDegenPerSecond = 100;
            nextFast.Advance(3); nextFast = await nextSchedule.Next(Game.RecoveryStepMs);
            Assert.Equal(500u + (uint)lifeRate - 40, ordinary.PlayerLife); // Focus80, half bypasses ES.
            Assert.Equal(60u, ordinary.EnergyShield.Current);
            ordinary.PlayerStats.Remove(Key(IncomingResourceDefences.TakenStat));
            ordinary.PlayerStats.Remove(Key(IncomingResourceDefences.BypassStat));
            ordinary.TrapDegenPerSecond = 0;
            h.Wire.Drain(h.Receive);
            nextSlow.Advance(6); await nextSchedule.Next(Game.PlayerTickMs);
            var expired = h.Wire.Drain(h.Receive);
            Has(expired, InstanceEntities.RemoveBuff(Program.PlayerId, activeBlind)); Has(expired, BlindStats(Program.PlayerId, 0));
            Assert.True(Game.PlayerUpdatesCurrent(ordinary, nextUpdates));
            // Current main's lethal trap degeneration must not admit Mana recovery after death on the resumed writer.
            ordinary.PlayerLife = 30; ordinary.PlayerMana = 23; ordinary.EnergyShield.SetMaximum(10, true);
            ordinary.TrapDegenPerSecond = 100;
            nextFast.Advance(7); nextFast = await nextSchedule.Next(Game.RecoveryStepMs);
            Assert.Equal(0u, ordinary.PlayerLife); Assert.Equal(23u, ordinary.PlayerMana);
            Assert.Equal(0u, ordinary.EnergyShield.Current); Assert.Null(ordinary.ResourceEffectsState);
            Assert.Equal(0, currentState.Mana.SpentRemainder);
            Has(h.Wire.Drain(h.Receive), InstanceProtocol.LifeUpdate(Program.PlayerId, 0));
            var reentered = h.NewConnection("replacement"); var nextState = Game.ResourceEffectsOf(reentered);
            Assert.Same(replacementSource, nextState.Source);
            Assert.False(Game.ResourceEffectsCurrent(reentered, effects));
            reentered.MainAscendancy = new(definition.Identity, B, AscendancyRules.AwardKeys);
            reentered.Pools = Game.PoolsFor(reentered, 68);
            reentered.PlayerLife = reentered.PlayerMaxLife - 100; reentered.PlayerMana = reentered.Pools.Mana;
            var nextItems = Game.ItemsOf(reentered);
            foreach (var old in nextItems.Items(PlayerInventories.Flask).Where(i => i.X == 0).ToArray()) nextItems.Remove(PlayerInventories.Flask, old.Id);
            nextItems.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(Program.Items.Find("Small Mana Flask")!) with { Charges = 24 });
            await reentered.FlaskBarGate.WaitAsync();
            var staleUse = Game.UseFlask("Whisperer", h.Wire, h.Ciphers, reentered, 0);
            Assert.False(staleUse.IsCompleted);
            replacementSource.Stop(ex => throw ex); Assert.Null(reentered.ResourceEffectsState);
            reentered.FlaskBarGate.Release(); await staleUse;
            Assert.Equal(24, nextItems.Items(PlayerInventories.Flask).Single(i => i.X == 0).Item.Charges);
            Assert.Empty(reentered.FlaskEffects.End(_ => true)); Assert.Empty(FlaskSignals(reentered));
        }
        replacementRules.Stop(ex => throw ex); Assert.Null(ordinary.ResourceEffectsState);
        await nextUpdates.Completion; Assert.Null(ordinary.PlayerUpdatesState);
        ordinary.Closed = true;
        await ChargePublicationAcrossCurrentPublishers(h);
        await AcceptedLevelAndChargeClockAcrossGameAssemblies(h);
        await AcceptedFootstepAndQuestAcrossGameAssemblies(h);
        await MinionAcceptedWorkAcrossGameAssemblies(h);
        await AcrossGameAssemblies(h);
        await UtilityAcrossGameAssemblies(h);
        await RecoveryAcrossGameAssemblies(h);
        await CommandsAcrossGameAssemblies(h);
        await TriggerChainAcrossGameAssemblies(h);
        Assert.Equal(definition.Identity, CharacterSaves.Load(c.Character!)!.MainAscendancy!.Selection);
    }

    static async Task ChargePublicationAcrossCurrentPublishers(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var priorLogic = Program.Logic; var priorRules = Game.CombatLifetime; string? priorOptIn = Environment.GetEnvironmentVariable("Game_VERIFIED_POLYTHEIST");
        string? priorDrops = Environment.GetEnvironmentVariable("Game_DROPS"); Environment.SetEnvironmentVariable("Game_VERIFIED_POLYTHEIST", "1"); Environment.SetEnvironmentVariable("Game_DROPS", "0");
        var provider = new ModuleLifetime("PhreciaAscendancies"); using var source = AscendancyPolicies.Register(provider, h.Policy); var failures = new List<string>();
        try
        {
            foreach (bool converted in new[] { false, true })
            {
                using var wire = new CaptureStream(); var ciphers = new ConnectionCiphers(new byte[64], false); var owner = new ModuleLifetime("Game");
                Game.CombatLifetime = owner; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(owner));
                var definition = new AscendancyCatalog().Definitions.Single(d => d.Name == "Polytheist");
                byte row = (byte)Enumerable.Range(0, 7).First(r => Program.Skills.ClassFor(r).IntegerId == definition.BaseClass);
                var c = h.NewConnection("chargeorder" + Guid.NewGuid().ToString("N"), optional: false, classRow: row);
                c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string>(["Game", "PhreciaAscendancies", "QuestObjects"]));
                c.MainAscendancy = new(definition.Identity, [27664, 22056, 26231, 32742, 21979, 61900, 59443], AscendancyRules.AwardKeys); c.Passives = [];
                AscendancyPolicies.SelectionChanged(c); AscendancyPolicies.RequirePlayable(c, definition.Identity); Game.AscendancyRules.Validate(definition.BaseClass, c.MainAscendancy);
                c.Wire = ("Whisperer", wire, ciphers); c.Walk.Teleport(100, 100, 0); c.AiEnabled = false;
                var projectiles = new Game.ProjectileSession("Whisperer", wire, ciphers, c); c.ProjectileRuntime = projectiles; projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(100));
                c.PlayerStats[Key("add_power_charge_on_kill_%_chance")] = 100;
                c.PlayerStats[Key(Charges.ConversionFlag(ChargeType.Power))] = converted ? 1 : 0;
                c.Pools = Game.PoolsFor(c, 68); c.PlayerLife = c.PlayerMaxLife; c.PlayerMana = c.Pools.Mana; c.PlayerSent = true;
                var grant = Assert.Single(Game.FootstepSkills(c, 100)); Assert.Equal("RepeatingShockwave", grant.EffectId);
                var variety = Game.Monsters.All.First(v => v.Supported && v.Id.Contains("Zombies/Zombie") && Game.Monsters.BaseExperience(v, 68) > 0);
                var victim = new SpawnedMonster("charge ordering victim", new(104, 100, 0), 0, 0, 1) { Variety = variety, Level = 68 }; c.Monsters[42] = victim;
                var loadedType = typeof(ModHost).GetNestedType("LoadedMod", flags)!;
                var loaded = Activator.CreateInstance(loadedType, flags, null, [ModHost.ReadManifest("charge-order-r14", "{}"), "logs", null], null)!;
                var context = (IModuleContext)Activator.CreateInstance(typeof(ModHost).GetNestedType("Context", flags)!, flags, null, [Program.ServerMods, loaded], null)!;
                var heldHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Inline release drives the real hook continuation to the already-held cipher gate before that write resumes.
                var releaseHook = new TaskCompletionSource(); int notifications = 0, kills = 0;
                using var notify = context.OnMonsterKilled(k => { if (k.Player.Character != c.Character) return Task.CompletedTask; notifications++; heldHook.TrySetResult(); return releaseHook.Task; });
                using var xp = context.ModifyExperience(e => { if (e.Player.Character == c.Character) e.Experience = 37; });
                using var kill = GameEvents.Kill.Add("Game", k => { if (ReferenceEquals(k.Conn, c)) { kills++; Assert.True(k.PlayerKill); } });
                Task? accepted = null, ticker = null; bool heldProjectionGate = false;
                var projectionGate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
                var schedule = new ManualUpdates(new ManualRuntime { At = 100 }) { At = 100 }; var updates = new PlayerUpdatesState(owner, schedule);
                try
                {
                    uint startXp = c.Experience; accepted = Game.TriggerFootstepSkill("Whisperer", wire, ciphers, c, grant);
                    await heldHook.Task; Assert.False(accepted.IsCompleted); Assert.True(victim.Dead); Assert.Equal(1, c.PlayerChargeState.Held(ChargeType.Power));
                    var session = Game.ChargesOf(c);
                    foreach (var type in Charges.Types) session.Enqueue(new ChargeEvent.Grant(type, 1, "production queue ordering control"));
                    c.PlayerUpdatesState = updates;
                    Assert.True(c.PlayerUpdatesWorker.TryStart(c, owner, null, updates, out long generation)); updates.Generation = generation;
                    ticker = Game.RunCharges("Whisperer", wire, ciphers, c, updates); schedule.Completion = ticker;
                    var first = await schedule.Next(PlayerUpdateRole.Charges);
                    while (wire.Plain.Reader.TryRead(out _)) { }
                    wire.Hold = () => true; first.Advance(100); await wire.Held.Task; Assert.False(ticker.IsCompleted);
                    var shown = c.PlayerChargeState.Presented.ToArray(); Assert.Equal(new[] { 1, 1, 2 }, shown.Select(v => v.Count).ToArray()); Assert.Equal(converted, shown[2].Converted);
                    projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(111.25)); releaseHook.TrySetResult(); Assert.False(accepted.IsCompleted);
                    wire.Release.TrySetResult(); await accepted; var following = await schedule.Next(PlayerUpdateRole.Charges);
                    var frames = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var packet)) frames.Add(packet);
                    projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(111.30)); following.Advance(111.30); await schedule.Next(PlayerUpdateRole.Charges);
                    var nextFrames = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var packet)) nextFrames.Add(packet);
                    var bytes = frames.SelectMany(p => p).ToArray();
                    foreach (var type in Charges.Types)
                    {
                        var old = shown[(int)type]; var add = ChargeBuffs.Add(type, old.Buff, old.Count, 10, Program.PlayerId, old.Converted);
                        var remove = InstanceEntities.RemoveBuff(Program.PlayerId, old.Buff);
                        int addAt = bytes.AsSpan().LastIndexOf(add), removeAt = bytes.AsSpan().LastIndexOf(remove);
                        if (addAt < 0 || removeAt <= addAt) failures.Add($"expired {type} add overtook removal (converted={converted}, add={addAt}, remove={removeAt})");
                        Assert.Equal(0, c.PlayerChargeState.Held(type)); Assert.Equal(0, c.PlayerChargeState.Presented[(int)type].Count);
                        Console.WriteLine($"[r14] converted={converted} {type}: last add={addAt}, removal={removeAt}, held/shown=0/0");
                    }
                    Assert.DoesNotContain(nextFrames, p => p.Length > 1 && p[0] == 1 && (p[1] == 0xc1 || p[1] == 0xc2));
                    Assert.Empty(session.Presentation(111.30, () => c.NextBuff++));
                    Assert.Equal(startXp + 37, c.Experience); Assert.Equal(1, notifications); Assert.Equal(1, kills); Assert.False(c.Closed);
                    Console.WriteLine("[r14] buff frames: " + string.Join(",", frames.Where(p => p.Length > 1 && p[0] == 1 && (p[1] == 0xc1 || p[1] == 0xc2)).Select(Convert.ToHexString)));
                    // The projectile consumer uses this real outbox wrapper: no snapshot/ack before its send gate.
                    projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(120));
                    session.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "projectile output ordering control"));
                    var current = Game.CharacterPlayerStats(c);
                    int Get(string name) => Program.Stats.Key(name) is uint key ? current.GetValueOrDefault(key) : 0;
                    session.Step(120, Get, () => Game.WeaponProfileFor(c), true, false, false, new FixedDraw(0));
                    await projectionGate.WaitAsync(); heldProjectionGate = true;
                    projectiles.PresentCharges(); Assert.Equal(0, c.PlayerChargeState.Presented[(int)ChargeType.Frenzy].Count);
                    projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(131.25));
                    var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    projectiles.Enqueue(() => { drained.TrySetResult(); return Task.CompletedTask; });
                    projectionGate.Release(); heldProjectionGate = false; await drained.Task;
                    Assert.Equal(0, c.PlayerChargeState.Held(ChargeType.Frenzy)); Assert.Equal(0, c.PlayerChargeState.Presented[(int)ChargeType.Frenzy].Count);
                    while (wire.Plain.Reader.TryRead(out var late)) Assert.False(late.Length > 1 && late[0] == 1 && late[1] == 0xc1);
                    // A retired projectile job waiting at the same gate neither sends nor acknowledges the gain.
                    projectiles.Runtime.AdvanceTo(ProjectileRuntime.Time(140));
                    session.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 1, "retired projectile output control"));
                    session.Step(140, Get, () => Game.WeaponProfileFor(c), true, false, false, new FixedDraw(0));
                    await projectionGate.WaitAsync(); heldProjectionGate = true; projectiles.PresentCharges();
                    owner.Stop(_ => { }); projectionGate.Release(); heldProjectionGate = false; await projectiles.OutputCompleted;
                    Assert.Equal(0, c.PlayerChargeState.Presented[(int)ChargeType.Frenzy].Count);
                    while (wire.Plain.Reader.TryRead(out var late)) Assert.False(late.Length > 1 && late[0] == 1 && late[1] == 0xc1);
                    Console.WriteLine($"[r14] converted={converted}: delayed projectile output expired without add; retired output did not acknowledge/send");
                }
                finally
                {
                    if (heldProjectionGate) projectionGate.Release();
                    wire.Release.TrySetResult(); releaseHook.TrySetResult(); if (accepted is not null) await accepted;
                    c.RetirePlayerUpdates();
                    if (ticker is not null) try { await ticker; } catch (OperationCanceledException) when (updates.Stop.IsCancellationRequested) { }
                    c.Closed = true; owner.Stop(_ => { }); await projectiles.OutputCompleted;
                    ((ModuleLifetime)context.Lifetime).Stop(_ => { });
                }
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
        finally
        {
            Game.CombatLifetime = priorRules; Program.SetGameLogic(priorLogic); provider.Stop(_ => { });
            Environment.SetEnvironmentVariable("Game_VERIFIED_POLYTHEIST", priorOptIn); Environment.SetEnvironmentVariable("Game_DROPS", priorDrops);
        }
    }

    static async Task AcceptedLevelAndChargeClockAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var priorLogic = Program.Logic; var priorRules = Game.CombatLifetime; var priorSchedule = Game.PlayerUpdatesSchedule;
        string[] optIns = ["Game_VERIFIED_POLYTHEIST", "Game_DROPS"]; var priorFlags = optIns.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (string flag in optIns) Environment.SetEnvironmentVariable(flag, "1");
        var provider = new ModuleLifetime("PhreciaAscendancies"); using var source = AscendancyPolicies.Register(provider, h.Policy);
        var failures = new List<string>();
        try
        {
            foreach (var scenario in new (bool Reload, double Delay, bool Another)[] { (false, 1.25, false), (true, 1.25, false),
                (false, 11.25, false), (true, 11.25, false), (false, 1.25, true) })
            {
                using var wire = new CaptureStream(); var ciphers = new ConnectionCiphers(new byte[64], false);
                var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
                var alc = new AssemblyLoadContext("Accepted level/charge " + scenario, isCollectible: true);
                alc.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
                var assembly = alc.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, assembly);
                var game = typeof(Game);
                object? Call(string name, params object?[] args)
                {
                    var method = game.GetMethods(flags).Single(m => m.Name == name && m.GetParameters().Length >= args.Length
                        && m.GetParameters().Skip(args.Length).All(p => p.HasDefaultValue));
                    var values = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                    args.CopyTo(values, 0); return method.Invoke(null, values);
                }
                var runtime = new ManualRuntime { At = 99.5 }; var before = new ManualUpdates(runtime) { At = 99.5 }; var after = new ManualUpdates(runtime) { At = 99.5 };
                Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
                var definition = new AscendancyCatalog().Definitions.Single(d => d.Name == "Polytheist");
                byte row = (byte)Enumerable.Range(0, 7).First(r => Program.Skills.ClassFor(r).IntegerId == definition.BaseClass);
                var c = h.NewConnection("levelclock" + Guid.NewGuid().ToString("N"), optional: false, classRow: row);
                c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string>(["Game", "PhreciaAscendancies", "QuestObjects"]));
                c.MainAscendancy = new(definition.Identity, [27664, 22056, 26231, 32742, 21979, 61900, 59443], AscendancyRules.AwardKeys);
                c.Passives = []; c.RefundPoints = 20;
                CharacterSaves.SetMainAscendancy(c.Character!, c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 20);
                c.SavedPassiveProgress = new(c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 20);
                c.Experience = CharacterStats.ExperienceFor(69) - 1; CharacterSaves.SetExperience(c.Character!, c.Experience);
                AscendancyPolicies.SelectionChanged(c); AscendancyPolicies.RequirePlayable(c, definition.Identity); Game.AscendancyRules.Validate(definition.BaseClass, c.MainAscendancy);
                c.ProjectileRuntimeState = runtime; c.Wire = ("Whisperer", wire, ciphers); c.Walk.Teleport(100, 100, 0); c.AiEnabled = false;
                c.Pools = Game.PoolsFor(c, 68); c.PlayerLife = c.PlayerMaxLife; c.PlayerMana = c.Pools.Mana;
                c.PlayerStats[Key("virtual_strength_for_requirements")] = 1000;
                c.PlayerStats[Key("gain_X_endurance_charges_per_second")] = 1;
                c.PlayerStats[Key("gain_X_power_charges_per_second")] = scenario.Another ? 0 : -1;
                bool movingControl = !scenario.Reload && scenario.Delay < 10 && !scenario.Another;
                c.PlayerStats[Key("gain_a_frenzy_charge_every_X_seconds_while_moving")] = movingControl ? 2 : 0;
                c.PlayerStats[Key("endurance_charge_on_kill_%")] = 100;
                c.PlayerStats[Key("add_frenzy_charge_on_skill_hit_%")] = 100;
                c.PlayerStats[Key("add_power_charge_on_kill_%_chance")] = 100;
                var items = Game.ItemsOf(c); var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalGroundSlam")!);
                var weapon = items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with { Sockets = [new(SocketColour.White, gem)], Links = [1] });
                var variety = Game.Monsters.All.First(v => v.Supported && v.Id.Contains("Zombies/Zombie") && Game.Monsters.BaseExperience(v, 68) > 0);
                var victim = new SpawnedMonster("accepted level victim", new(104, 100, 0), 0, 0, 1) { Variety = variety, Level = 68 }; c.Monsters[42] = victim;
                var loadedType = typeof(ModHost).GetNestedType("LoadedMod", flags)!;
                var loaded = Activator.CreateInstance(loadedType, flags, null, [ModHost.ReadManifest("level-clock-r13", "{}"), "logs", null], null)!;
                var context = (IModuleContext)Activator.CreateInstance(typeof(ModHost).GetNestedType("Context", flags)!, flags, null, [Program.ServerMods, loaded], null)!;
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int kills = 0, notifications = 0, xpHooks = 0, drops = 0;
                using var kill = GameEvents.Kill.Add("Game", k => { if (ReferenceEquals(k.Conn, c)) { kills++; Assert.True(k.PlayerKill); } });
                using var notify = context.OnMonsterKilled(k => { if (k.Player.Character != c.Character) return Task.CompletedTask; notifications++; held.TrySetResult(); return notifications == 1 ? release.Task : Task.CompletedTask; });
                using var xp = context.ModifyExperience(e => { if (e.Player.Character == c.Character) { xpHooks++; e.Experience = 37; } });
                using var drop = context.ModifyDrops(d => { if (d.Player.Character == c.Character) { drops++; d.Cancel = true; d.Items.Add("Portal Scroll"); } });
                var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1)); bool gateHeld = false;
                PlayerUpdatesState? oldState = null; Task? completion = null, pending = null, later = null;
                var projectiles = new Game.ProjectileSession("Whisperer", wire, ciphers, c); c.ProjectileRuntime = projectiles;
                ProjectileRuntime clock = projectiles.Runtime; clock.AdvanceTo(ProjectileRuntime.Time(99.5)); Task? replacementOutput = null;
                try
                {
                    var grant = Assert.Single(Game.FootstepSkills(c, 100)); Assert.Equal("RepeatingShockwave", grant.EffectId);
                    c.PlayerSent = true; Program.ActivatePlayerUpdates(c); oldState = (PlayerUpdatesState)c.PlayerUpdatesState!; before.Completion = oldState.Completion;
                    await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
                    _ = Game.CharacterPlayerStats(c); var initialProfile = Game.WeaponProfileFor(c);
                    Game.ChargesOf(c).Step(99.5, null, () => initialProfile, true, true, false, new FixedDraw(0));
                    runtime.At = before.At = after.At = 100; clock.AdvanceTo(ProjectileRuntime.Time(100));
                    uint start = c.Experience; pending = Game.TriggerFootstepSkill("Whisperer", wire, ciphers, c, grant);
                    await held.Task; Assert.False(pending.IsCompleted); Assert.Equal(0u, victim.Life); Assert.Equal(69, c.Pools.Level);
                    // Account/Auth is outside this isolated packet/timer test; gameplay and all native projections stay real.
                    c.Minions.PresentedAccountRevision = c.Minions.AccountRevision;
                    double acceptedAt = c.Minions.OutputQueue.Single(o => o.Kind == PlayerMinions.OutputKind.Charges).Charges!.Max(e => e.At);
                    if (scenario.Reload)
                    {
                        oldOwner.Stop(ex => throw ex); game = assembly.GetType("Game")!;
                        game.GetField("CombatLifetime", flags)!.SetValue(null, owner); game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
                        var replacementProjectiles = Activator.CreateInstance(game.GetNestedType("ProjectileSession", flags)!, flags, null, ["Whisperer", wire, ciphers, c], null)!;
                        clock = (ProjectileRuntime)replacementProjectiles.GetType().GetProperty("Runtime")!.GetValue(replacementProjectiles)!;
                        clock.AdvanceTo(ProjectileRuntime.Time(acceptedAt)); c.ProjectileRuntimeState = (IProjectileRuntime)replacementProjectiles;
                        replacementOutput = (Task)replacementProjectiles.GetType().GetProperty("OutputCompleted")!.GetValue(replacementProjectiles)!;
                        Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                        var state = c.PlayerUpdatesState!; completion = (Task)state.GetType().GetField("Completion", flags)!.GetValue(state)!; after.Completion = completion;
                        await after.Next(PlayerUpdateRole.Recovery); await after.Next(PlayerUpdateRole.Player); await after.OtherRoles();
                    }
                    await (Task)Call("SendPassives", "Whisperer", wire, ciphers, c)!;
                    var beforePaid = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var p)) beforePaid.Add(p);
                    var stale = ((byte[] Bytes, string Label))Call("PassivesMessage", c)!; Assert.Contains(beforePaid, p => p.SequenceEqual(stale.Bytes));
                    await gate.WaitAsync(); gateHeld = true;
                    var refund = (Task<string?>)Call("ChangePassives", "Whisperer", wire, ciphers, c, new ushort[] { 59443 }, false, "accepted level paid refund", null, false, true)!;
                    Assert.False(refund.IsCompleted); Assert.Equal((ushort)15, c.RefundPoints); Assert.DoesNotContain((ushort)59443, c.MainAscendancy.Allocated);
                    gate.Release(); gateHeld = false; Assert.Null(await refund);
                    var fresh = ((byte[] Bytes, string Label))Call("PassivesMessage", c)!;
                    var refundFrames = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var p)) refundFrames.Add(p);
                    Assert.Contains(refundFrames, p => p.SequenceEqual(fresh.Bytes)); Assert.False(stale.Bytes.SequenceEqual(fresh.Bytes));
                    Assert.True(items.Items(PlayerInventories.Weapon1).Single(i => i.Id == weapon.Id).Item.Sockets[0].Gem!.GemExperience > 0);
                    await (Task)Call("PickUpItem", "Whisperer", wire, ciphers, c, new InstanceItems.PickUpItem(PlayerInventories.InventoryId(PlayerInventories.Weapon1), weapon.Id, 0, 0, 0))!;
                    Assert.Empty(items.Items(PlayerInventories.Weapon1)); Assert.Equal(weapon.Id, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
                    while (wire.Plain.Reader.TryRead(out _)) { }
                    var session = Call("ChargesOf", c)!; _ = Call("CharacterPlayerStats", c);
                    var step = session.GetType().GetMethod("Step")!; var profile = (WeaponProfile)Call("WeaponProfileFor", c)!;
                    void Live(double at)
                    {
                        runtime.At = before.At = after.At = at; clock.AdvanceTo(ProjectileRuntime.Time(at));
                        step.Invoke(session, [at, null, (Func<WeaponProfile>)(() => profile), true, true, false, new FixedDraw(0)]);
                    }
                    Live(acceptedAt); Live(acceptedAt + scenario.Delay);
                    int beforeDrain = c.PlayerChargeState.Held(ChargeType.Endurance); double deadline = c.PlayerChargeState.ExpiresAt(ChargeType.Endurance);
                    Live(acceptedAt + scenario.Delay); Assert.Equal(beforeDrain, c.PlayerChargeState.Held(ChargeType.Endurance));
                    double liveOrigin = (double)session.GetType().GetField("lastStep", flags)!.GetValue(session)!;
                    if (scenario.Another)
                    {
                        runtime.At = before.At = acceptedAt + 5; clock.AdvanceTo(ProjectileRuntime.Time(runtime.At));
                        c.Monsters[43] = new("later accepted victim", new(104, 100, 0), 0, 0, 1) { Variety = variety, Level = 68 };
                        wire.Hold = () => c.Monsters[43].Dead;
                        later = (Task)Call("TriggerFootstepSkill", "Whisperer", wire, ciphers, c, grant)!;
                        await wire.Held.Task; Assert.False(later.IsCompleted); Assert.Equal(0u, c.Monsters[43].Life);
                        wire.Release.TrySetResult(); await later;
                    }
                    release.TrySetResult(); await pending;
                    await (Task)Call("PlayerMinionsTick", "Whisperer", wire, ciphers, c, runtime.At)!;
                    int afterDrain = c.PlayerChargeState.Held(ChargeType.Endurance), powerAfterDrain = c.PlayerChargeState.Held(ChargeType.Power);
                    if ((double)session.GetType().GetField("lastStep", flags)!.GetValue(session)! != liveOrigin) failures.Add("output changed live integration origin: " + scenario);
                    double currentAt = runtime.At; Live(currentAt); int afterRepeat = c.PlayerChargeState.Held(ChargeType.Endurance);
                    if (!scenario.Another && c.PlayerChargeState.Held(ChargeType.Power) != powerAfterDrain) failures.Add("periodic loss replay: " + scenario);
                    if (afterRepeat != afterDrain) failures.Add("periodic clock replay: " + scenario);
                    if (!scenario.Another && c.PlayerChargeState.ExpiresAt(ChargeType.Endurance) != deadline) failures.Add("newer deadline overwritten: " + scenario);
                    if (scenario.Delay > 10 && (c.PlayerChargeState.Held(ChargeType.Power) != 0 || c.PlayerChargeState.Presented[(int)ChargeType.Power].Count != 0))
                        failures.Add("expired accepted charge resurrected/flashed: " + scenario);
                    if (scenario.Another && c.PlayerChargeState.ExpiresAt(ChargeType.Power) != acceptedAt + 15)
                        failures.Add("later accepted batch deadline lost");
                    int repeatedAtSameClock = afterRepeat;
                    if (movingControl)
                    {
                        int frenzy = c.PlayerChargeState.Held(ChargeType.Frenzy);
                        currentAt = acceptedAt + 1.5; Live(currentAt);
                        if (c.PlayerChargeState.Held(ChargeType.Frenzy) != frenzy + 1) failures.Add("partial moving timer reset by output");
                        afterRepeat = c.PlayerChargeState.Held(ChargeType.Endurance);
                        Console.WriteLine($"[r13] moving phase: Frenzy {frenzy}->{c.PlayerChargeState.Held(ChargeType.Frenzy)} at {currentAt:R}");
                    }
                    var frames = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var p)) frames.Add(p);
                    var passiveFrames = frames.Where(p => p.Length >= 2 && p[0] == 0 && p[1] == 0x33).ToArray();
                    if (passiveFrames.Length == 0 || passiveFrames.Any(p => !p.SequenceEqual(fresh.Bytes))) failures.Add("stale passive tree after paid refund: " + scenario);
                    uint inventoryId = PlayerInventories.InventoryId(PlayerInventories.Weapon1);
                    var rowFrames = frames.Where(p => p.Length >= 11 && p[0] == 0 && p[1] == 0xbf
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(3, 4)) == inventoryId).ToArray();
                    var emptyRow = InstanceItems.InventoryChange(inventoryId, 0, [weapon.Id], []);
                    if (rowFrames.Any(p => !p.AsSpan(11).SequenceEqual(emptyRow.AsSpan(11)))) failures.Add("queued gem XP replayed old equipment row: " + scenario);
                    int expected = scenario.Another ? 2 : 1;
                    Assert.Equal(start + (uint)(37 * expected), c.Experience); Assert.Equal(expected, kills); Assert.Equal(expected, notifications); Assert.Equal(expected, xpHooks); Assert.Equal(expected, drops);
                    Assert.Equal((ushort)15, c.RefundPoints); Assert.DoesNotContain((ushort)59443, c.MainAscendancy.Allocated); Assert.False(c.Closed);
                    Console.WriteLine($"[r13] {scenario}: level={c.Pools.Level} refunds={c.RefundPoints} nodes={c.MainAscendancy.Allocated.Count}; charges {beforeDrain}/{afterDrain}/{repeatedAtSameClock}, deadline {deadline:R}->{c.PlayerChargeState.ExpiresAt(ChargeType.Endurance):R}; passives=" + string.Join(",", passiveFrames.Select(Convert.ToHexString)));
                    Console.WriteLine($"[r13] old-tree={Convert.ToHexString(stale.Bytes)} fresh-tree={Convert.ToHexString(fresh.Bytes)} equipment-frames={rowFrames.Length}");
                    await Game.PublishMinionOutcomes("Whisperer", wire, ciphers, c); await (Task)Call("PublishMinionOutcomes", "Whisperer", wire, ciphers, c)!;
                    Live(currentAt); Assert.Equal(afterRepeat, c.PlayerChargeState.Held(ChargeType.Endurance)); Assert.Equal(expected, notifications);
                    Assert.Empty(c.Minions.OutputQueue); Assert.Empty(c.Minions.FlaskRewards);
                }
                finally
                {
                    if (gateHeld) gate.Release(); wire.Release.TrySetResult(); release.TrySetResult(); if (pending is not null) await pending; if (later is not null) await later;
                    c.Closed = true; c.RetirePlayerUpdates(); oldOwner.Stop(_ => { }); owner.Stop(_ => { });
                    if (oldState is not null) await oldState.Completion; if (completion is not null) await completion;
                    await projectiles.OutputCompleted; if (replacementOutput is not null) await replacementOutput;
                    ((ModuleLifetime)context.Lifetime).Stop(_ => { }); alc.Unload();
                }
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
        finally
        {
            Game.CombatLifetime = priorRules; Game.PlayerUpdatesSchedule = priorSchedule; Program.SetGameLogic(priorLogic); provider.Stop(_ => { });
            for (int i = 0; i < optIns.Length; i++) Environment.SetEnvironmentVariable(optIns[i], priorFlags[i]);
        }
    }

    // Actual qualified relic expiry and accepted fighting-minion lethal contact, across the real cipher gate.
    static async Task AcceptedFootstepAndQuestAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var priorLogic = Program.Logic; var priorRules = Game.CombatLifetime; var priorSchedule = Game.PlayerUpdatesSchedule;
        string[] optIns = ["Game_VERIFIED_POLYTHEIST", "Game_VERIFIED_ANCESTRAL_COMMANDER", "Game_DROPS"];
        var priorFlags = optIns.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (string flag in optIns) Environment.SetEnvironmentVariable(flag, "1");
        var provider = new ModuleLifetime("PhreciaAscendancies"); using var source = AscendancyPolicies.Register(provider, h.Policy);
        var failures = new List<string>();
        try
        {
            foreach (var scenario in new (bool Player, bool Reload, bool Earlier, bool Refund)[] { (true, false, false, false),
                (true, false, false, true), (false, false, false, false), (false, true, false, false),
                (false, false, true, false), (false, true, true, false), (true, true, false, false) })
            {
                using var wire = new CaptureStream(); var ciphers = new ConnectionCiphers(new byte[64], false);
                var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
                var alc = new AssemblyLoadContext("Accepted footstep/quest " + scenario, isCollectible: true);
                alc.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
                var assembly = alc.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, assembly);
                var game = typeof(Game);
                object? Call(string name, params object?[] args) => game.GetMethods(flags)
                    .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
                var runtime = new ManualRuntime(); var before = new ManualUpdates(runtime); var after = new ManualUpdates(runtime);
                Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
                var definition = new AscendancyCatalog().Definitions.Single(d => d.Name == (scenario.Player ? "Polytheist" : "Ancestral Commander"));
                byte classRow = (byte)Enumerable.Range(0, 7).First(row => Program.Skills.ClassFor(row).IntegerId == definition.BaseClass);
                var c = h.NewConnection("accepted" + Guid.NewGuid().ToString("N"), optional: false, classRow: classRow, area: "1_1_1");
                c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string>(["Game", "PhreciaAscendancies", "QuestObjects"]));
                c.MainAscendancy = new(definition.Identity, scenario.Player
                    ? [27664, 22056, 26231, 32742, 21979, 61900, 59443] : [19258, 47491, 17511], AscendancyRules.AwardKeys);
                c.Passives = []; c.Wire = ("Whisperer", wire, ciphers); c.AiEnabled = false;
                AscendancyPolicies.SelectionChanged(c); AscendancyPolicies.RequirePlayable(c, definition.Identity);
                Game.AscendancyRules.Validate(definition.BaseClass, c.MainAscendancy);
                c.ProjectileRuntimeState = runtime; c.Walk.Teleport(100, 100, 0);
                c.Pools = Game.PoolsFor(c, 68) with { Life = 1000, Mana = 1000, Evasion = 0, SpellCriticalStrikeChancePct = 100000 }; c.PlayerLife = c.PlayerMana = 1000;
                c.Minions.Random = new FixedDraw(0);
                c.PlayerStats[Key("add_frenzy_charge_on_skill_hit_%")] = 100;
                c.PlayerStats[Key("add_power_charge_on_kill_%_chance")] = 100;
                c.PlayerStats[Key("recharge_flasks_on_crit")] = 1;
                var inventory = Game.ItemsOf(c);
                foreach (var f in inventory.Items(PlayerInventories.Flask).ToArray()) inventory.Remove(PlayerInventories.Flask, f.Id);
                inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(Program.Items.Find("Small Mana Flask")!) with { Charges = 0 });
                var vaal = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalGroundSlam")!);
                inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
                    { Sockets = [new(SocketColour.White, vaal)], Links = [1] });
                c.PlayerStats[Key("virtual_strength_for_requirements")] = 1000;
                Game.RefreshVaalSouls(c); Assert.Equal(0u, Assert.Single(c.VaalSouls.Read()).Current);
                var variety = Game.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")!;
                var victim = new SpawnedMonster("accepted quest victim", new(104, 100, 0), 0, 0, 1) { Variety = variety, Level = 68 };
                c.Monsters[42] = victim;
                var exit = new AreaTransitionObject(110, 100, AreaTransitionCatalog.HashOf("1_1_town")!.Value);
                typeof(ClientState).GetProperty(nameof(ClientState.Layout))!.SetValue(c, c.Layout with { AreaTransitions = [(0x501u, exit)] });
                Assert.Equal("a1q1-KilledHillock", Game.TransitionFlag(c, exit));
                bool clearedExit = !scenario.Player && scenario.Reload && !scenario.Earlier;
                var loadedType = typeof(ModHost).GetNestedType("LoadedMod", flags)!;
                var loaded = Activator.CreateInstance(loadedType, flags, null, [ModHost.ReadManifest("accepted-r12", "{}"), "logs", null], null)!;
                var context = (IModuleContext)Activator.CreateInstance(typeof(ModHost).GetNestedType("Context", flags)!, flags, null, [Program.ServerMods, loaded], null)!;
                int kills = 0, playerKills = 0, xpHooks = 0, dropHooks = 0, excluded = 0;
                using var kill = GameEvents.Kill.Add("Game", k => { if (ReferenceEquals(k.Conn, c)) { kills++; Assert.Equal(scenario.Player, k.PlayerKill); } });
                using var absent = GameEvents.Kill.Add("not-in-this-league", k => { if (ReferenceEquals(k.Conn, c)) excluded++; });
                using var playerKill = context.OnMonsterKilled(k => { if (k.Player.Character == c.Character) playerKills++; return Task.CompletedTask; });
                using var xp = context.ModifyExperience(e => { if (e.Player.Character == c.Character) { xpHooks++; e.Experience = 37; } });
                using var drops = context.ModifyDrops(d => { if (d.Player.Character == c.Character) { dropHooks++; d.Cancel = true; d.Items.Add("Portal Scroll"); } });
                var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1)); bool held = false;
                Task? pending = null, normal = null; Task? completion = null; PlayerUpdatesState? oldState = null;
                try
                {
                    var grant = scenario.Player ? Assert.Single(Game.FootstepSkills(c, 0)) : default;
                    if (scenario.Player) Assert.Equal("RepeatingShockwave", grant.EffectId);
                    PlayerMinions.Attack? attack = null;
                    if (!scenario.Player)
                    {
                        var stats = Game.CharacterPlayerStats(c);
                        Assert.True(Game.SpiritChieftainsWhenHitLocked(c, stats, false, false, 1000));
                        await Game.SpiritChieftainMinion("Whisperer", wire, ciphers, c);
                        var minion = Assert.Single(c.Minions.All); minion.Monster.Walk.Teleport(103, 100, 0);
                        attack = new(minion.Id, minion.Monster, (103, 100), 42, victim, (104, 100), 0) { Started = true };
                        c.Minions.Attacks[minion.Id] = attack;
                    }
                    if (scenario.Refund)
                    {
                        c.RefundPoints = 200;
                        CharacterSaves.SetMainAscendancy(c.Character!, c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
                        c.SavedPassiveProgress = new(c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
                        Assert.Null(await Game.ChangePassives("Whisperer", wire, ciphers, c, [grant.Node], false, "footstep source refund", refundPoints: true));
                        Assert.Empty(Game.FootstepSkills(c, 0)); Assert.Equal((ushort)195, c.RefundPoints);
                    }
                    if (clearedExit) await Game.SetQuestFlags("Whisperer", wire, ciphers, c, ["a1q1-SeenHillock"]);
                    c.PlayerSent = true; Program.ActivatePlayerUpdates(c);
                    oldState = (PlayerUpdatesState)c.PlayerUpdatesState!; before.Completion = oldState.Completion;
                    await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
                    while (wire.Plain.Reader.TryRead(out _)) { }
                    uint startXp = c.Experience;
                    if (scenario.Earlier)
                    {
                        await gate.WaitAsync(); held = true;
                        normal = Game.SetQuestFlags("Whisperer", wire, ciphers, c, ["a1q1-SeenHillock"]);
                        Assert.False(normal.IsCompleted);
                    }
                    else if (!scenario.Refund) wire.Hold = () => victim.Dead;
                    if (scenario.Player) pending = Game.TriggerFootstepSkill("Whisperer", wire, ciphers, c, grant);
                    else
                    {
                        // Real accuracy/block rolls stay intact; only accepted contacts enter this race.
                        for (int attempt = 0; attempt < 32; attempt++)
                        {
                            c.Minions.Attacks[attack!.Id] = attack;
                            pending = Game.MinionSwing("Whisperer", wire, ciphers, c, attack);
                            if (!scenario.Earlier) await Task.WhenAny(pending, wire.Held.Task);
                            if (victim.Dead) break;
                            await pending!;
                        }
                        Assert.True(victim.Dead, "Bounded actual minion contact did not land");
                    }
                    if (scenario.Refund)
                    {
                        await pending!;
                        if (victim.Life != 1 || c.Experience != startXp || kills != 0) failures.Add("refunded footstep source accepted stale grant");
                        continue;
                    }
                    if (!scenario.Earlier) await wire.Held.Task;
                    Assert.False(pending!.IsCompleted); Assert.True(victim.Dead); Assert.Equal(0u, victim.Life);
                    byte[]? staleFlags = null;
                    if (!scenario.Player && !scenario.Earlier)
                        staleFlags = InstanceEntities.PlayerQuestFlagsUpdate(Program.PlayerId, Game.PlayerQuestFlags(c));
                    if (scenario.Reload)
                    {
                        oldOwner.Stop(ex => throw ex); game = assembly.GetType("Game")!;
                        game.GetField("CombatLifetime", flags)!.SetValue(null, owner);
                        game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
                        Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                        var state = c.PlayerUpdatesState!; completion = (Task)state.GetType().GetField("Completion", flags)!.GetValue(state)!; after.Completion = completion;
                    }
                    SpawnedMonster? replacement = null;
                    if (scenario.Player && scenario.Reload)
                    {
                        replacement = new("replacement actor identity", new(104, 100, 0), 0, 0, 777) { Variety = variety, Level = 68 };
                        c.Monsters[42] = replacement;
                    }
                    if (!scenario.Player && !scenario.Earlier)
                        normal = (Task)Call("SetQuestFlags", "Whisperer", wire, ciphers, c,
                            new[] { clearedExit ? "a1q1-KilledHillock" : "a1q1-SeenHillock" }, !clearedExit)!;
                    if (held) { gate.Release(); held = false; } else wire.Release.TrySetResult();
                    await pending!; if (normal is not null) await normal;
                    if (scenario.Reload) { await after.Next(PlayerUpdateRole.Recovery); await after.Next(PlayerUpdateRole.Player); await after.OtherRoles(); }
                    await (Task)Call("PlayerMinionsTick", "Whisperer", wire, ciphers, c, 0d)!;
                    Console.WriteLine($"[r12] player={scenario.Player} reload={scenario.Reload} earlier={scenario.Earlier}: XP {startXp}->{c.Experience}; kill/player/xp/drop {kills}/{playerKills}/{xpHooks}/{dropHooks}");
                    if (c.Experience != startXp + 37 || kills != 1 || playerKills != (scenario.Player ? 1 : 0) || xpHooks != 1 || dropHooks != 1)
                        failures.Add("accepted kill lost or misattributed: " + scenario);
                    Assert.Equal(0, excluded); Assert.False(c.Closed); Assert.False(c.LeavingArea);
                    var packets = new List<byte[]>(); while (wire.Plain.Reader.TryRead(out var packet)) packets.Add(packet);
                    Assert.Equal(1, packets.Count(p => p.SequenceEqual(InstanceEntities.PlayerExperienceUpdate(Program.PlayerId, c.Experience))));
                    Assert.Equal(1u, Assert.Single(c.VaalSouls.Read()).Current);
                    Assert.Equal(1u, Assert.Single(c.SentVaalSouls).Value.Current);
                    var ground = c.Instance.Ground.Within(100, 100, 1000, _ => true).ToArray(); Assert.Single(ground);
                    Assert.Equal(Program.Items.Find("Portal Scroll")!.Hash, ground[0].Item.Item.TypeHash);
                    Assert.Equal(scenario.Player ? 1 : 0, c.PlayerChargeState.Held(ChargeType.Frenzy));
                    Assert.Equal(scenario.Player ? 1 : 0, c.PlayerChargeState.Held(ChargeType.Power));
                    Assert.Equal(FlaskCharges.FullShortcut ? 24 : (int)Math.Floor(FlaskCharges.KillCharges(MonsterRarity.Normal, Program.Items.Mods)) + (scenario.Player ? 1 : 0),
                        inventory.Items(PlayerInventories.Flask).Single().Item.Charges);
                    if (replacement is not null)
                    {
                        Assert.Same(replacement, c.Monsters[42]); Assert.Equal(777u, replacement.Life);
                        Assert.DoesNotContain(packets, p => p.SequenceEqual(InstanceProtocol.LifeUpdate(42, 0)));
                    }
                    if (!scenario.Player)
                    {
                        var currentFlags = InstanceEntities.PlayerQuestFlagsUpdate(Program.PlayerId, (List<int>)Call("PlayerQuestFlags", c)!);
                        var questPackets = packets.Where(p => p.Length > 17 && p[0] == 2 && p[1] == 0xba && p[13] == 0x0e).ToArray();
                        if (questPackets.Length == 0 || questPackets.Any(p => !p.SequenceEqual(currentFlags)))
                            failures.Add("stale full quest flags published: " + scenario);
                        Assert.True(staleFlags is null || !staleFlags.SequenceEqual(currentFlags));
                        var create = InstanceEntities.CreateObject(0x501, InstanceEntities.ObjectTypeHash(exit.Type), exit.Blob());
                        if (clearedExit)
                        {
                            Assert.DoesNotContain(packets, p => p.AsSpan().IndexOf(create) >= 0);
                            Assert.Contains(packets, p => p.SequenceEqual(InstanceProtocol.RemoveObject(0x501)));
                        }
                        else Assert.Contains(packets, p => p.AsSpan().IndexOf(create) >= 0);
                        Console.WriteLine($"[r12] flags current={Convert.ToHexString(currentFlags)} packets=" + string.Join(",", questPackets.Select(Convert.ToHexString)));
                    }
                    uint acceptedXp = c.Experience; int acceptedKills = kills;
                    await Game.PublishMinionOutcomes("Whisperer", wire, ciphers, c);
                    if (scenario.Player) await Game.TriggerFootstepSkill("Whisperer", wire, ciphers, c, grant);
                    else await Game.MinionSwing("Whisperer", wire, ciphers, c, attack!);
                    await (Task)Call("PlayerMinionsTick", "Whisperer", wire, ciphers, c, 0d)!;
                    Assert.Equal(acceptedXp, c.Experience); Assert.Equal(acceptedKills, kills);
                    Assert.Equal(1, xpHooks); Assert.Equal(1, dropHooks); Assert.Equal(scenario.Player ? 1 : 0, playerKills);
                    Assert.Empty(c.Minions.OutputQueue); Assert.Empty(c.Minions.FlaskRewards);
                    while (wire.Plain.Reader.TryRead(out var late))
                        Assert.False(late.AsSpan().IndexOf(InstanceEntities.PlayerExperienceUpdate(Program.PlayerId, c.Experience)) >= 0);
                    if (scenario.Reload && scenario.Player)
                    {
                        // Retired producer cannot accept another genuine contact, even with a formerly valid grant.
                        c.Monsters[43] = new("retired footstep target", new(104, 100, 0), 0, 0, 777) { Variety = variety, Level = 68 };
                        await Game.TriggerFootstepSkill("Whisperer", wire, ciphers, c, grant);
                        Assert.Equal(777u, c.Monsters[43].Life);
                        provider.Stop(_ => { }); Assert.True(c.Closed);
                        await (Task)Call("TriggerFootstepSkill", "Whisperer", wire, ciphers, c, grant)!;
                        Assert.Equal(777u, c.Monsters[43].Life); Assert.Equal(acceptedXp, c.Experience);
                    }
                    else if (scenario.Player)
                    {
                        // Already completed gains keep their count, original deadline and native buff identity too.
                        var shown = c.PlayerChargeState.Presented.ToArray();
                        double deadline = c.PlayerChargeState.ExpiresAt(ChargeType.Power);
                        oldOwner.Stop(ex => throw ex); game = assembly.GetType("Game")!;
                        game.GetField("CombatLifetime", flags)!.SetValue(null, owner); game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
                        Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                        var state = c.PlayerUpdatesState!; completion = (Task)state.GetType().GetField("Completion", flags)!.GetValue(state)!; after.Completion = completion;
                        await after.Next(PlayerUpdateRole.Recovery); await after.Next(PlayerUpdateRole.Player); await after.OtherRoles();
                        var session = Call("ChargesOf", c)!;
                        Assert.Same(c.PlayerChargeState, session.GetType().GetProperty("State")!.GetValue(session));
                        var repeated = (IEnumerable)session.GetType().GetMethod("Presentation")!.Invoke(session, [0d, (Func<ushort>)(() => c.NextBuff++), false])!;
                        Assert.Empty(repeated.Cast<object>()); Assert.Equal(shown, c.PlayerChargeState.Presented);
                        Assert.Equal(1, c.PlayerChargeState.Held(ChargeType.Power)); Assert.Equal(deadline, c.PlayerChargeState.ExpiresAt(ChargeType.Power));
                    }
                }
                finally
                {
                    if (held) gate.Release(); wire.Release.TrySetResult();
                    if (pending is not null) await pending!; if (normal is not null) await normal;
                    c.Closed = true; c.RetirePlayerUpdates(); oldOwner.Stop(_ => { }); owner.Stop(_ => { });
                    if (oldState is not null) await oldState.Completion; if (completion is not null) await completion;
                    ((ModuleLifetime)context.Lifetime).Stop(_ => { }); alc.Unload();
                }
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
        finally
        {
            Game.CombatLifetime = priorRules; Game.PlayerUpdatesSchedule = priorSchedule; Program.SetGameLogic(priorLogic); provider.Stop(_ => { });
            for (int i = 0; i < optIns.Length; i++) Environment.SetEnvironmentVariable(optIns[i], priorFlags[i]);
        }
    }

    static async Task MinionAcceptedWorkAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var priorLogic = Program.Logic; var priorRules = Game.CombatLifetime; var priorSchedule = Game.PlayerUpdatesSchedule;
        string[] optIns = ["Game_VERIFIED_PALADIN", "Game_VERIFIED_ANCESTRAL_COMMANDER", "Game_DROPS"];
        var priorFlags = optIns.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (string flag in optIns) Environment.SetEnvironmentVariable(flag, "1");
        var failures = new List<string>();
        var provider = new ModuleLifetime("PhreciaAscendancies");
        using var source = AscendancyPolicies.Register(provider, h.Policy);
        try
        {
            foreach (var (kind, reload, refund) in new[]
                { ("expiry", false, false), ("expiry", true, false), ("expiry", false, true), ("kill", false, false), ("kill", true, false) })
            {
                using var wire = new CaptureStream();
                var ciphers = new ConnectionCiphers(new byte[64], false);
                var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
                var alc = new AssemblyLoadContext("Minion accepted " + kind + reload, isCollectible: true);
                alc.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
                var assembly = alc.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, assembly);
                var game = assembly.GetType("Game")!;
                object? Call(string name, params object?[] args) => game.GetMethods(flags)
                    .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
                var runtime = new ManualRuntime(); var before = new ManualUpdates(runtime); var after = new ManualUpdates(runtime);
                Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
                var definition = new AscendancyCatalog().Definitions.Single(d => d.Name == (kind == "expiry" ? "Paladin" : "Ancestral Commander"));
                byte classRow = (byte)Enumerable.Range(0, 7).First(row => Program.Skills.ClassFor(row).IntegerId == definition.BaseClass);
                var c = h.NewConnection("minion" + kind + Guid.NewGuid().ToString("N"), optional: false, classRow: classRow, area: kind == "kill" ? MercyMission.Area : "Whisperer");
                c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string>(kind == "kill" ? ["Game", "PhreciaAscendancies", "QuestObjects"] : ["Game", "PhreciaAscendancies"]));
                c.MainAscendancy = new(definition.Identity, kind == "expiry"
                    ? [363, 13763, 29126, 40299, 2437, 52104, 43835, 46492] : [19258, 47491, 17511], AscendancyRules.AwardKeys);
                c.Passives = []; c.Wire = ("Whisperer", wire, ciphers); c.AiEnabled = false;
                AscendancyPolicies.SelectionChanged(c); AscendancyPolicies.RequirePlayable(c, definition.Identity);
                c.ProjectileRuntimeState = runtime; c.Walk.Teleport(100, 100, 0);
                c.Pools = Game.PoolsFor(c, 68) with { Life = 1000, Mana = 1000, Armour = 0, Evasion = 0, BlockChance = 0 };
                c.PlayerLife = c.PlayerMana = 1000; c.EnergyShield.SetMaximum(0, false); c.Ward.SetMaximum(0);
                c.IncomingHitRandom = new HighDraw(); c.Minions.Random = new FixedDraw(0);
                var variety = kind == "kill" ? Game.Monsters.Find(MercyMission.Hailrake)!
                    : Game.Monsters.All.First(v => v.Supported && v.Id.Contains("Zombies/Zombie") && Game.Monsters.BaseExperience(v, 68) > 0);
                Assert.True(variety.Supported);
                var trigger = new SpawnedMonster("actual minion source", new(105, 100, 0), 0, 0, 1000)
                    { Variety = variety, Level = 68, Rarity = MonsterRarity.Unique };
                c.Monsters[40] = trigger;
                var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1)); bool held = false;
                PlayerUpdatesState? oldState = null; object? currentState = null; Task? currentCompletion = null;
                // Register real mod hooks through their normal host context; no disk module, worker or test project.
                var loadedType = typeof(ModHost).GetNestedType("LoadedMod", flags)!;
                var loaded = Activator.CreateInstance(loadedType, flags, null,
                    [ModHost.ReadManifest("QuestObjects", "{}"), "logs", null], null)!;
                var context = (IModuleContext)Activator.CreateInstance(typeof(ModHost).GetNestedType("Context", flags)!, flags, null,
                    [Program.ServerMods, loaded], null)!;
                using var mercy = new GameServer.Modules.M_questobjects.QuestObjectsModule();
                mercy.Load(context);
                int killHooks = 0, excludedHooks = 0, xpHooks = 0, dropHooks = 0, playerKillHooks = 0;
                using var kill = GameEvents.Kill.Add("Game", k => { if (ReferenceEquals(k.Conn, c)) { killHooks++; Assert.False(k.PlayerKill); } });
                using var excluded = GameEvents.Kill.Add("not-in-this-league", k => { if (ReferenceEquals(k.Conn, c)) excludedHooks++; });
                using var xp = context.ModifyExperience(e => { if (e.Player.Character == c.Character) { xpHooks++; e.Experience = 37; } });
                using var drops = context.ModifyDrops(d => { if (d.Player.Character == c.Character) { dropHooks++; d.Cancel = true; d.Items.Add("Portal Scroll"); } });
                using var playerKill = context.OnMonsterKilled(k => { if (k.Player.Character == c.Character) playerKillHooks++; return Task.CompletedTask; });
                var originalOutput = Console.Out; using var output = new StringWriter(); Console.SetOut(TextWriter.Synchronized(output));
                try
                {
                    if (kind == "expiry")
                    {
                        Assert.True(Game.TriggerElementalRelicLocked(c, trigger, hit: true, killed: false));
                        Assert.Equal(1, c.Minions.PendingRelics);
                        await Game.PlayerMinionsTick("Whisperer", wire, ciphers, c, 0);
                    }
                    else
                    {
                        var attack = new MonsterAttack(0x102, 0, 10, DamagePacket.Of(DamageType.Physical, 200, 200));
                        var hit = Game.CommitMonsterHitLocked("Whisperer", c, 40, trigger, attack, c.Clock.Elapsed.TotalSeconds, (100, 100), (105, 100), false)!;
                        Assert.True(hit.Spirit); await Game.PresentMonsterHit("Whisperer", wire, ciphers, c, 40, hit);
                    }
                    var minion = Assert.Single(c.Minions.All); Assert.True(kind == "expiry" ? minion.Explodes is not null : minion.Fights);
                    if (refund)
                    {
                        c.RefundPoints = 200;
                        CharacterSaves.SetMainAscendancy(c.Character!, c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
                        c.SavedPassiveProgress = new(c.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
                        Assert.Null(await Game.ChangePassives("Whisperer", wire, ciphers, c, [40299], false, "minion source refund", refundPoints: true));
                        Assert.DoesNotContain((ushort)40299, c.MainAscendancy.Allocated); Assert.Equal((ushort)195, c.RefundPoints);
                    }
                    c.Monsters.Remove(40);
                    var victim = new SpawnedMonster("accepted minion victim", new(104, 100, 0), 0, 0, kind == "expiry" ? 10000000u : 1u)
                        { Variety = variety, Level = kind == "expiry" ? (byte)68 : (byte)1 };
                    c.Monsters[42] = victim;
                    if (kind == "kill")
                    {
                        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalGroundSlam")!);
                        Game.ItemsOf(c).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
                            { Sockets = [new(SocketColour.White, gem)], Links = [1] });
                        c.PlayerStats[Key("virtual_strength_for_requirements")] = 1000;
                        foreach (var old in Game.ItemsOf(c).Items(PlayerInventories.Flask).ToArray()) Game.ItemsOf(c).Remove(PlayerInventories.Flask, old.Id);
                        Game.ItemsOf(c).Add(PlayerInventories.Flask, 0, 0,
                            ItemCatalog.Create(Program.Items.Find("Small Mana Flask")!) with { Charges = 0 });
                        Game.RefreshVaalSouls(c); Assert.Equal(0u, Assert.Single(c.VaalSouls.Read()).Current);
                        await Game.SendVaalSouls("Whisperer", wire, ciphers, c);
                    }
                    c.PlayerSent = true; Program.ActivatePlayerUpdates(c);
                    oldState = (PlayerUpdatesState)c.PlayerUpdatesState!; before.Completion = oldState.Completion;
                    await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
                    double at = kind == "expiry" ? minion.Until + .01 : 0;
                    before.At = runtime.At = at;
                    uint startXp = c.Experience, startLife = victim.Life;
                    while (wire.Plain.Reader.TryRead(out _)) { }
                    await gate.WaitAsync(); held = true;
                    Task pending; PlayerMinions.Attack? acceptedAttack = null;
                    if (kind == "expiry") pending = Game.PlayerMinionsTick("Whisperer", wire, ciphers, c, at);
                    else
                    {
                        minion.Monster.Walk.Teleport(103, 100, 0);
                        var attack = new PlayerMinions.Attack(minion.Id, minion.Monster, (103, 100), 42, victim, (104, 100), 0) { Started = true };
                        c.Minions.Attacks[minion.Id] = attack; acceptedAttack = attack;
                        pending = Game.MinionSwing("Whisperer", wire, ciphers, c, attack);
                    }
                    Assert.False(pending.IsCompleted);
                    if (kind == "expiry") { Assert.True(minion.Monster.Dead); Assert.False(c.Minions.Owns(minion.Id)); }
                    else { Assert.True(victim.Dead); Assert.Equal(0u, victim.Life); }
                    if (reload)
                    {
                        oldOwner.Stop(ex => throw ex);
                        game.GetField("CombatLifetime", flags)!.SetValue(null, owner);
                        game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after); after.At = at;
                        Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                        currentState = c.PlayerUpdatesState!;
                        currentCompletion = (Task)currentState.GetType().GetField("Completion", flags)!.GetValue(currentState)!; after.Completion = currentCompletion;
                    }
                    gate.Release(); held = false; await pending;
                    if (reload)
                    {
                        await oldState.Completion;
                        var tick = await after.Next(PlayerUpdateRole.Minions); tick.Advance(at); await after.Next(PlayerUpdateRole.Minions);
                    }
                    bool ok = kind == "expiry" ? (refund ? victim.Life == startLife : victim.Life < startLife)
                        : c.Experience == startXp + 37 && killHooks == 1 && xpHooks == 1 && dropHooks == 1;
                    Console.WriteLine($"[r10] {kind} reload={reload} refund={refund}: target {startLife}->{victim.Life}, XP {startXp}->{c.Experience}, kill/xp/drop hooks {killHooks}/{xpHooks}/{dropHooks}");
                    Assert.Equal(0, excludedHooks); Assert.Equal(0, playerKillHooks); Assert.False(c.Closed);
                    if (!reload) Assert.True(ok, "No-reload positive control: " + kind);
                    else if (!ok) failures.Add(kind + " accepted work lost across distinct Game replacement");
                    uint acceptedLife = victim.Life, acceptedXp = c.Experience;
                    var projected = new List<byte>(); while (wire.Plain.Reader.TryRead(out var packet)) projected.AddRange(packet);
                    static int Occurrences(byte[] bytes, byte[] needle)
                    {
                        int count = 0, at = 0;
                        while (at <= bytes.Length - needle.Length)
                        { int found = bytes.AsSpan(at).IndexOf(needle); if (found < 0) break; count++; at += found + needle.Length; }
                        return count;
                    }
                    var native = projected.ToArray();
                    if (kind == "expiry") Assert.Equal(1, Occurrences(native, InstanceProtocol.LifeUpdate(minion.Id, 0)));
                    else if (ok)
                    {
                        Assert.Equal(1, Occurrences(native, InstanceProtocol.LifeUpdate(42, 0)));
                        Assert.Equal(1, Occurrences(native, InstanceEntities.PlayerExperienceUpdate(Program.PlayerId, acceptedXp)));
                        Assert.Equal(1u, Assert.Single(c.VaalSouls.Read()).Current);
                        Assert.Equal(1u, Assert.Single(c.SentVaalSouls).Value.Current);
                        int expectedCharges = FlaskCharges.FullShortcut ? 24 : (int)Math.Floor(FlaskCharges.KillCharges(MonsterRarity.Normal, Program.Items.Mods));
                        Assert.Equal(expectedCharges, Game.ItemsOf(c).Items(PlayerInventories.Flask).Single().Item.Charges);


                        var ground = c.Instance.Ground.Within(100, 100, 1000, _ => true).ToArray();
                        Assert.Equal(2, ground.Length); // The real league QuestObjects handler adds the Medicine Chest too.
                        Assert.Contains(ground, g => Program.Items.ByHash(g.Item.Item.TypeHash)?.Id == MercyMission.MedicineChest);
                    }
                    // Late old callbacks cannot repeat a hit, XP/drop hooks or consume the current output head.
                    var queued = c.Minions.OutputQueue.ToArray();
                    await gate.WaitAsync(); held = true;
                    var oldPublish = Game.PublishMinionOutcomes("Whisperer", wire, ciphers, c);
                    if (reload) { Assert.True(oldPublish.IsCompleted); Assert.Equal(queued, c.Minions.OutputQueue.ToArray()); }
                    gate.Release(); held = false; await oldPublish;
                    if (acceptedAttack is not null) await Game.MinionSwing("Whisperer", wire, ciphers, c, acceptedAttack);
                    if (reload) await (Task)Call("PlayerMinionsTick", "Whisperer", wire, ciphers, c, at)!;
                    else await Game.PlayerMinionsTick("Whisperer", wire, ciphers, c, at);
                    Assert.Equal(acceptedLife, victim.Life); Assert.Equal(acceptedXp, c.Experience);
                    Assert.Equal(kind == "kill" ? 1 : 0, killHooks); Assert.Equal(killHooks, xpHooks); Assert.Equal(killHooks, dropHooks);
                    Assert.Empty(c.Minions.OutputQueue); Assert.Empty(c.Minions.FlaskRewards);
                    string acceptedLogs = output.ToString();
                    if (kind == "kill")
                    {
                        Assert.Equal(1, acceptedLogs.Split($"0x02ba current player experience ({victim.Rarity} {victim.Label} 0x2a)").Length - 1);
                        Assert.Contains($"0x01b1 monster 0x2a hit by minion 0x{minion.Id:x} ({minion.Monster.Label}) for ", acceptedLogs);
                        Assert.Contains($"life 0/{victim.MaxLife} (dies)", acceptedLogs);
                    }
                    else if (!refund)
                    {
                        Assert.Contains($"{minion.Kind} Relic 0x{minion.Id:x} explodes for ", acceptedLogs);
                        Assert.Equal(1, acceptedLogs.Split(" cells: 1 hit, 0 killed").Length - 1);
                    }
                    else Assert.DoesNotContain(" explodes for ", acceptedLogs);
                    if (kind == "kill") Assert.Equal(FlaskCharges.FullShortcut ? 24 : (int)Math.Floor(FlaskCharges.KillCharges(MonsterRarity.Normal, Program.Items.Mods)),
                        Game.ItemsOf(c).Items(PlayerInventories.Flask).Single().Item.Charges);

                    if (kind == "expiry")
                    {
                        // Even a new dead actor at the old body id is a different identity and must remain.
                        var newer = new SpawnedMonster("replacement body identity", new(110, 110, 0), 0, 0, 777) { Dead = true, Life = 0 };
                        if (reload)
                        {
                            while (wire.Plain.Reader.TryRead(out _)) { }
                            await gate.WaitAsync(); held = true;
                            var removal = (Task)Call("PlayerMinionsTick", "Whisperer", wire, ciphers, c, at + 1.25)!;
                            Assert.False(removal.IsCompleted); Assert.False(c.Monsters.ContainsKey(minion.Id));
                            c.Monsters[minion.Id] = newer;
                            gate.Release(); held = false; await removal;
                            var removalPackets = new List<byte>(); while (wire.Plain.Reader.TryRead(out var packet)) removalPackets.AddRange(packet);
                            Assert.Equal(0, Occurrences(removalPackets.ToArray(), InstanceProtocol.RemoveObject(minion.Id)));
                        }
                        else
                        {
                            c.Monsters[minion.Id] = newer;
                            await Game.PlayerMinionsTick("Whisperer", wire, ciphers, c, at + 1.25);
                        }
                        Assert.Same(newer, c.Monsters[minion.Id]);
                    }
                    else if (reload)
                    {
                        // A current source retired before acceptance cannot turn a queued plan into damage/rewards.
                        var refused = new SpawnedMonster("retired source target", new(104, 100, 0), 0, 0, 777);
                        c.Monsters[43] = refused;
                        var plan = new PlayerMinions.Attack(minion.Id, minion.Monster, (103, 100), 43, refused, (104, 100), 0) { Started = true };
                        c.Minions.Attacks[minion.Id] = plan;
                        provider.Stop(_ => { }); Assert.True(c.Closed);
                        await (Task)Call("MinionSwing", "Whisperer", wire, ciphers, c, plan)!;
                        Assert.Equal(777u, refused.Life); Assert.Equal(acceptedXp, c.Experience);
                        Assert.Equal(1, killHooks); Assert.Equal(1, xpHooks); Assert.Equal(1, dropHooks);
                    }

                }
                finally
                {
                    Console.SetOut(originalOutput);
                    if (held) gate.Release(); c.Closed = true; c.RetirePlayerUpdates(); oldOwner.Stop(_ => { }); owner.Stop(_ => { });
                    if (oldState is not null) await oldState.Completion; if (currentCompletion is not null) await currentCompletion;
                    ((ModuleLifetime)context.Lifetime).Stop(_ => { }); alc.Unload();
                }
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
        finally
        {
            Game.CombatLifetime = priorRules; Game.PlayerUpdatesSchedule = priorSchedule; Program.SetGameLogic(priorLogic);
            provider.Stop(_ => { });
            for (int i = 0; i < optIns.Length; i++) Environment.SetEnvironmentVariable(optIns[i], priorFlags[i]);
        }
    }

    // Extend the grouped regression: no additional Facts or runtime/client access.
    static async Task QuestAndCollisionReloads(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var priorLogic = Program.Logic; var priorRules = Game.CombatLifetime;
        var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
        var context = new AssemblyLoadContext("Quest and collision handoff", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location);
        var game = assembly.GetType("Game")!;
        object? Call(string name, params object?[] args) => game.GetMethods(flags)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
        using var wire = new CaptureStream();
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var receive = new ConnectionCiphers(new byte[64], true);
        var c = h.NewConnection("phase" + Guid.NewGuid().ToString("N"), optional: false, area: Game.MavenNucleus.Area);
        var frozen = h.NewConnection("frozen" + Guid.NewGuid().ToString("N"), optional: false);
        var moving = h.NewConnection("held" + Guid.NewGuid().ToString("N"), optional: false);
        Task? pending = null, ai = null;
        try
        {
            Game.CombatLifetime = oldOwner; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
            var variety = Game.Monsters.Find(Game.MavenNucleus.Maven)!; Assert.True(variety.Supported);
            var victim = new SpawnedMonster("Maven", new(100, 100, 0), 0, 0, 1000)
                { Variety = variety, Level = 68, Rarity = MonsterRarity.Unique, Life = 0, Dead = true };
            c.Monsters[42] = victim;
            c.Minions.OutputQueue.Add(new([], null, Target: victim, Kind: PlayerMinions.OutputKind.QuestKill, At: (100, 100)));
            wire.Hold = () => true;
            pending = Game.PublishMinionOutcomes("handoff", wire, ciphers, c);
            await wire.Held.Task;
            Assert.False(pending.IsCompleted);
            // One cipher transaction admits every phase actor and its complete creation batch.
            Assert.Single(c.Monsters.Values, m => m.Variety?.Id == Game.MavenNucleus.Nucleus);
            Assert.Equal(4, c.Monsters.Values.Count(m => m.Variety?.Id == Game.MavenNucleus.Phase2Cores[0]));
            var identities = c.Monsters.Keys.ToArray();
            var ringVariety = Game.Monsters.Find(Game.MavenNucleus.Phase2Cores[0])!;
            Assert.Contains((ringVariety.Row, (byte)68), c.MonsterTypes);
            Assert.True(wire.Plain.Reader.TryRead(out var creationBatch));
            foreach (uint id in identities.Where(id => id != 42))
                Has(creationBatch!, InstanceProtocol.EnterWakeZone(id));
            // Run the real planner while creation owns the cipher gate. Its output must queue after this batch.
            c.AiEnabled = true; c.EntryProtection.End(0, (0, 0), action: true);
            c.Walk.Teleport(100, 100, 0);
            ai = Game.StepMonsterAi("handoff", wire, ciphers, c, new MonsterAiCadence());

            // Freeze and movement lose the cipher gate before retirement; neither may acknowledge/mutate early.
            double now = frozen.Clock.Elapsed.TotalSeconds;
            frozen.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 1000, 1000),
                DamagePacket.Of(DamageType.Cold, 1000, 1000), true, frozen.PlayerMaxLife, now, _ => 0, new Random(1));
            Assert.True(frozen.ElementalAilments.FreezeUntil(now) > now);
            frozen.FreezeGeneration++;
            moving.Walk.Teleport(100, 100, 0);
            moving.HeldMove = new(120, 100, (ushort)0x4000, 0, 0);
            var click = moving.HeldMove; int action = moving.ActionCount;
            var freeze = Game.ShowFreeze("handoff", wire, ciphers, frozen);
            var move = Game.ReleaseHeldMove("handoff", wire, ciphers, moving);
            Assert.NotEqual(frozen.FreezeGeneration, frozen.FreezeShown);
            Assert.Same(click, moving.HeldMove); Assert.Equal(action, moving.ActionCount);
            oldOwner.Stop(_ => { });
            game.GetField("CombatLifetime", flags)!.SetValue(null, owner);
            Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!,
                flags, null, [owner], null)!);
            wire.Release.TrySetResult(); await pending; await ai; await freeze; await move;
            Assert.NotEqual(frozen.FreezeGeneration, frozen.FreezeShown);
            Assert.Same(click, moving.HeldMove); Assert.Equal(action, moving.ActionCount);
            Assert.Equal((100, 100), moving.Walk.Position(0));
            Assert.Empty(c.Minions.OutputQueue);
            await (Task)Call("PublishMinionOutcomes", "handoff", wire, ciphers, c)!;
            await (Task)Call("ShowFreeze", "handoff", wire, ciphers, frozen)!;
            await (Task)Call("ReleaseHeldMove", "handoff", wire, ciphers, moving)!;
            Assert.Equal(frozen.FreezeGeneration, frozen.FreezeShown);
            Assert.Null(moving.HeldMove); Assert.Equal(action + 1, moving.ActionCount);
            Assert.Empty(c.Minions.OutputQueue); Assert.Equal(identities, c.Monsters.Keys.ToArray());
            var bytes = wire.Drain(receive);
            Has(bytes, InstanceEntities.MonsterTypes([((ushort)ringVariety.Row, (byte)68)]));
            Has(bytes, InstanceProtocol.ActorAnimation(Program.PlayerId, 1, PlayerHitRecovery.TakeHitAnimation)[..6]);
            foreach (uint id in identities.Where(id => id != 42)) Has(bytes, InstanceProtocol.EnterWakeZone(id));
            Has(bytes, InstanceProtocol.ActionStarted(Program.PlayerId, 100, 100, 120, 100, (ushort)0x4000, 0, 0));
            await (Task)Call("PublishMinionOutcomes", "handoff", wire, ciphers, c)!;
            await (Task)Call("ReleaseHeldMove", "handoff", wire, ciphers, moving)!;
            Assert.Equal(action + 1, moving.ActionCount); Assert.Equal(identities, c.Monsters.Keys.ToArray());

            // Conversely, once the current cipher transaction accepts output, retirement must not discard it.
            using var acceptedWire = new CaptureStream();
            var acceptedCiphers = new ConnectionCiphers(new byte[64], false);
            var acceptedReceive = new ConnectionCiphers(new byte[64], true);
            acceptedWire.Hold = () => true;
            moving.HeldMove = new(130, 100, (ushort)0x4000, 0, 0);
            var acceptedMove = (Task)Call("ReleaseHeldMove", "handoff", acceptedWire, acceptedCiphers, moving)!;
            await acceptedWire.Held.Task;
            Assert.Null(moving.HeldMove); Assert.Equal(action + 2, moving.ActionCount);
            owner.Stop(_ => { });
            acceptedWire.Release.TrySetResult(); await acceptedMove;
            Assert.NotEmpty(acceptedWire.Drain(acceptedReceive));
            await Game.ReleaseHeldMove("handoff", acceptedWire, acceptedCiphers, moving);
            Assert.Equal(action + 2, moving.ActionCount);
        }
        finally
        {
            wire.Release.TrySetResult(); if (pending is not null) await pending;
            oldOwner.Stop(_ => { }); owner.Stop(_ => { }); context.Unload();
            Game.CombatLifetime = priorRules; Program.SetGameLogic(priorLogic);
        }
    }

    // Exercise real distinct Game identities, sharing only the framework/catalog/protocol assemblies.
    static async Task AcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var previousLogic = Program.Logic; var previousRules = Game.CombatLifetime; var previousSchedule = Game.PlayerUpdatesSchedule;
        var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
        var context = new AssemblyLoadContext("Whisperer current Game regression", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location);
        Assert.NotSame(typeof(Game).Assembly, assembly);
        var game = assembly.GetType("Game")!;
        object? Call(string name, params object?[] args) => game.GetMethods(flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
        static object Field(object value, string name) => value.GetType().GetField(name, flags)!.GetValue(value)!;
        var c = h.NewConnection("cross" + Guid.NewGuid().ToString("N"), optional: false); c.MainAscendancy = new();
        c.PlayerSent = true; c.Wire = ("Whisperer", h.Wire, h.Ciphers); c.AiEnabled = false;
        c.IncomingHitRandom = new HighDraw(); c.Pools = new(68, 1000, 1000, 100, 0) { Armour = 0, Evasion = 0 };
        c.PlayerLife = c.PlayerMana = 1000; c.EnergyShield.SetMaximum(0, false); c.Ward.SetMaximum(0);
        c.PlayerStats[Key("gain_x_rage_when_hit")] = 1; c.PlayerStats[Key(ManaRecoveryRules.RagePer200)] = 10;
        c.PlayerStats[Key("no_inherent_rage_loss")] = 1; c.PlayerStats[Key("virtual_intelligence_for_requirements")] = 1000;
        var runtime = new ManualRuntime(); c.ProjectileRuntimeState = runtime;
        var before = new ManualUpdates(runtime); var after = new ManualUpdates(runtime);
        PlayerUpdatesState? oldState = null; object? currentState = null; Task? currentCompletion = null;
        var spawnGate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1)); bool spawnHeld = false;
        int spawnCommits = 0;
        Action<uint, SpawnedMonster> admitMinion = (id, m) =>
        {
            spawnCommits++;
            c.Minions.Add(new(id, m, "spawn-admission-control", "relic") { Until = double.PositiveInfinity });
        };
        try
        {
            Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
            Program.ActivatePlayerUpdates(c); oldState = Assert.IsType<PlayerUpdatesState>(c.PlayerUpdatesState); before.Completion = oldState.Completion;
            await before.Next(Game.RecoveryStepMs);
            await before.Next(Game.PlayerTickMs);
            await before.OtherRoles();
            var attack = new MonsterAttack(0x102, 0, 10, DamagePacket.Of(DamageType.Physical, 100, 100));
            var enemy = new SpawnedMonster("replacement AI", new(105, 100, 0), 100, 5, 1000) { Attack = attack, Aggro = true };
            var stage = new SpawnedMonster("pending stage", new(180, 180, 0), 0, 0, 1000)
                { Stage = new(50, 1, 1, 1), StageRunning = true, StageTaken = true, Life = 500, BusyUntil = 0 };
            c.Monsters[43] = stage;
            c.Monsters[42] = enemy; c.AiEnabled = true; c.Walk.Teleport(100, 100, 0);
            // A reserved new-main minion contact and its neutral roster survive distinct Game replacement.
            var minion = new SpawnedMonster("owned melee", new(105, 102, 0), 100, 5, 1000)
                { Attack = attack with { Seconds = .4 }, Hostile = false, Aggro = true, Level = 68, BusyUntil = .4 };
            var minionVictim = new SpawnedMonster("minion victim", new(105, 103, 0), 0, 0, 1000) { Level = 0 };
            c.Monsters[44] = minion; c.Monsters[45] = minionVictim;
            var owned = new PlayerMinions.Minion(44, minion, "current-minion-control", "melee")
            {
                Fights = true, Until = double.PositiveInfinity,
                OwnerAura = new("Anger", AuraCatalog.BuffRow("Anger"), 60, [(Key("base_life_regeneration_rate_per_minute"), 1)]),
            };
            c.Minions.Add(owned);
            var pendingMinion = new PlayerMinions.Attack(44, minion, (105, 102), 45, minionVictim, (105, 103), .2);
            c.Minions.Attacks[44] = pendingMinion;
            c.PlayerStats[Key("link_skills_can_target_minions")] = 1;
            var link = new LinkSkills.Link(44, 1, "SoulLink") { Until = 100, ManaPerMinute = 300 };
            c.Links.Add(link); c.Links.PaidAt = 0;
            Game.AreaUpdateContext.Value = oldState;
            Task oldMinion;
            try { oldMinion = Game.MinionSwing("Whisperer", h.Wire, h.Ciphers, c, pendingMinion); }
            finally { Game.AreaUpdateContext.Value = null; }
            await before.Next(PlayerUpdateRole.MonsterAction); Assert.True(pendingMinion.Started);
            var variety = Game.Monsters.Find("Metadata/Monsters/AnimatedItem/ElementalLivingRelicFire")!;
            Assert.True(variety.Supported);
            await spawnGate.WaitAsync(); spawnHeld = true;
            var oldSpawn = Game.SpawnMonster("Whisperer", h.Wire, h.Ciphers, c, variety, 68, MonsterRarity.Normal,
                110, 110, friendly: true, log: false, owned: admitMinion, ownedCurrent: () => true);
            Assert.False(oldSpawn.IsCompleted);


            oldOwner.Stop(ex => throw ex); await oldState.Completion;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldMinion);
            Assert.Same(pendingMinion, c.Minions.Attacks[44]); Assert.Equal(1000u, minionVictim.Life);

            game.GetField("CombatLifetime", flags)!.SetValue(null, owner);
            game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
            var logic = (IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!;
            Program.SetGameLogic(logic); currentState = c.PlayerUpdatesState!; Assert.NotSame(oldState, currentState);
            spawnGate.Release(); spawnHeld = false;
            Assert.Equal(0u, await oldSpawn); Assert.Equal(0, spawnCommits);

            currentCompletion = (Task)Field(currentState, "Completion"); after.Completion = currentCompletion;
            await after.Next(Game.RecoveryStepMs); await after.Next(Game.PlayerTickMs);
            await after.Next(PlayerUpdateRole.MonsterAi); await after.Next(PlayerUpdateRole.MonsterRegeneration);
            await after.Next(PlayerUpdateRole.LivingCollisions); await after.Next(PlayerUpdateRole.Charges);
            await after.Next(PlayerUpdateRole.Fortification); var rageFrame = await after.Next(PlayerUpdateRole.Rage);
            await after.Next(PlayerUpdateRole.FlaskCharges);
            // A retired stage continuation cannot leave a surviving boss permanently StageRunning.
            while (stage.StageRunning)
            {
                var read = h.Wire.Plain.Reader.ReadAsync().AsTask();
                if (await Task.WhenAny(read, currentCompletion) == currentCompletion)
                { await currentCompletion; throw new InvalidOperationException("Area owner stopped before stage completion"); }
                var packet = await read;
                if (packet.AsSpan().IndexOf(InstanceProtocol.LifeUpdate(43, 1000)) >= 0) break;
            }
            Assert.False(stage.StageRunning); Assert.Equal(1000u, stage.Life);
            Assert.Equal(900u, c.PlayerLife); Assert.False(c.Closed); // Started current AI did the actual 100-damage contact.
            Assert.Equal(1, after.Count(PlayerUpdateRole.MonsterAi)); Assert.Equal(1, after.Count(PlayerUpdateRole.Rage));
            var minionContact = await after.Next(PlayerUpdateRole.MonsterAction);
            minionContact.Advance(.2);
            while (minionVictim.Life != 900)
            {
                var read = h.Wire.Plain.Reader.ReadAsync().AsTask();
                if (await Task.WhenAny(read, currentCompletion) == currentCompletion)
                { await currentCompletion; throw new InvalidOperationException("Area owner stopped before resumed minion contact"); }
                await read;
            }
            Assert.False(c.Minions.Attacks.ContainsKey(44)); Assert.Same(owned, c.Minions.All.Single());
            uint freshMinion = await (Task<uint>)Call("SpawnMonster", "Whisperer", h.Wire, h.Ciphers, c, variety, (byte)68,
                MonsterRarity.Normal, 110, 110, null, true, false, null, null, true, null, 0f, null, admitMinion, (Func<bool>)(() => true), null, null)!;
            Assert.True(freshMinion > 0); Assert.Equal(1, spawnCommits);
            Assert.Same(c.Monsters[freshMinion], c.Minions.All.Single(m => m.Id == freshMinion).Monster);
            Has(h.Wire.Drain(h.Receive), InstanceProtocol.EnterWakeZone(freshMinion));

            var minionFrame = await after.Next(PlayerUpdateRole.Minions);
            minionFrame.Advance(.2); await after.Next(PlayerUpdateRole.Minions);
            Assert.True(owned.OwnerBuff > 0); Assert.Same(link, c.Links.On(44)); Assert.Equal(.2, c.Links.PaidAt);
            Assert.Equal(999u, c.PlayerMana); // Current 100 ms minion -> link producer paid its real running cost.
            Assert.Equal(1, after.Count(PlayerUpdateRole.Footsteps));
            var footstepFrame = await after.Next(PlayerUpdateRole.Footsteps);
            footstepFrame.Advance(.2); await after.Next(PlayerUpdateRole.Footsteps);
            var keptBuff = owned.OwnerBuff; double paidAt = c.Links.PaidAt;
            await Game.PlayerMinionsTick("Whisperer", h.Wire, h.Ciphers, c, 101);
            await Game.LinkSkillsTick("Whisperer", h.Wire, h.Ciphers, c, 101);
            await Game.MinionSwing("Whisperer", h.Wire, h.Ciphers, c, pendingMinion);
            Assert.Equal(keptBuff, owned.OwnerBuff); Assert.Equal(paidAt, c.Links.PaidAt);
            Assert.Same(link, c.Links.On(44)); Assert.Equal(900u, minionVictim.Life); Assert.Equal(999u, c.PlayerMana);

            var retiredContact = Game.CommitMonsterHitLocked("Whisperer", c, 42, enemy, attack,
                c.Clock.Elapsed.TotalSeconds, (100, 100), (105, 100), false);
            Assert.Equal(900u, c.PlayerLife);
            Assert.True((bool)Call("InflictPlayerBlind", c, 4d, 0, null)!);
            Call("BlindPresentation", c, null);
            var items = Game.ItemsOf(c);
            foreach (var old in items.Items(PlayerInventories.Weapon1).ToArray()) items.Remove(PlayerInventories.Weapon1, old.Id);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [new(SocketColour.White, gem)], Links = [1] });
            ushort skill = (ushort)((IEnumerable)Call("GemSkillEffects", c)!).Cast<object>().Single().GetType().GetField("Item1")!.GetValue(
                ((IEnumerable)Call("GemSkillEffects", c)!).Cast<object>().Single())!;
            void Spend()
            {
                for (int i = 0; i < 40; i++)
                {
                    object?[] args = [c, skill, 0u, 0u]; Assert.True((bool)Call("PayMana", args)!); Assert.Equal(5u, args[2]);
                }
            }
            Spend(); // Accepted ordinary skill payments naturally change Rage after Blind was sent.
            var sent = c.SentRage; var keys = c.SentStatKeys.Order().ToArray();
            var stale = Game.RagePresentation(c);
            bool untouched = sent == c.SentRage && keys.SequenceEqual(c.SentStatKeys.Order());
            Spend(); // Ensure a further real change even on the red path that wrongly acknowledged the earlier value.
            while (h.Wire.Plain.Reader.TryRead(out _)) { }
            var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1)); await gate.WaitAsync();
            var oldContact = Game.MonsterContact("Whisperer", h.Wire, h.Ciphers, c, 42, enemy, enemy.ActionGeneration);
            Assert.True(oldContact.IsCompleted); await oldContact; Assert.Equal(900u, c.PlayerLife);
            int oldReads = 0; Task oldSend;
            using (GameEvents.PlayerStatSources.Add("Game", (_, stats) => { oldReads++; return stats; })) oldSend = Game.SendRage("Whisperer", h.Wire, h.Ciphers, c);
            bool refusedBeforeBuild = oldSend.IsCompleted && oldReads == 0;
            rageFrame.Advance(0); gate.Release(); await oldSend; await after.Next(PlayerUpdateRole.Rage);
            var packets = new List<byte>(); while (h.Wire.Plain.Reader.TryRead(out var packet)) packets.AddRange(packet);
            var rows = PlayerStatRows([.. packets]);
            Assert.True(retiredContact is null && stale is null && untouched && refusedBeforeBuild,
                $"Retired accepted contact={retiredContact is not null}; retired Rage bytes={stale?.Length}; bookkeeping unchanged={untouched}; refused before build={refusedBeforeBuild}");
            Assert.Contains((973u, 1), rows); Assert.Contains((15023u, -20), rows); Assert.Contains((15024u, -20), rows);
            Assert.DoesNotContain(rows, row => row.Key is 973 or 15023 or 15024 && row.Value == 0);
            Assert.Null(Call("BlindPresentation", c, null)); Assert.Equal(80, Call("PlayerAccuracy", c, null));
            // Field Medicine and new Onslaught scaling keep the accepted recipient snapshot too.
            c.PlayerStats[Key("flasks_apply_to_your_linked_targets")] = 1;
            c.PlayerStats[Key("flask_effect_+%")] = 50; c.PlayerStats[Key("onslaught_effect_+%")] = 100;
            foreach (var old in items.Items(PlayerInventories.Flask).ToArray()) items.Remove(PlayerInventories.Flask, old.Id);
            var silver = Program.Items.Find("Silver Flask")!; var silverUtility = Program.Items.UtilityFlaskOf(silver)!;
            items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(silver) with { Charges = silverUtility.MaxCharges });
            await (Task)Call("UseFlask", "Whisperer", h.Wire, h.Ciphers, c, 0u)!;
            var received = Assert.Single(c.Links.Flasks); Assert.Equal(44u, received.Target);
            Assert.Contains(received.Stats, s => s.Stat == "attack_speed_+%" && s.Value == 40);
            Assert.Contains(c.UtilityFlaskEffects.Stats(), s => s.Stat == "attack_speed_+%" && s.Value == 50);
            await Game.LinkSkillsTick("Whisperer", h.Wire, h.Ciphers, c, received.Until);
            Assert.Same(received, Assert.Single(c.Links.Flasks));
            await (Task)Call("LinkSkillsTick", "Whisperer", h.Wire, h.Ciphers, c, received.Until)!;
            Assert.Empty(c.Links.Flasks); Assert.False(c.Closed);
            Has(h.Wire.Drain(h.Receive), InstanceEntities.RemoveBuff(44, received.Buff));

            c.PlayerUpdatesWorker.Complete(oldOwner, null, oldState, oldState.Generation);
            Assert.True((bool)Call("PlayerUpdatesCurrent", c, currentState)!);
            Assert.False(c.PlayerUpdatesWorker.TryStart(c, owner, null, currentState, out _));
        }
        finally
        {
            if (spawnHeld) spawnGate.Release();
            c.Closed = true; oldOwner.Stop(_ => { }); owner.Stop(_ => { });
            if (currentCompletion is not null) await currentCompletion;
            Game.CombatLifetime = previousRules; Game.PlayerUpdatesSchedule = previousSchedule; Program.SetGameLogic(previousLogic);
            context.Unload();
        }
    }

    // Actual accepted catalog flasks, real expiry/drainer loops, distinct Game assemblies and a held send gate.
    static async Task UtilityAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var previousLogic = Program.Logic; var previousRules = Game.CombatLifetime; var previousSchedule = Game.PlayerUpdatesSchedule;
        var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
        var context = new AssemblyLoadContext("Whisperer utility handover regression", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, assembly);
        var game = assembly.GetType("Game")!;
        object? Call(string name, params object?[] args) => game.GetMethods(flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
        var c = h.NewConnection("utility" + Guid.NewGuid().ToString("N"), optional: false); c.MainAscendancy = new();
        c.PlayerSent = true; c.Wire = ("Whisperer", h.Wire, h.Ciphers); c.AiEnabled = false;
        var runtime = new ManualRuntime(); c.ProjectileRuntimeState = runtime;
        var before = new ManualUpdates(runtime); var after = new ManualUpdates(runtime);
        PlayerUpdatesState? oldState = null; object? currentState = null; Task? currentCompletion = null;
        var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1)); bool held = false;
        try
        {
            Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
            Program.ActivatePlayerUpdates(c); oldState = Assert.IsType<PlayerUpdatesState>(c.PlayerUpdatesState); before.Completion = oldState.Completion;
            var oldFast = await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
            var type = Program.Items.All.First(t => t.Class == "UtilityFlask" && t.Supported
                && Program.Items.UtilityFlaskOf(t) is { ChargesPerUse: > 0 } u && u.MaxCharges >= 2 * u.ChargesPerUse);
            var utility = Program.Items.UtilityFlaskOf(type)!;
            var plain = ItemCatalog.Create(type);
            var enchanted = Program.Items.Mods.All.Where(m => m.GenerationType == 22 && m.WeightFor(type.Tags) > 0)
                .Select(m => plain with { Mods = [.. plain.Mods, m.Roll(ModSection.Enchant, new Random(1))] })
                .Single(i => FlaskEnchantments.Matches(ItemModStats.Local(i, Program.Items.Mods), FlaskEnchantments.Trigger.End, 0, 0, false));
            var items = Game.ItemsOf(c);
            var one = items.Add(PlayerInventories.Flask, 0, 0, enchanted with { Charges = utility.MaxCharges });
            var two = items.Add(PlayerInventories.Flask, 1, 0, enchanted with { Charges = utility.MaxCharges });
            async Task Accept(InventoryItem flask, double at)
            {
                before.At = runtime.At = at; Game.AreaUpdateContext.Value = oldState;
                try { Assert.True(await Game.UseUtilityFlask("Whisperer", h.Wire, h.Ciphers, c, flask.X, flask, type, utility)); }
                finally { Game.AreaUpdateContext.Value = null; }
            }
            await Accept(one, 1); await Accept(two, 3);
            var first = c.UtilityFlaskEffects.Of(one.Id)!; var second = c.UtilityFlaskEffects.Of(two.Id)!;
            Assert.Equal(1, first.Started); Assert.Equal(3, second.Started);
            var cost = FlaskCharges.Of(utility.Charges, ItemModStats.Local(enchanted, Program.Items.Mods)).ChargesPerUse;
            int Charges(uint id) => items.Items(PlayerInventories.Flask).Single(f => f.Id == id).Item.Charges;
            Assert.Equal(utility.MaxCharges - cost, Charges(one.Id)); Assert.Equal(utility.MaxCharges - cost, Charges(two.Id));
            // Existing clocks freeze on pause; an unchanged clock must not shorten the accepted effect.
            double halfway = (first.Started + first.Ends) / 2;
            oldFast.Advance(halfway); oldFast = await before.Next(PlayerUpdateRole.Recovery);
            c.Clock.Stop(); oldFast.Advance(halfway); oldFast = await before.Next(PlayerUpdateRole.Recovery); c.Clock.Start();
            Assert.Same(first, c.UtilityFlaskEffects.Of(one.Id)); Assert.Same(second, c.UtilityFlaskEffects.Of(two.Id));
            Assert.DoesNotContain(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.End);
            // Related stat-dependent ground expiry is owned too; establish its published smoke/consecration state.
            c.Walk.Teleport(100, 100, halfway);
            var monster = new SpawnedMonster("flask smoke", new(100, 100, 0), 0, 0, 1000); c.Monsters[44] = monster;
            c.FlaskGrounds.Add(new(FlaskGrounds.Kind.SmokeCloud, 100, 100, 10, second.Ends));
            c.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 100, 100, 10, second.Ends));
            Game.AdvanceFlaskGrounds(c, oldState); Assert.Equal(80, Game.UnderBlind(c, 44, 100)); Assert.NotEmpty(c.FlaskGrounds.PlayerStats());
            await Game.PublishFlaskChanges("Whisperer", h.Wire, h.Ciphers, c);
            h.Wire.Drain(h.Receive); while (h.Wire.Plain.Reader.TryRead(out _)) { }
            await gate.WaitAsync(); held = true;
            oldFast.Advance(first.Ends); await oldFast.Observed.Task;
            lock (c.Monsters)
            {
                Assert.Null(c.UtilityFlaskEffects.Of(one.Id)); Assert.Same(second, c.UtilityFlaskEffects.Of(two.Id));
                var edge = Assert.Single(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.End);
                Assert.Equal(new(FlaskEnchantments.Trigger.End, one.Id, one.X, first.Activation), edge.Signal);
                Assert.NotEmpty(c.FlaskAutomation.Packets);
            }
            // Stop without releasing the old output. The other flask becomes due in the no-current-Game gap.
            oldOwner.Stop(ex => throw ex); before.At = runtime.At = second.Ends + .25;
            int receipts = c.FlaskAutomation.Packets.Count; long revision = c.FlaskAutomation.StatRevision;
            Game.AdvanceUtilityFlasks(c, oldState); Game.AdvanceFlaskGrounds(c, oldState);
            var oldPublish = Game.PublishFlaskChanges("Whisperer", h.Wire, h.Ciphers, c);
            Assert.True(oldPublish.IsCompleted); await oldPublish;
            Assert.Same(second, c.UtilityFlaskEffects.Of(two.Id)); Assert.Equal(receipts, c.FlaskAutomation.Packets.Count);
            Assert.Equal(revision, c.FlaskAutomation.StatRevision); Assert.NotEmpty(c.FlaskGrounds.PlayerStats());
            after.At = runtime.At;
            game.GetField("CombatLifetime", flags)!.SetValue(null, owner); game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
            Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
            currentState = c.PlayerUpdatesState!; Assert.NotSame(oldState, currentState);
            currentCompletion = (Task)currentState.GetType().GetField("Completion", flags)!.GetValue(currentState)!; after.Completion = currentCompletion;
            var fast = await after.Next(PlayerUpdateRole.Recovery);
            Assert.Same(second, c.UtilityFlaskEffects.Of(two.Id)); Assert.Equal(second.Ends, c.UtilityFlaskEffects.Of(two.Id)!.Ends);
            fast.Advance(after.At); await fast.Observed.Task;
            lock (c.Monsters)
            {
                Assert.Null(c.UtilityFlaskEffects.Of(two.Id));
                Assert.Equal(2, c.FlaskAutomation.Events.Count(e => e.Trigger == FlaskEnchantments.Trigger.End));
            }
            gate.Release(); held = false;
            await oldState.Completion; fast = await after.Next(PlayerUpdateRole.Recovery);
            await after.Next(PlayerUpdateRole.Player);
            foreach (var role in new[] { PlayerUpdateRole.MonsterAi, PlayerUpdateRole.MonsterRegeneration, PlayerUpdateRole.LivingCollisions,
                PlayerUpdateRole.Charges, PlayerUpdateRole.Fortification, PlayerUpdateRole.Rage }) await after.Next(role);
            var drain = await after.Next(PlayerUpdateRole.FlaskCharges); var ground = await after.Next(PlayerUpdateRole.FlaskGrounds);
            // Actual replacement ground producer removes expired smoke/consecration and publishes current state.
            ground.Advance(after.At); await after.Next(PlayerUpdateRole.FlaskGrounds);
            Assert.Equal(100, Call("UnderBlind", c, 44u, 100)); Assert.Empty(c.FlaskGrounds.PlayerStats());
            drain.Advance(after.At); drain = await after.Next(PlayerUpdateRole.FlaskCharges);
            var reusedOne = c.UtilityFlaskEffects.Of(one.Id)!; var reusedTwo = c.UtilityFlaskEffects.Of(two.Id)!;
            Assert.NotNull(reusedOne); Assert.NotNull(reusedTwo);
            Assert.Equal(after.At, reusedOne.Started); Assert.Equal(first.Ends - first.Started, reusedOne.Ends - reusedOne.Started, 8);
            Assert.Equal(second.Ends - second.Started, reusedTwo.Ends - reusedTwo.Started, 8);
            Assert.Equal(utility.MaxCharges - 2 * cost, Charges(one.Id)); Assert.Equal(utility.MaxCharges - 2 * cost, Charges(two.Id));
            Assert.True(reusedOne.Activation > first.Activation); Assert.True(reusedTwo.Activation > second.Activation);
            Assert.DoesNotContain(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.End);
            // A late old completion/expiry and a replayed old activation cannot remove/reuse the new effect.
            Game.AdvanceUtilityFlasks(c, oldState); Game.AdvanceFlaskGrounds(c, oldState);
            c.PlayerUpdatesWorker.Complete(oldOwner, null, oldState, oldState.Generation);
            c.FlaskAutomation.Enqueue(new(FlaskEnchantments.Trigger.End, one.Id, one.X, first.Activation));
            drain.Advance(after.At); drain = await after.Next(PlayerUpdateRole.FlaskCharges);
            fast.Advance(after.At); fast = await after.Next(PlayerUpdateRole.Recovery);
            Assert.Same(reusedOne, c.UtilityFlaskEffects.Of(one.Id)); Assert.Same(reusedTwo, c.UtilityFlaskEffects.Of(two.Id));
            Assert.Equal(utility.MaxCharges - 2 * cost, Charges(one.Id)); Assert.True((bool)Call("PlayerUpdatesCurrent", c, currentState)!);
            Assert.Equal(1, after.Count(PlayerUpdateRole.Player)); Assert.Equal(1, after.Count(PlayerUpdateRole.MonsterAi));
            var sent = new List<byte>(); while (h.Wire.Plain.Reader.TryRead(out var packet)) sent.AddRange(packet);
            int Occurrences(byte[] needle) { int count = 0, at = 0; while (at <= sent.Count - needle.Length)
                { int found = sent.ToArray().AsSpan(at).IndexOf(needle); if (found < 0) break; count++; at += found + needle.Length; } return count; }
            Assert.Equal(1, Occurrences(InstanceEntities.RemoveBuff(Program.PlayerId, first.Buff)));
            Assert.Equal(1, Occurrences(InstanceEntities.RemoveBuff(Program.PlayerId, second.Buff)));
            // An End edge cannot follow an item to another cell or survive removal of its triggering enchant.
            items.Change(PlayerInventories.Flask, one.Id, i => i with { Charges = utility.MaxCharges });
            items.Change(PlayerInventories.Flask, two.Id, i => i with { Charges = utility.MaxCharges });
            fast.Advance(Math.Max(reusedOne.Ends, reusedTwo.Ends) + .25); fast = await after.Next(PlayerUpdateRole.Recovery);
            Assert.Equal(2, c.FlaskAutomation.Events.Count(e => e.Trigger == FlaskEnchantments.Trigger.End));
            var moved = items.Remove(PlayerInventories.Flask, one.Id)!; items.Put(PlayerInventories.Flask, moved with { X = 2 });
            items.Change(PlayerInventories.Flask, two.Id, i => i with { Mods = [.. i.Mods.Where(m => m.Section != ModSection.Enchant)] });
            drain.Advance(after.At); drain = await after.Next(PlayerUpdateRole.FlaskCharges);
            Assert.Equal(utility.MaxCharges, Charges(one.Id)); Assert.Equal(utility.MaxCharges, Charges(two.Id));
            Assert.Null(c.UtilityFlaskEffects.Of(one.Id)); Assert.Null(c.UtilityFlaskEffects.Of(two.Id));
            // Replacing the original item in the same cell also cannot transfer its old activation to a new flask.
            var removed = items.Remove(PlayerInventories.Flask, two.Id)!;
            var replacement = items.Add(PlayerInventories.Flask, two.X, two.Y, enchanted with { Charges = utility.MaxCharges });
            c.FlaskAutomation.Enqueue(new(FlaskEnchantments.Trigger.End, removed.Id, removed.X, reusedTwo.Activation));
            drain.Advance(after.At); await after.Next(PlayerUpdateRole.FlaskCharges);
            Assert.Equal(utility.MaxCharges, Charges(replacement.Id)); Assert.Null(c.UtilityFlaskEffects.Of(replacement.Id));
            // Main's stationary ground and linger use this same replacement-owned ground step.
            c.Walk.Teleport(100, 100, after.At); c.FlaskGrounds.Configure(12, 2000);
            Call("AdvanceFlaskGrounds", c, currentState); Assert.NotEmpty(c.FlaskGrounds.PlayerStats());
            c.FlaskGrounds.Configure(0, 2000); Call("AdvanceFlaskGrounds", c, currentState);
            Assert.NotEmpty(c.FlaskGrounds.PlayerStats());
            after.At += 2; Call("AdvanceFlaskGrounds", c, currentState); Assert.Empty(c.FlaskGrounds.PlayerStats());
            await (Task)Call("PublishFlaskChanges", "Whisperer", h.Wire, h.Ciphers, c)!;

        }
        finally
        {
            c.Closed = true; oldOwner.Stop(_ => { }); owner.Stop(_ => { }); if (held) gate.Release();
            if (oldState is not null) await oldState.Completion; if (currentCompletion is not null) await currentCompletion;
            Game.CombatLifetime = previousRules; Game.PlayerUpdatesSchedule = previousSchedule; Program.SetGameLogic(previousLogic); context.Unload();
        }
    }

    // The real charge/send boundary, not a synthetic lifetime facade or a manually inserted recovery.
    static async Task RecoveryAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var previousLogic = Program.Logic; var previousRules = Game.CombatLifetime; var previousSchedule = Game.PlayerUpdatesSchedule;
        foreach (string scenario in new[] { "ordinary", "copied", "instantMana", "instantLife", "atEnd", "enduring", "hybrid" })
        {
            var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
            var context = new AssemblyLoadContext("Whisperer accepted recovery " + scenario, isCollectible: true);
            context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            var assembly = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, assembly);
            var game = assembly.GetType("Game")!;
            object? Call(string name, params object?[] args) => game.GetMethods(flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
            var c = h.NewConnection("recovery" + Guid.NewGuid().ToString("N"), optional: false); c.MainAscendancy = new();
            c.PlayerSent = true; c.Wire = ("Whisperer", h.Wire, h.Ciphers); c.AiEnabled = false;
            c.PlayerStats[Key("mana_regeneration_rate_per_minute_%")] = -PanelStats.BaseManaRegenPctPerMinute;
            c.PlayerStats[Key("base_life_regeneration_rate_per_minute")] = 0;
            c.PlayerStats[Key("life_regeneration_rate_per_minute_%")] = 0;
            var runtime = new ManualRuntime { At = 10 }; c.ProjectileRuntimeState = runtime;
            var before = new ManualUpdates(runtime) { At = 10 }; var after = new ManualUpdates(runtime) { At = 10 };
            PlayerUpdatesState? oldState = null; Task? currentCompletion = null; Task? use = null;
            var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1)); bool held = false;
            try
            {
                Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
                Program.ActivatePlayerUpdates(c); oldState = Assert.IsType<PlayerUpdatesState>(c.PlayerUpdatesState); before.Completion = oldState.Completion;
                await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
                var type = Program.Items.Find(scenario == "instantLife" ? "Small Life Flask" : scenario == "hybrid" ? "Small Hybrid Flask" : "Small Mana Flask")!;
                var item = ItemCatalog.Create(type) with { Charges = scenario == "instantLife" ? 21 : 24 };
                string? localFlag = scenario.StartsWith("instant", StringComparison.Ordinal) ? "local_flask_recovers_instantly"
                    : scenario == "atEnd" ? "local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect"
                    : scenario == "enduring" ? "local_flask_effect_not_removed_at_full_mana" : null;
                if (localFlag is not null)
                    item = Program.Items.Mods.All.Where(m => m.GenerationType is 1 or 2 && m.WeightFor(type.Tags) > 0)
                        .Select(m => item with { Mods = [.. item.Mods, m.With(ModSection.Explicit, m.MaxValues(), new Random(1))] })
                        .First(i => ItemModStats.Local(i, Program.Items.Mods).GetValueOrDefault(localFlag) > 0);
                var baseUse = Program.Items.FlaskOf(type)!;
                if (scenario == "hybrid") item = item with { Charges = baseUse.MaxCharges };
                var items = Game.ItemsOf(c);
                foreach (var initial in items.Items(PlayerInventories.Flask).ToArray()) items.Remove(PlayerInventories.Flask, initial.Id);
                var flask = items.Add(PlayerInventories.Flask, 0, 0, item);
                c.PlayerLife = scenario == "instantLife" ? c.PlayerMaxLife - 5 : c.PlayerMaxLife - 100;
                c.PlayerMana = scenario == "instantMana" ? c.Pools.Mana - 5 : 0;
                if (scenario is "copied" or "atEnd") c.PlayerStats[Key("non_instant_mana_recovery_from_flasks_also_recovers_life")] = 1;
                if (scenario == "hybrid") c.PlayerStats[Key("flask_mana_recovery_rate_+%")] = 100;
                if (scenario == "instantMana") c.PlayerStats[Key(ManaRecoveryRules.RagePer200)] = 10;
                var player = Game.CharacterPlayerStats(c);
                int Get(string name) => Program.Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
                var local = ItemModStats.Local(item, Program.Items.Mods);
                var accepted = FlaskRecovery.For(baseUse, item, local, Get, c.PlayerLife, c.PlayerMaxLife, c.Pools.Level);
                var instant = FlaskRecovery.Instant(accepted, c.PlayerLife, c.PlayerMana, c.PlayerMaxLife, Game.UnreservedMana(c))!.Value;
                var charge = FlaskCharges.Of(baseUse, local, Get);
                var oldResource = Game.ResourceEffectsOf(c);
                var leech = Game.LeechOf(c);
                if (scenario == "instantMana")
                {
                    oldResource.Mana.Spent(199, 10); oldResource.Mana.Hit(10, 4, 20);
                    leech.Mana.Configure(10, new(1, 1000, 1)); leech.Mana.Grant(10, 100, 1000, 100);
                }
                Assert.Equal(0, ManaRate(c));
                while (h.Wire.Plain.Reader.TryRead(out _)) { } h.Wire.Drain(h.Receive);
                await gate.WaitAsync(); held = true;
                before.At = runtime.At = after.At = 11;
                Game.AreaUpdateContext.Value = oldState;
                try { use = Game.UseFlask("Whisperer", h.Wire, h.Ciphers, c, 0); }
                finally { Game.AreaUpdateContext.Value = null; }
                Assert.False(use.IsCompleted); Assert.Equal(item.Charges - charge.ChargesPerUse, items.Items(PlayerInventories.Flask).Single().Item.Charges);
                if (scenario == "copied") c.PlayerStats[Key("non_instant_mana_recovery_from_flasks_also_recovers_life")] = 0;
                if (scenario == "instantMana")
                {
                    Assert.Equal(instant.Mana, c.PlayerMana); Assert.Equal(0, leech.Mana.Count);
                    var settled = oldResource.Mana.Capture(11); Assert.Equal(199, settled.Spent); Assert.Equal(0m, settled.Pending);
                    Assert.Equal(.6m, Assert.Single(settled.Recoup).Remaining); // Future Recoup survives full-pool settlement.
                }
                var native = c.FlaskAutomation.Packets.Where(p => p.Log?.StartsWith("0x01c1 player buff", StringComparison.Ordinal) == true)
                    .Select(p => p.Packet.ToArray()).ToArray();
                Assert.Equal(accepted.Seconds > 0 ? scenario == "hybrid" ? 2 : 1 : 0, native.Length);
                var inventory = Assert.Single(c.FlaskAutomation.Packets,
                    p => p.Log?.StartsWith("0x00bf flask", StringComparison.Ordinal) == true).Packet.ToArray();
                int pending = c.FlaskAutomation.Packets.Count;
                oldOwner.Stop(ex => throw ex); await oldState.Completion;
                game.GetField("CombatLifetime", flags)!.SetValue(null, owner); game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
                Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                var state = c.PlayerUpdatesState!; currentCompletion = (Task)state.GetType().GetField("Completion", flags)!.GetValue(state)!; after.Completion = currentCompletion;
                var fast = await after.Next(PlayerUpdateRole.Recovery);
                gate.Release(); held = false; await use;
                Assert.Equal(pending, c.FlaskAutomation.Packets.Count); // The late old sender cannot consume current receipts.
                await after.Next(PlayerUpdateRole.Player); await after.OtherRoles();
                // Drive the actual replacement writer: pending native output first, then accepted recovery progress.
                fast.Advance(11); fast = await after.Next(PlayerUpdateRole.Recovery);
                Assert.Equal(instant.Life, c.PlayerLife); Assert.Equal(instant.Mana, c.PlayerMana);
                Assert.Single(c.FlaskAutomation.Events, e => e.Trigger == FlaskEnchantments.Trigger.Adjacent && e.Item == flask.Id);
                Assert.Equal(scenario is "instantLife" or "hybrid" ? 1 : 0, c.FlaskAutomation.Events.Count(e => e.Trigger == FlaskEnchantments.Trigger.LifeFlask));
                if (accepted.Seconds > 0)
                {
                    if (scenario == "atEnd") Assert.Equal(0u, c.PlayerMana);
                    if (scenario == "enduring")
                    {
                        c.PlayerMana = c.Pools.Mana;
                        fast.Advance(11 + accepted.Seconds / 2); fast = await after.Next(PlayerUpdateRole.Recovery);
                        Assert.True(c.FlaskEffects.Any); // Full Mana does not discard an accepted Enduring effect.
                    }
                    fast.Advance(11 + accepted.Seconds); fast = await after.Next(PlayerUpdateRole.Recovery);
                    uint expectedMana = scenario == "enduring" ? (uint)Game.UnreservedMana(c)
                        : (uint)Math.Min(Game.UnreservedMana(c), instant.Mana + accepted.Mana + accepted.ManaAtEnd);
                    uint expectedLife = (uint)Math.Min(c.PlayerMaxLife, instant.Life + accepted.Life + (scenario == "copied" ? accepted.Mana : 0));
                    Assert.Equal(expectedMana, c.PlayerMana); Assert.Equal(expectedLife, c.PlayerLife);
                    if (scenario == "ordinary") Assert.Equal(50u, c.PlayerMana);
                }
                Assert.Equal(item.Charges - charge.ChargesPerUse, items.Items(PlayerInventories.Flask).Single().Item.Charges);
                await (Task)Call("PublishFlaskChanges", "Whisperer", h.Wire, h.Ciphers, c)!;
                Assert.False(c.Closed); Assert.False(c.LeavingArea);
                var packets = new List<byte>(); while (h.Wire.Plain.Reader.TryRead(out var packet)) packets.AddRange(packet);
                byte[] sent = [.. packets];
                int Count(byte[] needle) { int count = 0, at = 0; while (at <= sent.Length - needle.Length)
                    { int found = sent.AsSpan(at).IndexOf(needle); if (found < 0) break; count++; at += found + needle.Length; } return count; }
                Assert.Equal(1, Count(InstanceItems.ItemUsed(item.TypeHash, PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id)));
                Assert.Equal(1, Count(inventory));
                foreach (var buff in native) Assert.Equal(1, Count(buff));
                // Late old production continuations cannot reinstall the already consumed use or consume output.
                var stale = Game.PublishFlaskChanges("Whisperer", h.Wire, h.Ciphers, c); Assert.True(stale.IsCompleted); await stale;
                Assert.Empty(c.FlaskAutomation.Packets);
                Assert.Empty(c.FlaskEffects.End(_ => true));
            }
            finally
            {
                c.Closed = true; oldOwner.Stop(_ => { }); owner.Stop(_ => { }); if (held) gate.Release();
                if (use is not null) await use; if (oldState is not null) await oldState.Completion; if (currentCompletion is not null) await currentCompletion;
                Game.CombatLifetime = previousRules; Game.PlayerUpdatesSchedule = previousSchedule; Program.SetGameLogic(previousLogic); context.Unload();
            }
        }
    }

    // Actual Commands builders bind different Game assemblies; only the loader wave is manually scheduled.
    static async Task CommandsAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var previousLogic = Program.Logic; var previousRules = Game.CombatLifetime;
        var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
        var context = new AssemblyLoadContext("Whisperer current slash registration", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location);
        var commandsAssembly = context.LoadFromAssemblyPath(typeof(GameServer.Modules.M_commands.CommandsModule).Assembly.Location);
        Assert.NotSame(typeof(Game).Assembly, assembly);
        Assert.NotSame(typeof(GameServer.Modules.M_commands.CommandsModule).Assembly, commandsAssembly);
        var oldModule = new GameServer.Modules.M_commands.CommandsModule();
        var currentModule = (IModule)Activator.CreateInstance(commandsAssembly.GetType("GameServer.Modules.M_commands.CommandsModule")!)!;
        var c = h.NewConnection("commands" + Guid.NewGuid().ToString("N"), optional: false); c.MainAscendancy = new();
        c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string> { "Game", "Commands" });
        c.PlayerSent = true; c.Wire = ("Whisperer", h.Wire, h.Ciphers); c.ProjectileRuntimeState = new ManualRuntime();
        var area = c.Layout; var instance = c.Instance; uint ticket = c.Ticket; int waves = 0;
        Assembly HandlerAssembly(ChatCommands commands)
        {
            var entries = (Dictionary<string, (string, Func<string[], Task>, string[])>)typeof(ChatCommands).GetField("_commands", flags)!.GetValue(commands)!;
            return entries["set_stat"].Item2.Method.Module.Assembly;
        }
        static (ushort Id, byte[] Bytes) Chat(string text) => (InstanceChat.ChatId,
            new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray());
        async Task Dispatch(IGameLogic logic, params string[] text) =>
            await logic.OnClientMessages("Whisperer", h.Wire, h.Ciphers, text.Select(Chat).ToArray(), [], c, 0);
        try
        {
            Game.CombatLifetime = oldOwner; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
            oldModule.Load(null!); // The actual module's builder, not a substitute set_stat handler.
            // Route this one test reload to its isolated assemblies rather than the process-wide production host.
            // Replacement Game is installed before Commands registers, exactly as the real dependency wave.
            using var wave = GameEvents.CommandsBuilding.Add("Commands", (commands, _, _, _, target) =>
                commands.Add("module_reload", "/module_reload [id]", async args =>
                {
                    Assert.Equal("Game", Assert.Single(args)); waves++;
                    var executing = target.Commands;
                    oldModule.Dispose(); oldOwner.Stop(ex => throw ex);
                    assembly.GetType("Game")!.GetField("CombatLifetime", flags)!.SetValue(null, owner);
                    Program.SetGameLogic((IGameLogic)Activator.CreateInstance(assembly.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                    Assert.Same(executing, target.Commands); // SetGameLogic is too early to rebuild dependent commands.
                    currentModule.Load(null!);
                    await target.Commands!.Reply("Module Game reloaded (/module for the list).");
                }));
            c.Commands = Program.Commands("Whisperer", h.Wire, h.Ciphers, c);
            Assert.Same(typeof(GameServer.Modules.M_commands.CommandsModule).Assembly, HandlerAssembly(c.Commands));
            await Dispatch(Program.Logic, "/set_stat strength 777");
            Assert.Contains((Key("strength"), 777), PlayerStatRows(h.Wire.Drain(h.Receive)));
            var cached = c.Commands!;
            var batchLogic = Program.Logic;
            await Dispatch(batchLogic, "/module_reload Game", "/set_stat strength 778");
            var sameBatch = h.Wire.Drain(h.Receive);
            Has(sameBatch, InstanceChat.ChatLine("Module Game reloaded (/module for the list)."));
            Assert.Equal(1, waves); Assert.NotSame(batchLogic, Program.Logic);
            Assert.Equal(778, c.PlayerStats[Key("strength")]);
            Assert.Contains((Key("strength"), 778), PlayerStatRows(sameBatch));
            Assert.NotSame(cached, c.Commands); Assert.Same(commandsAssembly, HandlerAssembly(c.Commands!));
            // A retained old closure still cannot publish: refreshing never weakens old Game's guards.
            Assert.True(await cached.RunAsync("/set_stat strength 779"));
            Assert.Equal(779, c.PlayerStats[Key("strength")]); Assert.Empty(h.Wire.Drain(h.Receive));
            await Dispatch(Program.Logic, "/set_stat strength 780");
            Assert.Contains((Key("strength"), 780), PlayerStatRows(h.Wire.Drain(h.Receive)));
            Assert.Same(commandsAssembly, HandlerAssembly(c.Commands!));
            Assert.NotNull(c.Commands!.ModCommand); Assert.NotNull(c.Commands.ModUsages);
            // Same ordinary connection, now a league without Commands: current factory must apply the live filter.
            c.LeagueModule = new(new League("WhispererTest", "Standard", false, false), new HashSet<string> { "Game" });
            await Dispatch(Program.Logic, "/set_stat strength 781");
            Has(h.Wire.Drain(h.Receive), InstanceChat.ChatLine("Unknown command /set_stat. Type /commands (or /com) for the list."));
            Assert.Equal(780, c.PlayerStats[Key("strength")]); Assert.DoesNotContain(c.Commands!.Usages, u => u.Name == "set_stat");
            Assert.Contains(c.Commands.Usages, u => u.Name == "module_reload");
            Assert.NotNull(c.Commands.ModCommand); Assert.NotNull(c.Commands.ModUsages);
            await c.Commands.Send(InstanceChat.MessageBox("Gateway send sink"), "command sink control");
            Has(h.Wire.Drain(h.Receive), InstanceChat.MessageBox("Gateway send sink"));
            Assert.False(c.Closed); Assert.False(c.LeavingArea); Assert.True(c.PlayerSent);
            Assert.Same(area, c.Layout); Assert.Same(instance, c.Instance); Assert.Equal(ticket, c.Ticket);
        }
        finally
        {
            c.Closed = true; oldModule.Dispose(); ((IDisposable)currentModule).Dispose();
            oldOwner.Stop(_ => { }); owner.Stop(_ => { });
            Game.CombatLifetime = previousRules; Program.SetGameLogic(previousLogic); context.Unload();
        }
    }

    // A real Block root must retain accepted recipients and finish its unprocessed/Adjacent recipients on reload.
    static async Task TriggerChainAcrossGameAssemblies(Harness h)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var previousLogic = Program.Logic; var previousRules = Game.CombatLifetime; var previousSchedule = Game.PlayerUpdatesSchedule;
        var acceptedLogs = new List<string>();
        foreach (var scenario in new[] { (Reload: false, BeforePayment: false), (Reload: true, BeforePayment: false), (Reload: true, BeforePayment: true) })
        {
            var oldOwner = new ModuleLifetime("Game"); var owner = new ModuleLifetime("Game");
            var context = new AssemblyLoadContext("Whisperer trigger-chain handover", isCollectible: true);
            context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            var replacement = context.LoadFromAssemblyPath(typeof(Game).Assembly.Location); Assert.NotSame(typeof(Game).Assembly, replacement);
            var c = h.NewConnection("chain" + Guid.NewGuid().ToString("N"), optional: false); c.MainAscendancy = new();
            c.PlayerSent = true; c.Wire = ("Whisperer", h.Wire, h.Ciphers); c.AiEnabled = false;
            c.PlayerStats[Key("flask_duration_+%")] = 20;
            var runtime = new ManualRuntime { At = 10 }; c.ProjectileRuntimeState = runtime;
            var before = new ManualUpdates(runtime) { At = 10 }; var after = new ManualUpdates(runtime) { At = 10 };
            PlayerUpdatesState? oldState = null; object? state = null; Task? completion = null; Task? drain = null;
            Type game = typeof(Game); ManualUpdates schedule = before;
            object? Call(string name, params object?[] args) => game.GetMethods(flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
            async Task CurrentDrain()
            {
                var local = game.GetField("AreaUpdateContext", flags)!.GetValue(null)!; var value = local.GetType().GetProperty("Value")!;
                object? prior = value.GetValue(local); value.SetValue(local, state);
                try { await (Task)Call("DrainFlaskTriggers", "Whisperer", h.Wire, h.Ciphers, c)!; }
                finally { value.SetValue(local, prior); }
            }
            var gate = Program.SendGates.GetValue(h.Ciphers, _ => new(1, 1)); bool held = false, barHeld = false;
            var originalOutput = Console.Out; using var output = new StringWriter(); Console.SetOut(TextWriter.Synchronized(output));
            try
            {
                Game.CombatLifetime = oldOwner; Game.PlayerUpdatesSchedule = before; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(oldOwner));
                Program.ActivatePlayerUpdates(c); oldState = Assert.IsType<PlayerUpdatesState>(c.PlayerUpdatesState); before.Completion = oldState.Completion;
                var fast = await before.Next(PlayerUpdateRole.Recovery); await before.Next(PlayerUpdateRole.Player); await before.OtherRoles();
                state = oldState;
                var type = Program.Items.Find("Quicksilver Flask")!; var utility = Program.Items.UtilityFlaskOf(type)!;
                var plain = ItemCatalog.Create(type) with { Charges = 60 };
                Item Enchanted(FlaskEnchantments.Trigger trigger) => Program.Items.Mods.All.Where(m => m.GenerationType == 22 && m.WeightFor(type.Tags) > 0)
                    .Select(m => plain with { Mods = [.. plain.Mods, m.Roll(ModSection.Enchant, new Random(1))] })
                    .Single(i => FlaskEnchantments.Matches(ItemModStats.Local(i, Program.Items.Mods), trigger, 2, 1, false));
                var block = Enchanted(FlaskEnchantments.Trigger.Block); var adjacent = Enchanted(FlaskEnchantments.Trigger.Adjacent);
                var end = Enchanted(FlaskEnchantments.Trigger.End);
                var items = Game.ItemsOf(c); foreach (var initial in items.Items(PlayerInventories.Flask).ToArray()) items.Remove(PlayerInventories.Flask, initial.Id);
                InventoryItem[] flasks = [items.Add(PlayerInventories.Flask, 0, 0, block), items.Add(PlayerInventories.Flask, 1, 0, block),
                    items.Add(PlayerInventories.Flask, 2, 0, adjacent), items.Add(PlayerInventories.Flask, 3, 0, adjacent)];
                int Charges(int slot) => items.Items(PlayerInventories.Flask).Single(f => f.Id == flasks[slot].Id).Item.Charges;
                Game.QueueFlaskTrigger(c, FlaskEnchantments.Trigger.Block); Assert.Single(c.FlaskAutomation.Events);
                await gate.WaitAsync(); held = true;
                if (scenario.BeforePayment) { await c.FlaskBarGate.WaitAsync(); barHeld = true; }
                Game.AreaUpdateContext.Value = oldState;
                try { drain = Game.DrainFlaskTriggers("Whisperer", h.Wire, h.Ciphers, c); }
                finally { Game.AreaUpdateContext.Value = null; }
                Assert.False(drain.IsCompleted); Assert.Equal(scenario.BeforePayment ? 60 : 30, Charges(0));
                var first = c.UtilityFlaskEffects.Of(flasks[0].Id);
                if (!scenario.BeforePayment) { Assert.NotNull(first); Assert.Equal(1, first.Activation); Assert.Equal(7.2, first.Ends - first.Started, 8); }
                if (scenario.Reload)
                {
                    oldOwner.Stop(ex => throw ex); await oldState.Completion;
                    if (barHeld) { c.FlaskBarGate.Release(); barHeld = false; await drain; Assert.Equal(60, Charges(0)); }
                    game = replacement.GetType("Game")!; schedule = after;
                    game.GetField("CombatLifetime", flags)!.SetValue(null, owner); game.GetField("PlayerUpdatesSchedule", flags)!.SetValue(null, after);
                    Program.SetGameLogic((IGameLogic)Activator.CreateInstance(replacement.GetType("GameServer.Modules.Game.GameLogic")!, flags, null, [owner], null)!);
                    state = c.PlayerUpdatesState!; completion = (Task)state.GetType().GetField("Completion", flags)!.GetValue(state)!; after.Completion = completion;
                    fast = await after.Next(PlayerUpdateRole.Recovery);
                }
                gate.Release(); held = false; await drain;
                if (scenario.Reload) { await after.Next(PlayerUpdateRole.Player); await after.OtherRoles(); }
                await CurrentDrain();
                Assert.Equal(30, Charges(0)); // R5 RED: reload replayed Block, paying this same recipient twice (0).
                Assert.Equal(1, c.UtilityFlaskEffects.Of(flasks[0].Id)!.Activation);
                foreach (int slot in new[] { 1, 2, 3 }) Assert.Equal(30, Charges(slot));
                Assert.Empty(c.FlaskAutomation.Events); // The unfinished Block recipient and Adjacent pair both completed.
                var committed = flasks.Select(f => c.UtilityFlaskEffects.Of(f.Id)!).ToArray();
                await CurrentDrain(); foreach (int slot in Enumerable.Range(0, 4)) Assert.Equal(30, Charges(slot));
                // A later actual End has a fresh chain and can legitimately use the first item again.
                items.Change(PlayerInventories.Flask, flasks[0].Id, i => i with { Mods = end.Mods });
                fast.Advance(committed.Max(e => e.Ends)); fast = await schedule.Next(PlayerUpdateRole.Recovery);
                Assert.Equal(4, c.FlaskAutomation.Events.Count(e => e.Trigger == FlaskEnchantments.Trigger.End));
                await CurrentDrain(); Assert.Equal(0, Charges(0)); Assert.Equal(30, Charges(1));
                Assert.True(c.UtilityFlaskEffects.Of(flasks[0].Id)!.Activation > committed[0].Activation);
                Assert.Empty(c.FlaskAutomation.Events);
                // A distinct later root drives another finite Adjacent chain, not a retained 'used forever' set.
                Call("QueueFlaskTrigger", c, FlaskEnchantments.Trigger.Adjacent, flasks[1].Id, flasks[1].X);
                await CurrentDrain(); Assert.Equal(0, Charges(2)); Assert.Equal(0, Charges(3)); Assert.Equal(30, Charges(1));
                Assert.Empty(c.FlaskAutomation.Events); Assert.False(c.Closed); Assert.False(c.LeavingArea);
                // The real Iron Flask's paid Ward restoration keeps its observable main log too.
                var ironType = Program.Items.Find("Iron Flask")!; var ironUse = Program.Items.UtilityFlaskOf(ironType)!;
                var iron = items.Add(PlayerInventories.Flask, 4, 0, ItemCatalog.Create(ironType) with { Charges = ironUse.MaxCharges });
                Assert.True(ItemModStats.Local(iron.Item, Program.Items.Mods).GetValueOrDefault("local_flask_restore_ward") > 0);
                c.PlayerStats[Key("base_ward")] = 100; c.Ward.SetMaximum(100); c.Ward.Damage(1, schedule.At, _ => 0); Assert.True(c.Ward.Broken);
                var localContext = game.GetField("AreaUpdateContext", flags)!.GetValue(null)!;
                var localValue = localContext.GetType().GetProperty("Value")!; localValue.SetValue(localContext, state);
                try { Assert.True(await (Task<bool>)Call("UseUtilityFlask", "Whisperer", h.Wire, h.Ciphers, c, 4u, iron, ironType, ironUse)!); }
                finally { localValue.SetValue(localContext, null); }
                Assert.False(c.Ward.Broken);
                acceptedLogs.Add(output.ToString());
            }
            finally
            {
                Console.SetOut(originalOutput);
                c.Closed = true; oldOwner.Stop(_ => { }); owner.Stop(_ => { }); if (barHeld) c.FlaskBarGate.Release(); if (held) gate.Release();
                if (drain is not null) await drain; if (oldState is not null) await oldState.Completion; if (completion is not null) await completion;
                Game.CombatLifetime = previousRules; Game.PlayerUpdatesSchedule = previousSchedule; Program.SetGameLogic(previousLogic); context.Unload();
            }
        }
        foreach (string log in acceptedLogs)
        {
            var lines = log.Split('\n');
            Assert.Equal(7, lines.Count(l => l.Contains("<= 0x0070 use flask", StringComparison.Ordinal)
                && l.Contains("flask_utility_sprint for 7.2 s:", StringComparison.Ordinal)));
            Assert.Equal(2, lines.Count(l => l.Contains("0x01c1 player buff: Quicksilver Flask (flask 0), 7.2 s", StringComparison.Ordinal)));
            Assert.Single(lines, l => l.Contains("0x01c2 player buff: Quicksilver Flask (flask 1) effect ends (its time is up)", StringComparison.Ordinal));
            Assert.Single(lines, l => l.Contains("Ward restored on paid Iron Flask:", StringComparison.Ordinal));
        }
    }

    static List<(uint Key, int Value)> PlayerStatRows(byte[] packet)
    {
        var result = new List<(uint, int)>(); byte[] header = InstanceEntities.StatsUpdate(Program.PlayerId, [])[..12];
        int at = 0;
        while (at < packet.Length && packet.AsSpan(at).IndexOf(header) is int found && found >= 0)
        {
            at += found + header.Length; int count = (int)Read(false);
            for (int i = 0; i < count; i++) result.Add((Read(false), (int)Read(true)));
        }
        return result;
        uint Read(bool signed)
        {
            uint first = packet[at++]; int length = first < 128 ? 1 : first < 192 ? 2 : first < 224 ? 3 : first < 240 ? 4 : 5;
            int bits = length == 5 ? 32 : 8 * length - length;
            uint value = first & (length == 5 ? 0u : (uint)((1 << (8 - length)) - 1));
            for (int i = 1; i < length; i++) value = (value << 8) | packet[at++];
            if (signed && bits < 32 && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
            return value;
        }
    }

    sealed class FixedDraw(int value) : Random { public override int Next(int maxValue) => value; public override double NextDouble() => 0; }
    sealed class HighDraw : Random { public override int Next(int maxValue) => maxValue - 1; public override double NextDouble() => 0; }
    sealed class ManualRuntime : IProjectileRuntime
    {
        public double At; public bool Active { get; private set; } = true;
        public double NowTicks => At * 120000; public void Invalidate() => Active = false;
    }
    sealed class ManualUpdates(ManualRuntime runtime) : IPlayerUpdateSchedule
    {
        readonly ConcurrentDictionary<PlayerUpdateRole, Channel<Frame>> queues = new();
        readonly ConcurrentDictionary<PlayerUpdateRole, int> waits = new();
        readonly ManualRuntime runtime = runtime;
        internal Task? Completion;
        internal double At;
        internal int FastWaits => Count(PlayerUpdateRole.Recovery);
        internal int Count(PlayerUpdateRole role) => waits.GetValueOrDefault(role);
        Frame? observing;
        Channel<Frame> Queue(PlayerUpdateRole role) => queues.GetOrAdd(role, _ => Channel.CreateUnbounded<Frame>());
        internal sealed class Frame(ManualUpdates schedule)
        {
            readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Wait(CancellationToken stop) => release.Task.WaitAsync(stop);
            internal void Advance(double at)
            { schedule.At = at; schedule.runtime.At = at; schedule.observing = this; release.SetResult(); }
        }
        public double Now(ClientState conn) { observing?.Observed.TrySetResult(); return At; }
        public Task Wait(int milliseconds, CancellationToken stopping) =>
            Wait(milliseconds == Game.RecoveryStepMs ? PlayerUpdateRole.Recovery : PlayerUpdateRole.Player, milliseconds, stopping);
        public Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping)
        {
            var frame = new Frame(this); waits.AddOrUpdate(role, 1, (_, n) => n + 1);
            Queue(role).Writer.TryWrite(frame); return frame.Wait(stopping);
        }
        internal Task<Frame> Next(int milliseconds) => Next(milliseconds == Game.RecoveryStepMs ? PlayerUpdateRole.Recovery : PlayerUpdateRole.Player);
        internal async Task<Frame> Next(PlayerUpdateRole role)
        {
            var read = Queue(role).Reader.ReadAsync().AsTask();
            if (Completion is not null && await Task.WhenAny(read, Completion) == Completion)
            { await Completion; throw new InvalidOperationException("Area updates stopped before " + role); }
            return await read;
        }
        internal async Task OtherRoles()
        {
            foreach (var role in new[] { PlayerUpdateRole.MonsterAi, PlayerUpdateRole.MonsterRegeneration,
                PlayerUpdateRole.LivingCollisions, PlayerUpdateRole.Charges, PlayerUpdateRole.Fortification,
                PlayerUpdateRole.Rage, PlayerUpdateRole.FlaskCharges, PlayerUpdateRole.FlaskGrounds,
                PlayerUpdateRole.Minions, PlayerUpdateRole.Footsteps }) await Next(role);
        }
    }
    sealed class CaptureStream : NetworkStream
    {
        readonly MemoryStream bytes = new(); readonly Socket peer;
        readonly ConnectionCiphers observer = new(new byte[64], true);
        internal readonly Channel<byte[]> Plain = Channel.CreateUnbounded<byte[]>();
        internal Func<bool>? Hold;
        internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        static (Socket Writer, Socket Peer) Pair()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var writer = new Socket(SocketType.Stream, ProtocolType.Tcp); writer.Connect(listener.LocalEndpoint);
            return (writer, listener.AcceptSocket());
        }
        public CaptureStream() : this(Pair()) { }
        CaptureStream((Socket Writer, Socket Peer) pair) : base(pair.Writer, ownsSocket: true) => peer = pair.Peer;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            bytes.Write(buffer.Span); var plain = buffer.ToArray(); observer.Receive.Process(plain); Plain.Writer.TryWrite(plain);
            return Hold?.Invoke() == true && Held.TrySetResult() ? new(Release.Task) : ValueTask.CompletedTask;
        }
        public byte[] Drain(ConnectionCiphers receive) { var result = bytes.ToArray(); bytes.SetLength(0); receive.Receive.Process(result); return result; }
        protected override void Dispose(bool disposing) { if (disposing) { peer.Dispose(); bytes.Dispose(); } base.Dispose(disposing); }
    }
    sealed class Harness : IDisposable
    {
        const string Flag = "Game_VERIFIED_WHISPERER";
        readonly string? oldFlag = Environment.GetEnvironmentVariable(Flag);
        readonly string path = Path.Combine(Path.GetTempPath(), "whisperer-" + Guid.NewGuid().ToString("N") + ".db");
        readonly IGameLogic? oldLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? oldRules = Game.CombatLifetime;
        readonly IPlayerUpdateSchedule oldSchedule = Game.PlayerUpdatesSchedule;
        readonly IDisposable registration;
        readonly List<ClientState> connections = [];
        internal readonly ModuleLifetime Rules = new("Game"), Provider = new("PhreciaAscendancies");
        internal readonly IAscendancyFamilyPolicy Policy;
        internal readonly AscendancyCatalog.Definition Definition;
        internal readonly CaptureStream Wire = new();
        internal readonly ConnectionCiphers Ciphers = new(new byte[64], false), Receive = new(new byte[64], true);
        internal readonly ManualRuntime Time = new();
        internal readonly ClientState Conn;
        internal PlayerInventories Items => Game.ItemsOf(Conn);
        internal (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Fireball =>
            Game.GemSkillEffects(Conn).Single(g => Game.SkillsFor(Conn).EffectId(g.Effect) == "Fireball");
        readonly IDisposable questTables = Quests.Register("QuestObjects", GameServer.Modules.M_questobjects.QuestObjectsModule.Acts());
        internal Harness(AscendancyCatalog.Definition definition, byte classRow = 3)
        {
            Saves.Open(path); Definition = definition;
            Game.CombatLifetime = Rules; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            var assembly = typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly;
            Policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(assembly.GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy")!, true)!;
            OptIn(true); registration = AscendancyPolicies.Register(Provider, Policy);
            Conn = NewConnection("Whisperer" + Guid.NewGuid().ToString("N"), classRow: classRow); Conn.ProjectileRuntimeState = Time;
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [new(SocketColour.White, gem)], Links = [1] });
            Conn.PlayerStats[Key("virtual_intelligence_for_requirements")] = 1000;
            Conn.PlayerStats[Key("no_inherent_rage_loss")] = 1;
        }
        internal void OptIn(bool enabled) => Environment.SetEnvironmentVariable(Flag, enabled ? "1" : "0");
        internal (ChatCommands Commands, List<string> Replies) CommandsFor(ClientState connection)
        {
            List<string> replies = [];
            var commands = new ChatCommands { Send = (_, _) => Task.CompletedTask, Reply = text => { replies.Add(text); return Task.CompletedTask; } };
            connection.Commands = commands;
            typeof(GameServer.Modules.M_commands.CommandsModule).Assembly.GetType("CommandsFeature")!
                .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [commands, "Whisperer", Wire, Ciphers, connection]);
            return (commands, replies);
        }
        internal static ClientState Connection(string name, bool optional = false, uint ticket = 1, byte classRow = 3, string area = "Whisperer") =>
            new(new(1, area, 300, 300, 100, 100, null, []), ticket, 1)
            {
                Character = name, ClassRow = classRow, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
                Experience = CharacterStats.ExperienceFor(68), Pools = new(68, 1000, 1000, 100, 0),
                LeagueModule = new(new League("WhispererTest", "Standard", false, false),
                    new HashSet<string>(optional ? ["Game", "PhreciaAscendancies"] : ["Game"])),
                Keyring = new InstanceKeyring(new byte[32]),
            };
        internal ClientState NewConnection(string name, uint ticket = 1, bool optional = true, byte classRow = 3, string area = "Whisperer")
        {
            var c = Connection(name, optional, ticket, classRow, area); c.MainAscendancy = new(Definition.Identity, A, AscendancyRules.AwardKeys);
            connections.Add(c); if (optional) AscendancyPolicies.SelectionChanged(c); return c;
        }
        internal void Build(ushort[] nodes)
        {
            Conn.MainAscendancy = new(Definition.Identity, nodes, AscendancyRules.AwardKeys);
            Conn.Passives = []; Conn.RefundPoints = 200; Conn.StatSourcesCacheState = null;
            CharacterSaves.SetMainAscendancy(Conn.Character!, Conn.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
            Conn.SavedPassiveProgress = new(Conn.MainAscendancy, [], new Dictionary<ushort, ushort>(), 200);
            AscendancyPolicies.SelectionChanged(Conn);
            Conn.Pools = Game.PoolsFor(Conn, 68); Conn.PlayerLife = Conn.PlayerMaxLife; Conn.PlayerMana = Conn.Pools.Mana;
        }
        internal int SkillRecordCost(ushort skill) => Game.PlayerSkills(Conn).Single(s => s.Id == skill).Stats!.Single(s => s.Key == Key("mana_cost")).Value;
        public void Dispose()
        {
            foreach (var c in connections) { c.Closed = true; c.RetirePlayerUpdates(); c.RetireResourceEffects(); c.RetireOwnedCombat(); PlayerInventories.Forget(c.Character!); }
            questTables.Dispose(); registration.Dispose(); Provider.Stop(_ => { }); Rules.Stop(_ => { }); Wire.Dispose();
            Game.CombatLifetime = oldRules; Game.PlayerUpdatesSchedule = oldSchedule;
            Program.SetGameLogic(oldLogic!); Environment.SetEnvironmentVariable(Flag, oldFlag);
            Saves.Db.Dispose(); foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}

public sealed partial class WhispererRuntimeTests
{
    // The existing two Harbinger tests now exercise the combined production resource writer, too.
    internal static async Task CombinedHarbingerResourceTransactions()
    {
        var definition = Assert.Single(new AscendancyCatalog().Definitions,
            d => d.Identity.Family == AscendancyFamily.Phrecia && d.Name == "Whisperer");
        using var h = new Harness(definition);
        h.Build(A);
        var c = h.Conn;
        c.PlayerLife = c.PlayerMana = 1000;
        c.Ward.SetMaximum(50);
        c.EnergyShield.SetMaximum(100, true);
        var stats = Game.CharacterPlayerStats(c);
        stats[Key(IncomingResourceDefences.TakenStat)] = -20;
        stats[Key(IncomingResourceDefences.BypassStat)] = 50;
        stats[Key("physical_damage_%_taken_from_mana_before_life")] = 100;
        stats[Key("damage_taken_goes_to_mana_%")] = 50;
        var state = Game.ResourceEffectsOf(c); state.Mana.Clear();
        Assert.Equal(120, Game.DamagePlayerHitTyped(c, 100, 100, 100, 0, stats));
        Assert.Equal(985u, c.PlayerMana);
        Assert.Equal(45u, c.EnergyShield.Current);
        Assert.Equal(95, state.Mana.Take(4), 8); // Reduced, post-Ward 190; no pre-Ward or Life-only Recoup.

        // Ordinary controls retain the exact typed ordering with neither Harbinger input.
        c.PlayerMana = 1000; c.Ward.SetMaximum(50); c.Ward.Restore(); c.EnergyShield.SetMaximum(100, true);
        stats.Remove(Key(IncomingResourceDefences.TakenStat)); stats.Remove(Key(IncomingResourceDefences.BypassStat));
        state.Mana.Clear();
        Assert.Equal(150, Game.DamagePlayerHitTyped(c, 100, 100, 100, 0, stats));
        Assert.Equal(1000u, c.PlayerMana); Assert.Equal(0u, c.EnergyShield.Current);
        Assert.Equal(125, state.Mana.Take(4), 8);

        // The final Life/Mana sources add ES once; repeated rebuilding cannot compound the derived pool.
        c.PlayerStats[Key("maximum_life_%_to_add_as_maximum_energy_shield")] = 0;
        c.PlayerStats[Key("mana_%_to_add_as_energy_shield")] = 0;
        var ordinary = Game.PoolsFor(c, c.Pools.Level);
        c.PlayerStats[Key("maximum_life_%_to_add_as_maximum_energy_shield")] = 10;
        c.PlayerStats[Key("mana_%_to_add_as_energy_shield")] = 20;
        var extra = Game.PoolsFor(c, c.Pools.Level);
        Assert.Equal(IncomingResourceDefences.Maximum(ordinary.EnergyShield, extra.Life, extra.Mana,
            extra.EnergyShieldPct, 10, 20), extra.EnergyShield);
        c.Pools = extra;
        Assert.Equal(extra.EnergyShield, Game.PoolsFor(c, extra.Level).EnergyShield);

        // A forged receipt for a non-channelled, otherwise payable skill is never a Focus permission.
        var fireball = h.Fireball;
        Assert.True(Game.PayMana(c, fireball.Id, out _, out _));
        c.ChannelSkill = fireball.Id; c.ChannelGeneration = 7; c.LastChannelUpdateAt = c.Clock.Elapsed.TotalSeconds;
        c.PaidChannel = new(fireball.Id, fireball.Effect, c.ActionCount, 7, h.Rules);
        Assert.False(Game.PaidChannelCurrent(c, c.PaidChannel));
        c.PlayerLife = 0; c.InvalidateIncomingHits();
        Assert.Null(c.PaidChannel); Assert.Equal(0, c.NonChaosDotBypassCarry);
        await Task.CompletedTask;
    }
}
