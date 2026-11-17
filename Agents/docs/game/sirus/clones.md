# 克隆体 Die Beam（8100 `EmptyActionSpellOrionCloneBeam`）

## 一句话结论

本体站到玩家外围半径 35 格的某一方向，念 "DIE"，播 `trap_beam_01`（GE 8100 绑定的 clip，5330 ms）。
这个 clip 里有 **6 次 `clone_beam_blast` 事件**（4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722 s，每个事件都带一个模型坐标），
圆周上的 **12 个克隆体**依次朝玩家打元素光束。

**客户端动作类 `EmptyActionSpell2`（ActionType 811）只播动画、不画光束**（观察：`vtnear` 在它的 vtable 区间里找不到任何绘图方法）；
6 次 `clone_beam_blast` **没有任何客户端美术**（`.act` 没绑 effect、`.ot` 没监听）⇒ 幻影与光束都必须服务端画。

> **更正 1**：旧文写"伤害技能是 7019–7031 十二个效果"。实际 **十二个元素光束是 7019–7030**
> （`AtlasExileOrionSpammableBeam{Physical,Cold,Fire,Lightning}{1,2,3}`），**7031 是第 13 个 `AtlasExileOrionCloneBeam`**
> （独立效果，SkillID 359、StatSet1 7036）。服务端固定用 7031 名字没错，但"官方随机 7019–7031"应改成"7019–7030 随机"。
> **更正 2**：旧文猜"幻影可能用第 12 号 clip `clone_beam_01`"——`clone_beam_01` / `clone_beam_end_01`
> **在任何 `.ao` 里都不存在**（只在 `atlasexile5.act` / `atlasexile5influencewild.act` 里被绑定 ⇒ 死绑定，客户端播不出来）。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 技能 | GE **8100** `EmptyActionSpellOrionCloneBeam` → ActiveSkill **1246**，CastTime **5330 ms**，`Animation` **35 = `SpellAreaOfEffectChaos`** → 本体 `.act` `"trap_beam_01"`，StatSet1 8092 |
| 终极 | GE **8101** `EmptyActionSpellOrionCloneBeamUber`（1246 / 5330 / 槽 **31 = `SpellProjectileUpwardChaos`** → `trap_beam_uber_01`，StatSet1 8093） |
| 拉幻影 | GE **10147** `AtlasExilesOrionCreateClones`（ActiveSkill 1449 / CastTime 2000 / 槽 **35** → `trap_beam_01`，SkillID 101） |
| 元素光束 | **7019–7030** `AtlasExileOrionSpammableBeam{Physical,Cold,Fire,Lightning}{1,2,3}`（ActiveSkill 1242，CastTime 1000，**`Animation` 为空**）；**7031** = `AtlasExileOrionCloneBeam`（同 AS/CastTime，无动画槽） |
| 本体 GE 列表 | 8376 `AtlasExile5` 的 `GrantedEffectsKeys` 含 **10147 / 8100 / 8101 / 8098 / 8099 / 7019–7031 / 10948–10953 / 10973 / 10141 / 10945 / 10991 / 10974 / 7013 / 7014 / 9851 / 9690 / 7008 / 7012 / 6809** |
| 本体 `.act` | `SpellAreaOfEffectChaos = "trap_beam_01"`、`SpellProjectileUpwardChaos = "trap_beam_uber_01"`、`SpellProjectileUpwardFire = "clone_beam_start_01"`、`SpellBuffChaos = "clone_beam_start_uber_01"`、`SpellProjectile = "beam_01" then "beam_02"`、`SpellProjectileFire = "beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao"`、`SpellAreaOfEffect = "beam_barrage_01"` |
| 本体 `.ot` | `on_event_identify_for_EmptyActionSpell2 = { AddEffectPack('…/body_fx/Trap_Beam_Signal.epk'); PlayEffect('…/body_fx/flash_beam.ao'); }`、`on_end_EmptyActionSpell2 = { RemoveEffectPack('…/Trap_Beam_Signal.epk'); }`、`on_clone_beam_start_01 = { Call(start_clone_teleport); }` / `on_event_teleport_for_clone_beam_start_01` / `on_end_clone_beam_start_01` |
| clip 事件 | `atlasexile5.ao` → `trap_beam_01`：`start_face` @0.0（`StartFaceEventType`，data `"0 0"`；`trap_beam_uber_01` 是 `"80 0"`）、`dialogue` @2.0、`end_face` @3.74214、**`identify` @3.74214**、**6 × `clone_beam_blast`** @4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722；六个 position 全是 x=0、y=−2.9576，z = −223.047 / −216.277 / −214.625 / −212.387 / −213.061 / −211.552（模型单位 ⇒ 正前约 2.1–2.2 格、齐胸高） |
| 同一组事件在克隆体上 | `atlasexile5influence.ao`（**Character rig**）的 `clone_beam_start_01` 里也有 **同样 6 个 `clone_beam_blast`、时间完全一致**；另有 `rocks_off/pointlight_off/eyes_off` @0.0、`contact`+`teleport` @0.5、`rocks_on/pointlight_on/eyes_on` @0.55125、`contact` @0.5667/0.6386/0.72807/1.45 ⇒ **这 6 次爆破事件本来就长在"克隆体"模型上** |
| 幻影模型 | `atlasexile5_beamclone.ao`：**13** 个 clip，`default_animation = "trap_beam_01"`，rig = `Art/Models/MONSTERS/AtlasExiles/TheOrion/Character/rig.amd`（与本体同 rig），SkinMesh `TheOrionExile.sm`；`beam_01` 的 contact 位置 (−9.4614, −91.9149, −173.767)，本体 `beam_01` 是 (−9.46, −106.94, −209.72)（**不同**） |
| 候选光束美术 | `…/monsters_effects/atlasexiles/orion/clone_trap_beam.ao`：client effect object，rig `…/clone_trap_beam/rig.amd`、`default_animation clonetrap_beam`、SkinMesh `clonebeam.sm`、骨组 `beam_length false jntBnd_beam aux_beam`、粒子 `clone_trap_beam_length.pet` 挂 `beam_length`、声音 `Audio/…/Sirus/Effects/Beam/TripleBeam3`、ScreenShake 13 / 0.4 s |

---

## 客户端实现（反编译）

### 类卡片（观察）

| 项 | 值 |
|---|---|
| 类 | `EmptyActionSpell2`（ActionType 811，`HASH16` **7582 = 0x1D9E**；旧笔记写 0x1F5B，**错**） |
| 同族 | `EmptyActionSpell`(732, 18594)、`…3`(812, 7583)、`…4`(813, 7576)、`…5`(814, 7577) —— **共用同一个类与同一张 vtable `off_1434BBFC8`** |
| 类型号怎么来的 | ctor `sub_1419C6500` **不写死类型号**，而是运行时算哈希：把 `"EmptyActionSpell"` 与一个数字拼成字符串（`sub_1404D0650` → `sub_140148EC0(v20, 0, "EmptyActionSpell", 16)`），再跑 `for (i = len; i; --i) v6 = *(char*)v11 ^ (16777619 * v6);` → `sub_1419CF930(a1, (unsigned short)(v6 ^ HIWORD(v6)), a2, a3)`，即 **FNV-1 变体（basis 0、乘 16777619、折 16 位）** |

### 状态机

**无**。这个类没有自己的状态机、没有自己的绘图方法（观察：`vtnear 0x1434BBFC8 0x1419A000 0x1419E000` 返回 **0**）。

### 序列化（客户端收到什么）

- 槽 86 / 87 `sub_1419CF9B0` / `sub_1419CFA20`：**只读写 `*(a1+472)` 一个字节**（与 MoveDaemon 同款 1 字节载荷）。
- 服务端下发的是**动作 + 动画槽**；客户端回传 1 字节。
- 槽 31 启动 `sub_1419CFAB0`：把 `*(a1+816)` 的字符串加上 `"_start"` 后缀，调 `sub_1419CFCF0(a1+792, str)` **按名字播动画**，然后转基类 `sub_1419E9840`。⇒ 它播的是一条**按名字拼出来**的 clip。

### 命中与伤害

**无**。`EmptyActionSpell2` 不产生命中、不判定伤害；6 次 `clone_beam_blast` 客户端侧也没有伤害。伤害完全在服务端。

### 客户端表现

只有三样（观察）：

1. `trap_beam_01` 这条动画本身（本体念 DIE、抬手）。
2. `identify` @3.742：本体 `.ot` 的 `on_event_identify_for_EmptyActionSpell2` 挂上 `Trap_Beam_Signal.epk` 并 `PlayEffect('…/body_fx/flash_beam.ao')`；`on_end_EmptyActionSpell2` 撤掉 effect pack ⇒ **这是官方"克隆体要开火了"的唯一客户端信号**。
3. `dialogue` @2.0 的语音。

**没有**的东西：12 个幻影的显示体、6 条光束、6 次 `clone_beam_blast` 的美术（`.act` 没绑 effect、`.ot` 没监听这些事件）——全部要服务端给。

### 服务端该发什么（要点）

动画 + 本体上的 `identify` 事件（客户端自己闪）+ 12 个幻影 + 6 条光束与 6 次伤害结算。光束本身必须服务端画（详见文末 §服务端该发什么）。

### 未解

1. **客户端到底播哪条 clip**：槽 31 拼的是 `*(a1+816) + "_start"`；而 8100 的 `Animation` 槽 35 绑的是 `SpellAreaOfEffectChaos` → `trap_beam_01`。`*(a1+816)` 的来源没确认（若它就是绑定名，拼出来会是 `trap_beam_01_start`，而 `.ao` 里没有这条 clip）⇒ **播 `trap_beam_01` 还是 `clone_beam_start_01` 未决**。
2. **12 个幻影的官方模型**：`atlasexile5_beamclone.ao`（13 clip、`default_animation trap_beam_01`、Character rig）与 `atlasexile5influence.ao`（Character rig、带那 6 个 `clone_beam_blast`）都像官方克隆体，但 **没有任何 MonsterVarieties 行引用它们**（`AtlasExile5Influence.act/.ao`、`atlasexile5_beamclone.ao` 都没有）⇒ 幻影到底是"本体 `.ao` 的一条 clip"还是独立模型，未解。
3. **谁生成 12 个幻影**：`AtlasExilesOrionCreateClones`（10147）没有数量/半径参数（CastTime 2000、Animation 35），数量 12、半径 35 格都是估的。
4. **`clone_trap_beam.ao` 是给谁用的**：它是最像"两骨之间光束"的客户端 effect object（`beam_length` 骨组 + 拉长粒子 + TripleBeam3 音 + 震屏 13/0.4 s），但**全 orion 树没有任何 `.ot`/`.ao` 引用它** ⇒ 未解。
5. **发散角度**：官方截图里是三条从不同方向发散、在玩家附近交叉的巨型光束；数据里找不到角度参数。

---

## 当前服务端实现与反馈（`Modules/Core/Game/Monster/Sirus.Combat.cs` `Clones()`）

```csharp
special = 3; specialUntil = ready = now + 5.33; var aim = c.Walk.Position(now);
int real = Random.Shared.Next(12);                       // 本体站在圆周上的某一向
await Teleport(c, id, at, now);
await Cast(c, id, 8100, aim, now); await Voice(5688);
for (int i = 0; i < 12; i++) { … await Fx(c, clone, Root + "AtlasExile5.ao", specialUntil, clip: 7, rotation: …); }
double[] blasts = [4.141, 4.282, 4.388, 4.500, 4.597, 4.722];
hits.Add(new(now + blasts[i], body!.ActionGeneration, ++group, 7031, from, Extend(from, aim, 180), 6,
    Beam: true, Track: c, CloneHit: shooters.Count > 0));
```

- 幻影按圆周 12 等分、半径 35 格，不可走的位置跳过（`c.Layout.WalkableFor`）；射击者乱序轮转（`shooters`），保证 6 条光束来自不同幻影。
- 幻影显示体 = **本体 `AtlasExile5.ao` 的第 7 条 clip**（观察：`.ao` 里 animation 条目按序第 7 条是 `clone_beam_start_01`；客户端的 clip 序号是否与文件文本顺序一致未验证）。
- 光束画法：`Beam()` 用 `FireBeam`（行 37）；命中 `Beam: true` → `KitavaActTen.LineDistance(...) <= 6`（`Damage()`，`:951`）。
- `clone_beam_blast` 的模型坐标（正前 2.1–2.2 格、齐胸高）**目前没有用**，光束起点直接用本体位置。

| 时间 | 反馈 |
|---|---|
| 2026-11-16 | 早期版本是 6 条平行紫蓝色光束（`logs/shots/user-report/beams-parallel.jpg`），被指为"Die Beam 应该是三条发散的元素光束" |
| 2026-11-17 | 未再单独反馈过克隆体 |

---

## 服务端该发什么

1. **动作**：对本体发 8100 的 `AnimationUpdate`（槽 35 → `trap_beam_01`），并在 3.742 s 触发 `identify`（客户端会自己闪 `flash_beam.ao`、挂 `Trap_Beam_Signal.epk`）。
2. **幻影**：12 个可见体。首选官方模型 `atlasexile5_beamclone.ao`（13 clip，`default_animation trap_beam_01`）——但它**没有 MonsterVarieties 引用**，得先确定怎么生成；当前先用 effect object + 本体 `.ao` 第 7 条 clip 兜底。
3. **光束**：客户端动作类画不出来（`EmptyActionSpell2` 无绘图方法），所以光束**必须服务端画**：`ServerBeamEffect` / `MiscBeams` 行，或走 daemon 实体。6 次 `clone_beam_blast` 的模型坐标（z = −223.047 … −211.552，即正前 2.1–2.2 格、齐胸高）应该作为**光束起点/落点**，而不是用本体位置。
4. **元素**：官方在 7019–7030（十二个元素光束）里随机，代码现在固定 7031（`AtlasExileOrionCloneBeam`）——名字没错，但元素/颜色多样性缺失。
5. **伤害**：6 次结算（4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722 s），每次一条光束；`CloneHit` 只影响 `BeamBombBeam`（行 240）的历史分支。
6. **未实现**：`dialogue` @2.0 的语音（现在用 5688）、`identify` 的客户端闪光事件、发散角度（当前 6 条光束全部穿过玩家同一点）。
