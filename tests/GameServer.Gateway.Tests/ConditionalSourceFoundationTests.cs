namespace GameServer.Gateway.Tests;

public class ConditionalSourceFoundationTests
{
    [Fact]
    public void Conditional_subjects_and_event_amounts_keep_live_inputs_and_exact_windows()
    {
        UniqueVariantTestCases.World_variants_keep_coupled_modifiers_bases_and_default_creation();
        ConditionalOperatorCoreCases.Compound_operators_fold_only_supplied_live_subjects_and_keep_source_local_values();
        ConditionalOperatorCoreCases.Event_amounts_use_exact_independent_windows_and_saturating_coefficients();
        IncomingDamagingAilmentTestCases.Monster_applications_use_final_avoidance_and_rolled_damage_ranges();
        IncomingDamagingAilmentTestCases.Resource_writer_preserves_stack_intervals_routing_and_paid_cleanse_protection();
    }

    [Fact]
    public void Physical_skill_sources_keep_identity_and_irrevocable_lifetimes()
    {
        SkillSourceLifetimeCases.Shared_physical_markers_retire_old_leases_and_transfer_one_activation_writer();
        SkillSourceLifetimeCases.Accepted_socket_edits_retire_the_source_and_skill_ids_separate_source_kinds();
        WornSkillSourceCases.Worn_grants_use_real_rows_distinct_hosts_and_supported_current_sources();
    }
}
