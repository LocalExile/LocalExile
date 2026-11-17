# 分身双手激光（10941 `AtlasOrionBeamSweep` + 8614/8615 `move_daemon`）

## 一句话结论

分身（`AtlasExile5Apparition`）的双手激光在官方实现里是**两段拼接**：

1. 技能本体 = 10941 `AtlasOrionBeamSweep`（客户端动作类 `OrionBeamSweep`，ActionType 1034）——它**画不出任何光**（`state` 永远不会被置 1，见 §3.2）；
2. 两个 `AtlasExiles5BeamTargetDaemon` / `…Daemon2`（MonsterVarieties 8387/8388）由 **GE 8614/8615 `AtlasExileBoss5BeamTargetDaemon1/2`** 拉起（ActiveSkill 1174 `move_daemon`，ActionType **758 `MoveDaemon`**）——`MoveDaemon` **只把这两个不可见 daemon 按 dat 行 + ActionData `+496` 的偏移平移并转向（±90°）**，自己不造任何可见物。

⇒ **两束光既不是 `OrionBeamSweep` 画的，也不是 `MoveDaemon` 画的，更不是 `ServerBeamEffect` 画的。**
官方光束来自 daemon 实体自身（`InvisibleFire.ao` 的 `cyl`/`mid` 骨组 = 光束筒骨架），而那份 daemon 模型
`Metadata/Monsters/Daemon/Daemon.ao` **没有被抽出来** ⇒ 光束本体仍属未解。

> **更正**：旧版本文写"分身激光由 `ServerBeamEffect` 画"。那是**服务端当前的替代实现**（`Sirus.Combat.cs`），
> 玩家连续否掉三次；官方路径不是它。
> **更正**：`MoveDaemon` 的 HASH16 是 **35726 = 0x8B8E**（旧笔记写 35982，**错**）。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 技能本体 | GE **10941** `AtlasOrionBeamSweep` → ActiveSkill **1569** `orion_beam_sweep`，CastTime **4200 ms**，`Animation` **51 = `HeavyStrike`**，StatSet1 10902 |
| 同族 | 10967–10972 `AtlasOrionBeamSweepNoFinalExplosion{Short,Medium,Long}{,Uber}` → 槽 15 = `DoubleStrike`（分身里是 `double_beam_hands_small_01`）/ 槽 51 |
| 谁能放 | GE **10941** 只出现在 8378 `AtlasExile5Apparition` 的 `GrantedEffectsKeys` 里（8379 `AtlasExile5ApparitionDetached` 不含 10941，它带的是 10967–10972）；`AtlasExile5ApparitionDaemon`（8383）的 GE 是 [10141, 10945, 10957, 10946, 10142, 10143]（陨石场地类），**与光束无关** |
| 两个 daemon | GE **8614** `AtlasExileBoss5BeamTargetDaemon1`（SkillID **53**、StatSet1 8603）、**8615** `…Daemon2`（SkillID **54**、StatSet1 8604）→ ActiveSkill **1174 `move_daemon`**，CastTime 1000，**`Animation` 为空** |
| `move_daemon` | `ActiveSkills.json`：`ActionType 758`、`ActiveSkillTargetTypes [1,2,4]`、`ActiveSkillTypes [21,31]`、`SkillTotemId 22`、`IsManuallyCasted true` |
| `MoveDaemon` 类 | `ActionTypes.json` 行 758 → `HASH16` **35726 = 0x8B8E** |
| daemon 怪物 | `MonsterVarieties.json` 行 **8387** `Metadata/Monsters/InvisibleFire/AtlasExiles5BeamTargetDaemon` / 行 **8388** `…Daemon2`：`MonsterTypesKey 1232`、`MovementSpeed 18`、`ObjectSize 1`、`ModelSizeMultiplier 100`、`Name "Invisible"`、`ACTFiles [Metadata/Monsters/InvisibleFire/InvisibleFire.act]`、`AOFiles […/InvisibleFire.ao]`、`BaseMonsterTypeIndex …/InvisibleFireMoveDaemonOrion` / `…Orion2`、`GrantedEffectsKeys []`、`AISFile NoAi.ais` |
| dat 行 | `data/tables/MoveDaemon.json`（103 行，35 列 `__col0..__col34` + `MonsterVarietiesKeys`）：**第 52 行** id=53 → MonsterVarieties **8387**；**第 53 行** id=54 → **8388**；两行 `__col5` = **+90 / −90**（全表为 15 的倍数角）、`__col11` = 0、`__col25` = true、`__col27` = 3 |
| 分身 clip 事件 | `atlasexile5apparition.ao` → `double_beam_hands_01`：`daemon1` @0.91027 (−559.025, −160.465, 0)、`daemon2` @0.91027 (+559.0, −160.465, 0)、`contact` @0.91027、`stop_beam` @2.72873、`blast` @3.43070 (0, −753.937, 0)。`daemon1`/`daemon2`/`stop_beam` **全 orion 树只在此一个 `.ao` 里出现** |
| 事件类型 | 三个事件都是 `StandardEventType`，**没有 data 字段**（观察）——"客户端按 `daemon` 关键字自动挂实体"没有证据 |
| 分身 rig | `Tools/re/out/ggpk-rig/art/models/monsters/atlasexiles/theorion/apparition/rig.ast`（268 231 B）含 **`L_Weapon` / `R_Weapon`**、L/R_wrist_jntBnd、hip_jntBnd；**不含** `shoulder_l`/`l_arm`/`arm_r` |
| 分身 `.ot` | `atlasexile5apparition.ot`：`define_shared_state = "beaming; x_pos; y_pos; z_pos;"`；`on_double_beam_hands_01 = { PlayAttachedEffect('…/Orion/apparitionSurge.ao', hip_jntBnd); }` |
| `apparitionSurge.ao` | `SkinMesh { }` 空、骨组只有 `PTEmit false aux_mid aux_line`、粒子 `surgeEmit.pet` @0.0、`Lights` 空 ⇒ **腰间一团光，不是光束** |
| daemon 美术 | `InvisibleFire.ao`：`SkinMesh { }` 空；骨组 `root`、`frameupper`、**`cyl`(R_jnt01 R_jnt02)**、**`mid`(R_jnt01 R_jnt03)**；`ParticleEffects`/`Lights`/`SoundEvents` 全空 |
| daemon 脚本 | `invisiblefire.ot`：`collision_hull_type "projectile"`、`blocking false`、`base_cannot_be_damaged 1`、`untargetable_by_monster_ai 1`、`corpse_usable false`、`Animated{animated_object "Metadata/Monsters/Daemon/Daemon.ao"}`、`DiesAfterTime{}`、client `Targetable{no_highlight true; targetable false}`；`invisiblefiremovedaemonorion.ot` / `…orion2.ot` = `extends InvisibleFire` + `DiesAfterTime{}` + `Positioned{serialise_position = true}`（**无 client 块**） |

`±559` / `−160.465` / `−753.937` 都是**模型单位**（100 = 1 格）⇒ 两个 daemon 相距 11.2 格、在身前 1.6 格；`blast` 在正前 7.54 格。

---

## 客户端实现（反编译）

### 3.1 `MoveDaemon`（ActionType 758）—— 只摆位，不画东西

**类卡片（观察）**

| 项 | 值 |
|---|---|
| 类名 / 类型号 | `MoveDaemon` / **0x8B8E (35726)** |
| 工厂 | `sub_1412B3F90` → thunk **`sub_1412D6A80`**（410 B，876/332/858 三分支） |
| 分配 / ctor | **808** 字节；`sub_1419E9B10(v9, 0x8B8Eu, a1, a2)` |
| 主 vtable | **`off_14356DC78`**；类型名串 `Utility::TypeIDImpl::PrettyName<class Loaders::MoveDaemon>` @ `0x14356f5c0` |
| dat 表 | `Data/MoveDaemon.dat`（19 字符）→ 缓存 `sub_141CC1550` → `sub_141CC15F0` → 行选择 `sub_142257C90(v10+792, id, &row)`，`id = (**ctx)(ctx, 6433)` |
| dat 行句柄 | 存于 `+792`（**不是** vtable） |

**覆写槽（观察）**：153 `sub_141CBC0A0`（启动）、314/315 `sub_141CC17A0`（`*a2 = off_14356E648`）、317 `sub_141CC1790`（RTTI 描述符，名字含 `CreateStampedeObject_MTXQuicksilverBestiaryStampede_Actions` ⇒ 与 stampede 同源）、351 `sub_141CBDFF0`（平移 + 转向）。基类槽：85 `sub_141245F80`（`*(u8*)(a1+286)==0`）、86 `sub_1419CF9B0`（写 1 字节 `+472`）、87 `sub_1419CFA20`（读 1 字节 `+472`）。

**状态机（观察）**：本类没有自己的状态机；`+472` 这 1 字节是它唯一的状态。

**序列化（客户端收到什么）**：**只有 1 个字节**（槽 86/87 读写 `+472`）。位置、目标、资源**都不在包里**——全部由本地 dat + ActionData 推出。

**槽 153（启动）**：dat 行 `+76` 字节 ≠ 0 才动；`*(qword*)(a1+304) = *(qword*)(*(qword*)(ent+152)+660)`（`entity+152` = 变换组件，`+660` = **2×int32 世界坐标**，不是时间戳）；**ctx 属性 13701 必须 == 0**，否则整个 MoveDaemon 空操作；随后 `sub_141E355A0(anim, a1+304)` 用位置设置动画。

**槽 351（平移 + 转向）**：偏移来自 **ActionData `+496`**（两个 int，`sub_140DF5100`）；实体镜像标志 `entity+485 & 0x10` 时 **X 取反**；目标 = 当前变换位置 + 偏移；有位移时 `sub_140E2DD60()` 算角、`sub_141E35920(xform, ang)` 设朝向（与 GeometryTrigger 随机 yaw 同一函数）。函数内**没有** `sub_142C8CB50` / `sub_141F6A910` / Effect。

**命中与伤害**：**无**。MoveDaemon 不产生伤害、不产生命中事件。

**客户端表现**：**零**。全客户端只有两个"造可见物"入口 —— ① `Effect`（584 字节实例，资源入口固定串 `Metadata/Effects/Effect`）② `sub_141F6A910(owner, attrId, pos, mode2)`；MoveDaemon 两条都不走。加上载荷仅 1 字节、客户端二进制里 `find` `AtlasExileBoss5` / `BeamTargetDaemon` **0 命中**，三条独立证据都指向"它只是摆位"。

### 3.2 `OrionBeamSweep`（ActionType 1034）—— 技能本体，同样画不出

**类卡片（观察）**：类名串 `0x143267c88`；工厂 `sub_141985CE0` 先探测 ctx 属性 876/332/858 选变体，都不命中才 `sub_142C8CB50(832)` 分配 **832** 字节并调 ctor `sub_141D1AC90`（`sub_1419E9B10(this, 0x4A62, actor, ctx)`）；主 vtable **`0x143587090`**、次接口 `[+0xA8] = 0x14323E408`、`[+0x310] = 0`。**构造函数不接收任何目标坐标**。

**覆写槽 7 个（观察）**：31 `0x141D1AD80`（启动：算 rate、播动画 **349**、把自己重锚成"施法者 + 方向×L"，L 由组件 `vtbl[0](349)` 决定：0→70.0、1→23.0、2→46.0、3→69.0 格，常量 `0x1436AE80C/608/730/808`）、35 `0x141D1AFF0`（每帧 0.1 s 定步）、40 `0x141D1AF20`（动画事件：认 `0xA32E`=`stop_beam` → state 2/4，认 `0x8A6A`=`blast` → state 3）、86 `0x141D1B100`（写 24 B：**客户端→服务端**上报 `state/t1/t2/posX/posY/rate`）、153 `0x141D1AF10`（`mov dword [rcx+318h],1; ret`）、157 `0x141D1B230`（每 0.1 s 解两臂端点）、161 `0x141D1BC40`（`blast` 后清扫，半径 `stat/100*35`）。

**端点解算（观察）** `sub_141D1B6E0`：`tip = casterPos + rotate(ownPos − casterPos, ±(1 − t/rate) × 75°)`，常量 `0x1436AE828 = 75.0`、`0x1436ADA7C = π/180` ⇒ 两臂从"分身的双手"出发，张角由 ±75° 收到 0°（收敛光束）。

**为什么画不出（观察）**：`state`（`this+0x318`）没有任何初始化写入；唯一写 1 的是槽 153，而它 `callers = 0`（只能虚调用），它在等动画事件 `0x9B8D`，而 `0x9B8D` 在 4783 个怪物 `.ao`、45 个角色 `.ao`、23362 个 stat 里**都不存在** ⇒ state 恒 0，端点永不解算。客户端断言串 `.rdata 0x1435DE800` 同证："MakeBeam: Must specify either a to_variable or a non-zero offset, or both"。

**命中与伤害**：槽 157 会做射线/方框枚举（把端点附近的实体找出来），但**伤害判定在服务端**；客户端只上报 24 字节状态。

**序列化（客户端收到什么）**：服务端下发的是**技能动作 + 动画 + 目标**；客户端回传 24 字节 `state/t1/t2/posX/posY/rate`。

### 3.3 两束光到底归谁（观察 + 推断）

> **2026-11-17 追加（决定性观察）**：把 `Metadata/Monsters/InvisibleFire/InvisibleFire.ao` 从 GGPK 里抽出来了
> （1130 B，`ggdump` 输出目录必须先存在）。内容：rig = `Art/Models/Effects/totems/rig.amd`、
> `SkinMesh {}` **空**、`ParticleEffects {}` **空**、`SoundEvents {}` **空**、`Lights {}` 空、`ScreenShake {}` 空；
> 只有 `BoneGroups`：`root`、`frameupper`（都绑 `root head_chunk_jnt`）、
> **`cyl`（`R_jnt01` → `R_jnt02`）、`mid`（`R_jnt01` → `R_jnt03`）**。
> ⇒ **daemon 自己一个像素都不画**：它是纯锚点（三个骨，供别人来连）。
> ⇒ 所以"光束由 daemon 实体自身的模型画出"这条推断**作废**；光束要么是
> ①**效果实体**（`clone_trap_beam.ao` = `MiscAnimated` 3729 `AtlasExileOrionCloneBeam`，
> 或是 `TripleBeam/triple_beam_0N.ao`），要么是
> ②**几何动作画的连线**——`7031 AtlasExileOrionCloneBeam` 的 ActiveSkill 是 **1242 `geometry_spell`**，
> 而 `GeometrySpell` 的 `+792` 子对象序列化里带"**4 字节实体 id + 1 字节标志**"的**目标实体列表**
> （`sub_14196E1D0` 写 / `sub_14196E530` 读），语义就是"从起点连到一组目标实体"——
> **daemon 正是那组目标实体**（两个 daemon = 两条光束的两端）。
> ⇒ 最可能的官方流程：Cast `7031`（几何动作）在分身身上 + 生成两个 daemon 当锚点 + 把这两个实体 id
> 作为目标表发给客户端 ⇒ 客户端用 `0x7A39632A` 属性指定的美术画两条连线。
> **未验证**（需要知道服务端怎么把"目标实体表"塞进动作包里）。

- **观察**：客户端二进制里没有 `AtlasExileBoss5` / `BeamTargetDaemon` 字符串；分身 `.ot` 只挂 `apparitionSurge.ao`（空 SkinMesh 的粒子）；`daemon1`/`daemon2` 是无 data 的动画事件；`MoveDaemon` 不造物。
- **推断**：官方流程 = 服务端生成两个 `AtlasExiles5BeamTargetDaemon`(`Daemon2`) 实体 → 对它们施放 `move_daemon`（GE 8614/8615，SkillID 53/54）→ `MoveDaemon` 按 dat 行（`__col5` = ±90°）与 ActionData `+496` 的偏移把两者摆到分身左右手并转向 → **光束由 daemon 实体自身的模型/特效画出**（`InvisibleFire.ao` 的 `cyl`/`mid` 骨组正是光束筒的两组骨）。同族 daemon 的做法可直接看代码：`invisiblefiremovedaemonignorancebeam.ot` 与 `maven/invisiblefiremovedaemonmaventargetbase.ot` 都在 `on_construction_complete` 里 `AddAttached('…/eyelaser_aoe.ao', <root>/<lock>)` 挂一个客户端 effect object。
- **但**：Orion 这两个 daemon 的脚本 `invisiblefiremovedaemonorion.ot` / `…orion2.ot` **没有 client 块**（只有 `DiesAfterTime` + `serialise_position`）⇒ 它们的 visuals 只能来自 `Animated{animated_object = "Metadata/Monsters/Daemon/Daemon.ao"}` 那个**未被抽出**的通用 daemon 模型 ⇒ 光束本体仍是未解。

### 3.4 服务端该发什么（要点）

动作 + 两个 daemon 实体 + 两次伤害结算；光束本体优先让客户端画（daemon），服务端 `ServerBeamEffect` 只作兜底。完整清单见文末 §服务端该发什么。

### 3.5 未解

1. **谁把两个 daemon 放进来的**：GE 8614/8615 的 `SkillID 53/54` 指向的 **Skills 表没有被抽出来**（`data/tables/` 无 `Skills.json`，`sql/base/client/` 也没有），所以"官方由谁生成这两个 daemon"没有证据。
2. **光束本体**（**2026-11-17 已推进**）：`InvisibleFire.ao` 抽出后确认 daemon **不画任何东西**（空 SkinMesh/粒子/声音），
   它只有骨 `R_jnt01/02/03`（骨组 `cyl`/`mid`）⇒ 光束不是 daemon 画的。
   剩下的两条路：效果实体（`clone_trap_beam.ao` / `triple_beam_0N.ao`）或几何动作的"目标实体连线"。
   还差的是：`Metadata/Monsters/Daemon/Daemon.ao`（`Animated` 指向的通用 daemon 模型）**没被抽出**
   （`Tools/re/out/orion/metadata/monsters/` 下无 `Daemon/`）；若它是另一个可见模型，光束也许在它身上。
3. **dat 列语义**：`data/schema/schema.min.json` 里 `MoveDaemon` 的 35 个列名**全是 null**（只有类型：0–15 i32、16–18 bool、19–24 i32、25 bool、26 string、27 i32、28–29 bool、30 i32、31 bool、32 f32、33 i32、34 bool；列 6 = `MonsterVarietiesKeys` → MonsterVarieties 外键）。因此 `row+76` 那个门控字节对应哪一列、`__col5`（±90）与 `__col11` 分别是"朝向"还是"光束起始角"，**无法从仓库确定**。
4. **±90° vs 75°**：dat 的 `__col5` = ±90，客户端端点解算用 **75°**；若前者是"daemon 朝向"、后者是"光束收敛角"则自洽，但没有数据能证明（未解）。
5. **`daemon1`/`daemon2` 事件名怎么用**：客户端如何把这两个事件名变成"生成两个 daemon"没找到代码（观察：事件无 data 字段；`.rdata 0x1435a90c8` 有 "daemon" 串，但没有把它与 clip 事件名连起来的证据）。
6. `stop_beam` @2.729（收光）与 `blast` @3.431（正前 7.54 格爆炸）的服务端行为仍未实现。

---

### 3.6 官方分身光束的美术是 `clone_trap_beam.ao`（观察）

- `MiscAnimated` 行 3729 `AtlasExileOrionCloneBeam` → `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/clone_trap_beam.ao`。
- 它的结构：rig `clone_trap_beam/rig.amd`、`default_animation = "clonetrap_beam"`、网格 `clone_trap_beam/clonebeam.sm`、
  **`BoneGroups: beam_length false jntBnd_beam aux_beam`**（和 `triple_beam_0N.ao` 一样：长度由客户端按数据拉）、
  粒子 `clone_trap_beam/clone_trap_beam_length.pet` 挂在 `beam_length` 骨组、音效 `…/Sirus/Effects/Beam/TripleBeam3`
  挂 `jntBnd_beam`、`ScreenShake` 13 / 0.4 s。
- **分身自己的** `atlasexile5_beamclone.ao`（与本体那份不同）带 `beam_01` / `beam_02` 的 contact 事件**和位置**：
  `beam_01` contact@0.672 位置 `(-9.46, -91.91, -173.77)`、`beam_02` contact@0.6915 位置 `(5.61, -96.42, -179.80)`
  ⇒ 两只手各自的起点（模型空间，1 单位 = 0.01 格 ⇒ 左右各偏 0.09 格 / 前方 1.7 格量级）。
- 但它的骨组里**没有 beam 骨组**（只有 root / frameupper / 眼睛 / 手臂 / 腿 / 手 / aura）⇒ **它自己也不画光**。

⇒ **分身两束的官方画法：服务端在两个 contact 位置各放一个 `clone_trap_beam.ao` 效果实体**，而不是
`ServerBeamEffect` 行 227（那是 `fusion_beam.ao`，9 格宽的熔岩墙，也就是"满屏"那个）。


### 3.7 分身两束：没有 daemon → daemon 的代码（`logs/tmp/re/beamlength.md`，观察）

- 客户端对 `DaemonSpecialBeam` / `AtlasExiles5BeamTargetDaemon` / `clone_trap_beam` **零命中**；
  121 个动作类里没有 Clone/Beam 类；`AtlasExile5` 字符串（0x143443330）**无代码引用**
  ⇒ **不存在 daemon → daemon 的光束代码**。
- 分身两束 = **两条独立的几何光束**：起点 = 分身 clip 的 contact `beam_01` / `beam_02`，
  终点 = **服务端给的实体列表**（几何通道状态的那个 list）。
- 骨组只是"名字→骨下标"索引：`sub_1429432F0`（文本 `bone_group = "名 bool 骨名…"`）、`sub_142BBB680`（二进制），
  条目 56 B = {std::string name; vector<int> boneIndices}，查名 `sub_1429431E0`（56 callers），骨名→下标 `sub_14291B360`
  ⇒ **没有通用的"拉伸骨组" API**，长度必须由数据驱动。
- 长度存在 animated object 上：`sub_141DCC410`（vtable 0x1435a1a80 槽 28）把 `"beam_length"` 映到 **+556**、`"radius"` 映到 **+552**；
  `sub_141E31CA0` 把它置 0x7FFFFFFF（=自动）；每帧 `sub_141E37DB0` 里 **+556 = +656**（+656 的写入者未解）。
- 数据来源 = **几何通道**：`sub_14196E530`（读）/`sub_14196E1D0`（写）的格式是
  u32×4 → u32 +240 → list@216 → u8 条数 → 每条 **u32 实体 id + u8 标志**；stat 6433 → GeometryAttack 行 Id。

## 当前服务端实现与反馈（`Modules/Core/Game/Monster/Sirus.Combat.cs`，分身 `kind == 0`）

```csharp
await Cast(c, apparition, 10941, aim, now); await Voice(5665);
hits.Add(new(now + .910, beamGen, ++group, 10941, from, Extend(from, aim, 160), 10, Beam: true, ApparitionHit: true, Ghost: true));
for (int hand = -1; hand <= 1; hand += 2)                       // 两束 ServerBeamEffect
{
    int hx = from.X + (int)Math.Round(fx * 1.6 + px * 2.8 * hand);
    int hy = from.Y + (int)Math.Round(fy * 1.6 + py * 2.8 * hand);
    hits.Add(new(now + .910, beamGen, ++group, 10941, (hx, hy), (ex, ey), 0, Beam: true,
        ApparitionHit: true, Bone: hand < 0 ? "L_Weapon" : "R_Weapon", Life: 3.431 - .910 + .07, Visual: true));
}
hits.Add(new(now + 3.431, beamGen, ++group, 10941, from, (ex, ey), 16, ApparitionHit: true));
```

- 美术行：`Beam()` 对所有 `hit.ApparitionHit` 用 `FireBeam`（行 37，2.9 格宽）；`BeamReach = 20`（`:1014`）。
- 起点挂 `L_Weapon` / `R_Weapon` —— **分身 rig 里确实有这两根骨**（见上表，观察）。
- 起手位置用服务端的 ±2.8 格 / 前 1.6 格，**与 clip 常量 ±5.59 格 / 1.6 格不一致**。

| 时间 | 做法 | 玩家反馈 |
|---|---|---|
| 2026-11-16 | 两片静态 `fusion_beam.ao` 摆在双肩 | 起点不对、两片浮着的板 |
| 2026-11-17 10:17 | 两束 `ServerBeamEffect`，远端 = 玩家 | m01493「分身激光的问题这么明显你都看不到吗，都满屏了」 |
| 2026-11-17 11:0x | 远端改回 clip 自带的 7.54 格爆破点 | 仍然满屏（`logs/shots/app3`） |
| 2026-11-17 11:2x | 行 37 + 至少 20 格 | m02016「到底能不能修好」、m02031「这个激光也是错的」 |
| 2026-11-17 12:4x | 停止换美术，转 IDA 逆向（m02045 的要求） | 本文档 |

---

## 服务端该发什么

1. **动作**：对分身发 `AnimationUpdate` 播 `double_beam_hands_01`（10941，动画槽 51 = `HeavyStrike`）。
2. **两个 daemon（摆位，但不画光）**：客户端**没有 daemon → daemon 的光束代码**（3.7 节），所以 daemon 实体只能当**骨锚点**；生成 `Metadata/Monsters/InvisibleFire/AtlasExiles5BeamTargetDaemon`（8387）与 `…Daemon2`（8388），位置取 clip 常量 `daemon1` (−559.025, −160.465, 0) / `daemon2` (+559.0, −160.465, 0)（模型单位 → 左右各 5.59 格、前 1.6 格），并对它们施放 GE **8614/8615**（`move_daemon`，SkillID 53/54）。
3. **光束本体**：官方路径是**两条独立的几何光束**（起点 = 分身的两个 contact，终点 = 服务端给的实体列表），而不是 daemon 自画；需要服务端发那份几何状态（同一个未解，子任务 `geomstate`）。在那之前只能用 `ServerBeamEffect` 兜底（近端必须挂 `L_Weapon`/`R_Weapon`），但这条已被玩家否掉三次（满屏火炷墙）。
4. **伤害**：服务端在 **0.910 s**（两束光扫过）与 **3.431 s**（正前 7.54 格 `blast` 范围）两次结算；客户端的 `OrionBeamSweep` 只上报 24 字节状态，不负责伤害。
5. **收光**：`stop_beam` @2.729 应关掉两束光（当前实现没有）。
6. **共享状态**：分身 `.ot` 的 `beaming / x_pos / y_pos / z_pos` 仍可写（客户端目前不读，但那是官方预留的入口，值得单独验证一次）。
