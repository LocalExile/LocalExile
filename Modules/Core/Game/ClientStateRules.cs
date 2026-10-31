/// <summary>What the connection holds for the game's rules, in the rules' types. A reload gives new types: what an old one holds starts over.</summary>
static class ClientStateRules
{
    extension(ClientState conn)
    {
        internal Game.ProjectileSession? ProjectileRuntime
        {
            get => conn.ProjectileRuntimeState as Game.ProjectileSession;
            set => conn.ProjectileRuntimeState = value;
        }

        internal Game.StatSourcesResult? StatSourcesCache
        {
            get => conn.StatSourcesCacheState as Game.StatSourcesResult;
            set => conn.StatSourcesCacheState = value;
        }
    }
}
