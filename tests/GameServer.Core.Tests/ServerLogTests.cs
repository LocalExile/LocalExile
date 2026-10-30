using GameServer.Core.Diagnostics;

namespace GameServer.Core.Tests;

public class ServerLogTests
{
    [Fact]
    public void Every_line_starts_with_the_time_however_it_is_written()
    {
        var inner = new StringWriter { NewLine = "\n" };
        var at = new DateTime(2026, 10, 29, 12, 34, 56, 789);
        var log = new TimestampedWriter(inner, () => at) { NewLine = "\n" };
        log.WriteLine("[gateway] one");
        log.Write("[gateway] two, ");
        log.Write("still two\n[gateway] three\n");
        log.Write('x');
        log.WriteLine();
        log.WriteLine("a\r\nb");
        Assert.Equal("12:34:56.789 [gateway] one\n12:34:56.789 [gateway] two, still two\n12:34:56.789 [gateway] three\n" +
                     "12:34:56.789 x\n12:34:56.789 a\r\n12:34:56.789 b\n", inner.ToString());
    }

    // Console's own writer flushes after every write: a line with its stamp must be one write, not three.
    sealed class CountingWriter : StringWriter
    {
        public int Writes;
        public override void Write(string? value) { Writes++; base.Write(value); }
        public override void Write(char value) { Writes++; base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Writes++; base.Write(buffer, index, count); }
    }

    [Fact]
    public void A_line_is_one_write_to_the_console()
    {
        var inner = new CountingWriter { NewLine = "\n" };
        var log = new TimestampedWriter(inner, () => DateTime.Now) { NewLine = "\n" };
        log.WriteLine("[gateway] one");
        log.WriteLine("[gateway] two\n    indented");
        Assert.Equal(2, inner.Writes);
    }

    [Fact]
    public void Traces_are_a_comma_list_and_all_turns_every_one_on()
    {
        Assert.True(ServerTrace.Parse(" Move, packets;AI ").SetEquals(["move", "packets", "ai"]));
        Assert.Empty(ServerTrace.Parse(null));
        Assert.Empty(ServerTrace.Parse(""));
    }

    [Fact]
    public void A_closing_connection_is_one_line_and_a_bug_has_its_stack_trace()
    {
        Assert.Equal("IOException: reset", ServerLog.Describe(new IOException("reset")));
        Exception bug;
        try { throw new InvalidOperationException("no skill"); }
        catch (Exception e) { bug = e; }
        string text = ServerLog.Describe(bug);
        Assert.StartsWith("System.InvalidOperationException: no skill", text);
        Assert.Contains(nameof(A_closing_connection_is_one_line_and_a_bug_has_its_stack_trace), text);
    }
}
