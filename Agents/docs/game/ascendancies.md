# Ordinary and Phrecia main ascendancies

An ascendancy is a character's specialisation. LocalExile can select **one** main specialisation from the ordinary
and Legacy of Phrecia alternatives for the character's base class. An Aristocrat and a Slayer can belong to the same
ordinary Standard league and use its same account stash. This does not allow taking two ascendancies at once.

## Trying the implementation

Start the single client through `scripts/dev/live.ps1`, as in [Getting Started](../../GettingStarted.md).
In the in-game chat:

```text
/ascendancy
/ascendancy phrecia Aristocrat
/ascendancy_points all
```

The first command lists the choices for this character. The second example is for a Duelist; selecting a family or
class reloads the area so the native client builds the right tree. The third is a **development shortcut**, granting
the four Labyrinth tiers' two points each, once per tier. Labyrinth completion and the Altar selection flow are not
implemented. The ordinary character selection screen still displays the base class.

Open the passive tree with P. Ascendancy points are separate from ordinary skill points. Allocate and refund through
the native tree. `/add_passive <id> [...]` is a development shortcut for a connected set of nodes. `/reset_passives`
resets ordinary nodes; `/reset_ascendancy` refunds the ascendancy's nodes with the usual refund-point payment.
Refund allocated ascendancy nodes before choosing another specialisation. Nodes which grant ordinary points cannot
be refunded while ordinary allocations still spend those points.

## What is implemented

- Exact 3.29.3b catalog: 21 ordinary choices and 19 Phrecia choices. The three ordinary Scion choices remain distinct;
  Scavenger is one Phrecia choice, not three duplicate aliases.
- Durable family, stable class Id and data revision, allocated ascendancy nodes and idempotent point awards in
  `MainAscendancy`. Older unascended saves keep their items, ordinary nodes and unknown JSON fields.
- Separate ordinary/ascendancy budgets, graph ownership and connection checks for ascendancy nodes, exclusive
  choices, and separate refund costs. A choice shell costs one ascendancy point and its option costs zero; removing
  the shell costs zero regrets, while changing its selected effect costs five. Ordinary nodes cost one regret.
- Saves commit before runtime publication. Identity, nodes and refund payment are one transaction. A stale area's
  connection cannot overwrite a newer area's passive progress. Unknown saved revisions/nodes refuse loading and keep
  the save intact.
- Native Player/0x33 class identity and earned ascendancy points. Received world stat `21146` selects the Phrecia
  tree/art per character; the actual league and stash identity stay unchanged.
- The generated Phrecia graph retains all **2,576 ordinary/non-main nodes, edges and positions from the normal
  graph**, plus the 381 original Phrecia main-ascendancy nodes. Server and client use the same generated graph hash.
- Active-gem level/quality modifiers use effective properties without altering the saved gem's experience/quality.
  Percentage attributes, item/gem requirements and lowest-attribute damage are calculated on the server.
- Gambler damage/critical luck, triple damage, resistance inversion and the per-hit 90% elemental-resistance override
  run in the hit pipeline. Damage-over-time does not receive hit-only rolls. Monster base resistances come from the
  target client's tables. Attack/spell block support both lucky and unlucky rolls.
- Additional pool rules cover zero Intelligence, mana from Dexterity/Strength, loss of Intelligence's mana bonus,
  life per level and Power of Purpose's mana-to-armour conversion.
- Sightless and Final Blow replace the armed/unarmed attack critical base before increased chance.
  Sea Legs derives evasion and attack/spell critical increases from uncapped resistances after campaign penalties.
  The combined calculation applies overcap once and ignores weapons in the inactive set.
- Whisperer's mana-spending node grants current Rage from paid costs; Rage changes real attack damage and expires.
  Behemoth's maximum and maximum-on-hit-gain chance share that resource, rather than only displaying a maximum.
  The state survives area transfers and clears on death; paid refunds remove its generation source.
- Whisperer's maximum-mana node adds real attack lightning, including attack/melee scaling and its free-cast rule.
  General penetration and critical elemental ignore compose with existing target resistances and nearby effects.
- Behemoth's Incorruptible applies final armour to chaos hits after resistance, with paid refund and relog coverage.
- Passive `GrantedEffectsPerLevel` references grant real skills without gems, with stable skill identities and exact
  native levels. Paladin's level 30 Determination/Grace or level 40 Discipline and Arakaali's level 15 Envy execute.
  Envy's no-reservation rule applies to its real reservation. Removing the source removes the active skill/effect;
  remembered enabled intent is retained, as for unequipped aura gems.
- Passive `BuffTemplates` attachments contribute owner stats and native buffs. Ghost of the Deep applies its actual
  movement, attack, cast and damage increases. Non-recovery buffs have no uncounted stat-value wire payload.
- Oath of the Maji checks socket contents, doubles the body armour's local defences and applies its conditional
  resistance/life/movement bonuses. Glimpse Chaos counts active equipped corrupted items. Item-local defences include
  local modifiers and quality. Sanguine Power substitutes equipped armour's energy shield with life and prevents ES.
- Harbinger gains ES from maximum life/mana; its non-chaos bypass and Scavenger's Shavronne chaos absorption use the
  actual damage path. Skill costs check and spend life/mana together; Impure Blood uses life, Nightblood adds a life
  cost, and Whisperer's no-mana-cost modifier is enforced. Hewn Flesh uses life reservation efficiency and keeps life
  reservation through pool updates, regeneration, healing and area restoration.
- Nearby enemy passives use actual distance and hostility: Light of Divinity increases elemental damage taken,
  Fell Fen overrides nearby chaos resistance, and Born under Fire converts the enemy's outgoing physical damage.
  Presence visuals enter/leave on the client, and hit/DoT damage uses the applicable target modifiers.
- Antiquarian converts body armour/evasion into Ward, with local defence fractions retained until final rounding.
  Ward absorbs one hit before ES, breaks and restores; Starlight scales its value and restoration speed.
  Runic Tablet grants the client's actual Adrenaline modifiers for 20 seconds when Ward breaks.
  Scavenger's Death Rush grants five seconds on a kill. Active Adrenaline does not refresh; its original deadline
  survives area transfers and expiry/death removes its stats and buff.
- Spell-use, spell-damage, warcry and aura restrictions use the actual skill types. Gruthkul refuses spells before
  paying their costs; Rampager still permits utility spell casts while suppressing spell damage. Disabled auras
  suspend their runtime effect/reservation while retaining enabled intent, and restore after refunding the restriction.

## What this does not complete

**The 19 choices are not 19 fully implemented sets of mechanics.** Forwarding a raw stat to the character panel is
not an implementation of its effect. Current numerical support is strongest for Aristocrat and Gambler; even those
classes still have limitations. Support-gem execution and incoming ailments/stuns are absent, so Gem-lined Cap,
conditional ailment immunities and Risk Aversion are not complete. The server also lacks the minion ownership/AI,
warcry/totem, shrine/wisp and other subsystems required by most other signature notables.
Rage has a functioning resource/damage path, but its Vaal costs and other conditional mechanics remain unfinished.

The [all-19 dependency map](phrecia-ascendancies.md#dependencies-of-all-19-classes) names the remaining systems and
implementation waves. Each class must gain an actual gameplay proof for its signature mechanics before it can be
described as fully functional. Monster modifier resistance, exposure and curses still require additional sources.

Ordinary tree connectivity is enforced on allocation, mastery selection, refunds, family changes and restoration.
Ascendant's Path nodes supply their additional class starts; refunding one requires first removing ordinary branches
which depend on it.
Masteries cannot serve as bridges, and unsupported proxy/expansion nodes are refused.
Expansion/cluster jewel topology remains separate unfinished work.
Secondary ascendancy
systems are not modelled as another main choice. Do not infer their support from the spare protocol byte.

All rows below have the shared choice, point, ownership and save lifecycle. The effect column describes executed
server code, not acceptance of the whole class. A maximum-charge value in the panel does not implement current
charges, and a skill's table entry does not grant or execute that skill.

| Phrecia class | Executed effect examples | Important missing mechanics |
|---|---|---|
| Ancestral Commander | Life/armour modifiers; Born under Fire's nearby enemy conversion | Ancestors, totems, echoes and warcries |
| Behemoth | Strength-derived mana; Power of Purpose; unarmed critical base; Rage maximum/chance; chaos armour | Fortification, Rampage, Vaal Rage costs and remaining conditional bonuses |
| Antiquarian | Attributes/defences, Ward, Adrenaline and Strength-derived skill duration | Far Shot, special stun and ranged/shield rules |
| Wildspeaker | Suppression and Dexterity | Wolves, Aspects, Tailwind and minion/charge sharing |
| Whisperer | Mana per Dexterity; no Intelligence mana bonus; mana-derived attack lightning/free costs; Rage | Recoup, mana damage absorption, blind and projectile forks |
| Daughter of Oshabi | Oath of the Maji's equipment conditions and body defences | Wisps, Shrines, Vines and Spellslinger |
| Harbinger | Extra ES from life/mana and non-chaos ES bypass | Granted Harbingers, ally AI and time effects |
| Herald | Shared choice-node allocation, Apocalypse's Herald-gem levels and supported generic modifiers | Actual Herald effects/minions and Herald-specific reservation rules |
| Bog Shaman | Sanguine Power, Nightblood's life cost and Fell Fen's chaos override | Cost-derived damage/self-hits, self-poison, Pacify and Malediction |
| Aristocrat | Ordinary points, active-gem level/quality, attributes/requirements and lowest-attribute damage | Support-gem execution and conditional ailment immunity |
| Gambler | Hit/critical/block luck, triple damage and hit resistance rules | Ailment/stun avoidance and further enemy resistance sources |
| Paladin | Granted auras, Deep Connections's Link-gem levels and Light of Divinity's nearby elemental damage taken | Ground, Links, Relics and party sharing |
| Architect of Chaos | Equipped corruption conditions, Chaos Reigns's Vaal-gem levels, life reservation and life cost | Dissolution, sacrifices and Vaal souls |
| Puppeteer | Percentage Strength and Flesh Puppets's Raise Zombie gem levels | Owned minions, inheritance, corpses and minion-death effects |
| Polytheist | Gruthkul life/regeneration, Brine King maximum cold resistance, Lunaris spell area | Charges, Blind, curses, ailments and triggered effects |
| Servant of Arakaali | Free passive-granted level 15 Envy | Spiders, Webs, Penance Mark and minion effects |
| Blind Prophet | Armed critical base override and supported generic modifiers | Blind, projectile returns/direction and Shade Form |
| Surfcaster | Stormy Seas conversion; Ghost of the Deep; Sea Legs overcap bonuses | Chill/shock/freeze and fishing-rod conditions |
| Scavenger | Exclusive choices; Fulcrum conversion; Shavronne chaos absorption; Death Rush Adrenaline | Most chosen unique-item properties and their dependent subsystems |

## Evidence and reproducible checks

### Passive attachments, costs and nearby effects

Gateway integration checks allocate/refund real graph paths and invoke the production skill, stat and damage methods.
Whaletooth Bangle 27339 adds one increased skill-duration point per complete ten final Strength, once per stat rebuild.
Its allocation, paid refund, reload and final-attribute inputs have Gateway coverage; native duration checks are pending.
The pure duration helper is available to summon-lifetime consumers without assigning owner duration to minion buffs.
They cover all three Paladin aura choices, free Envy, Ghost of the Deep, Oath socket conditions, corrupted equipment,
Sanguine Power, Harbinger's extra ES, atomic life/mana costs, life reservation and nearby enemy modifiers.
They also cover the four family gem-level nodes (Apocalypse, Chaos Reigns, Deep Connections, Flesh Puppets): a
socketed gem's effective level rises with the node and falls back to its intrinsic level on refund, while the stored
experience, quality and requirements stay intrinsic.
The family gem levels are not checked with the real client yet.
The core wire test distinguishes life reservation from mana reservation in the initial Player object.

Native checks use fresh accounts in an isolated ignored database, not the user's existing characters.
The local client casts passive-granted Determination and Envy without gems, retains enabled intent after relog,
and removes their native effects after paid node refunds.
Surfcaster's passive buff and Harbinger's extra ES survive area entry; refunding their paths removes the buffs.
Two fixture characters start with socketed Fireball/Clarity in that isolated database.
With Impure Blood, native Fireball spends five life and no mana.
With Hewn Flesh, native Clarity reserves 17 life and no mana; refunding the path leaves the same aura running with
zero life reserved and 34 mana reserved.
Native traces also confirm installation/removal of nearby enemy buffs for Paladin, Bog Shaman and Ancestral Commander.
The nine scenarios and read-only native observations are in ignored `logs/phrecia-completion/`.
These checks establish the named effects, not completion of their entire classes.

### Ward and timed Adrenaline

With the target client's 19-armour Plate Vest, Kalguuran Armour plus its 10% defence prerequisite gives 10 Ward:
the converted 9.5 base Ward retains its fraction before the final increased-defence calculation.
Starlight and its prerequisite give 20 Ward and a one-second restoration delay.
Native checks observed 90 and 80 life lost from an otherwise unmitigated 100 fire hit, broken/restored Ward buffs,
and the return to 100 life lost after refunding the Ward paths.
Runic Tablet's native Adrenaline remained active after transferring to another town and expired at the original
20-second deadline, rather than restarting its duration on entry.
The trace is ignored `logs/phrecia-completion/ward-native.jsonl`.
The earlier 9/19-Ward probe preceded the fractional-rounding correction and is not the accepted numerical result.
Mixed physical/chaos absorption and Death Rush's five-second actual-projectile-kill trigger have production-path tests.

### Ordinary connectivity and Ascendant roots

The Gateway validates complete ordinary candidates against the selected graph before saving or paying a refund.
See [the rules and load behavior](../protocol/15-progression.md#ordinary-tree-connectivity).
Automated checks include disconnected allocations/masteries, bridge and leaf refunds with gold or regrets,
whole-tree gold refunds, retained invalid saves, reverse-order allocation batches followed by level trimming,
and Ascendant additional-root removal.
Real-data checks cover first allocations for all seven classes in both graph families, mastery shortcuts and
proxy/expansion placeholders beyond ordinary large jewel sockets.

Client 3.29.3b checks use an isolated save database and the native buffered sender through the repository control
bridge.
Normal Elementalist and Phrecia Herald each reject isolated `21678`, accept the Witch path
`57226 -> 21678 -> 32210`, refuse a `57226` refund without spending currency, and refund leaf `32210` for one regret.
The client receives the canonical `0x33` tree after every request.
Normal Ascendant allocates Path of the Marauder `24755`, then ordinary node `31628` from the additional start.
Refunding the Path is refused until `31628` is refunded; the two accepted refunds cost one and five points respectively.
These checks exercise real client/server packets and native tree state, rather than mouse clicks on the passive UI.
All three characters retain their canonical allocations and refund totals after relog and a full client/Gateway restart.
The isolated native trace and results are in ignored `logs/ordinary-connectivity/`.
The verification harness selects password-digest mode for its fresh account and adds the tree request layouts to a
temporary control-bridge copy; it authenticates normally and leaves the tracked bridge unchanged.

### Local client data override

`extract-data.ps1` entry `passive-graphs` v2 generates the server topology and
`data/phrecia-passive-skillgraph.psg` from verified 3.29.3b sources. The original event graph omits 29 nodes from the
current ordinary tree; the merged graph restores them and retains the ordinary directed edges and geometry exactly.
Its SHA-256 is `F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638`.

A full `live.ps1` run closes the old client, recovers any interrupted patch before extraction, and installs the generated
graph before starting Patch/the new client.
The tool appends a dedicated bundle and redirects that one virtual PSG entry in the bundle index. Original bundle
contents and the ordinary PSG remain unchanged. Original index/file backups and a hashed receipt live in ignored
`Client/LocalExileBackups/phrecia-passive-graph-v1/`. `-NoClient`/`-Only` check the actual installed graph read-only and
refuse a mismatch; restarting only the server cannot leave it serving a different topology from the running client.
Every GGPK write and length change is protected by a durable byte-range undo journal, including index/hash updates
made during disposal. Recovery restores raw bytes before parsing the index and checks the Windows file identity.
Interrupted recovery can be repeated. Server-only runs and standalone extraction refuse a pending journal without
modifying the pack. With the client closed, explicit recovery is:

```powershell
dotnet run --project scripts/re/passive-graphs -- --recover Client/Content.ggpk
```

With the client closed, restore the original event graph with:

```powershell
dotnet run --project scripts/re/passive-graphs -- --restore Client/Content.ggpk
```

The next full `live.ps1` run installs the merged graph again. Restore refuses unrelated index or patch-bundle changes.
Actual installation, restoration and reinstallation passed independent GGPK/Node-OOZ readback checks; normal PSG
remained byte-identical. Synthetic tests cover busy-file locks, unknown source data, index edits, bundle-name
collisions, and fresh-process recovery after interrupted writes and length changes during both apply and restore.
The existing
login-key executable patch is still required; the ascendancy change adds no executable patch.

See [the client evidence](../re/ascendancy-client-3.29.3b.md) for exact executable/graph hashes, class-byte encoding,
world-stat packet layout and native point accounting. All game data is generated locally through
`scripts/dev/extract-data.ps1`; no game binaries or extracted tables belong in Git.

`AscendancyGatewayIntegrationTests` calls the real Gateway mutation/load paths with a real `ClientState`, SQLite
saves, TCP and Salsa20. It verifies allocation, cross-family rejection, separate pools, paid refunds, failure and
stale-connection atomicity, old/unknown saves, and Orb of Regret/Book of Regrets followed by allocation/refund.
An actual socketed Fireball has effective level/quality `1/0 → 2/20 → 1/0`; its base damage is `9–14 → 10–16 → 9–14`
and its server hit radius is `10 → 13 → 10`. The gem's saved quality and experience remain zero. These are automated
Gateway checks; live-client observations must be recorded separately.

Run the three required suites from the repository root:

```powershell
dotnet test tests/GameServer.Core.Tests
dotnet test tests/GameServer.Data.Tests
dotnet test tests/GameServer.Gateway.Tests
```

Against the implementation integrated with upstream `793d153`, including ordinary connectivity, the required suites
passed: Core 416, Data 34, Gateway 1,626; zero failures or skips.
The Gateway suite includes 33 actual mutation/network integration scenarios and 20 ordinary-connectivity checks.
The full suite also checks catalog initialization from the test host's subdirectory; catalogs and the default
experience table resolve absolute data paths rather than caching empty tables from an earlier working directory.
The ClientOnly startup mock checks graph validation before gateway writes or client launch.

For a running Gateway whose DLL is locked, use a separate `--artifacts-path Tools/ascendancy-checks`; it builds fresh
dependencies without stopping the client. Live traces use
`scripts/re/frida/run.py <pid> --log logs/ascendancies-native.jsonl scripts/re/frida/trace-ascendancies.js`.

Live 2026-10-25: the executable with only its existing login-key patch loaded `DefaultAltAscendancies` in Standard after receiving stat
`21146=1`; packed class 68 decoded to Duelist/ClassNo 1. The native client retained node 54114, counted one spent
ascendancy point and one ordinary point granted by that node, and displayed 7/1 unspent points after a Gateway
restart. The actual client subsequently loaded the merged graph/bundle, retained six allocated Aristocrat nodes and
counted six spent ascendancy points and eight ordinary points granted by those nodes. A socketed Fireball remained
present after the full client/server restart. Further live checks are recorded in the native evidence note.

In the same client session, a second Duelist in Standard selected normal Slayer. Clicking node 50845 and Apply
Points in the native tree saved it and reduced free ascendancy points from eight to seven. Returning through the
character list to Aristocrat restored the Phrecia graph and its six saved nodes. Clicking and applying Aristocrat
node 34482 changed ordinary points `8 → 9` and free ascendancy points `2 → 1`. Refunding it through the native
Refund Passives button changed those counters back and spent exactly five refund points. The Gateway logs and
read-only native trace agreed on all allocations, graph families and counters.
After the journal-enabled restore/reapply and a full client/server restart, the native trace again recorded Slayer
with node 50845 in `Default`, followed by Aristocrat's original six nodes and eight ordinary-point grants in
`DefaultAltAscendancies`. The post-refund counters survived that restart.

With Predictive networking enabled, a right-click Fireball cast on Aristocrat spent five mana and created, moved
and removed the actual projectile. The published skill had effective level 2, quality 20, base fire damage 10–16
and radius 13. This is a live cast check; controlled hit-radius comparisons and active aura/Righteous Fire refresh
are covered by the automated Gateway checks rather than claimed as manual client observations.

The post-upstream integration run repeated both saved characters on the rebuilt server and client: Aristocrat
loaded the Phrecia graph with its six nodes and eight ordinary-point grants, cast Fireball for five mana and emitted
the new upstream projectile messages; Slayer then loaded `Default` with node 50845. The final native trace is
`logs/phrecia-main/native-final.jsonl`. All server error logs were empty.
