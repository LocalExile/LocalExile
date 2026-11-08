# Address index (3.29.3c `Game.exe`, image base 0x140000000)

Every address used in the protocol docs, grouped by subsystem. Fn = function, Vt = vtable, Cls = message class vtable,
G = global / static data.

## Network core
| Address | Kind | What |
|---|---|---|
| `0x140df9600` | Fn | stream (socket) ctor, login |
| `0x140df9810` | Fn | stream ctor from a connected socket (instance) |
| `0x140df9ed0` | Fn | stream read (sets `+0x252` when short) |
| `0x140dfa000` | Fn | stream write |
| `0x140dfa160` | Fn | flush / send (cipher slot 1) |
| `0x140df9c70` | Fn | receive (cipher slot 2) |
| `0x140dfa6d0` | Fn | install cipher pair (`+0x1b8`), decrypt buffered bytes |
| `0x140e00a10` | Fn | crypto object ctor (DH groups, "Rhoa") |
| `0x140e022d0` | Fn | Salsa20 pair (km, isClient) |
| `0x140e030e0` | Fn | Salsa20 object |
| `0x140e0b250` / `0x140e0b480` | Fn | wstr write / read |
| `0x141fab040` / `0x141faaf20` | Fn | blob16 read / write |
| `0x141faa680` / `0x141e655b0` | Fn | compact int write / read |
| `0x141faa790` | Fn | signed prefix-length compact reader used by synthetic action aim fields; not zigzag or LEB128 |
| `0x14089f790` / `0x14089dc40` | Fn | compact int write / read (stream) |
| `0x1429a77a0` | Fn | message pump: read id, offer to groups |
| `0x1429a7710` | Fn | register handler group |
| `0x1429a7660` | Fn | generic group handle slot (factory → read → owner) |

## Login
| Address | Kind | What |
|---|---|---|
| `0x142144f60` | Fn | login connect, sends 0x0002 |
| `0x142145ae0` | Fn | login S→C dispatch (switch on id) |
| `0x1421a2cc0` | Fn | login S→C factory |
| `0x140e01ed0` / `0x140e01d00` | Fn | verify server key / DH2 agree + SHA-512 |
| `0x140d060a0` | Fn | account login sender (0x03, password digest) |
| `0x140d13090` | Fn | character screen → 0x18 |
| `0x142147040` | Fn | Play → 0x0e |
| `0x142144ef0` | Fn | login connection dropped: login `+0xde8` (0 none, 2 handshake, 3 logged in) back to 0 |
| `0x1400f2890` | Fn | state stack singleton; states by id byte (`+0x209`) at `+0x48 + id×0x10` ([09](09-area-change.md) §6) |
| `0x140104860` | Fn | builds every state (login id 7 `0x140d0c8f0`, character select id 9 `0x140d12170`, in game id 4 `0x140d53100`) |
| `0x140d0d1c0` | Fn | login state enter: character select if login `+0xde8` = 3 |
| `0x140d54a00` | Fn | in-game frame update → `0x140d551f0` (instance down: login state, login object kept) or `0x140d54c50` |
| `0x140cf1a20` | Fn | Esc → "Exit to Character Selection": C→S 0x153 |
| `0x140cfc290` | Fn | gateway_list.txt reader |
| `0x142fca1f0` / `0x142fca540` / `0x142fca6c0` | G | DSA key / client static private / server static public |

## Instance client
| Address | Kind | What |
|---|---|---|
| `0x1415a7310` | Fn | creates the instance client (0xe208 bytes) |
| `0x14214b4a0` | Fn | instance client ctor |
| `0x14346c648` / `0x14346c6c0` | Vt | instance client / its `+8` sub-object |
| `0x14214f1d0` | Fn | tick (connect job, 0x7d, pump, 0x1b, 0x31, 0xc7) |
| `0x14250cfd0` → `0x14250d080` → `0x14250d160` → `0x14250d290` | Fn | connect job → thread → happy-eyeballs connect |
| `0x14214ee50` | Fn | send opening 0x0F + 0x0E, state 2 |
| `0x1421752b0` | Fn | send message (vtable slot 1 on the stream) |
| `0x141fe7d90` | Fn | digest used by 0x0E |
| `0x142fc9c38` | G | 8-byte constant in the 0x0E digest |
| `0x142150ae0` | Fn | instance S→C dispatcher |
| `0x14215b764` | G | dispatcher byte table (`id − 0x0d` → case) |
| `0x1421bc5b0` | Fn | instance S→C factory |
| `0x1435e4808` | Vt | instance handler group |
| `0x142179f80` / `0x1429a7000` → `0x1429a6ec0` | Fn | 0x11: main cipher / side-channel cipher |
| `0x142feaed0` | G | 64-byte XOR mask for the side-channel key |
| `0x1421505f0` | Fn | disconnect (state 1) |
| `0x1421691e0`, `0x142168fe0` | Fn | other state writers |

## Area and world
| Address | Kind | What |
|---|---|---|
| `0x1435e4c20` (ctor `0x1421a42b0`, write `0x142147f10`, read `0x142148840`) | Cls | 0x1d area |
| `0x14215bc60`, `0x140e33630` | Fn | queue area generation |
| `0x1435ca2c0` | Cls | C→S 0x7d (tile hash, doodad hash) |
| `0x1435cad90` | Cls | C→S 0x1b tick |
| `0x1431767e8` | Cls | u32 message (login 0x1f/0x20, instance 0x0F, 0x22) |
| `0x1431768d8` | Cls | empty message (login 0x01, instance 0x11) |
| `0x14218d910` | Fn | set local player id (`world+0x980`) |
| `0x1435e65c8` (ctor `0x1421a96b0`, write `0x1421a9710`, read `0x1421a9950`) | Cls | 0x21 enter area |
| `0x141d506b0` | Fn | 0x21 Record68 writer |
| `0x14215d6d0` | Fn | enter area: activate world, bind player, state 3 |
| `0x1400f2740` / `0x14013f510` | Fn | world's first local entity / its "Player" component |
| `0x1435e4d80` | Vt | world handler group |
| `0x1421ccac0` | Fn | world S→C factory |
| `0x14218ddf0` | Fn | world message handler (0x2bb create, …) |
| `0x14218f630` | Fn | registers the world group on the instance pump |
| `0x142189fe0` | Fn | world activation (world vtable +0x1c8): replay queue |
| `0x141d87640` → `0x141d7fb50` | Fn | spatial index insert (23×23 buckets, no bounds check) |

## Objects and components
| Address | Kind | What |
|---|---|---|
| `0x141eebd10`, `0x141eea7d0` | Fn | GameObjectTypeRegister factory / resource cache |
| `0x141ec8170` | Fn | build the register from dat tables |
| `0x1424fe0d0` / `0x1424fe270` / `0x141674420` | Fn | register add / insert / lookup |
| `0x14247b370` → `0x140df6e10` | Fn | narrow path → MurmurHash2 |
| `0x1435e81f8` (write `0x141fe6cf0`, read `0x141fe6d40`) | Cls | 0x2bb create object |
| `0x141f98850` / `0x141f988f0` | Fn | entity header write / read |
| `0x141ec4a40` | Fn | create object from blob |
| `0x141ec47e0` | Fn | create a client-side object (id from the world): the client's own predicted projectiles ([11](11-skills.md#10-projectiles-experimental)) |
| `0x141dedf80` / `0x141ee6950` | Fn | blob base data / tasks |
| `0x14242dfe0` | Fn | build components from the type |
| `0x14247c6a0`, `0x14247cdd0`, `0x14247d050` | Fn | `.ot` parser, extends, sections |
| `0x140e272d0` | Fn | blob reader out-of-range (throws `0x143a5c5c0`) |
| `0x1435ab608` / `0x141e323f0` | Vt / Fn | Positioned / deserializer |
| `0x1435e7f88` / `0x1421c98b0` | Vt / Fn | comp1 (stats-like) |
| `0x1435a7a20` / `0x141e14b30` | Vt / Fn | comp3 |
| `0x1435b0d98` / `0x141e72890` (pool `0x141e72410`, flags `0x141e72810`) | Vt / Fn | Life |
| `0x141e28b50` / `0x141e28ca5` | Fn / Insn | Life type constructor / default `+e4..e7 = 00 00 00 01` (corpse usable true) |
| `0x141e6dcc0` / `0x1435b11e0` | Fn / Str | Life boolean parser / `corpse_usable` → config `+e7` |
| `0x141e6d460` / `0x141e6d526` | Fn / Insn | Life type finalizer / sets `+e6` corpse-use veto when `Player` exists |
| `0x141e6d6b0` / `0x141e6de70` | Fn | Life factory / runtime constructor copies corpse and sinking config to `+26a/+26b` |
| `0x141e72bb0` | Fn | Corpse predicate: usability, Player veto, life, spectre restriction and stat-9525 fallback |
| `0x1435af9c0` / `0x141e5afd0` | Vt / Fn | Animated |
| `0x143470e78` / `0x141e84300` | Vt / Fn | Player (xp setter `0x141e85da0`) |
| `0x1435a6cb8` / `0x1415daf60` → `0x141e22490` | Vt / Fn | Inventories component |
| `0x14347a818` / `0x1421993f0`, slot 20 `0x142199c20` | Vt / Fn | comp9 |
| `0x1435b3800`, `0x1435aa590`, `0x1434730b8`, `0x143476978` | Vt | Pathfinding, comp7, comp10, comp11 |
| `0x1400f1180` | Fn | empty `ret` (no-op slot) |
| `0x141e42a90` | Fn | StateMachine update reader (vtable `0x1435e5358` / `0x1435ab870` +0xa0, S→C 0x2bc): the creation blob's states and timers again; changed values run their events (boss life bar, [12](12-quests.md) §6) |
| `0x141e45a00` | Fn | Transitionable component constructor: state `+0x120` = 1, `+0x121` = the type's `transition_on_damage_taken` |
| `0x141e46070` | Fn | Transitionable .ot keys: `num_states` (2..127, else "That number of states is unsupported"), `transition_on_damage_taken` |
| `0x141e46340` | Fn | Transitionable type constructor (vtable `0x1435aa878`): `num_states` (`+0xb0`) defaults to **2** ([10](10-chat-and-commands.md) §5, emerge monsters) |
| `0x141d8ad70` | Fn | TriggerableBlockage: blocks the cells of the Animated model's footprint at the object's grid position ([12](12-quests.md) §6) |
| `0x1421c8c80` | Fn | Transitionable update reader (vtable `0x1435e7d30` +0xa0, S→C 0x2bc): u8 target state, steps toward it ([12](12-quests.md) §6) |
| `0x141e47880` | Fn | TriggerableBlockage reader: u8 blocked, then only while blocked u8 count + count × u8 |
| `0x141fe7cd0` | Fn | S→C 0x1c6 reader: TriggerableBlockage's own update (it has no 0x2bc reader); u8 0 → +0xb8 `0x141e47150` (unblock), else +0xb0 with the sections ([labyrinth-trials](../game/labyrinth-trials.md)) |
| `0x141e467f0` | Fn | TriggerableBlockage .ot keys: `start_blocked` +0x10, `disable_save` +0x11, `shunt_objects_away` +0x12 (`save_as_diff` not kept) |
| `0x141d8bd20` | Fn | stamps objects' Hulls (`0x141d8ad70`): some types always, the others only while their TriggerableBlockage is blocked |
| `0x141ee7400` | Fn | blob task lookup by u16 task type (registry), used by the tasks reader `0x141ee6950` |
| `0x142f536a0` | G | static initializer table entry (Positioned) |
| `0x1445c63b0`, `0x1445c6860`, `0x1445c6fc0`, `0x1445c6ed0`, `0x1445c68d0`, `0x1445c6e80`, `0x1445c59c0`, `0x1445c7780` | G | component pools (Positioned, Stats, Pathfinding, Life, Animated, Player, Inventories, Actor) |

### Player Actor scale ([04](04-player-entity.md#41a-player-actor-scale-299))

| Address | Kind | What |
|---|---|---|
| `0x1421c98b0` | Fn | Creation Stats pairs reach the network setter; trailing LE u32/f32/u8 loads target entropy/timer/reset |
| `0x1424d6510` | Fn | Network stat override getter; upstream fallback only when absent |
| `0x1424d65c0` | Fn | Insert/replace one raw pair, notifying only that key |
| `0x1424d67d0` | Fn | Stat change notification to registered listeners |
| `0x141fe7b70` | Fn | 0x1b3 reader; world handler `0x14218ddf0` uses the same setter |
| `0x1400cb050` | Fn | Register final1973 evaluator `0x1427f37e0` |
| `0x1427f37e0` | Fn | final1973 formula: raw720, capped1974, permyriad10778, optional limit1975 |
| `0x141ea32d0` | Fn | Actor setup reads1973 at `0x141ea35ad` and applies scale |
| `0x141ea3840` | Fn | Actor change observer compares key1973 at `0x141ea38e1` |
| `0x141ea3730` | Fn | Scale applicator writes Positioned +0x2b0 at `0x141ea381f` |
| `0x14013dd20` | Fn | Monster-specific optional scale coefficient lookup |
| `0x1415f6850` | Fn | Render typeScale * uniform * independent stretch; interpolation at +0x358 |
| `0x1415f91e0` | Fn | Render cached scale axes at +0x380/+0x384/+0x388 |
| `0x141e394f0` | Fn | Local-to-world transform uses the same scale axes |
| `0x141c9e4e5` | Code | Native melee range scales only when separate flag9807 is set |
| `0x141c9e59a` | Code | Second native melee range consumer with the same flag9807 guard |
| `0x141e65ae0` | Fn | Blocking uses signed Positioned size +0x1e3, independently of uniform scale |

## Chat ([10](10-chat-and-commands.md) §1, §2)
| Address | Kind | What |
|---|---|---|
| `0x142164740` | Fn | send a typed line (instance, text, linked items): client slash commands, else C→S 0x15 |
| `0x142162d20` | Fn | the client's slash commands (Commands.dat); `/global N`, `/trade N` (row + 1 = 0x2a, 0x2b) → `0x14216fc00` |
| `0x14216fc00` | Fn | join a chat channel: C→S 0x134, 5 s deadline (instance +0xcf08 trade, +0xcf18 global), listeners +0xdf8 |
| `0x1435ca148` / `0x141fa1860` | Cls / Fn | C→S 0x134 class / write |
| `0x1435e7160` / `0x1421ad640` | Cls / Fn | S→C 0x135 class / read (ctor `0x1421ad570`, dispatcher case 0x56 at `0x142155b43`) |
| `0x140d59480` | Fn | in-game listener (instance +0x240): joins global and trade after entering an area (once, flag +0xa08) |
| `0x141914fe0` / `0x1419150b0` / `0x141917e50` | Fn | settings `join_global_channel` / `join_trade_channel` / chat language |
| `0x143a6bce8` / `0x143a6bc98` | G | remembered global / trade channel (0xffff until `/global N`, `/trade N`) |
| `0x14214f1d0` | Fn | instance network update: a passed join deadline fires the instance +0xe78 listeners |
| `0x140178dd0` | Fn | chat panel attaches to the instance: listeners +0xb78 (0x17 line), +0xd78, +0xe78 (join timeout) |
| `0x14017e0b0` | Fn | chat panel: "Timeout while joining {0} chat channel {1}" (ClientStrings rows 207 / 206) |
| `0x140195e00` | Fn | chat panel channel popup ("Joining {0} channel …", "Connected to {0} channel …") |
| `0x1422e2950` / `0x1422045d0` | Fn | dat row by index / ClientStrings text by id (row + 1) |
| `0x1435cac90` / `0x141f9bc30` | Cls / Fn | C→S 0x99 class / write (4 bytes) |

## Monsters ([10](10-chat-and-commands.md) §5)
| Address | Kind | What |
|---|---|---|
| `0x1435e7200` (read `0x1421ab8a0`, write `0x1421ab7e0`) | Cls | S→C 0x20 monster types at a level (dispatcher case 9) |
| `0x141ec8df0` / `0x141ec9190` | Fn | register `"<variety Id>@<level>"` / the plain variety Id |
| `0x14228f620` | Fn | MonsterVarieties row by index (0x31a-byte rows) |
| `0x1435b33d8` / `0x141e76390` | Vt / Fn | Monster component (slot 17 `ret`) / its factory ("Creating monster with no level") |
| `0x1435b31a8`, `0x141e76860` | Vt, Fn | Monster component type; slot 9 parses the `@level` suffix |
| `0x141e30ba0` | Fn | Positioned .ot keys (`serialise_collidable` → +0x1b, …) |
| `0x14161d160` (3.29.3b; not found in 3.29.3c) | Fn | Targetable deleter (where a failed object build crashes) |

## Patching
| Address | Kind | What |
|---|---|---|
| `0x1428f3c7e` | call site | "No patching required" decision |
| `0x1421d3460` | Fn | local GGPK root hash is non-zero |
| `0x1428f31c0` | Fn | compare a local directory hash with the welcome |

## Inventories and UI
| Address | Kind | What |
|---|---|---|
| `0x1421713e0` | Fn | inventory manager by selector |
| `0x141d65c80` | Fn | register inventory (row → id, id → inventory) |
| `0x141d65830` | Fn | inventory lookup by id (409 call sites) |
| `0x1431b4368` (ctor `0x140b17880`, write `0x140b178b0`, read `0x140b17970`) | Cls | 0xc0 add inventory |
| `0x1421fb3d0` / `0x1421fb650` / `0x1421f45a0` | Fn | inventory object write / read / ctor |
| `0x1421fb910`, `0x1421f2b40` | Fn | inventory items read / item write |
| `0x14217a930`, `0x14217ae70` | Fn | 0xc3 sockets/gems, 0xc4 list |
| `0x14029e550` → `0x1402bb540` → `0x1402d0a90` | Fn | flask frame UI (needs row 11) |
| `0x14098c66c` | call site | UI lookup of row 0x54 (expanded main inventory) |
| `0x1435e6708` (ctor `0x1421a5480`, write `0x1421a54b0`) | Cls | 0x3d Atlas passive tree presets |
| `0x1435e7940` (read `0x1421a4d60`, handler `0x141ef8f70`, numbers only `0x141efdcb0`) | Cls | S→C 0x33 character Passive Skill Tree (instance `+0xc2d8`) |
| `0x141efa6c0` | Fn | character passive tree check: points_from_level, skill_points_from_passives, extra_skill_points |
| `0x1403cc9a0` | Fn | passive tree click → C→S 0x34 allocate / 0x36 refund (Atlas: 0x3e / 0x3f) |
| `0x1435ca028` (write `0x141f993f0`) | Cls | C→S 0x36 refund a passive (u32 node, u8 expansion, u8 gold respec) |
| `0x141f019b0` | Fn | a passive's gold respec cost: VillageBalancePerLevelShared GoldRespecCost × multiplier |
| `0x140a11cdb`, `0x1409db727` | Fn | "Refund All" → C→S 0x37 (id only) |
| `0x141ef3860` | Fn | Atlas passive tree check |
| `0x1435e84f8` (read `0x141fe6090`), `0x1435e8418` (read `0x141fe65f0`) | Cls | S→C 0x1c1 add buff, 0x1c2 remove buff |
| `0x141e14d80` | Fn | Buffs component: 0x1c1/0x1c2 handler (world handler cases 0x1c1, 0x1c2) |
| `0x141e15610` | Fn | Removes an installed Buffs pointer and marks instance +0xd0 bit 0x10 inactive; instance ID is +0x9c. Native hybrid flask removal verified ([13](13-items.md) §8). |
| `0x14208e210` | Fn | effect script: plays a MiscAnimated as a `Metadata/Effects/*Effect` object |
| `0x1400bad30` | Fn | registers the derived stat active_skill_area_of_effect_radius (from the base radius and area %) |
| `0x14215c540` | Fn | create passive slot 0/1/2 (`instance+0xc2f8`) |
| `0x140dcb2d0` | Fn | S→C 0x7b kind 9 (instance +0x19f8): shows the Trial Plaque window (live HUD +0x7e8, built by `0x140b71170`) ([labyrinth-trials](../game/labyrinth-trials.md)) |
| `0x141f6fb00` | Fn | a Labyrinths row's trial done: LabyrinthTrials column 7 quest flag or column 1 league quest flag |
| `0x14078a490` | Fn | UI reading the active passive slot |

## Items ([13](13-items.md))
| Address | Kind | What |
|---|---|---|
| `0x1421f2b40` / `0x1421f2980` | Fn | item write (u16 length + data) / data |
| `0x1421f2c30` / `0x1421f2d00` | Fn | item read (length) / data: type hash → ItemTypeRegister, flags, components (slot 5) |
| `0x141fabcc0` | Fn | ItemTypeRegister lookup by HASH32 |
| `0x1421f2800` | Fn | the four type hashes with a trailing u16 |
| `0x1421ed330`, `0x1421e6050`, `0x1421e9790`, `0x1424b5720`, `0x1424a3f80`, `0x1423fb5f0`, `0x1424755d0` | Fn | item component reads: Base, Mods, Sockets, Quality, Charges, Stack, SkillGem |
| `0x1421edee0`, `0x1421e7b00` | Fn | debug dumps of the Base and Mods components: each field's name ([13](13-items.md) §2f) |
| `0x1415726d0` | Fn | an item's render flags: Base +0xc6 influences, Mods relic, synthetic, fractured ([13](13-items.md) §2f) |
| `0x1424e6250` | Fn | a mod line's display kind: crafted, fractured, mutated, divergent |
| `0x142208700` | Fn | ObjectMagicProperties read: rarity, identified, mod lists, name ([13](13-items.md) §2a) |
| `0x142436e50` / `0x1424371e0` | Fn | one mod read (u16 Mods.dat HASH16 + a compact per stat) / the mod's n-th stat |
| `0x142205d50` → `0x142206280` | Fn | after the read: rarity tag, apply each mod list with its kind |
| `0x1421fc010` | Fn | inventory item entry read (u32 id, u8 x, u8 y, item) |
| `0x1435e6c28` (ctor `0x1421aa120`, write `0x1421aa160`, read `0x1421aa1f0`) | Cls | S→C 0xbf inventory change (`0x14250e550` / `0x14250e7a0`) |
| `0x141d65be0` → `0x1421fbaf0` | Fn | 0xbf handler → apply removals, additions, revision |
| `0x1435e78e0` (read `0x1421b4840`, write `0x1421b4760`) | Cls | S→C 0x263 item-use result; successful flask use triggers the drinking sound ([13](13-items.md) §8) |
| `0x14215a26b` → `0x142178200` | Fn | 0x263 dispatch (case 0xc6) → instance +0x3c78 item-use listeners |
| `0x140d931e0` / `0x140dda620` → `0x140ff86e0` | Fn | item-use UI callbacks → native audio submission (BaseItemTypes use sound when override is empty) |
| `0x1435cb2d0`, `0x1435e7560` | Cls | S→C 0xc1 remove inventory, 0xc2 inventory permissions |
| `0x1435ca008` (write `0x141fa7730`, sender `0x142165c70`) | Cls | C→S 0x28 pick up item |
| `0x1435ca128` (write `0x141f967a0`, sender `0x142165d70`) | Cls | C→S 0x2a place held item |
| `0x1435cb270` (write `0x141f96f40`) | Cls | C→S 0x30 socket the held gem |
| `0x140117ee0` | Fn | drop held item: 0x29 outside town, Keep / Destroy popup in town |
| `0x1435ca938` (write `0x141f97d70`, sender `0x142162d20`) | Cls | C→S 0x16 Commands.dat command (Destroy = 9) |
| `0x1435cacf0` (write `0x141f96c30`) | Cls | C→S 0x2d (not mapped) |
| `0x1435ca0e8` (write `0x141fa7300`, sender `0x142165f30`) | Cls | C→S 0x4a held item onto an item ([13](13-items.md) §4a) |
| `0x1435ca810` (write `0x141f97160`, sender `0x14216bff0`) | Cls | C→S 0x7c split a stack ([13](13-items.md) §4a) |
| `0x141ea7950` | Fn | WorldItem component read |
| `0x141e22490` → `0x1424fb090` | Fn | player Inventories component: visible equipment entries |
| `0x1402ceb80` | Fn | item attribute requirement check (virtual_*_for_requirements) |
| `0x1424ad860` | Fn | an item's requirement for one attribute (type value, flat and % item stats) ([13](13-items.md) §2d) |
| `0x1421e6d40` | Fn | an item's level requirement → Mods +0x2bc (type level, mod level × 4 / 5, override, +); not run for network items, which keep the sent byte ([13](13-items.md) §2d) |
| `0x1421f33c0` | Fn | can the player use this item: level (Mods +0x2bc vs Player +0x1ac), gem levels, attributes |
| `0x141e86a30`, `0x141e869c0` | Fn | Player caches: requirement attributes; UI attributes |
| `0x1424b6a90` / `0x1424b6a30` | Fn | Armour component raw base-roll byte reader / writer; 32 is a minimum flag |
| `0x1424b6760` | Fn | Armour roll setter: ordinary 0..31 at +0x28; special flag at +0x2a |
| `0x1424b6360`, `0x1424b63e0`, `0x1424b6460` | Fn | Base armour/evasion/ES: interpolate over 31, then round |
| `0x1424b5de0`, `0x1424b5c70`, `0x1424b5f00` | Fn | Final armour/evasion/ES: rounded base, locals, quality, round |
| `0x1424b65e0`, `0x1424b6660`, `0x1424b66e0` | Fn | Local increased ES/evasion/armour helpers |
| `0x14252a660` | Fn | ES-to-Life: full flag wins; truncate final local ES per active armour slot |
| `0x1428bb9f0` | Fn | Effective shield ES: suppressed for ES-to-Life conversion or shield transfer/prohibition |
| `0x14272b800` | Fn | Combined shield defence increase: generic stat 24 plus the Devotion-dependent term |
| `0x142736230` | Fn | Shield armour increase: global armour plus shield-only armour and combined shield defences |
| `0x14272e980` | Fn | Shield evasion increase: global evasion plus shield-only evasion and combined shield defences |
| `0x142536250` | Fn | Shield ES increase: global ES plus shield-only ES and combined shield defences |
| `0x1427387ac` | Code | IR shield evasion uses shield armour-only plus shield evasion/shared increases once |
| `0x142739a97` / `0x14273114b` | Code | Final AR/EV: truncate after adding 0.5 to the summed positive contributions |
| `0x142533297` / `0x14251a3c0` | Code / Fn | Final ES calls signed half-away rounding after summing sources |
| `0x1425332e0` / `0x142534028` / `0x1425340e3` | Fn / Code | Flat ES getter reads key 135 and adds global flat ES |
| `0x1405be330` / `0x1406654d0` | Fn | Linear interpolation / half-up rounding used by item defences |
| `0x140e18ad0` | Fn | Item's Armour component getter |
| `0x140030c10` / `0x143466fd0` | Fn/Cls | Register Sacred Orb action / validator vtable |
| `0x141f89920` | Fn | Sacred Orb target gate: Armour, Mods, no mirror, at least one variable native range |
| `0x1402f3c50` | Fn | item's AttributeRequirements component getter |
| `0x141e21340` | Fn | InteractionAction type parser (`interaction_distance_override` → +0x1bb) |

## NPCs ([17](17-npcs.md))
| Address | Kind | What |
|---|---|---|
| `0x141e7b6e0` | Fn | NPC component reader (slot 17): u8 → +0x20, u8 → +0x21 |
| `0x141e7b860` | Fn | NPC component debug dump ("NPCTalk: (") |
| `0x141e7d430` | Fn | the NPCTalk row the client picks for the player |
| `0x1435e84d8` (read `0x141fe44d0`) | Cls | S→C 0x84 NPC dialogue (object header, u8 → +0x20), world handler `0x14218ddf0` |
| `0x142150700` | Fn | 0x84's handler: object 0 closes; u8 0xff fires the listeners at instance +0x13f8 |
| `0x140dc9fb0` | Fn | in-game UI: open the NPC dialogue (registered on instance +0x13f8 by `0x140dba5d0`) |
| `0x1407253a0` | Fn | NPCDialogueWindow setup (dialogue style; via `0x140720d10`) |

## Waypoints and the World panel ([16](16-waypoints.md))
| Address | Kind | What |
|---|---|---|
| `0x1435e76c0` (read `0x1421a5dd0`) | Cls | S→C 0x4c activated waypoints (u32, 19-byte bitset → instance +0xcf31), dispatcher case 0x12 |
| `0x1435e71a0` (read `0x1421a6dc0`) | Cls | S→C 0x7b open a UI (u8 kind, 4 × u32), dispatcher case 0x1c |
| `0x1435cb2f0` (write `0x141f99750`) | Cls | C→S 0x4d waypoint travel (u16 area, u32, u8 Ctrl) |
| `0x142169dd0` | Fn | 0x7b's switch over UI kinds; kind 0 → listeners at instance +0xff8 |
| `0x140dcae00` | Fn | in-game UI: open the World panel in travel mode (`0x140aafe50` → `0x140aa2b10`, panel +0x522) |
| `0x140aa6d60` | Fn | World panel pin click → C→S 0x4d |
| `0x141fe37b0` | Fn | is an area's waypoint bit set (bit = index among HasWaypoint rows with +0x1e4 in {1, 2, 5, 11}) |
| `0x14214b340` | Fn | waypoint look of the current area (0 idle, 1 inactive, 2 activation), from 0x4c |
| `0x140aaed40` | Fn | pin widget update: revealed (+0x329) and waypoint (+0x32a) states |
| `0x140ab4c10` | Fn | pin revealed: any MapPins `QuestFlags` set |
| `0x140ab49d0` | Fn | pin as a waypoint: bit set and area accessible |
| `0x14077ecf0` | Fn | area accessible: all WorldAreas +0x54 quest flags set (Player component) |
| `0x140aa5fb0` / `0x140aa6270` | Fn | connection lines: only between two shown pins |
| `0x141e89be0` | Fn | flag set reader (u16 n × (u8 block, u64 bits)); Player +0x1b8 quest flags, +0x1d0 |
| `0x14012ae40` | Fn | flag set test `(set, bit)` |
| `0x141e85520` | Fn | Player component update reader (0x2bc): u16 flags, 0x02 quest flags, 0x08 league quest flags, 0x10 Hideouts keys, 0x200 9 bytes, 0x01 experience |
| `0x141e1fbb0` | Fn | HideoutDoodad reader: u32 MTX hash, u8 variation, u8 flag, only when the object's data +0x14e is set (in hideouts) |

## Hideouts ([21](21-hideouts.md))
| Address | Kind | What |
|---|---|---|
| `0x141e84300` / `0x141e85520` | Fn | Player reader / update: the two Hideouts keys (update bit 0x10, then vtable +0x130) |
| `0x142fcb390` | Data | league quest flags: 0x776 sorted {UTF-16 name, length, u32 id} |
| `0x1423f8a70` / `0x141deb5c0` | Fn | league quest flag name → id (0x777 unknown) |
| `0x14155f120` | Fn | league flag test on Player +0x1d0 with bit id − 1 |
| `0x141e6bb03` | Call | the map device's Targetable test of `UnlockedMapDevice` (bit 349) |
| `0x1421731f0` | Fn | HUD hideout panel check: quest flag HASH32 0x2f1b9c25 in the Player component |
| `0x1401ff540` | Fn | hideout panel shown or hidden by `0x1421731f0` |
| `0x1401fcc30` | Fn | hideout panel (palette) constructor |
| `0x142169dd0` case 5 | Fn | S→C 0x7b kind 5 → listeners at instance +0x1778 → `0x140dcb230` |
| `0x142169dd0` case 0x2b | Fn | S→C 0x7b kind 0x2b (HideoutMapDeviceScreen) → listeners at instance +0x2278; opens the Atlas map device |
| `0x00e7` | C→S | Atlas map node picked: u16 BE WorldAreas HASH16, u8 0x0d (21-hideouts.md §4) |
| `0x142157401` (case 0x7f) / `0x1421576ac` (case 0x80) | Fn | S→C 0x1a3 Atlas progress masks / 0x1a4 one completed node → instance +0xd030, +0xd070, +0xcff0 (21-hideouts.md §4) |
| `0x14215ca00` | Fn | 0x1a3 row mask (22 bytes, rows below 170) → an AtlasNode set |
| `0x14077bc30` | Fn | AtlasNode set lookup (instance +0xcff0 / +0xd030 / +0xd070) |
| `0x14078c0c0` | Fn | Atlas node state updater: completed (+0x9cb/+0x9ce) when the node is in instance +0xd070 |
| `0x140dcb230` / `0x140dcba80` / `0x140dcbb20` | Fn | open the map device window (modes 0xd, 0x5e, 0x56; lists +0x1778, +0x17f8, +0x1878) |
| `0x140c284f0` | Fn | map device window: Activate button (mode 0xd → `0x14216ce30`, mode 0x5e → C→S 0x297) |
| `0x14216ce30` | Fn | C→S 0x00d8 Activate: u16 (target +0x7b), u8 (writer class `0x1435ca958`) |
| `0x1435e6648` (read `0x1421a7370`, write `0x1421a7210`) | Cls | S→C 0x008f the map device's portals (dispatcher case 0x21) |
| `0x14218c9e0` | Fn | 0x008f handler: per portal PortalClient +0xb8, `on_map_portal_opened` / `on_map_portal_closed` |
| `0x1421c0460` | Fn | 0x008f portal list read (u32 object, u16 area, then u16, u8, u8, u8, wstr when the area is set) |
| `0x14161c0e0` | Fn | PortalClient vtable +0xb8: set the portal's area, model (`0x141e30580`) and rendering |
| `0x141e30220` / `0x141e30280` | Fn | Portal component read (u8) / set the multiplex portal type (the "portal" state) |
| `0x1423fa9d0` / `0x1423fa920` | Fn | item Map component read / write (u8, u8, u8 flag [u32]) |
| `0x140c259f0` | Fn | map device window constructor (HUD +0x7d8) |
| `0x140dba5d0` | Fn | HUD: registers its 0x7b listeners on the instance lists |

## Stash ([18](18-stash.md))
| Address | Kind | What |
|---|---|---|
| `0x142173910` | Fn | stash list by kind: instance +0xc498 personal, +0xc4b0 guild, +0xc4c8 trade (0x68-byte records) |
| `0x1421c0ac0` / `0x141d50880` | Fn | tab list read / tab record read |
| `0x1421a9cc0` | Fn | S→C 0x136 read (one stash list, case 0x57) |
| `0x1421aaf20` | Fn | S→C 0xc8 read (tab loaded, case 0x3b) |
| `0x140dcae70` / `0x140dcb010` | Fn | 0x7b kind 1 / 2 listener: open the stash / guild stash, select a tab by affinity |
| `0x14216d9c0` | Fn | queue C→S 0xc7 (tab contents request); sent by the tick `0x14214f1d0` |
| `0x141f9c9d0` | Fn | C→S 0xc7 write (vtable `0x1435ca958`) |
| `0x14216daa0` / `0x141f9cb60` | Fn | C→S 0xcc tab order: build / write |
| `0x14216dd20` / `0x141f9d680` | Fn | C→S 0xd1 tab into folder: build / write |
| `0x140a87e90` | Fn | stash panel: build the tab buttons from the list |
| `0x140a86290` | Fn | stash panel: tab selected (request or build its page) |
| `0x140a8d810` | Fn | stash panel: refresh on opening (requests the current tab) |
| `0x14025b9b0` | Fn | tab settings: rename / recolour |
| `0x14216f460` / `0x141f9df90` | Fn | C→S 0xe0 (Ctrl + click into a tab): build / write |
| `0x14216f7d0` / `0x141f9e870` | Fn | C→S 0xe3 withdrawal: build / write (kind, raw token, bulk boolean) |
| `0x140312da0` / `0x1403119d0` | Fn | Shared stash withdrawal click handler / bulk click entry; forwards bulk 1 to 0xe3 |
| `0x140dc2640` | Fn | show / hide a panel (a second 0x7b kind 1 would close the stash) |

## Strings worth searching
"Connect time to instance server was", "Client-Safe Instance ID = ", "Generating level ", "Instance sent world area
that does not exist with hash: ", "Area reported by instance does not exist", "Unable to deserialise packet with pid ",
"Unknown object type serialized by server", "Unrecognised task serialised by server; missing registration?",
"Metadata/GameObjectRegister", "Game::GameObjectTypeRegister", "Art/2DArt/UIImages/InGame/Azmeri/SapFlaskFrame".

## Latency, HNC, wake zone, movement ([07](07-latency-visibility-movement.md))

| Address | Kind | What |
|---|---|---|
| `0x14215c280` | Fn | 0x1c: drop acknowledged entries from the send-time queue (`+0xc458`); index ≥ size clears it and returns `+0xc470` (latest tick time) |
| `0x14215222c` | Code | 0x1c handler after the pop: rtt − server delay clamps the latency graph value (`+0xc490`) |
| `0x140d551f0` | Fn | per-frame perf stats: feeds `+0xc490` to the latency graph |
| `0x141fa7150` | Fn | 0x1c read (u16, u16, u32 HNC carrier) |
| `0x1429a71b0` | Fn | HNC byte state machine ("HNC Protocol Violated") |
| `0x1429a6ec0` | Fn | HNC key: km XOR `0x142feaed0`, byte swaps |
| `0x142feaed0` | Data | HNC 64-byte mask |
| `0x141ec7350` | Fn | ObjectInWakeZone (0x1a9) |
| `0x14219aa10` | Fn | Actor message handler (0x1ab action started, …) |
| `0x141c096ba`..`0x141c0971e` | Code | Attack timing reads total base ms 0x221a and final player speed 0x126 |
| `0x14270d118`..`0x14270d13f` | Code | Explicit total base attack duration getter returns stored 0x221a |
| `0x141e33fd0` | Fn | Positioned message handler (0x1b7 teleport, 0x1ba move, …) |
| `0x14216ccb0` | Fn | C→S 0x25 / 0x24 sender (use skill at location / on object) |
| `0x141fe4290` | Fn | 0x1ab read |
| `0x141faa680` / `0x141faa790` | Fn | compact int write / read |
| `0x141f98850` / `0x141f988f0` | Fn | world object header write / read |
| `0x1416b21d0` | Fn | perf overlay ("network: {} ms") |
| `0x141e81c20` | Fn | Pathfinding component MoveTo (target, min/max range, precision, tolerance) |
| `0x141e82010` / `0x141e81de0` | Fn | Pathfinding: compute the path (waypoints `+0xd4`, count `+0x518`) / start the next segment |
| `0x141e52690` / `0x141e50f90` | Fn | the pathfinder (A*, player ellipse, waypoint pruning; `ClientPathfinder`) |
| `0x141e50ed0` | Fn | pathfinder heuristic (distance outside the range + 0.001 × cross product) |
| `0x141d888f0` / `0x141d8a7f0` | Fn | walk value of a cell (4 bits, 3 layers) / Bresenham line check against a threshold |
| `0x141e65910` / `0x141e65ae0` / `0x141e65c40` | Fn | object collision: cell (tile buckets) / one object (Chebyshev − size) / line |
| `0x141bedeb0` / `0x141bedd40` | Fn | pathfinder open list push / pop (MSVC heap, `a.f > b.f`) |
| `0x142fc93e0` / `0x142fc9420` | Data | the 8 directions (dx, dy) / opposite direction |
| `0x141e37740` / `0x141e50880` | Fn | Positioned straight move to a cell / Bresenham segment setup (step time) |
| `0x141e35a50` / `0x141e50a90` | Fn | Positioned per-frame move tick (step checks, accumulator) / one Bresenham step |
| `0x141e38420` | Fn | Positioned stop: takes effect at the next tick (`+0x1e6` bit 7), keeps the accumulator |
| `0x141e34540` / `0x141e38590` | Fn | 0x1b7 relocation / immediate glide cancellation: clears movement durations +0x240/+0x280; projectile impact endpoint (static 3.29.3c evidence) |
| `0x141e52590` | Fn | pathfinder: the coarse waypoint a cell heads for (within √143 and in sight) |
| `0x141e827a0` | Fn | Pathfinding speed: base `+0x548` × (1 + stat 0xf5 / 100) × (1 + stat 0xce6 / 100), capped by stat 0x44f |
| `0x141ea32d0` | Fn | Actor setup: run-speed roll `+0x1a8` = 1 + v / 100, v by TinyMT32 in the actor data's [`+0xa0`, `+0xa4`] |
| `0x1413ce240` / `0x1404d0990` | Fn | TinyMT32 seeding (init_by_array) / random int in a range |
| `0x1421660b0` | Fn | C→S use a skill: lockstep 0x018e (dx, dy) / 0x018f (object) when the network mode `+0x984` is 2, else 0x25 / 0x24 |
| `0x1421664c0` / `0x142166660` / `0x142166800` | Fn | C→S lockstep 0x0192 (new dx, dy) / 0x0195 (release) / 0x0196 (release a skill) |
| `0x14218a2a0` | Fn | world update: lockstep runs the queued server frames (deque `+0x9c0`, count `+0x9d8`), catching up × 1.01–5 |
| `0x14218a650` | Fn | world step (vtable `+0x360`): one server frame's messages, then the objects' ticks |
| `0x14218dc20` | Fn | world message intake: queues messages; S→C 0x2b7 ends a frame (pushes world `+0x11c` ms), 0x2b8 sets it |
| `0x141648b50` | Fn | S→C 0x2b8: world `+0x11c` = the frame length in ms |
| `0x141915300` | Fn | config `networking_type` → network mode (0 predictive, 2 lockstep, 3 auto) |


## Fixed instance-message writers ([02](02-instance-connection.md))

| Address | Kind | What |
|---|---|---|
| `0x140e27920` / `0x14346cd80` | Fn/VT | C→S 0x020a writer / vtable: u16 opcode and one byte |
| `0x141f99e90` / `0x1435ca8b0` | Fn/VT | C→S 0x0059 writer / vtable: 16 bytes including opcode |
| `0x1421669d0` | Fn | C→S 0x0059 sender; fixed 4/2/2/4/1/1-byte fields |

## Shared projectile execution ([RE 012](../re/012-projectile-execution.md))

Bounded static research for 3.29.3b; detailed fields, concrete vtables, evidence and open questions are in
[the execution ledger](../re/projectiles/evidence-and-open-questions.md).

| Address | Type | Role |
|---|---|---|
| `0x141ba73c0 → 0x141ba7840 → 0x141ba7c30` | Fn | release, animation/model contact offset, count/origin/aim/terrain preparation |
| `0x141ba8b00` / `0x141ba8c50` | Fn | generic arrows/projectiles count and per-release distribution |
| `0x141bb3e30` / `0x141bb39b0` | Fn | single/multiple emission dispatcher and layout profile |
| `0x141bbb460` / `0x141bbbd10` / `0x141bbbf90` / `0x141bbc1c0` | Fn | Volley / randomized seed-vector / radial / fan layouts |
| `0x141a0f3e0` / `0x141bbcb00` / `0x14193fe70` | Fn | five-word / seven-word projectile RNG initialization / object-seed hash; recipes in RE 012 random inputs |
| `0x141bb4f10` / `0x141bac460` | Fn | per-arrow admission/construction and final placement/observer/overlap/motion |
| `0x141423e30 → 0x1419433b0` / `0x14193fe70` | Fn | derived-byte allocator / object-seed generation boundary |
| `0x141baf9f0` / `0x141e35a50` | Fn | shared action motion setup / Positioned frame and due-sample loop |
| `0x141e50880` / `0x141e50a90` | Fn | dominant-axis integer path init / next sample, strict >0 minor-axis tie |
| `0x141baa5c0` / `0x141bafee0` | Fn | shared candidate contact / special-object handling |
| `0x141b723e0` / `0x1419f1830` | Fn | final selected attack / ordinary Fireball avoidance wrappers; acceptance bool inverted |
| `0x141eeb180 → 0x141ec3a50` | Fn | supplied f32 chance to target-Stats entropy; target ID/sequence initialization and single-subtraction trial |
| `0x141e441c0` / `0x1421c98b0` | Fn | target entropy reset-timer update / creation LE entropy-timer-reset trailer |
| `0x141ec38e8..0x141ec392c` | Code | attack's one-step defensive-provider redirect/fallback; original target Entity retained, selected component owns entropy |
| `0x141f988f0` / `0x14218ead2..0x14218eb5b` | Fn / Code | property-message common header / typed Stats provider pair loop, separate from entropy creation trailer |
| `0x141ec3ba0` | Fn | local spell avoidance: deterministic identity seeds or TLS mode, signed extra-roll clamp/min/max |
| `0x141ec3240` / `0x1425060c0` | Fn | ordered attack probability composition / base u32 accuracy-evasion formula: divisor 5, f32 exponent 0.9, 1.25 multiplier |
| `0x1403b4ee0 → 0x142d83230` / `0x1406654d0` | Fn | native power dispatch/body / f32 add-half floor-to-i32 helper |
| `0x141ba9240` / `0x141ba9270` / `0x141ba9350` | Fn | completed observer / blocked observer / shared terminal |
| `0x141bb0d40` / `0x141bb0b50` / `0x141bbb2d0` | Fn | phase target contains/insert / history deep copy |
| `0x141bb0c60` / `0x141bb2080` | Fn | erase own target reservation / dispatch local continuation reason |
| `0x141bb14f0` / `0x141bb19d0` / `0x141bb1d30` | Fn | Chain candidates / ranking flags / redirect |
| `0x141bb8fc0` | Fn | chain budget (vtable +0x638): `virtual_number_of_chains_for_projectiles` from the skill record; Rolling Magma's ground bounces spend it ([11](11-skills.md#10-projectiles-experimental)) |
| `0x141bb1bc0 → 0x141bb3450` | Fn | random/terrain Bounce, previously mislabelled Return |
| `0x141bb2f80` / `0x141bb31a0` | Fn | actual Return qualification / phase mutation and redirect-or-clone |
| `0x1419627f0` / `0x141962870` | Fn | hit handle reader / sequence-derived-stage comparator |
| `0x141e977f0` / `0x141e974d0` | Fn | 0x1af immediate/queue / 0x1b0 first-match remove and optional process |
| `0x14193f690` / `0x14193f8c0` | Fn | exact local hit-result consumption / stage increment and old-range processing |
| `0x141e97bd0` / `0x141e97da0` | Fn | result range processing / kind-world filter |
| `0x1416502e0` | Fn | checked client world getter returns 1+(raw mode==2); ordinary queue survives end batches |
| `0x141e957f0` / `0x141e93130` | Fn | retained-action runner / Actor reset including full pending-result cleanup |

## Skills ([11](11-skills.md))

| Address | Kind | What |
|---|---|---|
| `0x1400dd410` | Fn | registers own-aura virtual 11075 with dependencies 21148/11881 |
| `0x1428a0fe0` | Fn | 11075: signed-nonzero 21148 OR 11881 |
| `0x1400e5030` | Fn | registers aura-skill virtual 15801 |
| `0x1428d62e0` | Fn | 15801: 3004 OR (2730 AND 2532) |
| `0x1400e5090` | Fn | registers skill-disabled virtual 15798 |
| `0x1428d6330` | Fn | 15798: aura branch checks skill 15801 and caster 11075; other restrictions separate |
| `0x141eee810` | Fn | skill usable (+0xb0) from the hand types, skill types and skill stats, `is_dual_wielding` included ([11](11-skills.md) §10a) |
| `0x141eeec70` | Fn | one hand's type against the skill's weapon restriction (+0xb2 main / +0xb3 off) |
| `0x141eef550` | Fn | skill has ActiveSkillType (row + 1) |
| `0x14219fb70` | Fn | ActiveSkillManager vtable +8: stores the hand types at +0x14c / +0x150 |
| `0x14251a1e0` | Fn | registers a stat calculator: key, evaluator, dependency keys |
| `0x140093520` | Fn | registers `is_dual_wielding` (0xf6) with dependencies 3, 4, 0x2b33, 1 |
| `0x142527d40` | Fn | `is_dual_wielding`: both hands in table 0x142fe77c0 and off hand not 14, or unarmed rule ([13](13-items.md) "Client weapon-type stats") |
| `0x142fe77c0` | Data | byte per native weapon type: set for weapons, clear for 13 (shield) and 14 (empty hand) |
| `0x1428b2680`, `0x1428b2e00`, `0x1428b2a40` | Fn | `is_wielding_sword` (0–2), `_mace` (3–5), `_axe` (7, 8): the weapon-type enum's sources |
| `0x14332f508` | Vtable | Shield Charge's action (type 0x2bc0, constructor `0x1413c9170`; [11](11-skills.md) §10k) |
| `0x141a6ea70` | Fn | moving-attack per-frame update: travel, then the end phase's scheduled events |
| `0x141a6e4f0` | Fn | moving-attack setup: path, at most 75 cells × (1 + `shield_charge_range_+%` / 100), `shield_charge_extra_distance` |
| `0x1413c9dc0` → `0x141a701d0` | Fn | Shield Charge's hit (vtable +0x550): kind 0 path, 1 final, 2 starts the end phase (+0x3f8) |
| `0x141a70170` | Fn | a moving-attack hit's range by kind (Shield Charge 8 / 16 / 28) |
| `0x141a6f440`, `0x1413c9b70` → `0x141a70910` | Fn | arrival; Shield Charge's override: final hit, then the end attack's animation |
| `0x1428b7310` | Fn | virtual double-armour chance 12268; [model/evidence](../game/steel-willed-armour.md) |
| `0x1428b7414` | Code | reads Steel Willed per-recent-enemy-hit input 20680 |
| `0x1428b74ab` | Code | reads recent enemy-hit counter 4534 |
| `0x1428b7522` | Code | multiplies per-hit input by recent count |
| `0x1428b77e4..0x1428b77f6` | Code | caps the per-hit contribution at 30 |
| `0x1428b77a3` / `0x1428b77ff` | Code | reads generic chance 8404 / adds it after the contribution cap |
| `0x1435e6400` | VT | ActiveSkillManager (Actor +0x598; skills at +0x158, sorted by instance +0x40) |
| `0x141ef0f00` | Fn | ActiveSkillManager::SkillInstanceAdded (manager slot 7) |
| `0x141ef0ca0` | Fn | find/create skill cooldown record (requires virtual cooldown count) |
| `0x141ef2730` | Fn | cooldown object constructor: base duration from skill +0xc8 or override |
| `0x141ef2840` | Fn | cooldown consume: base-time entry and separate recovery rate |
| `0x141ef2610..0x141ef2628` | Fn | cooldown tick subtracts elapsed time times recovery rate |
| `0x141ef200a..0x141ef2047` | Fn | refresh an active cooldown entry's recovery rate |
| `0x141ef1573..0x141ef15aa` | Fn | skill replacement refreshes cooldown only for base/count changes or zero rate |
| `0x14219e694..0x14219e7eb` | Fn | Actor 0x40 cooldown trailer reads base-time progress/total, rate and flags |
| `0x14219e900..0x14219e948` | Fn | Actor 0x40 replaces the matching existing cooldown entry vector |
| `0x140123640` | Fn | instant input sender: mode 4 ground 0x190, modes 5/6 object/self 0x191 |
| `0x1435cb150` | Cls | C→S 0x190 location instant; writer `0x141f98850`, reader `0x141f988f0` |
| `0x14219c1f0` | Fn | shared S→C 0x1ce/0x1cf action executor: transported sequence, derived-one registration and synthetic start/contact |
| `0x1435ccac0` | Cls | S→C 0x1cf event-bearing action; writer `0x141fe4b90`, reader `0x141fe4d50`, copy `0x141fe3ec0` |
| `0x1421cce19` | Code | world factory's 0x1cf branch: allocate 0xd8, install message table, invoke reader |
| `0x1419e7ee0` / `0x141bb0d10` | Fn | action configuration reader / projectile receiver: optional signed speed input to action +434 |
| `0x14328cb08` | Vt | IceDash action, installed by factory `0x141282930` |
| `0x1412f3590` | Fn | IceDash start: departure at action +0x318, aim at +0x320 |
| `0x141c00cd0` | Fn | blink endpoint: max 42 cells scaled by stat 11856, min 32, then terrain validation |
| `0x14219c421..0x14219c47e` | Code | object instant confirmation binds target and reads its Positioned cell |
| `0x141c010a0` | Fn | object blink aim: minimum vector, size-aware extension, candidate trace before clamp |
| `0x141c04ae0` | Fn | object blink candidate Bresenham trace, retaining the last terrain/target-valid cell |
| `0x141c056c0` | Fn | candidate target-footprint check through `0x141c06710` |
| `0x1406722c0` | Fn | normalized cell vector: floor(component * scale + .5), including negative components |
| `0x141ef0050` | Fn | create the action for a skill id |
| `0x1421991d0` | Fn | ClientActor ctor: basic actions from the type's u16 list (`type+0xf8`) |
| `0x14219f6b0` | Fn | AddSkill(manager, ActionTypes HASH16): basic action |
| `0x142199c20` | Fn | Actor slot 20 reader (f0 0x40 skill list, 0x20 skill update, …) |
| `0x1424763d0` | Fn | SkillGem: the level from the experience (thresholds from ItemExperiencePerLevel) |
| `0x14219e480` | Fn | Actor 0x40: skills, soul row/capacity/current, cooldown trailer |
| `0x141fe5330` / `0x141fe5270` | Fn | S2C 0x1b4 skill/stat-set raw-pair reader/writer ([11](11-skills.md#31-s2c-0x1b4-update-one-skills-stat-set)) |
| `0x14219adf2..0x14219b047` | Code | Actor 0x1b4: full-u16 skill lookup, selected network stats, pair setter and conditional cooldown refresh |
| `0x14219f170` | Fn | Actor 0x20: skills/removes, soul seeds, current/new-capacity updates |
| `0x1421a03f0` | Fn | Sorted soul-entry upsert at skill manager +0x188, stride 0x20 |
| `0x141ef0e10` | Fn | Soul payload lookup through GrantedEffects ActiveSkills pointer |
| `0x141eeef80` | Fn | Effective required souls: no-souls flag, selected multiplier, final percent |
| `0x141ef0c00` | Fn | Availability: required souls <= current |
| `0x141ef0bb0` | Code | Native soul subtraction and dirty flag inside 0x141ef0590 |
| `0x140ad0900` | Fn | Soul icon uses/fill; divisions guarded by nonzero required souls |
| `0x140ad8d00` | Fn | Soul current/capacity text |
| `0x140827ab0` | Fn | Soul current/capacity text |
| `0x14219eed0` | Fn | Actor f0 0x20: remove u16 skill ids from the skill manager |
| `0x14219eb40` | Fn | read n skill records into the manager |
| `0x14219d650` | Fn | skill record reader |
| `0x14219df60..0x14219df80` | Code | Clear wire bit1 and nonzero raw stored uses set constructor disable bit |
| `0x14219e058..0x14219e066` | Code | Constructor parameter13 = (wire bit2 * 2) or dil |
| `0x141eeddd1..0x141eede1a` | Code | Parameter13 bit0 zeros soul inputs; otherwise copies GEPL +0x28/+0x2c |
| `0x1424a7890` | Fn | GrantedEffectsPerLevel reference reader |
| `0x1424a7460` | Fn | GrantedEffectStatSetsPerLevel reference reader |
| `0x141eedc30` | Fn | ActiveSkillInstance ctor (action id from GEPL → GrantedEffect → ActiveSkill → ActionType) |
| `0x1435c19f0` | VT | ActiveSkillInstance |
| `0x1424d6510` | Fn | stat getter (stats, key); stored pairs at stats +0xf0 |
| `0x1424d03f0` | Fn | Character-panel formatter: ordered stat keys at +0xd8/+0xe0; returns UTF-16 text. Natural leech-row calls verified ([20](20-damage.md) §5b). |
| `0x1424ccaf0` | Fn | Panel argument converter: leech limits use NoFunction, DivideByOneHundred, PerMinuteToPerSecond2dpIfRequired and PermyriadPerMinuteToPercentPerSecond. |
| `0x141e70730` | Fn | Life tick: notifies listeners only while life > 0 |
| `0x140220e50` | Fn | mini life bar entry: flags from the Life pools (0x20 = dead) |
| `0x140220fd0` | Fn | mini life bar entry: stat listener (`hide_mini_life_bar` 0x2a8b, `virtual_is_hidden_monster` 0x59d0) |
| `0x140222370` | Fn | mini life bar: visible? |
| `0x141e937b0` | Fn | Actor: start (queue) an action |
| `0x141e94900` | Fn | Actor: per-frame runner of the queued action |
| `0x141e93c70` | Fn | Fresh local action sequence from Actor `+0x21a + 1` |
| `0x141e951cb..0x141e951d2` | Code | Action start: copy u16 sequence `+0x11a` to Actor RNG seed `+0x21c` |
| `0x141ba5b94..0x141ba5ba5` | Code | Animation seed: zero-extended action sequence plus intrinsic repeat count |
| `0x141b71600` | Fn | Melee ctor: `attack_repeat_count` (stat 0x4eb) becomes action `+0x2d0` |
| `0x1419e6370` | Fn | Melee completion: decrement intrinsic repeat count; previous zero ends action |
| `0x140e0a760` | Fn | Animation RNG initializer: literal uint32 recurrence and eight warm-up steps |
| `0x1404855a0` | Fn | Native animation RNG next word: state transition and tempering |
| `0x140485560` | Fn | Native bounded RNG draw: rejection sampling; bound one consumes no word |
| `0x141e9f4e0` | Fn | Speed-based ACT animation eligibility and weighted reservoir selection |
| `0x141e9fa80` | Fn | Duration-based ACT animation eligibility and weighted reservoir selection |
| `0x141fe51f0` | Fn | 0x1d1/0x1d2 reader: duration float, Animation.dat type byte, boolean |
| `0x141e9c700` | Fn | 0x1d1 Actor animation: cancel prediction, select stance ACT, lock input during recovery |
| `0x14219b333` | Code | Actor 0x1d1 dispatch to virtual `+0xe0` recovery handler |
| `0x141e941a0` | Fn | Action-start gate: Actor `+0x219` bit 2 blocks input during recovery |
| `0x141e96fe0` | Fn | End local predicted action (Actor +0x1b8) |
| `0x141e99d50` | Fn | End Actor animation recovery; clear +0x219 bit 2 and resume queued/idle actions |
| `0x141e5d440` | Fn | Generic Animated message handler: 0x1b2 kind zero plays a rig clip |
| `0x141dbdbc0` | Fn | Play(controller, resource pair, index in r8d, speed); main state at controller +0x180 |
| `0x14193df80` | Fn | Melee action begin = skill_is_non_pathing |
| `0x1421a6190` | Fn | S→C 0x55 death screen read |
| `0x140290210` | Fn | `death_screen` panel ctor (setup `0x140290db0`, buttons `0x140291180` → C→S 0x54 / 0x153) |
| `0x140d838f0` | Fn | HUD vtable +0x4d0: show/hide the death screen |
| `0x1435e73a0` (read `0x1421a5f70`) | Cls | S→C 0x53 skill bar: 13 × u16 skill ids (LE), u16 slot masks → instance +0xcfd0, +0xcfd2 |
| `0x1435ca380` | Cls | C→S 0x4f "Always Attack Without Moving" / 0x52 (u8 slot, u8 on; bit slot of instance +0xcfd0 / +0xcfd2) |
| `0x140aecf00` | Fn | skill bar slot option box: toggles instance +0xcfd0 bit slot, sends C→S 0x4f |
| `0x1435ca670` | Cls | C→S 0x4e bind a skill to a skill bar slot (u8 slot, u16 skill id) |
| `0x140aec430` | Fn | skill bar UI: put a skill in a slot (`0x140ae2400`) or clear it (`0x140ae2d20`), then send C→S 0x4e |
| `0x1431a7b88` | VT | skill bar UI (13 slots; `0x140ae1200` subscribes it to instance +0xaf78 and +0xad78) |
| `0x140ae1d00` | Fn | skill bar vtable +0x390: rebuild from 13 skill ids (lookup in ActiveSkillManager +0x158, slot set +0x398, clear +0x3a0) |
| `0x1435aada8` | VT | Projectile component (0x20 bytes; slot 17 reads nothing, almost every slot a `ret`) |
| `0x141e3ab90` | Fn | Projectile component constructor (writes `0x1435aada8`) |
| `0x141e33fd0` | Fn | Positioned message handler: 0x1b7 teleport, 0x1b8, 0x1b9, 0x1ba move (height u32 +0x28, speed f32 +0x2c), 0x1bc |
| `0x1419ea070` / `0x1419f1960` | Fn | Spell duration: virtual base milliseconds / 1000 / (1 + cast speed / 100) |
| `0x1419e9f10` / `0x1419ea04c` | Fn / Code | Ordinary spell speed getter / read cast_speed_+% (296) |
| `0x141d88190` | Fn | terrain height of a cell (terrain, int cell[2]) in steps of 7.8125; `0x141e38b40` interpolates for a Positioned |
| `0x1415f7500` | Fn | Render: model transform; tilt = −`0x141e38f40`(Positioned) when the type has `tilt_to_target` |
| `0x141e37740` | Fn | Positioned: start a move (integer path `0x141e50880`; height 0x7fffffff = terrain, else z = −height × 7.8125) |
| `0x141e37c5a` / `0x141e37c61` | Fn | Move endpoint/current Positioned markers passed to the 3D stepper |
| `0x141e37c9b` to `0x141e37cab` | Code | Move endpoint world z = -marker * 7.8125 before render interpolation |
| `0x141e50c10` | Fn | Render interpolation setup: stores endpoint world z minus current world z |
| `0x142048bb0` | Fn | create one client-side doodad from a placed record (grid x, y +0, +4; angle +0x14; type +0x30; .ao +0x38): rooms' doodads too ([14](14-monster-packs.md) §3) |
| `0x142154531` | Fn | instance dispatcher case 0xe2 (S→C 0x2aa gold, read `0x140911270`, u32 BE): stores it at session +0xc40c, plays Gold_Spend.ogg when it drops |
| `0x140d60630` | Fn | 0xb8 listener on the UI state: flags bit 0 → +0x984 = other party, +0x988 = 1 (the active trade) |
| `0x14030e970` | Fn | Ctrl + click on an inventory item: with +0x988 set sends 0xb2 x = y = 0xff (`0x14216d010`), else the other panels' checks |
| `0x140dc5fe0` | Fn | S→C 0x7b kind 0x5f: the character's passive tree (UI +0x650); u32 ≠ 0 → mode 2 (`0x1409df890`) |
