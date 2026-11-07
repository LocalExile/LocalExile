using System.Net;
using GameServer.Core.Chat;

// A public chat server for LocalExile's Gateways (Agents/docs/protocol/10-chat-and-commands.md §6): it transmits lines
// between characters on channels and stores nothing. Game_CHAT_BIND (default 0.0.0.0), Game_CHAT_PORT (default 6113).
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_CHAT_BIND") ?? "0.0.0.0");
int port = int.Parse(Environment.GetEnvironmentVariable("Game_CHAT_PORT") ?? "6113");

using var server = new ChatServer(bind, port, maxPerAddress: 32);
server.Start();
Console.WriteLine($"[chat] listening on {bind}:{server.Port}");

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.TrySetResult();
await stopped.Task;
