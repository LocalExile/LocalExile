using GameServer.Core.Accounts;
using GameServer.Modules.Survivors;
using GameServer.Modding;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SurvivorsLeagueTests
{
    [Fact]
    public async Task Survivors_is_a_separate_creatable_event_league_without_changing_standard()
    {
        var leagues = LeagueCatalog.FromFolder("Leagues");
        Assert.Empty(leagues.Errors);
        var league = leagues.Find("Survivors");
        Assert.NotNull(league);
        Assert.True(league.IsMain);
        Assert.Equal("Void", league.Parent);
        Assert.True(leagues.CanEnter("Survivors"));
        Assert.Contains(leagues.Entries(), entry => entry.Name == "Survivors");
        Assert.Contains("Survivors/SurvivorsRun", league.Modules);
        Assert.Contains("Survivors/SurvivorsSkills", league.Modules);
        Assert.Contains("Survivors/SurvivorsRewards", league.Modules);
        Assert.DoesNotContain(leagues.Find("Standard")!.Modules, module => module.StartsWith("Survivors/"));

        var skills = new ModuleLifetime("SurvivorsSkills");
        var rewards = new ModuleLifetime("SurvivorsRewards");
        var runs = new ModuleLifetime("SurvivorsRun");
        using var cleanup = new Cleanup(() =>
        {
            runs.Stop(ex => throw ex);
            rewards.Stop(ex => throw ex);
            skills.Stop(ex => throw ex);
        });
        new SurvivorsSkillsModule().Load(Context(skills));
        new SurvivorsRewardsModule().Load(Context(rewards));
        new SurvivorsRunModule().Load(Context(runs));
        var standard = Connection("ordinary", leagues.Find("Standard")!);
        var survivor = Connection("survivor", league);
        var state = SurvivorsSkills.StateOf(survivor);
        state.Survivors = new();
        state.SurvivorsItemIds.Add(123);
        Assert.Equal(100, GameEvents.AdjustIncomingDamage(standard, 100));
        Assert.True(GameEvents.AllowsLoot(standard));
        Assert.Empty(GameEvents.ExemptItems(standard));
        Assert.Empty(GameEvents.ExtraSkills(standard));
        Assert.Equal(60, GameEvents.AdjustIncomingDamage(survivor, 100));
        Assert.False(GameEvents.AllowsLoot(survivor));
        Assert.Contains(123u, GameEvents.ExemptItems(survivor));
        Assert.False(SurvivorsRunRules.TryClaimAdmission(standard));
        Assert.True(SurvivorsRunRules.TryClaimAdmission(survivor));
        Assert.False(SurvivorsRunRules.TryClaimAdmission(survivor));
        state.SurvivorsPausedByWindow = true;
        survivor.Clock.Stop();
        SurvivorsRunRules.Release(survivor);
        Assert.Null(state.Survivors);
        Assert.Empty(GameEvents.ExemptItems(survivor));
        Assert.True(survivor.Clock.IsRunning);
        Assert.False(state.SurvivorsPausedByWindow);
        // An old run's cleanup must not release the replacement run's pause.
        var previous = new SurvivorsRun();
        var replacement = new SurvivorsRun();
        state.Survivors = replacement;
        state.SurvivorsPausedByWindow = true;
        survivor.Clock.Stop();
        SurvivorsRunRules.Release(survivor, previous);
        Assert.Same(replacement, state.Survivors);
        Assert.False(survivor.Clock.IsRunning);
        SurvivorsRunRules.Release(survivor, replacement);
        Assert.True(survivor.Clock.IsRunning);

        // The old ticker finishes while the replacement is still asynchronously publishing its initial packets.
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Survivors = previous;
        SurvivorsRunRules.StartRunner(survivor, _ => finishing.Task);
        var oldTicker = state.Runner!;
        state.Survivors = replacement;
        finishing.SetResult();
        await oldTicker;
        Assert.Same(replacement, state.Survivors);
        SurvivorsRunRules.Release(survivor);
        await OwnedContinuations(survivor, league);
    }

    [Fact]
    public void Extracted_run_preserves_progression_supports_rewards_and_presentation()
    {
        new SurvivorsFormulasTests().A_gems_cast_time_and_cast_speed_set_the_interval();
        new SurvivorsFormulasTests().Critical_chance_is_base_plus_added_times_increased_and_capped();
        new SurvivorsFormulasTests().Critical_multiplier_starts_at_150_percent();
        new SurvivorsFormulasTests().Area_grows_with_the_square_root_of_increased_area();
        new SurvivorsFormulasTests().The_way_onward_is_sealed_only_while_the_lore_boss_lives();
        new SurvivorsFormulasTests().Added_damage_is_scaled_by_the_skill_effectiveness();
        new SurvivorsRunTests().The_horde_grows_from_fourteen_to_the_cap();
        new SurvivorsRunTests().The_spawner_refills_a_quarter_of_the_gap_at_most_eight();
        new SurvivorsRunTests().The_roster_walks_its_tier_pool_over_the_run();
        new SurvivorsRunTests().A_run_skill_grows_in_damage_and_speed_with_its_rank();
        new SurvivorsRunTests().The_boss_ladder_and_its_experience_match_the_reference();
        new SurvivorsRunTests().The_run_has_the_reference_s_six_skill_slots();
        new SurvivorsRunTests().A_run_skill_gains_a_projectile_at_ranks_three_five_and_seven();
        new SurvivorsRunTests().Monsters_grow_tougher_and_hit_harder_over_a_run();
        new SurvivorsRunTests().A_run_hit_reaches_the_spawn_ring();
        new SurvivorsRunTests().Incoming_monster_hits_are_taken_at_sixty_percent_and_fortitude_softens_them();
        new SurvivorsRunTests().The_herald_pulse_reaches_four_times_farther();
        new SurvivorsRunTests().The_horde_gets_a_ranged_ring_wave_every_twelve_seconds();
        new SurvivorsRunTests().A_run_moves_to_random_areas_near_a_growing_level();
        new SurvivorsRunTests().Experience_follows_monster_rarity();
        new SurvivorsRunTests().Kills_level_the_run_and_queue_choices();
        new SurvivorsRunTests().A_unique_kill_can_grant_several_levels_at_once();
        new SurvivorsRunTests().An_offer_holds_six_distinct_legal_choices();
        new SurvivorsRunTests().A_new_skill_waits_for_the_learned_skill_s_five_supports();
        new SurvivorsRunTests().Choosing_a_new_skill_learns_it_and_closes_the_offer();
        new SurvivorsRunTests().A_full_skill_bar_is_offered_supports_for_its_skills();
        new SurvivorsRunTests().An_offer_only_pairs_a_support_with_a_skill_the_rule_allows();
        new SurvivorsRunTests().Blessings_change_the_run_numbers();
        new SurvivorsRunTests().Every_skill_can_hold_five_supports();
        new SurvivorsRunTests().A_unique_item_is_offered_on_third_levels_from_the_free_pool();
        new SurvivorsRunTests().A_run_with_every_slot_mastered_offers_a_heal();
        new SurvivorsRunTests().An_invalid_choice_changes_nothing();
        new SurvivorsRunTests().A_second_level_up_does_not_overwrite_an_open_offer();
        new SurvivorsRunTests().The_clock_ignores_paused_and_broken_steps();
        new SurvivorsSupportMatrixTests().Every_support_moves_its_own_number_on_every_skill_it_can_support();
        new SurvivorsSupportMatrixTests().A_run_skill_uses_the_reference_s_thirty_percent_a_rank();
        new SurvivorsSupportMatrixTests().Area_gems_join_the_catalogue_and_cast_an_area_pulse();
        new SurvivorsAnimationTests().Every_class_and_weapon_resolves_a_cast_clip_and_the_projectile_impact();
        new SurvivorsAnimationTests().Every_run_skill_maps_to_a_cast_clip();
        new MonsterModEffectsTests().Extra_effects_add_to_a_rolled_monster_s_own_mods();
        new MonsterModEffectsTests().Adding_no_effects_keeps_the_same_set();
        new MonsterModEffectsTests().A_single_stat_can_be_replaced_by_its_solved_value();
        new UniqueCatalogueTests().Every_unique_resolves_its_mods_and_every_equipment_unique_has_a_stat();
    }

    static ClientState Connection(string name, GameServer.Core.Accounts.League league) =>
        new(new(1, name, 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = name,
            LeagueModule = new(league, ServerLeagues.ModulesOf(league)),
        };

    static async Task OwnedContinuations(ClientState conn, GameServer.Core.Accounts.League league)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-ownership-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
        try
        {
            var run = new SurvivorsRun();
            run.RecordExperience(5);
            var choice = Assert.Single(run.DealOffer(new Random(1), 0, 0, 0));
            Assert.Equal(SurvivorsChoiceKind.Heal, choice.Kind);
            var old = SurvivorsRunRules.BindRun(conn, run);
            var state = SurvivorsSkills.StateOf(conn);
            state.SurvivorsOffer = new() { [1] = choice };
            state.SurvivorsCatalog = [];
            conn.PlayerLife = 1;
            Assert.True(gate.Wait(0));
            var pick = SurvivorsRewards.ApplySurvivorsChoice("ownership-test", null!, ciphers, conn, choice);
            Assert.False(pick.IsCompleted);
            SurvivorsRunRules.Release(conn, run);
            var replacement = new SurvivorsRun();
            SurvivorsRunRules.BindRun(conn, replacement);
            state.SurvivorsPausedByWindow = true;
            conn.Clock.Stop();
            gate.Release();
            Assert.False(await pick);
            Assert.Equal(1u, conn.PlayerLife);
            Assert.False(conn.Clock.IsRunning);
            Assert.False(old.Current);

            var runtime = new ProjectileRuntime(new EmptyWorld());
            var shot = new ProjectileOffense(DamagePacket.Of(DamageType.Fire, 10, 10), true, 100, 0, 150, "retired")
                { Owner = old, Valid = () => old.Current };
            var use = new ProjectileUse(new("owned"), new(1), new(1), 1, new(0, 0), new(100, 0)) { Payload = shot };
            runtime.Release(use);
            runtime.Release(use with { Payload = "unrelated" });
            runtime.Discard(u => u.Payload is ProjectileOffense offense && ReferenceEquals(offense.Owner, old));
            Assert.Equal("unrelated", Assert.Single(runtime.Projectiles).Use.Payload);

            Assert.NotNull(SurvivorsRunRules.TravelRefusal(conn, new(1, 0, false)));
            var transfer = new PendingTransfer(2, 0, false);
            state.StageTransfer = transfer;
            Assert.Null(SurvivorsRunRules.TravelRefusal(conn, transfer));
            var carry = new SurvivorsRunRules.SurvivorsCarry(replacement, 1, DateTime.UtcNow, [123], conn.Character);
            SurvivorsRunRules.PendingRuns[conn] = carry;
            await SurvivorsRunRules.TransferResolved(conn, transfer, new() { Ok = true, Ticket = 99 });
            Assert.False(SurvivorsRunRules.SurvivorsCarried.ContainsKey(conn.Ticket));
            Assert.Same(replacement, SurvivorsRunRules.SurvivorsCarried[99].Run);
            Assert.Equal([123u], SurvivorsRunRules.SurvivorsCarried[99].ItemIds);
            // Fresh login has a different ticket and cannot consume the previous connection's carry.
            var login = Connection(conn.Character!, league);
            await SurvivorsRunRules.ResumeSurvivors("fresh-login-test", null!, ciphers, login);
            Assert.Null(SurvivorsSkills.StateOf(login).Survivors);
        }
        finally
        {
            SurvivorsRunRules.Release(conn);
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    sealed class EmptyWorld : IProjectileWorld
    {
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) => ProjectileHitResult.Miss;
        public void Publish(ProjectileEvent value) { }
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = DispatchProxy.Create<IModuleContext, HookContext>();
        ((HookContext)(object)context).Lifetime = lifetime;
        return context;
    }

    public class HookContext : DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "OnChatCommand" or "OnAreaGenerated" or "OnPlayerLeaving" or "OnPacket" => new Registration(),
            _ => throw new NotSupportedException(method.Name),
        };
        sealed class Registration : IDisposable { public void Dispose() { } }
    }

    sealed class Cleanup(Action run) : IDisposable { public void Dispose() => run(); }
}
