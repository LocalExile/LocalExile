# External party AI through MCP

PlayerBots exposes observations and bounded decisions; the game simulation owns movement, skills and saves.
No model or provider is bundled. Any MCP client can attach an external model through the stdio adapter or local HTTP.
The server does not send observations to an AI service on its own.

## Connection

The module settings `mcpEnabled`, `mcpPort` and `mcpLeaseSeconds` enable the endpoint, select its port and set the
control timeout. Defaults are disabled, port 6194 and 30 seconds. Operator overrides belong to the host's ordinary
module settings storage, not edits to the declaration in `settings.json`. Rebuilding the module reloads it;
changing these settings takes effect on its next load.

The endpoint is `http://127.0.0.1:6194/mcp/`. It binds only loopback and uses a generated bearer key in
`data/modules/PlayerBots-mcp.key`. The key is not an MCP resource or a log value. Browser-origin requests are refused.
An external host needs a trusted local bridge/tunnel; this module does not open a public listening interface.

`mcp-client.example.json` is a stdio registration template. Its Python adapter needs only Python 3.10+:

```json
{
  "mcpServers": {
    "localexile-playerbots": {
      "command": "python",
      "args": ["<repo>/Modules/Custom/PlayerBots/mcp.py", "--root", "<repo>", "--port", "6194"]
    }
  }
}
```

The adapter reads the private key locally, disables HTTP proxies/redirects and keeps stdout exclusively JSON-RPC.
It does not launch a server or client. The PlayerBots module must already be running in an enabled league.
Native HTTP clients use the bearer key themselves. Both current per-request MCP 2026-07-28 and initialization-based
2025-11-25/2025-06-18 are supported. HTTP uses JSON replies; unsolicited SSE subscriptions are not offered.

## Discovery and operation

The MCP carries its own operational guide: initialize/discover instructions, `playerbots://rules`,
`playerbots://configuration`, `playerbots://commands` and the `party_defender` prompt.
The configuration resource reports effective settings and enforced limits, without credentials.

| Tool | Purpose |
|---|---|
| `get_configuration` | Effective module configuration and limits |
| `list_bots` | Stable character IDs, readiness, leader and public lease owner |
| `observe_bot` | Current epoch, leader route, party, resources, skills, enemies, loot and inventory |
| `claim_bot` | Exclusive control of one ready bot; returns its private lease ID |
| `renew_control` | Renew that lease before the wall-clock deadline |
| `release_bot` | Clear pending intent and resume ordinary AI |
| `command_bot` | Queue an action with lease ID, current epoch and increasing sequence |

Actions are `auto`, `hold`, `move`, `attack`, `cast`, `focus`, `flask` and `pickup`.
The advertised tool schema supplies their parameters. A retry uses the same sequence and identical payload, so it
does not execute twice. A later sequence replaces the pending decision. `lastCommand` distinguishes queued, issued,
applied, rejected and expired orders; issued means the native request was processed, not that it hit or paid a cost.
The ordinary skill/flask/inventory rules can still refuse execution.

External decisions are useful at tactical intervals, not every simulation frame. An AI can observe, select a role,
focus a threat, reposition or cast, then observe the outcome. Only server-known skills and nearby targets are usable.
Reported supported mana cost precedes conditional cost conversions; actual payment stays in the ordinary skill path.

## Ownership and fallback

Each bot has an independent lease. Other controllers cannot steal an active lease. Expiry uses wall time even while
the game is paused. Orders expire after at most five game seconds; missing decisions fall back to ordinary party AI.
Explicit holds suppress autonomous attacks/idle wandering while following remains active.

Human control and party safety remain authoritative: only bots already in a live human-led party can be claimed.
The ordinary 60-cell distance rule, short catch-up grace, emergency recovery, clear entrances and attack completion
still apply. Attack commands stay in place; pickup does not chase distant loot. Moves must remain on reachable ground
inside the leader's radius. Death/resurrection and area following stay server-managed.

An area/session change produces a new epoch. Commands from the old epoch fail; the AI observes again before acting.
Module reload releases external leases; ordinary party state survives and the AI claims the bots again.
Kicking a bot or disbanding the party clears its external control immediately.

This is tactical control of existing characters, not a console/file API or an implementation of missing game mechanics.
The MCP cannot patch the client, create items, rewrite a build, delete inventories or take over a human character.
Names and other observation strings are game data, not instructions.
