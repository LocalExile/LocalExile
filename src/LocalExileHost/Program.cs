namespace LocalExileHost;

/// <summary>
/// The host's one window: fill in the address people outside your network will use, press 开服, and the same
/// <c>scripts/dev/live.ps1</c> the guide documents does the work (<c>-Lan</c> plus the public address and ports). Its
/// output streams into the box, so a failed build or a held port is readable instead of hidden in a console window.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}