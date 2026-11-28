using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

// A Move or a strike that arrives during a strike's walk-up or swing must start on the cell the client stands on
// (LockstepClientCell), not on the server's estimate, which runs a round trip and two frames ahead of it: an action's
// 0x1ab applies the client onto its cell pair whatever the distance, so a pair taken from the estimate jumps the
// player. The client is a second PlayerWalk fed by the same rules, two frames behind the server (LockstepClientCell's
// own term), so it is an independent record of where the player is. Each start here is checked against it.
public class LockstepStrikeChainTests
{
    const double Frame = .033;
    const ushort Slot = 0x8001, Flags = 0x0400, MoveSkill = 0x2909;

    // The player's 0x1ab messages the server wrote, decoded: from -> aim of its cell pair.
    // 01AB | u32 object | u32 0 | u16 header | u16 flags | posX posY aimX aimY | u16 skill | u16 sequence
    static List<((int X, int Y) From, (int X, int Y) To)> PlayerStarts(IEnumerable<string> lines)
    {
        var starts = new List<((int X, int Y), (int X, int Y))>();
        foreach (string line in lines)
        {
            var m = Regex.Match(line, @"=> 0x01ab\s+\d+ B ([0-9A-F]+)");
            if (!m.Success) continue;
            byte[] b = Convert.FromHexString(m.Groups[1].Value);
            if (b.Length < 22) continue;
            int at = 14;
            int Cell()
            {
                byte b0 = b[at++];
                if (b0 < 0x80) return b0;
                if (b0 < 0xc0) return (b0 & 0x3f) << 8 | b[at++];
                if (b0 < 0xe0) return (b0 & 0x1f) << 16 | b[at++] << 8 | b[at++];
                if (b0 < 0xf0) return (b0 & 0x0f) << 24 | b[at++] << 16 | b[at++] << 8 | b[at++];
                return b[at++] << 24 | b[at++] << 16 | b[at++] << 8 | b[at++];
            }
            starts.Add(((Cell(), Cell()), (Cell(), Cell())));
        }
        return starts;
    }

    [Fact]
    public async Task Strike_move_strike_move_starts_each_action_where_the_client_stands()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        player.Connection.LockstepClient = true;
        // Viper Strike needs its weapon class (a Rusted Sword); the fixture's socketed host gets swapped.
        Game.ItemsOf(player).Change(PlayerInventories.Weapon1,
            Game.ItemsOf(player).Items(PlayerInventories.Weapon1).Single().Id,
            _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        ushort skill = Assert.Single(fixture.Equip(("Viper Strike", 20)));
        int reach = Math.Max(Game.WeaponFor(player).Range, 1) - 1;
        // The live client on loopback answers a round trip in 100 ms and applies the frames two later (07 section 6):
        // the client's cell is that far behind the server's estimate, which is what LockstepClientCell answers.
        player.Connection.LockstepLatency.Add(100);
        double Lag = .1 + 2 * Frame;
        player.Walk.Teleport(100, 100, 0);
        // The client's own copy of the player: it takes each 0x1ab's pair a round trip and two frames after the
        // server sent it, then walks the order from there (in reach of the aim), as its own frames run it.
        var client = new PlayerWalk(100, 100, player.Walk.Speed);
        string[] Sent() => [.. Program.HistoryFor(fixture.Ciphers).Lines()];
        var trace = new List<string>();
        int seen = 0;

        void Take(int n)
        {
            var starts = PlayerStarts(Sent());
            for (; seen < starts.Count; seen++)
            {
                var (from, to) = starts[seen];
                // Frame n's 0x1ab arrives and executes on the client at n * Frame + Lag (after frames 0..n-1 have stepped);
                // compare `from` against the client's cell at THAT execution instant, not Lag earlier.
                var now = client.Position(n * Frame + Lag);
                double jump = Math.Max(Math.Abs(from.X - now.X), Math.Abs(from.Y - now.Y));
                trace.Add($"frame {n} ({n * Frame:0.000} s): 0x1ab ({from.X},{from.Y})->({to.X},{to.Y}), client ({now.X},{now.Y})");
                Assert.True(jump <= 2,
                    $"the 0x1ab starts at ({from.X},{from.Y}) and the client stands at ({now.X},{now.Y}): {jump:0.0} cells." +
                    " Trace: " + string.Join(" | ", trace) + " | " + string.Join(" | ", Sent()));
                client.Teleport(from.X, from.Y, n * Frame + Lag);
                client.Approach(to.X, to.Y, reach, n * Frame + Lag, -1, 1);
            }
        }

        // A press runs beside the frame pass and its send waits for the wire's turn: wait for it before the frame is
        // taken, so the start is placed in the frame the press came in.
        async Task Step(int n, Action? press = null)
        {
            int before = PlayerStarts(Sent()).Count;
            press?.Invoke();
            player.Connection.LockstepFrame = (n * Frame, (n + 1) * Frame);
            await Game.StartLockstepActions(player, n * Frame);
            for (int i = 0; press is not null && i < 200 && PlayerStarts(Sent()).Count == before; i++) await Task.Yield();
            Take(n);
        }

        // The strike: a walk-up far longer than its cut-in, so the inputs below land while it runs.
        await Step(0, () => player.LockstepSkill.Press((200, 100), null, skill, Flags, repeats: true));
        Assert.True(player.LockstepAttack is { Open: true });
        for (int n = 1; n < 30; n++) await Step(n);
        await Step(30, () => player.LockstepHold.Press((100, 130), MoveSkill, Flags));
        for (int n = 31; n < 60; n++) await Step(n);
        // The second strike, over that walk.
        await Step(60, () => player.LockstepSkill.Press((100, 20), null, skill, Flags, repeats: true));
        for (int n = 61; n < 90; n++) await Step(n);
        // And a Move over its walk-up.
        await Step(90, () => player.LockstepHold.Press((100, 80), MoveSkill, Flags));
        await Step(91);
        // Held presses repeat, so the chain runs six starts (each cancel re-fires the held strike). Every start must
        // still sit on the client's cell.
        Assert.True(PlayerStarts(Sent()).Count == 6,
            "six starts: " + PlayerStarts(Sent()).Count + " busy=" + player.LockstepBusyUntil +
            " attack=" + (player.LockstepAttack is null ? "null" : "set") + " dest=" + player.Walk.Destination +
            " hold=" + player.LockstepHold + " trace=" + string.Join(" | ", trace));
        // Both strikes' starts finish; nothing hangs on a walk-up that was replaced.
        player.LockstepAttack?.Fails();
        await Task.Yield();
        Assert.Equal(6, PlayerStarts(Sent()).Count);
    }
}
