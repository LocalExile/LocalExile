# 陨石雨（"Rain of stars!"，10957 / 10145 / 10141 / 10942）

## 一句话结论

官方一颗陨石 = **客户端的一个对象**：`Meteor_Rain.ao`（`MiscAnimated` 行 `AtlasExileOrionMeteorRain`，
HASH32 **2133148678**（= `MiscAnimated` 3622 的 HASH32；此前文档写的 `0x7F254406` 是抄错，客户端里其实是 `sub_14228BED0(&v16, 2133148678, &v25)`））。这个属性 id 被**写死**在 `OrionMeteor` 动作类里，**每个动作实例只造一个**它，
落在动作的落点上。⇒ **"整片雨"不是客户端自己长出来的**：雨 = N 条动作实例，N 条动作得由服务端一条条发。
现在服务端只发了 1 条 `10145`（ActiveSkill 1449 ⇒ `GeometryTrigger`，而它那一行没有地面标记 ⇒ **客户端什么都不画**），
所以现在**有伤害、没有雨**。

## 本文对旧文档的改正（反编译推翻的几条）

| 旧结论 | 改正 | 依据 |
|---|---|---|
| 「客户端用 `OrionMeteor` 自己建整片陨石雨、下坠轨迹、地面警示圈，**整片雨不需要服务端画**」 | 每个动作实例只造 **1 个**对象；雨是 N 条动作实例 | 槽 157 `sub_141D1F330` 只 new 一次 |
| 「`10141` 的 ActiveSkill 是 959 `GeometryTrigger`」 | ActiveSkill = **1449** `geometry_trigger`；**959 是 ActionType** | `ActiveSkills.json` 1449、`ActionTypes.json` 959 |
| 「`10942` 没有动画槽 ⇒ 客户端不因它画任何东西」 | `10942` 的 ActiveSkill 是 **1570 `orion_meteor`**（ActionType 1035 = `OrionMeteor`），**它正是唯一能造出 `Meteor_Rain.ao` 的效果**；没有动画槽只表示它不带 clip | `GrantedEffects.json` 10942、`ActiveSkills.json` 1570 |
| 「`10141` 实机不放任何东西」；没提 `10956/10957` | 结论对、原因错：不是"因为它是触发器"，而是 dat 行（Id 91）`GroundMarker` 为空；`10957 …Daemon`（AS 1449 / CastTime 2170 / Anim 34 / SkillID 139）才是现在服务端用的那个 | `GeometryTrigger.json` 行 91、`GrantedEffects.json` |
| 「`meteor_shower_01` 的 contact 一到，客户端就建出整片雨」 | 该 clip 只有 contact @0.2404 s + 挥空声 + 手部拖尾，**无粒子**；contact 是"命中时刻"，不是"建雨" | `atlasexile5apparition.ao` |

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 动作类 | `OrionMeteor` = `ActionTypes.json` 行 **1035**，HASH16 **28504 = 0x6F58**；`GeometryTrigger` = 行 **959**，HASH16 **10221 = 0x27ED** |
| ActiveSkill | **1570** `orion_meteor` → ActionType 1035；**1449** `geometry_trigger` → ActionType 959 |
| `10957 …CreateMeteorsSmallDaemon` | AS 1449，CastTime **2170**，Animation **34**，SkillID **139** |
| `10956 …CreateMeteorsSmall` / `10145 …MeteorRectangle` / `10146 …RectangleInfluence` | AS 1449，CastTime 2170，Animation 34，SkillID 134 / 100 / 103 |
| `10141 AtlasExilesOrionMeteorArena` | AS 1449，CastTime 2000，Animation **22**，SkillID 91 |
| `10942 AtlasOrionMeteor` / `10943`、`10944` / `10945 AtlasOrionMeteorGroundEffect` / `10946 …Temp` | AS 1570，CastTime 1000（10942 的 Animation 空、SkillID 1） |
| 动画槽 | 34 = `SpellAreaOfEffectLightning`，22 = `SpellAreaOfEffect`（`Animation.json`） |
| 分身 / 本体 `.act` | 分身：`SpellAreaOfEffectLightning = "meteor_shower_01"`、`SpellAreaOfEffect = "carpet_bomb_01"`；本体同槽是 `beam_barrage_abort_03` / `beam_barrage_01` |
| 美术 | `MiscAnimated` 行 3622 `AtlasExileOrionMeteorRain`，HASH32 **2133148678**（`0x7F254406` 是抄错的）→ `…/AtlasExiles/Orion/destruction_assets/meteors/Meteor_Rain.ao` |
| 焦土 | BuffDefinitions 1449 `atlas_orion_meteor_ground` → GroundEffectTypes 17 `OrionMeteor` → GroundEffects 159 `meteor_ground.ao`（`["loop"]`，结束 `fadeout`） |

### 地面标记（`GeometryTrigger.dat`）—— 陨石族全是空行

| dat 行 Id | 对应 granted effect | `GroundMarker` | `__col3`/`__col4` |
|---|---|---|---|
| 91 / 100 / 103 / 134 / 139 | 10945 / 10942 / 10943 / 10946 / 10946 | **全空** | 60/10000、20/2000、20/2000、12/800、8/800 |

490 行里只有 38 行带 marker，**陨石族一行都没有** ⇒ 走 `GeometryTrigger` 这条路，客户端什么都不画。

## 客户端实现（反编译）

### 类卡片：`OrionMeteor`

| 项 | 值 |
|---|---|
| 工厂 / 字节数 | `sub_14127ED30`（注册包装 `sub_14126EED0`，初始化 thunk `sub_14127ED20` → `sub_14141A240`）/ **824** |
| ctor | **`sub_141D1F120`**（114 B）：`sub_1419E9B10(this, 0x6F58, actor, ctx)`、装 `off_143588AE8`、`+792 = off_14337B358`（监听器表）、`+800 = 0`、`sub_141EC9890(+808,0)`、`+816 = 0` |
| **主 vtable** | **`off_14337C358`**（运行时；ctor 里先装 `off_143588AE8`，工厂结尾覆盖）。同族：BeamBomb `off_1433801B8`、CircleMazeBlast `off_1435875A0` |
| 复制 ctor | `sub_141D1F1A0`（保留 `off_143588AE8`）；clone 槽 `sub_14141A120` 把副本改回 `off_14337C358` |
| 成员 | `+304`/`+308` = **落点**（2 × int32，**格**）；`+800` = byte「服务端给了坐标吗」；`+808` = **陨石实体句柄**（`sub_141EC9890`）；`+816` = 回调 |
| 自有槽（运行时表） | 0 析构 `0x14141A190` / **38** `0x141D1F220` 落点判定 / **86** `0x141D1F720` 序列化 / **87** `0x14141A3F0` 反序列化 / 138 `0x14141A120` clone / **153** `0x141D1F6C0` 激活 / **157** `0x14141AA00` 生成+朝向 / **158** `0x14141AB20` 命中特效 / **159** `0x14141A660` 范围查询+伤害+陨石坑；160 `0x141414890` 析构（只在 ctor 版表） |
| 第二张表 | `off_14337B358` 装在 `+792`＝**动画事件监听器**：槽 9 `0x141D1F630` 收到事件 `0x9B8D`(39821) 时回调宿主槽 159 |

### 状态机

**观察**：`OrionMeteor` **没有通用动作类那种 state/count/index 字段**——`+808` 被 `sub_141EC9890` 存成陨石实体句柄，`+816` 是回调，类里也没有 `+840..+904` 的 float 计时器。它是一条**事件驱动**的链：
网络反序列化 → 生成陨石实体 → 陨石实体自己的动画事件 → 命中结算。

| 阶段 | 进入条件 | 代码 | 做什么 |
|---|---|---|---|
| ① 解析 | 服务端把动作交给客户端 | 槽 87 `0x14141A3F0` | 读 u8 flag 到 `+800`；flag≠0 时读 8 字节 (x,y) 写 `+304/+308`（**格**），**立即**调 `sub_141D1F330` 生成陨石实体并算朝向，最后读共享动画段 |
| ② 激活 | 动作被激活 | 槽 153 `0x141D1F6C0` | 若 `+800 == 0`（服务端没给坐标）就 **clone 自己**（槽 138）→ 副本绑到目标 → 副本 `+800 = 1` → 让副本去生成 |
| ③ 生成 | — | 槽 157 `0x14141AA00` → `sub_141D1F330` | 生成陨石实体（effect id **2133148678**），位置 = `+304/+308` |
| ④ 朝向 | 生成后立刻 | 同上 | `stat 349 != 0` → 朝向 0；否则朝向 = `1.5707964 − 抖动 + atanf(300/距离)`，抖动 ±0.25 rad（LCG） |
| ⑤ 飞行 | 陨石实体动画在播 | 客户端实体自身 | 客户端**不 tick**；落点固定，动画走到 impact 帧才结算 |
| ⑥ 命中 | 动画事件 `0x9B8D`(39821) 到达 | `0x141D1F630`（`off_14337B358` 槽 9） | 三个门槛：① 监听器 `this[8] != 0`；② 事件类型 ∈ {0} ∪ [17,27]；③ 事件 id `== 0x9B8D` ⇒ 调宿主 `*(vtable+1272)` = 槽 159 |
| ⑦ 结算 | ⑥ | 槽 159 `0x14141A660` | 半径查询 → 逐个结算伤害 → 炸陨石坑（见下） |

- **观察**：38 槽 `sub_141D1F220`（266 B）基类前置检查后遍历全局实体表 `dword_1445BF224`，**按落点**比距离找实体
  ⇒ 落点在启动前就定了，本类不自己选点。157 槽是本类**唯一产生可见物**的地方。
- **观察**：159 槽在运行时表里是 `0x14141A660`，ctor 版表里是 `0x141D1F7F0`（**不炸坑**）——同一段逻辑的两个版本。

### 序列化（客户端收到什么）

**观察**：写侧 = 槽 86 `0x141D1F720`，读侧 = 槽 87 `0x14141A3F0`。逐字段：

| 序 | 类型 | 含义 | 条件 |
|---|---|---|---|
| 0 | `u8` | `hasPosition` → `+800` | 必有 |
| 1 | `i32` | 落点格坐标 x → `+304` | 仅 flag≠0 |
| 2 | `i32` | 落点格坐标 y → `+308` | 仅 flag≠0 |
| 3 | `u32` | N = 技能名字符长度（UTF-16 code unit 数） | 共享段，必有 |
| 4 | `byte[2N]` | 技能名字（UTF-16LE） | 共享段 |
| 5 | `i32` | skill id（−1 = 无），客户端据此生成技能实例，scale = 字段 7 | 共享段 |
| 6 | `i32` | **客户端跳过不读**（写侧是动画对象 `+420`） | 共享段 |
| 7 | `f32` | 技能实例的 scale 参数 | 共享段 |
| 8 | `i32` | 动画对象 `+472` | 共享段 |
| 9 | `i32` | 动画对象 `+512/+520` 向量首项 id（空则 −1） | 共享段 |

- **观察**：坐标单位是**格**（世界坐标与报文的换算是 ×10）。写侧顺序 `sub_140E273F0(a2,1)` → u8 `+800` → 非 0 则写 `+304`/`+308` → 取**陨石实体**的动画（`sub_14013E1B0(this+808)`），没有实体就取施法者自己的 → `sub_141DC09A0(anim, a2)`。
- **观察**：读侧 u8 → `+800`；非 0 时读 8 字节成 (x,y) → **就地生成陨石** → 算朝向 → 读共享段。
- 客户端真正收到的第一手是 **0x1ab `ActionStarted`**：施法者格坐标 + **目标格** + skillId + sequence + flags
  （`src/GameServer.Core/Protocol/Instance/InstanceProtocol.cs:278`），按 skillId → granted effect → ActiveSkill → ActionType 选动作类。
- **skillId 的两套编号（易踩）**：`AtlasExilesBoss5` 由专属遭遇驱动（`AtlasBosses.cs:149` `FrameworkDrives` 对该布局返回 false），
  它发布的技能号是 **`Sirus.Native.cs:35` 的 `SkillBase = 0x4000` + 客户端 `GrantedEffectsKeys` 里的下标**；
  `AtlasBosses.cs:184` 的 `0x4020 + index` 是**框架驱动**的那条路（野外/连战变体）用的另一套号，两者不可混用。
  ⇒ **skillId 必须落在本体已注册的那张表里**，否则客户端解析不出来（这正是 10942 在本体上发出 `0x3FFF` 的原因）。

### 命中与伤害

- **观察**：半径 `r = (int)(20 × (stat(5521) + 100) / 100)` **格**（stat 5521 = 0 ⇒ 20 格）；查询矩形 = 落点 ± (r + `dword_143A6AE58`(=5) − 1) / + 5，
  `sub_1400F0CF0(rect, out, 23)` + `sub_141D23BA0` 收集，逐个 `sub_14193F690(this, targetId)`
  （按 `{hash16 = this+282, this[284], this[288], targetId}` 去怪物伤害列表配对，摘掉后经 `*(vtable+560)` 施加伤害），最后 `sub_14193F8C0` 结算。
- ⇒ **命中判定在客户端，伤害数字由服务端发**：客户端只回答"谁在圈里"，每一发的数字仍要服务端逐个发伤害包。
- **观察**：本类**没有 `vtable+1304` 自有槽**（+1304 = 槽 163，基类空桩），命中走 `+560`。
- **伤害数据**：10942 → ActiveSkill 1570 → StatSet 10903 `{ImplicitStats [620 is_area_damage, 22890], ConstantStats [1105,1107,1108] = 25/25/25（物理转火/电/混沌）, BaseEffectiveness 1.875}`。
- `Meteor_Rain.ao` 的 `contact` @ **1.92 s** 是这一颗的命中时刻，服务端应对齐它。
  （旧推断"半径随动作时间线性增长"已被上面的公式取代：半径由 stat 5521 决定，与施法时间无关。）

### 客户端表现

- **一个对象**（观察）：157 槽 `sub_141F6A910(&owner, &attr=2133148678, &pos, mode2)`——
  按**写死的属性 id** 实例化一个客户端对象（不是 Effect 那套 584 B / `Metadata/Effects/Effect`），
  句柄存 `+808`，并注册 16 B 销毁监听器。`2133148678` = `MiscAnimated` 行 3622 `AtlasExileOrionMeteorRain` 的 HASH32
  ⇒ **美术就是 `Meteor_Rain.ao`，服务器换不了皮，只能控制落点**。
- **另外三个客户端 effect id**（观察）：命中特效 **33276957**（槽 158 `0x14141AB20`）、陨石坑 **2775491209**（槽 159，缩放 = 半径）、
  `GeometryTrigger` 地面几何 **3902695543**（缩放 = `(int)(32 × stat / 100)` 格）——**注意：3902695543 = `MiscAnimated` 行 2827 `CascadingTriangleSlamEffect` → `triangleSlam.ao`，是 Synthesis Guardian 的三角砸地，并不是 Sirus 的美术**；它是客户端在没有更好的标记时用的**兜底地面标记**。
  ⇒ **陨石坑由客户端自己炸**，服务端不用再补贴花（此前"坑是服务端自己放的 `meteor_rain.ao` 自带动画"的说法要改：那是我们的临时做法）。
- `Meteor_Rain.ao`（UTF-16LE，3291 字符）自带全套表现（观察）：rig `…/destruction/meteor_rain/rig.amd` + skin `meteor.sm`，
  clip `meteor_01`；`contact` @1.92 s；声音 `MeteorWarning` @0.0（骨 `aux_mid`）、`MeteorWhoosh` @1.5439（骨 `aux_meteor_direction`）；
  粒子 `meteor_incoming.pet` @0.0（bone group `impact`）、`meteor_wind.pet` @0.3464、`meteor_head.pet` @0.3472、`meteor_grd.pet` @1.9166；
  拖尾 `meteor_trl.trl` @0.3464→1.9481；震屏 @1.901 s（0.115 s）。
  ⇒ **预警音、下坠拖尾、落点圈、落地尘、震屏全在这一个对象里**，不是服务端另画的，也不是按 marker 拼的。
- 分身 `meteor_shower_01`（`atlasexile5apparition.ao`）：只有 `contact` @0.2404 s、`AttackWhoosh` @0.7309、
  手部拖尾 `orion_app_handcast_trails.trl` @0.118→1.559，**无粒子** ⇒ 这个 clip 本身不画雨。
  地面焦土也不在动作类里：那是 buff 视觉（`atlas_orion_meteor_ground` → `meteor_ground.`，loop→fadeout）。

### 同族：`GeometryTrigger`（落点标记这条路的全部逻辑）

| 项 | 值 |
|---|---|
| 工厂 / 字节数 | `sub_141284980` →（thunk）**`sub_1412D1E00`**（579 B）/ **968**；无独立 ctor，工厂里就地构造（`sub_1419E9B10(this, 0x27ED, …)`） |
| **主 vtable** | **`off_1434480E8`**（`off_14353A728` 只是中间那一步，工厂结尾覆盖） |
| 成员 | `+792` = 一个 16 字节元素的 vector（工厂先塞一条 `{几何 id = stat 6433 的值, .dat 文件句柄}`）；`+960` = Effect 句柄；`+832` = 「已完成」标志 |
| 覆写槽 | 11 个：0 / 74 / 85 / 86 / 110 / 138 / 142 / 159 / 160 / 161 |

- **`Data/GeometryTrigger.dat` 是什么**（观察）：= GGPK 里的 `data/geometrytrigger.datc64`（113890 B，头 4 字节 `ea 01 00 00` = **490 行**，**纯二进制、0 条字符串**）。
  列定义在 `data/schema/schema.min.json`（48 列）与 `sql/base/client/GeometryTrigger.sql`：列 1 `GrantedEffect`、列 2 `Animation`、
  **列 25 `GroundMarker[]` → MiscAnimated**、列 34 `MiscAnimated`、列 39 `Stats[]`、列 40 `StatValues[]`。
  客户端类初始化 `sub_141515790` 用 **stat 6433** 的值去表里查一行，**命中才注册该行的几何条目，查不到就一段都不注册**。
- ⚠️ **stat 6433 在整个 .datc64 里一次都没出现** ⇒ 客户端走 flag=0 分支时按 6433 查不到行 ⇒ **什么也不放**（观察 + 推断）。
  这就是「`10141` 实机什么也没放」的真正原因，不是"因为它是触发器"。
- 160 槽 `sub_141516100`（启动）：从行 `+115` 长度 / `+123` 指针（元素 16 B）**随机挑 1 个** marker → 造 Effect
  （584 B，`Metadata/Effects/Effect`）→ 套 marker 值 → **随机 yaw 0..2π**（`sub_141E35920`）→ 提交；行 `+131` 非 0 才播 `"start"` / `"loop"`。
- 159 槽 `sub_1415159E0`：收尾播 `"end"`、发事件 **56827**、释放 `+960`。142 槽 `sub_141515B70`：骨骼锚定
  （`"BoneGroups"` / `"frameupper"`）——有些触发器挂在骨头上而不是地面。
- ⇒ **GroundMarker 为空的行，客户端什么都不画**（陨石族全是空行，见上表）。

**`GeometryTrigger` 的序列化（观察）**：u8 `flag` → 若 flag≠0：读 (i32 x, i32 y) → **立刻生成 effect 3902695543、缩放 `(int)(32 × stat / 100)` 格** →
再读 skillId / scale / id 三个 dword → 置 `this[832] = 1`；若 flag=0：走施法者自身动画段 + (x,y) + u32 条目数 + 每条 16 字节的几何条目清单。
⇒ **要让它画东西，就得发 flag≠0 + 坐标这条分支**；flag=0 必然查不到 .dat。

### 服务端该发什么（客户端契约）

1. **一颗陨石 = 一条 0x1ab**，且该动作的 granted effect 的 **ActiveSkill 必须是 1570** ⇒ 客户端建 `OrionMeteor` 类 →
   造 **1 个** `Meteor_Rain.ao`。技能表里现成可用的是 **10945**（`AtlasBosses.cs:172` `SirusMeteorGroundEffect = 10945`）；
   **10942 不在表里**，`Skill()` 给它表外的伪 id ⇒ 客户端解析不出来（**推断**）。
2. **落点 = 该条 0x1ab 的目标格**（`+304`）。6 颗就发 6 条、时间错开 ⇒ "整片雨"由服务端逐条发。
3. **不要指望 10141 / 10145 / 10146 / 10956 / 10957 画出东西**：ActiveSkill 1449 ⇒ `GeometryTrigger` ⇒ 行无 marker。
4. **伤害仍在服务端结算**，时间点对齐 `contact` @1.92 s；**焦土**另发 buff（10945/10946 → `atlas_orion_meteor_ground`）。

### 未解

1. **落点 `+304` 从哪来**：是不是 0x1ab 的 targetX/targetY——卡在"动作实例的 ActionData 从哪填"这一步。
2. **stat 5521 的服务端对应**：`Stats.json` 里 5521 查到的是 `melee_splash_off_hand_only`，与"半径系数"语义不符；
   客户端 stat id 空间与服务端不完全同构（5744 → `total_skill_area_of_effect_+%_including_final`、436 → `base_skill_area_of_effect_+%_` 对得上）。
3. ~~effect id → .ao 文件名的反向映射~~ → **已解决（2026-11-17 下午，逐个核对 `data/tables/MiscAnimated.json`）**：2133148678 = 行 3622 `AtlasExileOrionMeteorRain` → `Meteor_Rain.ao`；33276957 = 行 3699 `OrionGlobalHitEffects` → `Hit_FX.ao`；2775491209 = 行 3623 `AtlasExileOrionMeteorImpact` → `impact_Large.ao`；3902695543 = 行 2827 `CascadingTriangleSlamEffect` → `triangleSlam.ao`（**Synthesis 的，兜底标记**）。
4. **事件 id `0x9B8D`(39821)** 的来源：全部 `.ao` 与客户端表里都查不到（可能是引擎内部编号）。
5. `sub_141D1F220` 的实体类型 `dword_1445BF224` 与距离阈值未定位。
6. `GeometryTrigger` flag=0 分支那 16 字节几何条目的两个 qword 含义未解；`.dat` 48 列里 `__col3..__col47` 语义未解。
7. 159 槽的范围查询是否会上行一条"命中"报文（服务端要不要等它结算）没确认——但**伤害数字本来就要服务端发**，所以不影响做法。
8. **官方落点分布、每波几颗、间隔多少**——数据里没有，只能实机比对。

## 当前服务端实现（只读记录）

`MeteorArena()`（`Modules/Core/Game/Monster/Sirus.Combat.cs:546-578`）：

```csharp
ready = now + 2.17 + 3.2;              // 10957 的 CastTime 2170 ms + 3.2 s 间隔
await Voice(5664);                      // "Rain of stars!"
if (apparition != 0) {
    await Cast(c, apparition, 10145, aim, now);       // AS 1449 ⇒ 客户端什么也不画
    foreach (var p in Present.ToArray())
        await SendWorldRecord(p, apparition, () => InstanceProtocol.AnimationUpdate(apparition, 0, 5, 1));
    for (int i = 0; i < 6; i++) {                     // 6 颗，半径 6+rand*40 格
        double a = Random.Shared.NextDouble() * Math.Tau, r = 6 + Random.Shared.NextDouble() * 40;
        var at = (aim.X + (int)(Math.Cos(a) * r), aim.Y + (int)(Math.Sin(a) * r));
        hits.Add(new(now + 1.5 + i * .32, gen, ++group, 10942, at, at, 12));
    }
} else { await Cast(c, id, 10942, aim, now); hits.Add(new(now + 1.92, gen, ++group, 10942, aim, aim, 12)); }
```

- 分身不在时退回本体放 10942：10942 不在技能表里 ⇒ 这条 0x1ab 客户端解析不出动作（**推断**）。
- 命中循环（`:845+`）里 `hit.Effect is 10942 or 10945` **不画任何服务端效果**（等客户端画），而客户端现在画不出来
  ⇒ 星雨**只有伤害没有画面**。
- 另一处（`:720-734`，本体陨石）服务端**自己**放 `meteor_rain.ao`：
  `timedFx.Add((contact - 1.92, Art + "destruction_assets/meteors/meteor_rain.ao", at.X, at.Y, contact + .2, 0))`
  ——一条陨石 = 一张 `meteor_rain.ao`，起播在命中前 1.92 s，让 clip 自己的 contact 落在命中点上。
  同处注释还记一条 live 观察：cast 10942 会在落点上方罩一个**大蓝壳**，官方没有（`:720-723`）——
  `Meteor_Rain.ao` 的 `impact` bone group 正是预警壳（`meteor_incoming.pet`），但它还带下坠的头部与落地尘
  ⇒ 客户端那颗到底只画到壳、还是我们只看到壳，**未解**。

## 玩家反馈

2026-11-17「陨石+陨石坑已修好」（转阶段交接里确认）；"坑"来自服务端自己放的 `meteor_rain.ao` 自带动画，不是贴花。

## 待办（实机验证）

1. **实机验证 0x1ab → `+304`**：发一条 AS 1570 的动作，看客户端那颗 `Meteor_Rain.ao` 是否落在 0x1ab 的目标格上——
   这一条决定"走客户端"能不能成立。
2. **"大蓝壳"到底是什么**：客户端那颗对象是不是只画到 `impact`（预警壳）就没了？与 `Sirus.Combat.cs:720-723` 对一下。
3. 官方落点分布、每波几颗、间隔多少——数据里没有，只能实机比对。

## 服务端该发什么

1. **走客户端（正路）**：每个落点发一条 ActiveSkill = 1570 的 0x1ab（skillId 取本体技能表里的 **10945** ⇒ `0x4000 + 29 = 0x401D`），目标格 = 落格，
   时间按 `contact - 1.92 s` 错开；6 颗就是 6 条。客户端会自己造 `Meteor_Rain.ao`（预警 + 下坠 + 落地 + 震屏）**并自己炸陨石坑**
   （effect 2775491209，缩放 = 半径）⇒ **服务端不要再补画同一张图，也不要再补坑**。落点分布只能服务端自己铺（客户端不会自动展开一片雨）。
2. **命中范围**：服务端自己按客户端同款半径算（`(int)(20 × (stat(5521) + 100) / 100)` 格，stat=0 ⇒ 20 格；查询余量 ±5），
   **逐个目标**发伤害包；伤害数字用 StatSet 10903 的口径（BaseEffectiveness 1.875，25/25/25 物理转火/电/混沌），时间对齐 `contact` @1.92 s。
   （分身分支现在用的 `1.5 + i*0.32` 没有依据。）
3. **竞技场地面标记必须发 flag≠0 + 坐标**：客户端 `GeometryTrigger` 只有在 flag≠0 时才会生成 effect 3902695543（= `CascadingTriangleSlamEffect` → `triangleSlam.ao`，**Synthesis 的兜底标记，不是 Sirus 的美术**；缩放 `(int)(32 × stat/100)` 格）；
   flag=0 必然按 stat 6433 查不到 .dat 而什么也不放 ⇒ **10145 / 10141 走 flag=0 必然空放**，要画就得改发带坐标的分支。
4. **不要用 10145/10141/10956/10957 来画雨**：它们的行没有地面标记，客户端必然什么也不画；只能当"伤害 + 计时"用。
5. **两套方案别混**：若继续用服务端的 `timedFx` 放 `meteor_rain.ao`（现状），就**不要**再发 1570 的 0x1ab，否则双份。
6. **焦土**要另发 buff（10945/10946 → `atlas_orion_meteor_ground` → `meteor_ground.ao`），或服务端自画贴花。

## 陨石 vs 星辰雨（2026-11-17 晚，用户澄清后定名）

用户原话：「陨石不是star rain，而是一块非常大的陨石直接砸一下，产生一个大坑，而star rain是许多小投射物砸下来」。

| | 陨石（本场的大陨石） | 星辰雨（star rain） |
|---|---|---|
| 画面 | **一块非常大的陨石**直接砸下，砸出**一个大坑** | **许多小投射物**接连砸下来 |
| 官方美术 | `maze_meteor.ao`（MiscAnimated 3690 `AtlasExileOrionMazeMeteor`，rig `blocking_slams/meteor/rig.amd`，网格 `meteormaze.sm`），预警圈是同 rig 的 `maze_meteor_incoming.ao`（3690 旁边的 `…CircleMazeMeteorMarker`，MiscAnimated 3735） | `destruction_assets/meteors/meteor_rain.ao`（MiscAnimated 3623 `AtlasExileOrionMeteorRain`；clip `meteor_01`，**自带 contact @1.92 s**、预警音 `MeteorWarning`、下坠拖尾 `meteor_wind.pet`、落地尘） |
| 出现位置 | 迷宫环心（也是一阶段那颗） | 分身的 `Rain of stars!`（10954/10956），每颗一个落点 |
| 服务端 | 落点前 1.92 s 起 `maze_meteor.ao`，活过命中；命中后地面 8 s 燃烧（10945）＝坑 | 落点前 1.92 s 起 `meteor_rain.ao`，活过命中；命中后地面 8 s 燃烧（10945）＝焦土 |

**服务端该发什么**：延迟投放（新的 `timedFx`：`(At, Model, X, Y, Until, Rotation)`，到点才 `Fx()`）——陨石这类"先出现、后落地"的效果必须晚于施法帧发；`meteor_rain.ao` 自带 1.92 s 的落地节奏，所以提前 1.92 s 放、活过命中即可。`maze_meteor.ao` 没有 contact 事件，落地时刻靠对齐命中帧（现取同样 1.92 s，实机核对）。
