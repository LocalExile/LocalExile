using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string EssenceKaruiSummonMod = "EssenceDaemonSummonMovingKaruiSpirit1";
    public const string EssenceKaruiSpirit = "Metadata/Monsters/Ancestor/EssenceKaruiSpiritMovingAxe";
    sealed class EssenceSummonCycle(SkillCatalog.MonsterSummonInfo? summon)
    {
        public readonly SkillCatalog.MonsterSummonInfo? Summon = summon;
        public double Ready, Contact = double.NaN;
    }
    static readonly RuleTable<SpawnedMonster, EssenceSummonCycle> EssenceSummonCycles = new();
    static readonly Lazy<ushort?> EssenceKaruiModHash = new(() => Items.Mods.ById(EssenceKaruiSummonMod)?.Hash);
    // EssenceMovingAncestor.ot: these are untargetable, damage-immune spirits, not ordinary killable guards.
    static readonly MonsterModSet EssenceKaruiDefences = new([], [], new MonsterModEffects(new Dictionary<string, int>
    {
        ["base_cannot_be_damaged"] = 1, ["base_cannot_be_stunned"] = 1, ["cannot_be_knocked_back"] = 1,
        ["immune_to_curses"] = 1, ["immune_to_auras_from_other_entities"] = 1,
    }));
    public static MonsterModSet? EssenceSummonModifiers(MonsterCatalog.Variety variety) =>
        variety.Id == EssenceKaruiSpirit ? EssenceKaruiDefences : null;
    public sealed record EssenceSummonCast(uint Parent, SpawnedMonster Monster, SkillCatalog.MonsterSummonInfo Summon);

    // The daemon's skill supplies its cast time, count, cooldown and parent flags. Server-only .ais is unavailable;
    // our activation policy is one cast when an awake carrier aggroes, then its cooldown, with no catch-up burst.
    public static IReadOnlyList<EssenceSummonCast> EssenceSummonsDueLocked(ClientState conn, double now)
    {
        List<EssenceSummonCast> due = [];
        if (conn.Closed || conn.LeavingArea || conn.Paused || !IsMonsterRunner(conn)) return due;
        var catalog = SkillsFor(conn);
        foreach (var (id, source) in conn.Monsters)
        {
            if (!source.Hostile || source.Dead || source.Life == 0 || source.WorldHidden
                || EssenceKaruiModHash.Value is not ushort mod || !source.Mods.Mods.Any(m => m.Hash == mod)) continue;
            var cycle = EssenceSummonCycles.GetValue(source, s =>
                new(catalog.MonsterSummon(catalog.EffectRow("EssenceDaemonSummonMovingKaruiSpirit"), s.Level)));
            if (!source.Aggro || source.Asleep || source.EntranceGrace)
            { cycle.Contact = double.NaN; continue; }
            if (cycle.Summon is not { CooldownSeconds: > 0 } summon) continue;
            if (!double.IsNaN(cycle.Contact))
            {
                if (now < cycle.Contact) continue;
                cycle.Contact = double.NaN;
                due.Add(new(id, source, summon));
                continue;
            }
            if (now < cycle.Ready) continue;
            cycle.Contact = now + summon.CastSeconds;
            cycle.Ready = now + Math.Max(summon.CastSeconds, summon.CooldownSeconds);
        }
        return due;
    }

    public static async Task TickEssenceSummons(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now)
    {
        IReadOnlyList<EssenceSummonCast> due;
        int ended;
        lock (conn.Monsters)
        {
            ended = TickMonsterSummonParentsLocked(conn, now);
            due = EssenceSummonsDueLocked(conn, now);
        }
        if (ended > 0) await SendRemainingMonsters(remote, stream, ciphers, conn);
        foreach (var cast in due)
        {
            lock (conn.Monsters)
                if (!IsMonsterRunner(conn) || !MonsterSummonerCurrentLocked(conn, cast.Parent, cast.Monster)) continue;
            await MonsterSummon(remote, stream, ciphers, conn, cast.Parent, cast.Monster, cast.Summon);
        }
    }
}
