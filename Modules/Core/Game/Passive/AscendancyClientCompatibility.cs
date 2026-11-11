/// <summary>Read-only admission through the optional family provider; never installs client data.</summary>
public static class AscendancyClientCompatibility
{
    public static void RequirePlayable(ClientState conn, AscendancyIdentity? selection) =>
        AscendancyPolicies.RequirePlayable(conn, selection);
}
