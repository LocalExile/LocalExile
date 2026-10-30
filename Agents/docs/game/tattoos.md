# Tattoos

**Status: in progress (documentation complete, catalogue 100/100; 45 tattoo rows client-verified).** The
client's right-click tattoo + left-click passive flow is traced and implemented: C->S `0x77` carries the inventory
id, item id and passive graph id; the server consumes the item, replaces the passive's stats and sends the tattoo in
`0x33`'s fifth list as `node | PassiveSkillOverrides.HASH16 << 16`. Forty-five rows are `works` (the ten pilot,
22 plain stats, nine Makanga gates, three Journey tattoos and Journey Makanui), one is `partial` (Ancestral
Bloodlines: ES recovery works, life/mana recovery stats are unread), two are `inert` (Hinekora and Kitava Makanga)
and 52 are `not implemented`. The server enforces the client's per-group limits (Loyalty, Attribute Notable,
Ancestral, Keystone and Ascendancy) independently of the client tooltip. This page records the 3.29.3b catalogue,
the client mechanics, the protocol, the target/limit rules and the implementation groups. The `Status` column is
per row; `Reason` names the missing layer.

This page is based on the 3.29.3b client tables `BaseItemTypes`, `CurrencyItems`, `PassiveSkillTattoos`,
`PassiveSkillTattooTargetSets` and `PassiveSkillOverrides`, cross-checked with the poewiki
[Tattoo](https://www.poewiki.net/wiki/Tattoo) and
[List of tattoos](https://www.poewiki.net/wiki/List_of_tattoos) pages (2026-10-29) and Path of Building's
`src/Data/TattooPassives.lua`. Path of Building is a read-only third-party reference under `Tools/PathOfBuilding`
(not committed).

## Status summary

| Group | Items | Works | Partial | Inert | Not implemented |
|---|---:|---:|---:|---:|---:|
| Current (drop-enabled) | 53 | 29 | 0 | 0 | 24 |
| Drop-disabled | 40 | 16 | 1 | 2 | 21 |
| Event-only | 7 | 0 | 0 | 0 | 7 |
| **Total** | **100** | **45** | **1** | **2** | **52** |

| Status | Meaning |
|---|---|
| `works` | Implemented and verified with the real client. |
| `partial` | Part of the feature works (item source, server apply or effect), but at least one layer is missing; the reason names it. |
| `inert` | The item/apply flow works, but the effect has no gameplay consumer. |
| `not implemented` | No item source, apply/remove flow or passive-replacement state. |

`stat-only` in the reason column means the effect is a plain stat with no condition; it does **not** claim that the
stat already has a gameplay consumer. The first implementation pass must audit each replacement passive's stat keys.

## Implementation groups (simple to complex)

| Group | Contents | State |
|---|---|---|
| 1. Plain stats | The ten pilot tattoos plus the next ten (resistances, attributes, evasion, accuracy, regeneration, stun and max-res). | 32/32 non-Makanga plain stats client-verified; the Makanga rows are in group 3. |
| 2. Ailment/avoidance and skill stats | Chance/avoid ailments, leech, guard/warcry/mark/flask/minion/totem stats. | Not implemented. |
| 3. Counting, adjacency and limits | Journey per-allocated-tattoo, Ancestral per-tribe, Makanga adjacency, the five `PassiveSkillOverrides.Limit` groups. | Makanga, Journey counting and the generic limits are implemented (Journey Body/Mind/Soul and Keystone work; Ancestral is partial). |
| 4. Random specials | Journey Makanui (random keystone) and the seven Forbidden ascendancy tattoos. | Journey Makanui works; the seven Forbidden tattoos are not implemented. |
| 5. Loyalty summons | The ten Summon Spirit triggers, each limited to 1 Loyalty Tattoo. | Not implemented. |

## What a tattoo is

A tattoo is a currency item (1x1, stack size 10) that replaces the effect of one allocated passive skill. The item
is consumed on use; the passive node keeps its position, connections and allocation cost, but its name, icon and
stats become the tattoo's replacement passive. Tattoos are not jewels: they do not use a socket and they are not
limited by the passive tree's jewel sockets.

The ordinary tattoo items' client directions are: "Right click this item then left click an allocated Passive Skill.
Maximum 50 Tattoos." The 93 non-Forbidden tattoos use `Action = apply_passive_tattoo` and `CurrencyUseType = 7`.
The seven Forbidden ascendancy tattoos have no `apply_passive_tattoo` action (`CurrencyUseType = 3`); their
application is a separate ascendancy-tattoo flow that still needs a client trace.

## Mechanics

### Apply, replace and remove

| Action | Client behaviour |
|---|---|
| Apply | Right-click the tattoo, then left-click an **allocated** passive that matches its target set. The item is consumed and the passive's effect becomes the tattoo's replacement. |
| Replace | Applying another tattoo to the same passive replaces the previous tattoo; the old tattoo is lost. |
| Remove | Use an Orb of Scouring on the tattooed passive. The tattoo is removed and the original passive is restored; the passive stays allocated. |
| Unallocate | Unallocating the passive does **not** remove the tattoo. The tattooed state persists on the node and returns when the passive is allocated again. |
| Limit | A character can have at most **50 applied tattoos**. The state is per character, not per item. |
| Vendor | Three tattoo items from the same tribe can be vendored for one random tattoo of the same tribe. |

### Targets and limits

`PassiveSkillTattoos.Set` selects the target set. The row index maps to `PassiveSkillTattooTargetSets`:

| `PassiveSkillTattoos.Set` | Target set row | Meaning | Items |
|---:|---|---|---:|
| 0 | `SmallStrength` | Small Strength passive | 28 |
| 1 | `SmallDexterity` | Small Dexterity passive | 20 |
| 2 | `SmallIntelligence` | Small Intelligence passive | 22 |
| 3 | `SmallAnyAttribute` | Any small attribute passive | 10 |
| 4 | `BigStrength` | Notable with +30 to Strength | 4 |
| 5 | `BigDexterity` | Notable with +30 to Dexterity | 4 |
| 6 | `BigIntelligence` | Notable with +30 to Intelligence | 4 |
| 7 | `Keystone` | Keystone | 1 |
| 8 | `Mastery` | Mastery | 0 |
| 9 | `Ascendancy` | Ascendancy notable | 7 |

The `Set` field inside `PassiveSkillTattooTargetSets` is a different field (for example `SmallStrength` has
`Set: [1]`); the mapping above uses the **row index** of that table, which is what `PassiveSkillTattoos.Set` stores.
The mapping was checked against Path of Building's `targetType`/`targetValue` for all 92 items that have a
`TattooPassives.lua` entry; there are no mismatches. The remaining eight are Journey Makanui and the seven Forbidden
tattoos, whose target types are read directly from the client table.

Special limits and conditions:

- **Makanga**: four tattoos require **at most 1 adjacent allocated passive** (Arohongui, Ngamahu, Rongokurai,
  Valako); six require **at least 7 adjacent allocated passives** (Hinekora, Kitava, Ramako, Tasalio, Tawhoa,
  Tukohama); **Honoured Tattoo of the Makanga requires at least 8**. The condition is dynamic: the passive is
  replaced and the tattoo stays applied, but the replacement grants nothing while the allocated direct-neighbour
  count is outside `RequiresAdjacent`/`MaxAdjacent`. The client accepts the application even when the requirement is
  unmet; the server gates the stats.
- **Generic limits**: `PassiveSkillOverrides.Limit` is a group id, not a count. Tattoos use five ids; the server
  treats each as a one-per-character group and refuses a second tattoo from the same group on a different node
  (the `Limit` 7-39 rows in the table are Runegrafts, not tattoos):

  | `Limit` | Server group | Items |
  |---:|---|---:|
  | 0 | Loyalty Tattoo | 10 |
  | 4 | Attribute Notable Tattoo | 9 |
  | 5 | Ancestral Tattoo | 1 |
  | 6 | Keystone Tattoo | 1 |
  | 40 | Ascendancy Tattoo | 7 |

  Replacing the tattoo on the **same node** is allowed; the limit only counts a second node. The 50-tattoo cap is
  separate. The client draws the "Limited to: 1 ..." line from its own tables; it is not a server message, so the
  Gateway enforces the group itself.
- **Loyalty**: limited by the Limit 0 group; the ten Summon Spirit effects are not implemented yet.
- **Honoured attribute notables**: the nine Honoured tattoos that replace a +30 attribute notable share the Limit 4
  group. The small-attribute Honoured tattoos have no group limit.
- **Ancestral Tattoo of Bloodlines**: Limit 5.
- **Journey Tattoo of Makanui**: Limit 6; replaces a keystone and grants a random base keystone on application.
- **Forbidden**: Limit 40; replaces a non-multiple-choice ascendancy notable and grants a random
  non-multiple-choice ascendancy notable from the class's base ascendancies.

### Acquisition

- **Kingsmarch shipments** to the Karui ports: Ngakanu (Strength tattoos), Te Onui (Dexterity tattoos) and Moti Aro
  (Intelligence tattoos). The port is shown per row in the catalogue.
- **Lost Karui Goods** chests, with the Lost in Transit Atlas passive.
- **The Karui Fortress** in Act 6: the pack on the small island across the bridge drops one random tattoo once per
  character.
- Historical: Trial of the Ancestors rewards (Ancestor league), Allflame embers (Necropolis league), and the 3.28
  Return of the Ancestors event. The event-only Forbidden tattoos were introduced in 3.28.0j and deleted at the end
  of that event; the 3.29.3b client still contains their item rows.

### Interactions

- **Unnatural Instinct** grants the effects of tattoos on small **unallocated** passives. A tattoo on an unallocated
  small passive is therefore not inert; the Unnatural Instinct pass must read the tattooed stats.
- **Might of the Meek** and **Warrior's Tale** increase the effect of tattoo stats. The radius scaling must include
  the tattoo replacement's stats, not the original passive's stats.
- **Lethal Pride**, **Brutal Restraint**, **Militant Faith** (non-transformed passives) and Chieftain's Ngamahu,
  Flame's Advance grant their stats in addition to the tattoo stats.
- **Transformed passives** (Glorious Vanity, Elegant Hubris, Militant Faith transformed notables, attribute
  conversion jewels, etc.) prevent or disable tattoos. A timeless-conquered node must not apply its tattoo.
- **Anointed notables** reflect the tattoo's stats, not the original notable's stats.
- **Impossible Escape** and **Thread of Hope** can allocate passives without a connection; this is useful for the
  Makanga adjacency condition. Bypass-allocated passives must count as allocated for the adjacency check.

## Client protocol (0x77 and 0x33)

### Applying and removing

The client sends **C->S `0x77`** (12 bytes) when an item is used on a passive:

```
u32 inventory id   (big-endian)
u32 item id        (big-endian)
u16 PassiveSkillGraphId
```

Captured on 2026-10-29 after right-clicking Tattoo of the Ngamahu Firewalker and clicking allocated node 21301:

`0077 00000001 0000049C 5335` ? inventory 1, item 1180, node 21301.

The **item type decides the action**:

- a tattoo (`CurrencyItems.Action = apply_passive_tattoo`) replaces the passive;
- an Orb of Scouring (`Metadata/Items/Currency/CurrencyConvertToNormal`) removes the tattoo and restores the
  passive.

The server answers with the consumed stack (`0xbf`), the character stats (`0x1b3`) and the passive tree (`0x33`).
The same packet is sent on every click; the server must make repeated clicks idempotent (the first consumes the
item and later ones fail cleanly).

### The fifth list of 0x33

`0x33` carries the applied tattoos in its **fifth list** (a `vec16 u32`, count then little-endian pairs):

```
node | PassiveSkillOverrides.dat HASH16 << 16
```

For the Firewalker on node 21301 the entry is `0x5C2A5335`: node `0x5335` and `Ngamahu1` HASH16 `0x5C2A`
(23594). The client stores the pair at tree `+0x1f8` and looks the value up in `PassiveSkillOverrides.dat`.

This value is **not** the BaseItemTypes row (4841) and **not** the `PassiveSkillTattoos.Override` row index (0):
both left the node displaying its original `+10 to Strength`. Only the override row's HASH16 changed the name,
icon and tooltip. The other lists are unchanged: second empty, third cluster jewel nodes, fourth mastery effects.

### Verified client sequence

1. Right-click a tattoo; the item is held on the cursor.
2. Click an allocated passive of the right target type; the client sends `0x77`.
3. The server consumes one tattoo, updates the stats and sends `0x33` with the tattoo pair.
4. The node shows the replacement (`Fire Resistance`, `+6% to Fire Resistance`); relog keeps it.
5. Right-click an Orb of Scouring, click the tattooed node; the same `0x77` removes the tattoo, restores the
   passive and consumes one orb.

## Data model

- **100 distinct tattoo names** in `BaseItemTypes` (the 100 rows whose name contains "Tattoo"). Of these, 93 use the
  `apply_passive_tattoo` action; the seven Forbidden ascendancy tattoos use the separate flow described above. The
  client also has 36 Runegraft rows using `apply_passive_tattoo` (including four `[DNT - UNUSED]` rows), so the 129
  `CurrencyItems` rows with that action are 93 tattoos + 36 Runegrafts. Do not treat every `apply_passive_tattoo` item
  as a tattoo.
- **53 drop-enabled**, **40 drop-disabled** and **7 event-only** items in 3.29.3b. The drop-disabled group contains
  the ten Makanga, ten Loyalty, eighteen Honoured, Ancestral Bloodlines and Journey Makanui; the event-only group is
  the seven Forbidden tattoos.
- `PassiveSkillTattoos` has 136 rows: the 100 tattoos (including the seven Forbidden) plus 36 Runegrafts. Each tattoo
  row stores the item row (`Tattoo`), the target-set index (`Set`), the tribe (`Tribe`) and an internal
  `Override`/`OverrideType`.
- The currency item carries **no effect mods**: the replacement passive's stats are the effect. Path of Building's
  `TattooPassives.lua` gives the effect text and stat keys for 92 items; the eight special items (Journey Makanui and
  the seven Forbidden tattoos) need random generation or a special replacement.
- `PassiveSkillOverrides.json` has 343 rows. `PassiveSkillTattoos.Override` indexes this table; the client needs the
  row's `HASH16`, not the row index. Firewalker is `Override = 0` ? `Ngamahu1`, HASH16 23594; the row also carries
  the replacement icon, stats, `RequiresAdjacent`/`MaxAdjacent` and `TattooBlockingPassive`.
- The client's `Override` index is therefore decoded: use `PassiveSkillOverrides[Override].HASH16` in `0x33`'s
  fifth list. The target set and tribe are already available; the replacement stats for the 92 ordinary tattoos are
  also in PoB for the server-side stat pass.

## Implementation plan

1. **Client flow (done for the pilot).** C->S `0x77` is the item-on-passive packet; `0x33`'s fifth list carries
   `node | PassiveSkillOverrides.HASH16 << 16`. The client did not display the replacement until the HASH16 was sent.
2. **Catalogue and dev source (done).** `TattooCatalogue` reads `BaseItemTypes`, `PassiveSkillTattoos`,
   `PassiveSkillTattooTargetSets` and `PassiveSkillOverrides`; `/tattoo <name>` creates all 100 items. The seven
   Forbidden items still need their separate currency-use path.
3. **Save shape (done).** The character save stores `node -> tattoo name`; the map is independent of allocation, so
   refunding and reallocating the passive preserves the tattoo. Old saves without the field load as empty.
4. **Apply/remove (done for the pilot).** `0x77` validates the target set, the 50-tattoo cap and the per-group
   limits, consumes the item, installs the replacement stats and saves. The same packet with an Orb of Scouring
   removes the tattoo and restores the passive without refunding the point. Makanga adjacency is a dynamic stat
   gate: the tattoo can be applied, but its stats are inactive while `RequiresAdjacent`/`MaxAdjacent` is not met.
   The five `Limit` groups are enforced server-side; same-node replacement is allowed.
5. **Stat replacement (done for the 10 pilot stats).** The replacement stats are applied in the same pass that builds
   allocated passive stats; the node's allocation, connections and point cost stay unchanged. The client displays the
   replacement from the `0x33` fifth list; the panel stats come from the server.
6. **Interactions (partial).** Timeless-conquered suppression and Unnatural Instinct's unallocated-small-passive rule
   are wired into the stat pass. Might of the Meek/Warrior's Tale scaling and anointment replacement still need their
   own checks. A tattooed node must not double-apply its original stats.
7. **Random specials (Makanui done).** Journey Makanui picks one of the 48 base passive-tree keystones in
   `PassiveSkillOverrides` Type 15, excludes the target node's keystone and saves the choice in `TattooKeystones`,
   so relog does not reroll; `0x33` sends the chosen keystone's HASH16. The seven Forbidden tattoos still need to
   choose a random non-multiple-choice ascendancy notable from the class's base ascendancies and save it.
8. **Vendor recipe (not started).** Three same-tribe tattoo items -> one random same-tribe tattoo, using the existing
   vendor recipe path. Event-only and drop-disabled items need an explicit availability rule.
9. **Tests (pilot coverage).** Unit tests cover the 100-item catalogue, the target map, the pilot stat mapping, the
   Firewalker pool change, the `0x33` fifth-list layout, the 48-keystone Makanui pool/choice and the five limit
   groups. Live client checks cover apply, display, relog and Orb of Scouring removal for the Firewalker, plus the
   Makanui random replacement and live Keystone/Ancestral limit refusals. Still needed: replace, unallocate/reallocate,
   the 50 cap and one Forbidden tattoo.

## Full catalogue

The effect text is the poewiki item text; the target is the client target set. `Status` is per row: `partial` means
the server dev apply and stat replacement are implemented (the reason says whether the client item-use flow is
missing), and `not implemented` means no server effect/flow yet. `Source` is the Kingsmarch port for drop-enabled
rows; the drop-disabled and event-only groups are not normally obtainable in 3.29.

### Journey (3)

These three drop-enabled Journey tattoos count themselves for their own per-allocated-tattoo effect.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Journey Tattoo of the Body** | +25 to maximum Life per Allocated Journey Tattoo of the Body | Notable (+30 Str) | Ngakanu | works | live +25 maximum Life per allocated copy; base life 5535 -> 5550 after replacing Prowess |
| **Journey Tattoo of the Mind** | +30 to maximum Mana per Allocated Journey Tattoo of the Mind | Notable (+30 Dex) | Te Onui | works | live +30 maximum Mana per allocated copy; base mana 2336 -> 2365 |
| **Journey Tattoo of the Soul** | +40 to maximum Energy Shield per Allocated Journey Tattoo of the Soul | Notable (+30 Int) | Moti Aro | works | live +40 maximum Energy Shield per allocated copy; ES 34 -> 74 |

### Arohongui (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Arohongui Moonwarden** | +6% to Cold Resistance | Small Dexterity | Te Onui | works | client apply; cold 211% -> 217% |
| **Tattoo of the Arohongui Scout** | 10% chance to Avoid being Chilled<br>10% chance to Avoid being Frozen | Small Dexterity | Te Onui | not implemented | needs chill/freeze avoidance rolls |
| **Tattoo of the Arohongui Shaman** | 5% increased Effect of Herald Buffs on you | Small Intelligence | Moti Aro | not implemented | needs Herald buff effect |
| **Tattoo of the Arohongui Warmonger** | 5% chance to Freeze | Small Intelligence | Moti Aro | not implemented | needs freeze chance |
| **Tattoo of the Arohongui Warrior** | 5% increased Cold Damage | Small Dexterity | Te Onui | works | client apply; +5% cold damage |

### Hinekora (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Hinekora Deathwarden** | 6% reduced Effect of Curses on you | Small Intelligence | Moti Aro | works | client apply; -6% curse effect on you |
| **Tattoo of the Hinekora Shaman** | 8% increased Mana Regeneration Rate | Small Intelligence | Moti Aro | works | client apply; +8% mana regeneration |
| **Tattoo of the Hinekora Storyteller** | +3% to Chaos Resistance | Small Intelligence | Moti Aro | works | client apply; chaos 0% -> 3% |
| **Tattoo of the Hinekora Warmonger** | Minions have 5% increased maximum Life | Small Intelligence | Moti Aro | not implemented | needs minion life |
| **Tattoo of the Hinekora Warrior** | 3% increased maximum Energy Shield | Small Intelligence | Moti Aro | works | client apply; max ES +3% |

### Kitava (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Kitava Blood Drinker** | 0.5% of Attack Damage Leeched as Life | Small Strength | Ngakanu | not implemented | needs attack leech |
| **Tattoo of the Kitava Heart Eater** | Killing Blows have 4% chance to Consume corpses to recover 10% of Maximum Life | Small Intelligence | Moti Aro | not implemented | needs on-kill corpse consumption |
| **Tattoo of the Kitava Rebel** | 10% chance to Avoid Bleeding | Small Strength | Ngakanu | not implemented | needs bleed avoidance |
| **Tattoo of the Kitava Shaman** | Attacks have 5% chance to cause Bleeding | Small Strength | Ngakanu | not implemented | needs bleed chance |
| **Tattoo of the Kitava Warrior** | 5% increased Global Physical Damage | Small Strength | Ngakanu | works | client apply; +5% global physical damage |

### Ngamahu (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Ngamahu Firewalker** | +6% to Fire Resistance | Small Strength | Ngakanu | works | client 0x77 + 0x33 fifth list; apply/remove/relog verified |
| **Tattoo of the Ngamahu Shaman** | 10% reduced Ignite Duration on you | Small Strength | Ngakanu | works | client apply; -10% ignite duration on you |
| **Tattoo of the Ngamahu Warmonger** | 5% chance to Ignite | Small Strength | Ngakanu | not implemented | needs ignite chance |
| **Tattoo of the Ngamahu Warrior** | 5% increased Fire Damage | Small Strength | Ngakanu | works | client apply; +5% fire damage |
| **Tattoo of the Ngamahu Woodcarver** | 5% increased Totem Life | Small Strength | Ngakanu | not implemented | needs totem life |

### Ramako (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Ramako Archer** | 5% increased Global Accuracy Rating | Small Dexterity | Te Onui | works | client apply; +5% accuracy |
| **Tattoo of the Ramako Fleetfoot** | 2% increased Movement Speed | Small Dexterity | Te Onui | works | client apply; movement speed +2% |
| **Tattoo of the Ramako Scout** | 6% increased Evasion Rating | Small Dexterity | Te Onui | works | client apply; +6% evasion (total confounded by the replaced Dexterity node) |
| **Tattoo of the Ramako Shaman** | +2% chance to Suppress Spell Damage | Small Dexterity | Te Onui | works | client apply; +2% suppression (100% cap, panel unchanged) |
| **Tattoo of the Ramako Sniper** | 5% increased Projectile Speed | Small Dexterity | Te Onui | works | client apply; +5% projectile speed |

### Rongokurai (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Rongokurai Brute** | 10% increased Stun Threshold | Small Strength | Ngakanu | works | client apply; +10% stun threshold |
| **Tattoo of the Rongokurai Goliath** | 10% increased Stun Duration on Enemies | Small Strength | Ngakanu | works | client apply; +10% stun duration |
| **Tattoo of the Rongokurai Guard** | Guard Skills have 6% increased Duration | Small Strength | Ngakanu | not implemented | needs guard-skill duration |
| **Tattoo of the Rongokurai Turtle** | You take 5% reduced Extra Damage from Critical Strikes | Small Intelligence | Moti Aro | works | client apply; -5% extra critical damage taken |
| **Tattoo of the Rongokurai Warrior** | 6% increased Armour | Small Strength | Ngakanu | works | client apply; armour 319% -> 325% |

### Tasalio (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tasalio Bladedancer** | Attacks have 5% chance to Maim on Hit | Small Strength | Ngakanu | not implemented | needs maim on hit |
| **Tattoo of the Tasalio Scout** | 4% increased Effect of your Marks | Small Dexterity | Te Onui | not implemented | needs mark effect |
| **Tattoo of the Tasalio Shaman** | 5% chance to Hinder Enemies on Hit with Spells | Small Intelligence | Moti Aro | not implemented | needs hinder on spell hit |
| **Tattoo of the Tasalio Tideshifter** | 10% chance to Avoid being Stunned | Small Dexterity | Te Onui | not implemented | needs stun avoidance |
| **Tattoo of the Tasalio Warrior** | 5% chance to Blind Enemies on Hit with Attacks | Small Dexterity | Te Onui | not implemented | needs blind on attack hit |

### Tawhoa (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tawhoa Herbalist** | 4% increased Flask Effect Duration | Small Dexterity | Te Onui | not implemented | needs flask effect duration |
| **Tattoo of the Tawhoa Naturalist** | 8% increased Life Recovery from Flasks | Small Dexterity | Te Onui | not implemented | needs flask life recovery |
| **Tattoo of the Tawhoa Scout** | 10% chance to Avoid being Poisoned | Small Dexterity | Te Onui | not implemented | needs poison avoidance |
| **Tattoo of the Tawhoa Shaman** | 5% chance to Poison on Hit | Small Intelligence | Moti Aro | not implemented | needs poison chance |
| **Tattoo of the Tawhoa Warrior** | 5% increased Chaos Damage | Small Intelligence | Moti Aro | works | client apply; +5% chaos damage |

### Tukohama (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tukohama Brawler** | 10% chance to Knock Enemies Back on hit | Small Strength | Ngakanu | not implemented | needs knockback on hit |
| **Tattoo of the Tukohama Shaman** | Regenerate 0.3% of Life per second | Small Strength | Ngakanu | works | client apply; 0.3%/s life regeneration |
| **Tattoo of the Tukohama Warcaller** | 8% increased Warcry Cooldown Recovery Rate | Small Strength | Ngakanu | not implemented | needs warcry cooldown recovery |
| **Tattoo of the Tukohama Warmonger** | 5% increased Melee Damage | Small Strength | Ngakanu | works | client apply; +5% melee damage |
| **Tattoo of the Tukohama Warrior** | Melee Hits which Stun have 5% chance to Fortify | Small Strength | Ngakanu | not implemented | needs fortify on stun |

### Valako (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Valako Scout** | 10% reduced Effect of Shock on you | Small Intelligence | Moti Aro | works | client apply; -10% shock effect on you |
| **Tattoo of the Valako Shaman** | 5% chance to Shock | Small Intelligence | Moti Aro | not implemented | needs shock chance |
| **Tattoo of the Valako Shieldbearer** | +1% Chance to Block Attack Damage | Small Strength | Ngakanu | works | client apply; uncapped block 168% -> 169% |
| **Tattoo of the Valako Stormrider** | +6% to Lightning Resistance | Small Intelligence | Moti Aro | works | client apply; lightning 211% -> 217% |
| **Tattoo of the Valako Warrior** | 5% increased Lightning Damage | Small Intelligence | Moti Aro | works | client apply; +5% lightning damage |

### Ancestral (1)

Limited to 1 Ancestral Tattoo.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Ancestral Tattoo of Bloodlines** | 2% increased Recovery Rate of Life, Mana and Energy Shield per Tribe for which you have an allocated Tattoo | Small Attribute | drop-disabled | partial | live +18% recovery for 9 tribes; ES recovery consumed, life/mana recovery stats unread |

### Honoured (18)

The nine +30-attribute notable tattoos share the limit of 1 Attribute Notable Tattoo; the nine small-attribute
tattoos do not. All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Honoured Tattoo of the Barbarian** | +1% to maximum Cold Resistance<br>+1 to Maximum Frenzy Charges | Notable (+30 Dex) | drop-disabled | not implemented | needs max cold res + frenzy charge cap |
| **Honoured Tattoo of the Berserker** | You gain Onslaught for 4 seconds on Hit | Notable (+30 Dex) | drop-disabled | not implemented | needs Onslaught-on-hit buff |
| **Honoured Tattoo of the Dove** | +4% to Chaos Resistance | Small Attribute | drop-disabled | works | client apply; chaos 0% -> 4% |
| **Honoured Tattoo of the Flock** | 1% increased effect of Non-Curse Auras from your Skills | Small Attribute | drop-disabled | not implemented | needs non-curse aura effect |
| **Honoured Tattoo of the Flood** | Gain Arcane Surge on Hit with Spells | Notable (+30 Int) | drop-disabled | not implemented | needs Arcane Surge on spell hit |
| **Honoured Tattoo of the Hatungo** | 1% increased Reservation Efficiency of Skills | Small Attribute | drop-disabled | not implemented | needs reservation efficiency |
| **Honoured Tattoo of the Hunter** | +1 to Level of all Dexterity Skill Gems | Notable (+30 Dex) | drop-disabled | not implemented | needs Dexterity gem levels |
| **Honoured Tattoo of the Makanga** | +2% to all maximum Resistances | Small Attribute | drop-disabled | works | live 8-neighbour gate; max res 86 -> 84 after refunding one neighbour |
| **Honoured Tattoo of the Mountain** | +1% to maximum Fire Resistance<br>+1 to Maximum Endurance Charges | Notable (+30 Str) | drop-disabled | not implemented | needs max fire res + endurance charge cap |
| **Honoured Tattoo of the Oak** | 2% increased maximum Life | Small Attribute | drop-disabled | works | client apply; max life +2% |
| **Honoured Tattoo of the Pa** | Melee Hits Fortify | Notable (+30 Str) | drop-disabled | not implemented | needs melee-hit fortify |
| **Honoured Tattoo of the Pillager** | 3% increased Rarity of Items found | Small Attribute | drop-disabled | works | client apply; +3% rarity of items found |
| **Honoured Tattoo of the Sky** | +3% to all Elemental Resistances | Small Attribute | drop-disabled | works | client apply; fire/cold/lightning +3% |
| **Honoured Tattoo of the Storm** | +1% to maximum Lightning Resistance<br>+1 to Maximum Power Charges | Notable (+30 Int) | drop-disabled | not implemented | needs max lightning res + power charge cap |
| **Honoured Tattoo of the Tuatara** | +4 to all Attributes | Small Attribute | drop-disabled | works | client apply; +4 all attributes |
| **Honoured Tattoo of the Turtle** | 3% increased Global Defences | Small Attribute | drop-disabled | works | client apply; +3% global defences |
| **Honoured Tattoo of the Warlord** | +1 to Level of all Strength Skill Gems | Notable (+30 Str) | drop-disabled | not implemented | needs Strength gem levels |
| **Honoured Tattoo of the Wise** | +1 to Level of all Intelligence Skill Gems | Notable (+30 Int) | drop-disabled | not implemented | needs Intelligence gem levels |

### Journey Makanui (1)

Replaces a keystone, is limited to 1 Keystone Tattoo, and grants a random keystone on application. The pool is the
48 ordinary base passive-tree keystones from `PassiveSkillOverrides` Type 15; the target node's own keystone is
excluded so the node always changes.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Journey Tattoo of Makanui** | Grants a random Keystone | Keystone | drop-disabled | works | client `0x77`; Roiling Tempest 40351 -> Mind Over Matter 34098; `/stat` mana_shield 1, lightning_purist 0; relog; a second keystone was refused; removal restored the original keystone |

### Loyalty (10)

Each is limited to 1 Loyalty Tattoo and triggers a Summon Spirit skill; most triggers require a Unique Enemy in your
Presence. All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Loyalty Tattoo of Ahuana** | Trigger level 1 Summon Spirit of Ahuana Skill when you Suppress Spell Damage from a Unique Enemy | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + suppress trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Akoya** | Trigger level 1 Summon Spirit of Akoya Skill when you reach Maximum Rage while a Unique Enemy is in your Presence | Small Strength | drop-disabled | not implemented | needs Summon Spirit + max-rage trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Ikiaho** | Trigger level 1 Summon Spirit of Ikiaho Skill when you use a Travel Skill while a Unique Enemy is in your Presence | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + travel-skill trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kahuturoa** | Trigger level 1 Summon Spirit of Kahuturoa Skill when you take a Critical Strike from a Unique Enemy | Small Strength | drop-disabled | not implemented | needs Summon Spirit + crit-taken trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kaom** | Trigger level 1 Summon Spirit of Kaom Skill when a Totem dies while a Unique Enemy is in your Presence | Small Strength | drop-disabled | not implemented | needs Summon Spirit + totem-death trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kiloava** | Trigger level 1 Summon Spirit of Kiloava Skill when you Block Damage from a Unique Enemy | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + block trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Maata** | Trigger level 1 Summon Spirit of Maata Skill when you reach Low Life while a Unique Enemy is in your Presence | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + low-life trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Rakiata** | Trigger level 1 Summon Spirit of Rakiata Skill on Critical Strike against Marked Unique Enemy | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + marked-unique crit trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Tawhanuku** | Trigger level 1 Summon Spirit of Tawhanuku Skill when Energy Shield Recharge starts while a Unique Enemy is in your Presence | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + ES-recharge trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Utula** | Trigger level 1 Summon Spirit of Utula Skill on taking a Savage Hit from a Unique Enemy | Small Strength | drop-disabled | not implemented | needs Summon Spirit + savage-hit trigger; limit 1 Loyalty Tattoo |

### Makanga (10)

Four require at most 1 adjacent allocated passive (Arohongui, Ngamahu, Rongokurai, Valako); six require at least
7 adjacent allocated passives (Hinekora, Kitava, Ramako, Tasalio, Tawhoa, Tukohama). All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Arohongui Makanga** | +1% to maximum Cold Resistance | Small Dexterity | drop-disabled | works | live MaxAdjacent 1; max cold 85 -> 84 with two neighbours |
| **Tattoo of the Hinekora Makanga** | 8% increased Effect of your Curses | Small Intelligence | drop-disabled | inert | stat applied (8% curse effect); no enemy-curse gameplay consumer |
| **Tattoo of the Kitava Makanga** | 80% increased Maximum total Life Recovery per second from Leech | Small Strength | drop-disabled | inert | stat applied (80% max leech rate); no leech-cap consumer |
| **Tattoo of the Ngamahu Makanga** | +1% to maximum Fire Resistance | Small Strength | drop-disabled | works | live MaxAdjacent 1; max fire 85 -> 84 with two neighbours |
| **Tattoo of the Ramako Makanga** | Skills fire an additional Projectile | Small Dexterity | drop-disabled | works | live 7-neighbour gate; +1 projectile (projectile modifier consumer) |
| **Tattoo of the Rongokurai Makanga** | 8% chance to Defend with 200% of Armour | Small Strength | drop-disabled | works | live MaxAdjacent 1; 8% armour-double chance (RecentEnemyHits consumer) |
| **Tattoo of the Tasalio Makanga** | 8% increased Cooldown Recovery Rate | Small Intelligence | drop-disabled | works | live 7-neighbour gate; 8% cooldown speed (cooldown consumer) |
| **Tattoo of the Tawhoa Makanga** | Flasks applied to you have 8% increased Effect | Small Dexterity | drop-disabled | works | live 7-neighbour gate; 8% flask effect (flask consumer) |
| **Tattoo of the Tukohama Makanga** | 16% increased Life Regeneration rate | Small Strength | drop-disabled | works | live 7-neighbour gate; 16% life-regeneration rate (panel/recovery consumer) |
| **Tattoo of the Valako Makanga** | +1% to maximum Lightning Resistance | Small Intelligence | drop-disabled | works | live MaxAdjacent 1; max lightning 84 -> 83 with two neighbours |

### Forbidden (7)

Each replaces a non-multiple-choice ascendancy notable, is limited to 1 Ascendancy Tattoo, and grants a random
non-multiple-choice notable from the class's base ascendancies. Event-only; the items are deleted at the end of the
Return of the Ancestors event.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Forbidden Tattoo of the Duelist** | Grants a random Duelist Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Duelist ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Marauder** | Grants a random Marauder Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Marauder ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Ranger** | Grants a random Ranger Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Ranger ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Scion** | Grants a random Scion Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Scion ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Shadow** | Grants a random Shadow Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Shadow ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Templar** | Grants a random Templar Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Templar ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Witch** | Grants a random Witch Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Witch ascendancy notable; limit 1 Ascendancy Tattoo |

## Verification log

Client checks 2026-10-29 (real 3.29.3b client):

- **Tattoo apply:** right-click Tattoo of the Ngamahu Firewalker + click allocated small Strength 21301 sent
  `0077 00000001 0000049C 5335` (inventory 1, item 1180, node 21301). The client retried the packet until the server
  handled it.
- **Item and stats:** the server consumed one tattoo; fire resistance 258% -> 264%.
- **Display:** the node showed `Fire Resistance` / `+6% to Fire Resistance` only after `0x33`'s fifth list carried
  `21301 | 23594 << 16` (`0x5C2A5335`, `Ngamahu1` HASH16). Sending the BaseItemTypes row 4841 or the override row
  index 0 left the original `+10 to Strength`.
- **Relog:** the node stayed Fire Resistance and fire resistance stayed 264%.
- **Tattoo removal:** right-click Orb of Scouring + click 21301 sent the same `0x77` with the orb item. The server
  removed the tattoo, restored `+10 to Strength`, changed fire resistance 264% -> 258%, and consumed one orb
  (20 -> 19).
- **Repeated clicks:** before `0x77` was handled, the client sent the same packet on every click; the first valid
  request must consume the item and later ones must fail cleanly.

Batch client checks 2026-10-29 (same session, after the Firewalker log):

- **Moonwarden** on 27659 (small Dexterity): cold resistance 211% -> 217%.
- **Stormrider** on 37671 (small Intelligence): lightning resistance 211% -> 217%.
- **Storyteller** on 27415 (small Intelligence): chaos resistance 0% -> 3%.
- **Rongokurai Warrior** on 21301 (small Strength): armour 319% -> 325% (2685 -> 2724).
- **Fleetfoot** on 7112 (small Dexterity): movement speed +2%.
- **Valako Shieldbearer** on 50862 (small Strength): uncapped block 168% -> 169%.
- **Ramako Shaman:** +2% Spell Suppression; the character is at the 100% cap, so `/suppression` stays 100% and the
  client apply/stat was checked instead of the panel number.
- **Hinekora Warrior** on 33479 (small Intelligence): maximum Energy Shield +3%.
- **Honoured Oak** on 16775 (small Strength/attribute): maximum Life +2%.
- **Refusals:** Firewalker on an Intelligence node; Storyteller first clicked Undertaker (25439, unallocated) and
  was refused, then the allocated Intelligence node worked.
- The pilot batch was removed with `/tattoo_remove`; the tree was back to 523 passives, no tattoos, and resistances
  258/211/211/0 at that point.

Next plain-stat batch 2026-10-30 (client `0x77`, real client):

- **Dove** on 10490: chaos resistance 0% -> 4%.
- **Sky** on 18033: fire 258% -> 261%, cold 211% -> 214%, lightning 211% -> 214%.
- **Makanga** on 10829: max fire/cold 84% -> 86%, max lightning 83% -> 85%, max chaos 75% -> 77%. A first click
  on `Evasion and Spell Suppression` (20812) was refused; an allocated small Dexterity node worked.
- **Tuatara** on 33479: +4 all attributes (server stat).
- **Ramako Archer** on 16167: +5% accuracy (server stat).
- **Ramako Scout** on 51786: +6% evasion (the total is confounded by the replaced Dexterity node).
- **Hinekora Shaman** on 46092: +8% mana regeneration (server stat).
- **Tukohama Shaman** on 46910: 0.3%/s life regeneration (server stat).
- **Rongokurai Brute** on 16775: +10% stun threshold (server stat).
- **Rongokurai Goliath** on 28330: +10% stun duration (server stat).
- The tree currently carries these ten plus the earlier Ramako Shaman (11 tattoos at that point).

Adjacency bug/fix 2026-10-30:

- **Honoured Tattoo of the Makanga** was applied to node 10829 without checking its `RequiresAdjacent = 8` gate.
  The node has only six neighbours and two were allocated at the time, so it can never meet the requirement; before
  the fix it incorrectly granted +2% to all maximum resistances.
- The stat pass now counts **allocated direct neighbours** from the character's graph and applies the replacement
  stats only while `RequiresAdjacent`/`MaxAdjacent` is met. The tattoo stays applied when inactive, but the
  replaced passive grants nothing until the condition is met.
- Unit tests use node 3644 (eight neighbours): seven allocated neighbours leave the tattoo inactive, eight add the
  +2% maximum resistances; Ngamahu Makanga is active with one allocated neighbour and inactive with two.
- The invalid Honoured Makanga on 10829 was removed; maximum resistances returned to 84/84/83/75.

Next plain-stat batch 2026-10-30 (client `0x77`, real client):

- All 13 remaining plain tattoos were applied through the client, replacing the previous ten on their nodes. The
  saved map now has 13 tattoos: 10490 Valako Warrior, 33479 Rongokurai Turtle, 46092 Tawhoa Warrior,
  18033 Hinekora Deathwarden, 51923 Valako Scout, 28574 Ramako Sniper, 51786 Arohongui Warrior, 16775 Kitava
  Warrior, 46910 Ngamahu Shaman, 46578 Ngamahu Warrior, 28330 Tukohama Warmonger, 16167 Honoured Turtle and
  10829 Honoured Pillager.
- Resistances are back to 258/211/211/0 (this batch has no resistance tattoos); armour 322% and evasion 398%
  reflect Honoured Turtle's global defences and the replaced attribute nodes. Block and suppression are unchanged.
- All 13 were accepted; no refusals.

Makanga live checks 2026-10-30 (client and server, real tree):

- **Honoured Makanga** on 4367 (eight allocated neighbours): active, maximum resistances 86/86/85/77.
  Refunding leaf 60989 left seven neighbours and dropped them to 84/84/83/75; reallocating 60989 restored
  86/86/85/77. The tattoo stayed applied; it was then removed.
- **Ngamahu Makanga** on 30733 (small Strength, one allocated neighbour): max fire 85. Allocating a second
  neighbour (49178) dropped it to 84; refunding 49178 restored 85. Refunding the tattooed node kept the map entry;
  the tattoo was then removed.
- **Arohongui Makanga** on 11497 (small Dexterity, one neighbour): max cold 85 -> 84 with a second neighbour ->
  85 after refunding it.
- **Valako Makanga** on 36542 (small Intelligence, one neighbour): max lightning 84 -> 83 with a second
  neighbour -> 84 after refunding it.
- **Tukohama Makanga**'s seven-neighbour gate is covered by unit tests; its life-regeneration stat has no
  command readout, so the live panel check remains.
- The test nodes were refunded/removed; the character keeps its own 12 tattoos and 530 passives from the session.

Conditional Makanga stats 2026-10-30 (client/server, `/stat` after the live gate):

- **Hinekora** on 4367 (eight neighbours): `curse_effect_+% = 8`. No enemy-curse consumer; `inert`.
- **Kitava** on 3644 (seven neighbours): `maximum_life_leech_rate_+% = 80`. No leech-cap consumer; `inert`.
- **Ramako** on 20807 (seven neighbours): `number_of_additional_projectiles = 1`; the projectile modifier already
  consumes the stat.
- **Rongokurai** on 30733 (one neighbour): `chance_to_double_armour_effect_on_hit_% = 8`; on 3644 with seven
  neighbours it is 0 (MaxAdjacent 1), so the gate works.
- **Tasalio** on 4367 (eight neighbours): `base_cooldown_speed_+% = 8`; the cooldown path consumes it.
- **Tawhoa** on 20807 (seven neighbours): `flask_effect_+% = 8`; the flask path consumes it.
- **Tukohama** on 3644: `life_regeneration_rate_+% = 16`; refunding one neighbour (seven -> six) made it 0. The
  panel/recovery formula now applies `life_regeneration_rate_+%`.
- The test tattoos were removed and the extra nodes refunded; the tree is back to 530 passives and the user's 12
  tattoos.

Counting tattoos 2026-10-30 (client/server, `/stat`):

- **Journey Body** on 52714 (Prowess, +30 Strength): base maximum life 5535 -> 5550. The net +15 is because the
  replaced Prowess stats were removed; the counting pass adds +25 per allocated Body.
- **Journey Mind** on 56029 (Agility, +30 Dexterity): base maximum mana 2336 -> 2365 (the net +29 is +30 minus the
  replaced Agility stat).
- **Journey Soul** on 50197 (Ancestral Knowledge, +30 Intelligence): base maximum energy shield 34 -> 74 (+40).
- **Ancestral Bloodlines** on 3644: life/mana/ES recovery 18% for 9 distinct tribes; the Ancestral and Journey
  tattoos use tribe 11 and are excluded. ES recovery is consumed; the life/mana recovery stats are currently
  unread, so the row is `partial`.
- The test tattoos were removed and the three bonus notables refunded; the tree is back to 530 passives, 12 user
  tattoos and baseline pools (life 5490, mana 2306, ES 34, recovery 0).

Journey Makanui and limit checks 2026-10-30 (client/server):

- **Makanui random replacement:** applied through the client to 40351 Roiling Tempest; the server drew Mind Over
  Matter (34098). `/stat keystone_lightning_purist` changed 1 -> 0 and `keystone_mana_shield` 0 -> 1. The save
  stored `Tattoos[40351] = Journey Tattoo of Makanui` and `TattooKeystones[40351] = 34098`; a relog loaded both.
  The same tattoo was then removed and the original keystone returned (`lightning_purist` 1, `mana_shield` 0).
- **Keystone limit:** a second Makanui on 50679 was refused with "only one Keystone Tattoo can be applied" while
  the first stayed on 40351. The only Keystone-group item is Makanui itself, so a same-node replacement has no
  second item; the same-node rule is covered by the unit test with Honoured Warlord/Mountain.
- **Ancestral limit:** Ancestral Bloodlines on 10221 was accepted; the same tattoo on 3644 was refused with "only
  one Ancestral Tattoo can be applied". It was then removed.
- Unit tests cover all five `LimitName` groups (0/4/5/6/40) and the same-node replacement rule.
- The test tattoos were removed; the tree is back to 530 passives, the user's 12 tattoos and baseline keystones
  (Roiling Tempest on 40351, Versatile Combatant on 50679).

Not yet verified through the client: replace, reallocating a tattooed node, the 50 cap and the seven Forbidden
tattoos.

## See also

- [Unique jewels](unique-jewels.md), [radius jewels](radius-jewels.md) and [cluster jewels](cluster-jewels.md) for
  the other passive-tree item families.
- [Warrior's Tale](radius-jewels.md) is the radius jewel that scales tattoo effects; it is currently inactive for the
  same reason (no tattoo model).
- [Protocol 15: progression](../protocol/15-progression.md) for the passive tree packets and the save path.
