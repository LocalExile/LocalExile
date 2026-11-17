# 陨石迷宫（`meteor_maze_01`，10949 / 10950 / 10951；Action 42185）

## 一句话结论

**客户端是有完整迷宫实现的**：`OrionCircleMazeBlast`（`ActionTypes.HASH16` **0xA4C9 = 42185**）会**自己一环一环造环**，
还会自己造一个名为 **`meteor_incoming`** 的预警对象；环数来自**技能 stat `orion_circle_maze_number_of_circles`（客户端 id 10845 = 我们行 10844）**，
总时长来自 **`base_secondary_skill_effect_duration`（客户端 id 3249 = 我们行 3248，基准 ms）**，
中心来自名叫 **`apparition`** 的实体的位置。

> **stat id 约定（2026-11-17 校准）**：我们 `data/tables/Stats.json` 的行是 0 基，`GrantedEffectStatSets.json` 的 `ConstantStats` 数组里装的是**我们的行号**（已逐个验证语义：1105=物理转火、1107=转雷、1108=转混沌、2748=对召唤物-50%、358=`base_skill_effect_duration`、10844=`orion_circle_maze_number_of_circles`、3248=`base_secondary_skill_effect_duration`）；而**客户端二进制里的 stat id 比我们的行号大 1**（6433→行 6432 `skill_behaviour_variation`、5521→行 5520 `total_skill_area_of_effect_radius_+%_including_final`、349→行 348 `skill_art_variation`、215→行 214 `skill_area_of_effect_+%_`）。引用时一律写「客户端 id = 行号 + 1」。

**我们其实已经把这条路径走了一半**：`Cast(10949/10950/10951)` 发的是
`ActionStarted(actor, …, skillId = 0x4000 + 该 effect 在 GrantedEffectsKeys 里的下标, …)`，
客户端按 GEPL → GrantedEffect → ActiveSkill **1572** → ActionType **1037** 反查，
**动作类会被实例化**（旧文档说"服务端没发 Action 42185、客户端从未实例化"是**错的**）。

那为什么环没出来？三个可疑点——**前两个已经在 2026-11-17 下午查清并排除**：
① ~~属性 id 查错~~ → 已校准（见上方 stat id 约定）：环数 = 客户端 id 10845、基准时长 = 客户端 id 3249、每圈时长 = 客户端 id 3250 `secondary_skill_effect_duration`；
② ~~美术 hash 没匹配上~~ → **已对上**（四个都是 `MiscAnimated` 的 HASH32，见下表）；
③ **中心要一个名字叫 `apparition` 的实体**，我们的分身叫 `AtlasExile5ApparitionDetached`——名字对不上就会退回 `+304` ⇒ **这现在是唯一的可疑点**。

⇒ 现在迷宫的一切（环、墙、出口、预警、伤害）都是服务端自己画的；客户端那套只是**没跑起来**。

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 技能 | `AtlasExileOrionCircleMazeBlast1/2/3` = **10949 / 10950 / 10951**，ActiveSkill 行 1572（ActionType 1037），CastTime **1500**，动画槽 **36 `SpellBuffFire`** → 本体 `.act` `"meteor_maze_start_01"` |
| 动作类 | `OrionCircleMazeBlast`，928 字节，主 vtable `off_1435875A0`，ctor `sub_141D1BE60` |
| 互斥 | 与 `OrionBeamBomb`（HASH16 **13449**）**互斥**：canStart 谓词里看见 13449 就拒绝启动 |
| 环数 N | **技能 stat `orion_circle_maze_number_of_circles`**（客户端 id **10845** = 我们行 10844）→ 客户端内部 `+888`；美术属性按 N=1/2/3/≥4 四选一，**四个都已对上 `MiscAnimated` 行**：`0x5C9AE98E`→行 3779 `AtlasExileOrionMazeMeteor1Ring`→`meteor_100.ao`、`0xF7E94E3B`→行 3780 `…2Rings`→`meteor_150.ao`、`0x798AE0D7`→行 3781 `…3Rings`→`meteor_200.ao`、`0x29D35DDC`→行 3690 `AtlasExileOrionMazeMeteor`→`maze_meteor.ao` |
| 总时长 | `+840` = (基准 + N × 每环时长) / 1000 秒。`GrantedEffectStatSets.json` 行 10910-10913 的 `ConstantStats` = `[10844, 3248, 1105, 1107, 1108, 2748]`、值 `[N, 1000, 25, 25, 25, -50]`（即我们的行：圈数 / `base_secondary_skill_effect_duration` / 转火 / 转雷 / 转混沌 / 对召唤物-50%）；行 10914（5 圈）多一个 `358 = 6000`（`base_skill_effect_duration`） |
| 伤害 | 同行的 1105/1107/1108 = 25/25/25（物理转火/雷/混沌）、2748 = -50（`monster_penalty_against_minions_damage_+%_final_vs_player_minions`）；`BaseEffectiveness` 5.625 / 6.563 / 7.5 |
| 中心 | 准备阶段找名字 **`apparition`** 的实体，取它的位置写进 `+880`；找不到就退回 `+304` |
| 每圈数量 | 效果本身就是「圈数」：10949 一圈、10950 两圈、10951 三圈（各行带自己的 `orion_circle_maze_number_of_circles`） |
| `meteor_maze_01` | 本体 `.ao` 第 **23** 号 clip（`create_maze` @0.461、`contact` @2.006）；**与建环无关**——客户端建环不靠它（推断：这就是上次「Cast 10949 再放第 23 号 clip」失败的原因，Cast 只播动画，不实例化动作类） |

## 客户端自己画什么 / 服务端必须画什么

- **客户端（若我们发 Action 42185）**：环（四个美术属性之一，位置 = 中心）、`meteor_incoming` 预警对象、动作类自己的计时与状态推进。
- **客户端（现在）**：只有 `meteor_maze_start_01` 的抬手动作；迷宫本体一个像素都没有。
- **服务端**：环的**碰撞/墙**、缺口、出口、伤害结算、玩家位移——客户端做的空间查询只用于表现推进，不结算伤害（推断）。

## 当前服务端实现

`Maze()`（`Modules/Core/Game/Monster/Sirus.Combat.cs:349-467`）

1. **中心是玩家，不是 Sirus**（`:375` `mazeCentre = c.Walk.Position(now)`）：官方把玩家关进环里；
   以 Sirus 为心时 44 格的本体站在第一圈内，把墙全遮住（实测 2026-11-15）。
2. 施法：`await Cast(c, id, rings == 1 ? 10949 : rings == 2 ? 10950 : 10951, mazeCentre, now)`（`:379`），
   `rings = Math.Min(3, Round + 1)`。**这一步只播动画，不建 Action 42185。**
3. 时钟：`StartClip = 1.5`、`Contact = 2.006`，`clipAt = now + 1.5`、`collapseAt = clipAt + 2.006`，
   墙站到 `collapseAt`（陨石落地就被拆掉，`wallUntil = collapseAt`）。
4. 玩家被传送进中心 `(mazeCentre.X + 3, mazeCentre.Y + 3)`（`:388-390`）。
5. 每圈：`radius = 12 + ring * 8`、`gap = .7 + ring * 2.15`、
   `wallStep = τ / max(8, round(τ*radius/2.6))`、`openHalf = max(.36, 7.0/radius) + wallStep`。
   - 碰撞：`await Wall(c, at, a + π/2, wallUntil)` 用 `Tutorial_Blocker`（`TriggerableBlockage`，客户端自己关格子），每 **1.6 格**一个。
   - 画面：`Art + "blocking_02.ao"`，`clip: 2`（`loop`；默认 `wait` 没有粒子事件 ⇒ 纯 effect object 看不见），`scale: 1.5`，每 **2.6 格**一个；
     缺口用**同一个** `openHalf` 跳过（画面和碰撞必须一致，否则玩家看得见洞却撞墙）。
   - 出口：`ExitHere.ao`。
6. 预警：`6 + rings * 4` 个 `maze_meteor_incoming.ao`，半径 `10 + rand(rings*15 + 8)`，烧到 `collapseAt`。
7. 中心那一颗：`timedFx.Add((collapseAt - 1.92, Art + "destruction_assets/meteors/meteor_rain.ao", …))`
   ——`meteor_rain.ao` 的 `contact` 在 1.92 s，所以提前 1.92 s 放，落地正好对上 `hits.Add(... 10949/10950/10951, mazeCentre, mazeCentre, 17 + (rings-1)*15)`。
   `meteor_maze_01` 是**故意不播**的：它不建迷宫，但会带一颗自己的陨石，玩家会看到中心被砸两次（实测 2026-11-16）。

## 玩家反馈

| 时间 | 反馈 |
|---|---|
| 2026-11-15 | 「迷宫没有出口，是个封闭的环」→ 画面缺口比碰撞缺口宽一格的 bug |
| 2026-11-16 | 「站在 ExitHere 光圈上出不去」→ 出口从 0.32 rad 改成 7 格净空 + 一格墙距 |

## 客户端实现（反编译）

### 动作类：`OrionCircleMazeBlast`

| 项 | 值 |
|---|---|
| 类名 | `"OrionCircleMazeBlast"` @ `0x143267ce0`（`.rdata` 普通 C 字符串） |
| 工厂 | `sub_14127EAB0`（另有 `sub_141985A00`）：先探测 ctx 属性 876 / 332 / 858 选变体，都不命中才 `sub_142C8CB50(928)` 分配 **928 字节**并调 ctor |
| ctor | `sub_141D1BE60`：`sub_1419E9B10(this, 0xA4C9u, actor, ctx)`（第二参就是这个 Action 的 HASH16）；`*this = off_1435875A0`；`[+168] = off_14323E408`；`[+792] = off_14337C858`；`[+800] = off_14337BDF8` |
| 主 vtable | **`off_1435875A0`**（工厂里覆盖的 `off_14337B8D8` 只是中间表，**不是**主表） |

自有关键函数（**只认地址与行为，槽号不可信**：笔记里的「槽 N」与虚表字节偏移差 2–3）：

| 地址 | 行为 |
|---|---|
| `sub_141D1C420` | 准备：查 `apparition` 定中心（`+880`）、属性 10845 定环数（`+888`）、算总时长（`+840`） |
| `sub_141D1CB40` | 每帧状态机（状态 `+808`） |
| `sub_141D1CFA0` | 每帧计时器：`+820` 累加 dt，≥ `+816` 就造一环，`+824` ≥ `+888` 收尾 |
| `sub_141D1D060` | **造一个环**：按 N 选美术属性 → `sub_141F6A910(施法者, 属性对象, 中心)` → 句柄存 `+896` |
| `sub_141D1D4E0` | **状态推进** 0→1→2→3→4→5 |
| `sub_141D1D430` | canStart：附近 Action 里有 13449（OrionBeamBomb）就拒绝 |
| `sub_141D1D940` / `sub_141D1DAB0` | 序列化 / 反序列化 |
| `sub_141D1BE20` | clone（`sub_142C8CB50(928)` + copy ctor `sub_141D1BF30`） |
| `sub_141D1C640` | 清理：释放 `+896` / `+904`，摘掉 `+912` / `+920` 监听器 |

### 状态机（观察）

`+808` 是 u32 状态，0…5 线性推进（`sub_141D1D4E0`）：

| 状态 | 进入时的动作（观察） |
|---|---|
| 0 | 等 0.5 s（每帧 `+844` 累加 dt）后推进 |
| 1 | 环计时器跑：间隔 **0.2 s**（`+816`），每到一个点造一环（`+824++`），造满 N 环推进 |
| 2 | 造**预警对象**：属性 id **2696282090**，半径参数 = **20 × N**，位置 = 中心，并把它的状态设成字面量 **`"meteor_incoming"`**；同时对中心做一次矩形空间查询（半宽 `20×N + dword_143A6AE58 − 1`），等到 (总时长 − 已用) ≤ 1.0 s 才推进 |
| 3 | **clone 自己**，clone 的 `+849` 置 1（自带位置），并对 clone 造一个环 |
| 4 | 间隔改成 **0.15 s**，释放预警对象（`+904`） |
| 5 | 置 `+290` 的 bit 0x10、注册回调、释放最后一个环（`+896`） |

### 序列化（客户端收到什么）

86 号槽写 / 87 号槽读，布局完全对称（观察）：

```
u32  state      → +808
u8   flag       → +849      （flag≠0 表示「自带中心」）
u32  duration   → +840      （秒）
u32  elapsed    → +844      （秒）
[flag≠0] u32 x, u32 y → +880 / +884   （中心坐标对）
```

后面跟一段通用实体状态（`sub_141DC09A0` / `sub_141DC0BC0`）；state 为 2/3 时还有第二段（第二个实体）。
反序列化里若 flag≠0，客户端**当场造一个环**（虚表 +1264）。

### 命中与伤害

- 客户端在状态 2 对中心做矩形查询（`sub_141D22D80`），但**没有找到伤害结算路径**：命中与伤害仍然是服务端的事（推断）。
- 状态 2 的出口条件里有个字节 `+848`（「可以收尾」），已知代码里从没写过它 ⇒ 一直为 0 时状态 2 出不去（未解）。

### 客户端表现

- 一个环 = `sub_141F6A910(施法者实体, 属性对象, 中心)` 造出来的效果对象，句柄留在 `+896`（只保留最后一个）。
- 造环前会步进线程 LCG（`state = 1103515245·state + 12345`，TLS+368），但**角度不在本函数里算**；位置直接取 `+880`
  ⇒ 每个环都在**中心**，随机性藏在属性对象里（推断）。
- 四个美术属性按**总环数**选（N=1/2/3/≥4），所以一次施法的所有环是同一种美术。
- 状态 1→2 还会造一个 `meteor_incoming` 对象 ⇒ **预警陨石客户端也会画**（服务端不必再画一遍）。
- 官方目录 `Tools/re/out/orion/metadata/effects/spells/monsters_effects/atlasexiles/orion/` 里与迷宫相关的资产：
  `maze_meteor.ao`、`maze_meteor_fall.ao`、`maze_meteor_impact.ao`、`maze_meteor_incoming.ao`、`maze_meteor_loop.ao`、
  `maze_warp.ao`、`blocking_01.ao` / `blocking_02.ao`、`exithere.ao`。

### 服务端该发什么（结论）

激活客户端实现只要发**一个** Action 42185 并带齐属性与字段（清单见文末）：
Action 属性 **10845** = 环数、**3250** = 每环毫秒、**360** = 基准毫秒；载荷 = u32 state / u8 flag / u32 duration / u32 elapsed /（flag=1 时）中心 x,y。
**不需要**逐环下发实体——客户端自己按 0.2 s 一个的节奏造。

### 未解

1. ~~四个美术属性 id 对应哪张 .ao~~ → **已解决（2026-11-17 下午）**：它们就是 `MiscAnimated` 的 HASH32 —— `0x5C9AE98E`=行 3779 `meteor_100.ao`、`0xF7E94E3B`=行 3780 `meteor_150.ao`、`0x798AE0D7`=行 3781 `meteor_200.ao`、`0x29D35DDC`=行 3690 `maze_meteor.ao`。
2. 半径单位：`20 × N + dword_143A6AE58 − 1` 用的是哪套坐标（客户端内部单位 vs 格；1 格 = 10 世界单位），以及 `dword_143A6AE58` 的取值。
3. **`apparition` 是谁（现在唯一的头号嫌疑）**：客户端按名字找的实体，服务端要怎么造它（或用 flag=1 直接给坐标绕开它）。已派子任务在反编译名字解析机制，见 `logs/tmp/re/entname.md`。
4. 字节 `+848` 谁写、状态 2 的真正出口条件。
5. 准备函数算出的 `+840` / `+888` 与服务端载荷里的 `+840` 谁后写（顺序未确认，可能互相覆盖）。
6. 客户端的环**能不能挡人**：客户端造的是效果对象，碰撞仍是服务端的事（墙还得服务端画）。

## 服务端该发什么

| # | 发什么 | 字段 / 取值 | 时机 |
|---|---|---|---|
| 1 | 一个 Action，类型 `OrionCircleMazeBlast`（`ActionTypes.HASH16` **42185**，ActiveSkills 行 1572 / ActionType 1037） | 技能 stat **10844** = 环数 N（10949/10950/10951 ⇒ N=1/2/3，值随 stat set 10910/10911/10912 走） | 施法那一刻，紧跟现有 `Cast(...)` |
| 2 | 同上 | 技能 stat **3248** = 基准毫秒（1000）⇒ 客户端总时长 = (3248 + N×每圈时长)/1000 s（5 圈那行另有 stat 358 = 6000） | 同上 |
| 3 | Action 载荷 | u32 state = 0、u8 flag = **1**、u32 duration = 0、u32 elapsed = 0、u32 x、u32 y = 迷宫中心（格） | 同上 |
| 4 | 伤害 | 仍走服务端 `hits.Add(...)`：落点 = 中心，伤害 = `17 + (rings-1)*15`，触发时刻 = 本体 contact | 与现在一致 |
| 5 | 服务端仍要画的美术 | 墙 `blocking_02.ao`（clip 2 loop、scale 1.5、每 2.6 格）、出口 `ExitHere.ao`、中心陨石 `destruction_assets/meteors/meteor_rain.ao`、传送 `maze_warp.ao` | 与现在一致 |
| 6 | 可以撤掉的美术 | 预警陨石 `maze_meteor_incoming.ao` —— 客户端状态 1→2 自己造 `meteor_incoming` 对象；先截帧比对了再删服务端的 | 发 Action 之后 |

要点：

- **环不要逐个下发**：客户端 0.2 s 一个自己造（`sub_141D1CFA0` → `sub_141D1D060`），服务端只要给环数。
- 墙的半径按客户端的 **20 × N + 3**（`dword_143A6AE58` 若为 4）重画，而不是现在的 12 / 20 / 28——注意这是**整座迷宫一个半径**，不是一圈一个（单位待确认，见未解 2）。
- `OrionBeamBomb`（13449）与迷宫互斥：同一时间只能存在一个。
- 落地顺序：先发 Action 42185 + 实机截帧，确认环和预警长什么样，再决定服务端还要画多少、要不要把半径换成官方的。

## 未解 / 待办（服务端）

1. 发 Action 42185 的最小实现（Action 属性 + 载荷字段）——本文末的表就是规格。
2. 半径改用官方的 20 × N + 3，并确认单位（格 vs 世界单位）。
3. `TraceMaze`（`Sirus.Combat.cs:474-491`）是调试打印，交付前要删。
4. 预警陨石的数量/半径分布目前是估的；等客户端自带 `meteor_incoming` 后可整段删掉。

## 环心的大陨石与坑（2026-11-17 晚实施）

环心不是小陨石（那是星辰雨用的），是**一块非常大的陨石直接砸下、砸出大坑**：

- 预警：`maze_meteor_incoming.ao`（同 rig 的 `incoming` clip，AirRush 音 + `meteor_incoming.pet` 的地面圈），放在环心，活到命中。
- 陨石：`maze_meteor.ao`（`meteor_01` clip，`MazeMeteor/MeteorWhoosh` + `meteor_head.pet`/`meteor_wind.pet`），命中前 1.92 s 起、活过命中 1.2 s。
- 坑：`patches`（10945 的地面伤害区）＝环心半径 `17 + 15×(环数-1)`，持续 8 s —— 玩家困在里面就一直掉血。

**服务端该发什么**：环心的三个效果（预警圈、大陨石、坑）都由服务端发：客户端的 `OrionCircleMazeBlast` 只在动作状态带 flag=1 + 环心时才造环（见下文「迷宫没有环的原因」），而我们从不发那种状态 ⇒ 环心也只能自己画。
