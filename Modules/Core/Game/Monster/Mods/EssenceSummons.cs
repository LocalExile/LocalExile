using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string EssenceKaruiSummonMod = "EssenceDaemonSummonMovingKaruiSpirit1";
    public const string EssenceKaruiSpirit = "Metadata/Monsters/Ancestor/EssenceKaruiSpiritMovingAxe";
    // Contempt's three chasing spirits (PoE Wiki, Essence of Contempt). Count=1 is a cast's batch, not its living cap.
    public const int EssenceKaruiLimit = 3;
    sealed class EssenceSummonCycle(SkillCatalog.MonsterSummonInfo? summon)
    {
        public readonly SkillCatalog.MonsterSummonInfo? Summon = summon;
        public double Ready, Contact = double.NaN;
    }
    static readonly RuleTable<SpawnedMonster, EssenceSummonCycle> EssenceSummonCycles = new();
    static ushort? EssenceKaruiModHash;
    internal static void LoadEssenceSummonsData() => EssenceKaruiModHash = Items.Mods.ById(EssenceKaruiSummonMod)?.Hash;
    // EssenceMovingAncestor.ot: these are untargetable, damage-immune spirits, not ordinary killable guards.
    static readonly MonsterModSet EssenceKaruiDefences = new([], [], new MonsterModEffects(new Dictionary<uint, int>
    {
        [Stat.BaseCannotBeDamaged] = 1, [Stat.BaseCannotBeStunned] = 1, [Stat.CannotBeKnockedBack] = 1,
        [Stat.ImmuneToCurses] = 1, [Stat.ImmuneToAurasFromOtherEntities] = 1,
    }));
    public static MonsterModSet? EssenceSummonModifiers(MonsterCatalog.Variety variety) =>
        variety.Id == EssenceKaruiSpirit ? EssenceKaruiDefences : null;
    public static bool EssenceSummonCanSpawnLocked(Player player, uint parent, MonsterCatalog.Variety variety) =>
        variety.Id != EssenceKaruiSpirit || player.Area.Monsters.Values.Count(m => m.SummonerId == parent
            && m.Variety?.Id == EssenceKaruiSpirit && !m.Dead && m.Life > 0) < EssenceKaruiLimit;
    public sealed record EssenceSummonCast(uint Parent, SpawnedMonster Monster, SkillCatalog.MonsterSummonInfo Summon);

    // The daemon's skill supplies its cast time, count, cooldown and parent flags. Server-only .ais is unavailable;
    // our activation policy is one cast when an awake carrier aggroes, then its cooldown, with no catch-up burst.
    public static IReadOnlyList<EssenceSummonCast> EssenceSummonsDueLocked(Player player, double now)
    {
        List<EssenceSummonCast> due = [];
        if (player.Closed || player.LeavingArea || player.Paused || !IsMonsterRunner(player)
            || EssenceKaruiModHash is not ushort mod) return due;
        var catalog = SkillsFor(player);
        foreach (var (id, source) in player.Area.Monsters)
        {
            if (!source.Hostile || source.Dead || source.Life == 0 || source.WorldHidden
                || source.Mods.Mods.Count == 0 || !source.Mods.Mods.Any(m => m.Hash == mod)) continue;
            var cycle = EssenceSummonCycles.GetValue(source, s =>
                new(catalog.MonsterSummon(catalog.EffectRow("EssenceDaemonSummonMovingKaruiSpirit"), s.Level)));
            if (!source.Aggro || source.Asleep || source.EntranceGrace)
            { cycle.Contact = double.NaN; continue; }
            if (cycle.Summon is not { CooldownSeconds: > 0 } summon) continue;
            if (!double.IsNaN(cycle.Contact))
            {
                if (now < cycle.Contact) continue;
                cycle.Contact = double.NaN;
                if (Monsters.Find(summon.Variety.ToString()) is not { } variety
                    || !EssenceSummonCanSpawnLocked(player, id, variety)) continue;
                due.Add(new(id, source, summon));
                continue;
            }
            if (now < cycle.Ready) continue;
            cycle.Contact = now + summon.CastSeconds;
            cycle.Ready = now + Math.Max(summon.CastSeconds, summon.CooldownSeconds);
        }
        return due;
    }

    public static async Task TickEssenceSummons(Player player, double now)
    {
        int ended = TickMonsterSummonParentsLocked(player, now);
        IReadOnlyList<EssenceSummonCast> due = EssenceSummonsDueLocked(player, now);
        if (ended > 0) await SendRemainingMonsters(player);
        foreach (var cast in due)
        {
            if (!IsMonsterRunner(player) || !MonsterSummonerCurrentLocked(player, cast.Parent, cast.Monster)) continue;
            await MonsterSummon(player, cast.Parent, cast.Monster, cast.Summon);
        }
    }
}
