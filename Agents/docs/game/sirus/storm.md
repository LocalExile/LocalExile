# 转阶段风暴与死亡区（`InvisibleFireOrionDeathZone` / `deathzone.ao`）

## 一句话结论

风暴/黑圈在官方**不是特效，是一个怪物实体**：`InvisibleFireOrionDeathZone` 的四个变体都用 `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionDeathZone.ao`，客户端自己把它画出来（5 条粒子 + 3 条声音）。本体的 `create_deathzone` 事件（`Emerge2` = `throne_activate_01` @**2.3439 s**）**在客户端没有任何处理器** ⇒ 收到也不画。风暴光环（ActiveSkill 1116）走 `AddBuffToTarget` 动作类，**零覆写、无造物** ⇒ **上 buff 不产生任何可见物**，光环的伤害与减速全在服务端。
⇒ 正路：服务端生成 `InvisibleFireOrionDeathZone*` 怪物实体 + 按 `GrantedEffectsKeys` 原顺序发布它的 12 个效果 + 逐级发冲击波 action；`Fx(... "deathzone.ao")` 是绕路（见文末）。

---

## 客户端数据（观察）

| 项 | 值 |
|---|---|
| 怪物变体 | `data/tables/MonsterVarieties.json` 行 **3282–3285**（0 基索引） |
| 美术 | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionDeathZone.ao`（同名 `.act`） |
| 基型（决定尺寸与脚本） | 3282 = `AtlasExile5DeathZoneMobile`、3283 = `AtlasExile5DeathZone`、3284 = `AtlasExile5DeathZoneApparition`、3285 = `AtlasExile5DeathZoneMobileSupporter` |
| 效果（`GrantedEffectsKeys` 原顺序） | 3282：`6833, 6834, 10981, 10982, 7074–7081`；3283：`6833, 6834, 10981, 10982, 4858`；3284：`6833, 6835, 7074–7081`；3285：`6836, 6837, 10983, 10984, 7074–7081` |
| 光环效果 | `AtlasOrionDeathZoneAura`(6833) / `…AuraCore`(6834) / `…AuraCoreApparition`(6835) / `…AuraSupporter`(6836) / `…AuraCoreSupporter`(6837) + `…Minion`(10981–10984) = ActiveSkill **1116** `add_buff_to_target_triggered`，ActionType **720** = `AddBuffToTarget`，CastTime 2000，**Animation 空** |
| 冲击波 | `AtlasOrionDeathZoneShockwave`(7074) … `…Shockwave8`(7081) = ActiveSkill **1242** `geometry_spell`（ActionType 807），CastTime 1000，`active_skill_area_of_effect_radius_+%_final` 0, 50, … 350（逐级放大） |
| 召唤 | `SSMOrionApparitionSummonDeathZone`(3629) = ActiveSkill 499 `summon_specific_monsters_on_self` |
| 几何动作动画槽 | `data/tables/Animation.json` 行 **22** = `SpellAreaOfEffect` |
| 尺寸 | `deathzone.sm` BoundingBox `±1500.5`（100 模型单位 = 1 格）⇒ scale 1 时**直径 30 格**、地下深 4.6 格 |
| 房间道具 | `Tools/re/out/sirusnum/arena/fountain.arm` 摆 **6 个** `InvisibleFireOrionDeathZoneStationary`： (375,463) (513,223) (374,273) (223,350) (181,538) (536,441) |

光环每分钟伤害（每种元素各一份，见 `Agents/docs/game/sirus.md` 的 storms 一节）：6833 = 35,476（591/s ×4）、6834 = 354,762（5,913/s ×4）、6835 = 570,087（9,501/s ×4）、10981/10982 = 83/s / 887/s（×4）。

### 两份 deathzone 美术（观察）

| | 怪物版 `OrionDeathZone.ao` | 特效版 `deathzone.ao` |
|---|---|---|
| 路径 | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/` |
| rig / 网格 | `…/death_zone/rig.amd` + `…/death_zone/deathzone.sm`（同一套） | 同 |
| `default_animation` | 无（动画由 `.act` 的 `Idle = "idle"` 给） | **有** `"idle"` |
| 声音 | `DeathCloud.loop.ogg`(root)、`EarthCrumble.loop.ogg`(aux_up_circle)、`DeathCloudStart.once.ogg`(aux_cyl2) | **无** |
| 骨骼组 | 多 `root` / `frameupper` | 无 |
| 粒子（都 t=0） | `cloud.pet`(line)、`death_core.pet`(sphere_core)、`lightning_circle.pet`(up_circle)、`grd_rocks.pet`(center_circle)、`grd_shockwaves.pet`(shockwave) | 同五条 |

⇒ 怪物版才有声音与根骨骼；特效版能独立播但**没有声音**。`…DeathZone.act`：`Idle = "idle"`、`Taunt`/`Run` = `@Idle`、`Death = "idle"`、`movement_speed 10.0`、`action_set minion`。

### 旧记录更正

1. 风暴的圈是**怪物版** `OrionDeathZone.ao`（有声音），`deathzone.ao` 是特效版（无声音）——旧记录把两者混为一谈。
2. 我们生成的 `InvisibleFireOrionDeathZone` 用的是 **Mobile** 基型（`base_actor_scale_+% = -80`）⇒ 不是 30 格大圈；满尺寸的是 `…DeathZoneStationary`（基型 `AtlasExile5DeathZone` 没有 -80）。
3. `…Supporter` 同样是 Mobile 基型（也带 -80）外加 `ModelSizeMultiplier 50`；两个乘数怎么叠加**未解**。
4. 房间里是 **6 个** `…DeathZoneStationary`（旧记录写 4 个）。
5. 冲击波是 8 级 `7074–7081`，名字是 `…Shockwave`…`…Shockwave8`（没有 `Shockwave1`）。
6. `Fx(... "deathzone.ao", scale: 2f)` 是**绕路**，正路是生成怪物实体（见文末）。

## 客户端自己画什么 / 服务端必须画什么

- **客户端画**：① 怪物实体自己的 `OrionDeathZone.ao`（粒子 + 声音 + `idle` 循环 250 ms）；② 冲击波（`geometry_spell` 动作，客户端自建 584 字节 Effect）；③ 本体 `Emerge2` 的动作与 `flashbang` 闪白；④ 玩家身上的光环贴图（本体 `.ot` 的 `client.Preload` 预载了 `preload_buff = "atlas_orion_death_zone"` / `"atlas_orion_death_zone_core"` 与 `preload_animated_object = "…/dz_debuff.ao"`）。
- **客户端不画**：① `create_deathzone` 事件（本体 `atlasexile5.ot` 全文搜 `deathzone` 只有两行 `preload_buff`，**没有 client 处理器**）；② 上 buff 本身（`AddBuffToTarget` 无造物）。
- **服务端必须做**：生成风暴实体（带官方 stat）、发布它的 12 个效果、按时发 8 级冲击波 action、算光环伤害与减速。

## 当前服务端实现

- 变体名 `Sirus.Native.cs:12-13`，特效目录前缀 `:14`；`Effects(v)` 只认本体与分身（`Sirus.Native.cs:33`）⇒ **风暴一个技能都没发布**（`SkillsFor` `:46-48` 返回 null）；技能号约定 `SkillBase = 0x4000`（`:35`），号 = `0x4000 + GrantedEffectsKeys` 的下标。
- 生成：`Sirus.cs:274`（stage 5→6）`uint storm = await Actor(c, Storm, Position(now)); RegisterStorm(storm, c.Monsters[storm], now);`；开场道具 `Sirus.cs:181-184`。
- 转阶段 `Sirus.cs:306-341` `Ascend()`：本体/分身瞬移到 `scene.Centre` → 生成王座 → 本体 `("throne",1),("norendering",1)` → `Basic(c, id, "Emerge2", now, 10)` → **`:337-338` `stormAt = now; stormArt = await Fx(c, scene.Centre, Art + "deathzone.ao", now + 13, clip: null, scale: 2f);`（绕路）** → 语音 `5681 + min(2, Round)` → `stage = 5; deadline = now + 8`。
- 收尾 `Sirus.cs:278-305` `Descend()`：撤掉 `stormArt`（`:283-287`）、`ClearNearbyStorms(c, Position(now), 70)`（`:304`）。
- 伤害 `Sirus.Damage.cs:62-74` `Dot()` 读 `base_*_damage_to_deal_per_minute / 60`；`:81-82` 38 格用 6833（minion 10981）、13 格用 6834（minion 10982）；`:106-110` 圈内 `action_speed_-% 25`。
- 冲击波 `Sirus.Combat.cs:782-789`：移动风暴 `Born + 3` 之后每 0.2 s 一次（**没有发 action**）。

## 客户端实现（反编译）

### 动作类 `AddBuffToTarget`（光环 1116 用的就是它）

**类卡片**（观察）

| 项 | 值 |
|---|---|
| 类名 / HASH16 | `AddBuffToTarget` / **0x6DE2 (28130)** = `ActionTypes.json` 行 720 |
| 工厂 / 分配 | `sub_14198E350` / **792 字节**（最小的那几个 Action 之一） |
| 主 vtable | `off_1434CB088` |
| 覆写 | **零**：28 个槽全部落在基类代码区 `0x14193xxxx`/`0x14194xxxx` |
| 读 ctx 属性 | **12731**（动画播放速度倍率）；基类 ctor 另读 2322/48/12674/9080/876/22504/2495 |
| 造物 | **无**：没有 `sub_142C8CB50(584)` + `Metadata/Effects/Effect`，没有 `sub_141F6A910` |
| dat 表 / 目标选择 | **无** / **无**（不遍历目标、不取位置参数） |

**状态机**：本类没有自己的状态，全部行为 = 基类启动 `sub_1419E9840`（712 B）：

1. `sub_1419E71C0()` 复位；`+488 != 0`（已在"准备中"）直接 return。
2. 选动画编号：虚槽 528/1232/1248 → `sub_14193E130(a1,&anim,v5)`；`idx = (anim != 0) ? (anim - *table)/77 : 179` —— **取不到时默认 179**，而 `Animation.json` 只有 179 行（索引 0–178）⇒ 179 越界（推断：什么也不播；客户端的动画表与这份抽取是否同一份未核）。
3. 装参数：`args[0]=idx`、`args[1]=vtable[792]`（动作 id）、`args[2]=*(u32*)(a1+720) + *(u16*)(a1+282)`（起始帧 + 时长）、`args[3]=sub_141944680(a1)`（朝向）。
4. 速度：ctx 12731 非 0 ⇒ `vtable[1176]/100 + 1`；否则 `vtable[1192]` 真则 `vtable[1184]`；再否则 1.0。
5. 播：`sub_141EA0C60(ent,args)`（`anim+27 != 0` 时改走 `sub_141EA2420`）—— **在施法者实体上播一段动画**，这是它唯一的副作用。

**序列化（客户端收到什么）**

- 这个动作**不读包里的任何资源串**（对比 `GeometrySpell`：它读 2 个资源串 + 2 个 u32）。客户端只拿到"技能 id + 坐标"的 ActionStarted。
- 结论（观察）：服务端发 `add_buff_to_target_triggered`，客户端最多"抬一次手"，**不会创建任何新对象**。

**命中与伤害**

- 客户端**不判定命中**：没有目标列表、没有范围、没有 `contact`；目标完全由服务端解析。光环伤害 100% 服务端算：6833/6834/6835/10981/10982 的 `base_{物理,火焰,闪电,混沌}_damage_to_deal_per_minute`。
- 我们就是这么做的（`Sirus.Damage.cs:62-82`）：38 格 rim（6833 / minion 10981）、13 格 core（6834 / minion 10982），圈外无；圈内 `action_speed_-% 25`。

**客户端表现**

- 官方风暴的可见部分只有三样：① 怪物实体的 `OrionDeathZone.ao`；② 冲击波 action 自己建的 Effect；③ 玩家身上的光环贴图（buff 自己的特效 / 预载的 `dz_debuff.ao`）。
- **结论（观察）**：上 buff 不造任何可见物；**风暴光环 1116 上 buff 时客户端也不造可见物**。

### 造可见物只有两个入口（客户端全局通用）

| 入口 | 说明 |
|---|---|
| `Effect` | **584 字节**实例；资源入口固定串 `Metadata/Effects/Effect`（23 字符）；内容由 ActionData 属性 `0x7A39632A`（2050581290）或包里的两个资源串灌入 |
| `sub_141F6A910(owner, attrId, pos, mode2)` | 按属性实例化对象 |

⇒ 判断"客户端会不会自己画出某个东西"：先看技能是不是几何类动作（807 `GeometrySpell` ⇒ 客户端自建 Effect），再看有没有属性驱动的实例化。`AddBuffToTarget` **两个入口都不走**。

### 落点标记：`GeometryTrigger`（备选路径，官方风暴没用）

`GeometryTrigger` = HASH16 **0x27ED (10221)**，968 字节，主 vtable `off_1434480E8`；用 ctx 属性 **6433** 选 `Data/GeometryTrigger.dat` 的行；启动槽 `sub_141516100` 自己建 584 字节 Effect + **随机挑一个 `GroundMarker`** + **随机 yaw** + 播 `start`/`loop`；收尾 `sub_1415159E0` 播 `end` 并释放。
⇒ 想让风暴在地面画落点标记，**服务端只要发这个动作并带上属性 6433，客户端自己画**（细节见 `logs/tmp/re/GeometryTrigger.md`）。但官方风暴的冲击波走 `geometry_spell`（1242），**不是** `GeometryTrigger`。

### 服务端该发什么（摘要）

1. 生成 `InvisibleFireOrionDeathZone` 实体并给官方 stat（`base_actor_scale_+% = -80` 等）—— 见文末。
2. 按 `GrantedEffectsKeys` 顺序把这 12 个效果发布成它的技能（`0x4000 + 下标`）—— 现在一个都没有。
3. 逐级发 8 个冲击波 action（`0x4004..0x400B`），半径 0 → 350 %。
4. 光环伤害与减速全部服务端算，**不要**指望客户端。

### 未解

见文末「## 未解 / 待办」；本节最要紧的一条：**没有实机截帧证明客户端画出了风暴实体**。

## 服务端该发什么

1. **实体**：`Actor(c, Storm, pos)`（已在 `Sirus.cs:274`），但要补官方 `.ot`（`atlasexile5deathzonemobile.ot`）的 stat：
   - `base_actor_scale_+% = -80`（Mobile 基型；**不补就是 30 格大圈**；服务端把 `base_actor_scale_+%` 当 `actor_scale_+%` 下发，见 `src/GameServer.Gateway/Monster/MonsterMods.cs:183-184`；`ModelSizeMultiplier` 由 `src/GameServer.Gateway/Monster/MonsterCatalog.cs:354` 单独给）。
   - `base_cannot_be_damaged`、`base_cannot_be_stunned`、`cannot_be_knocked_back`、`untargetable_by_monster_ai`、`immune_to_curses`、`immune_to_auras_from_other_entities`、`set_item_drop_slots = 0`、`corpse_usable = false`；客户端状态 `Targetable { no_highlight = true; targetable = false }`。
   - 存活时间：`DiesAfterTime {}` 是空的 ⇒ **官方时长不由数据决定**（推断：由施法者/技能控制）⇒ 服务端自己定（我们的 intermission 8 s）。
2. **技能发布**：把这 12 个效果按 `GrantedEffectsKeys` 的**原顺序**编号（`SkillBase = 0x4000`，`Sirus.Native.cs:35`）：`0x4000=6833`、`0x4001=6834`、`0x4002=10981`、`0x4003=10982`、`0x4004..0x400B = 7074..7081`。现在 `Effects(v)` 对风暴返回空（`Sirus.Native.cs:33`）⇒ 风暴没有任何技能，客户端也就无从跑冲击波。
3. **冲击波**：每级一个 `geometry_spell` action，客户端自己画（几何类动作会自建 Effect）。半径按 `active_skill_area_of_effect_radius_+%_final` 0, 50, … 350 逐级放大；级间隔数据里没有，我们的 0.2 s（`Sirus.Combat.cs:782-789`）是猜的 ⇒ 需实机校。
4. **光环**：官方路径是"怪物施放 1116 → 服务端给玩家上 `atlas_orion_death_zone` buff"；我们现在是**纯服务端 DoT**（`Sirus.Damage.cs:81-82`）+ `action_speed_-% 25`（`:106-110`），玩家身上没有官方那圈贴图。要还原观感就得给玩家上 buff（视觉走客户端的 buff 特效）。
5. **撤掉绕路**：`Sirus.cs:337-338` 的 `Fx(... "deathzone.ao", scale: 2f)` 换成上面 1–4。绕路的三处硬伤：特效版 `deathzone.ao` **没有声音**（怪物版才有 `DeathCloud.loop.ogg` / `EarthCrumble.loop.ogg` / `DeathCloudStart.once.ogg`）；没有实体自带的 5 条粒子绑定；不随实体消失，也不会被 `ClearNearbyStorms`（`Sirus.cs:304`）按距离清掉。过渡期可以保留 `Fx`，但要在提交说明里写明是临时做法。

## 玩家反馈时间线

| 时间 | 反馈 |
|---|---|
| 2026-11-16 | 交接文档：「转阶段的风暴和不断扩大的中心崩解黑圈都还没做」 |
| 2026-11-17 | m01489「继续做转阶段风暴」——尚未修好 |

## 未解 / 待办

1. **客户端到底画没画风暴实体**：服务端已经在 `Sirus.cs:274` 生成 `InvisibleFireOrionDeathZone`，但**没有任何实机截图**证明客户端画出了 `OrionDeathZone.ao`。旧记录里"地板是空的"是 `create_deathzone` 的结论，不是这个实体的结论 ⇒ 下一步先实机截帧。
2. **模型实际直径**：`base_actor_scale_+% = -80`（20 %）与 `ModelSizeMultiplier`（Supporter = 50）怎么叠加，未解；推算 Mobile = 6 格、Supporter = 3 格，需实机量。
3. **冲击波的节奏**：`active_skill_area_of_effect_radius_+%_final` 只给半径，级与级之间隔多久没有数据 ⇒ 0.2 s 是猜的。
4. **几何动作取哪个 Effect 资产**：`GeometrySpell` 走 `flag == 0` 分支时读客户端本地技能数据，取到哪个资产**未解**（`body-beam.md` 记了同一条）—— 冲击波用的就是它。
5. **风暴的墙**：官方有把人挡在场内的风墙；四个 `.ot` 都是 `collision_hull_type = "projectile"`（Supporter 是 `"walk"`）、`blocking = false` ⇒ 风暴本身不挡人 ⇒ 那道墙是别的东西，未解。
6. **被召唤的变体**：`SSMOrionApparitionSummonDeathZone`(3629) 的 `alternate_minion` **611** 在 `MonsterVarieties` 里对不上行（行 611 是 Bonewarped Archer）⇒ 分身召出的到底是哪个变体，未解（`Agents/docs/game/sirus.md` 的 storms 一节记了同一个死胡同）。
7. **房间里的 6 个 StationaryStorm**：开局就在（`fountain.arm`），现在由 `Sirus.cs:181-184` 生成；它们该不该在开战时就存在，需要跟玩家确认。
