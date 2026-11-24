using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapEssences
{
    /// <summary>Crystal Resonance's map-boss half (atlas_essence_3_5, map_essence_X_mods_inherited_by_map_boss): every
    /// imprisoned monster slain grants the map boss a random essence modifier from that monster's essences; the boss is
    /// not imprisoned, its life is recovered when it gains a modifier, and it drops the inherited essences when slain.
    /// The essences are kept here for the drop; the boss's own mods/life carry the modifiers (and persist through the
    /// instance's MonsterOrigins record).</summary>
    sealed class BossInheritance
    {
        public readonly List<int> Essences = [];
        public int Applied;
    }
    static readonly RuleTable<InstanceState, BossInheritance> Inherited = new();

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> AreaBossIds = new();

    /// <summary>The area's own boss varieties (WorldAreas Bosses_MonsterVarietiesKeys): the map boss is named there even
    /// when it has no big life bar — Mesa's Oak the Mighty has BossHealthBar 0, so the bar alone misses it.</summary>
    static HashSet<string> BossIdsOf(Player conn) => AreaBossIds.GetOrAdd(conn.Layout.Id, id =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!ClientTables.Has("WorldAreas") || !ClientTables.Has("MonsterVarieties")) return set;
        var varieties = ClientTables.Table("MonsterVarieties");
        foreach (var area in ClientTables.Table("WorldAreas"))
            if (area.Text("Id") == id)
                foreach (int row in area.Ints("Bosses_MonsterVarietiesKeys"))
                    if (row >= 0 && row < varieties.Count && varieties[row].Text("Id") is { Length: > 0 } vid)
                        set.Add(vid);
        return set;
    });

    static bool IsMapBoss(Player conn, MonsterCatalog.Variety? v) =>
        v is not null && (BossIdsOf(conn).Contains(v.Id) || v.BossHealthBar || MapScarabBosses.IsBoss(v));

    /// <summary>The essence's display mod (Essences Display_Monster_ModsKey), the row that names the essence on the
    /// monster's hover; −1 when none. The catalogue keeps the stat mods only, so it is read here for the inheritance.</summary>
    static int EssenceDisplayMod(int row) =>
        ClientTables.Has("Essences") && ClientTables.Table("Essences").Row(row)?.IntOrNull("Display_Monster_ModsKey") is int display
            ? display : -1;

    /// <summary>A slain released rare hands X random essences to the map boss (X = the stat's value, 1 for Crystal
    /// Resonance). Called once per rare kill, after the reward gate. The modifiers are applied to the live boss by the
    /// tick (it may still be held back far from the player).</summary>
    static void InheritBossEssences(Player conn, Monolith monolith)
    {
        if (MapModsOf(conn) is not { } mods) return;
        int count = mods.Get(Stat.MapEssenceXModsInheritedByMapBoss);
        if (count <= 0 || monolith.Essences.Count == 0) return;
        var inh = Inherited.GetValue(conn.Instance, _ => new());
        for (int i = 0; i < count; i++)
            inh.Essences.Add(monolith.Essences[Random.Shared.Next(monolith.Essences.Count)]);
        Console.WriteLine($"[map-content] essence Crystal Resonance: map boss inherits {count} essence mod(s) from slain 0x{monolith.Monster:x}");
    }

    /// <summary>A slain map boss drops the essences it inherited from imprisoned monsters (the wiki: the unique boss
    /// that carries the essence modifier drops the corresponding essence). Runs before the monolith reward gate.</summary>
    static void BossEssenceDrops(DropRoll roll)
    {
        if (roll.Monster.Rarity != MonsterRarity.Unique || !IsMapBoss(roll.Player, Monsters.Find(roll.Monster.VarietyId))) return;
        if (!Inherited.TryGetValue(roll.Player.Instance, out var inh) || inh.Essences.Count == 0) return;
        foreach (int row in inh.Essences)
            if (MapEssenceCatalog.Find(row) is { } essence) roll.Items.Add(essence.Item);
        Console.WriteLine($"[map-content] essence Crystal Resonance: map boss 0x{roll.Monster.ObjectId:x} drops {inh.Essences.Count} inherited essence(s)");
        inh.Essences.Clear(); inh.Applied = 0;
    }

    /// <summary>Applies the pending inherited essences to the live map boss, then publishes the change: its mods (name
    /// plate), maximum life scaled by the modifiers, life recovered to full (the wiki), attack and speed, and the
    /// instance record later clients are created from. One of several map bosses takes them all.</summary>
    static async Task ApplyBossEssences(Player conn, BossInheritance inh)
    {
        uint bossId = 0, life = 0;
        List<(uint Key, int Value)>? client = null;
        byte[]? magic = null;
        SpawnedMonster? boss = null;
        foreach (var (id, m) in conn.Area.Monsters)
            if (!m.Dead && m.Rarity == MonsterRarity.Unique && IsMapBoss(conn, m.Variety)) { boss = m; bossId = id; break; }
        if (boss is null) return; // held back until the player comes near; a later tick applies them
        bool changed = false;
        while (inh.Applied < inh.Essences.Count)
        {
            int row = inh.Essences[inh.Applied++];
            if (MapEssenceCatalog.Find(row) is not { } essence) continue;
            // The stat mods plus the essence-named display mod, so the inherited essence shows on the boss's hover.
            var (mods, stats) = TormentCatalog.Roll(essence.MonsterMods.Append(EssenceDisplayMod(essence.Row)).Where(r => r >= 0), Random.Shared);
            if (mods.Count == 0) continue;
            var before = boss.Effects;
            uint oldLife = boss.MaxLife;
            boss.Mods = boss.Mods with { Mods = [.. boss.Mods.Mods, .. mods], Effects = boss.Mods.Effects.Combine(stats) };
            int scale = boss.Effects.MaxLife(100000);
            if (scale != before.MaxLife(100000))
                boss.MaxLife = (uint)Math.Clamp((long)oldLife * scale / Math.Max(1, before.MaxLife(100000)), 1, uint.MaxValue);
            boss.Life = boss.MaxLife; // the wiki: a boss that already took damage recovers its life
            if (boss.Attack is { } attack)
                boss.Attack = attack with
                {
                    Damage = MonsterModEffects.AddedTo(attack.Damage, stats),
                    Seconds = before.AttackSeconds(attack.Seconds, stats.GetValueOrDefault(Stat.AttackSpeedPlusPct)),
                };
            changed = true;
        }
        if (!changed) return;
        life = boss.Life;
        client = [(Stat.MaximumLife, (int)Math.Min(int.MaxValue, boss.MaxLife)), (Stat.MovementVelocityPlusPct, boss.Effects.MovementPct)];
        if (boss.Attack is { } a) client.AddRange(a.Stats());
        if (conn.Instance.World.MonsterOrigins.TryGetValue(bossId, out var origin))
        {
            var keys = client.Select(c => c.Key).ToHashSet();
            conn.Instance.World.MonsterOrigins[bossId] = origin with
            {
                Object = origin.Object with
                {
                    Mods = boss.Mods.Mods,
                    Life = boss.MaxLife,
                    Stats = [.. (origin.Object.Stats ?? []).Where(s => !keys.Contains(s.Key)), .. client],
                },
            };
            var w = new BlobWriter();
            MagicProperties.Write(w, (byte)boss.Rarity, identified: true, boss.Mods.Mods, boss.Mods.NameWords, origin.Object.Name);
            w.U8(0);
            magic = InstanceEntities.ComponentUpdate(bossId, 2, w.ToArray());
        }
        if (magic is not null)
            await SendWorldRecord(conn, bossId, magic, $"essence boss 0x{bossId:x} inherited mods");
        await SendWorldRecord(conn, bossId, InstanceEntities.StatsUpdate(bossId, client!), null);
        await SendWorldRecord(conn, bossId, InstanceProtocol.LifeUpdate(bossId, life), null);
    }
}