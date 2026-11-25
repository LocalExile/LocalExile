using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The two armies standing in stasis around the monolith: the planned monsters and chest monsters are spawned as the
/// instance's own hostile monsters, held the way the Essence's encased previews are (no aggro, no attack, no rewards, AI
/// held) and given the client's own <c>LegionBreakout</c> action (R10, R11): created in its state 0 they are hidden and have
/// no collision on the client and on the server (SpawnMonster noBodyUntilReleased) and take no hits; the reveal sends state 2
/// (the client poses them mid-attack and attaches the faction's crystal), the break-out state 3 (shards, breaking_free, the 3 s
/// ramp), the end state 5. The client draws the crystal itself (it is in the Legion monsters' models, and their StateMachine
/// defaults breakout 0 / activated 0 mean frozen). Hits on a revealed member are stored and zeroed until the encounter ends
/// (LegionMonolith.Breakout.cs).
/// </summary>
static class LegionStasis
{
    /// <summary>A spawned army member: its object id, the army (index into the plan's factions), the rank tier (0 normal,
    /// 1 elite, 2 sergeant, 3 general), whether it is a chest monster (a War Hoard when WarHoard), whether it broke out, the
    /// sequence of its LegionBreakout action (the 0x2c8 updates must carry it), whether it was revealed (state 2), its reward types
    /// (LegionRewardTypeVisuals rows; null or empty: none), and its formation facing angle.</summary>
    public sealed record Member(uint Id, ushort Buff, int Faction, int Tier, bool Chest, bool WarHoard, bool Broken = false, ushort Sequence = 0, bool Revealed = false, int[]? Rewards = null, float? Facing = null);

    /// <summary>The LegionBreakout action: ActionTypes HASH16 (the 0x2c8 header's action type) and its skill id, the type's HASH16 &amp; 0x3fff (R11 section 0).</summary>
    const ushort ActionType = 0xd8fe, ActionSkill = 0x18fe;
    /// <summary>The action's states (R10 section 1): hidden, revealed, breaking out, ended.</summary>
    public const byte Hidden = 0, Revealed = 2, BreakingOut = 3, Ended = 5;

    /// <summary>Whether buff 1311 and the paused animation (0x1b2 speed 0) are sent as well as the action. OFF for the first live test of the
    /// action alone, so the client's native rendering is seen cleanly (user decision 2026-11-24); set true to restore the old stasis.</summary>
    public static readonly bool SendFreezeBuff = false;   // not a const: the compiler rejects the dead branches of a const (CS0162, an error here)

    /// <summary>BuffDefinitions 1328 legion_reward_display: its list is [LegionRewardTypeVisuals row, hidden (0 shows), height, index, count], always 5 entries.</summary>
    public const ushort RewardBuff = 1328;

    /// <summary>A woken member as the drops see it: its reward types (LegionRewardTypeVisuals rows), its faction's LegionFactions Id and its rank tier.</summary>
    public sealed record Fighter(int[] Rewards, string FactionId, int Tier);

    /// <summary>The members that woke and fight, by the new monster's id, per AREA (ids restart in every area instance; the table goes with its area).
    /// Also persisted in Instance.ContentState["LegionFighters"] so module hot-reloads keep sergeant/general rewards intact.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameArea, System.Collections.Concurrent.ConcurrentDictionary<uint, Fighter>> FightersByArea = new();
    static volatile bool anyFighter;
    const string FightersSaveKey = "LegionFighters";
    static readonly JsonSerializerOptions Json = new();

    /// <summary>True once any fighter was registered (the drops' cheap first test); false after the module's cleanup.</summary>
    public static bool AnyFighter => anyFighter;

    public static void ClearFighters() { FightersByArea.Clear(); anyFighter = false; }

    static void PersistFighters(Player player, System.Collections.Concurrent.ConcurrentDictionary<uint, Fighter> fighters)
    {
        if (player.Instance is not { } instance) return;
        var snapshot = fighters.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (snapshot.Count == 0) instance.ContentState.TryRemove(FightersSaveKey, out _);
        else instance.ContentState[FightersSaveKey] = JsonSerializer.Serialize(snapshot, Json);
    }

    /// <summary>Restores woken fighters persisted across a module reload for this instance.</summary>
    public static void RestoreFighters(Player player)
    {
        if (player.Instance is not { } instance || FightersByArea.TryGetValue(player.Area, out _)) return;
        if (!instance.ContentState.TryGetValue(FightersSaveKey, out string? text) || string.IsNullOrWhiteSpace(text)) return;
        var saved = JsonSerializer.Deserialize<Dictionary<uint, Fighter>>(text, Json);
        if (saved is not { Count: > 0 }) return;
        var map = FightersByArea.GetOrCreateValue(player.Area);
        foreach (var (id, fighter) in saved)
            if (player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead) map[id] = fighter;
        if (map.Count > 0) anyFighter = true;
    }

    public static Fighter? FighterOf(Player player, uint id, bool remove)
    {
        if (!FightersByArea.TryGetValue(player.Area, out var fighters))
        {
            RestoreFighters(player);
            if (!FightersByArea.TryGetValue(player.Area, out fighters)) return null;
        }
        if (remove)
        {
            if (!fighters.TryRemove(id, out var taken)) return null;
            PersistFighters(player, fighters);
            return taken;
        }
        return fighters.TryGetValue(id, out var found) ? found : null;
    }

    /// <summary>The minimap icon stat (monster_minimap_icon = MinimapIcons row + 1) of a reward row, the first reward's; null when none.</summary>
    public static int? RewardIcon(int[]? rewards) =>
        rewards is { Length: > 0 } && LegionData.Current.RewardTypes.FirstOrDefault(r => r.Row == rewards[0]) is { MinimapIcon: >= 0 } t ? t.MinimapIcon + 1 : null;

    /// <summary>The create stats of a reward carrier: the icon, and hidden until its reveal when <paramref name="hidden"/> (state 0 shows on the minimap otherwise).</summary>
    static List<(uint Key, int Value)>? RewardStats(int[]? rewards, bool hidden)
    {
        if (RewardIcon(rewards) is not int icon) return null;
        var stats = new List<(uint, int)> { (Stat.MonsterMinimapIcon, icon) };
        if (hidden) stats.Add((Stat.MonsterHideMinimapIcon, 1));
        return stats;
    }

    /// <summary>The symbols over a monster: one 1328 buff per reward, side by side (index, count), in the AREA's Buffs component (Tag = index + 1,
    /// so each is its own instance): the area writes them to every client now, replays them to one that joins later and ends them with the
    /// monster. With <paramref name="reveal"/> also the minimap icon's hide stat 0, through the area's Stats component.</summary>
    public static void ShowRewards(Player player, uint id, int[]? rewards, bool reveal)
    {
        if (rewards is not { Length: > 0 }) return;
        for (int i = 0; i < rewards.Length; i++)
            player.Area.Buffs.Set(id, new Buff(RewardBuff, List: [(uint)rewards[i], 0, 0, (uint)i, (uint)rewards.Length], Tag: (ushort)(i + 1)));
        if (reveal && RewardIcon(rewards) is not null) player.Area.Stats.Set(id, Stat.MonsterHideMinimapIcon, 0);
    }

    /// <summary>The sequence of the next LegionBreakout action: a simple counter, never 0.</summary>
    static int sequences;
    static ushort NextSequence() => (ushort)((uint)Interlocked.Increment(ref sequences) % 0xffff + 1);

    /// <summary>BuffDefinitions frozen_in_time, with its default visual 1082 legion_petrify; the faction visual overrides it (TimeLockVisual).</summary>
    const ushort FrozenBuff = 1311;

    /// <summary>The faction's BuffVisuals row (1099-1103 legion_time_lock_*), sent as the AddBuff visual override of 1311 so the soldier is drawn
    /// under a thin faction-coloured coat instead of the default 1082 legion_petrify (big purple spikes). Faridun (Afarud) has no
    /// row of its own: it uses the Maraketh one (GUESS: the same desert faction). An unknown id keeps the default.</summary>
    static ushort? TimeLockVisual(string? faction) => faction switch
    {
        "Karui" => 1099, "Maraketh" or "Faridun" => 1100, "Vaal" => 1101, "Templar" => 1102, "EternalEmpire" => 1103, _ => null,
    };

    /// <summary>EncounterStats carried by frozen army members: knockback immunity plus any map_legion_frozen_monsters_damage_taken_+%
    /// (mirrors BuffDefinitions 1311 frozen_in_time). Shared dictionary when the modifier is 0.</summary>
    static readonly IReadOnlyDictionary<uint, int> BaseFrozenStats = new Dictionary<uint, int> { [Stat.CannotBeKnockedBack] = 1 };

    public static IReadOnlyDictionary<uint, int> FrozenStats(int damageTakenPct) =>
        damageTakenPct == 0 ? BaseFrozenStats
            : new Dictionary<uint, int> { [Stat.CannotBeKnockedBack] = 1, [Stat.BaseDamageTakenPlusPct] = damageTakenPct };

    /// <summary>Spawns both armies of the plan hidden (action state 0); each member is handed to <paramref name="keep"/> (false: the timer ended, it is removed and spawning stops),
    /// then to <paramref name="standing"/> (outside the encounter's lock, for a reveal when the shatter has already passed).
    /// When a general is planned and <paramref name="monolithRotation"/> is finite, the axis aligns the general's army with the monolith's forward direction.
    /// <paramref name="monolithId"/> is written into each unit's <c>owning_initiator</c> state (with <c>present=0</c>).</summary>
    public static async Task Spawn(IModuleContext module, Player player, LegionArmy.ArmyPlan plan, int cx, int cy, int level, Func<Member, bool> keep, Func<Member, Task> standing, int frozenDamageTakenPct = 0, float monolithRotation = float.NaN, uint monolithId = 0)
    {
        var rng = global::Game.AreaRng(player);
        var frozenStats = FrozenStats(frozenDamageTakenPct);
        int generalArmy = Array.FindIndex(plan.Factions, f => f.Squads.Any(s => s.Tier == 3));
        double axis = generalArmy >= 0 && float.IsFinite(monolithRotation)
            ? monolithRotation + (generalArmy == 0 ? -Math.PI / 2 : Math.PI / 2)
            : rng.NextDouble() * Math.PI * 2;
        // The formations (LegionFormation.cs): the block of each army from its member count and its bodies' size.
        var formations = plan.Factions.Select((f, i) => new LegionFormation(player, rng, cx, cy, axis, i, f,
            row => Monsters.Find(row.ToString()) is { } v ? v.ObjectSize : 3)).ToArray();
        byte areaLevel = (byte)Math.Clamp(level, 1, 100);
        var skippedBy = new int[plan.Factions.Length];
        int skipped = 0, sergeants = 0, generals = 0, chests = 0;
        var counts = new int[plan.Factions.Length];
        bool stopped = false;   // the timer ended while the army was being spawned

        async Task Add(int faction, int row, MonsterRarity rarity, int tier, bool chest, bool hoard, int from, int to, int[]? rewards = null)
        {
            if (stopped) return;
            if (Monsters.Find(row.ToString()) is not { Supported: true } variety || player.Closed || player.LeavingArea) { skipped++; skippedBy[faction]++; return; }
            if (formations[faction].Place(from, to, variety.ObjectSize, tier == 3) is not { } at) { skipped++; skippedBy[faction]++; return; }
            // bulk: no wait for the client's birth per monster (a frame each, 5-8 s for 150-250). The action is in its create (hidden from the first frame).
            ushort sequence = NextSequence();
            var action = BreakoutAction(player, variety, row, sequence, Hidden, 100);
            float facing = formations[faction].Facing;
            uint id = await SpawnMonster(player, variety, areaLevel, rarity, at.X, at.Y,
                currentAction: action, noBodyUntilReleased: true,
                friendly: false, unrisen: false, alone: false, scripted: true, instanceOwned: true, bulk: true, log: false, rotation: facing,
                extraStats: RewardStats(rewards, true),
                placement: MonsterPlacement.Established,   // the formation checked the spot (walkable, clear of the others): no nearest-clear search that could stack two
                shape: monolithId == 0 ? null : o => o with { StateMachine = (o.StateMachine ?? variety.StateMachine)?.Set("present", 0).Set("owning_initiator", (int)monolithId) });
            if (id == 0) { skipped++; return; }
            ushort buff = await Freeze(player, id, variety.Id, TimeLockVisual(plan.Factions[faction].Id), frozenStats);
            var member = new Member(id, buff, faction, tier, chest, hoard, Sequence: sequence, Rewards: rewards is { Length: > 0 } ? rewards : null, Facing: facing);
            if (!keep(member)) { stopped = true; await Remove(player, [member]); return; }
            await standing(member);
            counts[faction]++;
            if (chest) chests++; else if (tier == 2) sergeants++; else if (tier == 3) generals++;
        }

        for (int f = 0; f < plan.Factions.Length; f++)
        {
            var faction = plan.Factions[f];
            foreach (var squad in faction.Squads.OrderByDescending(q => q.Tier))   // the general first: its rear-centre spot is still free
            {
                // The rarity is rolled in the plan from the LegionRanks weights (a GUESS: soldiers mostly Normal, elites some Magic, sergeants Rare, the general Unique); an old save has none: by tier.
                for (int i = 0; i < squad.Count; i++) await Add(f, squad.Monster, (MonsterRarity)(squad.Rarities is { } rs && i < rs.Length ? rs[i] : LegionArmy.TierRarity(squad.Tier)), squad.Tier, false, false, squad.FromPct, squad.ToPct, squad.Rewards is { } r && i < r.Length ? r[i] : null);
            }
            foreach (var chest in faction.Chests)
                await Add(f, chest.Monster, MonsterRarity.Normal, 0, true, chest.WarHoard, chest.FromPct, chest.ToPct, chest.Rewards);
        }
        string names = string.Join(" + ", plan.Factions.Select((f, i) => $"{f.Id} {counts[i]}"));
        module.Log($"army spawned: {names} ({sergeants} sergeants, {generals} general, {chests} chests), {skipped} skipped, formations {string.Join(", ", formations.Select((f, i) => $"{f.Describe()} skipped {skippedBy[i]}"))}");
    }

    /// <summary>The attack clip a hidden soldier is created in (R11 section 4): the server names the clip, the client picks the time inside it at
    /// the reveal. The variety's "Melee" pool of the contact data (the bow skills map to Melee in SkillAnimations.Key), chosen as the client
    /// chooses a clip for an action with this sequence (a stanced variety, a Rogue Exile, plays its stance's pool); else the variety's ordinary
    /// melee clock; else clip 0 (idle).</summary>
    static int AttackClip(Player player, MonsterCatalog.Variety variety, ushort sequence)
    {
        if (SkillAnimations.IsStanced(variety.Id)
            && SkillAnimations.StanceClip(variety.Id, SkillAnimations.StanceName(variety.Weapons), "Melee", sequence) is { } stanced) return (int)stanced.Index;
        var catalog = global::Game.SkillsFor(player);
        return catalog.MonsterContact(variety.Id, "Melee", sequence) is { } clip ? (int)clip.Index
            : catalog.MeleeClock(variety.Id, sequence) is { } clock ? (int)clock.Index : 0;
    }

    /// <summary>The paused props a soldier is created with (R10 section 2): LegionMonsterVarieties.MiscAnimatedKey rows (an arrow rain, a
    /// ballista ball), each frozen at a time inside its clip from col10..col11 in ms. A monster gets props with col5 percent chance;
    /// col15 gives the prop count per MiscAnimatedKey entry (col6 is the variety spawn weight, not prop count). The client creates them at
    /// the monster with a random rotation, so no position is sent.</summary>
    static int At(int[]? values, int i) => values is not { Length: > 0 } ? 0 : values[Math.Min(i, values.Length - 1)];

    static List<(int Row, AnimationControllerState Animation)> PropsOf(int monsterRow, Random rng)
    {
        var props = new List<(int, AnimationControllerState)>();
        if (LegionData.Current.MonsterVarieties.FirstOrDefault(v => v.Monster == monsterRow) is not { MiscAnimated.Length: > 0 } row
            || row.PropChance <= 0 || rng.Next(100) >= row.PropChance) return props;
        int pick = rng.Next(row.MiscAnimated.Length);
        int n = Math.Max(0, At(row.PropCounts, pick));
        if (n == 0) return props;
        int from = At(row.PropFromMs, pick), to = At(row.PropToMs, pick);
        for (int i = 0; i < n; i++)
        {
            int ms = to > from ? rng.Next(from, to + 1) : from;
            props.Add((row.MiscAnimated[pick], new AnimationControllerState(0) { Time = ms / 1000f }));
        }
        return props;
    }

    /// <summary>The LegionBreakout action in <paramref name="state"/> as the client's creation reader takes it (R11 section 1): the attack clip, then
    /// i32 prop count, i8 state, u32 last threshold percent, two f32 (the client overwrites them), then each prop's MiscAnimated row and animation.</summary>
    static ActorAction BreakoutAction(Player player, MonsterCatalog.Variety variety, int monsterRow, ushort sequence, byte state, uint percent)
    {
        var props = PropsOf(monsterRow, global::Game.AreaRng(player));
        var w = new BlobWriter().I32(props.Count).U8(state).U32(percent).F32(0).F32(0);
        foreach (var (row, animation) in props) animation.Write(w.U32((uint)row));
        return new ActorAction(ActionSkill, sequence) { Animation = new AnimationControllerState(AttackClip(player, variety, sequence)), Payload = w.ToArray() };
    }

    /// <summary>S->C 0x2c8 for the member's action: its payload <c>i8 state, u32 percent</c> (R11 section 2). The sub-id is 0.</summary>
    public static Task SendState(Player player, uint id, ushort sequence, byte state, uint percent, string? log = null)
    {
        Span<byte> payload = stackalloc byte[5];
        payload[0] = state;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[1..], percent);
        return global::Game.SendActionData(player, id, ActionType, ActionSkill, sequence, 0, payload, log);
    }

    /// <summary>Writes the action's state into the monster's origin, so a client that joins later is created with it (its create carries the action).</summary>
    public static void SetOriginState(Player player, uint id, byte state)
    {
        if (!player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) || origin.Object.CurrentAction is not { Payload.Length: >= 5 } action) return;
        byte[] payload = [.. action.Payload];
        payload[4] = state;   // after the i32 prop count
        player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { CurrentAction = action with { Payload = payload } } };
    }

    /// <summary>The stasis of one spawned monster: held scripted with EntranceGrace until RevealMembers arms its non-lethal
    /// combat floor and clears EntranceGrace; EncounterStats supplies knockback immunity and frozen damage taken.</summary>
    static async Task<ushort> Freeze(Player player, uint id, string variety, ushort? visual, IReadOnlyDictionary<uint, int> frozenStats)
    {
        if (player.Area.Monsters.TryGetValue(id, out var monster))
        {
            monster.NoRewards = true; monster.Attack = null; monster.Aggro = false; monster.EntranceGrace = true;
            monster.BusyUntil = double.PositiveInfinity;
            monster.EncounterStats = frozenStats;
        }
        ushort buff = player.NextBuff++;
        if (SendFreezeBuff) await LegionOutput.ToArea(player, p => SendStasis(p, id, buff, variety, true, visual));
        return buff;
    }

    /// <summary>The old stasis (SendFreezeBuff): buff 1311 and the animation paused at its first frame.</summary>
    static async Task SendStasis(Player player, uint id, ushort buff, string variety, bool withBuff, ushort? visual)
    {
        if (withBuff) await global::Game.SendWorldRecord(player, id, InstanceEntities.AddBuff(id, buff, FrozenBuff, visual: visual), $"legion stasis {variety}");
        // Pause the actor: 0x1b2 kind 0 plays animation 0 at speed 0.
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 0), $"legion freeze {variety}");
    }

    /// <summary>A player who enters later: the area gives its client the monsters with their states and their action (MonsterOrigins), so this goes to
    /// that player alone, after the creates: a 0x2c8 state 2 for a revealed member that has not broken out (a broken one is created in state 3 from its origin and gets none). With
    /// <see cref="SendFreezeBuff"/> also the buff and the paused animation the area does not keep.</summary>
    public static async Task Present(Player player, Member[]? army, LegionArmy.ArmyPlan? plan)
    {
        if (army is not { Length: > 0 }) return;
        await LegionOutput.ToOne(player, async () =>
        {
            foreach (var member in army)
            {
                if (!player.Area.Monsters.TryGetValue(member.Id, out var m) || m.Dead) continue;
                if (SendFreezeBuff)
                    await SendStasis(player, member.Id, member.Buff, "member", !member.Broken,
                        plan is not null && member.Faction < plan.Factions.Length ? TimeLockVisual(plan.Factions[member.Faction].Id) : null);   // a broken-out one is still paused, without the buff
                // A revealed member's origin is state 0, so the joiner gets the same 0 -> 2 change (pose, a general's shell). A broken one is created in state 3
                // from its origin: no 0x2c8 (it would replay breaking_free, the shards and the 3 s ramp).
                if (member.Revealed && !member.Broken)
                    await SendState(player, member.Id, member.Sequence, Revealed, 100, "legion action state (joiner)");
            }
        });
    }

    /// <summary>Sets StateMachine states (each to 1) on a monster, server-side (a player who enters later is created with them) and once for every client in the area.</summary>
    public static async Task SetState(Player player, uint id, SpawnedMonster monster, params string[] names)
    {
        var states = monster.States ?? monster.Variety?.StateMachine;
        if (states is null) return;
        foreach (var name in names) states = states.Set(name, 1);
        monster.States = states;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        int component = monster.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
        if (component < 0) return;
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(p, id, InstanceEntities.StateMachineUpdate(id, (byte)component, states), null));
    }

    /// <summary>The end of the stasis for a member that broke out. The frozen monster was spawned scripted (no AI, no kit, cannot be
    /// stunned: all fixed at spawn), so it is swapped for a new ordinary monster of the same variety, rarity, cell and formation facing,
    /// shaped with <c>present=1</c>, <c>breakout=1</c>, <c>activated=1</c> and <c>owning_initiator</c> right in its 0x02bb create so no crystal flickers and no post-spawn state update is needed.
    /// Returns the woken monster's object id, or 0 when the new monster could not be made (the frozen one is then left standing).</summary>
    public static async Task<uint> Wake(Player player, Member member, SpawnedMonster monster, int cx, int cy, string? factionId = null, uint monolithId = 0)
    {
        var variety = monster.Variety;
        if (variety is null) return 0;
        var (x, y) = monster.Walk.Position(global::Game.AreaTime(player));
        float rotation = member.Facing is float f && float.IsFinite(f) ? f : (float)Math.Atan2(cx - x, -(cy - y));
        uint id = await SpawnMonster(player, variety, monster.Level, monster.Rarity, x, y, mods: monster.Mods,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false, placement: MonsterPlacement.Established,
            rotation: rotation,
            extraStats: RewardStats(member.Rewards, false),   // the icon, shown
            shape: o =>
            {
                var sm = (o.StateMachine ?? variety.StateMachine)?.Set("present", 1).Set("breakout", 1).Set("activated", 1);
                if (monolithId != 0) sm = sm?.Set("owning_initiator", (int)monolithId);
                return o with { StateMachine = sm };
            });
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out _)) return 0;   // the frozen one stays: the caller removes it
        await Remove(player, [member]);
        if (member.Rewards is { Length: > 0 } rewards) ShowRewards(player, id, rewards, false);
        if (member.Tier >= 2 && factionId is not null || member.Rewards is { Length: > 0 })
        {
            var map = FightersByArea.GetOrCreateValue(player.Area);
            map[id] = new Fighter(member.Rewards ?? [], factionId ?? "", member.Tier);   // for the drops (LegionRewards)
            anyFighter = true;
            PersistFighters(player, map);
        }
        return id;
    }

    /// <summary>Removes the members still standing from the instance and from every client in the area; returns how many.</summary>
    public static async Task<int> Remove(Player player, IEnumerable<Member> army)
    {
        var ids = new List<uint>();
        foreach (var member in army)
            if (player.Area.Monsters.Remove(member.Id, out var m))
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                m.OnNonLethalZero = null;
                player.Instance.World.MonsterOrigins.TryRemove(member.Id, out _);
                ids.Add(member.Id);
            }
        if (ids.Count == 0) return 0;
        var members = LegionOutput.Members(player).ToList();
        if (members.Count == 0)
        {
            foreach (uint id in ids) LegionOutput.ForgetComponents(player, id);
        }
        else
        {
            await LegionOutput.ToArea(members, async p =>
            {
                foreach (uint id in ids) await global::Game.SendWorldRemoval(p, id);
            });
        }
        return ids.Count;
    }
}
