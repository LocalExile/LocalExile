# 本体普通光束（7008 / 7012）

## 一句话结论

普攻是 `AtlasExileOrionSpammableBeamBlast`（7008，终极形态 7012），动画槽 = **`SpellProjectile`**，
`.act` 绑 `beam_01` **then** `beam_02`（终极 `final_beam_01` then `final_beam_02`）。

**官方的美术我们早就在用了**：客户端表 `MiscAnimated` 里
`AtlasExileOrionSpammableBeam` → `Orion/spam_beam.ao`、`AtlasExileOrionSpammableBeamFinal` → `Orion/spam_beam_final.ao`
（观察）。问题不在"用哪张图"，而在**怎么放**：官方是把它挂在**手骨**上、随武器朝向伸出去；
我们现在是"在脚下放一张贴地网格"，所以永远差一口。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| GrantedEffects | `AtlasExileOrionSpammableBeamBlast` → 7008；`…BlastFinal` → 7012 |
| ActiveSkill | 1242 = `geometry_spell`，`ActionType 807`，**没有 projectile 字段** |
| CastTime | 1500 ms（7012：1350 ms） |
| `Animation` | **20 = `SpellProjectile`**（`data/tables/Animation.json`） |
| `.act` 绑定 | `SpellProjectile = "beam_01"; then "beam_02";`；stance2 为 `final_beam_01` / `final_beam_02` |
| clip 长度 | 1500 ms（`data/monster-contacts.json`） |
| clip 事件 | `beam_01`：只有 `contact` @0.672，位置 (-9.46, -106.94, -209.72)（模型单位，100 = 1 格）<br>`beam_02`：只有 `contact` @0.6915，位置 (5.61, -107.72, -206.74) |
| 客户端钩子 | 本体 `.ot` 的 `client.Animated` 里**没有**与 `beam_01/02` 相关的条目 |
| 官方美术 | `MiscAnimated` 行 `AtlasExileOrionSpammableBeam`（`spam_beam.ao`，HASH32 3850907589）、`…SpammableBeamFinal`（`spam_beam_final.ao`，HASH32 1122204890） |

两条 `contact` 的位置几乎相同（都约 1 米前、1 米高的体前），**说明"两段"是同一个姿势的两次施法节拍**，
不是两束方向不同的光。战斗里 `then beam_02` 表示动作结束后再播一次同槽的下一个 clip。

### 官方美术长什么样（`spam_beam.ao` 的结构，观察）

`Tools/re/out/orion/metadata/effects/spells/monsters_effects/atlasexiles/orion/spam_beam.ao`（UTF-16LE，2004 字符）：

| 段 | 内容 |
|---|---|
| `AnimationController` | `metadata = "Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/rig.amd"`，`default_animation = "beam_strike"` |
| `SkinMesh` | `skin = "Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/spambeam.sm"` |
| `BoneGroups` | `beam_length false jntBnd_beam aux_beam`、`beam_flash false jntBnd_beam aux_circle2` |
| `ParticleEffects` | `Metadata/Particles/monster_effects/AtlasExiles/Orion/impact_beams/beam_flash.pet`，挂 bone_group `beam_flash` |
| `SoundEvents` | `Audio/…/Sirus/Effects/Beam/SpamBeam`，挂 `jntBnd_beam` |

要点（观察）：

- `spam_beam.ao` 与 `TripleBeam/triple_beam_01.ao` **共用同一套 rig 与网格**
  （`impact_beams/rig.amd` + `impact_beams/spambeam.sm`），只有材质、动画名和粒子不同
  （`spam_beam_ao_pass0.mat` vs `triple_beam_01_ao_pass0.mat`，`beam_strike` vs `tripleBeam_01`，
  `beam_flash.pet` vs `triple_01.pet`）。**"三段光束"和"普攻光束"是同一套模型的两套皮。**
- 两个 bone group 说明**长度与端头闪光是客户端按数据驱动的**（`beam_length` 拉长度、`beam_flash` 闪端点），
  不是模型自带的长度。裸放一个 Effect 只会得到模型默认长度。
- `fusion_beam.ao`（`MiscBeams` 行 227，我们曾经用的那张）是**另一套**模型
  （`fusion_beam/fusionbeam.sm`，9 格宽的熔岩片），这就是当初"满屏"的来源；
  它文件里出现的 "TripleBeam" 只是音效名 `…/Sirus/Effects/Beam/TripleBeam2`，**不是**引用 TripleBeam 效果。

## 客户端自己画什么 / 服务端必须画什么

- **客户端**：播放 `beam_01` 的主体动画（双手前伸/武器前刺的那套动作），以及 `contact` 时刻的判定窗口。
  `beam_01` 的 `.ao` 里没有任何 `ParticleEffectType`，`.act` 里也没有 effect 行 → **clip 本身不画光**（观察）。
- **动作类会画**：`GeometrySpell`（本技能的动作类）在启动时**自己 new 一个 584 字节的 Effect** 并挂到施法实体上
  （见下节）。所以"客户端不画"只对 clip 成立。
- **服务端**：伤害判定 + 把光束放到该放的位置（手骨、朝向、长度）。

### 实测过的三种画法

| 做法 | 结果 |
|---|---|
| `Ghost: true`（什么都不画） | 实机 2026-11-16 证实：整段只有施法动作，屏幕上一道光都没有 |
| 行 228 `fusioned_nuke_beam.ao`（`len=150`，`life=1.5`） | 一条横穿全屏的橙紫熔岩柱；玩家否决（m00566「把这个大激光去掉，这个不是普通攻击的光束」） |
| 行 227 `fusion_beam.ao` | 仍然"满屏"（美术本体 2.15 格宽、9.4 格长，拉伸后是一片墙），玩家否决 |
| **`spam_beam.ao` 静态网格（当前）** | 脚下沿朝向放一张网格，0.6 s；玩家确认"现在正常了"（m00843），但它贴地、没有来源骨骼 |

## 客户端实现（反编译）

### 动作类：`GeometrySpell`

（观察）`ActiveSkills[1242]` 的 `ActionType` 是 **807**，`data/tables/ActionTypes.json` 里 807 = `GeometrySpell`，
`HASH16 = 52470 = 0xCCF6`：

| 项 | 值 |
|---|---|
| 包装体 | `sub_14126D130`：`_crtInitOnceExecuteOnce("GeometrySpell", sub_14128CD10, sub_14128CD00, 0)` |
| 工厂 | **`sub_14128CD10`** —— 先探测 ctx 属性 `876` / `332` / `858` 选变体，都不命中才 `sub_142C8CB50(1192)` 分配 **1192 字节** 并调 ctor |
| 构造 | **`sub_141514B10`**：`sub_1419E9B10(this, 0xCCF6, actor, ctx)`、`sub_141940AA0(this, *(u8*)(this+372))` → `(**v4)(v4, 6433)` → `sub_141967050(this+792, v5)`、先装 `off_14353BC00` 再改成主 vtable **`off_143444E98`**、`[+168] = off_14323E408`、`[+792] = off_143446C70` |
| 自有槽 | `0x141514AC0`(析构)、`0x1415148F0`(谓词 `+286==0 \|\| +888!=0`)、`0x141514910`(写)、`0x141514920`(读)、`0x141514940`(clone)、`0x141517184`(子对象 thunk)、`0x141512B90`(析构变体)、`0x141512C40`(推进)、`0x141512C10`(释放特效句柄)、`0x141513080`(启动·读包)、`0x141512B10`(影子副本)、`0x141512D30`(启动·不走包) |

（注意）槽号是相对传入地址算的，不同类的 vtable 布局不同，**只认函数地址与行为，不认槽号**。

**它到底画不画？画。**（观察）

```c
// 造 Effect：584 字节，表 off_143460C40
v = sub_142C8CB50(584);
sub_141EC47E0(v, &resource, actor, 0, 1);     // resource = L"Metadata/Effects/Effect"（23 字符，固定串）
sub_141F6E890(entityCtx, v);                 // 挂到实体上
sub_141EC9890(a1+888, v);                    // 句柄存 +888
```

特效**内容**的来源有两种（观察）：

| 来源 | 怎么来的 |
|---|---|
| ActionData 属性 **`0x7A39632A`**（2050581290） | `sub_14228BED0(&prop, 2050581290, &t)` → `sub_141E59F80(effect, &prop, 0)` |
| 包里的 **2 个资源串 + 2 个 u32** | 启动槽 `sub_141513080`：`flag = *buf++`；`flag != 0` 时读两个串（`+856` / `+864`）与两个 u32（`+872` / `+876`）；`flag == 0` 时改走 `sub_141DC0BC0(...)` 从技能数据读 |

每帧只做一件事：把动作时间重映射成特效时间轴（`+656` 帧数、`+692` 起播偏移、`+680/+684` 归一化进度，
`+872/+880` 是时长与起始，除以 5.0 是一段五段缓动）。

⇒ **对服务端的含义**：我们的 `0x1ab`（`InstanceProtocol.ActionStarted`）只带 skill id 与坐标，
**不带那两个资源串**，所以客户端走 `flag == 0` 分支，从它自己那份技能数据里取效果属性；
若那份属性取不到，客户端就会创建一个**空 Effect**（解释了"同一动作有时什么都没出现"）。
本机客户端表里 `0x7A39632A` 这个数字同时出现在 `MiscAnimated` 行 `IncursionNovasoulMovingCone` 的 `HASH32` 上
（**观察**），但属性键与 MiscAnimated 的 HASH32 不是同一套编号，**不能据此断定**普攻用的是哪张图；
官方那张图仍以 `MiscAnimated` 的 `AtlasExileOrionSpammableBeam` 为准。

### 几何数据从哪来：`Data/GeometryAttack.dat` 与 stat 6433（决定性，2026-11-17）

`GeometrySpell` 的 `+792` 子对象在构造时（`sub_141967050`）加载 **`Data/GeometryAttack.dat`**（不是
`Data/GeometryTrigger.dat`），用 **ctx 属性 6433** 的值作键查一行：

- `sub_1414C7DD0(v4, L"Data/GeometryAttack.dat", ...)` 取文件句柄 → `sub_1401297C0` 拿表 → `sub_140215DA0(map, &key)`
  按 u32 键查；查不到（返回尾哨兵）这一行就不存在。
- 命中后把行里的字符串搬进子对象（`+200`/`+208` 一对、`+257`/`+265` 一对）——那几对字符串就是这道光的**名字与美术**。
- 6433 在我们的表里是 **`skill_behaviour_variation`**（`data/tables/Stats.json` 第 6432 行，客户端 stat id 比我们的行号大 1）。

⇒ **官方普攻的光不是客户端"随便"画的：它由 `GeometryAttack.dat` 里的一行决定，而这一行要靠技能自己的
`skill_behaviour_variation` 取到。** 我们的 7008 / 7012 的 StatSet（7013 / 7017）**没有设 6433** ⇒ 值 0 ⇒ 查不到行
⇒ 客户端自己画不出这道光（这与我们实机看到的一致：光一直是服务端补的）。

⇒ 两条路：**（甲）**给技能一个对的 `skill_behaviour_variation`，让客户端自己画（最像官方，但要试出那个值）；
**（乙）**服务端放官方 `.ao`（`EffectObject` + Parent + Bone=`R_Weapon`），美术用 `spam_beam.ao`。

两个坑（观察）：

- `Data/GeometryAttack.dat` 在 GGPK 里是 `data/geometryattack.datc64`（432,112 B，头 4 字节 `0x550` = 1360 行），
  **纯二进制、一条可读字符串都没有**；`.datcl64` 是它的本地化伴随文件。我们扫过整份文件：**没有任何 `MiscAnimated` 的
  HASH32**（`spam_beam` / `triple_beam_*` / `corridor_slash_01` / `clone_trap_beam` 的 hash 全部 0 命中）⇒ 这张表不直接点名
  `MiscAnimated` 的美术行。
- `GeometryTrigger`（10141 / 10145 / 10957，ActiveSkill 1449）查的是**另一张**表 `Data/GeometryTrigger.dat`（490 行），
  同样以 6433 为键——而 6433 在那张表里**一次都没出现** ⇒ 走 `geometry_trigger` 的技能（我们的陨石雨标记 10141/10145）
  永远什么也不放。这解释了实机里"陨石雨标记是空的"。


### 本体 `.ao`：普攻两个 clip 只有声音，没有粒子、没有拖尾

`Tools/re/out/orion/metadata/monsters/atlasexiles/atlasexile5.ao`（UTF-16LE，54772 字符）的 `client` 段：

| 段（字符偏移） | `beam_01` / `beam_02` 相关条目 |
|---|---|
| `SoundEvents` | `beam_01`：人声 `…/Sirus/HumanFormVocals/Attack` @0.4544（`head_jntBnd`）+ 挥空 `…/Unarmed/New_Unarmed_$(#).ogg` @0.5868（**`R_wrist_jntBnd`**）；`beam_02`：人声 @0.5096 + 挥空 @0.5132（**`L_wrist_jntBnd`**） |
| `ParticleEffects`（42903） | **没有** `beam_01` / `beam_02` 条目。有粒子的只有 `beam_03`、`beam_barrage_01`、`beam_barrage_abort_01..03`、`final_beam_01/02/03`、`final_beam_barrage_01` |
| `TrailsEffects`（48279） | **没有** `beam_01` / `beam_02` 条目。有拖尾的是 `beam_03`（`melee_trail.trl` 双臂 0.333→0.841）、`beam_barrage_01`（`beamBarrage.trl` R/L_arm_FX 0.0→6.6）、`clone_beam_start_01`（`cloneBeam.trl` 四条）、`death_01` |

⇒ **官方普攻的"光"不是 clip 画的**：动作 + 两条挥空音效在 clip 里，光是 `GeometrySpell` 建的 Effect 画的。

`.ao` 里还有两个与手相关的 socket（观察，目前**没有**脚本挂载者，`.ot` 全文搜 `aux_hand_orb` = 0 命中）：
`aux_hand_orb_attach` 挂 `R_Weapon`、`aux_hand_orb_attach_l` 挂 `L_Weapon`，translation 分别 `0 -10 0` / `0 10 0`——
它们能给出"手心位置"，是服务端 beam 该用的骨骼高度的旁证。

## 决定性结论：这道光的美术由 `SkillID` → `GeometryAttack` 行决定（2026-11-17 下午）

链条已经闭合，每一环都有本地证据：

| 环节 | 证据 |
|---|---|
| 客户端用**自己的** `skill_behaviour_variation` stat 去查几何表 | `SkillCatalog.BehaviourVariation`（`src/GameServer.Gateway/Skill/SkillCatalog.cs:328-336`）的注释：`a geometry channel its GeometryAttack row (by Id)`；stat id `0x1921` = 6433 = `Stats.json` 行 6432 `skill_behaviour_variation` |
| 我们的服务端**已经**把这个 stat 发给客户端 | `Modules/Core/Game/Monster/Sirus.Native.cs:48` 调 `WithBehaviourVariation(c, e, …)`；`Modules/Core/Game/Monster/Monsters.cs:24-27` 把它加成 `(StatKeys.SkillBehaviourVariation, effect 自己的 SkillID)`；`GrantedSkill.Stats` 由 `InstanceEntities.cs` 的 `WriteSkillRecord`（`.Stats(s.Stats)`）写进技能记录 |
| 这个值就是 `GrantedEffects` 行的 `SkillID` | `data/tables/GrantedEffects.json`：7008 → **332**、7012 → **333**、7019/7020/7021 → 342/343/**344**、7022-7024 → 345/346/347、7025-7027 → 348/349/350、7028-7030 → 351/352/353、7031 → **359**（7013/7014 终极版 → 344） |
| 而 `SkillID` 正好是 `GeometryAttack` 的行 Id | `data/tables/GeometryAttack.json`（1360 行）：Id **332** → `Animation [3667]` = `AtlasExileOrionSpammableBeam` = `spam_beam.ao`（`HitEffect [3701]` = `Hit_FX_beam.ao`）；333 → 3668 `spam_beam_final.ao`；342/343/344 → 3702/3703/3704 `triple_beam_01/02/03.ao`；345-347 → `_ice`；348-350 → `_fire`；351-353 → `_lightning`；**359** → 3729 `AtlasExileOrionCloneBeam` = `clone_trap_beam.ao` |

⇒ **官方光束本来就该由客户端画**：它拿 `skill_behaviour_variation` 去 `GeometryAttack` 查到 `.ao`，自己建 Effect、自己按骨组 `beam_length` 拉长度、自己挂命中特效 `Hit_FX_beam.ao`。服务端再画一张 `spam_beam.ao`／`fusion_beam.ao` 是**重复**，而且因为没挂骨骼会贴地——这正是玩家一直说"不对"的根因候选。

几何行里的数字（Sirus 全部几何行一模一样）：`__col4=3, __col5=3, __col6=230, __col7=10, __col9=6, __col10=3, __col11=100, __col15=3, __col17=4, __col24=7`。其中 `__col6=230` 与 `OrionBeamSweep` 反编译里那组端点长度 `{23, 46, 69, 70}` 格的第一个数对上：**世界单位 = 格 × 10 ⇒ 官方光束长 23 格**，`__col7=10` = 宽 1 格。另一类形状（Id 330 `death_carpet_bomb_impact`）则是 `__col6=18`、`__col7=0`、`__col9=13`、`__col10=13`。

**因此这三条可以改**（每一处只动一个变量）：

1. 撤掉服务端自己画的普攻光束（`Sirus.Combat.cs` 里 7008/7012 那条 `spam_beam.ao` / `spam_beam_final.ao` 贴地效果），让客户端自画；**预测**：光束会从 BOSS 手部射出、长 23 格、宽 1 格，走向我们的 aim 目标。
2. 多段光束（7019-7031）同理撤掉 `ServerBeamEffect` 行 227 的 `fusion_beam.ao`；**预测**：三条 `triple_beam_0N.ao`（3702-3704，物理）或元素变体（3705-3713）依次出现，每条 23 格。
3. 分身双手激光（7031 → GeometryAttack 359 = `clone_trap_beam.ao`）同样交给客户端；**预测**：分身两手各一条 23 格光束。

**`.dat` 的字节布局（本地解析，`logs/tmp/gg/data_geometryattack.datc64`，432,112 B）**：
`u32 rowCount = 1360` → 固定段 `rowCount × 305 B` → `8 × 0xBB` 标记 → 变量段（17,300 B）。固定字段里引用变量的（数组、字符串）存 **u32 偏移**（相对变量段起点）：一个数组列占 16 B（`[u32 len][u32 pad][u32 varOffset][u32 pad]`）。行 332 的头 36 字节 = `Id 332 | Animation(len 1, var 4336) | HitEffect(len 1, var 4352)`；变量段里 4336+8 处是 **3701**（`Hit_FX_beam.ao`），4352+8 处是 **3667/3668**（`spam_beam.ao` / `spam_beam_final.ao`）——即 `Animation` 与 `HitEffect` 存的是 **MiscAnimated 行号**，不是美术路径（路径在 `MiscAnimated.AOFile` 里）。整份文件里**没有任何字符串**（UTF-8/UTF-16 全扫过，0 命中），所以几何行不可能自带 `.ao` 路径。

**客户端怎么用它**：`sub_141967050(a1+792, key)` 用 **stat 6433 的值**当 u32 键查这张表，把行里的两组字段（+200/+208、+257/+265）与数值字段（`sub_142257C90` 拷到子对象 +320）搬进 `+792` 子对象；**美术的最终解析在别处**——`sub_141512D30`（不走包启动）与 `sub_141513080`（读包启动）都先把 `Metadata/Effects/Effect` 模板建出来，再塞 **写死的 `2050581290`**（`MiscAnimated` 2169 `IncursionNovasoulMovingCone` → `moving_cone.ao`）作为**默认**内容。`flag != 0` 的读包分支可以从包里读两个资源串（+856/+864）覆盖它；`flag == 0` 走 `sub_141DC0BC0` 读技能数据。

⇒ **待查的最后一步**：几何行的 `Animation [3667]` 是在哪个函数里变成 `spam_beam.ao` 并替换掉那个默认锥体的（候选：`+792` 子对象的解析函数 `sub_141511F30`、每帧推进 `sub_141512C40`、或命中特效那条路）。这决定了"我们什么都不做、客户端自己画"是否成立。

**几何行里的数字**：`__col4=3, __col5=3, __col6=230, __col7=10, __col9=6, __col10=3, __col11=100, __col15=3, __col17=4, __col24=7`（Sirus 全部几何行一样；另一类形状 Id 330 是 `__col6=18, __col7=0, __col9=13, __col10=13`）。其中 `__col6=230` 与 `OrionBeamSweep` 反编译里那组端点长度 `{23, 46, 69, 70}` 格的第一个数吻合：**世界单位 = 格 × 10 ⇒ 官方光束长 23 格**，`__col7=10` = 宽 1 格（**推断，等客户端侧确认**）。

**客户端怎么用这一行（`sub_141967050`，lead 2026-11-17 下午逐行读完）**：
`sub_141967050(a1+792, key)` 做四件事——① 加载 `Data/GeometryAttack.dat`（`sub_1414C7DD0`）；② 用 `sub_140215DA0`（FNV-1a 哈希表）在**行 Id = key** 上找行；③ 把行里的**名字字符串**（行内 +200/+208 的共享串）拷进子对象（`sub_1419E3570`）；④ `sub_142257C90(a1+320, key, &table)` 把这一行的**几何数据**解到子对象的 +320，再解第二组数组。子对象随后被初始化（+96 标志、+100..+112 四个 dword、+216 监听器表、+276/+284 两个容器、+296..+312）。

`+320` 那个"几何数据"对象里：+20 = **m_HitEffects 的条数**、+28 = 数组首地址、+117 = 命中特效的朝向模式（3/4/5/6/7/8 分别是 ContactPoint / 随机 / 目标 / InFront / RNG / SelfOnce）。取它的是子对象 vtable（`off_143446C70`）的 **槽 14 = `sub_141514EA0`，函数里自带字符串 `"GetHitEffectOrientationReference"`**——它按 +117 选朝向，再从 m_HitEffects 里随机取一条并生成。⇒ **命中特效由客户端自己出**（`Hit_FX_beam.ao` 就在 `HitEffect [3701]` 里）。

**光束本体长什么样（两条启动路径都对得上）**：
- `sub_141512D30`（不走包启动）与 `sub_141513080`（读包启动）都先建 `Metadata/Effects/Effect` 模板，再把内容设成 **写死的 MiscAnimated `2050581290` = `IncursionNovasoulMovingCone` → `moving_cone.ao`**；每帧的 `sub_141512C40` 又读同一行，用 `*(DWORD*)(row+36) != 0` 决定缩放随时间插值。
- ⇒ **官方几何光束 = 一个"锥体"效果（形状）+ 几何行给的形状参数（长度/宽度）+ 几何行 `Animation` 里的美术**。所以"官方普攻那束光"的**美术**只能来自几何行的 `Animation [3667] = spam_beam.ao`，而**形状**来自锥体模板。
- `sub_141513080` 里客户端能拿到美术路径的入口只有两个：包里 `flag != 0` 分支读的两个资源串（+856/+864）或两个 u32（+872/+876）；`flag == 0` 分支读的是**共享技能数据段**（`sub_141DC0BC0`）。⇒ **我们的 0x1ab 不带这两种载荷**，所以客户端只能靠 stat 6433 → GeometryAttack 行这条路。

**仍要查清**：客户端拿 332 建好 Effect 之后，几何的"起点 + 目标实体列表"（`+792` 子对象）由谁填——是动作自己的目标格（0x1ab 已经带 `aim.X/aim.Y`），还是要服务端另发一份动作状态。查清前不要撤服务端那层，先把两者摆在一起截帧比对。

## 当前服务端实现

`Modules/Core/Game/Monster/Sirus.Combat.cs`：

- 施法（`kind == 0`，`:301-315`）：
  ```csharp
  int effect = Round == 3 ? 7012 : 7008;
  await Cast(c, id, effect, aim, now);
  hits.Add(new(now + .672 * Attack(c, body!.Variety!, body.Level, effect).Seconds / 1.5,
      body.ActionGeneration, ++group, effect, from, Extend(from, aim, 150), 3, Beam: true));
  ready = now + Attack(c, body.Variety!, body.Level, effect).Seconds + .3;
  ```
  `.672` 是 `beam_01` 的 `contact` 秒数，`Seconds / 1.5` 是按实际施法时间（`CastTime` 1500 ms）缩放。
  只施一次法，不播第二段 `beam_02`——这是回到红线之前（2026-11-15 之前）的形状。
- 绘制（`Beam()`，`:204-215`）：7008/7012 走早退分支，在 `hit.From` 放一张
  `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/spam_beam.ao`（终极 `spam_beam_final.ao`），
  旋转 `atan2(aim−from) + π/2`，存在 0.6 s。它**不跟随任何骨骼**。

## 服务端该发什么（结论）

> **2026-11-17 追加**：客户端动作类用 **`MiscAnimated` 的 HASH32** 选美术（机制见 `README.md`
> 「美术是怎么被选中的」）。普攻这道光束的哈希在客户端本地的 ActionData 里，我们的客户端表没有这一张，
> 所以"客户端自己画不画得出这道光"仍**未证伪也未证实**。
> 两条路都保留：① 先按下面的「服务端该发什么」用 `EffectObject` 挂手骨（图是对的，只改挂点）；
> ② 若实机发现客户端其实自己画了（普攻时出现一条从手出发的细光），就把我们的 `EffectObject` 关掉，只留伤害。


1. **动画**：`SpellProjectile`（`Animation 20`）→ `beam_01`，`contact` 在 0.672 s（终极 7012 的 clip 为 1350 ms）。
2. **伤害**：`contact` 时刻一次性结算（现有 `hits` 已如此）。
3. **视觉**：**不要再用 `ServerBeamEffect` 画普攻**（官方普攻不用它；227/228 两行都是熔岩片）。
   改成把官方资产**挂在手骨上**：
   - 实体：`Metadata/Effects/PermanentServerEffect`（`EffectObject`），`AnimatedObject = …/Orion/spam_beam.ao`
     （终极 `spam_beam_final.ao`）；
   - `Parent` = 施法者 id，`Bone` = `R_Weapon`（终极可用 `L_Weapon` 交替），
     `AttachKind = 0`、`AttachFlags = 3`（Arrow Nova 的组合：**保留骨骼的朝向**，效果随武器指哪儿就指哪儿）；
   - 位置给施法者的格子坐标即可（父对象带着它走），寿命 0.6 s。
   `EffectObject` 的 `Parent` / `Bone` / `AttachKind` / `AttachFlags` 字段都在
   `src/GameServer.Core/Protocol/Instance/InstanceEntities.cs:859-931`。
4. **长度**：官方长度由 bone group `beam_length` 按技能数据驱动；我们裸放 Effect 只能得到模型默认长度。
   要对齐官方长度，得再往下挖 `sub_141E59F80` 怎么把属性写进 Effect 的 bone group（**未解**）。

## 玩家反馈时间线

| 时间 | 反馈 |
|---|---|
| 2026-11-16 22:2x | 基线问题：起点在身前地面、没有发射环、数量过多 |
| m00566 | 大激光（行 228）"不是普通攻击的光束" |
| m00755 / m00756 | 行 228+`R_Weapon` 污染了普攻观感，「请你把普攻的改回去」 |
| m00843 | 回到 `spam_beam.ao` 后「现在正常了」 |

## 实机验证（2026-11-17 16:40–17:05，两轮）

### 第一轮：撤掉服务端网格

改动：`Sirus.Combat.cs` 的 `Beam()` 里 7008/7012 分支改成直接 `return`（服务端不再放 `spam_beam.ao`）。

预测：客户端自己画的那束光会从手部出来。

结果（抓帧 `logs/shots/sirus-kind0-1`，31 帧）：**手部有一个白色椭圆环，没有光束本体**。
⇒ 客户端确实建出了 `spam_beam.ao`（`beam_flash`/`aux_circle2` 的圆环），但**长度为零**：没有端点。

### 第二轮（H1）：0x1ab 带目标对象 id

假设：端点是"目标实体"，而我们没把目标对象 id 发过去。

改动：`Cast(...)` 加 `uint? targetObjectId`，普攻调用点传 `PlayerId`（0x1ab 的 flags bit 0x80）。

结果（抓帧 `logs/shots/sirus-h1`，38 帧；逐帧亮像素/红橙像素统计）：**仍然没有光束**，只有手部那个环。
⇒ H1 否决。

### 现在的状态（客观）

| 现象 | 归因 |
|---|---|
| 手部白色椭圆环 | 客户端自建的 `spam_beam.ao` 效果（`beam_flash` 骨组 `aux_circle2`） |
| 没有光束本体 | 缺端点 ⇒ `beam_length` 骨组长度 = 0 |
| 分身两臂的宽大熔岩光束 | 我们的 `ServerBeamEffect` 行 37（`FireBeam`，`firebeam_beam.ao`），与普攻无关 |

⇒ 官方光束的**方向与长度必须由服务端用另一条消息给**（几何通道状态），我们还没发那条消息（子任务 `geomstate` 在查）。

### 可用的替代美术：`MiscBeams` 行 364

`MiscBeams` 里与 AtlasExiles 相关的 11 行**都没有** `spam_beam.ao`/`triple_beam_0N.ao`/`clone_trap_beam.ao` ⇒ 官方这三张图**无法**用 `ServerBeamEffect` 画。

但**行 364 `HeistRobotPyreKnightSpamBeam`** → MiscAnimated 4791 → `Metadata/Effects/Spells/monsters_effects/League_Heist/robot/pyre_knight/spambeam.ao`：
rig `spam_beam/spambeam.amd`、skin `spam_beam/spambeam.sm`、`BoneGroups: line false root aux_beam_target`、线形粒子 `pyre_knight/spambeam/line.pet`。
⇒ 方向与长度完全可控的**细光束**，观感比被否的行 37/227/228（熔岩墙）接近得多，但美术不是 Sirus 自己的。

### 骨组与长度（`logs/tmp/re/beamlength.md`）

骨组只是"名字→骨下标"索引（`sub_1429432F0` 文本 / `sub_142BBB680` 二进制，条目 56 B = {name, vector<int> boneIndices}），**没有通用的"拉伸骨组" API**；长度存在 animated object 上：`sub_141DCC410`（vtable 0x1435a1a80 槽 28）`beam_length`→+556、`radius`→+552，`sub_141E31CA0` 置 0x7FFFFFFF 表示自动；每帧 `sub_141E37DB0` 里 **+556 = +656**（+656 的写入者未解）。

### 分身两束（`logs/tmp/re/beamlength.md`）

客户端对 `DaemonSpecialBeam`/`AtlasExiles5BeamTargetDaemon`/`clone_trap_beam` 零命中，121 个动作类无 Clone/Beam 类，`AtlasExile5` 串无代码引用 ⇒ **没有 daemon→daemon 光束代码**；
分身两束 = **两条独立几何光束**（起点 = 分身 contact `beam_01`/`beam_02`，终点 = 服务端给的实体列表）。


## 实机结果（2026-11-17 傍晚）

用户拍板「先用行 364 顶一顶」后的第一批改动：普攻 7008/7012 撤掉服务端那张贴地网格，改用
`MiscBeams` 行 **364**（`HeistRobotPyreKnightSpamBeam` → MiscAnimated 4791 →
`League_Heist/robot/pyre_knight/spambeam.ao`，线形粒子，最细的光束），命中包带 `R_Weapon` 骨骼。

- **客户端自画的那道光确实存在**：手部一个白色椭圆环（`spam_beam.ao` 的 `beam_flash`/`aux_circle2`），
  但没有端点 ⇒ 长度 0（抓帧 `logs/shots/sirus-h1`）。
- **H1 失败**：给 0x1ab 带上目标对象 id（`PlayerId`）后，客户端仍然只画那个环。
- **行 364 的服务端光束渲染成功**：一条**细红线**从 Sirus 右手射出、穿过玩家、延伸到画外约 15 格
  （截图 `logs/shots/sirus-thin/beam-row364.jpg`，对应日志 17:36:15.386 那一帧；
  日志里是 `[Sirus] beam: …6D01…`，`6D01` = 365 = 行 364 + 1，骨骼名 `R_Weapon`）。
- ⇒ 普攻现在是「官方结构 + 他家细线美术」：起点在手上、方向朝玩家、长度可控、边界清晰；
  颜色偏红（官方是白→橙→红渐变），等几何端点消息到手后可换回 `spam_beam.ao`。

## 未解 / 待办

1. **官方光束的端点由哪条消息给**（几何通道状态 ⇒ 目标实体列表）—— 子任务 `geomstate` 在查（`logs/tmp/re/geomstate.md`）。
2. **官方实机画面从未拍到**：参考图 `logs/shots/ref/wiki_Beam.png`（从手/武器发出、发射点两圈红环、悬空、白橙芯 + 红边）。
   当前服务端不再画普攻光束（已撤），官方形态只能靠客户端自画 ⇒ 依赖 (1) 的端点。
3. **官方普攻用哪个几何行**：332（`spam_beam.ao`）是普攻，342–344（`triple_beam_0{1,2,3}.ao`）是多段光束，359（`clone_trap_beam.ao`）是分身双束 ⇒ 官方实机用哪一行仍不可知。
4. **发射环**（官方普攻的两圈红环）在 `MiscAnimated` 里是 `DieBeamRing{Physical,Cold,Fire,Lightning}{1,2,3}` →
   `Metadata/Effects/Microtransactions/Rings/die_beam_ring/triple_beam_0{1,2,3}.ao`（与 `TripleBeam/triple_beam_0N.ao` 逐行差一行音效）；
   这些行**没有任何 `MiscBeams` 行引用**，`ServerBeamEffect` 走不到，只能**另发效果实体**（EffectObject）。
5. `then beam_02` 要不要真的播第二段，取决于官方是否真的连续两拍；数据里有，但实机无法直接比对。
6. `GeometrySpell` 走 `flag == 0`（读客户端本地技能数据）时到底取到哪个 Effect 资产：实机表证是 **客户端会自建效果并选中官方美术**（手部的白圆环 = `spam_beam.ao`）。
7. **永久解决方案候选**（等用户选）：(a) 继续挖几何状态消息（子任务在跑）；(b) 服务端改用 `ServerBeamEffect` 行 364（Pyre Knight 的细线光束，方向/长度可控）；(c) 维持现状（只有环）。
