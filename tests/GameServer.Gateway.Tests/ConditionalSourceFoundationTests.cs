namespace GameServer.Gateway.Tests;

public class ConditionalSourceFoundationTests
{
    [Fact]
    public void Conditional_subjects_and_event_amounts_keep_live_inputs_and_exact_windows()
    {
        ConditionalOperatorCoreCases.Compound_operators_fold_only_supplied_live_subjects_and_keep_source_local_values();
        ConditionalOperatorCoreCases.Event_amounts_use_exact_independent_windows_and_saturating_coefficients();
    }

    [Fact]
    public void Physical_skill_sources_keep_identity_and_irrevocable_lifetimes()
    {
        SkillSourceLifetimeCases.Shared_physical_markers_retire_old_leases_and_transfer_one_activation_writer();
        SkillSourceLifetimeCases.Accepted_socket_edits_retire_the_source_and_skill_ids_separate_source_kinds();
    }
}
