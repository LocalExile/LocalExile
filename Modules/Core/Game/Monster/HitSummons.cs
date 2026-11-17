using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static SummonCatalog.Definition? HitSummonDefinition(string? effect) => effect is "Absolution" or "AbsolutionAltX"
        ? new(effect, "Absolution", "Metadata/Monsters/TemplarJudge/AbsolutionTemplarJudge",
            "base_max_number_of_absolution_sentinels", "") : null;

    internal static void AbsolutionOnHitLocked(ClientState conn, SpawnedMonster monster,
        IReadOnlyDictionary<uint, int>? skill, ushort source, double now, Random random)
    {
        if (skill is null || GolemStat(skill, "base_max_number_of_absolution_sentinels") <= 0
            || !monster.Hostile || conn.PlayerLife == 0) return;
        var gem = SupportedSkill(conn, source);
        if (HitSummonDefinition(SkillsFor(conn).EffectId(gem.Effect)) is null || !gem.RequirementsMet) return;
        var pair = conn.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, monster));
        if (pair.Value is null) return;
        if (monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique && random.Next(100)
            < GolemStat(skill, "absolution_blast_chance_to_summon_on_hitting_rare_or_unique_%"))
            conn.Summons.HitSummons.Add(new(pair.Key, monster, source, gem.Effect, gem.SourceGem, false));
        if (monster.Rarity == MonsterRarity.Unique) return;
        double duration = SkillDurationModifiers.Seconds(GolemStat(skill, "base_skill_effect_duration"),
            n => GolemStat(CharacterPlayerStats(conn), n), n => GolemStat(skill, n));
        conn.Summons.HitMarks[(pair.Key, source)] = new(monster, source, gem.Effect, gem.SourceGem, now + duration);
        if (monster.Dead || monster.Life == 0) HitSummonsOnDeathLocked(conn, pair.Key, monster, now);
    }

    static void HitSummonsOnDeathLocked(ClientState conn, uint id, SpawnedMonster monster, double now)
    {
        foreach (var (key, mark) in conn.Summons.HitMarks.Where(p => p.Key.Id == id).ToArray())
        {
            conn.Summons.HitMarks.Remove(key);
            if (!ReferenceEquals(mark.Monster, monster) || now >= mark.Until || monster.Rarity == MonsterRarity.Unique) continue;
            conn.Summons.HitSummons.Add(new(id, monster, mark.Source, mark.Effect, mark.Gem, true));
        }
    }

    internal static async Task HitSummonsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now)
    {
        SummonedMinions.HitSummon[] pending;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return;
            foreach (var (key, mark) in conn.Summons.HitMarks.ToArray())
                if (now >= mark.Until || conn.Monsters.GetValueOrDefault(key.Id) != mark.Monster || conn.PlayerLife == 0)
                    conn.Summons.HitMarks.Remove(key);
            pending = [.. conn.Summons.HitSummons]; conn.Summons.HitSummons.Clear();
        }
        foreach (var request in pending)
        {
            var gem = SupportedSkill(conn, request.Source);
            var definition = HitSummonDefinition(SkillsFor(conn).EffectId(gem.Effect));
            bool Current() => CurrentGameWork && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
                && conn.Monsters.GetValueOrDefault(request.Target) == request.Monster
                && (!request.ConsumeCorpse || request.Monster.Dead && !request.Monster.CorpseConsumed && !request.Monster.Ailments.Shattered)
                && SameTriggerSource(gem, SupportedSkill(conn, request.Source));
            if (definition is null || gem.Effect != request.Effect || !ReferenceEquals(gem.SourceGem, request.Gem)
                || !gem.RequirementsMet) continue;
            lock (conn.Monsters)
            {
                if (!Current()) continue;
                int limit = SummonLimit(conn, definition, gem);
                if (limit <= 0) continue;
                var alive = conn.Summons.Fighters.Values.Where(g => g.Definition.Kind == definition.Family && !g.Monster.Dead && g.Monster.Life > 0).ToArray();
                if (alive.Length >= limit)
                {
                    var refresh = alive.MinBy(g => conn.Summons.Until[g.Id])!;
                    conn.Summons.Until[refresh.Id] = now + SummonDuration(conn, definition, gem);
                    foreach (var minion in conn.Minions.All.Where(m => m.Id == refresh.Id)) minion.Until = conn.Summons.Until[refresh.Id];
                    refresh.Monster.Life = refresh.Monster.MaxLife;
                    conn.Minions.OutputQueue.Add(new([], "Sentinel refreshed", refresh.Id, refresh.Monster, PlayerMinions.OutputKind.Life));
                    if (request.ConsumeCorpse) ConsumeHitSummonCorpseLocked(conn, request);
                    continue;
                }
            }
            uint id = await SummonFighterCore(remote, stream, ciphers, conn, request.Source, definition,
                request.Monster.Walk.Position(now), current: Current, triggered: true,
                consumedCorpse: request.ConsumeCorpse ? request.Monster : null);
            if (id != 0 && request.ConsumeCorpse)
                lock (conn.Monsters) ConsumeHitSummonCorpseLocked(conn, request);
        }
    }

    static void ConsumeHitSummonCorpseLocked(ClientState conn, SummonedMinions.HitSummon request)
    {
        ConsumeSummonCorpseLocked(conn, request.Monster);
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(request.Target), "Sentinel corpse consumed",
            WorldId: request.Target, RemovesObject: true));
    }
}
