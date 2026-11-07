# Currency Market

Real market prices from poe.ninja's public PoE 1 exchange feed drive finite local liquidity for Faustus.
These are local orders generated from observed prices and volume, not orders from the official trade market.
The mod requires this checkout's Currency Exchange API and NpcDialogue module.

Build from the server root: `dotnet build Mods/Source/CurrencyMarket -c Debug`.
Enable/disable: `/mod_enable currencymarket` / `/mod_disable currencymarket`.
Status: `/cx_market`. Re-enter the hideout after enabling if Faustus was absent.

Edit `data/mods/currencymarket/settings.json`, then run `/mod_reload currencymarket`.
Builds never overwrite settings. The mod generates this file on first load:

```json
{
  "FetchPrices": false,
  "PriceLeague": "Standard",
  "LocalLeagues": ["Standard"],
  "RefreshMinutes": 60,
  "GapPercent": 10,
  "LevelGapPercent": 2,
  "VolumeFactor": 0.05,
  "LevelVolumeMultiplier": 1.25
}
```

| Setting | Meaning |
| --- | --- |
| FetchPrices | Defaults to false: keep the saved snapshot indefinitely; true enables periodic online refresh |
| PriceLeague | poe.ninja source league |
| LocalLeagues | Local leagues receiving stock; [] serves all non-SSF leagues |
| RefreshMinutes | Price fetch interval and UTC-aligned restock interval; minimum 15 minutes |
| GapPercent | Total gap around reference: 10 means buy at -5% and sell at +5% |
| LevelGapPercent | Additional percentage points away from reference per deeper level |
| VolumeFactor | First level's units = observed traded units × this factor, on both sides; 0 disables stock |
| LevelVolumeMultiplier | Each deeper level's target volume multiplier; greater than 0, at most 2 |

There are up to six levels per side. With a 100-Chaos reference, default bids are
95, 93, 91, 89, 87, 85; asks are 105, 107, 109, 111, 113, 115.
An observation of 1,000 traded units produces volumes 50, 62, 78, 97, 122, 152.
Observed units are `volumePrimaryValue / primaryValue`.
Rates round outward into distinct integer lots; this can widen gaps further.
Each level has at most 10,000 currency units, subject to protocol limits.
Zero-volume prices, levels whose configured volume cannot fund one whole lot, unrepresentable rates
and unsupported client currencies are skipped. A pair can therefore have fewer than six levels per side.

Faustus and player-to-player Currency Exchange work independently of the mod.
The bundled Standard snapshot contains public Currency and Fragment prices captured on 2026-11-04.
On first load it is copied from `prices.default.json` into the runtime cache; existing caches are never replaced.
Prices stay pinned indefinitely by default. Set `FetchPrices` to `true` and run `/mod_reload currencymarket`
to opt into periodic online refresh. Turn it off again to retain the most recently saved snapshot indefinitely.
The cache lives at `data/mods/currencymarket/prices.json`.
Failed fetches keep the last valid cache and its original timestamp; `/cx_market` reports the failure.
With no cache, no market liquidity is generated; player orders still work.
Requests preserve the service's ETag and respect HTTP cache freshness even if RefreshMinutes is shorter.
The cache timestamp records the last successful HTTP check, not when the underlying trades occurred.
Network work runs in the background. No accounts, tokens or player data are sent.

Published stock enters the book when Currency Exchange opens or an exchange session processes a tick/request.
Exchange packet and leaving handlers register once, on the first accepted Currency Exchange interaction.
No exchange tick hook is registered until the first exchange session; unrelated players skip market work.
The hook is removed when the last tracked session switches windows or leaves. Unchanged ticks skip database work.
Trades reduce stock persistently and push depth updates; exhausted levels disappear.
Requests never replenish stock. Reloading the same snapshot within an interval preserves fills.
Stock can replenish at the configured interval while the cached price version stays fixed.
Changed prices or settings can replace stock before the next interval.
Disabling withdraws mod liquidity while preserving player orders and earned proceeds.
Gold fees and normal collection apply. SSF stays disabled.

Source and API contract: https://poe.ninja/docs/api
