# 四向旋转光束（10973 `AtlasExileOrionBeamBomb`）

## 一句话结论

本体在场地中心爆出四条**持续旋转**的光束，效果是 **10973 `AtlasExileOrionBeamBomb`**（ActiveSkill **1574**，CastTime **3000 ms**，`Animation` **28 = `SpellProjectileUpwardFire`** → stance1 的 `clone_beam_start_01`；终极形态 `…BeamBombUber` = **10974**，槽 **39 = `SpellBuffChaos`** → `clone_beam_start_uber_01`）。

**客户端动作类 `OrionBeamBomb` 自己不画光束**（**观察**）：它的 15 个覆写槽里没有任何 effect/mesh 构造，`+880` 那个"客户端对象句柄"只被释放、从不创建 ⇒ **光束实体必须由服务端下发**。它唯一给出确定数值的动态量是**扫转角速度 10°/秒**（`0.17453292f` rad/s）。客户端自己画的是施法 clip 上的东西：`clone_beam_start_01` 挂了 **4 条臂/手拖尾**（`cloneBeam.trl`）⇒ 官方"四臂"= **客户端拖尾（仅普通形态）+ 服务端光束**两层叠加；Uber 的 clip 没有拖尾，光几乎全靠服务端。

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| GrantedEffects | `AtlasExileOrionBeamBomb` = **10973**（1574 / 3000 / Animation 28）；`…BeamBombUber` = **10974**（1574 / 3000 / 39）；`…BeamBombKitavaGauntlet` = **10975**（无动画） |
| ActionType / ActiveSkill | `ActionTypes.json` 行 **1039** `OrionBeamBomb`，**HASH16 = 13449 (0x3489)**；ActiveSkill **1574** `orion_beam_bomb`（Types [1,7,6,71,30,27,41]） |
| `.act` 绑定 | `atlasexile5.act`：槽 28 `SpellProjectileUpwardFire = "clone_beam_start_01"`（**无 effect 子句**）；槽 39 `SpellBuffChaos = "clone_beam_start_uber_01"` |
| clip 事件 | `clone_beam_start_01`：`contact` @0.5 / 0.566667 / 0.638597 / 0.728070 / 1.45，`teleport` @0.5；rocks·pointlight·eyes off @0、on @0.55125。Uber：`contact` @0.82999 / @0.94344（**接触点坐标未取**） |
| clip 拖尾 | `clone_beam_start_01` **4 条** `Metadata/Particles/monster_effects/AtlasExiles/Orion/on_act/beam_03/cloneBeam.trl`（`L_arm_FX`/`R_arm_FX` 0.144→0.5221/0.5228；`R_hand_FX`/`L_hand_FX` 0.65775→1.38）；Uber **无拖尾** |
| 光束美术行 | `MiscBeams` **240** `AtlasExileOrionBeamBombBeam` → `Orion/orb_beam.ao`（`__col2` 700）；**239** `…ChargeBeam` → `Orion/fusion_beam.ao`（900）；`MiscAnimated` 3774 `…Explosion` → `Orion/maze_meteor_impact.ao` |
| 状态时长 / 伤害 | StatSet 10934（下列数字是**我们 `Stats.json` 的行号**，客户端 id = 行号 + 1）：358 `base_skill_effect_duration` = **13000 ms**；3248 `base_secondary_skill_effect_duration` = **5000 ms**；1105 物理→火 **25%**；1108 物理→混沌 **25%**；2748 对玩家召唤物 **-50%**；BaseEffectiveness 0.325（10936 = 0.40） |
| debuff | BuffDefinitions **1525 `orion_beam_bomb_slow`**：每层「受到伤害 +10%、速度 -6%」，**BuffLimit 8**；视觉 1268（`Art/2DArt/BuffIcons/CorruptionDegenDebuff.dds` + `player_aura.epk` / `essence_corruption.epk`） |

客户端数据表全库搜 `beambomb` 只命中上列几行 —— **两个 arena object 模板不在客户端表里**（见下）。

## 客户端自己画什么 / 服务端必须画什么

- **客户端**：施法动画（3 s 蓄力，`clone_beam_start_01`）＋ 4 条臂/手拖尾（`cloneBeam.trl`，普通形态）。`.ot` 里 10973 对应的钩子**确实存在**：`on_clone_beam_start_01 → Call( start_clone_teleport )`、`on_event_teleport_for_clone_beam_start_01 → Call( do_clone_teleport )`、`on_end_clone_beam_start_01 → Call( finish_clone_teleport )`，另有 `preload_buff_visual = "orion_beam_bomb_slow"`。
- **服务端**：**四条旋转的光束本体**（动作类不画，`+880` 句柄只释放不创建）、旋转角速度、命中判定、以及每层的 `orion_beam_bomb_slow`（buff 1525）。终极形态（10974）客户端 clip 没有拖尾 ⇒ 服务端要补的更多。

## 当前服务端实现

`Modules/Core/Game/Monster/Sirus.Combat.cs`

- 施法（`QuadBeam`，`:531-541`）：
  ```csharp
  await Voice(5663); var at = Position(now); await Cast(c, id, 10973, at, now);
  double start = now + Attack(c, body!.Variety!, body.Level, 10973).Seconds;
  double until = start + Stat(c, 10973, body.Level, "base_skill_effect_duration") / 1000d;
  quads.Add(new(at, start, until, Random.Shared.NextDouble() * Math.Tau));
  ```
- 四臂（主循环 `:911-937`）：角速度 **0.32 rad/s**、臂长 **80 格**、美术 `Art + "clone_trap_beam.ao"`、按 `a + π/2` 旋转；命中 `KitavaActTen.LineDistance(...) < 3`；叠 buff 1525（最多 8 层）。`:1011` `BeamBombBeam = BeamRow("AtlasExileOrionBeamBombBeam", 240)`；`:326` `kind == 4 → QuadBeam`。起手位置 = 本体当前位置（`Position(now)`），不是场地中心。
- ⚠️ `:536-538` 的注释「OrionBeamBomb (10973) draws its own core and its own four arms (AtlasExileOrionBeamBombBeam = orb_beam.ao) and keeps them for the skill's duration」**与反编译冲突**：动作类不画任何东西，`orb_beam.ao` 是 `MiscBeams` 行 240 供**服务端拉伸**的光束美术。

## 玩家反馈

| 时间 | 反馈 |
|---|---|
| m00790 | 在多选题里先选了"四向光束"（kind 4），随后更正为"念 Die 之后的多段光束"（kind 1） |
| m00836 | 「普攻变成十字激光」——其实是环境变量 `SIRUS_ONLY_KIND=4` 把整个循环钉成了这个技能，不是代码改动 |

## 未解 / 待办（产品层面）

1. **角速度要改**：客户端是 **10°/s = 0.17453 rad/s**（一圈 36 s），现在的 0.32 rad/s（19.6 s/圈）没有依据。
2. **臂长 80 格、命中半径 3 格**全是估的，客户端数据里没有对应字段。
3. **四臂的官方画法**未定：普通形态客户端已经画了 4 条拖尾，服务端再叠同样的臂拖尾就是"画两遍"；`clone_trap_beam.ao` 是克隆体 Die Beam 的粒子件，只是权宜。
4. **终极形态未区分**（10974 + 槽 39，BaseEffectiveness 0.40）。
5. **两个 arena object 模板由谁放**未定；`SirusFight.cs:202-210` 的 `Bomb(...)` 注释说 10973 是"玩家脚下的 doom geyser"——同一个 effect id 两种语义，哪一个是官方行为未确认。`Sirus.Combat.cs:536-538` 的错误注释要改。

## 客户端实现（反编译）

> 原始笔记：`logs/tmp/re/OrionBeamBomb.md`（163 行）。**观察** = 反汇编直接看到；**推断** = 由调用关系推出。

### 类卡片

| 项 | 值 |
|---|---|
| 类名 / HASH16 | `OrionBeamBomb` / **0x3489 (13449)** |
| 工厂 / 分配 | `sub_14127E960` →（thunk）**`sub_1412D0520`**（550 B，876/332/858 三分支）／**992 字节**（`sub_142C8CB50(992)`） |
| ctor | 无独立 ctor，就地构造 `sub_1419E9B10(v9, 0x3489u, a1, a2)` |
| vtable | 主表 **`off_1433801B8`**；`+792` → `off_14337DAE0`；`+800` → `off_14337DA80`；中间表 `off_1435885C8`（写完即覆盖） |
| dat 表 / ctx 属性 | **无** dat 表（ctor 里没有 `Data/` 字符串）；**不吃 ctx 属性**（无 `sub_141940AA0` + 属性号调用，只有基类通用 2322/48/12674/876/22504） |

兄弟表 `OrionCircleMazeBlast` = `off_1435875A0`、`OrionMeteor` = `off_143588AE8`：**槽号可互参，函数地址必须单独看**。成员（全部清零，**观察**）：`+808` 状态（0/2/3/4）· `+812` word 附近计数 · `+864` 动画时钟时间戳 · `+872` 主倒计时 · `+880` 客户端对象句柄 · `+896` 第二倒计时 · `+900` 旋转角。覆写槽（首块 0..164，代码簇 `0x141D18xxx–0x141D1Axxx`）：0 删除析构 `sub_141D18B30` · 4 析构 `sub_141D19160` · 31 扫描附近实体 `sub_141D18C90` · 35 每帧状态机 `sub_141D19020` · 41 状态迁移 `sub_141D18DA0` · 74 释放 +880 `sub_141D191B0`（**推断**）· 86 序列化 `sub_141D1A520`（**推断**）· 87 反序列化 `sub_141D1A780`（**推断**） · 138 clone `sub_141D18AF0`（`sub_142C8CB50(912)`）· 153 启动 `sub_141D18EA0` · 159 周期动作 A `sub_141D19710` · 160 收尾 `sub_141D19BC0` · 161 中间阶段 `sub_141D19F10` · 162 周期动作 B `sub_141D1A130`。

### 状态机

`+808` 取值 0/2/3/4。**state 2 = 蓄力**（**观察**）：`+872` 倒计时，到点后每帧调槽 159（周期动作 A）；启动时若 `+812 == 0`（附近没有带 tag `0x9B8D` 的实体）**直接置失效**，提前结束。**2→3 = 发射**（**观察** + **推断**）：槽 41 把 `+808` 改成 3，内部 `sub_141D19910()` 算这次光束的目标位置/朝向，然后给实体发「事件 29」（`struct { int tag=29; … }` + `sub_141EA0C60(*(qword*)(a1+296), &args)`）⇒ **推断**：这是让怪物自己播一个动作/特效的实体级事件；**本类不 new 任何对象**。**state 4 = 旋转**（**观察**）：

```c
case 4:
    *(float*)(a1+900) += dt * 0.17453292f;   // 10°/s（0.17453 = 10π/180）
    if (*(qword*)(a1+896) <= 0) {
        vtable[1296](a1);                    // → 槽 162
        *(qword*)(a1+896) = *(qword*)(a1+872);
    } else *(qword*)(a1+896) -= dt;
    if (*(qword*)(a1+896) <= 0) { *(dword*)(a1+290) |= 0x10; vtable[592](a1); }  // 失效位 + 通知结束
```

`+290 |= 0x10` 是基类的"失效"位（与其它 Action 一致）。**3 → 4 的跳转点未定位**。

### 序列化（客户端收到什么）

- **观察**：86 槽 `sub_141D1A520`（1222 B）与 87 槽 `sub_141D1A780`（1222 B）长度互为镜像、后者是 reader ⇒ **推断**为一对序列化/反序列化函数；**具体字段布局没有解出来**。
- **观察**：`+880`（qword，客户端对象句柄）在本类里只被释放（槽 4/74 的清理路径），**从不创建**；槽 138 clone 重新分配 **912** 字节（≠ 992），克隆体比本体小。
- **结论**：只发 Action 本体 = 客户端只有计时和转速，**屏幕上没有光束**。要让画面出现，必须同时把"光束/弹"实体发给客户端，并把它的句柄塞进本 Action 的序列化（86 槽）。服务端不发实体时客户端**什么都不会出现，也不会崩**（`+880` 为 0 时释放路径直接跳过）。

### 命中与伤害

- **观察**：客户端动作类**不做伤害**。槽 31 只做两件事：把实体动画时钟 `*(ent+152)+660` 存进 `+864`；遍历附近实体，`if (*(_WORD*)(x+56) == 0x9B8D) ++n;` → `*(_WORD*)(a1+812) = n`。
- **观察**：常量 **`0x9B8D` (39821)** 是本类唯一的外部依赖。它**不是** `hash("release")`（那是 0x0746）——旧笔记把两者当成一回事是**已被证伪的推测**（`Tools/re/out/sirus/notes/beam-plan.md`）：0x9b8d 在全游戏怪物 .ao 6191 个事件名、人物 .ao 798 个事件名、Stats 表 23362 个属性名里**全部 0 命中**。**推断**：它是框架内部的一个伪标签，语义大概是"可被光束锁定/可挂光束的实体"。
- **观察**：伤害数据全在 granted effect 的 StatSet 里（见「客户端数据」表）；客户端只负责播动画和命中窗口，结算一律在服务端。debuff 是 BuffDefinitions **1525**，每层 +10% 受伤、-6% 速度，**上限 8 层**，每层时长由 `base_secondary_skill_effect_duration` = 5000 ms 决定。

### 客户端表现

- **观察**：动作类 15 个覆写槽里**没有** `Metadata/Effects/Effect` 字符串、**没有** `sub_142C8CB50(584)`（Effect 分配）、**没有** `sub_141F6E890`（挂实体）、**没有** `sub_141F6A910`（按属性造对象）⇒ **它不造任何可见物**。全客户端造可见物只有这两个入口：`Effect`（584 字节实例，资源入口固定串 `Metadata/Effects/Effect`）与 `sub_141F6A910(owner, attrId, pos, mode2)`；本类两个都没碰。
- **观察**：两个 arena object 模板（**谁放它们未解**）：`Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBomb.ot`（`Animated { animated_object = "Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/beam_core.ao" }`，`on_end_start → PlayAnimation(idle)`、`on_end_end → Delete()`；`no_serialise = true`、`object_size = 0`、`blocking = false`）与 `…/OrionBeamBombClone.ot` → `Metadata/Monsters/AtlasExiles/AtlasExile5_BeamClone.ao`（与本体同 rig，**无 TrailsEffects/ParticleEffects**，default_animation `trap_beam_01`；出生时播 `teleporting/orion_teleport_movement.ao` + 三个 epk，`fade_out` 态后 `Delete()`）。`beam_core.ao`（3330 B）是**被两端点拉伸的"芯"**（骨骼组 `root`/`aux_line`，粒子 `beam_core/idle.pet` + `start_once.pet`，声音 `…/Sirus/Effects/Beam/Core.loop.ogg`），**全 t=0 无 contact** ⇒ 伤害仍由服务端算。

### 服务端该发什么（结论）

- 动作类只提供计时与 10°/s 转速 ⇒ **光束实体必须服务端发**（`ServerBeamEffect` 或 arena object），句柄进 Action 序列化。
- 伤害判定完全在服务端；客户端侧唯一的反馈是"附近带 `0x9B8D` 标签的实体数"。

### 未解（反编译层面）

1. `0x9B8D` 到底是什么标签（名字解不出，"release" 已被证伪）。
2. `+880` 指向的对象由谁创建、是什么类型；86/87 槽的序列化字段布局（两个 1222 B 的函数未逐行解）。
3. state 3 → 4 的跳转点未定位；`+872` 在 state 4 里的确切含义（周期长度）未确认。
4. 「事件 29」客户端收到后做什么未验证；槽 31 的"附近列表"半径未知。

## 服务端该发什么

1. **施法**：对玩家目标发 granted effect **10973**（普通）/ **10974**（终极）；客户端据此播 `clone_beam_start_01` / `clone_beam_start_uber_01`。CastTime **3000 ms**；起手位置 = 本体当前位置（`Position(now)`）。现有的 `Cast(c, id, 10973, at, now)` 保留。
2. **光束实体（必须，四条各一条）**：`ServerBeamEffect`（`InstanceEntities.BeamEffectObject`，客户端解析 `0x1416710c0`）：`U16 row+1` = **240**（`AtlasExileOrionBeamBombBeam` → `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/orb_beam.ao`，`__col2` 700 ≈ 7 格宽），蓄力期用 **239**（`…ChargeBeam` → `Orion/fusion_beam.ao`，900）；起点 `I32 x, I32 y`（世界单位 = 格 × 10）＋ `U32 startObject`（挂本体）＋ **骨骼名**（标志 `0xD7`；**不给骨骼起点会贴地**）；终点 `I32 x, I32 y` ＋ `U32 endObject`，终点 = 起点 + 臂长 × (cos a, sin a)，`a = 起始角 + i·π/2`（i = 0..3）。备选（未解）：改用 arena object `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBomb.ot`（内含可拉伸的 `Orion/beam_core.ao`）——但谁放它、客户端能否由服务端生成，都还没确认。
3. **旋转**：角速度 **10°/s = 0.17453 rad/s**（客户端唯一有数据依据的量，一圈 36 s）。当前代码的 0.32 rad/s 要改；起始角随机即可。
4. **时长**：`base_skill_effect_duration` = **13000 ms**（从数据读，不写死）；光束从「施法 + 3000 ms 蓄力」起算。
5. **伤害**：命中由服务端算（四条臂各一条线段，玩家站进臂里即命中；当前半径 3 格是估的，没有数据来源）；每层 debuff = BuffDefinitions **1525**：每层 **+10% 受伤、-6% 速度**、**最多 8 层**、每层时长 `base_secondary_skill_effect_duration` = **5000 ms**；伤害转换 **25% 物理→火 + 25% 物理→混沌**（1105/1108）、对玩家召唤物 **-50%**（2748）、BaseEffectiveness 0.325（10934）/ 0.40（10936）。官方在 clip 上的 5 个 `contact`（@0.5 / 0.566667 / 0.638597 / 0.728070 / 1.45）是接触窗口，坐标未取 ⇒ 先按服务端自己的判定，之后再对齐。
6. **不要重复画**：普通形态客户端已经有 4 条 `cloneBeam.trl` 臂/手拖尾（只在施法 clip 内），服务端再叠同样的臂拖尾就是"画两遍"；终极形态（Uber）客户端没有拖尾，服务端必须补齐。
7. **终极形态**：用 10974 + 槽 39（`clone_beam_start_uber_01`）并区分参数；当前实现完全不区分。
8. **不要指望动作类替我们画**：客户端拿不到实体句柄时只是什么都不显示，不会报错；反过来也别把 `+880` 之外的字段当成必需。
