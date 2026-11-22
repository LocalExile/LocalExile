using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool ConsumeAnimatedSourceLocked(ClientState conn, AnimatedSource? source)
    {
        if (source?.Ground is { } ground) return conn.Instance.Ground.Take(source.Id, ground) is not null;
        if (source?.BladeSource is not { } blades) return true;
        if (!conn.LingeringBladeSources.Contains(blades)) return false;
        double now = AreaTime(conn);
        return blades.Blades.RemoveAll(b => b.Id == source.Id && b.Landed == 0 && b.Gone > now) == 1;
    }

    static IEnumerable<Item> RollVaalWeapons(ClientState conn, int minimum, int maximum)
    {
        var items = ItemsFor(conn);
        var skills = SkillsFor(conn);
        var uniques = UniquesFor(conn);
        var definitions = uniques.All.Where(unique => items.Find(unique.BaseTypeId) is { } type
            && type.Class is not ("Bow" or "Wand") && skills.WeaponBase(type.Row) is not null).ToArray();
        Random.Shared.Shuffle(definitions);
        foreach (var definition in definitions)
        {
            if (uniques.Create(definition, Random.Shared) is not { } item || items.ByHash(item.TypeHash) is not { } type
                || type.Class is "Bow" or "Wand" || skills.WeaponBase(type.Row) is null) continue;
            int required = ItemModStats.LevelRequirement(item, type.DropLevel, items.Mods);
            if (required >= minimum && required <= maximum) yield return item;
        }
    }

    internal static async Task<bool> ReleaseVaalWeapons(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort source, SupportedGem gem, (int X, int Y) aim, double duration, Func<bool> current)
    {
        // The client supplies no radius for its ground search. Use the ordinary animated-item pickup radius.
        const double searchRadius = 15;
        var definition = SummonData.Find("VaalAnimateWeapon")!;
        int cap = Math.Min(SummonLimit(conn, definition, gem),
            GolemStat(gem.Stats, "vaal_animate_weapon_raise_up_to_X_weapons_as_uniques"));
        if (cap <= 0) return false;
        var candidates = new List<(uint Id, (int X, int Y) At, WorldItemObject? Ground, BladefallTrarthusState? Blades)>();
        lock (conn.Monsters)
        {
            double now = AreaTime(conn);
            foreach (var (id, ground) in conn.Instance.Ground.Within(aim.X, aim.Y, searchRadius,
                ground => AnimatedItemSlot(conn, definition, gem, ground.Item) != 0))
            {
                var origin = conn.Instance.Ground.Origin(id);
                if (origin.Shared || ReferenceEquals(origin.By, conn))
                    candidates.Add((id, (ground.GridX, ground.GridY), ground, null));
            }
            foreach (var state in conn.LingeringBladeSources)
                foreach (var blade in state.Blades)
                    if (blade.Landed == 0 && blade.Gone > now && Dist(aim, blade.At) <= searchRadius)
                        candidates.Add((blade.Id, blade.At, null, state));
        }
        if (candidates.Count == 0) return true;
        int minimum = GolemStat(gem.Stats, "vaal_animate_weapon_minimum_level_requirement");
        int maximum = GolemStat(gem.Stats, "animate_item_maximum_level_requirement");
        using var weapons = RollVaalWeapons(conn, minimum, maximum).GetEnumerator();
        int summoned = 0;
        Item? replacement = null;
        foreach (var entry in candidates.OrderBy(entry => Dist(aim, entry.At)))
        {
            if (!current()) return false;
            if (replacement is null)
            {
                if (!weapons.MoveNext()) break;
                replacement = weapons.Current;
            }
            var animated = new AnimatedSource(entry.Id, entry.Ground, new() { [2] = replacement }, BladeSource: entry.Blades);
            if (await SummonFighterCore(remote, stream, ciphers, conn, source, definition, entry.At,
                current: current, remaining: duration, triggered: true, preparedEquipment: animated) == 0) continue;
            replacement = null;
            if (++summoned >= cap) break;
        }
        return true;
    }
}
