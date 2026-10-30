using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class WhispererCurrentMainTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Whisperer_Dex_Mana_composes_with_final_attributes_group_sources_and_both_Int_flags(bool narrow, bool broad)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Strength = 20, Dexterity = 15, Intelligence = 21 };
        var b = new CharacterStats.Bonus(ManaPerDexterity: 2, ManaPerFourStrength: 8, ManaPerLevel: 3,
            Mana: 9, ManaPct: 33, DexterityPct: 20, IntelligencePct: 10,
            NoManaFromIntelligence: narrow, NoIntelligenceBonuses: broad,
            LifePerFourDexterity: 4, EnergyShield: 100, Armour: 100, ArmourPct: 50);
        var ordinary = CharacterStats.For(c, 1, b);
        Assert.Equal((20, 18, 23), (ordinary.Strength, ordinary.Dexterity, ordinary.Intelligence));
        decimal flat = 34 + 6 + 9 + 3 + 40 + 36 + (narrow || broad ? 0 : 11);
        Assert.Equal((uint)decimal.Floor(flat * 1.33m), ordinary.Mana);
        Assert.Equal(76u, ordinary.Life); // 38 +12 +10 Str +4 complete Dex groups *4.
        Assert.Equal(broad ? 100u : 102u, ordinary.EnergyShield);
        var purpose = CharacterStats.For(c, 1, b with { MentalConditioning = true });
        decimal exactFlat = 34 + 6 + 9 + 3 + 40 + 36 + (narrow || broad ? 0m : 11.5m);
        Assert.Equal((uint)decimal.Round(exactFlat * .2m * 1.33m, 0, MidpointRounding.AwayFromZero), purpose.Mana);
        Assert.Equal((int)decimal.Round(150 + exactFlat * 1.6m * 1.83m, 0, MidpointRounding.AwayFromZero), purpose.Armour);
        Assert.True(purpose.AttributesResolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Whisperer_hybrid_Mana_copy_tracks_its_independent_queue_and_actual_component_expiry(bool atEnd)
    {
        int Get(string n) => n switch
        {
            "flask_mana_recovery_rate_+%" => 100,
            "non_instant_mana_recovery_from_flasks_also_recovers_life" => 1,
            _ => 0,
        };
        var local = new Dictionary<string, int>();
        if (atEnd) local["local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect"] = 1;
        var use = FlaskRecovery.For(new(120, 90, 6, 10, 40), new Item(1, [], 1), local, Get, 10, 1000);
        ushort buff = 1;
        var parts = Flasks.RecoveryEffects(use, 0, 0, () => buff++, 5, 9);
        var life = Assert.Single(parts, e => e.LifePerSecond > 0);
        var mana = Assert.Single(parts, e => e.ManaPerSecond > 0 || e.ManaAtEnd > 0);
        Assert.False(life.ManaAlsoLife);
        Assert.True(mana.ManaAlsoLife);
        Assert.Equal(6, life.Ends); Assert.Equal(3, mana.Ends);
        Assert.Equal(1, life.Queue); Assert.Equal(2, mana.Queue);
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        running.Start(new(1, 3, 9, 0, 100, 0, 1)); // Higher Mana rate delays only the Mana component.
        var first = running.Advance(0, 3);
        Assert.Equal(atEnd ? 60 : 120, first.Life);
        Assert.Equal(atEnd ? 100 : 160, first.Mana);
        Assert.Equal(1, running.Remaining(mana));
        var rest = running.Advance(3, 6);
        Assert.Equal(atEnd ? 60 : 90, rest.Life);
        Assert.Equal(atEnd ? 90 : 30, rest.Mana);
        Assert.Equal(2, rest.Ended.Count);
        Assert.False(running.Any);
    }
}

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Whisperer_no_Int_Mana_uses_one_raw_flag_and_explicit_zero_restores_only_the_inherent_grant()
    {
        using var game = WhispererGame([17123, 4174]);
        await game.Command("/set_stat base_intelligence 7"); // 14 +7: odd final Intelligence.
        uint sourced = game.State.Pools.Mana;
        await game.Command("/set_stat gain_no_maximum_mana_from_intelligence 0");
        var c = Program.Skills.ClassFor(3);
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 32 + 10) * 1.06m), game.State.Pools.Mana);
        await game.Command("/set_stat gain_no_maximum_mana_from_intelligence off");
        Assert.Equal(sourced, game.State.Pools.Mana);
        await game.Command("/set_stat X_mana_per_dexterity 2");
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 64) * 1.06m), game.State.Pools.Mana);
        await game.Command("/set_stat gain_no_inherent_bonus_from_intelligence 1");
        Assert.Equal((uint)((c.BaseMaxMana + 68 * 6 + 64) * 1.06m), game.State.Pools.Mana);
        Assert.Equal(21, game.State.Pools.Intelligence);
        Assert.Equal(21, WStat(game.State, "virtual_intelligence_for_requirements"));
    }

    [Fact]
    public void Whisperer_loading_discards_Recoup_and_leech_accrual_but_preserves_remaining_grants_and_spend_progress()
    {
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State;
        conn.Whisperer.Hit(10, 101, 20); // 5.05 per second, including discarded fractional residue.
        conn.Whisperer.Spent(199, 10);
        var leech = Program.LeechOf(conn).Mana;
        leech.Configure(10, new(1, 1000, 1));
        leech.Grant(10, 100, 1000, 100);
        conn.LeavingArea = true;
        Program.DiscardLoadingRecovery(conn, 10.5);
        conn.ResumeAfterFailedTransfer();
        Assert.Equal(0, conn.Whisperer.Take(10.5));
        Assert.Equal(0, leech.TakeRecovery(10.5));
        Assert.Equal(199, conn.Whisperer.SpentRemainder);
        Assert.Equal(5, conn.Whisperer.Take(11.5)); // No .525-point loading residue.
        Assert.Equal(20, leech.TakeRecovery(11.5));
        Assert.Equal(12, conn.Whisperer.Take(14));
        Assert.Equal(50, leech.TakeRecovery(14));
    }

    [Fact]
    public async Task Whisperer_Recoup_does_not_catch_up_across_an_actual_refused_transfer()
    {
        using var game = WhispererGame(WhispererBuilds[0]);
        var conn = game.State;
        LifecycleSet(conn, "mana_regeneration_rate_+%", -100);
        conn.PlayerMana = 0;
        double now = Program.ProjectileWorldTime(conn);
        conn.Whisperer.Hit(now, 1000, 20); // 50 per second.
        var transfer = new PendingTransfer(0, 0, false);
        Assert.True(conn.TryBeginAreaChange(transfer, out _));
        await game.StatLifecycleExchange(async (stream, cipher) =>
        {
            var recovery = ProgramValue<Task>("RunResourceRecovery", "Whisperer-refused-transfer", stream, cipher, conn);
            var resumed = new System.Diagnostics.Stopwatch();
            try
            {
                await Program.Transfer("Whisperer-refused-transfer", stream, cipher, conn, transfer, async request =>
                {
                    await Task.Delay(350);
                    Assert.Equal(0u, conn.PlayerMana);
                    resumed.Start();
                    return new ControlResponse { Ok = false, Error = "Whisperer fixture refusal" };
                });
                while (conn.PlayerMana == 0 && resumed.Elapsed.TotalSeconds < 1) await Task.Delay(5);
                Assert.InRange(conn.PlayerMana, 1u, (uint)Math.Ceiling(50 * resumed.Elapsed.TotalSeconds) + 2);
                Assert.False(conn.RageRetired);
            }
            finally { conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(2)); }
        });
    }

    [Fact]
    public async Task Whisperer_selected_and_offhand_accuracy_apply_Blind_once_after_their_own_sources()
    {
        var spec = new SelectedMiscCase("SkillGemHeavyStrike", "SupportGemAdditionalAccuracy", "SupportAdditionalAccuracy",
            "accuracy_rating_+%", "accuracy_rating", "main_hand_accuracy_rating", 0, 74, 20);
        using var game = new SelectedMiscCharacter(spec);
        var items = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        game.Socket();
        var before = game.Read();
        int ordinary = Program.PlayerAccuracy(game.State);
        int selected = before.Get("main_hand_accuracy_rating");
        int offhand = before.Get("off_hand_accuracy_rating");
        Assert.True(selected > ordinary);
        Assert.True(Program.InflictPlayerBlind(game.State, 60));
        var blinded = game.Read();
        Assert.Equal(selected * 80 / 100, blinded.Get("main_hand_accuracy_rating"));
        Assert.Equal(offhand * 80 / 100, blinded.Get("off_hand_accuracy_rating"));
        Assert.Equal(selected * 80 / 100,
            ProgramValue<AttackAccuracyProfile>("SupportedAccuracy", game.State, blinded.Skill.Id).Rating);
        Assert.Equal(ordinary * 80 / 100, Program.CharacterPlayerStats(game.State)[WKey("main_hand_accuracy_rating")]);
        game.State.Blind.Clear();
        Assert.Equal(selected, game.Read().Get("main_hand_accuracy_rating"));
        Assert.Equal(offhand, game.Read().Get("off_hand_accuracy_rating"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Whisperer_full_Mana_flask_snapshots_level_modifiers_and_Life_copy_before_charge_publication()
    {
        using var game = WhispererGame([30419, 53715]);
        var conn = game.State;
        var items = ProgramValue<PlayerInventories>("ItemsOf", conn);
        var type = Program.Items.Find("Small Mana Flask")!;
        items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Charges = 24 });
        LifecycleSet(conn, "flask_effect_+%_per_level", 1);
        LifecycleSet(conn, "flask_effect_+%", -60);
        conn.PlayerLife -= 100;
        conn.PlayerMana = conn.Pools.Mana;
        await game.StatLifecycleExchange(async (stream, cipher) =>
        {
            var gate = Program.SendGates.GetValue(cipher, _ => new(1, 1));
            await gate.WaitAsync();
            var use = ProgramValue<Task>("UseFlask", "Whisperer-snapshot", stream, cipher, conn, 0u);
            try
            {
                Assert.False(use.IsCompleted);
                LifecycleSet(conn, "flask_effect_+%", 900);
                LifecycleSet(conn, "non_instant_mana_recovery_from_flasks_also_recovers_life", 0);
                conn.Pools = conn.Pools with { Level = 1 };
            }
            finally { gate.Release(); }
            await use;
        });
        var effect = Assert.Single(conn.FlaskEffects.End(_ => true));
        Assert.True(effect.ManaAlsoLife);
        Assert.True(effect.ManaStays);
        Assert.Equal(0, effect.Queue);
        Assert.Equal(54, effect.ManaPerSecond * (effect.Ends - effect.Started), 8); // 50 * (1 -.60 +.68).
        var running = new Flasks.Effects(); running.Start(effect);
        var recovered = running.Advance(effect.Started, effect.Ends);
        Assert.Equal(54, recovered.Life, 8); Assert.Equal(54, recovered.Mana, 8);
    }
}

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("Rusted Sword")]
    [InlineData("Crude Bow")]
    public async Task Whisperer_Blind_contacts_keep_one_entropy_advance_and_critical_confirmation(string weapon)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, weaponName: weapon,
            distance: weapon == "Crude Bow" ? 60 : 2, targetLevel: 60);
        f.Set("extra_critical_rolls_while_on_low_life", 0); f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 500 };
        Assert.True(Program.InflictPlayerBlind(f.Conn, 60));
        Assert.True(Program.InflictMonsterBlind(f.Target, 0, (100, 50, 60)));
        int evasion = (MonstersBaseEvasion() * 70) / 100;
        int chance = DamageModel.AccuracyHitChance(evasion, 400);
        var accuracy = new AccuracyDraws(99, 99); f.Conn.AttackAccuracyRandom = accuracy;
        await f.Use(); await f.HitLands();
        Assert.Equal(1000u, f.Target.MaxLife - f.Target.Life); // Failed critical confirmation leaves the direct hit.
        Assert.Equal(chance - 1, f.Target.AttackEvasion.Value);
        Assert.Equal(2, accuracy.Calls); Assert.Equal(1, critical.Calls);
    }
    static int MonstersBaseEvasion() => Program.Monsters.BaseEvasion(60)!.Value;
}

public partial class OwnAuraGatewayTests
{
    [Fact]
    public async Task Whisperer_resource_writer_integrates_leech_ES_Recoup_and_copied_Mana_once_at_one_boundary()
    {
        using var f = new Fixture();
        var conn = f.Conn;
        conn.Clock.Stop(); // Keep already-observed game time monotonic; advance only the manual runtime.
        double start = conn.Clock.Elapsed.TotalSeconds;
        double boundary = Math.Ceiling((start + 1) * 120000) / 120000;
        conn.Pools = conn.Pools with { EnergyShield = 100 };
        conn.EnergyShield.SetMaximum(100, false);
        conn.PlayerLife = conn.PlayerMaxLife / 2; conn.PlayerMana = 0;
        foreach (var (stat, value) in new[] { ("no_life_regeneration", 1), ("no_mana_regeneration", 1),
            ("energy_shield_recharge_rate_+%", -100) })
            conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
        Program.CharacterPlayerStats(conn);
        var leech = Program.LeechOf(conn);
        leech.Life.Grant(start, 100, conn.PlayerMaxLife, 100);
        leech.Mana.Grant(start, 100, conn.Pools.Mana, 100);
        leech.EnergyShield.Grant(start, 10, 100, 10);
        conn.Whisperer.Hit(start, 100, 20);
        conn.FlaskEffects.Start(new(0, 1, 9, 0, 10, start, start + 4, ManaAlsoLife: true));
        uint before = conn.PlayerLife;
        await using var simulation = new ProjectileRuntimeFixture(conn);
        simulation.Advance(boundary);
        var recovery = (Task)Call("RunResourceRecovery", "Whisperer-joint-recovery", f.Stream, f.Ciphers, conn)!;
        try
        {
            await Until(() => conn.PlayerMana > 0);
            conn.Closed = true;
            await recovery.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(before + (uint)Math.Floor(conn.PlayerMaxLife * .02 + 10), conn.PlayerLife);
            Assert.Equal((uint)Math.Floor(conn.Pools.Mana * .02 + 15), conn.PlayerMana);
            Assert.Equal(2u, conn.EnergyShield.Current);
            Assert.Equal(0, conn.Whisperer.Take(boundary));
            Assert.Equal((0d, 0d, 0d), leech.TakeRecovery(boundary));
        }
        finally { conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(2)); }
    }
}
