using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class UniqueBeltsFeature
{
    /// <summary>Headhunter's kill: the rare's affixes as stacks (StolenMods), when the killer has its stat.</summary>
    internal static void StealMonsterMods(Player player, SpawnedMonster m)
    {
        if (m.Rarity != MonsterRarity.Rare || !m.Mods.Affixes.Any() || player.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(player);
        var catalog = ItemsFor(player).Mods;
        double now = AreaTime(player);
        if (stats.GetValueOrDefault(StolenMods.SourceStat) > 0)
            player.StolenMods.Steal(m.Mods, catalog, now);
        player.StolenMods.StealRandom(m.Mods, catalog, stats.GetValueOrDefault(StolenMods.RandomSourceStat), now, AreaRng(player));
    }

    /// <summary>The character's stat sources with the modifiers it stole (CharacterStatSources).</summary>
    internal static Dictionary<uint, int> WithStolenMods(Player player, Dictionary<uint, int> sources)
    {
        var stolen = player.StolenMods.Stats();
        if (stolen.Count == 0) return sources;
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in stolen)
            stats[stat] = ActorScale.Saturate((long)stats.GetValueOrDefault(stat) + value);
        return stats;
    }

    /// <summary>A rolled hit with Headhunter's "increased damage with hits against rare monsters". It joins the character's
    /// global increased damage; the hit's other increases (physical, spell …) are already in the roll and are not separated
    /// here, so with those it counts for a little more than the game's sum would.</summary>
    internal static DamagePacket WithDamageVsRarity(IReadOnlyDictionary<uint, int> player, SpawnedMonster m, DamagePacket rolled)
    {
        if (m.Rarity != MonsterRarity.Rare || !(Stat.DamagePlusPctVsRareMonsters is var key)
            || player.GetValueOrDefault(key) is not (> 0 and var against)) return rolled;
        int increased = Stat.DamagePlusPct is var general ? player.GetValueOrDefault(general) : 0;
        return rolled.Scale((100.0 + Math.Max(0, increased) + against) / (100 + Math.Max(0, increased)));
    }

    // Every frame (GameEvents.AreaTick): Mageblood's flasks and the stolen modifiers of Headhunter and Inspired
    // Learning. On the tick rather than a loop of the module's own, so a reload's new module steps them from its first tick
    // and the old one stops.
    internal static async Task StepUniqueBeltEffects(Player player)
    {
        try
        {
            foreach (var packet in StepMagebloodBuffs(player))
                await Send(player, packet, "0x01c1/0x01c2 Mageblood flask buff");
            if (player.StolenMods.Step(AreaTime(player), player.PlayerLife == 0, out var belt, out var jewel) is null)
                return;
            List<byte[]> packets = [];
            RecomputePoolsAndSpeed(player);
            Update(ref player.StolenModsBuff, StolenMods.BuffRow, belt);
            Update(ref player.InspiredLearningBuff, StolenMods.InspiredLearningBuffRow, jewel);
            foreach (var packet in packets)
                await global::Game.SendWorldRecord(player, player.Id, packet, "stolen modifier buff");
            await SendCharacterStats(player, PlayerStatChange.Build);

            void Update(ref ushort instance, ushort row, StolenMods.Buff state)
            {
                instance = (ushort)(state.Count > 0 ? 1 : 0);
                if (state.Count > 0)
                    player.Area.Buffs.Set(player.Id, new(row, player.Id, U68: Buff.Seconds(state.Remaining), Stacks: (ushort)state.Count));
                else player.Area.Buffs.Clear(player.Id, row, player.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} unique belts: {ex.Message}");
        }
    }
}
