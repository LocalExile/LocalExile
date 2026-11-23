using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class UniqueBeltsFeature
{
    /// <summary>BuffDefinitions row of <c>stolen_mods_buff_experimented</c> ("Replica Headhunter": "You have taken the mods
    /// of a slain Magic Monster for yourself").</summary>
    const ushort MagicBuffRow = 1653;

    /// <summary>A player's modifiers stolen from magic monsters: their own stacks and buff, beside Headhunter's.</summary>
    sealed class MagicStolen
    {
        public readonly StolenMods Mods = new();
        public ushort Buff;
    }
    static readonly RuleTable<Player, MagicStolen> Magic = new();

    /// <summary>Headhunter's kill: the rare's affixes as stacks (StolenMods), when the killer has its stat. Replica
    /// Headhunter's: a magic monster's affixes, on its chance ("X% chance when you Kill a Magic Monster to gain its
    /// Modifiers for 60 seconds"), as 60 s stacks of their own, shown with their own buff.</summary>
    internal static void StealMonsterMods(Player player, SpawnedMonster m)
    {
        if (m.Rarity is not (MonsterRarity.Rare or MonsterRarity.Magic) || !m.Mods.Affixes.Any() || player.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(player);
        var catalog = ItemsFor(player).Mods;
        double now = AreaTime(player);
        if (m.Rarity == MonsterRarity.Magic)
        {
            if (RandomRolls.Chance(stats.GetValueOrDefault(Stat.GainMagicMonsterModsOnKillPctChance), 100, AreaRng(player)))
                Magic.GetValue(player, _ => new()).Mods.Steal(m.Mods, catalog, now);
            return;
        }
        if (stats.GetValueOrDefault(StolenMods.SourceStat) > 0)
            player.StolenMods.Steal(m.Mods, catalog, now);
        player.StolenMods.StealRandom(m.Mods, catalog, stats.GetValueOrDefault(StolenMods.RandomSourceStat), now, AreaRng(player));
    }

    /// <summary>The character's stat sources with the modifiers it stole (CharacterStatSources).</summary>
    internal static Dictionary<uint, int> WithStolenMods(Player player, Dictionary<uint, int> sources)
    {
        // Every player stat build passes here: an empty set of stacks returns before Stats() groups anything.
        bool rare = player.StolenMods.Count > 0;
        bool fromMagic = Magic.TryGetValue(player, out var stolenMagic) && stolenMagic.Mods.Count > 0;
        if (!rare && !fromMagic) return sources;
        var stolen = rare ? player.StolenMods.Stats() : new Dictionary<uint, int>();
        var magic = fromMagic ? stolenMagic!.Mods.Stats() : null;
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in magic is null ? stolen : stolen.Concat(magic))
            stats[stat] = ActorScale.Saturate((long)stats.GetValueOrDefault(stat) + value);
        return stats;
    }

    /// <summary>A rolled hit with Headhunter's "increased damage with hits against rare monsters" (Replica Headhunter's
    /// against magic monsters). It joins the character's global increased damage; the hit's other increases (physical,
    /// spell …) are already in the roll and are not separated here, so with those it counts for a little more than the
    /// game's sum would.</summary>
    internal static DamagePacket WithDamageVsRarity(IReadOnlyDictionary<uint, int> player, SpawnedMonster m, DamagePacket rolled)
    {
        uint? key = m.Rarity switch
        {
            MonsterRarity.Rare => Stat.DamagePlusPctVsRareMonsters,
            MonsterRarity.Magic => Stat.DamagePlusPctVsMagicMonsters,
            _ => null,
        };
        if (key is not uint statKey
            || player.GetValueOrDefault(statKey) is not (> 0 and var against)) return rolled;
        int increased = Stat.DamagePlusPct is var general ? player.GetValueOrDefault(general) : 0;
        return rolled.Scale((100.0 + Math.Max(0, increased) + against) / (100 + Math.Max(0, increased)));
    }

    // Every frame (GameEvents.AreaTick): Mageblood's flasks and the stolen modifiers of Headhunter, Replica
    // Headhunter and Inspired Learning. On the tick rather than a loop of the module's own, so a reload's new module steps them from its first tick
    // and the old one stops.
    internal static async Task StepUniqueBeltEffects(Player player)
    {
        try
        {
            foreach (var packet in StepMagebloodBuffs(player))
                await Send(player, packet, "0x01c1/0x01c2 Mageblood flask buff");
            double now = AreaTime(player);
            bool rare = player.StolenMods.Step(now, player.PlayerLife == 0, out var belt, out var jewel) is not null;
            var fromMagic = Magic.TryGetValue(player, out var m) ? m : null;
            StolenMods.Buff magic = default;
            bool magicChanged = fromMagic?.Mods.Step(now, player.PlayerLife == 0, out magic, out _) is not null;
            if (!rare && !magicChanged) return;
            List<byte[]> packets = [];
            RecomputePoolsAndSpeed(player);
            if (rare)
            {
                Update(ref player.StolenModsBuff, StolenMods.BuffRow, belt);
                Update(ref player.InspiredLearningBuff, StolenMods.InspiredLearningBuffRow, jewel);
            }
            if (magicChanged) Update(ref fromMagic!.Buff, MagicBuffRow, magic);
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
