using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Mercenary;

public sealed partial class MercenaryModule
{
    public sealed class Wild
    {
        public uint Body { get; set; }
        public int Class { get; set; }
        public int Build { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool Paid { get; set; }
        public List<MercenaryEquipment.Worn> Equipment { get; set; } = [];
    }

    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is { } opening && MapDevices.IsMapArea(opening.Area.Hash16)
            && MercenaryTables.EncounterRulesRow is not null && MercenaryTables.Eligible().Count > 0 && MercenaryTables.Eligible(true).Count > 0)
            request.HandledContent.Add("Mercenaries");
    }

    async Task PlaceWilds(Player player, Run run, AreaPopulated area, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        if (run.WildsPlanned) return;
        run.WildsPlanned = true;
        var points = new List<(int X, int Y)>();
        if (run.Stage != Stage.None && stats.GetValueOrDefault(Stat.MapMercanaryAccompaniedByParty) > 0)
            points.AddRange([(run.X + 8, run.Y), (run.X - 8, run.Y)]);
        int chance = stats.GetValueOrDefault(Stat.MapExileAccompaniedByWildMercenaryChancePct);
        if (chance > 0)
            foreach (var monster in player.Area.Monsters.Values.Where(m => !m.Dead && m.PackId == 0
                && player.Instance.World.MonsterOrigins.TryGetValue(m.ObjectId, out var origin)
                && origin.Object.Stats?.Any(s => s.Key == Stat.IsRogueExile && s.Value > 0) == true).ToArray())
                if (rng.Next(100) < chance)
                {
                    var at = monster.Walk.Position(AreaTime(player)); points.Add((at.X + 8, at.Y));
                }
        int difficulty = Math.Max(0, stats.GetValueOrDefault(Stat.MapWildMercenaryRewardAndDifficultyPerWildMercenary)) * points.Count;
        foreach (var aim in points)
        {
            if (MercenaryTables.Pick(rng, stats.GetValueOrDefault(Stat.MapMercenariesAreInfamous) > 0) is not { } pick
                || Monsters.Find(pick.Kind.Path) is not { Supported: true } variety) continue;
            var at = player.Layout.Near(aim);
            var equipment = MercenaryEquipment.Roll(pick.Kind, pick.Build, area.Level, rng,
                stats.GetValueOrDefault(Stat.MapMercenaryAllUniqueItems) > 0, UniquesFor(player));
            var fallback = MercenaryGear.Of(pick.Kind, pick.Build, area.Level, new Random(unchecked(area.Seed * 397 ^ pick.Build.Row)));
            uint id = await SpawnMonster(player, variety, area.Level, MonsterRarity.Unique, at.X, at.Y, instanceOwned: true,
                ownedLooks: MercenaryEquipment.Looks(equipment, fallback), extraMods: MercenaryEquipment.Effects(equipment, difficulty),
                ownedKit: KitOf(player, variety, pick.Build, area.Level, area.Seed), shape: o => o with
                {
                    MercenaryBuild = pick.Build.Hash16, StateMachine = States(variety, MercenaryWire.DuelStates), HasDialogue = false, NpcFlags = 0,
                });
            if (id == 0) continue;
            run.Wilds.Add(new Wild { Body = id, Class = pick.Kind.Row, Build = pick.Build.Row, X = at.X, Y = at.Y, Equipment = equipment });
        }
        context.Log($"{player.Layout.Id}: {run.Wilds.Count}/{points.Count} wild mercenaries, {difficulty}% increased difficulty");
    }

    void RebindWilds(Player player, Run run)
    {
        var used = run.Wilds.Where(w => player.Area.Monsters.ContainsKey(w.Body)).Select(w => w.Body).ToHashSet();
        foreach (var wild in run.Wilds.Where(w => !w.Paid && !player.Area.Monsters.ContainsKey(w.Body)))
        {
            if (MercenaryTables.ClassOf(wild.Class) is not { } kind) continue;
            var found = player.Area.Monsters.FirstOrDefault(p => !used.Contains(p.Key) && !p.Value.Dead && p.Value.Variety?.Id == kind.Path
                && p.Value.Hostile && p.Key != run.Body);
            if (found.Value is null) continue;
            wild.Body = found.Key; used.Add(found.Key);
        }
        Save(player, run);
    }

    void WildDrops(DropRoll roll)
    {
        if (Peek(roll.Player) is not { } run) return;
        Wild? wild;
        lock (run)
        {
            wild = run.Wilds.FirstOrDefault(w => w.Body == roll.Monster.ObjectId && !w.Paid);
            if (wild is null || MercenaryTables.ClassOf(wild.Class)?.Path != roll.Monster.VarietyId) return;
            wild.Paid = true;
        }
        Save(roll.Player, run);
        if (!GameEvents.AllowsLoot(roll.Player) || wild.Equipment.Count == 0) return;
        var stats = AreaLeagueStats(roll.Player);
        var rng = AreaRng(roll.Player);
        // The live item selection weights are not published; equipment is sampled uniformly without replacement.
        var pool = wild.Equipment.ToList();
        int count = rng.Next(100) < stats.GetValueOrDefault(Stat.MapMercenaryDropAllItemsChancePct) ? pool.Count
            : Math.Min(pool.Count, 1 + Math.Max(0, stats.GetValueOrDefault(Stat.MapMercenaryDropAdditionalItems)));
        for (int i = 0; i < count; i++)
        {
            int at = rng.Next(pool.Count); roll.Made.Add(pool[at].Item with { Identified = false }); pool.RemoveAt(at);
        }
    }
}
