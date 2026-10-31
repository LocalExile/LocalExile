# Debugging: logs, traces and crash leads

What the servers record for troubleshooting, how to turn on more, and how to bundle it for a bug report.
The setup and the log files themselves are in [GettingStarted](../../GettingStarted.md) (step 10, "Troubleshooting").

## The logs

`scripts/dev/live.ps1` redirects each server's console to `logs\live-<server>.log` (stdout) and `.err` (stderr).

- **Timestamps.** Every line starts with the local time, `HH:mm:ss.fff`, the same clock as the client's
  `Client\logs\LatestClient.txt` and `scripts/re/frida/trace-crash.js`, so the three line up. `Game_LOG_TIMESTAMPS=0`
  turns the prefix off. Code: `ServerLog.Install` (`GameServer.Core/Diagnostics/ServerLog.cs`), called at startup by
  every server (Auth when it starts serving, Patch after its command-line GGPK tools).
- **Errors.** An unexpected exception is written whole, with its stack trace and inner exceptions
  (`ServerLog.Describe`); what a closing connection throws (`IOException`, `SocketException`, `ObjectDisposedException`)
  stays one line. A packet handler that throws names the messages it was handling:
  `[gateway] 127.0.0.1:50123 error handling 0x0024, 0x0025: System.InvalidOperationException: …`. Unhandled exceptions
  and failed fire-and-forget tasks are written too (`unhandled exception`, `unobserved task exception`).
- **Disconnects.** The `DISCONNECT` line gives the session length and why it ended: `the client closed the connection
  (area change)`, `the client reset the connection (area change)`, `exit to character selection (0x0153)`,
  `handler error handling 0x…: …`, `error: IOException: …`, or `the client closed the connection (no area change or
  exit: a client crash or a closed game?)`.
  A run of 25 reads containing only unknown, unframed bytes closes the connection with
  `the client's stream no longer decodes`, allowing reconnection instead of leaving a deaf session open.
  Any framed message resets this run; incomplete known messages do not count as unknown bytes.
- **Earlier runs.** `live.ps1` moves the previous run's logs to `logs\archive\<yyyyMMdd-HHmmss>\` instead of deleting
  them, so a crash noticed only after a restart still has its log. A `-Only` restart's folder ends in the servers it
  restarted (`…-gateway`); the newest 10 full runs and the newest 20 restarts are kept.
- **Hot reload.** Under `dotnet watch` (the default) `live-gateway.log` records each save and applied reload
  (`[gateway] hot reload …`), and `live-gateway.err` also holds `dotnet watch`'s own lines, its compile errors among
  them. A line from before a reload ran the old code. Tips: [live testing](live-testing.md) ("hot reload").

## Packet history

The Gateway keeps each connection's last 256 messages in both directions (C→S as framed; S→C as written through `Send`,
before encryption): time, direction, id, length and the first 48 bytes (`PacketHistory`). The clock messages are left
out, as they would push everything else out within seconds: the C→S 0x1b ticks, their 0x1c answers and the S→C 0x2b5
frame ends. The 0x11 that starts encryption goes out before the history exists. A client crash
usually follows a message the server sent wrong or left out (3a718b29: the off-hand stats were never sent and the client
faulted one attack later), and the server only sees the disconnect, so these are the first lead.

- After a handler error, a connection error or a disconnect with no area change or exit, the log prints the last 32 as
  `[gateway] <remote> last 32 of N packet(s) (before …):` followed by one indented line per message.
- `Game_PACKET_DUMPS=<folder>` writes all of them to `<folder>\packets-<time>-<port>.txt` at every disconnect, with a
  header naming the character, area, seed and reason; the newest 60 files are kept. `live.ps1` gives the servers it
  starts `logs\packets` (relative to the checkout) unless the shell sets it, without changing the shell's own value;
  `0` turns it off.
- `/lastpackets [n]` (in chat) prints the last n (default 64) to the Gateway log.

## Dev traces

`Game_TRACE=<category>[,<category>…]` (or `all`) adds log lines that are too many for every run:

| Category | Lines |
|---|---|
| `move` | `[move-trace] qpc=…` for C→S 0x24/0x25/0x190/0x191 as read and S→C 0x1ab/0x1b7/0x1ba as written, on the QPC clock of `scripts/re/frida/trace-movement.js` ([07](../protocol/07-latency-visibility-movement.md)); `Game_MOVE_TRACE=1` still works |
| `latency` | a `[latency]` summary every interval instead of only slow ones ([07](../protocol/07-latency-visibility-movement.md)); `Game_LATENCY_METRICS=1` still works |
| `ticks` | every 50th 0x1b tick with its bytes |
| `ai` | every monster chase order (0x1ab); a monster giving up is always logged |
| `packets` | every message in both directions but the clock messages, in full hex (`[packets] …`, besides the usual 96-byte line): large logs, for short sessions |

Set it in the PowerShell session before `live.ps1`, for example `$env:Game_TRACE = 'move,packets'`.

## Client crashes

1. Read the end of `logs\live-gateway.log`: the `DISCONNECT` line and the `last … packet(s)` block before it, and the
   matching `logs\packets\` file.
2. Compare the times with `Client\logs\LatestClient.txt`.
3. For the faulting instruction, attach `scripts/re/frida/trace-crash.js` before reproducing it:
   `python scripts/re/frida/run.py Game.patched.exe scripts/re/frida/trace-crash.js --log logs/client-crash.jsonl`.
   Each native exception is sent with the time, the faulting address as a Ghidra VA, the memory access, the registers,
   a backtrace and the ids of the last 64 S→C messages a handler group accepted in `0x14299e300`
   ([02](../protocol/02-instance-connection.md) §5), without the 0x1c tick answers and 0x2b5 frame ends. That the call's
   return value is the acceptance is read from the pump, not yet checked live: if `lastReceived` stays empty, it is not.
   `run.py` prints `"reason": "process-terminated"` when the client goes. Load it on its own, not after `common.js`,
   whose exception handler reports the same exceptions.

## Bug reports

`.\scripts\dev\bugreport.ps1` writes `logs\bugreports\bugreport-<time>.zip`: this run's server logs, the newest 5 packet
dumps (`-Packets <n>`), `LatestClient.txt` and `info.txt` (revision, branch, local changes, last commits, .NET and
Windows versions, the `Game_*` settings). `-Previous` adds the previous run's logs from `logs\archive`. It reads the logs
without locking them, so run it while the servers and the client are still up.

Account and character names, machine ids, digests and the Windows user name become `<account1>`, `<character1>`,
`<redacted>` and `<user>`, names also in their hex forms (UTF-8 and UTF-16, as packet bytes show them, `<character1-hex>`);
`-NoRedact` keeps them. Names match in their exact case, so an account called `Player` leaves "player(s)" alone. They
are found in this run's and the archived logs: Auth's logins (`Account = "…"`), character creation and deletions, the
Gateway's tickets, player objects and hideout visits, and the packet-dump headers. Names under 3 characters, and a name
the logs never introduce that way, stay as they are: read the zip before attaching it to an issue.
