using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static SummonCatalog.Definition? HitSummonDefinition(string? effect, MonsterRarity rarity = MonsterRarity.Normal) => effect switch
    {
        "Absolution" or "AbsolutionAltX" => new(effect, "Absolution", "Metadata/Monsters/TemplarJudge/AbsolutionTemplarJudge",
            Stat.BaseMaxNumberOfAbsolutionSentinels, 0),
        "DominatingBlow" or "DominatingBlowAltX" => new(effect, "Dominating" + rarity,
            "Metadata/Monsters/Axis/AxisEliteSoldierDominatingBlow", Stat.Of("base_max_number_of_dominated_" + rarity.ToString().ToLowerInvariant() + "_monsters"), 0),
        _ => null,
    };

    internal static void HitSummonsOnHitLocked(Player player, SpawnedMonster monster,
        IReadOnlyDictionary<uint, int>? skill, ushort source, double now, Random random)
    {
        if (skill is null || GolemStat(skill, Stat.BaseMaxNumberOfAbsolutionSentinels) + GolemStat(skill, Stat.BaseMaxNumberOfDominatedNormalMonsters) <= 0
            || !monster.Hostile || player.PlayerLife == 0) return;
        var gem = SupportedSkill(player, source);
        var definition = HitSummonDefinition(SkillsFor(player).EffectId(gem.Effect));
        if (definition is null || !gem.RequirementsMet) return;
        if (definition.Family.StartsWith("Dominating", StringComparison.Ordinal)
            && (monster.Effects[Stat.CannotBeDominated] > 0 || monster.Effects[Stat.CannotBeConvertedOrDominated] > 0)) return;
        var pair = player.Area.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, monster));
        if (pair.Value is null) return;
        if (monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique && random.Next(100)
            < GolemStat(skill, definition.Family == "Absolution" ? Stat.AbsolutionBlastChanceToSummonOnHittingRareOrUniquePct : Stat.DominatingBlowChanceToSummonOnHittingUnqiuePct))
            player.Summons.HitSummons.Add(new(pair.Key, monster, source, gem.Effect, gem.SourceGem, false));
        if (monster.Rarity == MonsterRarity.Unique) return;
        double duration = SkillDurationModifiers.Seconds(GolemStat(skill, Stat.BaseSkillEffectDuration),
            n => GolemStat(CharacterPlayerStats(player), n), n => GolemStat(skill, n));
        player.Summons.HitMarks[(pair.Key, source)] = new(monster, source, gem.Effect, gem.SourceGem, now + duration);
        if (monster.Dead || monster.Life == 0) HitSummonsOnDeathLocked(player, pair.Key, monster, now);
    }

    static void HitSummonsOnDeathLocked(Player player, uint id, SpawnedMonster monster, double now)
    {
        QueueMarkedDeath(player, id, monster, now);
        foreach (var peer in player.Instance.PlayersExcept(player.Id))
            if (peer.Owner is { Closed: false, LeavingArea: false } other && ReferenceEquals(other.Instance, player.Instance))
                QueueMarkedDeath(other, id, monster, now);
    }

    static void QueueMarkedDeath(Player player, uint id, SpawnedMonster monster, double now)
    {
        foreach (var (key, mark) in player.Summons.HitMarks.Where(p => p.Key.Id == id).ToArray())
        {
            player.Summons.HitMarks.Remove(key);
            if (!ReferenceEquals(mark.Monster, monster) || now >= mark.Until || monster.Rarity == MonsterRarity.Unique) continue;
            player.Summons.HitSummons.Add(new(id, monster, mark.Source, mark.Effect, mark.Gem, true));
        }
    }

    internal static async Task HitSummonsTick(Player player, double now)
    {
        SummonedMinions.HitSummon[] pending;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
        foreach (var (key, mark) in player.Summons.HitMarks.ToArray())
            if (now >= mark.Until || player.Area.Monsters.GetValueOrDefault(key.Id) != mark.Monster || player.PlayerLife == 0)
                player.Summons.HitMarks.Remove(key);
        pending = [.. player.Summons.HitSummons]; player.Summons.HitSummons.Clear();
        foreach (var request in pending)
        {
            var gem = SupportedSkill(player, request.Source);
            var definition = request.Kind == "Phantasm" ? PhantasmDefinition(SkillsFor(player).EffectId(gem.Effect) ?? "")
                : HitSummonDefinition(SkillsFor(player).EffectId(gem.Effect), request.ConsumeCorpse ? request.Monster.Rarity : MonsterRarity.Normal);
            bool Current() => CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0
                && player.Area.Monsters.GetValueOrDefault(request.Target) == request.Monster
                && (!request.ConsumeCorpse || request.Monster.Dead && !request.Monster.CorpseConsumed && !request.Monster.Ailments.Shattered)
                && SameTriggerSource(gem, SupportedSkill(player, request.Source))
                && (request.Kind != "Phantasm" || request.Supports == SummonSupportStamp(gem));
            if (definition is null || gem.Effect != request.Effect || !(gem.SourceGem is not null && request.Gem is not null
                    && ReferenceEquals(PlayerInventories.GemIdentity(gem.SourceGem), PlayerInventories.GemIdentity(request.Gem)))
                || !gem.RequirementsMet) continue;
            if (!Current()) continue;
            int limit = SummonLimit(player, definition, gem);
            if (limit <= 0) continue;
            var alive = player.Summons.Fighters.Values.Where(g => g.Definition.Kind == definition.Family && !g.Monster.Dead && g.Monster.Life > 0).ToArray();
            if (alive.Length >= limit && definition.Family == "Absolution")
            {
                var refresh = alive.MinBy(g => player.Summons.Until[g.Id])!;
                player.Summons.Until[refresh.Id] = now + SummonDuration(player, definition, gem);
                foreach (var minion in player.Minions.All.Where(m => m.Id == refresh.Id)) minion.Until = player.Summons.Until[refresh.Id];
                refresh.Monster.Life = refresh.Monster.MaxLife;
                player.Minions.OutputQueue.Add(new([], "Sentinel refreshed", refresh.Id, refresh.Monster, PlayerMinions.OutputKind.Life));
                if (request.ConsumeCorpse) ConsumeHitSummonCorpseLocked(player, request);
                continue;
            }
            uint id = await SummonFighterCore(player, request.Source, definition,
                request.Monster.Walk.Position(now), current: Current, triggered: true,
                consumedCorpse: request.ConsumeCorpse ? request.Monster : null,
                inheritedMods: definition.Family.StartsWith("Dominating", StringComparison.Ordinal) && request.ConsumeCorpse
                    ? MonsterModRolls.Create(request.Monster.Mods.Affixes.ToArray(), request.Monster.Mods.NameWords,
                        request.Monster.Rarity, Math.Clamp(SkillsFor(player).PlayerLevelReq(gem.Effect, gem.Level), 1, 100)) : null,
                rarity: definition.Family.StartsWith("Dominating", StringComparison.Ordinal) && request.ConsumeCorpse
                    ? request.Monster.Rarity : MonsterRarity.Normal);
            if (id != 0 && request.ConsumeCorpse)
                ConsumeHitSummonCorpseLocked(player, request);
        }
    }

    static void ConsumeHitSummonCorpseLocked(Player player, SummonedMinions.HitSummon request)
    {
        ConsumeSummonCorpseLocked(player, request.Monster);
        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(request.Target), "Sentinel corpse consumed",
            WorldId: request.Target, RemovesObject: true));
    }
}
