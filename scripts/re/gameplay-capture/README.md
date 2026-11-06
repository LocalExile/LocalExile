# Official opening-run capture

Run from the server repository root after loading `scripts/dev/local-env.ps1`:

```powershell
python scripts/re/gameplay-capture/capture.py --client 'X:/Path/to/PathOfExile.exe' --check
python scripts/re/gameplay-capture/capture.py --client 'X:/Path/to/PathOfExile.exe'
```

Then launch your separate official installation yourself and log in normally.
The recorder waits for that exact executable path; it never launches or patches the client.
It checks the exact researched 3.29.3b SHA-256 before attaching and refuses changed builds.
Do not start the reference run until the recorder reports `ready` and both `FIRST C2S` and `FIRST S2C`.
Use an existing character for a preflight first; then create the reference Ranger while recording.
Record Twilight Strand, the starting weapon/gem, combat, Hillock, town entry, reward and inventory changes.
Use your preferred screen recorder for video; this tool does not capture video or keyboard input.
Stop with Ctrl+C after town interactions. Detachment never kills the game.

Output is private, ignored research data in `Tools/re/out/gameplay-captures/<timestamp>/`:
`session.json`, `events.jsonl`, `payloads.bin`, and the appended portion of `Client.txt`.
Each binary payload's offset and length are in its event. Payloads are plaintext and can contain chat,
character data and instance session material; do not commit or publish raw recordings.

The hooks observe the instance tick, send serializer, message pump, and stream reader/writer.
S2C `accepted=false` events are failed/partial attempts, not complete messages; retain them as diagnostics.
Record boundaries are serializer/pump boundaries, not TCP packet boundaries or timing of socket delivery.
Login/character-selection traffic and side channels are outside scope. No screen/video recording is bundled.
Python/JavaScript checks and executable identity were validated during preparation; live hook coverage
and overhead must be verified before relying on a reference run. Empty directions, capture errors,
crashes, or missing intervals make a capture incomplete. Raw playback alone cannot reproduce server RNG.

## Preliminary offline study

```powershell
python scripts/re/gameplay-capture/analyze.py Tools/re/out/gameplay-captures/<timestamp>
```

Writes `study.json` next to private evidence. It checks payload indexes and known request lengths,
decodes action-start compact coordinates and hit/life layouts, and summarizes opcodes, frame arrival
cadence and the town transition. Failed pump attempts and truncated payloads are excluded.
It reads the original `events.jsonl`, not a hand-curated accepted-record index.
The report contains identifiers and aggregate counts, never raw payloads or character/chat strings.
Capture timing is client hook timing: it cannot measure server CPU time or network-only RTT.

## Local comparison run

Start the prepared client only through `scripts/dev/live.ps1` (or root `Start-Local.ps1`).
Use `python scripts/re/gameplay-capture/capture.py --local` before entering the reference character.
The local recorder requires the default launcher's matching PID/start-time record and a pinned patched executable.
It uses identical instance hooks, records the source commit and working-tree fingerprint, and keeps the same format.
Login/menu state naturally has no instance messages; both directions should appear once a character enters an area.
For a clean stop, create the `STOP` file inside the active capture directory; wait for `Saved ...` before analysis.
This also finalizes the client log and manifest. Prefer this over killing the recorder process or its shell.

Compare official and local captures offline:

```powershell
python scripts/re/gameplay-capture/compare.py Tools/re/out/gameplay-captures/<official> Tools/re/out/gameplay-captures/<local>
```

The private `comparison.json` includes network-mode detection, per-phase counts and repeated explicit
pool-state counts. Mode/seed differences are confounds; repeated pool fields are investigation candidates,
not proof that sending them is unnecessary. Comparison never prints raw payloads.

## Entity, NPC and equipment study

```powershell
python scripts/re/gameplay-capture/semantics.py Tools/re/out/gameplay-captures/<timestamp>
python scripts/re/gameplay-capture/items.py Tools/re/out/gameplay-captures/<timestamp>
```

`semantics.json` resolves NPC and level-1/2 monster type hashes, conversation rows, hit/life events,
monster component prefixes, and supported local-player prefixes. Stats on the wire use table row + 1;
Mods table stat references use zero-based rows. Base-data entries with u32 entity-reference values are decoded;
other base value types and unsupported player buffs are rejected.
Monster decoding supports captured initial `hidden_monster` and `kitava_heart_damage_taken` buffs and continues
through Life. Their creation layout differs from the compact buff-update layout. Other initial buff backends
stop decoding explicitly because they may consume additional state; remaining component bytes are not claimed decoded.
Skill-stat updates (`0x1b4`) retain the entity, full skill ID, primary/additional stat-set selector and ordered signed
stat pairs in `skill_stats`, with malformed/truncated packets in `skill_stat_failures`. These are deltas, not complete
skill baselines or projectile paths; they do not establish every effective stat of an action already in flight.
`buff_updates` retains compact buff add/update prefixes and exact removals. Adds preserve instance identity,
source, stack count (default one), duration bits, presentation flags and the number of undecoded tail bytes.
Repeated adds for one instance are updates, not independent stack gains. Backend extra/recovery fields and list
contents remain undecoded; `buff_update_failures` reports malformed prefixes. Missing duration means the native
infinite default; non-finite values remain available through their raw bits rather than nonstandard JSON floats.
Deduplicate stream/entity before interpreting population counts: objects can be recreated after waking.
`equipment.json` decodes selected weapon/helm/flask snapshots, nested gems and quest reward inventories;
it checks item consumption and reports unsupported fields. It excludes general loot and private stash contents.
Different gear, exposure and monster variants must be accounted for before inferring damage or spawn rates.

`python scripts/re/gameplay-capture/population.py <folder>` writes private `population.json` for the opening
area before Lioneye's Watch. It deduplicates creation identities and reports already-existing hidden monsters and
untyped combat actors separately, excluding player order headers. Missing-creation actors retain unknown rarity.
Its reconstructed total is provisional: it cannot count monsters absent from both creation and update messages.

`python scripts/re/gameplay-capture/pack_order.py <folder>` writes private `pack-order.json`.
It compares first-created Drowned identities with their order in MonsterPackEntries, before the town transition.
Groups require consecutive entity IDs and an all-pairs distance bound, tried at 24, 32, 48 and 64 cells by default
(`--diameters`; `--pack` selects another pack). These are candidate groups, not decoded pack identities.
The grouping never uses variety order. Missing IDs split groups; later movement and neighbouring packs can change them.
The report retains entry flags, weights, ranks and positions, including order inversions rather than dropping them.
It cannot distinguish table-ordered member allocation from sorting members during entity creation.

`python scripts/re/gameplay-capture/pack_models.py Tools/re/out/strand-pack-models.json` compares the opening
pack tables under current independent weighted draws and an unverified independent proportional-rounding model.
It enumerates outcomes with exact rational probabilities, then reports member counts and duplicate-variety chances.
Both assume uniform unscaled MinCount/MaxCount draws. They omit placement, rarity and observation loss;
the report is a prediction to compare with evidence, not a recovered allocation formula or a gameplay setting.

`python scripts/re/gameplay-capture/wake.py Tools/re/out/gameplay-captures/<timestamp>` writes `wake.json`.
It pairs an initially decoded hidden buff's removal with the actor's subsequent Emerge action, retaining stream,
creation/removal/action records and positions. Player-action origins before and after removal are reported with
their time offsets and distances; destinations are never substituted for actual positions or interpolated.
Player identity uses a captured local-object header, or one unambiguous actor with self-action headers.
Unpaired events and ambiguous identities stay visible. The report does not infer exact server wake timing,
a universal radius, or pack membership; missing initial creations can prevent pairing.

## Read-only terrain inputs

`scripts/re/frida/trace-room-point-state.js` observes the prepared client's generation return without changing game
state. It records the seed, authored native type-0/type-3 point counts, cached WG flags and allocated room state.
It is self-contained: load it before an area load with the existing Frida runner. These point counts are not ordinary
monster totals and do not recover the official server's allocation or population RNG.

`python scripts/re/levelgen/room_flag_reads.py` reports offset-matched suppression-test candidates in the pinned
terrain function ranges, including straight-line register copies. It does not modify the level generator.
Copied records, shifted flags, cross-block flow and server-only code remain outside coverage; a missing candidate
is not proof that the corresponding rule is absent.
