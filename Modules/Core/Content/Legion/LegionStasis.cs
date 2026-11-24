using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The two armies standing in stasis around the monolith: the planned monsters and chest monsters are spawned as the
/// instance's own hostile monsters, held the way the Essence's encased previews are (no aggro, no attack, no rewards, AI
/// held, buff 1311 frozen_in_time, animation paused). The client draws the crystal itself (it is in the Legion monsters'
/// models, and their StateMachine defaults breakout 0 / activated 0 mean frozen), so no state is sent at spawn.
/// Hits on a frozen monster are stored and zeroed until the encounter ends (LegionMonolith.Breakout.cs).
/// </summary>
static class LegionStasis
{
    /// <summary>A spawned army member: its object id, the army (index into the plan's factions), the rank tier (0 normal,
    /// 1 elite, 2 sergeant, 3 general), whether it is a chest monster (a War Hoard when WarHoard), and whether it broke out.</summary>
    public sealed record Member(uint Id, ushort Buff, int Faction, int Tier, bool Chest, bool WarHoard, bool Broken = false);

    // THE FORMATION'S GEOMETRY IS A GUESS: tune these after the live check. Radii are in grid cells from the monolith.
    const double MinRadius = 12, MaxRadius = 60;
    /// <summary>Half the width of an army's sector (radians) around its half of the axis: 60 degrees.</summary>
    const double SectorHalfWidth = Math.PI / 3;
    /// <summary>How many random spots are tried for one member before it is skipped.</summary>
    const int Tries = 8;
    /// <summary>BuffDefinitions frozen_in_time, with its default visual 1082 legion_petrify. The faction's own
    /// BuffVisuals 1099-1103 as an override are a live-check question.</summary>
    const ushort FrozenBuff = 1311;

    /// <summary>The spot of one member: army 0 on one side of the axis through the monolith and army 1 on the opposite side,
    /// its depth a random percent inside the member's band. The whole geometry is here, to be tuned after the live check.</summary>
    static (int X, int Y)? Spot(Player player, Random rng, int cx, int cy, double axis, int army, int fromPct, int toPct, int bodySize)
    {
        var layout = player.Layout;
        for (int i = 0; i < Tries; i++)
        {
            double pct = fromPct + rng.NextDouble() * Math.Max(0, toPct - fromPct);
            double radius = MinRadius + Math.Clamp(pct, 0, 100) / 100 * (MaxRadius - MinRadius);
            double angle = axis + (army == 0 ? 0 : Math.PI) + (rng.NextDouble() * 2 - 1) * SectorHalfWidth;
            int x = cx + (int)Math.Round(Math.Cos(angle) * radius), y = cy + (int)Math.Round(Math.Sin(angle) * radius);
            if (x < 0 || y < 0 || x >= layout.GridW || y >= layout.GridH) continue;
            if (layout.WalkableFor(x, y, bodySize) && !player.Barrier.Contains((x, y))) return (x, y);
        }
        return null;
    }

    /// <summary>Spawns both armies of the plan frozen; each member is handed to <paramref name="keep"/> (false: the timer ended, it is removed and spawning stops).</summary>
    public static async Task Spawn(IModuleContext module, Player player, LegionArmy.ArmyPlan plan, int cx, int cy, int level, Func<Member, bool> keep)
    {
        var rng = global::Game.AreaRng(player);
        double axis = rng.NextDouble() * Math.PI * 2;
        byte areaLevel = (byte)Math.Clamp(level, 1, 100);
        int skipped = 0, sergeants = 0, generals = 0, chests = 0;
        var counts = new int[plan.Factions.Length];
        bool stopped = false;   // the timer ended while the army was being spawned

        async Task Add(int faction, int row, MonsterRarity rarity, int tier, bool chest, bool hoard, int from, int to)
        {
            if (stopped) return;
            if (Monsters.Find(row.ToString()) is not { Supported: true } variety || player.Closed || player.LeavingArea) { skipped++; return; }
            if (Spot(player, rng, cx, cy, axis, faction, from, to, Math.Clamp(variety.ObjectSize - 1, 0, 127)) is not { } at) { skipped++; return; }
            // bulk: no wait for the client's birth per monster (a frame each, 5-8 s for 150-250); the buff and pause records follow the create in the same frame.
            uint id = await SpawnMonster(player, variety, areaLevel, rarity, at.X, at.Y,
                friendly: false, unrisen: false, alone: false, scripted: true, instanceOwned: true, bulk: true, log: false);
            if (id == 0) { skipped++; return; }
            ushort buff = await Freeze(player, id, variety.Id);
            if (!keep(new Member(id, buff, faction, tier, chest, hoard))) { stopped = true; await Remove(player, [new Member(id, buff, faction, tier, chest, hoard)]); return; }
            counts[faction]++;
            if (chest) chests++; else if (tier == 2) sergeants++; else if (tier == 3) generals++;
        }

        for (int f = 0; f < plan.Factions.Length; f++)
        {
            var faction = plan.Factions[f];
            foreach (var squad in faction.Squads)
            {
                // A GUESS to confirm in the live check: elite Magic, sergeant Rare; the general is Unique through its MonsterMapBoss mod.
                var rarity = squad.Tier switch { 1 => MonsterRarity.Magic, 2 => MonsterRarity.Rare, 3 => MonsterRarity.Unique, _ => MonsterRarity.Normal };
                for (int i = 0; i < squad.Count; i++) await Add(f, squad.Monster, rarity, squad.Tier, false, false, squad.FromPct, squad.ToPct);
            }
            foreach (var chest in faction.Chests)
                await Add(f, chest.Monster, MonsterRarity.Normal, 0, true, chest.WarHoard, chest.FromPct, chest.ToPct);
        }
        string names = string.Join(" + ", plan.Factions.Select((f, i) => $"{f.Id} {counts[i]}"));
        module.Log($"army spawned: {names} ({sergeants} sergeants, {generals} general, {chests} chests), {skipped} skipped");
    }

    /// <summary>The stasis of one spawned monster: held like the Essence's encased previews (EssenceEncased.cs).</summary>
    static async Task<ushort> Freeze(Player player, uint id, string variety)
    {
        // No EncounterStats[BaseCannotBeDamaged]: the break-out counts hits through GameEvents.OutgoingDamage (LegionMonolith.Breakout.cs).
        // EntranceGrace makes the monster Invulnerable: damage over time and auras end in TakeDamageLocked, which then takes no life.
        // Only the player's own direct hits reach OutgoingDamage and count; totems, brands, traps, mines and minions drop the hit earlier.
        // LIVE CHECK: the animation stays paused (0x1b2 speed 0) when the buff is removed at break-out.
        if (player.Area.Monsters.TryGetValue(id, out var monster))
        {
            monster.NoRewards = true; monster.Attack = null; monster.Aggro = false; monster.EntranceGrace = true;
            monster.BusyUntil = double.PositiveInfinity;
        }
        ushort buff = player.NextBuff++;
        await LegionOutput.ToArea(player, p => SendStasis(p, id, buff, variety, true));
        return buff;
    }

    static async Task SendStasis(Player player, uint id, ushort buff, string variety, bool withBuff)
    {
        if (withBuff) await global::Game.SendWorldRecord(player, id, InstanceEntities.AddBuff(id, buff, FrozenBuff), $"legion stasis {variety}");
        // Pause the actor: 0x1b2 kind 0 plays animation 0 at speed 0.
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 0), $"legion freeze {variety}");
    }

    /// <summary>A player who enters later: the area gives its client the monsters with their states (MonsterOrigins), but not
    /// the buff and the paused animation sent at spawn, so these go to that player alone (only the pause for one that broke out).</summary>
    public static async Task Present(Player player, Member[]? army)
    {
        if (army is not { Length: > 0 }) return;
        await LegionOutput.ToOne(player, async () =>
        {
            foreach (var member in army)
            {
                if (!player.Area.Monsters.TryGetValue(member.Id, out var m) || m.Dead) continue;
                await SendStasis(player, member.Id, member.Buff, "member", !member.Broken);   // a broken-out one is still paused, without the buff
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
    /// stunned: all fixed at spawn), so it is swapped for a new ordinary monster of the same variety, rarity and cell, with its own
    /// kit, AI and rewards, already <c>breakout</c> 1 and <c>activated</c> 1. LIVE CHECK: whether the swap is visible.
    /// Returns false when the new monster could not be made (the frozen one is then left standing).</summary>
    public static async Task<bool> Wake(Player player, Member member, SpawnedMonster monster)
    {
        var variety = monster.Variety;
        if (variety is null) return false;
        var (x, y) = monster.Walk.Position(global::Game.AreaTime(player));
        uint id = await SpawnMonster(player, variety, monster.Level, monster.Rarity, x, y, mods: monster.Mods,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false, placement: MonsterPlacement.Established);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var woken)) return false;   // the frozen one stays: the caller removes it
        await Remove(player, [member]);
        await SetState(player, id, woken, "breakout", "activated");
        return true;
    }

    /// <summary>Removes the members still standing from the instance and from every client in the area; returns how many.</summary>
    public static async Task<int> Remove(Player player, IEnumerable<Member> army)
    {
        var ids = new List<uint>();
        foreach (var member in army)
            if (player.Area.Monsters.Remove(member.Id, out var m))
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                player.Instance.World.MonsterOrigins.TryRemove(member.Id, out _);
                ids.Add(member.Id);
            }
        var members = LegionOutput.Members(player).ToList();
        foreach (uint id in ids)
            _ = LegionOutput.ToArea(members, p => global::Game.SendWorldRemoval(p, id));   // not awaited: ~200 sends must not hold the tick
        return ids.Count;
    }
}
