using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    sealed class AlchemistMarkCooldowns
    {
        public double FlaskReady, FireReady, ChaosReady;
    }

    static readonly RuleTable<Player, AlchemistMarkCooldowns> AlchemistMarkTimers = new();

    static (int Radius, double Seconds, int FireArt, int CausticArt) AlchemistGroundFor(Player player,
        IReadOnlyDictionary<uint, int> skill, IReadOnlyDictionary<uint, int> caster)
    {
        int radius = skill.GetValueOrDefault(Stat.AlchemistsMarkTotalRadius);
        if (radius <= 0)
            radius = SkillRadius(player, 22, skill, spell: true, SkillsFor(player).EffectRow("AlchemistsMark"), caster);
        double seconds = SkillDurationModifiers.Seconds(skill.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            caster.GetValueOrDefault, skill.GetValueOrDefault);
        return (radius, seconds, skill.GetValueOrDefault(Stat.AlchemistsMarkGroundFireArtVariation),
            skill.GetValueOrDefault(Stat.AlchemistsMarkGroundCausticArtVariation));
    }

    static MonsterCurses.Curse? AlchemistMarkAt(SpawnedMonster target, uint stat, double now)
    {
        if (!target.HasCurses || target.Curses[stat] <= 0) return null;
        MonsterCurses.Curse? best = null;
        int bestValue = 0;
        foreach (var mark in target.Curses.All.Values)
            if (mark.Effect == "AlchemistsMark" && mark.Until > now && mark.Stats.GetValueOrDefault(stat) is var value
                && value > bestValue)
            {
                best = mark;
                bestValue = value;
            }
        return best;
    }

    static void AlchemistChargeOnHitLocked(Player player, SpawnedMonster target, double now)
    {
        const uint stat = Stat.AlchemistsMarkGrantAttackerXFlaskChargesWhenHitOncePer3s;
        if (target is not { Hostile: true, Invulnerable: false } || AlchemistMarkAt(target, stat, now) is not { } mark) return;
        var timers = AlchemistMarkTimers.GetOrCreateValue(player);
        if (now < timers.FlaskReady) return;
        timers.FlaskReady = now + 3;
        int charges = mark.Stats.GetValueOrDefault(stat);
        ProjectilesFor(player).EnqueueLater(() => player.Closed || player.LeavingArea ? Task.CompletedTask
            : GainFlaskCharges(player, (_, _) => charges, "Alchemist's Mark"));
    }

    static void AlchemistGroundOnAilmentLocked(Player player, SpawnedMonster target, DamageType type, double now)
    {
        bool fire = type == DamageType.Fire;
        uint stat = fire ? Stat.AlchemistsMarkIgniterCreatesBurningGroundPctIgniteDamage
            : Stat.AlchemistsMarkPoisonerCreatesCausticGroundPctPoisonDamage;
        if (target is not { Hostile: true, Invulnerable: false } || AlchemistMarkAt(target, stat, now) is not { } mark
            || mark.AlchemistGround is not { Radius: > 0, Seconds: > 0 } ground) return;
        var timers = AlchemistMarkTimers.GetOrCreateValue(player);
        ref double ready = ref (fire ? ref timers.FireReady : ref timers.ChaosReady);
        if (now < ready) return;
        double strongest = fire ? target.Ignites.StrongestDamagePerSecond(now)
            : target.Poisons.Strongest(now)?.DamagePerSecond ?? 0;
        if (strongest <= 0) return;
        ready = now + 1;
        var at = target.Walk.Position(now);
        ProjectilesFor(player).PlaceDamageGround(new(at.X, at.Y), ground.Radius, ground.Seconds, type,
            strongest * mark.Stats.GetValueOrDefault(stat) / 100d, ProjectileRuntime.Time(now),
            fire ? ground.FireArt : ground.CausticArt);
    }
}
