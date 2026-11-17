# 走廊（`beam_barrage_01`，10948 `AtlasExileOrionCorridorBlast`）

## 一句话结论

官方"建走廊"的技能是 **10948 `AtlasExileOrionCorridorBlast`**（ActiveSkill **1571**，CastTime **7500 ms**，
`Animation` **22 = `SpellAreaOfEffect`** → 本体 `.act` 的 `"beam_barrage_01"`）。
那个 clip 里同时装着三件事：`create_corridor` @**0.461 s**、手部 glow 的开关（`addhandglowepk` @0.945 /
`removehandglowepk` @1.425）、以及 **8 次 `beam_right` / `beam_left`**（1.1734 / 1.8246 / 2.4850 / 3.1556 /
3.8177 / 4.4966 / 5.1671 / 5.8124）。走廊的墙和中间那颗火球**客户端都不画**，全部由服务端放。

> 这一条以前被误当成"本体的多段光束"。真正的多段光束是 `beam_03`（8098），见 [body-barrage.md](body-barrage.md)。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 技能 | `AtlasExileOrionCorridorBlast` = **10948**，ActiveSkill **1571**，CastTime **7500**，`Animation` **22 `SpellAreaOfEffect`** |
| 本体绑定 | `atlasexile5.act`：`SpellAreaOfEffect = "beam_barrage_01"` |
| 动作类 | `OrionCorridorBlast`（Orion 系 5 个之一），**挂在自己的 action type 上**；`Cast(10948)` 会真的把它实例化出来（10948 → ActiveSkill 1571 → ActionType 1036 是通的） |
| `beam_barrage_01` 事件 | `cloak_off`/`pointlight_off`/`rocks_off` @0、`teleportstart` @0.100、`teleportend`+`rocks_on`+`pointlight_on`+`eyes_on` @0.400、`create_corridor` @0.461、`addhandglowepk` @0.945、`beam_right` @1.1734 / `beam_left` @1.8246 / `beam_right` @2.4850 / `beam_left` @3.1556 / `beam_right` @3.8177 / `beam_left` @4.4966 / `beam_right` @5.1671 / `beam_left` @5.8124、`removehandglowepk` @1.425、`finish` @6.536 |
| 手部坐标 | `beam_right` 用 `(-60, 0, -104.29)`、`beam_left` 用 `(+60, 0, -100)` 模型单位 ⇒ **左右各 0.6 格、身前 1.0 格** |
| `.ot` 钩子 | `on_event_addhandglowepk` → `AddEffectPack('…/on_act/beam_barrage_01/handGlow.epk')`，`on_event_removehandglowepk` 移除 |
| 终极形态 | `beam_barrage_abort_01/02/03`（`SpellAreaOfEffectFire/Cold/Lightning`），没有 `addhandglowepk` |

## 客户端自己画什么 / 服务端必须画什么

- **客户端**：`beam_barrage_01` 的动作（瞬移、抬手、8 次挥手）和手部 glow 的 epk。
  `create_corridor` 事件本身不建任何东西——**类被实例化了，但它只改状态，不造美术**（见下「走廊的真实结构」）。
- **服务端**：两侧的墙（碰撞 + 画面），以及中间那颗从本体滚向玩家的火球。

## 当前服务端实现

`Corridor()`（`Modules/Core/Game/Monster/Sirus.Combat.cs:494-529`）

```csharp
special = 1; specialUntil = ready = now + 7.5;
var at = RingPoint(c, aim, 42, now);                 // 本体瞬移到玩家外围 42 格
await Teleport(c, id, at, now); await Cast(c, id, 10948, aim, now); await Voice(5678);
corridorEnd = Extend(at, aim, 100);
for (int d = 8; d < 100; d += 6)                     // 每 6 格一段墙，两侧各一
    foreach (int side in new[] { -1, 1 }) {
        await Wall(c, wall, angle, specialUntil);     // Tutorial_Blocker：真碰撞
        await Fx(c, wall, Art + "blocking_02.ao", specialUntil, clip: 2, rotation: angle, scale: 1.5f);
    }
double[] contacts = [1.173, 1.825, 2.485, 3.156, 3.818, 4.497, 5.167, 5.812];
for (int i = 0; i < contacts.Length; i++)            // 8 颗火球沿中线滚过去
    timedFx.Add((now + contacts[i] - .75, Art + "nuke_beam_blast.ao", ball.Item1, ball.Item2, now + contacts[i] + .3, angle));
foreach (double contact in contacts)
    hits.Add(new(now + contact, body.ActionGeneration, ++group, 10948, at, corridorEnd, 7));
```

- 走廊中线 `at → Extend(at, aim, 100)`；两侧墙偏移 **±11 格**（夹角方向垂直距离）。
- 8 颗火球的位置按 `(i+1)/8` 沿中线插值，**每个刚好对上 `beam_right/beam_left` 的那 8 个时刻**，
  美术 `nuke_beam_blast.ao`（`nuke_blast.pet` + 0.06 s 的裂纹与碎石）。
- 伤害：8 条 `Hit(10948)`，`from = at`、`to = corridorEnd`，半径 **7** 的线形判定。

## 玩家反馈

| 时间 | 反馈 |
|---|---|
| 2026-11-16 | 「走廊的墙和红球已经有了」——墙与火球被判为可用 |
| 待办 | 命中模型是"从本体到走廊终点的整条线"（一条 100 格长的判定），而不是跟着火球走的区域 |

## 未解 / 待办

1. **命中语义不对**：现在 8 次 `Hit` 都用同一条 `at → corridorEnd` 线，半径 7 ⇒ 玩家站在走廊里会被打 8 次；
   官方是**火球滚到他身上**那一下才结算。应该改成"每颗火球一个 7 格圆"，圆心随时间移动。
2. `create_corridor` 事件是否在客户端建东西（真空/预告）没有验证；现在墙是我们自己摆的。
3. 墙的间距（6 格）、偏移（11 格）、长度（100 格）都是估的；官方走廊在竞技场里贯穿一侧到另一侧。
4. 本体施法时 `body.LifePhases.DamageTakenPercent = 20`（`:499`），这是"建走廊时本体减伤 20%"的猜测，没有数据依据。
5. 终极的 `beam_barrage_abort_01/02/03` 三个变体完全没有实现。

## 客户端实现（反编译）

**观察 · 身份**：`ActiveSkills.Id = orion_corridor_blast`（1571），`ActionType 1036` → `ActionTypes.json` 的 `OrionCorridorBlast`（Hash 0xA00D = 40973）。类名字符串 `0x143267cf8`；包装体 `_crtInitOnceExecuteOnce("OrionCorridorBlast", sub_141985AE0, …)`；工厂 `sub_141985AE0`：先探测 ctx 属性 `876(0x36C)` / `332(0x14C)` / `858(0x35A)` 选三个变体 ctor，都不命中才 `sub_142C8CB50(824)` 分配 **824 字节**并调 ctor `sub_141D1DE90`。ctor 里 `sub_1419E9B10(a1, 0xA00D, …)` 把 Hash 当类型号写进去，主 vtable **`off_143588048`**、次接口表 `[+168] = off_14323E408`，并清零 `+792/+800/+808/+812/+814/+816`。

**观察 · 主 vtable 有 420+ 槽**（基类 `Actions::Action` 的接口极宽）。这个类自己的方法聚在 `0x141D1D090–0x141D1F2xx`，落在该地址区间的槽：31 `0x141D1DF90`（启动）、35 `0x141D1E1E0`（每帧）、38 `0x141D1D430`、40 `0x141D1E3D0`（动画事件）、86 `0x141D1EC20`、87 `0x141D1EDB0`、96 `0x141D1E660`、138 `0x141D1DE50`、157 `0x141D1E280`、158 `0x141D1E460`、159 `0x141D1E8F0`、160 `0x141D1E9F0`、161 `0x141D1EA00`、163 `0x141D1EFA0`、180 `0x141D19160`、211 `0x141D19020`、250 `0x141D191B0`、262 `0x141D1A520`、263 `0x141D1A780`、378 `0x141D1F220`。
注意：**不能靠两张 vtable 逐槽 diff 找覆盖槽**（`OrionBeamSweep` 的 `off_143587090` 与它槽数不同，diff 会给出 256+ 差异）；靠"函数地址聚簇 + 成员偏移"判断。

**观察 · 槽 31 启动 `sub_141D1DF90`**：先取一个目标对象，把 `{u16 [a1+282], u8 [a1+284], int [caster+136], int [a1+720], u8 [a1+696]}` 打包交给 `sub_1419C73C0`，`sub_1404CD900` 取对象、`sub_1404CE9B0(obj, packed)` 落地。然后：若 `*(a1+792)` 或 `*(a1+796)` 非 0 → `*(a1+820)=1`；否则**从 0 开始以 0.17453294 rad（10°）为步长扫到 2π**，每个角度算一格 `own + (*(a1+304), *(a1+308))`，用 `sub_141D888F0(room, &cell, 0) >= 3`（房间侧是否允许）且目标距离平方 `< 0xE10 = 3600`（**60 格**）来判定，命中就写 `+792 = cell` 并跳出；最后 `+820 = 1`。
⇒ **地刺出现的方向是客户端自己挑的**：在 60 格内、房间允许的角度上取第一个可用的，步长 10°。服务端不该再自己算方向（现在服务端算的那套 `aim` 是多余的）。

**观察 · 槽 40 动画事件 `sub_141D1E3D0`**：读事件哈希 `*(_WORD*)(a2+56)`，分派 `0xA38A` → 虚调 `+1264`（槽 158）、`0xC1A4`（即 `beam_left` 的哈希 0x41A4）→ `+1288`（槽 161，第三参 0）、`0xDBED`（`beam_right`）→ `+1288`（槽 161，第三参 1）、`0xD9CF` → `+1280`（槽 160）；其余转基类 `sub_1419E9D30`。⇒ **`beam_left` / `beam_right` 这两个动画事件是走廊类自己消费的**，`BeamList` 里那 8 次手部光束的时间点（1.173–5.812 s）正是绑定到它们的 —— 视觉还是得有人画（见下）。

**结论（观察）**：`OrionCorridorBlast` **不创建任何可见美术**——它的方法里没有 effect/particle/mesh 的创建者，只做"选方向 + 记状态 + 消费 beam_left/right 事件"。`beam_barrage_01` 的 `create_corridor`（0.461 s）与 8 次 `beam_left/right`（1.173–5.812 s）在客户端只改动状态，**光束必须由服务端下发**；而 `.act` 给 `SpellAreaOfEffect = "beam_barrage_01"` **没有** `effect "…" "animate"` 子句，也证实没有 on_act 美术。

**服务端该发什么**：动画（`beam_barrage_01`）+ 8 次（终极 8 次）光束的 `ServerBeamEffect` + 地刺的伤害判定。

## 走廊的真实结构（2026-11-17 下午，lead 反编译）

**类**：`OrionCorridorBlast`，HASH16 **0xA00D = 40973**，ctor `sub_141D1DE90`（**824 字节**，主 vtable **`off_143588048`**）。
自有槽 37 个（31/35/38/40/86/87/96/138/157–173/176/180/207/211/217/250/262/263/314/329/335–338/378），
其中 **176–338 一段与 `OrionBeamBomb` 共用同一批函数**（`sub_141D18C90` 那套）⇒ 两个类同源，是"竞技场光束"的基类。

**观察 · 槽 31 启动 `sub_141D1DF90`（方向怎么定）**
先把 `{u16 [a1+282], u8 [a1+284], int [caster+136], int [a1+720], u8 [a1+696]}` 打包给 `sub_1419C73C0` / `sub_1404CE9B0` 设成施法目标。
然后分两种：

- **若 `*(a1+792)` 或 `*(a1+796)` 非 0 ⇒ 直接把它当目标格**（服务端可以指定，见「服务端该发什么」第 8 行）。
- 否则客户端自己挑：以 **10°（0.17453294 rad）** 为步长从 0 扫到 2π，每步算一格 `own + ([a1+304], [a1+308])`，
  要求 `sub_141D888F0(room, &cell, 0) >= 3`（房间允许）且与目标 actor 的距离平方 `< 0xE10 = 3600`（**60 格**），
  命中就写 `+792 = cell` 并跳出，最后 `+820 = 1`。

**观察 · 槽 158 `sub_141D1E460`（事件 `-23686` = `create_corridor`）**
原点取 `a1+296` 那个实体的位置；`a1+812 == 0` 时 **`a1+804 / a1+808 = 原点 − BOSS 位置`**（方向向量）。
方向长度 > 0.01 ⇒ `a1+813 = 1`、**计时器 `a1+816 = 0.3 s`**（原始位型 1050253722 = float 0.3）、
**走廊长度 `a1+800 = |方向| + 75 格`**。

**观察 · 槽 161 `sub_141D1EA00`（左右两道）**
动画事件 **`-15964` = `beam_left`（第三参 0）**、**`-9235` = `beam_right`（第三参 1）**
⇒ **`beam_left` / `beam_right` 是动画事件 id，不是 stat**（`0xC1A4` / `0xDBED` 就是它们的有符号 16 位值）。
它从 BOSS 位置到「原点 + 方向 × 长度」做**线段查询** `sub_141D235B0`，命中就调槽 159（vtable+1272）传 **33 / 34**
（左右各一个伤害编号），并**派生一个子动作**放在走廊原点（`v20+304 = BOSS 位置 + [a1+804] / [a1+808]`），
子动作自己再画那道斩击 ⇒ **官方美术 `corridor_slash_01.ao`（MiscAnimated 3692）就是这个子动作画的**。

**观察 · 槽 157 `sub_141D1E280`（每帧）**
计时器到 0 ⇒ 从 BOSS 到走廊远端做**线段查询** `sub_141D22FC0`（半宽 1.0：`-1082130432` = `-1.0f`、`1065353216` = `1.0f`），
命中调槽 159 传 **32** ⇒ **每 0.3 s 扫一次整条走廊**。

**观察 · 槽 159 `sub_141D1E8F0`（结算）**
`a1+814 == 0` 时把伤害包发出去（`sub_141EA0C60`，包体里带 1.0 / -1.0 的半宽），再置 `a1+814 = 1` ⇒ **只结算一次**；
槽 160 `sub_141D1E9F0` 只做 `a1+814 = 1`（不结算）。

**结论（观察）**：**走廊的命中形状 = 一条从 BOSS 指向目标格的线段，长 `|方向| + 75` 格、宽约 1 格，每 0.3 s 扫一次；
左右两道在走廊起点额外各打一次（伤害编号 33 / 34）**。走廊朝哪边**客户端自己挑**（10° 步长、60 格内、房间允许），
要跟官方一致，服务端得自己定目标格并塞进动作状态（和迷宫同一套路：动作的序列化状态）。

原始 dump：`logs/tmp/re/_raw/lead-corridor-1.txt`（槽表 + 启动）、`lead-corridor-2.txt`（槽 157/158）、`lead-corridor-3.txt`（槽 159/160/161 + 线段查询）。

## 官方美术（`MiscAnimated` 行，观察）

`data/tables/MiscAnimated.json` 里与走廊相关的行：

| 行 | Id | AOFile | 是什么 |
|---|---|---|---|
| 3692 | `AtlasExileOrionCorridorNuke` | `Orion/corridor_slash_01.ao` | **走廊的光束/斩击本体** |
| 3814 | `AtlasExileOrionCorridorProjectileExplode` | `Orion/maze_meteor_impact.ao` | **走廊火球的爆点** |
| 3619 | `AtlasExileOrionNukeBeamImpact` | `Orion/nuke_beam_blast.ao` | 核弹光束的爆点（**不是**走廊用的） |

⇒ 我们现在给走廊火球用的 `nuke_beam_blast.ao` **选错了行**：走廊该用 `maze_meteor_impact.ao`（3814）。
走廊那 8 道光束则**完全没画**（现在靠 `ServerBeamEffect` 行 37/227 代替，官方没有 Beam 组件这一步）。

`corridor_nuke.ao`（`AtlasBosses.cs:102` 的 `SirusCorridorArt`）在 `MiscAnimated` 里**没有行**，
是另一套资产（目录里还带一个 `corridor_nuke_ao_pass0.mat`），先别用。

## 服务端该发什么

| # | 发什么 | 字段 / 取值 | 时机 |
|---|---|---|---|
| 1 | 动画 | `Cast(10948)`（skill id = `0x4000 + 2`）⇒ 客户端播 `beam_barrage_01` | 施法那一刻 |
| 2 | 8 道光束 Effect | `EffectObject`，`AnimatedObject = …/Orion/corridor_slash_01.ao`（MiscAnimated 3692），`Parent` = 本体、`Bone` = `R_Weapon`/`L_Weapon` 交替（clip 里 `beam_right` 用 `(-60,0,-104.29)`、`beam_left` 用 `(+60,0,-100)` 模型单位 ⇒ 左右各 0.6 格、身前 1.0 格）、`AttachKind=0`、`AttachFlags=3` | clip 的 8 个事件时刻：1.1734 / 1.8246 / 2.4850 / 3.1556 / 3.8177 / 4.4966 / 5.1671 / 5.8124 s |
| 3 | 火球爆点 | 把 `nuke_beam_blast.ao` 换成 **`maze_meteor_impact.ao`**（3814），位置仍按 `(i+1)/8` 沿中线插值 | 每颗火球到达时（比 contact 早 0.75 s 放，寿命 0.3 s） |
| 4 | 墙 | `blocking_02.ao`（clip 2 loop、scale 1.5）+ `Tutorial_Blocker` 碰撞，每 6 格一段、两侧各一 | 施法后立即 |
| 5 | 伤害 | 改成**每颗火球一个 7 格圆**（圆心随时间移动），不要再用 8 条同一条 `at → corridorEnd` 的线 | 每颗火球 |
| 5b | 走廊本体命中 | 官方是**一条从 BOSS 指向目标格的线段**，长 `|方向|+75` 格、宽约 1 格、**每 0.3 s 扫一次**；左右两道在起点额外各打一次（编号 33/34）。我们的实现目前完全没有这条"扫走廊"的判定 | 每 0.3 s |
| 6 | 地刺方向 | 默认**交给客户端**：动作类自己以 10° 步长在 60 格内挑第一个房间允许的方向（`sub_141D1DF90`） | 客户端自动 |
| 7 | 终极形态 | `beam_barrage_abort_01/02/03` 三个变体还没有实现 | — |
| 8 | 走廊朝向（可选） | **服务端可以在动作状态里指定目标格**：槽 31 启动时若 `a1+792` 或 `a1+796` 非 0，客户端直接用它，不再自己扫方向。要与官方一致就得自己定 | 施法那一刻 |

要点：

- **不要再为走廊发 `ServerBeamEffect`**：官方的走廊光束是效果实体（3692 行），不是被拉伸的 Beam 行。
- 我们的 `Corridor()` 里自己算的地刺方向是多余的（客户端自己挑），但**前提是我们发的 action 真的被实例化**
  —— 走廊的 ActiveSkill 1571 → ActionType 1036 是通的，所以这一条应该是能的（待实机确认）。
- 走廊的墙、长度、偏移目前都是估的：官方走廊在竞技场里**贯穿一侧到另一侧**（未解，见下）。

## 走廊的光球（2026-11-17 晚实施）

走廊中间那颗**大型红色燃烧球**是官方的投射物，不是我们自己摆的效果：

- `data/tables/Projectiles.json` 行 858 `Metadata/Projectiles/AtlasOrionCorridorBlast` → AOFiles `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/death_projectile/death_projectile.ao`，LoopAnimationIds `["proj"]`，ImpactAnimationIds `["impact"]`，ProjectileSpeed 800。
- `death_projectile.ao`：default_animation `proj`（燃烧球本体 + `idle.pet`/`length` 拖尾），`impact` clip 是 `xplod.pet`（爆炸）；网格 `Orion_deathProjectile.sm`。
- 官方爆点美术 = MiscAnimated 3691 `AtlasExileOrionCorridorProjectileExplode` → `maze_meteor_impact.ao`（服务端命中时由非光束分支画）。

走廊本体还有官方斩击：MiscAnimated 3693 `AtlasExileOrionCorridorNuke` → `corridor_slash_01.ao`（rig `corridor_slash_01/blackGuardslash.amd`，粒子 `corridor_slash/lightning.pet` 挂骨组 `line`，音效 CorridorWhoosh/CorridorSlash）——落在**走廊口**（客户端的做法：派生一个子动作放在走廊原点）。

**服务端该发什么**（现实现）：
1. 走廊长度＝BOSS 到目标格的距离 **+75 格**（客户端自己的规则，`Extend(at, aim, Distance(at,aim)+75)`）；墙也跟着铺到那么长。
2. 8 次接触（1.173/1.825/…/5.812 s）：走廊口一道 `corridor_slash_01.ao`；中线上依次 8 颗 `death_projectile.ao`（间隔一段距离 ⇒ 看起来是一颗球在飞）。
3. 伤害：走廊本体是一条**一格宽的线**（客户端每 0.3 s 扫一次、只结算一次，`DrawBeam: false` 不画激光）；真正致命的是光球经过处的 **7 格圆**（`maze_meteor_impact.ao` 的爆点也在那儿）。
