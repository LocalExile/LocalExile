namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>A whole-class capability, its explicit opt-in and its read-only installed-graph policy.</summary>
internal sealed record PhreciaClass(string Id, string Name, string OptInEnvironmentVariable, Action RequireInstalledGraph)
{
    // Keep the literal revision: a future catalog bump must not silently admit unreviewed client data.
    public bool Matches(AscendancyIdentity identity) =>
        identity is { Family: AscendancyFamily.Phrecia, Revision: "3.29.3b" } && identity.Id == Id;

    public bool Enabled => Environment.GetEnvironmentVariable(OptInEnvironmentVariable) == "1";

    public void RequirePlayable()
    {
        if (!Enabled)
            throw new AscendancyRuleException($"{Name} play requires {OptInEnvironmentVariable}=1 and the separately "
                + $"reviewed 3.29.3b combined passive graph; see Agents/docs/game/{Name.ToLowerInvariant()}.md");
        RequireInstalledGraph();
    }
}
