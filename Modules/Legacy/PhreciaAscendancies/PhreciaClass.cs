namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>A whole-class capability, its explicit opt-in and its read-only installed-graph policy.</summary>
internal sealed record PhreciaClass(string Id, string Name, string OptInEnvironmentVariable, Action RequireInstalledGraph)
{
    // Only the reviewed revisions, never the whole catalog: a future graph bump must not silently admit unreviewed
    // client data. 3.29.3c ships the same PSG bytes as 3.29.3b, so both reviewed saves stay admitted.
    public bool Matches(AscendancyIdentity identity) =>
        identity is { Family: AscendancyFamily.Phrecia } && AscendancyCatalog.IsKnownRevision(identity.Revision)
        && identity.Id == Id;

    public bool Enabled => Environment.GetEnvironmentVariable(OptInEnvironmentVariable) == "1";

    public void RequirePlayable()
    {
        if (!Enabled)
            throw new AscendancyRuleException($"{Name} play requires {OptInEnvironmentVariable}=1 and the separately "
                + $"reviewed 3.29.3b combined passive graph; see Agents/docs/game/{Name.ToLowerInvariant()}.md");
        RequireInstalledGraph();
    }
}
