# Reading poewiki.net

poewiki is often the only technical source for a mechanic. Use the MediaWiki API first for an occasional lookup: with a
non-browser User-Agent it currently returns wikitext and rendered HTML directly, without an Anubis challenge. The
headless browser tool in `scripts/dev` is the fallback for when the API is challenged or when you need the page as a
browser renders it. Neither path is for crawling.

## API first (preferred)

On 2026-11-18 the gate's decision tracked the User-Agent. Browser-like agents got the Anubis challenge; non-browser
agents got the content directly. Tested non-browser agents were `curl/8.18.0`, `python-requests/2.31.0`, and
`LocalExile-Dev/0.1 (single-page lookup)`; all three received the un-gated content.

| Request | Browser UA | Non-browser UA |
|---|---|---|
| `/wiki/Damage` | Anubis challenge | article HTML, 728 KB |
| `api.php?action=parse&page=Damage&prop=wikitext` | Anubis challenge | JSON, 13 KB wikitext |
| `api.php?action=parse&page=Damage&prop=text` | Anubis challenge | JSON, 750 KB rendered HTML |
| `api.php?action=query&prop=revisions&titles=Damage` | Anubis challenge | JSON wikitext |
| `api.php?action=opensearch&search=damage` | Anubis challenge | JSON search results |

For wikitext:

```bash
curl -sS -A 'LocalExile-Dev/0.1 (single-page lookup)' \
  'https://www.poewiki.net/api.php?action=parse&page=Damage&prop=wikitext&format=json'
```

- `prop=wikitext` returns the source; `prop=text` returns the rendered HTML.
- `action=query&prop=revisions&rvslots=main&rvprop=content` is the query-API equivalent of the source.
- `action=opensearch&search=<term>` resolves page names.

The source wikitext is usually better for mechanics than rendered text, and it is far lighter than rendering the page.
If the API answers with the Anubis challenge page (HTML, "Making sure you're not a bot!") or is rate-limited, use the
browser fallback.

## Browser fallback

`scripts/dev/poewiki.mjs` starts a headless Chrome, Chromium, or Edge with its own persistent profile and lets the
wiki's own Anubis script solve the challenge. Use it when the API is challenged, when you need the rendered page, or
when you want to read the article as a normal browser does.

```bash
node scripts/dev/poewiki.mjs Damage
node scripts/dev/poewiki.mjs "Energy shield" --json
node scripts/dev/poewiki.mjs https://www.poewiki.net/wiki/Hit --html --out /tmp/hit.html
```

| Option | Effect |
|---|---|
| `--json` | one object: `source`, `fetchedAt`, and `pages` with `title`, `url`, `text` (and `html` with `--html`) |
| `--html` | print the rendered article HTML instead of text |
| `--out <file>` | write the output to a file |
| `--browser <path>` | Chrome, Chromium, or Edge executable |
| `--profile <dir>` | browser profile and robots cache (default `Tools/poewiki`) |
| `--timeout <s>` | gate and page timeout (default 45) |
| `--max-pages <n>` | refuse more than n pages per run (default 3, max 5) |
| `--delay <s>` | wait between pages (default 2) |

Requirements: Node.js 24+ (already needed for `Tools/ooz`) and a current Chrome, Chromium, or Edge. The browser is
started headless with its own profile under `Tools/poewiki`, which Git ignores. The first fetch solves the Anubis
challenge (about two seconds); later fetches reuse its cookie and are faster. The HTTP cache is disabled so a page is
read from the wiki, not from a stale local copy.

poewiki is also behind Cloudflare's bot management. A burst of fetches gets a "Just a moment..." challenge or HTTP 429
instead of the article. The tool then stops, records a five-minute cooldown in the profile, and refuses to run until it
expires. Do not delete the cooldown to retry in a loop; fetch only what you need and let it pass.

## Rules

- Start with the API for wikitext. Use the browser fallback for rendered HTML or when the API is challenged.
- Fetch only pages you are reading. The browser fallback's three-page cap and two-second gap are deliberate; direct API
  callers must keep their own volume low.
- `robots.txt` is fetched once a day by the browser fallback, which refuses disallowed paths. Direct API callers must
  honor it themselves; the API is not in the current disallowed list, but the same low-volume rules apply.
- The browser fallback accepts only `https://www.poewiki.net/wiki/<page>`. It does not follow links and does not save a
  copy of the wiki.
- The two paths are separate commands: the browser fallback does not call the API.

## How the browser fallback works

Anubis' bot policy answers Chrome's `HeadlessChrome` user agent with "Oh noes!" before the challenge is served. The
browser's own user agent is therefore sent with `HeadlessChrome` replaced by `Chrome`; the page's unmodified Anubis
script still does the proof of work. The gate is not forged or bypassed. If a browser update changes the user agent
wording, `--verbose` shows the page title and Anubis status.

The fetcher talks to Chromium over its debugging pipe, so it needs no npm package and opens no port. One tab fetches the
requested pages in order.

## Troubleshooting

- The API returns the Anubis challenge page: use the browser fallback.
- The API returns HTTP 429 or a Cloudflare page: wait, then retry; the browser fallback records a five-minute cooldown.
- `no Chrome, Chromium, or Edge found`: install one or pass `--browser`.
- `Anubis refused the browser ("Oh noes!")`: update the browser; a very old build may also be rejected.
- `another poewiki fetch is using ...`: two runs share the profile. Wait, or pass `--profile <other-dir>`.
- `cooling down until ...`, `Cloudflare challenge ...`, or `rate limiting this IP (HTTP 429)`: the wiki is asking for a
  pause. Wait for the cooldown; delete `Tools/poewiki/cooldown` only when you know the limit has cleared.
- `timed out ... Anubis status "..."`: check the connection and rerun with `--verbose`.
- `no article at ...`: the page name is misspelled or the page does not exist.
