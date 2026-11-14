namespace GameServer.Core.Tests;

// Tests of what only a set-up checkout or one system has. Without it such a test shows as skipped, rather than passing
// without having run.

/// <summary>Needs data/client.db, the client's tables (TestTables.Real).</summary>
sealed class ClientTablesFactAttribute : FactAttribute
{
    public ClientTablesFactAttribute()
    {
        if (TestTables.RealPath is null) Skip = "needs data/client.db (a server or the Gateway tests build it)";
    }
}

/// <summary>Needs the client itself, Client/Game.exe above the test's folder.</summary>
sealed class ClientExeFactAttribute : FactAttribute
{
    public ClientExeFactAttribute()
    {
        if (Exe is null) Skip = "needs Client/Game.exe";
    }

    internal static string? Exe
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Path.Combine(dir.FullName, "Client", "Game.exe") is var exe && File.Exists(exe)) return exe;
            return null;
        }
    }
}

/// <summary>Only Windows behaves this way.</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}
