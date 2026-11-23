using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The imprisoned monsters inside the crystals: the monolith's rare and the pack's magic monsters as paused, invulnerable
/// monster objects the client draws its model, life bar and essence panel from. This is the encounter's own display, not a
/// monster behaviour — the released rare's spells live in Monster/Mods/EssenceDaemons.cs and beside it.
/// </summary>
public static partial class MapEssences
{
    /// <summary>The encased preview monsters: the rare and the pack's magic monsters as paused, invulnerable monster
    /// objects in the frozen visual, standing in the crystals. The client draws the model, life bar and essence panel
    /// from these objects; the monolith crystal renders around them. Instance-owned so every client sees them.</summary>
    public static async Task<Monolith> SpawnEncasedPreviews(Player conn, Monolith monolith)
    {
        byte level = (byte)AreaLevel(conn);
        uint main = 0;
        ushort mainBuff = 0;
        // The imprisoned preview carries only its own rare affixes (null mods → the ordinary rare roll), NOT the essence
        // mods: the panel's top section is the monster's ObjectMagicProperties, and the essence names belong only to the
        // Monolith component's list below it. Applying essence mods here baked them into the preview, so a corruption
        // that changed the essences left the old names in the top section. The essence mods apply on release instead.
        if (Monsters.Find(monolith.Variety) is { Supported: true } rv)
            (main, mainBuff) = await SpawnEncased(conn, rv, level, MonsterRarity.Rare, monolith.X, monolith.Y, null);
        var minis = monolith.Minis.ToList();
        for (int i = 0; i < minis.Count; i++)
        {
            if (Monsters.Find(minis[i].Variety) is not { Supported: true } mv) continue;
            // The minis are the pack's monsters themselves, friendly (allies take no target or hover) and asleep, with
            // hide_mini_life_bar so not even the ally bar shows. They keep the model, the ice and the freeze.
            var (id, buff) = await SpawnEncased(conn, mv, level, MonsterRarity.Magic, minis[i].X, minis[i].Y, null, friendly: true);
            minis[i] = minis[i] with { Preview = id, PreviewBuff = buff };
        }
        return monolith with { Preview = main, PreviewBuff = mainBuff, Minis = minis };
    }

    static async Task<(uint Id, ushort Buff)> SpawnEncased(Player conn, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        int x, int y, MonsterModSet? mods, bool friendly = false)
    {
        // Asleep (no orders) with the client's hide_mini_life_bar stat: the model renders, but no life bar. The rare is
        // hostile so the monolith's panel shows its mods; a mini is friendly, so the client does not target it at all.
        // Established: the crystal's cell is already placed and checked, and the monster stands in its centre. The
        // proposed placement's nearest-clear search would push it out of the crystal (the monolith's own blocker).
        uint id = await SpawnMonster(conn, variety, level, rarity, x, y,
            friendly: friendly, mods: mods, unrisen: false, alone: false, scripted: true, instanceOwned: true,
            placement: MonsterPlacement.Established,
            // hide_mini_life_bar hides the bar. A mini (friendly) also gets virtual_is_hidden_monster (stat key 0x59d0,
            // row 22991), the client's other "not a real monster" marker, which should take the name and hover with it.
            extraStats: friendly ? [(Stat.HideMiniLifeBar, 1), (Stat.VirtualIsHiddenMonster, 1)] : [(Stat.HideMiniLifeBar, 1)],
            log: false);
        if (id == 0) return (0, 0);
        // It stands in the crystal: it neither moves, attacks nor can be hurt or rewarded, but it is a real instance
        // monster (the monolith's panel and the client's encased state read it by id).
        if (conn.Area.Monsters.TryGetValue(id, out var monster))
        {
            monster.NoRewards = true; monster.Attack = null; monster.Aggro = false;
            monster.EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };
        }
        // The essence's own effect only: BuffDefinitions 1311 frozen_in_time with its visual overridden to
        // BuffVisuals 658 `monster_essence_effect_buff` (3.30 661), the light_blue monster effect. No BuffDefinitions
        // row uses 658 in either build, so an AddBuff visual override is its only use. The row's own visual is
        // legion_petrify and is never sent: it is Legion's, not the essence's.
        ushort buff = conn.NextBuff++;
        await SendWorldRecord(conn, id,
            InstanceEntities.AddBuff(id, buff, 1311, visual: 658), $"essence encased {variety.Id}");
        // Freeze the actor without a visual: 0x1b2 kind 0 plays animation 0 at speed 0 (paused in place).
        await SendWorldRecord(conn, id,
            InstanceProtocol.AnimationUpdate(id, 0, 0, 0), $"essence freeze {variety.Id}");
        return (id, buff);
    }

    /// <summary>The frozen state of the encased previews, sent to one player: a client that joins after the crystals
    /// were made has the monster objects but not their buff and paused animation.</summary>
    public static async Task PresentEncasedState(Player conn, Monolith monolith)
    {
        foreach (var (id, buff) in new (uint, ushort)[] { (monolith.Preview, monolith.PreviewBuff) }
            .Concat(monolith.Minis.Select(m => (m.Preview, m.PreviewBuff))))
        {
            if (id == 0 || buff == 0 || !conn.Area.Monsters.ContainsKey(id)) continue;
            await SendWorldRecord(conn, id,
                InstanceEntities.AddBuff(id, buff, 1311, visual: 658), $"essence encased {monolith.Variety}");
            await SendWorldRecord(conn, id,
                InstanceProtocol.AnimationUpdate(id, 0, 0, 0), $"essence freeze {monolith.Variety}");
        }
    }

    /// <summary>A rebuilt instance (a save's monoliths keep their object ids, which the new instance does not): the
    /// previews are spawned again and the record returned with the new ids.</summary>
    public static async Task<Monolith> EnsureEncasedPreviews(Player conn, Monolith monolith)
    {
        if (monolith.Stage >= 4) return monolith;
        bool missing = monolith.Preview == 0 || !conn.Area.Monsters.ContainsKey(monolith.Preview)
            || monolith.Minis.Any(m => m.Stage < 2 && (m.Preview == 0 || !conn.Area.Monsters.ContainsKey(m.Preview)));
        if (!missing) return monolith;
        var cleaned = monolith with
        {
            Preview = 0, PreviewBuff = 0,
            Minis = [.. monolith.Minis.Select(m => m with { Preview = 0, PreviewBuff = 0 })],
        };
        return await SpawnEncasedPreviews(conn, cleaned);
    }

    /// <summary>Removes the encased previews when the crystal breaks; the released monsters take their place.</summary>
    public static void RemoveEncasedPreviews(Player conn, Monolith monolith)
    {
        var ids = new List<uint> { monolith.Preview };
        ids.AddRange(monolith.Minis.Select(m => m.Preview));
        foreach (uint id in ids)
            if (id != 0 && conn.Area.Monsters.Remove(id, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                // The supported world send, not the minion output queue: this runs on a spell/release continuation.
                _ = SendWorldRemoval(conn, id, "essence encased ends");
            }
    }
}