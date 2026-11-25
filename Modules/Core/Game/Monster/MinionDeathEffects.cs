using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionDeathHit(GameArea Area, int Epoch, int Physical, double At);
    static readonly RuleTable<Player, Queue<MinionDeathHit>> MinionDeathHits = new();

    static void OwnedMinionDeathEffectsLocked(Player player, GolemState.Golem dead, double now)
    {
        if (dead.DeathEffectsApplied || !GolemCurrent(player, dead) || player.PlayerLife == 0
            || dead.Definition.Kind is ItemHarbingerFamily or "DoedresEffigy" || SpectreGroundHex(dead.Definition.Kind) is not null) return;
        dead.DeathEffectsApplied = true;
        var stats = PlayerStatValues(player);
        int recovery = GolemStat(stats, Stat.MinionRecoverPctMaximumLifeOnMinionDeath);
        if (recovery > 0)
            foreach (var minion in player.Minions.All)
                if (minion.Id != dead.Id && OwnedFighter(player, minion.Id) is { } survivor)
                    RecoverMinionLifeLocked(player, survivor, (long)survivor.Monster.MaxLife * recovery / 100);

        MinionDeathGroundLocked(player, dead, stats, now);
        int physical = GolemStat(stats, Stat.PhysicalDamageTakenOnMinionDeath);
        if (physical > 0)
            MinionDeathHits.GetOrCreateValue(player).Enqueue(new(player.Area, player.ProjectileEpoch, physical, now));
    }

    static void MinionDeathGroundLocked(Player player, GolemState.Golem dead, IReadOnlyDictionary<uint, int> stats, double now)
    {
        int chaos = GolemStat(stats, Stat.MinionCausticCloudOnDeathMaximumLifePerMinuteToDealAsChaosDamagePct);
        int fire = GolemStat(stats, Stat.MinionFireCloudOnDeathMaximumLifePerMinuteToDealAsFireDamagePct);
        if (chaos > 0) Place(DamageType.Chaos, chaos, "MonsterCausticCloudOnDeath", Stat.MonsterCausticCloudOnDeathBaseAreaOfEffectRadius);
        if (fire > 0) Place(DamageType.Fire, fire, "MonsterGroundFireOnDeath1", Stat.MonsterGroundFireOnDeathBaseAreaOfEffectRadius);

        void Place(DamageType type, int perMinute, string nativeGround, uint radiusStat)
        {
            var effects = dead.Monster.Effects;
            int Get(uint stat) => effects[stat];
            if (DamageStats.Prohibited(type, Get) || ItemsFor(player).Mods.ById(nativeGround) is not { } basis) return;
            // The belt records store damage only. The corresponding native death grounds supply the geometry and lifetime.
            int radius = basis.Stats.FirstOrDefault(stat => stat.Id == radiusStat)?.Min ?? 0;
            int duration = basis.Stats.FirstOrDefault(stat => stat.Id == Stat.MonsterGroundEffectOnDeathBaseDurationMs)?.Min ?? 0;
            double seconds = SkillDurationModifiers.Seconds(duration, _ => 0, Get);
            if (seconds <= 0 || radius <= 0) return;
            var (_, unitDps, increase) = MinionHazardDamage(player, dead, now, dead.Monster.MaxLife * perMinute / 6000d, area: true);
            bool burning = type == DamageType.Fire;
            double increased = (double)increase + Get(Stat.DamageOverTimePlusPct) +
                (burning ? Get(Stat.FireDamagePlusPct) + Get(Stat.BurnDamagePlusPct) + Get(Stat.FireDamageOverTimePlusPct)
                    : Get(Stat.ChaosDamagePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct));
            double dotMultiplier = Get(Stat.DotMultiplierPlus) + Get(burning ? Stat.FireDotMultiplierPlus : Stat.ChaosDotMultiplierPlus);
            double dps = unitDps * Math.Max(0, 1 + increased / 100) * Math.Max(0, 1 + dotMultiplier / 100)
                * Math.Max(0, 1 + Get(Stat.SupportEfficacyDamageOverTimePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Get(Stat.SupportRapidDecayDamageOverTimePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Get(burning ? Stat.SupportBurningDamagePlusPctFinal : Stat.SupportVoidManipulationChaosDamagePlusPctFinal) / 100d)
                * DoedreSewers.MinionDamageFactor(player) * MapMinionDamageFactor(player);
            if (dps <= 0) return;
            ProjectilesFor(player).PlaceDamageGround(ProjectileSession.Vector(dead.Monster.Walk.Position(now)),
                SkillArea.Radius(radius, Get, spell: true), seconds, type, dps, ProjectileRuntime.Time(now));
        }
    }

    // A minion can die while a hit aimed at its owner is still being allocated. Apply the self-hit only after
    // the enclosing contact has published its own life and resource updates.
    static async Task RunMinionDeathHits(Player player)
    {
        if (!MinionDeathHits.TryGetValue(player, out var hits)) return;
        uint life, mana;
        bool changed;
        int deathEpoch;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
            life = player.PlayerLife; mana = player.PlayerMana;
            uint shield = player.EnergyShield.Current;
            while (hits.TryDequeue(out var hit))
            {
                if (player.PlayerLife == 0 || hit.Area != player.Area || hit.Epoch != player.ProjectileEpoch) continue;
                ApplySelfHitLocked(player, DamagePacket.Of(DamageType.Physical, hit.Physical, hit.Physical), hit.At);
            }
            MinionDeathHits.Remove(player);
            changed = life != player.PlayerLife || mana != player.PlayerMana || shield != player.EnergyShield.Current;
            deathEpoch = player.IncomingHitEpoch;
            player.Frames.Area?.Life.Changed(player);
        }
        if (life > 0 && player.PlayerLife == 0)
            await PlayerDiedAtEpoch(player, "death from minion death damage", deathEpoch);
        else if (changed && player.Frames.Area is null && !player.Closed && !player.LeavingArea)
        {
            if (life != player.PlayerLife)
                await SendWorldRecord(player, player.Id, PlayerLifeUpdate(player), "life after minion death");
            await SendPlayerMana(player, "resources after minion death");
        }
    }
}
