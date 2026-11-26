using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// Damage on a frozen army member and its break-out. While revealed and unbroken, a frozen member takes normal combat damage
/// (hits, crits, resistances, armour, energy shield, damage over time, minions, totems, brands, traps, mines, culling strike)
/// with a non-lethal life floor (<see cref="SpawnedMonster.OnNonLethalZero"/>): when damage would drop life to 0, life stops
/// at 1, Contagion spreads if active, EntranceGrace re-engages so no further hits apply, and BreakOut sends state 3 (shards,
/// breaking_free, 3 s ramp) and refills life/ES so the client hides the bar. It stays where it stands, still without AI,
/// until the encounter ends (ReleaseArmy wakes it).
/// </summary>
static partial class LegionMonolith
{
    /// <summary>The chest's break-out reach, from the client's code 0x141c496f0 (R11 section 5): a square query of 45 cells scaled by
    /// map_legion_chests_break_out_range_+%, plus 5 cells. Whether the 45 is the half side is a GUESS (live check).</summary>
    const double ChestRadius = 45, ChestMargin = 5;
    /// <summary>How long a one-shot effect object stays before it is removed.</summary>
    const double FlashSeconds = 3;
    /// <summary>GUESS: the rolls an opened chest and an opened War Hoard drop.</summary>
    const int ChestRolls = 3, HoardRolls = 6;
    /// <summary>MiscAnimated 3326 LegionChestBreakoutExplode.</summary>
    const int ChestExplodeKey = 3326;
    /// <summary>Official-capture floating height for chest reward symbols: Positioned fixedHeight (25, -195.3125f = 25 * -7.8125).</summary>
    const uint ChestSymbolHeightSteps = 25;
    const float ChestSymbolHeightZ = -195.3125f;

    /// <summary>The openable chest that a broken-out war chest became (kept in the save; its id changes when it is opened). Rewards are its
    /// LegionRewardTypeVisuals rows; Faction is the LegionFactions ROW of its army (-1: unknown, an old save); SymbolIds are the floating
    /// 3D reward symbols hovering above it until opened.</summary>
    public sealed record Box(uint Id, string Type, int X, int Y, float Rotation, float? Z, string? Model, bool Hoard, bool Opened,
        int[]? Rewards = null, int Faction = -1, uint[]? SymbolIds = null);

    /// <summary>What the server keeps of one frozen member: whether it broke out (its life is the monster's own).</summary>
    sealed class Hold(Encounter e, LegionStasis.Member member)
    {
        public readonly Encounter E = e;
        public LegionStasis.Member Member = member;
        public bool Broken;
        /// <summary>Set when the reveal (0x2c8 state 2) was sent; until then the member takes no hits (it is hidden and has no body).</summary>
        public bool Revealed;
    }

    static readonly ConditionalWeakTable<SpawnedMonster, Hold> Holds = new();

    static string? ModelOf(int key) => LegionData.Current.MiscAnimatedModels.GetValueOrDefault(key);

    static Player? ResolveRunner(Player fallback, InstanceState? instance)
    {
        if (!fallback.Closed && !fallback.LeavingArea && ReferenceEquals(fallback.Instance, instance)) return fallback;
        if (instance is null) return null;
        foreach (var p in instance.PlayersExcept(0))
            if (!p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance)) return p;
        return null;
    }

    internal static Player? LiveRunner(Player fallback, InstanceState? instance) => ResolveRunner(fallback, instance);

    /// <summary>Arms the non-lethal floor and clears EntranceGrace so every combat damage path (hits, DoTs, minions, totems,
    /// traps, mines, brands, culling strike) can damage this revealed frozen member and break it out at 1 life.</summary>
    static void ArmFloor(IModuleContext module, Player player, Hold hold, SpawnedMonster monster)
    {
        var instance = player.Instance;
        monster.OnNonLethalZero = target =>
        {
            lock (hold)
            {
                if (hold.Broken) return;
                hold.Broken = true;
            }
            target.OnNonLethalZero = null;
            target.EntranceGrace = true;
            var runner = ResolveRunner(player, instance);
            if (runner is null) return;
            global::Game.SpreadContagionAtFloorLocked(runner, target);
            global::Game.SendAfter(runner, 0, () =>
                ResolveRunner(runner, instance) is { } live ? BreakOut(module, live, hold, target) : Task.CompletedTask);
        };
        monster.EntranceGrace = false;
    }

    /// <summary>Registers a frozen member's hold. Hidden and already-broken members stay under EntranceGrace; revealed unbroken
    /// members (e.g. restored after a module reload) get their non-lethal floor armed immediately.</summary>
    static void Arm(IModuleContext module, Player player, Encounter e, LegionStasis.Member member)
    {
        if (!player.Area.Monsters.TryGetValue(member.Id, out var monster)) return;
        // GetValue keeps an existing hold if two restores race.
        var hold = Holds.GetValue(monster, _ => new Hold(e, member)
        {
            Broken = member.Broken && !(e.MultipleBreakout && !member.Chest),
            Revealed = member.Revealed,
        });
        monster.EncounterStats = LegionStasis.FrozenStats(e.FrozenDamageTakenPct);
        if (hold.Revealed && !hold.Broken) ArmFloor(module, player, hold, monster);
        else { monster.OnNonLethalZero = null; monster.EntranceGrace = true; }
    }

    static async Task BreakOut(IModuleContext module, Player player, Hold hold, SpawnedMonster monster)
    {
        // The break-out is queued as a frame task; if it fails or the runner left, re-arm so a later hit/tick can break it out.
        var instance = player.Instance;
        bool done = false;
        try
        {
            var runner = ResolveRunner(player, instance);
            if (runner is null || monster.Dead) return;
            var (x, y) = monster.Walk.Position(global::Game.AreaTime(runner));
            if (hold.Member.Chest) await BreakChest(module, runner, hold, monster, x, y);
            else await Free(module, runner, hold.E, [(hold, monster)]);
            done = true;
            await EarlyEnd(module, runner, hold.E);   // once per break-out, not per hit
        }
        finally
        {
            if (!done)
            {
                bool revealed;
                lock (hold) { hold.Broken = false; revealed = hold.Revealed; }
                if (revealed && !monster.Dead && ResolveRunner(player, instance) is { } runner)
                    ArmFloor(module, runner, hold, monster);
            }
        }
    }

    /// <summary>The saved members are marked broken in O(1) via ArmyIndex (incrementing BreakOutCount for Eternal Conflict
    /// and advancing the Timeless Conflict Alert gauge when active); tracks dual Generals (Face Off) and returns whether Schism triggered.</summary>
    static (bool GeneralBroken, uint NextGeneralId, bool TriggerSchism, uint SchismPulse) MarkBroken(Player player, Encounter e, HashSet<uint> ids)
    {
        lock (e)
        {
            if (e.Army is not { } army) return (false, 0, false, 0);
            bool general = false;
            int alertAdded = 0;
            foreach (uint id in ids)
            {
                if (!e.ArmyIndex.TryGetValue(id, out int idx)) continue;
                var m = army[idx];
                if (m.Tier == 3 && !m.Chest) general = true;
                if (e.AlertMode) alertAdded += LegionArmy.RankAlertValue(LegionData.Current, e.Level, m.Tier, m.Chest, m.WarHoard);
                army[idx] = m with { Broken = true, BreakOutCount = m.BreakOutCount + 1 };
            }
            uint nextGen = 0;
            if (general)
            {
                foreach (var m in army)
                    if (m.Tier == 3 && !m.Chest && !m.Broken) { nextGen = m.Id; break; }
                e.GeneralId = nextGen;
            }
            bool triggerSchism = false;
            uint schismPulse = 0;
            if (e.AlertMode && alertAdded > 0)
            {
                e.AlertGauge = Math.Min(e.AlertMax, e.AlertGauge + alertAdded);
                if (e.Phase == Phase.Running && e.AlertGauge >= e.AlertMax)
                {
                    e.Phase = Phase.Frozen;
                    e.Releasing = true;
                    e.StageAt = global::Game.AreaTime(player);
                    e.RuleStage = 1;
                    e.RuleStep = 3;
                    schismPulse = e.Pulse;
                    triggerSchism = true;
                }
            }
            // Flush immediately on Schism phase change; coalesce per-monster breakouts in Tick to avoid AoE frame stalls.
            Keep(player, e, flush: triggerSchism);
            return (general, nextGen, triggerSchism, schismPulse);
        }
    }

    /// <summary>Breaks the monsters out: a 0x2c8 state 3 (the client plays the threshold shards, breaking_free, a general's shell break and the 3 s
    /// ramp itself, R10), crystal off (present=1, breakout=1) for everyone now and for players who enter later.
    /// Under Eternal Conflict, non-chest monsters scale life, receive buff 2459 and re-arm after 0.5 s for further break-outs.</summary>
    static async Task Free(IModuleContext module, Player player, Encounter e, List<(Hold Hold, SpawnedMonster Monster)> items)
    {
        items = [.. items.Where(i => !i.Monster.Dead && player.Area.Monsters.ContainsKey(i.Hold.Member.Id))];   // none that ReleaseArmy removed
        if (items.Count == 0) return;
        var (general, nextGeneralId, triggerSchism, schismPulse) = MarkBroken(player, e, [.. items.Select(i => i.Hold.Member.Id)]);
        // A member the reveal has not reached yet (a chest broke within the reveal's 0.8 s) is shown first: state 3 follows 2.
        await RevealMembers(module, player, e, [.. items.Where(i => !i.Hold.Revealed).Select(i => i.Hold.Member)]);
        bool multipleBreakout;
        lock (e) multipleBreakout = e.MultipleBreakout;
        var instance = player.Instance;
        foreach (var (hold, monster) in items)
        {
            uint id = hold.Member.Id;
            hold.Member = hold.Member with { Broken = true, BreakOutCount = hold.Member.BreakOutCount + 1 };
            monster.OnNonLethalZero = null;
            monster.EntranceGrace = true;
            // The break-out carries the life percent the client reads its threshold shards from; 1 at least, as the last hit left 1 life.
            uint percent = (uint)Math.Max(1, (long)monster.Life * 100 / Math.Max(1, monster.MaxLife));
            LegionStasis.SetOriginState(player, id, LegionStasis.BreakingOut);
            // Eternal Conflict: each break-out escalates MaxLife by +50% and grants buff 2459 before refilling life/ES.
            if (multipleBreakout && !hold.Member.Chest)
            {
                monster.MaxLife = (uint)Math.Clamp(Math.Round(monster.MaxLife * 1.50), 1, uint.MaxValue);
                player.Area.Buffs.Set(id, new Buff(LegionStasis.EnhancedBuff, Tag: 1));
            }
            // Seen in the live test: the life bar must not stay near 0 after the break-out. Full life/ES makes the client hide it; EntranceGrace blocks further damage.
            monster.Life = monster.MaxLife;
            global::Game.RefillMonsterEnergyShieldLocked(player.Area, id, monster);
            var states = (monster.States ?? monster.Variety?.StateMachine)?.Set("present", 1).Set("breakout", 1);
            if (states is not null)
            {
                monster.States = states;
                if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
            }
            int component = monster.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
            await LegionOutput.ToArea(player, async p =>
            {
                // breakout=1 stays: the client draws the crystal from the StateMachine (R10 says the action handles it; LIVE CHECK whether it is needed).
                if (states is not null && component >= 0)
                    await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceEntities.StateMachineUpdate(id, (byte)component, states), null);
                await LegionStasis.SendState(p, id, hold.Member.Sequence, LegionStasis.BreakingOut, percent, null);
                if (LegionStasis.SendFreezeBuff) await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceEntities.RemoveBuff(id, hold.Member.Buff), null);
            });
            if (multipleBreakout && !hold.Member.Chest && !triggerSchism)
            {
                var rearmHold = hold;
                var rearmMonster = monster;
                global::Game.SendAfter(player, 0.5, () =>
                {
                    var runner = ResolveRunner(player, instance);
                    if (runner is null || rearmMonster.Dead) return Task.CompletedTask;
                    lock (e) if (e.Phase != Phase.Running) return Task.CompletedTask;
                    lock (rearmHold) rearmHold.Broken = false;
                    ArmFloor(module, runner, rearmHold, rearmMonster);
                    return Task.CompletedTask;
                });
            }
        }
        if (general)
        {
            if (nextGeneralId != 0 && InArea(player, e.Id))
            {
                // Face Off: another General still stands frozen — retarget monolith lightning onto it.
                await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), e.Id,
                    InstanceEntities.BaseDataUpdate(e.Id, [EntityVariable.Entity(GeneralPropertyKey, nextGeneralId)]),
                    $"0x02c1 Legion monolith 0x{e.Id:x} general=0x{nextGeneralId:x} (retarget)"));
            }
            else
            {
                Phase phase;
                lock (e) phase = e.Phase;
                await ToAll(player, e, StatesOf(phase, e), "spawn_lightning=0");
            }
        }
        if (triggerSchism)
            await Finish(module, player, e, schismPulse, "schism");
    }

    /// <summary>A one-shot effect object at a cell, removed after <see cref="FlashSeconds"/>.</summary>
    static async Task Flash(Player player, string? model, int x, int y)
    {
        if (model is null) return;
        uint id = player.TakeObjectId();
        var instance = player.Instance;
        bool created = false;
        await LegionOutput.ToArea(player, async p =>
        {
            if (!await global::Game.SendWorldCreate(PlayerOf(p), id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, model).Blob()), null)) return;
            created = true;
            await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
        // One area timer resolving a live runner from the instance so portal-out by the activator cannot target a stale area.
        if (created)
            global::Game.SendAfter(player, FlashSeconds, () =>
                ResolveRunner(player, instance) is { } runner ? LegionOutput.Remove(runner, id) : Task.CompletedTask);
    }

    static async Task BreakChest(IModuleContext module, Player player, Hold hold, SpawnedMonster chestMonster, int x, int y)
    {
        var e = hold.E;
        double radius = ChestRadius * Math.Max(0, 100 + AreaStats(player).GetValueOrDefault(Stat.MapLegionChestsBreakOutRangePlusPct)) / 100.0 + ChestMargin;
        LegionStasis.Member[] army;
        int spreadChance;
        lock (e) (army, spreadChance) = (e.Army is { Count: > 0 } a ? [.. a] : [], e.ChestSpreadChancePct);
        double now = global::Game.AreaTime(player);
        var near = new List<(Hold Hold, SpawnedMonster Monster)> { (hold, chestMonster) };
        foreach (var m in army)
        {
            if (m.Chest || m.Tier > 1 || m.Broken) continue;
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead || !Holds.TryGetValue(monster, out var h)) continue;
            var (mx, my) = monster.Walk.Position(now);
            if (Math.Abs(mx - x) > radius || Math.Abs(my - y) > radius) continue;   // a square query (0x141c496f0)
            lock (h) { if (h.Broken) continue; h.Broken = true; }
            monster.OnNonLethalZero = null;
            monster.EntranceGrace = true;
            near.Add((h, monster));
        }
        // Legion Scarab of Treasures (map_legion_%_chance_chests_break_out_spread_rewards):
        // spreads this chest's reward rows onto freed soldiers and boosts their difficulty.
        int spreadCount = 0;
        if (spreadChance > 0 && hold.Member.Rewards is { Length: > 0 } chestRewards && near.Count > 1)
        {
            var rng = global::Game.AreaRng(player);
            var spreadUpdates = new Dictionary<uint, int[]>();
            for (int i = 1; i < near.Count; i++)
            {
                if (rng.Next(100) >= Math.Clamp(spreadChance, 0, 100)) continue;
                var (h, monster) = near[i];
                int[] merged = [.. (h.Member.Rewards ?? []).Concat(chestRewards).Distinct()];
                h.Member = h.Member with { Rewards = merged, SpreadEnhanced = true };
                monster.MaxLife = (uint)Math.Clamp(Math.Round(monster.MaxLife * 1.25), 1, uint.MaxValue);
                monster.Life = monster.MaxLife;
                spreadUpdates[h.Member.Id] = merged;
                spreadCount++;
            }
            if (spreadUpdates.Count > 0)
            {
                lock (e)
                {
                    if (e.Army is { } current)
                    {
                        foreach (var (mid, rewards) in spreadUpdates)
                            if (e.ArmyIndex.TryGetValue(mid, out int idx)) current[idx] = current[idx] with { Rewards = rewards, SpreadEnhanced = true };
                        Keep(player, e, flush: false);
                    }
                }
                foreach (var (mid, rewards) in spreadUpdates) LegionStasis.ShowRewards(player, mid, rewards, true);
            }
        }
        // During the timer a broken-out War Chest explodes, frees nearby normal/elite soldiers and stays as a broken-out
        // silhouette (state 3, breakout=1, overhead reward symbols intact); it becomes clickable only at release (ReleaseArmy).
        await Flash(player, ModelOf(ChestExplodeKey), x, y);
        await Free(module, player, e, near);
        module.Log($"chest broke out at ({x}, {y}), freed {near.Count - 1}" + (spreadCount > 0 ? $" (spread rewards to {spreadCount})" : ""));
    }

    /// <summary>The early end: every member still frozen breaks out as a hit would break it (the 0x2c8 state 3, the crystal off), so ReleaseArmy then wakes it.
    /// The rule only ends the encounter when every chest, sergeant and general is out already, so what is left is plain and elite soldiers.</summary>
    static async Task BreakRemaining(IModuleContext module, Player player, Encounter e)
    {
        LegionStasis.Member[] army;
        lock (e) army = e.Army is { Count: > 0 } a ? [.. a] : [];
        var left = new List<(Hold Hold, SpawnedMonster Monster)>();
        var inFlight = new HashSet<uint>();
        foreach (var m in army)
        {
            if (m.Broken || m.Chest || !player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead || !Holds.TryGetValue(monster, out var hold)) continue;
            bool queued;
            lock (hold) { queued = hold.Broken; hold.Broken = true; }
            monster.OnNonLethalZero = null;
            monster.EntranceGrace = true;
            // A hit already broke it and queued its BreakOut, which has not marked the saved member yet: mark it here, or the release would take it for
            // unbroken and remove it. The queued BreakOut then finds the member gone or sends its state 3 itself, so it is not freed twice.
            if (queued) inFlight.Add(m.Id); else left.Add((hold, monster));
        }
        if (inFlight.Count > 0) MarkBroken(player, e, inFlight);
        await Free(module, player, e, left);
    }

    // ---- the openable chest ----

    /// <summary>Resolves a Legion chest's QuestObjectTypes.Info via its own type or its Chests.InheritsFrom base
    /// (<c>Metadata/Chests/LegionChests/LegionChestBase</c>), allowing its empty StateMachine component.</summary>
    internal static QuestObjectTypes.Info? ResolveChestInfo(string type)
    {
        var info = QuestObjectTypes.Find(type);
        if (info is null && LegionData.Current.ChestInherits.TryGetValue(type, out var parent) && parent is not null)
            info = QuestObjectTypes.Find(parent);
        info ??= ChestBaseInfo;
        return info is not null && QuestObject.CanSerialize(info.Components, ChestBaseStates is not null) ? info : null;
    }

    /// <summary>Builds the QuestObject for a Legion chest (with empty StateMachine when inherited from LegionChestBase,
    /// minimap icon stat and primary floating reward symbol reference 0xf3a80181 while closed).</summary>
    internal static QuestObject BuildChestObject(Box box, QuestObjectTypes.Info info) =>
        new(box.X, box.Y, info.Components, box.Rotation, box.Z, box.Model,
            TransitionState: (byte)(box.Opened ? info.States : 1), Opened: box.Opened)
        {
            StateMachine = info.Components.Contains("StateMachine") ? ChestBaseStates : null,
            Stats = !box.Opened && LegionStasis.RewardIcon(box.Rewards) is int icon ? [(Stat.MonsterMinimapIcon, icon)] : [],
            EntityVariables = !box.Opened && box.SymbolIds is { Length: > 0 }
                ? new Dictionary<uint, uint> { [ChestSymbolPropertyKey] = box.SymbolIds[0] }
                : new Dictionary<uint, uint>(),
        };

    /// <summary>The floating 3D symbol art (.ao path and scale) for each of a chest's reward rows.</summary>
    static List<(string Ao, double Scale)> ChestSymbolsFor(int[]? rewards)
    {
        var list = new List<(string, double)>();
        if (rewards is not { Length: > 0 }) return list;
        var byRow = LegionData.Current.RewardTypesByRow;
        foreach (int row in rewards)
        {
            if (byRow.TryGetValue(row, out var rt) && ModelOf(rt.MiscAnimatedKey) is { Length: > 0 } ao)
                list.Add((ao, rt.Scale));
        }
        return list;
    }

    /// <summary>Creates the floating 3D reward symbol EffectObject(s) hovering above a closed Legion chest, matching official
    /// capture height (25, -195.3125f) and scale; War Hoards with 2-3 rewards fan them slightly apart laterally.</summary>
    static async Task ShowChestSymbols(Player player, Box box)
    {
        if (box.Opened || box.SymbolIds is not { Length: > 0 } ids) return;
        var visuals = ChestSymbolsFor(box.Rewards);
        int count = Math.Min(ids.Length, visuals.Count);
        for (int i = 0; i < count; i++)
        {
            uint symId = ids[i];
            if (symId == 0 || InArea(player, symId)) continue;
            int sx = count == 1 ? box.X : box.X + (int)Math.Round((i - (count - 1) / 2.0) * 5);
            var (ao, scale) = visuals[i];
            var effect = new EffectObject(sx, box.Y, ao)
            {
                Awake = true,
                Flags0 = 0x04,
                Scale = (float)scale,
                AnimationWord = ChestSymbolHeightSteps,
                AnimationValue = ChestSymbolHeightZ,
            };
            var create = InstanceEntities.CreateObject(symId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob());
            await LegionOutput.ToArea(player, p => global::Game.SendWorldCreate(PlayerOf(p), symId, create,
                $"0x02bb Legion chest symbol 0x{symId:x} at ({sx}, {box.Y})"));
        }
    }

    /// <summary>The chest the war chest becomes at release (the plan's Chests row, or HardmodeChest in Ruthless; the War Hoard's for a hoard), standing where it was.</summary>
    static async Task SpawnBox(IModuleContext module, Player player, Encounter e, LegionStasis.Member member, int x, int y)
    {
        var planned = e.Plan is { } plan && member.Faction < plan.Factions.Length
            ? plan.Factions[member.Faction].Chests.FirstOrDefault(c => c.WarHoard == member.WarHoard) : null;
        bool ruthless = Program.LeagueFor(player).HardMode;
        int row = planned is null ? -1 : ruthless && planned.HardmodeChest >= 0 ? planned.HardmodeChest : planned.Chest;
        static (string Type, QuestObjectTypes.Info Info)? ResolveChest(int chestRow) =>
            LegionData.Current.ChestsByRow.TryGetValue(chestRow, out var entry) && ResolveChestInfo(entry.Id) is { } info
                ? (entry.Id, info) : null;
        var resolved = ResolveChest(row) ?? (planned is not null && row != planned.Chest ? ResolveChest(planned.Chest) : null);
        if (resolved is not var (type, info))
        {
            module.Log($"chest row {row} cannot be shown: no chest at ({x}, {y})");
            return;
        }
        var visuals = ChestSymbolsFor(member.Rewards);
        uint[]? symbolIds = visuals.Count > 0 ? [.. Enumerable.Range(0, visuals.Count).Select(_ => player.TakeObjectId())] : null;
        // Official captures send Animated = 0 (null model override) for Legion chests: the client resolves AOFiles[0] from Chests.dat.
        var box = new Box(player.TakeObjectId(), type, x, y, 0, null, null, member.WarHoard, false, member.Rewards,
            e.Plan is { } fp && member.Faction < fp.Factions.Length ? fp.Factions[member.Faction].Faction : -1, symbolIds);
        lock (e) { e.Boxes = [.. e.Boxes ?? [], box]; Keep(player, e); }
        // Create the floating symbol(s) first so the chest's 0xf3a80181 reference resolves on the client immediately.
        await ShowChestSymbols(player, box);
        if (await ShowBox(player, box, info))
            foreach (var p in Members(player)) BlockBox(p, box, info);
    }

    /// <summary>The chest for the area, created once; true when the area has it.</summary>
    static async Task<bool> ShowBox(Player player, Box box, QuestObjectTypes.Info info)
    {
        if (InArea(player, box.Id)) return true;
        var create = InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(box.Type), BuildChestObject(box, info).Blob());
        bool created = false;
        await LegionOutput.ToArea(player, async p =>
        {
            if (!await global::Game.SendWorldCreate(PlayerOf(p), box.Id, create, $"0x02bb Legion chest 0x{box.Id:x} at ({box.X}, {box.Y}), opened={box.Opened}")) return;
            created = true;
            await global::Game.SendWorldRecord(PlayerOf(p), box.Id, InstanceProtocol.EnterWakeZone(box.Id));
        });
        return created;
    }

    /// <summary>The chest blocks the player's own walk.</summary>
    static void BlockBox(Player player, Box box, QuestObjectTypes.Info info)
    {
        if (info.Blocking) player.SetObjectBlocker(box.Id, box.X, box.Y, Math.Max(1, info.Size), info.ActorOrChest);
    }

    /// <summary>A player who enters later: the area gives its client the chests and floating symbols as they are; the player's walk is blocked by them.</summary>
    static async Task ShowBoxes(Player player, Encounter e)
    {
        Box[] boxes;
        lock (e) boxes = e.Boxes ?? [];
        foreach (var box in boxes)
        {
            await ShowChestSymbols(player, box);
            if (ResolveChestInfo(box.Type) is { } info && await ShowBox(player, box, info)) BlockBox(player, box, info);
        }
    }

    /// <summary>A click on one of the chests: walk up, open it once (it is created again opened under a new id, as a Chest
    /// has no update reader), remove its floating reward symbols and drop its items.</summary>
    static async Task<bool> OpenBox(IModuleContext module, Player player, Encounter e, uint targetId, double requestedAt)
    {
        Box? box;
        lock (e) box = e.Boxes?.FirstOrDefault(b => b.Id == targetId);
        if (box is null) return false;
        if (box.Opened || player.Closed || player.LeavingArea || player.PlayerLife == 0 || ResolveChestInfo(box.Type) is not { } info) return true;
        if (!await WorldOf(player).WalkUpTo(box.X, box.Y, Math.Max(1, info.Size), requestedAt)) return true;
        Box opened;
        uint[]? oldSymbols;
        lock (e)
        {
            int i = Array.FindIndex(e.Boxes ?? [], b => b.Id == targetId);
            if (i < 0 || e.Boxes![i].Opened) return true;
            oldSymbols = e.Boxes[i].SymbolIds;
            opened = e.Boxes[i] with { Id = player.TakeObjectId(), Opened = true, SymbolIds = null };
            e.Boxes = [.. e.Boxes.Select((b, n) => n == i ? opened : b)];
            Keep(player, e);
        }
        foreach (var member in Members(player)) member.RemoveBlocker(targetId);
        if (oldSymbols is { Length: > 0 })
            foreach (uint symId in oldSymbols)
                if (symId != 0 && InArea(player, symId)) await LegionOutput.Remove(player, symId);
        if (InArea(player, targetId)) await LegionOutput.Remove(player, targetId);
        if (await ShowBox(player, opened, info))
            foreach (var member in Members(player)) BlockBox(member, opened, info);
        await DropBox(module, player, e, opened, info.Size);
        return true;
    }

    /// <summary>The chest's items on the ground in a ring around it: by its reward types and its faction's splinters (LegionRewards.Chest); an old save
    /// without rewards keeps the server's container roll at the area level (GUESS for the number of rolls; no per-chest drop table is known).</summary>
    static async Task DropBox(IModuleContext module, Player player, Encounter e, Box box, int size)
    {
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = global::Game.AreaRng(player);
        int level = Math.Clamp(e.Level, 1, 100);
        List<(Item Item, string Name)> items;
        if (box.Rewards is { Length: > 0 } rewards)
        {
            string? faction = e.Plan is { } plan && plan.Factions.FirstOrDefault(f => f.Faction == box.Faction) is { } fp ? fp.Id : null;
            items = [.. LegionRewards.Chest(player, rewards, faction, box.Hoard, level, rng).Select(i => (i, global::Game.ItemsFor(player).ByHash(i.TypeHash)?.Name ?? "item"))];
        }
        else items = [.. global::Game.DropsFor(player).Container(level, box.Hoard ? HoardRolls : ChestRolls, 0, rng).Select(d => (d.Item, d.Type.Name))];
        int ring = Math.Max(2, size / 2 + 2);
        for (int i = 0; i < items.Count; i++)
        {
            double angle = i * 2.399963229728653 + rng.NextDouble();
            var at = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * ring), box.Y + (int)Math.Round(Math.Sin(angle) * ring)));
            var ground = new WorldItemObject(at.X, at.Y, items[i].Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await global::Game.SendGroundItem(player, id, ground, $"Legion chest 0x{box.Id:x} drops {items[i].Name} x{items[i].Item.StackSize}", justDropped: true);
        }
    }
}
