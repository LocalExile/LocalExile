namespace GameServer.Gateway.Tests;

public class PathOfBuildingCommandTests
{
    [Fact]
    public void HelpWorksWithoutGameDataOrSaveDatabase()
    {
        var output = new StringWriter();
        Assert.Equal(0, PathOfBuildingCommand.Run(["--help"], TextReader.Null, output, new StringWriter()));
        Assert.Contains("Community Fork", output.ToString());
        Assert.Contains("--preview", output.ToString());
    }

    [Theory]
    [InlineData(new string[] { }, "--file is required")]
    [InlineData(new[] { "--file" }, "--file needs a value")]
    [InlineData(new[] { "--file", "-" }, "requires --account and --name")]
    [InlineData(new[] { "--file", "-", "--file", "-", "--preview" }, "more than once")]
    [InlineData(new[] { "--file", "-", "--preview", "--preview" }, "more than once")]
    [InlineData(new[] { "--file", "-", "--replace", "--preview" }, "Unknown option")]
    public void BadArgumentsAreRejectedBeforeReadingOrSaving(string[] args, string message)
    {
        var error = new StringWriter();
        Assert.Equal(1, PathOfBuildingCommand.Run(args, TextReader.Null, new StringWriter(), error));
        Assert.Contains(message, error.ToString());
    }

    [Theory]
    [InlineData("not an export")]
    [InlineData("https://pobb.in/example")]
    [InlineData("<!DOCTYPE PathOfBuilding [<!ENTITY secret SYSTEM 'file:///secrets'>]><PathOfBuilding>&secret;</PathOfBuilding>")]
    public void InvalidInputReturnsAUsefulFailure(string text)
    {
        var error = new StringWriter();
        var output = new StringWriter();
        Assert.Equal(1, PathOfBuildingCommand.Run(["--file", "-", "--preview"], new StringReader(text), output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("\"ok\": false", error.ToString());
    }

    [Fact]
    public void InputLimitIsEnforcedBeforeParsing()
    {
        var error = new StringWriter();
        Assert.Equal(1, PathOfBuildingCommand.Run(["--file", "-", "--preview"],
            new StringReader(new string('A', 8 * 1024 * 1024 + 1)), new StringWriter(), error));
        Assert.Contains("8 MiB", error.ToString());
    }
}
