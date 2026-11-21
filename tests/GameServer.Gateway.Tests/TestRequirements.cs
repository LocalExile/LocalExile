namespace GameServer.Gateway.Tests;

/// <summary>
/// The fixtures equip level 20 gems and weapons on low level test characters. Their requirements are waived through the
/// hook a league system uses (GameEvents.RequirementExemptions), as the 1000 requirement attributes set through the
/// removed conn.PlayerStats did, so the worn items stay active.
/// </summary>
static class TestRequirements
{
    /// <summary>Everything <paramref name="conn"/> wears counts as meeting its requirements until disposed. The owner is
    /// the Game module, which every test league lists.</summary>
    internal static IDisposable Waive(Player player) => GameEvents.RequirementExemptions.Add("Game",
        c => ReferenceEquals(c, player) ? [.. global::Game.ItemsOf(c).EquipmentSnapshot().Select(e => e.Item.Id)] : []);
}
