# Additional Phrecia ascendancies

Baseline research and implementation plan, 2026-10-25, against repository commit `2cdaef1` and target client **3.29.3b**.
For the current implementation, commands and explicit remaining limitations, see [ascendancies.md](ascendancies.md).
The code-chain table and baseline test counts below describe the original commit, before the feature changes.
The requested behavior is one main ascendancy chosen from the union of the ordinary and Phrecia options for the
character's base class, in an ordinary league. Existing main options must remain available. Any secondary ascendancy
system present in the target data needs its own identity and budget.

## Finding and evidence limits

At the baseline commit, the server needed an ordinary ascendancy lifecycle before either family could work.
That lifecycle is now implemented. Native executable analysis and Frida established a per-character world-stat
selector for the Phrecia graph: key 21146 in the first optional compact list of 0x1d. Standard league identity and
the ordinary alternatives are retained. This does not create a six-option native Altar window; the current selection
entry point is `/ascendancy`. See [the exact evidence](../re/ascendancy-client-3.29.3b.md).

The initially missing client has now been downloaded from GGG's official CDN, with all 55,972 files verified against
the patch catalog's SHA-256 values. The target 3.29.3b tables are available (943 decoded, zero schema mismatches).
Local tools are .NET SDK **10.0.402**, Node **24.22.1** with `ooz-wasm`, and Python **3.13.17** in `.venv` with Frida.
They are kept in ignored local directories and selected through the current process's PATH, not a global PATH change.
The client has since been launched only through `live.ps1`; targeted Ghidra decompilation and read-only Frida traces
are available. Phrecia graph selection, the packed class, eight earned points and allocation of node 54114 were
observed in the actual client, including its one ordinary-point grant. Current observations are in
[the implementation notes](ascendancies.md#evidence-and-reproducible-checks), separate from the baseline below.

Evidence labels used here:

- **Code:** behavior established by this repository at the commit above.
- **Export:** facts in the pinned GGG JSON below; selected target-table cross-checks are recorded separately.
- **Client data:** facts checked in the downloaded target client and its extracted tables.
- **Open at baseline:** a question that still required GGPK, executable or live evidence when the baseline was written;
  resolved findings are linked to the current implementation/native evidence notes.
- **Proposed:** the original design, experiment or acceptance criterion; its inclusion here does not establish completion.

## Pinned external reference

The primary external reference is [GGG's skilltree-export](https://github.com/grindinggear/skilltree-export), commit
[`8bd138b32ea2631455cac5935bfab089f826094f`](https://github.com/grindinggear/skilltree-export/commit/8bd138b32ea2631455cac5935bfab089f826094f),
dated **2026-07-31 00:26:38 UTC**, with commit message **3.29.1**. Use immutable files, not the current master branch:

| File | Immutable source | SHA-256 |
|---|---|---|
| Ordinary tree | [data.json](https://raw.githubusercontent.com/grindinggear/skilltree-export/8bd138b32ea2631455cac5935bfab089f826094f/data.json) | `7E9F755E33152129EBF36C2EBDAD639C527E4AD70D274B1FEFB860F30CA01122` |
| Phrecia tree | [alternate.json](https://raw.githubusercontent.com/grindinggear/skilltree-export/8bd138b32ea2631455cac5935bfab089f826094f/alternate.json) | `C18A5383151FEDD6CCE7B0AFF89BC3E8632DB076D03098CB6D4D52FC298DC32A` |

These files were downloaded and compared outside tracked repository data. They define the **historical reference
edition** for the baseline comparison/dependency inventory here. Full semantic equality to 3.29.3b has not been
established. The current implementation uses the actual 3.29.3b tables/PSG files described below, not this export;
its roster, start IDs and ownership are checked by the extracted-data catalog tests. The supplied
[wiki page](https://www.poewiki.net/wiki/Legacy_of_Phrecia#Ascendancy_classes) returned HTTP 403 in this environment;
its current text is not used as the authority for the matrix.

**Export:** `classes[*].ascendancies` contains 19 Phrecia options. Each retains a normal internal `id`, while `name`
changes. The ordinary export contains **21** main options: three each for six base classes, plus Ascendant, Reliquarian
and Luminary for Scion. The extra two Scion options have no Phrecia counterpart in this export. Do not hard-code an
ordinary roster of 19, a total of 38, or a single Scion choice.

| Base class | Ordinary display name | Shared internal export ID | Phrecia display name | Shared start node ID |
|---|---|---|---|---|
| Marauder | Juggernaut | Juggernaut | Ancestral Commander | 17765 |
| Marauder | Berserker | Berserker | Behemoth | 29294 |
| Marauder | Chieftain | Chieftain | Antiquarian | 24704 |
| Ranger | Warden | Raider | Wildspeaker | 58427 |
| Ranger | Deadeye | Deadeye | Whisperer | 56856 |
| Ranger | Pathfinder | Pathfinder | Daughter of Oshabi | 64111 |
| Witch | Occultist | Occultist | Harbinger | 18378 |
| Witch | Elementalist | Elementalist | Herald | 16023 |
| Witch | Necromancer | Necromancer | Bog Shaman | 60791 |
| Duelist | Slayer | Slayer | Aristocrat | 33795 |
| Duelist | Gladiator | Gladiator | Gambler | 32730 |
| Duelist | Champion | Champion | Paladin | 24984 |
| Templar | Inquisitor | Inquisitor | Architect of Chaos | 61871 |
| Templar | Hierophant | Hierophant | Puppeteer | 30940 |
| Templar | Guardian | Guardian | Polytheist | 409 |
| Shadow | Assassin | Assassin | Servant of Arakaali | 58229 |
| Shadow | Trickster | Trickster | Blind Prophet | 55236 |
| Shadow | Saboteur | Saboteur | Surfcaster | 18635 |
| Scion | Ascendant | Ascendant | Scavenger | 35754 |

**Export collision analysis:** ordinary `nodes` has 3,390 entries; alternate has 3,331; 2,969 keys are shared. Comparing
the shared entries' `name` and `stats` found no differences. For each paired main ascendancy, the intersection of its
ordinary and Phrecia node IDs consists of its start alone. This is not a full semantic-equivalence check: adjacency,
ownership, choice flags, granted effects, group/position and assets also matter. Sharing an ID with identical text
does not make two alternative graph branches interchangeable. In particular, all 19 shared class/start identities
need an explicit variant mapping when both families are offered.

The shared starts also have different outgoing edges. For example, start `18378` connects to
`48124,6728,31984,32417,17018` in the ordinary export and `11043,24174,58661,63336,1811` in the alternate export.
Aristocrat/Slayer start `33795` connects to `54114` in the alternate tree and
`42293,45696,20954,61393,50845` in the ordinary one. Keeping either record silently loses the other's branches;
unioning their edges still does not create distinct selected identities or enforce mutually exclusive families.

Reproduce the comparison by parsing the two immutable files: match `classes[*].ascendancies` by `id`, find starts in
`nodes` by `ascendancyName == id` and `isAscendancyStart`, then compare both the whole graph namespace and each
ascendancy's node set. Verify file hashes before comparing. These JSON IDs are reference export IDs. At baseline their
target-client mapping was unverified; `AscendancyCatalog` now derives the live mapping from the target's Characters,
Ascendancy and PassiveSkills tables and its two PSG graphs. See [the current guide](ascendancies.md).

## Verified 3.29.3b client data

**Client data:** on 2026-10-25, the official `patch.pathofexile.com:12995` protocol-7 endpoint reported the pinned CDN
`https://patch.poecdn.com/3.29.3.3/`. The full catalog contains 55,972 files and 69,723,115,012 bytes (64.93 GiB).
Every downloaded file was size/SHA-256 checked, then all packed and external files were checked again after assembly.
Verification completed at **2026-10-25 18:47:20 UTC**. The resulting `Client/Content.ggpk` is 69,529,078,047 bytes.

Original `PathOfExile.exe` and its `Game.exe` copy are each 79,345,496 bytes, SHA-256
`78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5`. The original has a valid Grinding Gear Games
Authenticode signature and contains the ASCII version string `3.29.3b` at file offset 50,457,662. Local Auth key
generation and patching succeeded; only the separately named `Game.patched.exe` uses the generated local login keys.
The download inventory and pre-setup verification receipt are ignored `Client/download-manifest.json` and
`Client/download-verified.json`. Subsequent local setup points `gateway_list.txt` at `127.0.0.1:20493`, preserving the
original in ignored `data/gateway_list.original.txt`. Game binaries, data, keys and third-party tools are not part of
the proposed PR.

`scripts/dev/extract-data.ps1 -Only tables` decoded **943** tables, with two empty, 25 absent and **zero schema
mismatches** (`data/extract-report.json`). The official file hashes passed independently; absent schema entries are
not evidence of an incomplete download. All eight extraction manifest entries subsequently completed, including
object/item types, transitions, room points, hideouts, animation contacts and monster hit reactions.

The target `Ascendancy` table has **21 rows**, each with `ClassNo` 1, 2 or 3 for its `Characters` row. It contains the
same ordinary names and the expected Phrecia `LeagueName` aliases. The 21 aliases have only **19 distinct names**:
Ascendant, Reliquarian and Luminary all have `LeagueName=Scavenger`. This does not establish three separate Phrecia
Scion options. Preserve all three ordinary options while determining how the event deduplicates/maps Scavenger.

The target `PassiveSkills` table also confirms shared ownership: start `18378` is `AscendancyOccultistStart`, owner
`AscendancyKey=6`; start `33795` is `AscendancySlayerStart_`, owner 9. Inherited Fortune (`48846`,
`AscendancyCustomSlayer8`) also has owner 9 and `SkillPointsGranted=5`. Centerpiece (`30851`,
`AscendancyCustomSlayer10`) has owner 9 and stat row 15620, `all_skill_gem_quality_+`, value 20 (wire stat key 15621).
Thus the selected reference nodes match these target rows. A complete PSG/edge comparison and chooser/variant trace
remain outstanding; matching these samples is not full tree or native combined-selection verification.

The bundle index lists 1,214,937 paths and contains four matching graph files: the ordinary
`metadata/passiveskillgraph.psg`, `metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg`,
`metadata/alternateskillgraphs/royalepassiveskillgraph.psg`, and
`metadata/leagueskillgraphs/chayulatreepassiveskillgraph.psg`. Their names establish asset availability, not the
runtime selector or the ability to combine trees. Selected raw sources were dumped read-only to ignored
`Tools/re/out/phrecia/`; the confirmed hashes are:

| Virtual source path | SHA-256 |
|---|---|
| `data/ascendancy.datc64` | `97E7A834A76063900052C14EA0DA04A49C9404EFDD23AD96CD1ACA60AB4EE253` |
| `data/passiveskills.datc64` | `83763D4491F00D85500B6B998DD086472550650738D7A3F16353B5E3D4474020` |
| `data/stats.datc64` | `DC2854C92C79D3FCBB0D31B59ED5AA668D3E8D90B95E6D833D10DF5AB896AB4C` |
| `metadata/passiveskillgraph.psg` | `82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32` |
| `metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg` | `9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC` |

## Baseline server chain (`2cdaef1`)

File/line anchors below refer to `2cdaef1`, before implementation. The current branch changes these files; this table
records why the work was needed, not current behavior.

| Stage | Code evidence | Required work |
|---|---|---|
| Access and selection | [Roadmap](../../Roadmap.md#character-progression-the-rest-of-the-passive-skill-tree) leaves selection open; `Program.cs:449-474` handles ordinary passive allocation/refunds. No ascendancy chooser or Labyrinth-device handler was found. | Map the native choice request, eligibility, unlock awards and class-change conditions. |
| Base identity | `Core/Accounts/Models.cs:8,16`, `Auth/InstanceDirectory.cs:131`, `Core/Protocol/Instance/InstanceControl.cs:82` carry base class. | Preserve base class; add durable main ascendancy identity and family. |
| Character list | `Core/Protocol/Login/LoginServerSession.cs:248` emits base `(byte)c.Class`. | Determine correct list identity/portrait encoding; derive its projection from committed progress. |
| Player identity | `Gateway/Program.cs:7071` uses base `IntegerId`; `Core/Protocol/Instance/InstanceEntities.cs:846` writes it. `Gateway/SkillCatalog.cs:556-579` distinguishes row from IntegerId. | Verify Player and tree encodings independently, including the update on choosing/changing class. |
| Tree and points | `InstanceEntities.cs:143-147` supports `ascendancyPoints`; `Program.cs:3140-3141` sends the base class and omits this argument. | Supply consistent selected identity, graph and separate ascendancy budget. |
| Node ownership | `PassiveTree.cs:16-17,92` stores only `bool IsAscendancy`; `IsStart` comes from `Characters`, not `IsAscendancyStartingNode`. Its lines 6-7 state that graph connections are not loaded. | Load actual owner, family, start flags, graph edges and exclusions. |
| Allocate and refund | `Program.cs:3169` rejects all ascendancy nodes; `:3188` counts one ordinary pool. `PassiveTree.cs:40` sums generic `PointsGranted`. | Validate selected branch, connectivity, choices, costs and grants to the right pool. |
| Ordinary level changes | `Program.cs:3121-3125` trims the last allocations using ordinary points; `:2699-2706` invokes and persists this. | Keep ordinary trimming from deleting ascendancy state or concealing budget violations. |
| Effects | `Program.cs:2833-2836,2861,2947+` aggregates stats and explicitly interprets supported effects; `:2379-2400` publishes raw stats to the panel. | Connect granted skills, buffs and signature mechanics to simulation; panel text alone is not functional support. |
| Save and relog | `CharacterSaves.cs:46-54` lacks ascendancy state; `Program.cs:2811` filters ascendancy nodes out on load. | Save and restore identity, allocations, choices and awards before constructing pools, Player and tree. |

Paths in this table beginning with `Gateway/`, `Core/` and `Auth/` are under `src/GameServer.` respectively. For example,
`Gateway/Program.cs` is `src/GameServer.Gateway/Program.cs`.

At baseline ordinary ascendancies needed the shared lifecycle above. The current implementation supplies selection,
identity, point accounting, allocation/refund and durable restoration. An actual acquisition path remains unfinished:
Labyrinth content and the native Altar/choice exchange are separate from displaying a tree. The development unlock
isolates that work; it does not satisfy final gameplay acceptance. Ordinary ascendancy-specific effects also need an
effect inventory rather than an assumption that every stat is implemented.

### Original durable and runtime proposal

This is the baseline design brief. The implemented state is `MainAscendancy`; its exact behavior and remaining
limitations are in [the current guide](ascendancies.md#what-is-implemented).

Keep `Character.Class` as the existing base class and ordinary `Passives/MasteryEffects` as ordinary progress. Add an
optional versioned `Ascendancy` record containing `Family`, a stable logical `AscendancyId`, `DataRevision`, idempotent
`UnlockAwardIds`, `AllocatedNodes`, and explicit `Choices` where required. `(Family, AscendancyId, DataRevision)` is the
logical key: `Occultist` alone cannot distinguish ordinary Occultist from the export's Harbinger alias. Packed bytes
and mutable table row numbers belong in a verified runtime catalog, not the durable identity.

Derive earned/spent/unspent ascendancy points from validated awards and node costs. Ordinary points come from level,
quests and explicitly ordinary grants. Scion/Aristocrat nodes can intentionally grant ordinary passive points: separate
pools must permit those declared grants, without allowing arbitrary cross-funding. Starting and multiple-choice nodes
must use their real costs, not a universal `allocated.Count`. Refund of a prerequisite or point-granting node must
preserve reachability and both budgets; use the target client's actual policy for rejecting or cascading a refund.

Selection, class changes, node removals, refund payment and consumed items/gold must commit together. Existing
`Program.cs:3197-3210` saves before applying connection state at `:3213+`; `CharacterSaves.SetPassives:215-216` and
`Saves.Atomically` provide the pattern. `Storage/SaveDatabase.cs:65-107` joins nested transactions, rolls back and
invokes registered `OnRollback` callbacks. New caches must register their invalidation or remain unchanged until
commit; invalidation is not automatic. `Gateway/Saves.cs:36-42` exposes a write-failure injector for tests. If Auth
needs a list projection, derive it from committed state or update it in the same database transaction.

Old saves with no ascendancy record stay unascended. Unknown/unsupported family, revision or node must stop loading
with a diagnosable error or enter an explicit migration; it must not silently discard nodes. Preserve unrelated save
fields and items. The existing unknown-JSON preservation test at `ProgressSaveTests.cs:77-85` uses a dummy
`Ascendancy` field; it is not evidence of an implemented lifecycle.

## Client questions and how to resolve them

**Schema:** `data/schema/schema.min.json` is version 7, `createdAt=1789458335`, SHA-256
`3FB5AFC7BD952BD1B38595BDC4582E0B691EC73E6E20B7802FA8894AA67B4F48`. This identifies the schema, not the game contents.
Its PoE1 `Ascendancy` has `Id`, `ClassNo`, `Character`, ordinary `Name/BackgroundImage/PassiveTreeImage`, and
`LeagueName/LeagueBackgroundImage/LeaguePassiveTreeImage/LeagueIcon`. `PassiveSkills` links through `AscendancyKey`
and includes `IsAscendancyStartingNode`, `SkillPointsGranted`, multiple-choice flags, `PassiveSkillBuffs` and
`GrantedEffectsPerLevel`. These last effect sources are not handled by the current passive loader.

`AlternateTreeVersions` has `Id` and nine unnamed fields. Neither its name, `AlternatePassiveSkills/Additions`,
`IsBrequelStartingNode`, generic league flags, nor the spare byte in `0x33` establishes a Phrecia or Bloodline selector.

**Encoding (now resolved):** `base = b & 0x1f`, `a = (((b >> 5) & 1) << 2) | (b >> 6)`, with
`base = Characters.IntegerId` and **`a = 0` for unascended**. The earlier base-row wording and `7 = none` interpretation
were wrong; the comparison with 7 applies to the whole byte. `CharacterClassByte` implements the proven inverse.
The eight representable bit patterns alone do not prove a six-option native chooser; the current implementation
retains ClassNo 1..3 and chooses the graph family via the received world stat. See [native evidence](../re/ascendancy-client-3.29.3b.md).

Original research checklist, in dependency order (use the current/native evidence notes to distinguish completed
investigation from remaining Altar/UI and gameplay work):

1. Record original/patched EXE versions and SHA-256 separately, GGPK root hash and full-file hash where practical,
   schema hash, extractor commit, and every source table/graph path and hash. Gateway-list patching already changes
   the GGPK root; identify which state was hashed. Compare the target tables/graph with the reference edition.
2. List actual GGPK paths before choosing graph names. Extract the complete ordinary roster, all Phrecia variants,
   their graph starts, ownership, edges, choices and effect dependencies. Build the crosswalk
   `Characters row/IntegerId -> Ascendancy Id/row/ClassNo -> family -> graph -> start -> wire node ID`.
3. Classify shared nodes versus conflicting IDs using full semantics and adjacency. Check dangling edges, valid ID
   range, unique ownership and reachability. `PassiveTree.cs:91-93` currently uses `TryAdd` and silently keeps the first
   duplicate; a combined catalog must reject unexplained collisions. Decide whether variant graph selection is
   sufficient or added classes need distinct starts and remapped IDs.
4. In Ghidra trace Player deserializer `0x141e7dc10`, class-change notification `0x141e473f0`, consumer `0x142156d80`,
   and independently `0x33` reader `0x14219e3f0`, handler `0x141ef2710` and allocation validator `0x141ef3e60`.
   Follow table/asset xrefs to `ClassNo` lookup, chooser enumeration/layout, graph loading and the `League*` selector.
   Determine whether each selector is per character, connection, world, league or global client state.
5. Attach Frida to the single locally launched client. Capture decrypted application messages, chooser request,
   Player/tree class bytes, resolved row/ClassNo, graph path and variant before/after selection and respec. Use
   `common.js` rebasing; the existing `trace-tree-mode.js` traces ordinary/gold respec mode, not ascendancy selection.
   Add a focused research trace after the relevant functions have been identified.

The existing read-only listing command, checked against `scripts/re/ggdump/Program.cs:10-25`, is:

```powershell
dotnet run --project scripts/re/ggdump -- Client/Content.ggpk Tools/re/out/phrecia list '(Ascendancy|PassiveSkillGraph.*\.psg$|Phrecia)'
```

Use the returned exact paths for raw extraction into ignored `Tools/re/out/`. Server-consumed data must still come
through `scripts/dev/extract-data.ps1`. Use `scripts/re/ghidra/run.ps1`/`Decomp.java`/`Callers.java` as documented in
[tooling](../re/tooling.md); every address above is a documented lead, not a new live observation.

### Decision after the experiment

- **Server-only candidate:** native assets and protocol expose both families in one ordinary league, with a stable
  per-character identity and both families selectable. It passes the full A/B/A experiment below.
- **Data patch candidate:** the wire/lookup range works, but records, starts, graph or chooser entries are missing.
  Generate extra records and deterministic ID mappings; merge/select the graph and supply the required assets.
- **Code patch candidate:** decompilation and traces establish a hard chooser/lookup/layout limit that data alone
  cannot change. A version-pinned EXE patch for those exact consumers is then required; its extent is unknown.

A confirmed single global replacement selector combined with an exhaustive chooser/graph path that supports only one
family would rule out the native server-only approach. A failed packet attempt alone does not: it can instead mean
the server sent inconsistent or incomplete state. Trace the rejection before blaming the client.

`GameFileSystem.cs:31-61` reads bundles and lists their virtual files. `GgpkMaintenance.cs:31-38` replaces an existing
direct GGPK `FileRecord`; this is not a ready writer for an individual virtual `.datc64` or `.psg` inside bundles.
At baseline `GameServer.Patch/Program.cs:184-220` only supplied root-hash/gateway maintenance; a combined-tree/bundle
writer and rollback system did not exist. The current `scripts/re/passive-graphs` pipeline implements a deterministic
merged event graph and guarded bundle override/restore, with extraction-manifest integration. See
[local client data override](ascendancies.md#local-client-data-override). A native Altar chooser remains separate work.

## Dependencies of all 19 classes

This is the **baseline subsystem dependency inventory**, not a current readiness table. Its adapter column preserves
the original work breakdown: some adapters are now implemented, while its signature subsystems largely remain absent.
All 19 target-client Phrecia identities are selectable through the shared lifecycle. Aristocrat and Gambler have
the strongest executed numerical subsets. **None of the 19 classes is claimed to have every mechanic implemented.**
See [the current implementation and limitations](ascendancies.md#what-this-does-not-complete), instead of treating an
entry below as either wholly missing or wholly functional. The wave is the earliest useful subsystem work in the
original plan, not a promise that the entire class is complete in that wave.

Target-data correction: 3.29.3b Polytheist nodes do not use `IsMultipleChoice`/`IsMultipleChoiceOption`; its Devout
branches are not a generic exclusive-choice group. Do not impose the Scavenger/Ascendant option rule on those nodes.
The class still requires its individual conditions, restrictions and granted skills.

Baseline code keys: **P** `PassiveTree.cs:40`; **S** `CharacterStats.cs:81,100` and `Program.cs:2947-3110` (pools/stats);
**D** `Core/World/DamageModel.cs`, `DamagePacket.cs`, `Gateway/DamageStats.cs`, `SpellDamage.cs`;
**Q** `SkillCatalog.cs:92-145` (gem level/quality); **A** `AuraCatalog.cs:1-53` (ordinary self auras/buff transport);
**C** `ChargePanelStats.cs:9-12` (maximum rows only); **R** `RecoveryPool.cs:1`, `Flasks.cs:1-35`;
**E** `MonsterCatalog.cs`, `Program.cs:2158+` (monster objects/hit/death hooks). Conditional flags displayed by
`PanelStats` are not evidence of execution. Percentage attributes are now calculated by `PassiveAttributeModifiers`;
current charges, minion ownership and a functional ES pool remain absent. A pure helper or a displayed maximum is
not sufficient evidence that an incoming effect, summon or charge action executes.

| Class | Representative mechanics | Reusable primitives | Partial/new adapters and absent systems | Wave |
|---|---|---|---|---|
| Aristocrat | Inherited Fortune; Centerpiece | P ordinary point grants; Q level/quality inputs; gem requirement checks | Separate grant destination; global gem level/quality; percent attributes and lowest-attribute damage; requirement modifiers, conditional ailment immunity and support effects | 1 |
| Gambler | Block Luck; Growing Accumulator | D block/lucky-block, critical and resistance rolls | Per-event lucky/unlucky policies; triple damage; inverted enemy resistance and random resistance override; ailment/stun avoidance | 2 |
| Polytheist | Devout of Gruthkul; Devout of the Brine King | S life/resistance/evasion; R regeneration | Devout-specific state predicates and spell disabling; current/minimum charges, Blind, Hex reflection/Hexproof, burning ground and triggered nova/projectiles. No generic exclusive-choice group in 3.29.3b. | 2 |
| Surfcaster | Ghost of the Deep; Stormy Seas | S attack/cast/move speed; D offensive conversion | Overcapped-resistance and fishing-rod predicates; permanent touched source; chill/reverse chill, freeze, propagation and fishing rewards | 2 |
| Ancestral Commander | Ancestral Endurance; Deathrattle | S armour/life; C maxima; R; E death hooks | Action-speed bounds, incoming conversion, strike echoes/extra targets; warcries, totems/damage transfer, ancestors and corpse explosions | 3 |
| Behemoth | Dire Rampart; Rampager | S armour/unarmed/crit; R | Armour against chaos hits, base-crit overrides, armour scaling; Rage, Fortification, Rampage, Vaal costs and skill-family disabling | 3 |
| Antiquarian | Exhibit 24 Goliath Knuckle; Exhibit 377 Kalguuran Armour | S flat attributes, shield block and armour/evasion | Attribute overrides, special block, incoming conversion/Far Shot; Ward pool, break/restoration and Adrenaline | 3 |
| Whisperer | an infinite pursuit; don't panic yet | S mana/attack channels; R mana flasks | Mana/attribute-derived damage and spell-to-attack scaling; Rage, current charges, fork, recoup, mana damage absorption and shared recovery | 3 |
| Bog Shaman | Venom Drinker; Eerie Silence | S life/mana; D chaos; SkillArea | Upfront-cost self-hit, life costs, damage from cost and ES-armour substitution; self-poison, curses, Pacify, enemy resistance override | 3 |
| Architect of Chaos | Glimpse Chaos; Dissolution | Saved corruption flag; S/D; ordinary mana reservation | Equipped-corruption predicates, life reservation/cost; damage-reserved life and release timer, Vaal souls, Soul Gain Prevention, sacrifice and ES | 3 |
| Blind Prophet | Sightless; Directionless | Weapon crit baseline; projectile objects; S | Base-crit override, Abyss-jewel counting and extra projectiles; Blind, persistent curses/corpse explosions, returns/random direction, Shade Form and Elusive | 3 |
| Wildspeaker | Primal Instinct; Pack Mentality | S suppression/flat attributes; C maxima | Owner/minion stat and charge propagation; Spectral Wolves, Aspects, Tailwind and stolen rare modifiers | 4 |
| Daughter of Oshabi | Oath of the Maji; Woodland Shrines | Equipment/socket state; S; A transport | Socket-conditional bonuses, trigger/reservation context; shrines, Sacred Wisps, Grasping Vines, poison and Spellslinger | 4 |
| Harbinger | Harbinger allies and time effects | Skill records/cooldowns; A transport; R | Passive-granted skills and duration/cooldown modifiers; visiting Harbinger AI/buffs, ES/recharge/bypass and Temporal Chains aura | 4 |
| Herald | Coming Calamity; Herald Specialty | Q; reservation efficiency; A transport | Herald-specific levels/reservation and choices; Herald triggers, Sentinels/Agony Crawler, Virulence, Conflux, ailments and recoup | 4 |
| Paladin | Paladin Aura; Anointed Champion | A includes Grace/Determination/Discipline; reservation/effect primitives | Granted level-30/40 auras and choices; Discipline ES remains partial; Consecrated Ground, Links, party/flask sharing, Relics, Taunt and warcries | 4 |
| Puppeteer | Marionettes; Graveyard Shift | E objects; Q; chaos DoT primitive | Owned minions and stat inheritance; Zombie/Guardian/Weapon AI, corpses, minion-death ground, curse propagation and minion ES | 4 |
| Servant of Arakaali | Broodguard; Malevolent Strands | A has Envy; D chaos; C maxima | Granted/free Envy and owner/minion modifiers; Spiders, Penance Mark/Phantasms, Webs/Aspect, poison/Wither and current charges | 4 |
| Scavenger | Scavenged Armour; Scavenged Caster Weapon | S/D/A cover some underlying effects | Multi-choice and a reusable unique-property effect library; Ward/ES/recoup/leech, Rampage, minions, triggers and conditional effects | 4 |

This suggests implementing shared effect families rather than nineteen independent switches. For example, minion
ownership/AI unlocks work for several wave-4 classes, and event-driven conditions serve many classes across waves.
Each full class needs a node-level checklist, including multiple-choice children, granted skills and negative effects.
Mark supported branches explicitly while developing; do not offer a class as complete with inert notables.

## Original minimum falsifiable experiment

This preserves the baseline acceptance design. Executed slices and live observations are now recorded in
[the current guide](ascendancies.md#evidence-and-reproducible-checks); an unchecked item here is not automatically a
current blocker, and completion of a numerical slice does not establish all-19 gameplay support.

**Proposed pair:** Duelist Slayer and Aristocrat. The export's shared start is `33795`. Aristocrat's Inherited Fortune
`48846` grants five **ordinary** passive points; Centerpiece `30851` adds 20 quality to skill gems. Resolve these against
the target data before use. This pair exercises identity, a cross-pool grant and gem effects without requiring minion AI.

1. Establish original-state screenshots/logs and isolated test characters, with unchanged base class and ordinary
   league. Implement only the common state/transaction/encoding foundation needed for the test. A dev unlock grants
   test eligibility idempotently; label it separately from actual Labyrinth acquisition.
2. Require both families in the available selection flow. Select Slayer for A and Aristocrat for B. Use one client,
   sequentially **A -> B -> A**; repeat after a full client and Gateway restart. An event toggle that merely replaces
   the available family does not establish the requested combined choice.
   On A, allocate and refund a supported ordinary Slayer node: reference node `42293` includes 5% increased attack
   speed. With unchanged equipment/other speed sources and zero other increased speed, predict the 1.05 speed ratio
   independently, allowing documented rounding/tick timing; also check separate point spending. Repeat this control
   across area transfer, relog and restarts as well; selecting A without exercising its tree is insufficient.
3. On B, record ordinary and ascendancy earned/spent/unspent values immediately before and after Inherited Fortune,
   accounting for its already allocated prerequisites. Ordinary available points increase by exactly five; ordinary
   allocation consumes only the ordinary pool. Ascendancy spending follows its own node costs. Refund must remove the
   grant without leaving an over-budget/disconnected ordinary tree or partially spending its payment.
4. For a separate numerical execution check, add a global effective-quality provider for Centerpiece and route it
   through skill records, actual hits/auras, mana costs and restored skills. Choose a gem whose target-table quality
   stat has an implemented observable effect. Predict values independently from that stat's per-quality coefficient:
   `trunc(coefficient * effectiveQuality / 1000)`, with existing quality sources included. Use a verified positive
   coefficient for this control; rounding of negative quality stats needs separate research. Verify the resulting
   regeneration/damage/area behavior, not only the item's or character panel's text. If no suitable quality effect is
   supported, use Family Heirlooms' effective gem-level grant with a supported spell and independent per-level lookup.
   This adapter is new work; accepting a quality argument in `EffectStats` does not implement a global modifier.
5. Refund both effects and repeat after area transfer, logout, Gateway restart and client restart. Original stats,
   skill numbers and budgets must return; other characters must retain their chosen family. Ordinary gold Refund All
   must preserve ascendancy state.
6. Trace and reconcile chooser input, both class bytes, graph selection, node lists, budget fields, actual effect
   output and durable state. A screenshot alone or manually injecting `/set_stat` cannot establish this chain.

Save an evidence bundle under ignored `Tools/re/out/phrecia/` (and normal logs): source provenance, a readable
crosswalk/graph audit, decoded packet fields with timestamps, focused Frida trace, redacted test-state snapshots,
screenshots/video, predicted-versus-observed results and exact reproduction steps. Do not include credentials, login
keys or official-service traffic. Keep conclusions and source/code references in tracked documentation.

## Delivery stages and acceptance

1. **Materials and inventory:** restore documented tool/client prerequisites, extract source data, resolve roster,
   variants, starts, collisions and effect dependencies. Exit: reproducible hashes/crosswalk and a target-edition match
   or explicit difference list. Full data dumps stay ignored.
2. **Client feasibility:** discover chooser/variant protocol and independently verify both class-byte consumers;
   determine server-only, data-patch or code-patch scope. Exit: the combined chooser/identity part of the A/B/A test
   passes, or a traced client limitation and concrete patch design identify the next work.
3. **Shared lifecycle and narrow behavior proof:** versioned save model, awards, distinct budgets, ownership/graph
   checks, atomic choice/refunds and canonical refusal packets; complete the Aristocrat/ordinary comparator slice.
   Stages 2 and 3 share only the minimal test foundation until the client decision is known.
4. **Player acquisition:** implement native Altar/selection/class-change behavior and Labyrinth/trial award hooks;
   every award is idempotent. Exit: a player can earn and choose either family without a development command.
5. **Mechanic waves:** start with Aristocrat; then randomness/conditional-stat work for Gambler/Polytheist/Surfcaster;
   next resources, mitigation, costs and projectiles for wave 3; then owned entities, world effects, party systems and
   unique-property choices for wave 4. A class completes only when all of its nodes and drawbacks pass its checklist.
6. **Full regression and delivery:** all target-client ordinary options plus all 19 Phrecia options work, including
   Scion's actual roster and any confirmed independent secondary system. No hard-coded three-choice assumption.

Required automated checks: verified packet layouts/codec round trips; real-data catalog cardinality and collision
checks; graph ownership/reachability; wrong class/family/branch refusal; overspending; exclusive choices; duplicate
awards; prerequisite/point-grant refunds; ordinary level trimming; old/unknown save versions; effect enable/disable;
area/relog restoration; and injected failure at every identity/payment/save boundary. Assert precomputed expected
results rather than duplicating the implementation in tests. Test negative effects and removal, not just bonuses.

Use `Saves.FailWrite` and existing transaction/reopen tests (`SaveDatabaseTests`, `ItemMovesTests`, `ProgressSaveTests`) to
verify that identity, allocations, points, choices and payment remain at the last committed version after failure.
Unknown target data must stop loading without rewriting the save. Ordinary points can increase from a declared
ascendancy grant; they cannot purchase an ascendancy node. Every offered class must pass a real-client selection,
allocation/refund, numerical behavior and signature-mechanic check, with area/relog/restart persistence.

All client launches must use `scripts/dev/live.ps1`, exactly one client, from the repository root. Read `logs/` and
`Client/logs/Client.txt`. Never launch the executable directly. See [GettingStarted](../../GettingStarted.md).

If server-consumed graph/catalog files are introduced, add a versioned manifest entry with the appropriate table
dependencies in `scripts/dev/extract-data.ps1` and a matching `GameDataFiles.cs` requirement in the same change. Bump
an existing entry when its output changes. Do not check in generated game data or binaries. The original research-only
change needed no manifest bump; the subsequent implementation includes the `passive-graphs` manifest entry and Gateway
requirement. Record each implemented stage in Status and update its Roadmap entry; retain outstanding verification explicitly.

## Baseline verification and subsequent progress

Completed: repository/mandatory-doc review; server/save/packet/schema audit; immutable official-export download/hash
and ordinary/alternate comparison; all 19 dependency entries; local prerequisite checks. After the initial attempts
could not find `dotnet`, SDK 10.0.402 was installed in ignored `Tools/dotnet10/` using Microsoft's official install
script. `dotnet build GameServer.slnx` succeeds with zero warnings/errors. No feature source changes precede this
baseline. The client was then obtained and all extraction manifest entries completed; the following fresh checks use
the real local client and extracted data. These are setup/protocol/data checks, not interactive ascendancy verification.

| Required command | Actual result in this environment |
|---|---|
| `dotnet test tests/GameServer.Core.Tests` | Exit 0; 395 passed, 0 failed, 0 skipped |
| `dotnet test tests/GameServer.Data.Tests` | Exit 0; 15 passed, 0 failed, 0 skipped |
| `dotnet test tests/GameServer.Gateway.Tests` | Exit 0; 1,376 passed, 0 failed, 0 skipped |

Fresh TRX results are in ignored `logs/client-verification/`. The earlier no-data baseline remains in
`logs/ascendancy-baseline/` (Core 395 passed, Data 15 passed, Gateway 392 passed/116 failed). Those Gateway failures included missing `data/tables`,
`CraftingBenchOptions.json` and `Essences.json`, including fixture initialization exceptions wrapping those errors.
One panel assertion reports the missing `data/tables` for each expected stat. These are the baseline results before
ascendancy implementation; data-backed theory enumeration increases the count once the tables are present. Some existing real-client/data tests return normally when files are missing
(`LoginCryptoTests.cs:99`, `HncTests.cs:61`, `WaypointTests.cs:99,138`, `StashRulesTests.cs:206`,
`HideoutCatalogTests.cs:78`); the fresh run now has the expected client and data files. Passing these checks does not
replace a live-client choice/allocation/refund experiment.

Since this baseline: both target PSG files round-trip exactly; Ghidra/Frida established class-byte and graph-family
selection; Gateway integration tests cover allocation, paid refunds, separate budgets, reload and failed/stale saves.
Current tests and live observations are recorded in [ascendancies.md](ascendancies.md) and the native evidence note.
The original event graph omits 29 non-main-ascendancy nodes and differs in ordinary edges. The implementation now
generates the merged graph and installs a reversible local client-data override to preserve ordinary topology; see
[the installation and readback evidence](ascendancies.md#local-client-data-override).
The full native Altar/Labyrinth flow, all-19 signature mechanics and remaining acceptance scenarios are outstanding.
The startup requirements are in `Agents/GettingStarted.md` and the hard guards in `scripts/dev/live.ps1`; the repository
does not distribute the required historical standalone client.
