using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class UniqueBeltsFeature
{
    /// <summary>On Replica Headhunter's GainMagicMonsterModsOnKillUnique__1_: the chance a magic monster's kill steals.</summary>
    const string MagicSourceStat = "gain_magic_monster_mods_on_kill_%_chance";

    /// <summary>BuffDefinitions row of <c>stolen_mods_buff_experimented</c> ("Replica Headhunter": "You have taken the mods
    /// of a slain Magic Monster for yourself").</summary>
    const ushort MagicBuffRow = 1653;

    /// <summary>A connection's modifiers stolen from magic monsters: their own stacks and buff, beside Headhunter's.</summary>
    sealed class MagicStolen
    {
        public readonly StolenMods Mods = new();
        public ushort Buff;
    }
    static readonly RuleTable<ClientState, MagicStolen> Magic = new();

    /// <summary>Headhunter's kill: the rare's affixes as stacks (StolenMods), when the killer has its stat. Replica
    /// Headhunter's: a magic monster's affixes, on its chance ("X% chance when you Kill a Magic Monster to gain its
    /// Modifiers for 60 seconds"), as 60 s stacks of their own, shown with their own buff.</summary>
    internal static void StealMonsterMods(ClientState conn, SpawnedMonster m)
    {
        if (m.Rarity is not (MonsterRarity.Rare or MonsterRarity.Magic) || !m.Mods.Affixes.Any() || conn.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(conn);
        var catalog = ItemsFor(conn).Mods;
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (m.Rarity == MonsterRarity.Magic)
        {
            if (Stats.Key(MagicSourceStat) is uint replica
                && RandomRolls.Chance(stats.GetValueOrDefault(replica), 100, Random.Shared))
                Magic.GetValue(conn, _ => new()).Mods.Steal(m.Mods, catalog, now);
            return;
        }
        if (Stats.Key(StolenMods.SourceStat) is uint belt && stats.GetValueOrDefault(belt) > 0)
            conn.StolenMods.Steal(m.Mods, catalog, now);
        if (Stats.Key(StolenMods.RandomSourceStat) is uint jewel)
            conn.StolenMods.StealRandom(m.Mods, catalog, stats.GetValueOrDefault(jewel), now, Random.Shared);
    }

    /// <summary>The character's stat sources with the modifiers it stole (CharacterStatSources).</summary>
    internal static Dictionary<uint, int> WithStolenMods(ClientState conn, Dictionary<uint, int> sources)
    {
        // Every player stat build passes here: an empty set of stacks returns before Stats() groups anything.
        bool rare = conn.StolenMods.Count > 0;
        bool fromMagic = Magic.TryGetValue(conn, out var stolenMagic) && stolenMagic.Mods.Count > 0;
        if (!rare && !fromMagic) return sources;
        var stolen = rare ? conn.StolenMods.Stats() : new Dictionary<string, int>();
        var magic = fromMagic ? stolenMagic!.Mods.Stats() : null;
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in magic is null ? stolen : stolen.Concat(magic))
            if (Stats.Key(stat) is uint key) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
        return stats;
    }

    /// <summary>A rolled hit with Headhunter's "increased damage with hits against rare monsters" (Replica Headhunter's
    /// against magic monsters). It joins the character's global increased damage; the hit's other increases (physical,
    /// spell …) are already in the roll and are not separated here, so with those it counts for a little more than the
    /// game's sum would.</summary>
    internal static DamagePacket WithDamageVsRarity(IReadOnlyDictionary<uint, int> player, SpawnedMonster m, DamagePacket rolled)
    {
        string? stat = m.Rarity switch
        {
            MonsterRarity.Rare => "damage_+%_vs_rare_monsters",
            MonsterRarity.Magic => "damage_+%_vs_magic_monsters",
            _ => null,
        };
        if (stat is null || Stats.Key(stat) is not uint key
            || player.GetValueOrDefault(key) is not (> 0 and var against)) return rolled;
        int increased = Stats.Key("damage_+%") is uint general ? player.GetValueOrDefault(general) : 0;
        return rolled.Scale((100.0 + Math.Max(0, increased) + against) / (100 + Math.Max(0, increased)));
    }

    // Every player tick (GameEvents.Tick, 250 ms): Mageblood's flasks and the stolen modifiers of Headhunter, Replica
    // Headhunter and Inspired Learning. On the tick rather than a loop of the module's own, so a reload's new module steps them from its first tick
    // and the old one stops.
    internal static async Task StepUniqueBeltEffects(ClientState conn)
    {
        var (remote, stream, ciphers) = conn.Wire;
        try
        {
            foreach (var packet in StepMagebloodBuffs(conn))
                await Send(remote, stream, ciphers, packet, "0x01c1/0x01c2 Mageblood flask buff");
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool rare = conn.StolenMods.Step(now, conn.PlayerLife == 0, out var belt, out var jewel) is not null;
            var fromMagic = Magic.TryGetValue(conn, out var m) ? m : null;
            StolenMods.Buff magic = default;
            bool magicChanged = fromMagic?.Mods.Step(now, conn.PlayerLife == 0, out magic, out _) is not null;
            if (!rare && !magicChanged) return;
            List<byte[]> packets = [];
            lock (conn.Monsters)
            {
                RecomputePoolsAndSpeed(conn);
                if (rare)
                {
                    Update(ref conn.StolenModsBuff, StolenMods.BuffRow, belt);
                    Update(ref conn.InspiredLearningBuff, StolenMods.InspiredLearningBuffRow, jewel);
                }
                if (magicChanged) Update(ref fromMagic!.Buff, MagicBuffRow, magic);
            }
            foreach (var packet in packets)
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, packet, "stolen modifier buff");
            await SendCharacterStats(remote, stream, ciphers, conn, "stolen modifiers");

            void Update(ref ushort instance, ushort row, StolenMods.Buff state)
            {
                if (instance != 0) packets.Add(InstanceEntities.RemoveBuff(PlayerId, instance));
                instance = state.Count > 0 ? conn.NextBuff++ : (ushort)0;
                if (instance != 0) packets.Add(InstanceEntities.AddBuff(PlayerId, instance, row, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)state.Remaining), stacks: (ushort)state.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} unique belts: {ex.Message}");
        }
    }
}
