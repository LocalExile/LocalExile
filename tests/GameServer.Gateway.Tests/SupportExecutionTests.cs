using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

public sealed class SupportExecutionTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);

    static Player PlayerOf(string name)
    {
        var player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        TestRequirements.Waive(player);
        return player;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
    static ushort Equip(Player player, string active, string support)
    {
        var items = Game.ItemsOf(player);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with
        {
            Sockets = [new(SocketColour.White, Gem(active)), new(SocketColour.White, Gem(support))], Links = [2],
        });
        return Game.GemSkillEffects(player).Single().Id;
    }

    [Fact]
    public void Second_wind_reads_native_supports_and_recovers_stored_uses_in_order()
    {
        string path = Path.Combine(Path.GetTempPath(), $"supports-cooldown-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = PlayerOf("SecondWindAcceptance");
        try
        {
            ushort id = Equip(player, "SkillGemDash", "SupportGemSecondWind");
            var gem = Game.SupportedSkill(player, id);
            var resolved = Game.CooldownFor(player, gem.Effect, gem.Level, gem.Quality);
            Assert.True(resolved is not null, string.Join(", ", gem.Stats.Select(s => $"{Program.Stats.Name(s.Key)}={s.Value}")));
            var plan = resolved!.Value;
            // PoEDB: one additional NON-INSTANT use, level-one 24% reduced recovery, 200% cost multiplier.
            Assert.Equal(2, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            Assert.Equal(200, gem.Supports.Single().CostMultiplier);
            var state = new SingleUseCooldownState(0, plan.RecoveryRate);
            Assert.True(state.TryUse(0, plan));
            state.Refresh(.25, plan.RecoveryRate);
            double progress = state.RemainingBaseMilliseconds;
            Assert.True(state.TryUse(.25, plan)); Assert.Equal(progress, state.RemainingBaseMilliseconds, 6);
            Assert.False(state.TryUse(.25, plan)); Assert.Equal(2, state.SpentUses);
            // Native replacement contains two entries, and the second has a full base-time budget.
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate, state.SpentUses)]);
            var single = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate)]);
            Assert.Equal(single.Length + 16, bytes.Length);
            Assert.Equal((float)(progress / 1000), BitConverter.ToSingle(bytes, bytes.Length - 32));
            Assert.Equal(plan.BaseMilliseconds / 1000f, BitConverter.ToSingle(bytes, bytes.Length - 16));
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan.RecoveryRate);
            Assert.True(state.Ready); Assert.Equal(1, state.SpentUses);
            Assert.True(state.TryUse(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan));
            // Rate changes settle progress first, and reducing capacity never refunds a spent use.
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate) + .5, 2);
            state.Capacity(1); Assert.False(state.Ready);
            state.Capacity(2); Assert.False(state.Ready); // Re-linking cannot refund either spent use.
            state.Refresh(100, 2); Assert.True(state.FullyRecovered);
            var zero = new SingleUseCooldown(1000, -100, 2);
            Assert.True(state.TryUse(100, zero)); Assert.True(state.TryUse(100, zero));
            state.Refresh(1000, 0); Assert.False(state.Ready);
            // Instant skills receive recovery and cost modifiers, but never the extra use.
            id = Equip(player, "SkillGemFrostblink", "SupportGemSecondWind");
            gem = Game.SupportedSkill(player, id);
            plan = Game.CooldownFor(player, gem.Effect, gem.Level, gem.Quality)!.Value;
            Assert.Equal(1, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            // Native Flame Dash already has three charges; Second Wind grants the fourth.
            id = Equip(player, "SkillGemFlameDash", "SupportGemSecondWind");
            gem = Game.SupportedSkill(player, id);
            Assert.Equal(4, Game.CooldownFor(player, gem.Effect, gem.Level, gem.Quality)!.Value.Uses);
            // PoEDB's level-20 penalty (-5) and 20% quality recovery (+5) cancel in the installed native data.
            var qualityStats = Game.SkillsFor(player).EffectStats(Game.SkillsFor(player).EffectRow("SupportSecondWind"), 20, 20)
                .ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(0, qualityStats[Stat.BaseCooldownSpeedPlusPct]);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Multistrike_preserves_native_panel_damage_and_paces_one_paid_cancellable_repeat_series()
    {
        string path = Path.Combine(Path.GetTempPath(), $"multistrike-panel-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = PlayerOf("MultistrikePanelAcceptance");
        try
        {
            foreach (var (support, repeats) in new[]
            {
                ("SupportGemMultistrike", 2),
                ("SupportGemGreaterMultistrike", 3),
                ("SupportGemAddedFireDamage", 0),
            })
            {
                ushort id = Equip(player, "SkillGemCleave", support);
                Assert.Equal(repeats, Game.MeleeAttackRepeats(player, id));
                var items = Game.ItemsOf(player);
                for (int hands = 1; hands <= 2; hands++)
                {
                    if (hands == 2)
                    {
                        var off = ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword5")!);
                        items.Add(PlayerInventories.Offhand1, 0, 0, off);
                    }
                    player.PlayerSkillsCache = null;
                    var rows = Game.PlayerSkills(player).Single(s => s.Id == id).Stats!.ToDictionary(s => s.Key, s => s.Value);
                    int Row(uint key) => rows.GetValueOrDefault(key);
                    Assert.Equal(repeats, Row(Stat.AttackRepeatCount));
                    double Factor(bool mainHand) => mainHand
                        ? CriticalStrikePolicy.DamageFactor(Row(Stat.MainHandCriticalStrikeChance),
                            Row(Stat.MainHandCriticalStrikeMultiplier), 0, Row(Stat.MainHandChanceToHitPct))
                        : CriticalStrikePolicy.DamageFactor(Row(Stat.OffHandCriticalStrikeChance),
                            Row(Stat.OffHandCriticalStrikeMultiplier), 0, Row(Stat.OffHandChanceToHitPct));
                    // Dual-wielded Cleave hits with both weapons at once, at its data's merged 40% less damage
                    // (skill_double_hits_when_dual_wielding, active_skill_merged_damage_+%_final_while_dual_wielding).
                    double dualMore = Game.HandDamageMore(player, Game.SupportedSkill(player, id).Stats).DualWielding;
                    Assert.Equal(hands == 2 ? .6 : 1, dualMore, 9);
                    var mainHit = Game.SupportedHit(player, id).Scale(dualMore);
                    foreach (var (stat, value) in PanelStats.HandDamage(mainHit, "main_hand"))
                        Assert.Equal(value, Row(stat));
                    double mainSeconds = Game.SupportedAttackSeconds(player, id);
                    var main = SpellDamage.Display((mainHit.MinTotal, mainHit.MaxTotal), 1 / mainSeconds, Factor(mainHand: true));
                    int perUse = main.PerUse, perSecond = main.PerSecond;
                    if (hands == 2)
                    {
                        var offWeapon = Game.OffhandWeaponFor(player)!;
                        var offInputs = Game.SupportedHitInputsFor(player, id, weapon: offWeapon)!;
                        var offHit = Game.SupportedHitFrom(offInputs, suppress: true).Scale(dualMore);
                        foreach (var (stat, value) in PanelStats.HandDamage(offHit, "off_hand"))
                            Assert.Equal(value, Row(stat));
                        double offSeconds = Game.SupportedOffHandAttackSeconds(player, id, offWeapon);
                        var off = SpellDamage.Display((offHit.MinTotal, offHit.MaxTotal), 1 / offSeconds, Factor(mainHand: false));
                        // Both hits per use, at the average of the two weapons' attack times.
                        Assert.Equal(AttackHand.Both, Game.PanelHands(player, id));
                        perUse = main.PerUse + off.PerUse;
                        perSecond = (int)Math.Round(perUse / ((mainSeconds + offSeconds) / 2));
                    }
                    Assert.Equal(perUse, Row(Stat.DisplayHundredTimesDamagePerSkillUse));
                    Assert.Equal(perSecond, Row(Stat.HundredTimesDamagePerSecond));
                    // Native calculators keep these rows per hit; repeat damage belongs to execution only.
                    for (int repeat = 1; repeat <= repeats; repeat++)
                        Assert.Equal(1 + .22 * repeat, Game.MeleeRepeatDamageMore(player, id, repeat), 8);
                    player.NextAttackAt = 10 + mainSeconds;
                    player.LockstepBusyUntil = 10 + mainSeconds * .6;
                    uint mana = player.PlayerMana;
                    Game.ReserveMeleeRepeats(player, id, mainSeconds);
                    Assert.Equal(10 + mainSeconds * (repeats + 1), player.NextAttackAt, 8);
                    Assert.Equal(mana, player.PlayerMana);
                    if (repeats > 0)
                    {
                        Assert.False(Game.MeleeRepeatAllowsMove(player, 10 + mainSeconds * 1.5));
                        Assert.True(Game.MeleeRepeatAllowsMove(player, 10 + mainSeconds * 1.7));
                        Game.CancelMeleeRepeats(player, 10 + mainSeconds * 1.7);
                        Assert.Equal(10 + mainSeconds * 2, player.NextAttackAt, 8);
                        Assert.True(Game.MeleeRepeatAllowsMove(player, 10 + mainSeconds * 1.7));
                        // An object interaction replaces the action too (loot, portals, NPCs). It must release the
                        // unstarted swings' reservation and movement gates, while preserving the current swing.
                        player.Clock.Stop();
                        double now = Game.ProjectileWorldTime(player), start = now - mainSeconds * .7;
                        player.NextAttackAt = start + mainSeconds;
                        player.LockstepBusyUntil = start + mainSeconds * .6;
                        Game.ReserveMeleeRepeats(player, id, mainSeconds);
                        double reserved = player.NextAttackAt;
                        player.PlayerLife = 0;
                        Assert.False(Game.TryBeginObjectWalk(player, now, 110, 100, 3, out _));
                        Assert.Equal(reserved, player.NextAttackAt);
                        player.PlayerLife = 1000;
                        Assert.True(Game.TryBeginObjectWalk(player, now, 110, 100, 3, out _));
                        Assert.Equal(start + mainSeconds, player.NextAttackAt, 8);
                        Assert.True(Game.MeleeRepeatAllowsMove(player, start + mainSeconds * 1.5));
                    }
                }
                foreach (uint disabled in new[] { Stat.DisableAttackRepeats, Stat.DisableSkillRepeats })
                {
                    using (GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
                    {
                        if (!ReferenceEquals(p, player)) return sources;
                        return new Dictionary<uint, int>(sources) { [disabled] = 1 };
                    }))
                    {
                        Assert.Equal(0, Game.MeleeAttackRepeats(player, id));
                        player.PlayerSkillsCache = null;
                        Assert.Equal(0, Game.PlayerSkills(player).Single(s => s.Id == id).Stats!.Single(s => s.Key == Stat.AttackRepeatCount).Value);
                    }
                }
                foreach (var off in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Offhand1))
                    items.Remove(PlayerInventories.Offhand1, off.Item.Id);
            }
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }

}
