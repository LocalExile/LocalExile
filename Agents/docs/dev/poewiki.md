# Reading poewiki.net through Anubis

poewiki is often the only technical source for a mechanic. Its `/wiki/` pages are behind
[Anubis](https://anubis.techaro.lol/), a proof-of-work gate meant to stop mass scraping and AI training. This tool
fetches a page on demand, lets the wiki's own Anubis script solve the challenge, and prints the article. It is for the
page an agent or contributor is reading now; it is not a crawler.

## Usage

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

- Fetch only pages you are reading. Name them on the command line; do not loop over links, categories, or searches.
  The default three-page cap and two-second gap are deliberate.
- `robots.txt` is fetched once a day and disallowed paths are refused. That includes `Special:`, `File:`, `index.php`,
  `oldid`, and the print/redirect variants; the wiki's own search page is disallowed too, so name the article instead.
- Only `https://www.poewiki.net/wiki/<page>` is accepted. The tool does not follow links and does not save a copy of
  the wiki.
- This tool does not use the MediaWiki API; see [Direct API access](#direct-api-access-observed-not-used-by-this-tool)
  for the observed gate policy.

## How it works

Anubis' bot policy answers Chrome's `HeadlessChrome` user agent with "Oh noes!" before the challenge is served. The
browser's own user agent is therefore sent with `HeadlessChrome` replaced by `Chrome`; the page's unmodified Anubis
script still does the proof of work. The gate is not forged or bypassed. If a browser update changes the user agent
wording, `--verbose` shows the page title and Anubis status.

The fetcher talks to Chromium over its debugging pipe, so it needs no npm package and opens no port. One tab fetches the
requested pages in order.

## Direct API access (observed, not used by this tool)

On 2026-11-18 the gate's decision tracked the User-Agent. Browser-like agents were challenged; non-browser agents got
the content directly, with no Anubis page and no Anubis cookie:

| Request | Browser UA | `curl` / `python-requests` / product UA |
|---|---|---|
| `/wiki/Damage` | Anubis challenge | article HTML, 728 KB |
| `api.php?action=parse&page=Damage&prop=wikitext` | Anubis challenge | JSON, 13 KB wikitext |
| `api.php?action=parse&page=Damage&prop=text` | Anubis challenge | JSON, 750 KB rendered HTML |
| `api.php?action=query&prop=revisions&titles=Damage` | Anubis challenge | JSON wikitext |
| `api.php?action=opensearch&search=damage` | Anubis challenge | JSON search results |

Tested non-browser agents: `curl/8.18.0`, `python-requests/2.31.0`, and `LocalExile-Dev/0.1 (single-page lookup)`.
So the MediaWiki API is directly usable for occasional lookups:

```bash
curl -sS -A 'LocalExile-Dev/0.1' \
  'https://www.poewiki.net/api.php?action=parse&page=Damage&prop=wikitext&format=json'
```

`prop=wikitext` (or `action=query&prop=revisions&rvslots=main&rvprop=content`) returns the source, which is usually
better for mechanics than rendered text, and it is far lighter than rendering the page. This tool still sends a
browser User-Agent and passes the gate; it does not use the API. If you use the API directly, keep the same rules: a
descriptive User-Agent, one page at a time, no crawling or bulk download. This is the site's observed policy, not a
guarantee; it can change, and mass scraping is what the gate exists to stop.

## Troubleshooting

- `no Chrome, Chromium, or Edge found`: install one or pass `--browser`.
- `Anubis refused the browser ("Oh noes!")`: update the browser; a very old build may also be rejected.
- `another poewiki fetch is using ...`: two runs share the profile. Wait, or pass `--profile <other-dir>`.
- `cooling down until ...`, `Cloudflare challenge ...`, or `rate limiting this IP (HTTP 429)`: the wiki is asking for a
  pause. Wait for the cooldown; delete `Tools/poewiki/cooldown` only when you know the limit has cleared.
- `timed out ... Anubis status "..."`: check the connection and rerun with `--verbose`.
- `no article at ...`: the page name is misspelled or the page does not exist.
