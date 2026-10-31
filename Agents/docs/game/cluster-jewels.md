# Cluster jewels

Cluster jewels extend the passive tree: socketed into the outer jewel sockets, they create a small cluster of passive
nodes and, for the larger sizes, more jewel sockets of their own. This page records the game's rules, the mods they
carry, and exactly what the server does with them.

Game data: `PassiveTreeExpansionJewels.json` (indices per size), `PassiveJewelSlots.json` (the pane's expansion cells),
`PassiveSkills.json` (`IsExpansion` rows), `Mods.json` (domain 21, `ClusterJewel`). The full generated list of the
301 notables and 42 small-passive themes, with a works/partial/inert note for each, is
[cluster-jewel-list.md](cluster-jewel-list.md).

## Sizes

| Size | Passive skills (sockets included) | Jewel sockets | Notables | Base type |
|---|---:|---:|---:|---|
| Small | 2-3 | 1 | 1 | `Metadata/Items/Jewels/JewelPassiveTreeExpansionSmall` |
| Medium | 4-6 | 1 | 2 | `Metadata/Items/Jewels/JewelPassiveTreeExpansionMedium` |
| Large | 8-12 | 2 | 3 | `Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge` |

A non-unique cluster jewel carries two enchantments: how many passive skills the cluster has and what those passives
grant (that decides its mod pool). Medium and large ones carry a third one for their sockets (one and two respectively
since 3.11). The explicit mods then add notables, socket or keystone nodes.

A cluster jewel's only affixes are its notables, and the rarity follows them: none is **Normal**, one or two **Magic**,
three (a large cluster can carry three) **Rare**. A Magic jewel with no affix makes the client render the whole cluster
as empty "Nothingness" nodes, so `/cluster_jewel` sets the rarity from the notable count.

The client builds the layout from the **cell's size** (`PassiveTreeExpansionJewels` keyed by the base type), not from the
jewel's: a large cell has 8-12 nodes of which 2 are sockets, a medium 4-6 of which 1, a small 2-3 of which 1. The
"Adds N Passive Skills" count is that total, so a large "8 passives, 2 sockets" jewel has 6 small/notable passives and
its 2 sockets are 2 of the 8. A cluster jewel of a **smaller** size also fits a larger socket (small < medium < large):
the client pads the missing positions with the empty `affliction_empty_node_` ("Nothingness") node, which the player can
allocate for nothing. The server allows that (`ClusterSize(item) <= ClusterCellSize(cell)`); only the jewel's own
passives give stats.

Voices, the unique large cluster jewel, is the exception the server uses as its pilot: it adds **three** jewel sockets
(`local_jewel_expansion_jewels_count_override`) plus 1, 3, 5 or 7 empty passives, so all its sockets cost 4/5/7/8 points.

## Sockets and nesting

- A tree's outer (large) jewel socket takes a **large** cluster jewel, or a normal jewel.
- A **large** cluster jewel's sockets take a **medium** cluster jewel, or a normal jewel.
- A **medium** cluster jewel's socket takes a **small** cluster jewel, or a normal jewel.
- A **small** cluster jewel has one socket position in its template (index 4), but no pane cell opens for it: it is a
  stat-less node in the small's layout, not a cluster-jewel socket.
- A cluster jewel of a smaller size fits a larger socket (small into medium or large, medium into large). The client pads
  the cell with empty nodes, so only the jewel's own passives count.
- Normal jewels (ordinary, magic, rare, unique) can go into any of them; abyss jewels are a separate item class.
- The client numbers the socket cells of a large jewel 51, 52 and 53 whatever its socket count (it offers 53 even for a
  two-socket large). Its two **real** sockets are attached to its first and third child slots (cells 51 and 53), not
  51 and 52; the server accepts any of a parent's expansion cells and lets the client's layout decide which one it opens.

## Other rules

- **Points**: every cluster passive costs a point like a tree passive. Unsocketing the jewel refunds all the points
  spent in its cluster; if a jewel socket or a keystone of that cluster was allocated, unsocketing costs one refund
  point instead.
- **Notables**: one jewel never has the same notable twice; the same notable in two jewels stacks.
- **Radius**: cluster passives are never in the radius of a jewel, and a radius jewel socketed into a cluster jewel
  socket has no radius effect. Path of Building precomputes `socket.nodesInRadius` in `PassiveTree.lua` before
  `PassiveSpec:BuildClusterJewelGraphs` adds the synthetic cluster nodes, so its radius functions do not see them
  either; the client also draws cluster passives in the separate PassiveJewels pane.
- **Refunds**: a socket that still holds a jewel cannot be refunded until the jewel is taken out (the client has no
  automatic return).
- **Taking a nested jewel out**: only the leaving jewel's own passives block its removal. A nested cluster jewel (in an
  expansion cell of another cluster) leaves even while the outer jewel keeps all of its passives allocated; the outer
  jewel can only leave once its cells are empty.

## Mods

`Mods.json` holds 532 cluster-jewel mods (domain 21). They are the whole vocabulary of a cluster jewel:

| Kind | Mods | Stat | What it does |
|---|---:|---|---|
| Notables | 301 | `local_affliction_notable_<name>` | "1 Added Passive Skill is <notable>". Every mod has a matching `affliction_notable_<name>` row in `PassiveSkills.json` (300 distinct names; one duplicate row) that carries the notable's stats. |
| Small-passive grants (the theme) | 211 | `local_affliction_jewel_small_nodes_grant_<stat>` | "Added Small Passive Skills grant: ...", 42 distinct stats, three tiers each (`+2-3`, `+4-7`, `+8-10` life and so on). |
| Keystones | 7 | `local_jewel_expansion_keystone_<name>` | Lone Messenger, Disciple of Kitava, Nature's Patience, Secrets of Suffering, Kineticism, Veteran's Awareness, Hollow Palm Technique. |
| Small-passive effect | 2 | `local_affliction_jewel_small_nodes_have_effect_+%` | 25% or 35% increased effect of the small passives. |
| Structure (enchantments) | 11 | `local_jewel_expansion_passive_node_count`, `local_jewel_expansion_jewels_count`, `local_jewel_expansion_passive_node_index`, `local_unique_jewel_grants_x_empty_passives` | "Adds N Passive Skills", the jewel sockets, and Voices' empty passives. |

The theme, the notables and the keystones are *inputs* to the cluster the client generates: it builds the small passives
from the theme and the notable/keystone nodes from those mods, and the player allocates them like tree passives.

## What works and what does not

Rules, as a checklist:

| Rule | Status |
|---|---|
| A cluster jewel goes into a large tree socket (pane cells 11, 12, 13, 15, 17, 20) | **works** |
| A cluster jewel opens only the cluster sockets it has (Voices 3, large 2, medium 1, small 0; the small template's socket is a stat-less node) | **works** |
| Nesting by size: a cluster jewel fits only a socket of its own size (large tree socket → large, large jewel's sockets → medium, medium one's socket → small) | **works** |
| A normal jewel (ordinary/magic/rare/unique) in any open cluster socket counts like a tree jewel | **works** |
| Removing a cluster jewel needs its own sockets empty, its passives refunded, and costs one refund point | **works**: a jewel with allocated passives cannot leave, because the tree would keep listing nodes of a jewel the client no longer has (`PassiveAllocation::IsAllocatedLeaf`) |
| The cluster's passive points are refunded when a passive is refunded (C→S 0x36, expansion flag) | **works**: the client drops the node itself and the point returns |
| `/cluster_jewel` and `/voices` make a real item the client draws (enchantments, notables) | **works** (dev source only) |
| Allocating the cluster's passives costs points | **works**: a point each, counted in the tree message's unspent; the ids ride in its third list and are saved with the character (see below) |
| Cluster notables, small passives and keystones give their stats | **partly**: a tree-socket cluster gives the theme on each small passive as it is taken, nothing on a socket, and its notables' rows once all of its passives are allocated. A measured 4-passive medium in an expansion cell gives the theme on each of its smalls and nothing on its socket; other expansion-cell shapes count once all passives are allocated. Keystones are not modelled |
| One copy of a notable per jewel, the same notable stacked across jewels | **no**: notables are not tracked |
| Cluster passives are never in another jewel's radius; a radius jewel in a cluster socket has no radius | **by design**: Path of Building precomputes each socket's `nodesInRadius` before `BuildClusterJewelGraphs` adds the cluster nodes, and the client draws them in a separate pane; the server's radius query covers only the ordinary PSG |
| Abyss jewels in cluster sockets | **no**: abyss sockets live on items, out of scope |
| Vendor recipe (5 of the same size and rarity for one new jewel) | **no** |

Mods, kind by kind:

| Kind | Works | Missing |
|---|---|---|
| **Notables** (301) | Every notable's `affliction_notable_*` row is added once all of the jewel's passives are allocated (the sum does not need their positions). All 301 have a row. The generated list marks 42 with every stat read, 118 partial (condition or unread stat) and 141 with no read stat; a partly allocated cluster has no notable stats yet. | The server applies the row generically, but conditional or minion/trap/totem stats may have no consumer. |
| **Small-passive grants** (211, 42 stats) | The theme applies per allocated cluster node, up to the small passives of a full cluster (`ClusterJewelStats.ThemeStats` translates the granted stat: `..._grant_str` is `additional_strength`, `..._grant_fire_resistance_%` is `base_fire_damage_resistance_%`…). 20 of the 42 are translated. | Sockets are assumed to be taken last, so a partly allocated cluster may count one as a small passive; the other 22 stats (per-skill-type speeds, minion and totem grants and the like) are not translated or have no consumer. |
| **Small-passive effect** (2) | **works** for the theme: `local_affliction_jewel_small_nodes_have_effect_+%` scales it on every small passive of a fully allocated cluster. | Not applied to the notables (they are not small passives). |
| **Keystones** (7) | The item can carry them. | The keystone nodes and their effects are not modelled. |
| **Structure** (11) | **works**: `/cluster_jewel` writes the count, the sockets and the theme; `/voices` writes its override and empty passives; the socket counts drive which cells open. | The values are fixed by the dev commands, not rolled like a drop. |

How much of the notables' data the server could already use: their 301 rows carry 685 stat entries in 468 distinct
stats. The generated [list](cluster-jewel-list.md) marks each notable conservatively from the source consumers; 42
notables have every stat read, 118 are partial (a conditional stat or an unread stat) and 141 have no read stat. The
unread part is mostly conditional (`..._if_...`, `..._while_...`), minion/totem/trap stats or panel-only, so a notable
can still be partly inert even after the mapping.

## The client's cluster node ids

The client sends the allocated cluster nodes as small ids (196, 198, 202, 206 for a Voices), not
`PassiveSkillGraphId`s. The set mirrors the client's own (0x34 adds, 0x36 refunds) and is **saved with the character**, so
a relog restores it: the block base is fixed per pane cell and the layout is deterministic for the same jewel, so the ids
the client builds again are the same. On load the server only keeps as many as the socketed cluster jewels still have
room for (`ClusterNodeBudget`: the `Adds N Passive Skills` count per jewel, or Voices' empty passives plus its sockets)
and drops them all when no cluster jewel is socketed. A cluster jewel may not leave its socket while its passives are
allocated: a tree that lists nodes of a jewel the client no longer has crashes it in
`PassiveAllocation::IsAllocatedLeaf`, so the player refunds them first.

The ids come from the client's own node-id function (`FUN_141f0e770`; the generator is `FUN_141f10110` and the
constructor `FUN_141f114f0`, all decompiled after the full analysis and traced live with
`scripts/re/frida/trace-cluster-ids.js`):

- the block base is `39 * ClusterIndex` for a jewel in a tree socket (cell 20, index 5, uses 195);
- a child of one (a large jewel's socket cell) adds `9 * its ClusterIndex + 12` (cell 51 -> 207, cell 53 -> 225);
- a grandchild (a medium jewel's socket cell) adds 6.

The node's ring position is `(StartIndices[size] + templateIndex) % TotalIndices`, where `size` is the **jewel's** own
size (not the cell's); the id is the base plus that position. A tree-socket large jewel keeps the full 12-slot ring: the
`PassiveTreeExpansionJewels` template lists give the smalls, the sockets (the first socket positions) and the notable
positions (the first free notable positions in ascending order). Cell 20's 8/2 therefore uses smalls 195, 197 and 202,
sockets 198 and 206 and notables 196, 200 and 204.

A jewel in an **expansion cell** is compressed onto half the ring (the client draws its nodes on a 16-slot orbit and the
id is the base plus `position / 2`); the full ring is kept only in a large cell (a tree socket). Its nodes are laid
evenly around it: a 4-passive medium uses template indices 0, 3, 6 and 9, its own socket on the template's socket index
(6) and the other three small. Measured: the medium in cell 51 gives 208, 209 and 211 (smalls) and 212 (socket); the
same jewel in cell 53 gives 225, 228 and 229 and the socket 226. Only that shape is mapped so far: a jewel with
notables in an expansion cell still counts once all of its passives are allocated.

A **smaller jewel in a larger cell** keeps its own template but uses the cell's base and the cell's start for its size.
Measured with a rare small in the large tree socket 17 (base 156, cell start for size 0 is 5): the small's 6-ring
indices 0/2/4 give **157 and 161 as theme smalls and 159 as its first notable** (Prodigious Defence); the extra notable
mods are inert.

The six tree sockets use the same formula: cells 11, 12, 13, 15, 17 and 20 have ClusterIndex 0-5, so their blocks start at
0, 39, 78, 117, 156 and 195. Verified live on the first one (cell 11, base 0): a large 8/2 jewel, a medium in its cell 21
(base `0 + 0*9 + 12 = 12`) and a blue small in the medium's cell 24 (base `12 + 6 = 18`); allocating the small's 18/19
added the theme (7 life each) and 20 was Fettle. The other four tree sockets are not allocated on the test character, so
their blocks are covered by the shared formula rather than a live check.

A **small** cluster jewel is built by the client when it has a notable (a blue or yellow one): the small template has a
single notable position (index 4), so the client shows the first notable and the other two ring positions are theme
smalls; extra notable mods are inert and the jewel's own socket is not used (the small template's socket index shares
position 4). Measured live (a rare small with Fettle, Prodigious Defence and Advance Guard in cell 54): base
`207 + 6 = 213`, the notable at **213** and the theme smalls at **214/215**; in the medium cell 53 the same jewel uses
**225/227** (smalls) and **226** (its notable); in the large tree socket 17 it uses **157/161** (smalls) and **159**
(notable). A Normal, theme-only small drew no graph in our test, so only the notable shapes are mapped.

The client calls the smalls "Nothingness" but their tooltip carries the theme (+7 maximum Life for a life theme) - they
are the real small passives. So each small passive grants the theme as it is allocated, a socket grants nothing, and
each notable position applies the row of the notable the layout puts there (the jewel's notables in the order of their
Stats.dat row: for Fettle, Prodigious Defence and Advance Guard, Prodigious at 204, Advance at 196 and Fettle at 200).

What the client's map shows (live dump, hooking the resolver `0x1422d9920`): the tree's id -> node table is a flat
hash map (`[[resolver context] + 0x28] + 0x60`, FNV-1a over the two id bytes, probes of `0x100`, 16-byte entries whose
first u16 is the id) with **5706 entries keyed by the real `PassiveSkillGraphId`s**. The expansion nodes live in a second
map on the tree (`tree + 0x2b0`, keyed by the `PassiveJewelSlots` row, one graph per socketed cluster jewel), so a
cluster id can *collide* with a tree id: the client tells them apart through the `expansion` flag, and a tree message
that carries them in the wrong list makes it resolve a tree node and fail (`PassiveAllocation::IsAllocatedLeaf`). That
is why sending them for a jewel the client has no layout for crashes it.

The 0x33 handler (`0x141ef2710`), the node apply/refund functions (`0x141ef69e0`, `0x141ef6bf0`), the id function and
the generator were decompiled with Ghidra (project `Tools/ghidra-projects/poe3293`, `-noanalysis` read-only runs); the
id function is also hooked live by `scripts/re/frida/trace-cluster-ids.js`, which logs `(slot, size, position, id)` for
every expansion node and is the quickest way to check a new shape.

## Code

- `PassiveTree.cs`: `JewelNodeForSlot`, `ClusterNodeForSlot`, `ClusterSlots`, `ClusterParent`, `ClusterCellSize`,
  `IsLargeJewelSlot`.
- `Program.Items.cs`: `ClusterSize`, `ClusterSocketCount`, `ClusterPassiveCount`, `ClusterNodeBudget`,
  `ClusterJewelSocketed`, `JewelFitsCell`, `ClusterSlotOpen`, `PassiveJewelRefusal`, `ClusterRemovalRefusal`,
  `SpendClusterRemoval`, `CreateClusterJewel`, `CreateVoices`.
- `Program.Progression.cs`: cluster jewel nodes in the passive point budget and in 0x33; `ChangeClusterPassive` (which
  also saves the set and refreshes the stats).
- `ClusterJewelLayout.cs`: the client's layout for a jewel in a tree socket. `ClusterJewelStats.cs`: each mapped
  node's stats, and the full-allocation count for an expansion cell. `CharacterSaves.ClusterPassives` keeps the client's ids across sessions.
- The Gateway logs every cluster node allocation/refund and the `passive jewel stats: life N, mana N, A/B cluster
  node(s)` line; the character's chat has no per-node debug line.
- `scripts/dev/jewel_selftest.py`: an end-to-end check on the logged-in client (insert/remove a cluster jewel with no
  node, the small/notable stats of a large cluster on the measured cell-20 layout, a medium cluster in a large one's
  socket, a normal jewel with +80 life in a cluster socket) through `scripts/dev/client_control.py`, whose
  `passive:allocate`/`passive:refund` send the same 0x34/0x36 the client does. It reads the Gateway's
  `passive jewel stats: life N, mana N, A/B cluster node(s)` line. The theme is flat life, so the panel shows more than
  the tooltip's value when the character has increased maximum life (a 7-life small passive was +21 on a +200% build).
