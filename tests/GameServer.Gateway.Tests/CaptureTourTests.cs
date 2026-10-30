namespace GameServer.Gateway.Tests;

// The capture tour after a seed the client cannot generate: the client logs the failure and hangs, the tour lists the
// stop as a failure and waits, and after a restart it resumes at the next stop.
public class CaptureTourTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"tour{Guid.NewGuid():N}");
    readonly string _character = $"Tour{Guid.NewGuid():N}"[..16];

    public CaptureTourTests()
    {
        Directory.CreateDirectory(_folder);
        CaptureTour.StateFile = Path.Combine(_folder, "tour-left.txt");
        CaptureTour.FailuresFile = Path.Combine(_folder, "tour-failures.txt");
        CaptureTour.ClientLog = Path.Combine(_folder, "Client.txt");
        TourRecords.File = Path.Combine(_folder, "stops.jsonl");
        CaptureTour.HashOf = area => area.StartsWith("1_") ? (ushort)0x1234 : null;
        File.WriteAllLines(Path.Combine(_folder, "tour.txt"), ["# a list", "1_1_1 11", "1_1_2 22", "1_1_3 33", "Unknown 44"]);
    }

    public void Dispose()
    {
        CaptureTour.Unload(_character);
        Directory.Delete(_folder, recursive: true);
    }

    void ClientLogs(params string[] lines) => File.AppendAllLines(CaptureTour.ClientLog, lines);

    string Start() => CaptureTour.Start(_character, Path.Combine(_folder, "tour.txt"));

    [Fact]
    public void A_stop_fails_only_when_the_client_logs_the_failure_and_the_tour_then_waits()
    {
        Assert.StartsWith("Tour of 3 (area, seed) pairs started; skipped 1 unknown: Unknown", Start());
        Assert.Equal(("1_1_1", (ushort)0x1234, 11u), CaptureTour.Next(_character));
        ClientLogs("[DEBUG Client 1] Generating level 1 area \"1_1_1\" with seed 11");
        Assert.Null(CaptureTour.Failed(_character, "1_1_1", 11));   // still generating
        Assert.False(CaptureTour.Waiting(_character));

        ClientLogs("[INFO Client 1] Terrain Generation Failure in 1_1_1 strand.tgr 11: Could not tilise edge X",
            "[CRIT Client 1] Terrain generation failed: : Could not tilise edge X");
        Assert.Equal("Terrain Generation Failure in 1_1_1 strand.tgr 11: Could not tilise edge X", CaptureTour.Failed(_character, "1_1_1", 11));
        Assert.True(CaptureTour.Waiting(_character));
        Assert.Contains("1_1_1 11: the client did not arrive (Terrain Generation Failure in 1_1_1 strand.tgr 11: Could not tilise edge X)",
            File.ReadAllText(CaptureTour.FailuresFile));
        Assert.Equal(["1_1_2 22", "1_1_3 33"], File.ReadAllLines(CaptureTour.StateFile).Where(l => !l.StartsWith('#')));
    }

    [Fact]
    public void After_a_restart_the_tour_resumes_and_skips_a_stop_the_client_failed()
    {
        Start();
        CaptureTour.Next(_character);
        // The client failed and the Gateway went down before it noticed.
        ClientLogs("[DEBUG Client 1] Generating level 1 area \"1_1_1\" with seed 11",
            "[CRIT Client 1] Terrain generation failed: : Mismatching ground types");
        CaptureTour.Unload(_character);

        Assert.Equal("Tour resumed: 0 captured, 1 missed, 2 left; 1_1_1 seed 11 failed in the client (Terrain generation failed: : Mismatching ground types).",
            CaptureTour.Resume(_character));
        Assert.True(CaptureTour.Waiting(_character));
        Assert.Equal(("1_1_2", (ushort)0x1234, 22u), CaptureTour.Next(_character));
        Assert.Single(File.ReadAllLines(CaptureTour.FailuresFile));
    }

    [Fact]
    public void After_a_restart_a_stop_that_did_not_fail_is_tried_again()
    {
        Start();
        CaptureTour.Next(_character);
        CaptureTour.Unload(_character);

        Assert.Equal("Tour resumed: 0 captured, 0 missed, 3 left.", CaptureTour.Resume(_character));
        Assert.Equal(("1_1_1", (ushort)0x1234, 11u), CaptureTour.Next(_character));
        Assert.False(File.Exists(CaptureTour.FailuresFile));
    }

    [Fact]
    public void A_saved_tour_belongs_to_its_character_and_ends_with_stop()
    {
        Start();
        CaptureTour.Unload(_character);
        Assert.Null(CaptureTour.Resume("Someone else"));
        Assert.NotNull(CaptureTour.Resume(_character));
        Assert.StartsWith("Tour stopped", CaptureTour.Stop(_character));
        Assert.False(File.Exists(CaptureTour.StateFile));
        Assert.Null(CaptureTour.Resume(_character));
    }
}
