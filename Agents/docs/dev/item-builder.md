# Item builder

A Windows desktop tool for creating test items without typing `/item` in chat. It searches the extracted
`BaseItemTypes` and `Mods` tables, lets you choose a base, rarity, item level, quality and explicit affixes, and adds
the finished item to the active character's main inventory through the Gateway's normal 0xbf update.

```powershell
.\scripts\dev\live.ps1              # starts the Gateway item-tool API (on by default)
.\scripts\dev\item-builder.ps1      # opens the Windows app
```

The app talks to the Gateway at `http://127.0.0.1:6113/` (override with `Game_ITEM_TOOL_URL` or
`Game_ITEM_TOOL_PORT`). The API is loopback-only; set `Game_ITEM_TOOL=0` to keep it off. The historical browser page
at <http://127.0.0.1:6113/> remains as a fallback and uses the same API.

## Using the app

1. Pick the connected game session in the top bar (it refreshes every few seconds).
2. Search and select a base item. The results are limited to supported bases with a `Mods` component (equipment,
   jewels and flasks; gems, maps and currency are filtered out).
3. Set rarity (Normal/Magic/Rare), item level, quality and the number of copies.
4. Search modifiers by stat, like the trade site: type `fire resistance`, `life`, `attack speed`, etc. Pick a tier
   from the results, set each stat value inside its range, then Add modifier. Selected modifiers are listed with their
   prefix/suffix, tier and values; remove any before creating.
5. Create item. The item and its client update are handled by the Gateway; an empty modifier list rolls a Magic/Rare
   item automatically.

## Copying a real item

Copy an item in the game (Ctrl+C while hovering it), then press **Import clipboard** in the app. It reads the item's
rarity, base, item level, quality (including attribute-quality scaling), implicit notes, explicit lines, fractured and
crafted markers. The app fills the base and properties and selects matching explicit tiers/values so the item can be
created as a local copy. The rare name itself is regenerated; implicit and enchant lines are reported as notes because
the base supplies/implicit handling differs. Any line the matcher cannot resolve is listed as unmatched and can be
added manually with the normal stat search.

The matcher uses the stat/ModType ids and the rolled values because the extracted data has no localized mod sentences;
it is intentionally conservative rather than guessing a wrong mod.

Prefix/suffix limits, duplicate modifiers, conflicting families, domain mismatches, out-of-range values and a full
inventory are rejected with the reason in the app. Implicits come from the base and default sockets/links are generated
like `/item`. The item is saved before the client update, so it survives a disconnect.

## API

The app uses a small JSON API (the browser fallback uses the same one).

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/sessions` | connected game sessions (`ticket`, character, level, area, ready) |
| GET | `/api/bases?q=ring&limit=60` | search buildable base types |
| GET | `/api/stats?q=life&type=<base hash>` | explicit modifier groups by stat/ModType, with tiers and ranges |
| GET | `/api/mods?q=life&type=<base hash>` | flat modifier search (fallback) |
| POST | `/api/import` | parse copied item text into a draft |
| POST | `/api/create` | create an item |

`POST /api/create` takes:

```json
{
  "ticket": 123,
  "typeHash": 123456789,
  "rarity": "Rare",
  "itemLevel": 100,
  "quality": 20,
  "count": 1,
  "mods": [{ "hash": 1234, "values": [42], "fractured": false, "crafted": false }]
}
```

The response carries `ok`, `count`, the first item id, its display name, and the cells it occupies.
