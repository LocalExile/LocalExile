using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// Damage on a frozen army member and its break-out. A hit on a member lowers its life (the area writes it to the
/// clients' life bars) and is zeroed so nothing else is dealt; when it would reach 0 it is refilled and the member
/// breaks out: its crystal goes (StateMachine <c>breakout</c> 1), the frozen buff ends and the faction's effect plays. It stays
/// where it stands, still without AI, until the encounter ends (ReleaseArmy wakes it). A war chest that breaks out frees the
/// tier 0 and 1 monsters near it and becomes an openable chest object.
/// </summary>
static partial class LegionMonolith
{
    /// <summary>THE RADIUS IS A GUESS (live check): grid cells around a broken chest (about 2.5 x the armies' mean spacing of 12-14; a constant because the spacing is not reachable here), before map_legion_chests_break_out_range_+%.</summary>
    const double ChestRadius = 32;
    /// <summary>How long a one-shot effect object stays before it is removed.</summary>
    const double FlashSeconds = 3;
    /// <summary>GUESS: the rolls an opened chest and an opened War Hoard drop.</summary>
    const int ChestRolls = 3, HoardRolls = 6;
    /// <summary>MiscAnimated 3326 LegionChestBreakoutExplode.</summary>
    const int ChestExplodeKey = 3326;

    /// <summary>The openable chest that a broken-out war chest became (kept in the save; its id changes when it is opened).</summary>
    public sealed record Box(uint Id, string Type, int X, int Y, float Rotation, float? Z, string? Model, bool Hoard, bool Opened);

    /// <summary>What the server keeps of one frozen member: whether it broke out (its life is the monster's own).</summary>
    sealed class Hold(Encounter e, LegionStasis.Member member)
    {
        public readonly Encounter E = e;
        public readonly LegionStasis.Member Member = member;
        public bool Broken;
    }

    static readonly ConditionalWeakTable<SpawnedMonster, Hold> Holds = new();
    static volatile bool anyHold;
    static Dictionary<int, string>? Models;

    static string? ModelOf(int key)
    {
        Models ??= ClientTables.TableOrEmpty("MiscAnimated").Where(r => r.Text("AOFile") is { Length: > 0 })
            .ToDictionary(r => r.Index, r => r.Text("AOFile")!);
        return Models.GetValueOrDefault(key);
    }

    /// <summary>A member just spawned frozen: from now its hits lower its life. Ignite, poison, bleed and the other life takers
    /// end in TakeDamageLocked, which does nothing while the monster is Invulnerable: LegionStasis.Freeze sets EntranceGrace.</summary>
    static void Arm(Player player, Encounter e, LegionStasis.Member member)
    {
        if (!player.Area.Monsters.TryGetValue(member.Id, out var monster)) return;
        // TryAdd: a hold that already exists is kept, as it may be mid break-out (two restores in Of() can run at once).
        Holds.TryAdd(monster, new Hold(e, member) { Broken = member.Broken });
        anyHold = true;
    }

    /// <summary>GameEvents.OutgoingDamage (per hit): the common path is a flag and one table lookup, no allocation. A hit on a
    /// member of the army lowers its life (not below 1) and is zeroed, also after the member broke out, until the timer ends.</summary>
    static DamagePacket Hit(IModuleContext module, Player player, SpawnedMonster target, DamagePacket rolled)
    {
        if (!anyHold || !Holds.TryGetValue(target, out var hold)) return rolled;
        bool breaks = false;
        lock (hold)
        {
            if (!hold.Broken)
            {
                // GUESS: the damage is the rolled hit before resistances and crit, and map_legion_frozen_monsters_damage_taken_+%
                // is ignored. Setting Life marks it for the area's life write (AreaLife), which sends it to every client.
                // Damage over time is not counted: EntranceGrace makes TakeDamageLocked ignore it.
                double total = 0;
                for (int i = 0; i < 5; i++) total += rolled[(DamageType)i].Average;
                if (total >= target.Life) { target.Life = 1; hold.Broken = breaks = true; }
                else if (total >= 1) target.Life -= (uint)total;
            }
        }
        if (breaks) global::Game.SendAfter(player, 0, () => BreakOut(module, player, hold, target));
        return DamagePacket.Empty;
    }

    static async Task BreakOut(IModuleContext module, Player player, Hold hold, SpawnedMonster monster)
    {
        // The break-out is queued as a frame task; if it fails or the hitter left, the member can break out on a later hit.
        bool done = false;
        try
        {
            if (player.Instance is null || player.Closed || player.LeavingArea || monster.Dead) return;
            var (x, y) = monster.Walk.Position(global::Game.AreaTime(player));
            if (hold.Member.Chest) await BreakChest(module, player, hold, x, y);
            else await Free(player, hold.E, [(hold, monster)]);
            done = true;
            await EarlyEnd(module, player, hold.E);   // once per break-out, not per hit
        }
        finally { if (!done) lock (hold) hold.Broken = false; }
    }

    /// <summary>The saved members are marked broken; true when one of them was an unbroken general (its bolts go out).</summary>
    static bool MarkBroken(Player player, Encounter e, HashSet<uint> ids)
    {
        lock (e)
        {
            if (e.Army is not { } army) return false;
            bool general = army.Any(m => ids.Contains(m.Id) && m.Tier == 3 && !m.Chest && !m.Broken);
            e.Army = [.. army.Select(m => ids.Contains(m.Id) ? m with { Broken = true } : m)];
            Keep(player, e);
            return general;
        }
    }

    /// <summary>Breaks the monsters out: crystal off for everyone now and for players who enter later, buff 1311 removed, the
    /// faction's effect. The LegionFactions threshold columns and the shards_&lt;F&gt; effects are skipped.</summary>
    static async Task Free(Player player, Encounter e, List<(Hold Hold, SpawnedMonster Monster)> items)
    {
        items = [.. items.Where(i => !i.Monster.Dead && player.Area.Monsters.ContainsKey(i.Hold.Member.Id))];   // none that ReleaseArmy removed
        if (items.Count == 0) return;
        bool general = MarkBroken(player, e, [.. items.Select(i => i.Hold.Member.Id)]);
        double now = global::Game.AreaTime(player);
        foreach (var (hold, monster) in items)
        {
            uint id = hold.Member.Id;
            // Seen in the live test: the life bar must not stay near 0 after the break-out. Full life makes the client hide it; later hits stay zeroed.
            monster.Life = monster.MaxLife;
            var states = (monster.States ?? monster.Variety?.StateMachine)?.Set("breakout", 1);
            if (states is not null)
            {
                monster.States = states;
                if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
            }
            int component = monster.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
            var (x, y) = monster.Walk.Position(now);
            await LegionOutput.ToArea(player, async p =>
            {
                if (states is not null && component >= 0)
                    await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceEntities.StateMachineUpdate(id, (byte)component, states), null);
                await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceEntities.RemoveBuff(id, hold.Member.Buff), null);
            });
            string? model = e.Plan is { } plan && hold.Member.Faction < plan.Factions.Length
                && LegionData.Current.Factions.FirstOrDefault(f => f.Row == plan.Factions[hold.Member.Faction].Faction) is { MiscAnimated.Length: > 0 } faction
                    ? ModelOf(faction.MiscAnimated[0]) : null;
            await Flash(player, model, x, y);
        }
        if (general)
        {
            Phase phase;
            lock (e) phase = e.Phase;
            await ToAll(player, e, StatesOf(phase, e), "spawn_lightning=0");
        }
    }

    /// <summary>A one-shot effect object at a cell, removed after <see cref="FlashSeconds"/>.</summary>
    static async Task Flash(Player player, string? model, int x, int y)
    {
        if (model is null) return;
        uint id = player.TakeObjectId();
        bool created = false;
        await LegionOutput.ToArea(player, async p =>
        {
            if (!await global::Game.SendWorldCreate(PlayerOf(p), id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, model).Blob()), null)) return;
            created = true;
            await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
        // One area timer: it fires if the player who triggered it left, and takes the players there then.
        if (created) global::Game.SendAfter(player, FlashSeconds, () => LegionOutput.Remove(player, id));
    }

    static async Task BreakChest(IModuleContext module, Player player, Hold hold, int x, int y)
    {
        var e = hold.E;
        var chest = hold.Member;
        double radius = ChestRadius * Math.Max(0, 100 + global::Game.AreaLeagueStats(player).GetValueOrDefault(Stat.MapLegionChestsBreakOutRangePlusPct)) / 100.0;
        LegionStasis.Member[] army;
        lock (e) army = e.Army ?? [];
        double now = global::Game.AreaTime(player);
        var near = new List<(Hold Hold, SpawnedMonster Monster)>();
        foreach (var m in army)
        {
            if (m.Chest || m.Tier > 1 || m.Broken) continue;
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead || !Holds.TryGetValue(monster, out var h)) continue;
            var (mx, my) = monster.Walk.Position(now);
            double dx = mx - x, dy = my - y;
            if (dx * dx + dy * dy > radius * radius) continue;
            lock (h) { if (h.Broken) continue; h.Broken = true; }
            near.Add((h, monster));
        }
        MarkBroken(player, e, [chest.Id]);
        await Flash(player, ModelOf(ChestExplodeKey), x, y);
        await LegionStasis.Remove(player, [chest]);
        await SpawnBox(module, player, e, chest, x, y);
        await Free(player, e, near);
        module.Log($"chest broke out at ({x}, {y}), freed {near.Count}");
    }

    // ---- the openable chest ----

    /// <summary>The chest the war chest becomes (the plan's Chests row; the War Hoard's for a hoard), standing where it was.</summary>
    static async Task SpawnBox(IModuleContext module, Player player, Encounter e, LegionStasis.Member member, int x, int y)
    {
        int row = e.Plan is { } plan && member.Faction < plan.Factions.Length
            ? plan.Factions[member.Faction].Chests.FirstOrDefault(c => c.WarHoard == member.WarHoard)?.Chest ?? -1 : -1;
        string? type = ClientTables.TableOrEmpty("Chests").Where(r => r.Index == row).Select(r => r.Text("Id")).FirstOrDefault();
        if (type is null || QuestObjectTypes.Find(type) is not { } info || !QuestObject.CanSerialize(info.Components))
        {
            module.Log($"chest row {row} cannot be shown: no chest at ({x}, {y})");
            return;
        }
        var box = new Box(player.TakeObjectId(), type, x, y, 0, null, info.OwnModel ? null : info.Model, member.WarHoard, false);
        lock (e) { e.Boxes = [.. e.Boxes ?? [], box]; Keep(player, e); }
        if (await ShowBox(player, box, info))
            foreach (var p in Members(player)) BlockBox(p, box, info);
    }

    /// <summary>The chest for the area, created once; true when the area has it.</summary>
    static async Task<bool> ShowBox(Player player, Box box, QuestObjectTypes.Info info)
    {
        if (InArea(player, box.Id)) return true;
        var obj = new QuestObject(box.X, box.Y, info.Components, box.Rotation, box.Z, box.Model,
            TransitionState: (byte)(box.Opened ? info.States : 1), Opened: box.Opened);
        var create = InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(box.Type), obj.Blob());
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

    /// <summary>A player who enters later: the area gives its client the chests as they are; the player's walk is blocked by them.</summary>
    static async Task ShowBoxes(Player player, Encounter e)
    {
        Box[] boxes;
        lock (e) boxes = e.Boxes ?? [];
        foreach (var box in boxes)
            if (QuestObjectTypes.Find(box.Type) is { } info && await ShowBox(player, box, info)) BlockBox(player, box, info);
    }

    /// <summary>A click on one of the chests: walk up, open it once (it is created again opened under a new id, as a Chest
    /// has no update reader) and drop its items.</summary>
    static async Task<bool> OpenBox(Player player, Encounter e, uint targetId, double requestedAt)
    {
        Box? box;
        lock (e) box = e.Boxes?.FirstOrDefault(b => b.Id == targetId);
        if (box is null) return false;
        if (box.Opened || player.Closed || player.LeavingArea || player.PlayerLife == 0 || QuestObjectTypes.Find(box.Type) is not { } info) return true;
        if (!await WorldOf(player).WalkUpTo(box.X, box.Y, Math.Max(1, info.Size), requestedAt)) return true;
        Box opened;
        lock (e)
        {
            int i = Array.FindIndex(e.Boxes ?? [], b => b.Id == targetId);
            if (i < 0 || e.Boxes![i].Opened) return true;
            opened = e.Boxes[i] with { Id = player.TakeObjectId(), Opened = true };
            e.Boxes = [.. e.Boxes.Select((b, n) => n == i ? opened : b)];
            Keep(player, e);
        }
        foreach (var member in Members(player)) member.RemoveBlocker(targetId);
        if (InArea(player, targetId)) await LegionOutput.Remove(player, targetId);
        if (await ShowBox(player, opened, info))
            foreach (var member in Members(player)) BlockBox(member, opened, info);
        await DropBox(player, e, opened, info.Size);
        return true;
    }

    /// <summary>The chest's items on the ground in a ring around it: the server's container roll at the area level (GUESS for the
    /// number of rolls; no per-chest drop table is known).</summary>
    static async Task DropBox(Player player, Encounter e, Box box, int size)
    {
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = global::Game.AreaRng(player);
        var drops = global::Game.DropsFor(player).Container(Math.Clamp(e.Level, 1, 100), box.Hoard ? HoardRolls : ChestRolls, 0, rng);
        int ring = Math.Max(2, size / 2 + 2);
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653 + rng.NextDouble();
            var at = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * ring), box.Y + (int)Math.Round(Math.Sin(angle) * ring)));
            var ground = new WorldItemObject(at.X, at.Y, drops[i].Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await global::Game.SendGroundItem(player, id, ground, $"Legion chest 0x{box.Id:x} drops {drops[i].Type.Name} x{drops[i].Item.StackSize}", justDropped: true);
        }
    }
}
