using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort PestilentStrikeMarkRow = 1407;
    const ushort PestilentStrikeDamageRow = 1408;
    const int PestilentStrikeRadius = 27;
    const string PestilentStrikeNova = "Metadata/Effects/Spells/mamba_strike/mamba_nova.ao";

    sealed record PestilentMark(Player Caster, double Until, double Seconds, int Radius, int PoisonPercentPerMinute);
    static readonly RuleTable<SpawnedMonster, PestilentMark> PestilentMarks = new();
    static readonly RuleTable<GameArea, Dictionary<uint, AreaChaosDot>> PestilentDots = new();

    static bool IsPestilentStrike(string? effect) =>
        effect is "PestilentStrike" or "PestilentStrikeAltX" or "PestilentStrikeAltY";

    static bool HasPestilentDamage(GameArea area) => PestilentDots.TryGetValue(area, out var dots) && dots.Count > 0;

    static void MarkPestilentStrike(Player player, uint id, SpawnedMonster monster, int effect,
        IReadOnlyDictionary<uint, int> skill, IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        int SkillStat(uint stat) => skill.GetValueOrDefault(stat);
        int PlayerStat(uint stat) => playerStats.GetValueOrDefault(stat);
        int percent = SkillStat(Stat.MambaStrikeDealPctOfAllPoisonTotalDamagePerMinute);
        double markTime = SkillDurationModifiers.Seconds(SkillStat(Stat.BaseSkillEffectDuration), PlayerStat, SkillStat);
        double damageTime = SkillDurationModifiers.Seconds(SkillStat(Stat.BaseSecondarySkillEffectDuration), PlayerStat,
            SkillStat, SkillDurationModifiers.Kinds.Secondary);
        if (percent <= 0 || markTime <= 0 || damageTime <= 0) return;
        if (PestilentMarks.TryGetValue(monster, out var previous) && previous.Caster != player)
            player.Area.Buffs.Clear(id, PestilentStrikeMarkRow, previous.Caster.Id);
        int radius = SkillRadius(player, PestilentStrikeRadius, skill, spell: false, effect, playerStats);
        double rate = monster.RecipientDebuffRate;
        PestilentMarks.AddOrUpdate(monster, new(player, now + markTime / rate, damageTime, radius, percent));
        player.Area.Buffs.Set(id, new(PestilentStrikeMarkRow, player.Id, U68: Buff.Seconds(markTime), F6c: (float)rate));
    }

    static void RetimePestilentStrike(Player player, uint id, SpawnedMonster monster, double now, double oldRate, double rate)
    {
        if (PestilentMarks.TryGetValue(monster, out var mark) && mark.Until > now)
        {
            double until = RecipientEffectTime.RetimedDeadline(mark.Until, now, oldRate, rate);
            PestilentMarks.AddOrUpdate(monster, mark with { Until = until });
            player.Area.Buffs.Set(id, new(PestilentStrikeMarkRow, mark.Caster.Id,
                U68: Buff.Seconds((until - now) * rate), F6c: (float)rate));
        }
        if (PestilentDots.TryGetValue(player.Area, out var dots) && dots.TryGetValue(id, out var dot)
            && ReferenceEquals(monster, dot.Monster))
        {
            dot.Damage.RetimeExpiry(now, oldRate, rate);
            PresentPestilentDamage(player, id, monster, dot.Damage, now);
        }
    }

    static void SpreadPestilentStrike(Player player, uint id, SpawnedMonster monster, double now)
    {
        if (!PestilentMarks.TryGetValue(monster, out _) || monster.Life != 0) return;
        using var mutation = ProjectileMutation(player, advance: false);
        if (!PestilentMarks.TryGetValue(monster, out var mark) || !PestilentMarks.Remove(monster)) return;
        var area = player.Area;
        area.Buffs.Clear(id, PestilentStrikeMarkRow, mark.Caster.Id);
        if (monster.Poisons.RemainingAtDeath(now) is not { Remaining: > 0 } death || death.At >= mark.Until)
            return;
        double dps = death.Remaining * mark.PoisonPercentPerMinute / 6000d;
        var at = monster.Walk.Position(death.At);
        var cast = new MonsterContagion.Cast(dps, mark.Seconds, mark.Radius, Caster: mark.Caster);
        var dots = PestilentDots.GetOrCreateValue(area);
        foreach (var (targetId, target) in area.MonstersNear(at, mark.Radius))
        {
            if (target.Dead || target.Life == 0 || !target.Hostile || target.OwnerId is not null
                || target.Invulnerable || target.Effects[Stat.BaseCannotBeDamaged] != 0
                || Dist(target.Walk.Position(death.At), at) > mark.Radius) continue;
            if (!dots.TryGetValue(targetId, out var dot) || !ReferenceEquals(dot.Monster, target))
                dots[targetId] = dot = new(target, new());
            dot.Damage.Apply(death.At, cast with { Seconds = mark.Seconds / target.RecipientDebuffRate }, trackContagion: false);
            PresentPestilentDamage(player, targetId, target, dot.Damage, now);
        }
        var session = ProjectilesFor(player);
        if (!monster.CorpseConsumed && area.Monsters.GetValueOrDefault(id) == monster)
        {
            monster.CorpseConsumed = true;
            area.Monsters.Remove(id);
            session.WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], "Pestilent Strike consumes corpse", remove: true);
        }
        session.EnqueueLater(() => ShowContagionSpread(player, at, mark.Radius, PestilentStrikeNova, "Pestilent Strike"));
    }

    static void PresentPestilentDamage(Player player, uint id, SpawnedMonster monster, MonsterContagion dot, double now)
    {
        if (monster.Dead || monster.Life == 0 || dot.ApplicationAt(now) is not { } active)
        {
            EndDot(player, id, ref dot.Buff, dot.BuffSource);
            return;
        }
        double rate = monster.RecipientDebuffRate;
        ushort row = PestilentStrikeDamageRow;
        uint source = active.Cast.Caster?.Id ?? 0;
        uint perMinute = (uint)Math.Clamp(Math.Round(active.Cast.DamagePerSecond * 60), 0, int.MaxValue);
        ShowDot(player, id, ref dot.Buff, ref dot.BuffSource, new(row, source, Values: [perMinute],
            U68: Buff.Seconds((active.Until - now) * rate), F6c: (float)rate));
    }
}
