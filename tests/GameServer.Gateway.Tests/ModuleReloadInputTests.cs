namespace GameServer.Gateway.Tests;

public class ModuleReloadInputTests
{
    [Fact]
    public void Published_modules_watch_custom_assembly_paths_and_script_inputs_but_not_runtime_state()
    {
        string root = Path.Combine(Path.GetTempPath(), "module-with-custom-layout");
        bool Input(string path) => ModHost.IsModuleInput(root, "plugins/runtime.dll", Path.Combine(root, path));
        Assert.False(Input("status.txt"));
        Assert.False(Input("logs/status.txt"));
        Assert.False(Input("state/save.json"));
        foreach (string change in new[] { "plugins", "plugins/runtime.dll", "plugins/runtime.pdb", "module.json", "Rules.cs" })
            Assert.True(Input(change), change);
    }
}
