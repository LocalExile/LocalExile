# 本体多段光束（念 “DIE” 之后，8098 / 8099）

这是玩家从 m00631 起一直在追的那个技能：

> 「希鲁斯的身子后躬，双手聚在身前，发射多段激光，这就是一直没修好的『双手光束』，
> 之前的问题一直是多段激光没有特效，没有真实可见光束，或者光束提前，导致和动作不协调。」

## 一句话结论

技能链是**传送（6809）→ 空动作多段光束（8098 / 终极 8099）**。8098 的动画槽是
**`SpellProjectileFire`，在 `.act` 里绑 `beam_03`**；`beam_03` 的 clip 事件是
`identify` @0.400 + `small/medium/large_beam_blast` @0.841 / 1.098 / 1.370 —— **三段光爆**。
stance2 的 `final_beam_03` 把三段各**翻倍**成六段（.841/.991/1.125/1.264/1.389/1.516）。

**官方的三段光束是三个独立的 Effect 实体**（`MiscAnimated` 行
`AtlasExileOrionSpammableBeamPhysical1/2/3` → `Orion/TripleBeam/triple_beam_01/02/03.ao`），
**不是** `ServerBeamEffect` 的行 227/37。我们现在用 `ServerBeamEffect` 画，等于用一张
9 格宽的熔岩片代替官方的细光束 —— 这就是 m01493 / m02031 两次"满屏"的直接原因。

clip 自带的 `on_act_beam_03.ao` 只负责**蓄力拖尾与爆点粒子**，光束本体仍是服务端发的三个 Effect。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 传送 | `TeleportOrion` = **6809**，ActiveSkill 1113，CastTime 1000，`Animation` **97 = `Teleport`** → `teleport_01` |
| 多段光束 | `EmptyActionSpellOrionMultiBeam` = **8098**，ActiveSkill **1130**（`empty_action_spell`），CastTime 2000，`Animation` **24 = `SpellProjectileFire`** |
| 终极 | `EmptyActionSpellOrionMultiBeam2` = **8099**，CastTime 2500，`Animation` **25 = `SpellProjectileCold`** |
| 同槽的另一个效果 | `AtlasExileOrionSpammableBeamBlastRepeated` = 7031 家族（1242 / 2000 / 24）也走 `SpellProjectileFire` |
| `.act` 绑定（stance1） | `SpellProjectileFire = "beam_03" effect ".../on_act/beam_03/on_act_beam_03.ao" "animate"`；`SpellProjectileCold = "beam_uber_03"`（同 effect） |
| `.act` 绑定（stance2） | `SpellProjectileFire = "final_beam_03"`；`SpellProjectileCold = "final_beam_03"` |
| `beam_03` 事件 | `identify` @0.400；`small_beam_blast` @0.841（0.175, -6.351, -182.589）；`medium_beam_blast` @1.098；`large_beam_blast` @1.370 |
| `final_beam_03` 事件 | 上述三处各两次：.841/.9906、1.1251/1.2643、1.3895/1.5158 |
| on_act | `on_act_beam_03.ao`（挂在 `beam_03` 的 effect 上，由 `.act` 在施法动作里播） |

三个 blast 的位置几乎重合（都在体前 1.8 格、离地 0.06 格），**说明三段打在同一个远处方向上，只是越来越粗**，
不是三个方向。

## 官方美术长什么样（`MiscAnimated` 行，观察）

`data/tables/MiscAnimated.json`：

| 行 | Id | AOFile |
|---|---|---|
| 3702 / 3703 / 3704 | `AtlasExileOrionSpammableBeamPhysical1/2/3` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/TripleBeam/triple_beam_01/02/03.ao` |
| 3705-3707 | `…SpammableBeamCold1/2/3` | 同上 + `_ice.ao` |
| 3708-3710 | `…SpammableBeamFire1/2/3` | 同上 + `_fire.ao` |
| 3711-3713 | `…SpammableBeamLightning1/2/3` | 同上 + `_lightning.ao` |
| 7622-7633 | `DieBeamRing{Physical,Cold,Fire,Lightning}{1,2,3}` | `Metadata/Effects/Microtransactions/Rings/die_beam_ring/triple_beam_0{1,2,3}.ao` |

**这一族 7019–7030（`…SpammableBeam{Physical,Cold,Fire,Lightning}1..3`）就是多段光束的三个命中特效，
每个元素变体三张图 = 三段。** 官方一个元素（比如物理）打三段，就用 `triple_beam_01/02/03.ao`。

`triple_beam_01.ao`（1841 字符，观察）：

| 段 | 内容 |
|---|---|
| `AnimationController` | `Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/rig.amd`，`default_animation = "tripleBeam_01"` |
| `SkinMesh` | `Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/spambeam.sm` |
| `BoneGroups` | `beam_length false jntBnd_beam aux_beam`、`beam_flash false jntBnd_beam aux_circle2` |
| `ParticleEffects` | `Metadata/Particles/monster_effects/AtlasExiles/Orion/impact_beams/triple_01.pet`，挂 bone_group `beam_length` |
| `SoundEvents` | `Audio/…/AtlasExiles/Sirus/Effects/Beam/TripleBeam1`，挂 `jntBnd_beam` |
| `RenderPasses` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/TripleBeam/triple_beam_01_ao_pass0.mat`（Append） |
| `ScreenShake` | `tripleBeam_01` @0.0，duration 0.2，global，LinearDown |

要点：

- **它与普攻的 `spam_beam.ao` 共用 rig 与网格**（`impact_beams/rig.amd` + `impact_beams/spambeam.sm`），
  差别只有材质（`triple_beam_01_ao_pass0.mat` vs `spam_beam_ao_pass0.mat`）、动画名
  （`tripleBeam_01` vs `beam_strike`）和粒子（`triple_01.pet` vs `beam_flash.pet`）。
  **"普攻光束"与"多段光束"是同一套模型的两套皮**，这也解释了参考图里"单条只有 1/8~1/10 宽"的观感。
- **长度是数据驱动的**：`beam_length` bone group 决定光束多长、`beam_flash` 决定端头闪光；
  裸放 Effect 只会得到模型默认长度（官方由动作数据写进 bone group）。
- **`DieBeamRing*` 不是"环"**：把 `die_beam_ring/triple_beam_01.ao` 从 GGPK 解出来与
  `Orion/TripleBeam/triple_beam_01.ao` 逐行 diff，**唯一差别是音效**
  （`Microtransactions/SirusDieBeamRingMTX/TripleBeam1` vs `MonsterSounds/…/TripleBeam1`）——
  它是同一张图的微交易（MTX）版本。不要当成另一种视觉。

## 客户端自己画什么 / 服务端必须画什么

- **客户端**：`beam_03` 的动作（后仰、双手聚在身前）、`on_act_beam_03.ao` 的**蓄力拖尾与爆点粒子**、
  以及三次 `*_beam_blast` 事件本身（事件是给客户端"该炸了"的信号，`.ot` 里没有绑定任何画光束的东西）。
- **服务端**：**三个（终极六个）独立的光束 Effect**（`triple_beam_0N.ao`，挂在手骨上）+ 命中判定。
  三段光束**不是** `ServerBeamEffect` 画的——官方没有用 Beam 组件，一次光爆就是一个效果实体。

### 已确认的两件事

1. **包是对的**：`BeamEffectObject` 的布局与客户端 `0x1416710c0` 逐字段吻合（见 [README](README.md)），
   实机日志里能看到 `E400 D7 0C … 525F576561706F6E`（行 227 + `R_Weapon`）。
2. **没有 `StartBone` 就没有光束**：flag `0x97` 时代的现象是"包里发了、屏幕上看不见"，因为客户端把近端锚在
   施法者原点（贴地），整根网格画在地面以下。改成 `R_Weapon`/`L_Weapon` 交替后，实机帧里
   `logs/shots/k4/frame-0189` 确实出现了从双手发出的红白光束。

## 当前服务端实现

`Modules/Core/Game/Monster/Sirus.Combat.cs`：

- 触发（`kind == 1`，`:316-323`）：
  ```csharp
  var at = RingPoint(c, aim, 30 + Random.Shared.Next(20), now);
  await Cast(c, id, 6809, at, now); await Teleport(c, id, at, now);
  deferred.Add(new(now + .5, body.ActionGeneration, c, at, aim));
  ```
- 延迟段（`:826-843`）：
  ```csharp
  await Cast(c, id, Round == 3 ? 8099 : 8098, cast.Aim, now);
  var poseBarrage = Round == 3 ? 12 : 3;      // beam_03 / final_beam_03 在 .ao 动画表里的下标
  foreach (var p in Present.ToArray())
      await SendWorldRecord(p, id, () => InstanceProtocol.AnimationUpdate(id, 0, (uint)poseBarrage, 1));
  double[] contacts = Round == 3 ? [.841, .991, 1.125, 1.264, 1.389, 1.516] : [.841, 1.098, 1.370];
  hits.Add(new(now + contacts[i], body.ActionGeneration, ++group, 7019 + (i % 4) * 3, cast.From, Extend(cast.From, cast.Aim, 180), 4,
      Beam: true, Track: cast.Target, Bone: i % 2 == 0 ? "R_Weapon" : "L_Weapon"));
  ```
- 绘制（`Beam()`，`:228`）：`ushort row = FireBeam.Value;`（行 37，`firebeam_beam.ao`，2.9 格宽），
  近端由骨骼定位，远端 = 玩家当前位置（`Track`）。
- 落点爆发（`:879`）：`if (hit.Effect is >= 7019 and <= 7031) await Fx(c, draw, Art + "hit_fx_beam.ao", now + .5, clip: null);`

## 名字很像、但**不是**这个技能的三样东西（重要）

| 名字 | 真实归属（观察） |
|---|---|
| `beam_barrage_01` | 动画槽 `SpellAreaOfEffect`(**22**) → **走廊** `AtlasExileOrionCorridorBlast`(10948)。clip 里 8 次 `beam_right/beam_left`（1.173–5.812 s）+ `create_corridor` @0.461 |
| `standalone_beam_barrage_01` | 动画槽 `SpellProjectileChaos`(**27**) → `AtlasExileOrionDeathBarrage`（**1247**，独立效果，本战斗未使用） |
| `trap_beam_01` | 动画槽 `SpellAreaOfEffectChaos`(**35**) → 克隆体 `EmptyActionSpellOrionCloneBeam`（8100） |

也就是说：早期把 8 束手部光束当成"这个技能"是按名字猜错的，`beam_barrage_01` 属于走廊。
真正的多段光束是 `beam_03` 的三段（终极六段）。

## 玩家反馈时间线

| 时间 | 反馈 |
|---|---|
| m00631 | 定义目标：后躬、双手聚在身前、多段激光 |
| m00730 | 「姿势对，但是没有光束出来。闪了多次，应该是按照多段做的」 |
| m00850 | 在多选题里选定 B =「念 Die. 之后的多段光束」 |
| m02016 / m02031 | 「到底能不能修好」「这个激光也是错的」——此时用的是行 227，仍然满屏 |
| m02045 | 「别猜了，开ida详细地逆向分析一下每个光束技能的实现」 |

## 未解 / 待办

1. **长度**：官方光束的长度由 bone group `beam_length` 按技能数据驱动；裸放 `triple_beam_0N.ao`
   只能得到模型默认长度。要完全对齐官方，还要追 `sub_141E59F80` 怎么把属性写进 Effect 的 bone group。
   （**实机第一步**：只把三段光束从 `ServerBeamEffect` 换成三个 Effect，看宽度与"从手出发"是否对上；
   长度不对再往下挖。）
2. **段数与方位**：数据里三段都在同一方向，官方实机是不是"三束平行/扇形"没有画面证据。
3. **`identify` @0.400** 没有对应实现：官方可能在 0.4 s 锁定目标朝向，我们直接按玩家位置画。
4. `hit_fx_beam.ao` 只在落点画一次 0.5 s，官方 `*_beam_blast` 是否还有更长的地面残留未验证。
5. **元素变体**：官方按伤害元素换图（`_ice` / `_fire` / `_lightning`）；我们的多段光束目前没有元素，
   先用物理版 `triple_beam_01..03.ao`。

## 客户端实现（反编译，2026-11-17）

**结论先说**：`8098` / `8099` 这两条技能在客户端**不画任何光束**；画面上的多段激光来自动画 clip 自带的 `on_act` 特效（4 条 trail 带 + 3 次爆点粒子）。服务端只需要「让客户端播 clip」＋「按 blast 事件结算伤害」，多余的 `ServerBeamEffect` 反而是画蛇添足。

### 1. 技能 → 客户端类

| 项 | 值 |
|---|---|
| GrantedEffect | `8098 EmptyActionSpellOrionMultiBeam`（`Animation` = **24** `SpellProjectileFire`，CastTime 2000） |
| GrantedEffect | `8099 …MultiBeam2`（`Animation` = **25** `SpellProjectileCold`，CastTime 2500） |
| ActiveSkill | **1130 `empty_action_spell`**（`ActionType` = 732） |
| 客户端类 | **`EmptyActionSpell`**（`ActionTypes.json` 里 `HASH16 = 18594 = 0x48A2`） |

### 2. 类的装配链（观察）

- 类名字符串 `0x1434c7128 "EmptyActionSpell"` ← 包装 `sub_14196EEA0`：`_crtInitOnceExecuteOnce("EmptyActionSpell", sub_1419BF0B0, sub_141271520, 0); return &unk_1447602A2;`
- 工厂 `sub_1419BF0B0`：探测 ctx 属性 `876`/`332`/`858` 决定变体，否则 `sub_142C8CB50(840)` 分配 **840 字节**，`sub_1419CF930(obj, 18594, …)`（基类 ctor，第二参就是类名哈希），再写主 vtable `off_1434BBFC8`、次接口表 `off_14323E408`。
- `EmptyActionSpell2/3/5`（8100、DeathBarrage 等）走 `sub_1419C6500 / sub_1419C66A0 / sub_1419C6840`：运行时把 `"EmptyActionSpell"` 与数字拼成字符串、算 **FNV-1 变体 16 位哈希**（`v6 = byte ^ (16777619 * v6)`，再 `v6 ^ HIWORD(v6)`），然后调同一个基类 ctor 和**同一张 vtable**。⇒ 这一族是同一个类，只是类型号不同。

### 3. 它到底做了什么（覆盖的槽）

- **slot 31 启动** `sub_1419CFAB0`：构造字符串 `*(a1+816) + "_start"`，交给 `sub_1419CFCF0(a1+792, str)`，再转基类 `sub_1419E9840`。即「按名字播一个动画」，没有创建任何光束对象。
- **slot 86 序列化** `sub_1419CF9B0` / **slot 87 反序列化** `sub_1419CFA20`：只写/读 `*(a1+472)` 一个字节（状态标志），24 字节级的极简上报。
- vtable `off_1434BBFC8` 里其余槽全部落在基类 `Actions::Action` 的公共区（`sub_1419E9D30` 等），`vtnear 0x1434BBFC8 0x1419A000 0x1419E000` 返回 **0 个**——这个类没有自己的绘图方法。

### 4. 光束的视觉在 `.act` 的 `effect` 子句里（关键）

`Metadata/Monsters/AtlasExiles/AtlasExile5.act`：

```
SpellProjectileFire = "beam_03" effect "…/Orion/on_act/beam_03/on_act_beam_03.ao" "animate";
SpellProjectileCold = "beam_uber_03" effect "…/Orion/on_act/beam_03/on_act_beam_03.ao" "animate";
   stance2:
SpellProjectileFire = "final_beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao" "animate";
SpellProjectileCold = "final_beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao" "animate";
```

`on_act_beam_03.ao`（5854 B）内容（解码后）：

- `ClientAnimationController skeleton = …/on_act/beam_03/on_act_beam_03.ast`，`SkinMesh {}` **空**
- `BoneGroups`：`curve_01..04`（绑 `aux_trail_curve3 / curve2 / curve0 / curve1`）、`orbEmit`（`aux_orbEmit`）、`grd_Emit`（`aux_mid aux_line`）、`blast`（`aux_orbEmit aux_orbEmit1`）
- `ParticleEffects`：`orbEmit.pet` @0.617、`blast.pet` @0.831 / @1.094 / @1.363
- `TrailsEffects`：`on_act.trl` 挂在 `curve_01..04`，`time 0.0928 → end_time 1.9668`

`on_act.trl`（2902 B，两段）：

| 段 | material | segment_distance_threshold | max_segment_distance | segment_duration | scale |
|---|---|---|---|---|---|
| 1 | `…/Orion/death_projectile/ground_bits.mat` | 1 | 800 | 0.21 | 1.165 |
| 2 | `…/Orion/body_fx/chargeup_trail.mat` | 30 | 1000（=10 格） | 0.3 | **8.32** |

⇒ 三段「激光」其实是**两条 trail 带沿 4 根曲线骨骼逐段生成**，每段最多 10 格，宽度由 scale 8.32 × material 决定；爆点粒子时刻 0.831 / 1.094 / 1.363 与 clip 的 `small/medium/large_beam_blast`（0.841 / 1.098 / 1.370）几乎重合，说明**伤害与特效是同一时刻**。

## 服务端该发什么

1. 只发 **动画**：`AnimationUpdate(target, 0, 3, 1)`（stance1 = `beam_03`，全局第 7 号减 4）或 `12`（stance2 = `final_beam_03`，全局 20 减 8）。
2. 按 `contact/blast` 时刻结算伤害（现有 `contacts = [.841, 1.098, 1.370]`，终极 6 段）——这部分代码本来是对的。
3. **光束改为三个独立 Effect**：在每个 `contact` 时刻发一个 `EffectObject`
   （`Metadata/Effects/PermanentServerEffect`），`AnimatedObject` = `…/Orion/TripleBeam/triple_beam_01.ao`
   （第二段 `_02`、第三段 `_03`；终极翻倍则重复发一遍），
   `Parent` = 施法者 id、`Bone` = `R_Weapon`/`L_Weapon` 交替、`AttachKind = 0`、`AttachFlags = 3`
   （保留骨骼朝向，参考 Arrow Nova 的箭），寿命 ~0.2–0.6 s。
   `EffectObject` 的这些字段在 `src/GameServer.Core/Protocol/Instance/InstanceEntities.cs:859-931`。
4. **不要再为 8098/8099 发 `ServerBeamEffect`**：行 37/227 会把画面盖成"满屏火焰"
   （即 m01493 / m02031 两次被否的直接原因），而官方这族技能根本不走 Beam 组件。
5. `on_act_beam_03.ao` 的蓄力拖尾与爆点粒子由客户端自己播（`.act` 的 `effect` 子句），**服务端不用管**；
   若实机看不到蓄力，先确认 `.act` 的 effect 是否被客户端加载，而不是加自己的特效。

## 实机结果（2026-11-17 傍晚）

用户拍板「多段光束用三个 `triple_beam_0N.ao`」后的改动：8098/8099 的三段不再发 `ServerBeamEffect`（行 37），
改为在三个 contact 时刻各发一个 `EffectObject`：
`…/Orion/TripleBeam/triple_beam_01/02/03.ao`，`Parent` = Sirus、`Bone` = `R_Weapon`/`L_Weapon` 交替、
`AttachKind = 0`、`AttachFlags = 3`；命中点另发 `hit_fx_beam.ao`。

实测（`SIRUS_ONLY_KIND=1`，抓帧覆盖 17:49:13.531 那次施法，截图 `logs/shots/sirus-kind1c/barrage3.jpg`）：

- 画面上确实出现了**从 Sirus 手部射出、指向玩家、末端有明亮爆点**的一道光束
  ⇒ 官方 `TripleBeam` 美术已被选中并画出来了（不再是行 37 的火焰墙）。
- 但它**偏宽、偏熔融**（约为行 364 细线的 6–7 倍宽，≈1.5–2 格）：`triple_beam_0N.ao` 的 `beam_length`
  骨组没人驱动（没有几何端点），效果按模型默认长度/宽度渲染。
- ⇒ 要「官方细线」观感仍要等几何端点消息；在那之前若嫌宽，可把这三段也换成行 364（细线，方向/长度可控）。

