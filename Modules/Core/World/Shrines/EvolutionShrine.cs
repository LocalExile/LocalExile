using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>Evolution revives a slain eligible monster one rarity higher. Its counter belongs to the recipient and
/// area, not a buff instance: refreshing or overlapping shrine buffs cannot reset diminishing returns.</summary>
public static class EvolutionShrine
{
    // GGG publishes the initial chance and declining behavior, but not the decline coefficient. This is explicitly a
    // local model: chance = base / (1 + successes * coefficient / 10000). Exposed as module settings for calibration.
    public sealed record Settings(int EvolutionDeclinePerSuccessPermyriad = 100);
    public static Settings Policy { get; set; } = new();
    sealed class State { public int Successes; public readonly HashSet<SpawnedMonster> Seen = []; }
    static readonly ConditionalWeakTable<Player, State> States = new();
    static ushort? BuffRow;
    public static void Load() => BuffRow = ShrineCatalog.Find("UpgradeOnKill")?.BuffRow;

    public static int Chance(int basis, int successes, int coefficient) =>
        (int)Math.Clamp((long)basis * 10000 / (10000L + Math.Max(0, successes) * (long)Math.Max(1, coefficient)), 0, 10000);

    public static bool Eligible(SpawnedMonster monster) => monster is
        { Hostile: true, NoRewards: false, OwnerId: null, SummonerId: null, Variety.Supported: true,
            Rarity: MonsterRarity.Normal or MonsterRarity.Magic }
        && !monster.Variety.Id.Contains("Breach", StringComparison.OrdinalIgnoreCase)
        && !monster.Variety.Id.Contains("Daemon", StringComparison.OrdinalIgnoreCase)
        && monster.Effects[Stat.MonsterNoDropsOrExperience] == 0;

    public static void OnKill(KillContext kill)
    {
        if (BuffRow is ushort row && ShrineManager.HasBuff(kill.Player, row) && Eligible(kill.Monster))
            Upgrade(kill, row);
    }

    static void Upgrade(KillContext kill, ushort row)
    {
        var player = kill.Player;
        var state = States.GetOrCreateValue(player);
        int basis = PlayerStatValues(player).GetValueOrDefault(Stat.ChanceToUpgradeMonstersOnKillPermyriad);
        int chance, successes;
        lock (state)
        {
            if (!state.Seen.Add(kill.Monster)) return;
            chance = Chance(basis, state.Successes, Policy.EvolutionDeclinePerSuccessPermyriad);
            if (AreaRng(player).Next(10000) >= chance) return;
            successes = ++state.Successes;
        }
        var instance = player.Instance;
        var variety = kill.Monster.Variety!;
        var rarity = kill.Monster.Rarity == MonsterRarity.Normal ? MonsterRarity.Magic : MonsterRarity.Rare;
        var level = kill.Monster.Level;
        var at = kill.At;
        kill.Later(async () =>
        {
            if (!States.TryGetValue(player, out var current) || !ReferenceEquals(current, state)
                || !ReferenceEquals(player.Instance, instance) || !ShrineManager.HasBuff(player, row)) return;
            uint id = await SpawnMonster(player, variety, level, rarity, at.X, at.Y, log: false,
                unrisen: false, alone: true, placement: MonsterPlacement.Established);
            if (id != 0) Console.WriteLine($"[gateway] shrine evolution {kill.Monster.ObjectId:x} -> {id:x} {rarity}, chance {chance}/10000, successes {successes}");
        });
    }

    public static void LeaveArea(Player player) => States.Remove(player);
}
