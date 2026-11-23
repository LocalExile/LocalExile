#!/usr/bin/env node
// Fetch poewiki.net articles through Anubis, one page at a time, on demand.
//
// The wiki gates /wiki/ pages behind Anubis (a proof-of-work challenge). This runs a real headless Chromium with its
// own persistent profile, lets the wiki's own Anubis JavaScript solve the challenge, and prints the article. Nothing
// is crawled: one invocation fetches the pages named on the command line (at most --max-pages) and stops. robots.txt
// is fetched once a day and disallowed paths are refused. The MediaWiki API is not used: the site serves the API (and
// article HTML) un-gated to non-browser User-Agents, but this tool uses a browser agent and passes the gate as a
// normal reader would. See Agents/docs/dev/poewiki.md for the observed policy.
//
// Anubis' bot policy refuses the "HeadlessChrome" user agent, so the browser's own user agent is sent with that word
// replaced by "Chrome". The challenge is still solved by the wiki's unmodified page script; nothing is injected.
//
// Requirements: Node.js 24+ and a current Chrome, Chromium, or Edge. The profile (cookies, robots cache) is kept in
// Tools/poewiki, which Git ignores.
//
// Usage:
//   node scripts/dev/poewiki.mjs Damage
//   node scripts/dev/poewiki.mjs "Damage" "Armour" --json
//   node scripts/dev/poewiki.mjs https://www.poewiki.net/wiki/Energy_shield --html
//   node scripts/dev/poewiki.mjs "Hit" --out Tools/poewiki/hit.html --html
//
// See Agents/docs/dev/poewiki.md.

import { spawn, spawnSync } from 'node:child_process';
import {
  existsSync, mkdirSync, readFileSync, rmSync, statSync, unlinkSync, writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const BASE = new URL('https://www.poewiki.net/');
const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const DEFAULT_PROFILE = join(REPO, 'Tools', 'poewiki');
const DEFAULT_TIMEOUT_S = 45;
const DEFAULT_MAX_PAGES = 3;
const HARD_MAX_PAGES = 5;
const DEFAULT_DELAY_MS = 2000;
const ROBOTS_MAX_AGE_MS = 24 * 60 * 60 * 1000;

const HELP = `Fetch poewiki.net articles through the Anubis gate, on demand.

Usage: node scripts/dev/poewiki.mjs [options] <page|url>...

  Damage, "Energy shield"      page names
  https://www.poewiki.net/wiki/Damage
                               a poewiki article URL

Options:
  --json             print one JSON object (title, url, text, and html with --html)
  --html             print the article's rendered HTML instead of its text
  --out <file>       write the output to a file instead of stdout
  --browser <path>   Chrome, Chromium, or Edge executable
  --profile <dir>    browser profile and robots cache (default ${DEFAULT_PROFILE})
  --timeout <s>      wait for the Anubis gate and the article (default ${DEFAULT_TIMEOUT_S}, max 300)
  --max-pages <n>    refuse more than n pages per run (default ${DEFAULT_MAX_PAGES}, max ${HARD_MAX_PAGES})
  --delay <s>        wait between pages (default ${DEFAULT_DELAY_MS / 1000})
  --verbose          print browser stderr and progress
  -h, --help

Use only for a page you are reading now. Do not script bulk fetches.
`;

class BackoffError extends Error {}

function die(message, code = 1) {
  process.stderr.write(`poewiki: ${message}\n`);
  process.exit(code);
}

function usage(message) {
  process.stderr.write(`poewiki: ${message}\n\n${HELP}`);
  process.exit(2);
}

let cli;
try {
  cli = parseArgs({
    allowPositionals: true,
    options: {
      json: { type: 'boolean' },
      html: { type: 'boolean' },
      verbose: { type: 'boolean' },
      help: { type: 'boolean', short: 'h' },
      browser: { type: 'string' },
      profile: { type: 'string' },
      timeout: { type: 'string' },
      'max-pages': { type: 'string' },
      delay: { type: 'string' },
      out: { type: 'string' },
    },
  });
} catch (error) {
  usage(error.message);
}

if (cli.values.help) {
  process.stdout.write(HELP);
  process.exit(0);
}
if (cli.positionals.length === 0) usage('name at least one page or URL');

function numberOption(value, fallback, name, min, max) {
  if (value === undefined) return fallback;
  const n = Number(value);
  if (!Number.isFinite(n) || n < min || n > max) {
    usage(`${name} must be a number from ${min} to ${max}`);
  }
  return n;
}

const timeoutMs = numberOption(cli.values.timeout, DEFAULT_TIMEOUT_S, '--timeout', 5, 300) * 1000;
const maxPages = numberOption(cli.values['max-pages'], DEFAULT_MAX_PAGES, '--max-pages', 1, HARD_MAX_PAGES);
const delayMs = numberOption(cli.values.delay, DEFAULT_DELAY_MS / 1000, '--delay', 0, 60) * 1000;
const profileDir = resolve(cli.values.profile ?? DEFAULT_PROFILE);
const json = cli.values.json === true;
const html = cli.values.html === true;
const verbose = cli.values.verbose === true;

let browserChild = null;
let lockFile = null;

process.on('exit', () => {
  if (browserChild) {
    try { browserChild.kill('SIGKILL'); } catch { /* already gone */ }
  }
  if (lockFile) {
    try { unlinkSync(lockFile); } catch { /* already removed */ }
  }
});
for (const [signal, code] of [['SIGINT', 130], ['SIGTERM', 143]]) {
  process.once(signal, () => process.exit(code));
}

if (cli.positionals.length > maxPages) {
  usage(`${cli.positionals.length} pages requested; --max-pages is ${maxPages}. Fetch only what you are reading.`);
}

const COOLDOWN_MS = 5 * 60 * 1000;
const cooldownPath = () => join(profileDir, 'cooldown');

function activeCooldown() {
  try {
    const until = Date.parse(readFileSync(cooldownPath(), 'utf8').split('\n')[0]);
    if (Number.isFinite(until) && until > Date.now()) return new Date(until);
  } catch {
    // No cooldown recorded.
  }
  return null;
}

function setCooldown(reason) {
  mkdirSync(profileDir, { recursive: true });
  writeFileSync(cooldownPath(), `${new Date(Date.now() + COOLDOWN_MS).toISOString()}\n${reason}\n`);
}

function clearCooldown() {
  try { unlinkSync(cooldownPath()); } catch { /* already absent */ }
}

function platformUa() {
  const platform = process.platform === 'win32'
    ? 'Windows NT 10.0; Win64; x64'
    : process.platform === 'darwin'
      ? 'Macintosh; Intel Mac OS X 10_15_7'
      : 'X11; Linux x86_64';
  return `Mozilla/5.0 (${platform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36`;
}

// --- pages and robots -----------------------------------------------------------------------------------------------

function articleUrl(input) {
  const raw = input.trim();
  if (!raw) throw new Error('empty page name');
  let url;
  if (/^https?:\/\//i.test(raw)) {
    url = new URL(raw);
    if (url.protocol !== 'https:') throw new Error(`${raw}: only https URLs are accepted`);
    if (url.hostname !== 'www.poewiki.net' && url.hostname !== 'poewiki.net') {
      throw new Error(`${raw}: only www.poewiki.net is accepted`);
    }
    if (!url.pathname.startsWith('/wiki/')) {
      throw new Error(`${raw}: only /wiki/<page> article URLs are accepted`);
    }
    if (url.search || url.hash) {
      throw new Error(`${raw}: query strings and fragments are not used; name the article page itself`);
    }
  } else {
    url = new URL(`/wiki/${encodeURIComponent(raw.replace(/ /g, '_'))}`, BASE);
  }
  url.hostname = 'www.poewiki.net';
  return url;
}

function parseRobots(text) {
  const groups = [];
  let current = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/#.*/, '').trim();
    if (!line) continue;
    const match = /^([A-Za-z-]+)\s*:\s*(.*)$/.exec(line);
    if (!match) continue;
    const field = match[1].toLowerCase();
    const value = match[2].trim();
    if (field === 'user-agent') {
      if (!current || current.rules.length > 0) {
        current = { agents: [], rules: [] };
        groups.push(current);
      }
      current.agents.push(value.toLowerCase());
    } else if (field === 'allow' || field === 'disallow') {
      if (!current) {
        current = { agents: ['*'], rules: [] };
        groups.push(current);
      }
      if (value === '') continue; // an empty Disallow allows everything
      current.rules.push({ allow: field === 'allow', pattern: value });
    }
  }

  const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const matcher = (pattern) => {
    const anchored = pattern.endsWith('$');
    const body = anchored ? pattern.slice(0, -1) : pattern;
    return new RegExp(`^${body.split('*').map(escape).join('.*')}${anchored ? '$' : ''}`);
  };

  return {
    allowed(url, userAgent) {
      const target = `${url.pathname}${url.search}`;
      const ua = userAgent.toLowerCase();
      const rules = [];
      for (const group of groups) {
        if (!group.agents.some((agent) => agent === '*' || ua.includes(agent))) continue;
        for (const rule of group.rules) {
          const match = matcher(rule.pattern).exec(target);
          if (match) rules.push({ allow: rule.allow, length: match[0].length });
        }
      }
      if (rules.length === 0) return true;
      rules.sort((a, b) => b.length - a.length || (a.allow === b.allow ? 0 : a.allow ? -1 : 1));
      return rules[0].allow;
    },
  };
}

async function robotsFor(userAgent) {
  const cache = join(profileDir, 'robots.txt');
  try {
    if (Date.now() - statSync(cache).mtimeMs < ROBOTS_MAX_AGE_MS) {
      return parseRobots(readFileSync(cache, 'utf8'));
    }
  } catch {
    // Fetch it below.
  }
  const response = await fetch(new URL('/robots.txt', BASE), {
    headers: { 'user-agent': userAgent },
    redirect: 'follow',
    signal: AbortSignal.timeout(15_000),
  });
  if (!response.ok) {
    if (response.status === 429 || response.status === 403 || response.status >= 500) {
      throw new BackoffError(`robots.txt: HTTP ${response.status}; poewiki asked us to slow down`);
    }
    throw new Error(`robots.txt: HTTP ${response.status}`);
  }
  const text = await response.text();
  writeFileSync(cache, text);
  return parseRobots(text);
}

// --- browser -------------------------------------------------------------------------------------------------------

function findOnPath(name) {
  const command = process.platform === 'win32' ? 'where' : 'which';
  const result = spawnSync(command, [name], { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) return null;
  const first = result.stdout.split(/\r?\n/).map((l) => l.trim()).find(Boolean);
  return first && existsSync(first) ? first : null;
}

function findBrowser(explicit) {
  if (explicit) {
    const path = resolve(explicit);
    if (!existsSync(path)) throw new Error(`--browser ${explicit} does not exist`);
    return path;
  }
  const env = process.env.LOCALEXILE_BROWSER;
  if (env) {
    if (!existsSync(env)) throw new Error(`LOCALEXILE_BROWSER=${env} does not exist`);
    return env;
  }
  const candidates = [];
  if (process.platform === 'win32') {
    const roots = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.LOCALAPPDATA];
    for (const root of roots.filter(Boolean)) {
      candidates.push(join(root, 'Google', 'Chrome', 'Application', 'chrome.exe'));
      candidates.push(join(root, 'Microsoft', 'Edge', 'Application', 'msedge.exe'));
      candidates.push(join(root, 'Chromium', 'Application', 'chrome.exe'));
    }
    candidates.push('chrome', 'msedge', 'chromium');
  } else if (process.platform === 'darwin') {
    candidates.push(
      '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
      '/Applications/Chromium.app/Contents/MacOS/Chromium',
      '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    );
    candidates.push('google-chrome', 'chromium', 'microsoft-edge');
  } else {
    candidates.push('google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser', 'microsoft-edge');
  }
  for (const candidate of candidates) {
    if (candidate.includes('/') || candidate.includes('\\')) {
      if (existsSync(candidate)) return candidate;
    } else {
      const found = findOnPath(candidate);
      if (found) return found;
    }
  }
  throw new Error('no Chrome, Chromium, or Edge found; install one or pass --browser <path>');
}

class CdpPipe {
  constructor(child) {
    this.child = child;
    this.nextId = 1;
    this.pending = new Map();
    this.buffer = Buffer.alloc(0);
    this.exited = null;
    this.stderrTail = [];

    if (!child.stdio[3] || !child.stdio[4]) {
      this.exited = { error: new Error('browser debugging pipes are unavailable') };
      try { child.kill('SIGKILL'); } catch { /* already gone */ }
      return;
    }

    child.stderr.on('data', (chunk) => {
      const text = chunk.toString();
      if (verbose) process.stderr.write(`[browser] ${text}`);
      this.stderrTail.push(text);
      while (this.stderrTail.length > 12) this.stderrTail.shift();
    });

    child.stdio[4].on('data', (chunk) => this.onData(chunk));
    child.on('exit', (code, signal) => {
      this.exited = { code, signal };
      const message = `browser exited (code ${code}, signal ${signal})`;
      for (const { reject } of this.pending.values()) reject(new Error(message));
      this.pending.clear();
    });
    child.on('error', (error) => {
      this.exited = { error };
      for (const { reject } of this.pending.values()) reject(error);
      this.pending.clear();
    });
  }

  onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    for (;;) {
      const end = this.buffer.indexOf(0);
      if (end < 0) return;
      const raw = this.buffer.subarray(0, end).toString('utf8');
      this.buffer = this.buffer.subarray(end + 1);
      if (!raw.trim()) continue;
      let message;
      try {
        message = JSON.parse(raw);
      } catch {
        if (verbose) process.stderr.write(`[browser] unparsable CDP message: ${raw.slice(0, 200)}\n`);
        continue;
      }
      const entry = message.id ? this.pending.get(message.id) : null;
      if (!entry) continue;
      this.pending.delete(message.id);
      if (message.error) {
        entry.reject(new Error(`${message.error.message ?? 'CDP error'}${message.error.data ? `: ${message.error.data}` : ''}`));
      } else {
        entry.resolve(message.result);
      }
    }
  }

  send(method, params = {}, sessionId) {
    if (this.exited) throw new Error(`browser is not running (${this.describeExit()})`);
    const id = this.nextId++;
    const message = { id, method, params };
    if (sessionId) message.sessionId = sessionId;
    const promise = new Promise((resolvePromise, reject) => {
      this.pending.set(id, { resolve: resolvePromise, reject });
    });
    try {
      this.child.stdio[3].write(`${JSON.stringify(message)}\0`);
    } catch (error) {
      this.pending.delete(id);
      return Promise.reject(error);
    }
    return promise;
  }

  describeExit() {
    if (!this.exited) return 'unknown';
    if (this.exited.error) return this.exited.error.message;
    const tail = this.stderrTail.join('').trim().split('\n').slice(-3).join(' | ');
    return `code ${this.exited.code}, signal ${this.exited.signal}${tail ? `: ${tail}` : ''}`;
  }

  async evaluate(sessionId, expression) {
    const response = await this.send('Runtime.evaluate', {
      expression,
      returnByValue: true,
      awaitPromise: true,
    }, sessionId);
    if (response.exceptionDetails) {
      const detail = response.exceptionDetails.exception?.description ?? response.exceptionDetails.text;
      throw new Error(`page script failed: ${detail}`);
    }
    return response.result.value;
  }

  close() {
    const child = this.child;
    if (this.exited || !child.pid) return Promise.resolve();
    return new Promise((resolvePromise) => {
      let settled = false;
      const finish = () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolvePromise();
      };
      const timer = setTimeout(() => {
        try { child.kill('SIGKILL'); } catch { /* already gone */ }
        finish();
      }, 5000);
      child.once('exit', finish);
      // A graceful close flushes the profile (cookies included); the pipe closes without a reply.
      try {
        this.send('Browser.close').catch(() => {});
      } catch {
        finish();
      }
    });
  }
}

async function withTimeout(promise, ms, message) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), ms);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

const POLL_JS = `(() => {
  const status = document.getElementById('status');
  const nav = performance.getEntriesByType('navigation')[0];
  return {
    href: location.href,
    title: document.title,
    challenge: !!document.getElementById('anubis_challenge'),
    blocked: /^oh noes!?$/i.test((document.title || '').trim()),
    cloudflare: /^just a moment\.\.\./i.test((document.title || '').trim()),
    content: !!document.querySelector('#mw-content-text'),
    missing: !!document.querySelector('.noarticletext'),
    ready: document.readyState,
    status: status ? status.textContent : null,
    httpStatus: nav && typeof nav.responseStatus === 'number' ? nav.responseStatus : null,
  };
})()`;

const EXTRACT_JS = `(() => {
  const root = document.querySelector('#mw-content-text');
  const heading = document.querySelector('#firstHeading');
  return {
    url: location.href,
    title: ((heading && heading.innerText) || document.title || '').trim(),
    text: root ? root.innerText : '',
    html: root ? root.innerHTML : '',
    missing: !!document.querySelector('.noarticletext'),
  };
})()`;

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function launchBrowser(exe, profileDir) {
  mkdirSync(profileDir, { recursive: true });
  const args = [
    '--headless=new',
    '--remote-debugging-pipe',
    `--user-data-dir=${join(profileDir, 'chrome')}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-gpu',
    '--disable-dev-shm-usage',
    '--disable-extensions',
    '--disable-sync',
    '--disable-background-networking',
    '--disable-component-update',
    '--password-store=basic',
    '--use-mock-keychain',
    '--window-size=1280,1600',
    'about:blank',
  ];
  if (verbose) process.stderr.write(`poewiki: launching ${exe}\n`);
  const child = spawn(exe, args, { stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'], windowsHide: true });
  browserChild = child;
  const cdp = new CdpPipe(child);
  try {
    const version = await withTimeout(
      cdp.send('Browser.getVersion'),
      20_000,
      'browser did not start (try --browser <path>, or update Chrome/Chromium)',
    );
    return { cdp, version };
  } catch (error) {
    await cdp.close();
    throw error;
  }
}

async function fetchArticle(cdp, sessionId, url, timeoutMs) {
  await cdp.send('Page.navigate', { url: url.href }, sessionId);
  const deadline = Date.now() + timeoutMs;
  let last = null;
  let challenged = false;
  for (;;) {
    if (Date.now() >= deadline) {
      const seen = last
        ? `title "${last.title}"${last.challenge ? `, Anubis status "${last.status ?? 'unknown'}"` : ''}${last.httpStatus ? `, HTTP ${last.httpStatus}` : ''}`
        : 'no state';
      if (last && last.cloudflare) {
        throw new BackoffError(`Cloudflare challenge did not clear after ${timeoutMs / 1000}s; wait a few minutes and try again (${seen})`);
      }
      throw new Error(`timed out after ${timeoutMs / 1000}s waiting for ${url.href} (${seen})`);
    }
    await sleep(250);
    last = await cdp.evaluate(sessionId, POLL_JS);
    if (last.httpStatus === 429) {
      throw new BackoffError(`poewiki is rate limiting this IP (HTTP 429); wait a few minutes`);
    }
    if (last.blocked) {
      throw new Error('Anubis refused the browser ("Oh noes!"); update Chrome/Chromium or pass --browser');
    }
    if (last.missing) {
      throw new Error(`no article at ${url.href}; check the page name`);
    }
    if (last.challenge && !challenged) {
      challenged = true;
      if (verbose) process.stderr.write('poewiki: solving the Anubis challenge\n');
    }
    if (last.content && !last.challenge && last.ready !== 'loading') break;
  }

  const article = await cdp.evaluate(sessionId, EXTRACT_JS);
  if (article.missing || !article.text.trim()) {
    throw new Error(`no article at ${url.href}; check the page name`);
  }
  return {
    title: article.title,
    url: article.url,
    text: article.text.trim(),
    html: article.html,
    challenged,
  };
}

function render(pages) {
  if (json) {
    const body = pages.map((page) => ({
      title: page.title,
      url: page.url,
      text: page.text,
      ...(html ? { html: page.html } : {}),
    }));
    return `${JSON.stringify({ source: 'poewiki.net', fetchedAt: new Date().toISOString(), pages: body }, null, 2)}\n`;
  }
  return pages.map((page) => {
    const content = html ? page.html : page.text;
    return `# ${page.title}\n${page.url}\n\n${content.trim()}\n`;
  }).join('\n---\n\n');
}

// --- main ----------------------------------------------------------------------------------------------------------

async function main() {
  mkdirSync(profileDir, { recursive: true });
  const cooling = activeCooldown();
  if (cooling) {
    throw new Error(`cooling down until ${cooling.toISOString()} after a rate limit; delete ${cooldownPath()} to override`);
  }
  const lock = join(profileDir, '.lock');
  let lockOwned = false;
  try {
    while (!lockOwned) {
      try {
        writeFileSync(lock, JSON.stringify({ pid: process.pid, at: new Date().toISOString() }), { flag: 'wx' });
        lockOwned = true;
        lockFile = lock;
      } catch (error) {
        if (error.code !== 'EEXIST') throw error;
        let stale = false;
        try {
          const owner = JSON.parse(readFileSync(lock, 'utf8'));
          if (!Number.isInteger(owner.pid) || Date.now() - statSync(lock).mtimeMs > 10 * 60 * 1000) {
            stale = true;
          } else {
            try { process.kill(owner.pid, 0); } catch { stale = true; }
          }
        } catch {
          stale = true;
        }
        if (stale) {
          rmSync(lock, { force: true });
          continue;
        }
        throw new Error(`another poewiki fetch is using ${profileDir}; wait for it or use --profile <dir>`);
      }
    }

    const robotsUa = platformUa();
    const robots = await robotsFor(robotsUa);
    const urls = cli.positionals.map(articleUrl);
    for (const url of urls) {
      if (!robots.allowed(url, robotsUa)) {
        throw new Error(`robots.txt disallows ${url.pathname}${url.search}; leave this page alone`);
      }
    }

    if (verbose) process.stderr.write(`poewiki: fetching ${urls.length} page(s)\n`);
    const exe = findBrowser(cli.values.browser);
    const { cdp, version } = await launchBrowser(exe, profileDir);
    const fetched = [];
    try {
      const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
      const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
      try {
        await cdp.send('Page.enable', {}, sessionId);
        await cdp.send('Network.enable', {}, sessionId);
        await cdp.send('Network.setCacheDisabled', { cacheDisabled: true }, sessionId);
        const userAgent = String(version.userAgent)
          .replace(/HeadlessChrome/g, 'Chrome')
          .replace(/HeadlessEdg/g, 'Edg');
        await cdp.send('Emulation.setUserAgentOverride', {
          userAgent,
          acceptLanguage: 'en-US,en;q=0.9',
          platform: process.platform === 'win32' ? 'Win32' : process.platform === 'darwin' ? 'MacIntel' : 'Linux x86_64',
        }, sessionId);

        for (let i = 0; i < urls.length; i++) {
          if (i > 0 && delayMs > 0) await sleep(delayMs);
          if (verbose) process.stderr.write(`poewiki: ${urls[i].href}\n`);
          fetched.push(await fetchArticle(cdp, sessionId, urls[i], timeoutMs));
        }
      } finally {
        try { await cdp.send('Target.closeTarget', { targetId }); } catch { /* browser may be closing */ }
      }
    } finally {
      await cdp.close();
      browserChild = null;
    }

    clearCooldown();
    const output = render(fetched);
    if (cli.values.out) {
      const path = resolve(cli.values.out);
      writeFileSync(path, output);
      process.stderr.write(`poewiki: wrote ${fetched.length} page(s) to ${path}\n`);
    } else {
      process.stdout.write(output);
    }
  } finally {
    if (lockOwned) {
      try { unlinkSync(lock); } catch { /* already removed */ }
      lockFile = null;
    }
  }
}

main().catch((error) => {
  if (error instanceof BackoffError) setCooldown(error.message);
  if (verbose && error.stack) process.stderr.write(`${error.stack}\n`);
  die(error.message);
});
