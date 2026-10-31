using System.Reflection;

namespace GameServer.Gateway.Tests;

// HotReload.cs reads `dotnet watch`'s own lines to tell players about a failed compile; these are the lines it saw live.
public class HotReloadTests
{
    static string CompileError(string line) => (string)typeof(Program).Assembly.GetType("HotReload")!
        .GetMethod("CompileError", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [line])!;

    [Theory]
    [InlineData(@"dotnet watch ❌ C:\Users\Frost\Desktop\Projects\LocalExile\src\GameServer.Gateway\Program.Commands.cs(1502,74): error CS1002: ; expected",
        "Program.Commands.cs(1502,74): CS1002 ; expected")]
    [InlineData(@"dotnet watch ❌ C:\repo\src\GameServer.Gateway\HotReload.cs(26,42): error CS0103: The name 'ReadWatchOutput' does not exist in the current context",
        "HotReload.cs(26,42): CS0103 The name 'ReadWatchOutput' does not exist in the current context")]
    // MSBuild's form, with the project in brackets.
    [InlineData(@"C:\repo\src\GameServer.Core\World\Progression.cs(10,5): error CS1525: Invalid expression term ')' [C:\repo\src\GameServer.Core\GameServer.Core.csproj]",
        "Progression.cs(10,5): CS1525 Invalid expression term ')'")]
    [InlineData("src/GameServer.Gateway/Program.cs(3,1): error CS0116: A namespace cannot directly contain members",
        "Program.cs(3,1): CS0116 A namespace cannot directly contain members")]
    public void A_compile_error_line_becomes_file_line_code_and_message(string line, string expected) =>
        Assert.Equal(expected, CompileError(line));

    [Fact]
    public void A_line_without_a_compile_error_is_kept() =>
        Assert.Equal("dotnet watch 🔥 Unable to apply changes", CompileError("dotnet watch 🔥 Unable to apply changes"));
}
