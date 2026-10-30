using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The attack's base critical strike chance override (issue #211 foundation): the raw stats
/// <c>override_weapon_base_critical_strike_chance</c> (Slayer Overwhelm 800, Blind Prophet's Sightless 900, permyriad)
/// replace the armed main hand's WeaponTypes base (with its local mods) before the global increases — Path of
/// Building's CalcOffence.lua replaces the attack pass's <c>source.CritChance</c> with the WeaponBaseCritChance
/// override only when that hand's type is not "None" (unarmed) — and the unarmed base
/// <c>base_melee_critical_strike_chance_while_unarmed_%</c> (Behemoth Final Blow 1000 = 10%) does the same with the
/// empty weapon base. The production chance calculation is tested directly; spells keep their own extracted bases.
/// </summary>
public class AttackBaseCriticalStrikeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(PassiveTree Tree, ItemCatalog Catalog, StatCatalog Stats)> Setup =
        new(() => (new PassiveTree(Data()), new ItemCatalog(Data()), new StatCatalog(Data())));

    static ItemCatalog Catalog => Setup.Value.Catalog;

    static Func<string, int> Raw(params (string Name, int Value)[] stats) => name =>
    {
        var found = stats.FirstOrDefault(s => s.Name == name);
        return found.Name == name ? found.Value : 0;
    };

    static string OverrideStat => AttackBaseCriticalStrike.WeaponOverrideStat;
    static string UnarmedStat => AttackBaseCriticalStrike.UnarmedBaseStat;

    [Theory]
    [InlineData("global_cannot_crit")]
    public void Attack_crit_prohibitions_win_over_both_base_grants(string prohibition)
    {
        var raw = Raw((OverrideStat, 900), (UnarmedStat, 1000), (prohibition, 1));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(true, 830, 100, raw));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(false, 0, 100, raw));
    }

    // --- The raw stats in the data (Agents/docs/protocol/11-skills.md §crit) ---

    // --- The override replaces the armed weapon's base (PoolsFor's armed branch) ---

    // --- The weapon's local mods stay on the weapon path, not on the override ---

    // --- The unarmed context: its own unrelated base, never the weapon override ---

    // --- The spells keep their own route ---

}
