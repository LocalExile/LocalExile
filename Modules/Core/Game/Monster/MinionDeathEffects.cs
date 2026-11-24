using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionDeathHit(GameArea Area, int Epoch, int Physical, double At);
    static readonly RuleTable<Player, Queue<MinionDeathHit>> MinionDeathHits = new();

    static void OwnedMinionDeathEffectsLocked(Player player, GolemState.Golem dead, double now)
    {
        if (dead.DeathEffectsApplied || !GolemCurrent(player, dead) || player.PlayerLife == 0) return;
        dead.DeathEffectsApplied = true;
        var stats = CharacterPlayerStats(player);
        int recovery = GolemStat(stats, Stat.MinionRecoverPctMaximumLifeOnMinionDeath);
        if (recovery > 0)
            foreach (var minion in player.Minions.All)
                if (minion.Id != dead.Id && OwnedFighter(player, minion.Id) is { } survivor)
                    RecoverMinionLifeLocked(player, survivor, (long)survivor.Monster.MaxLife * recovery / 100);

        int physical = GolemStat(stats, Stat.PhysicalDamageTakenOnMinionDeath);
        if (physical > 0)
            MinionDeathHits.GetOrCreateValue(player).Enqueue(new(player.Area, player.ProjectileEpoch, physical, now));
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
                ApplyMinionDeathHitLocked(player, hit.Physical, hit.At);
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

    static void ApplyMinionDeathHitLocked(Player player, int physical, double now)
    {
        var stats = CharacterPlayerStats(player);
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        if (player.Immune || Get(Stat.BaseCannotBeDamaged) != 0) return;
        using var defenceTime = FortificationOf(player).Capture(now);
        var shifted = PlayerDamageTaken.Shift(DamagePacket.Of(DamageType.Physical, physical, physical), Get);
        var immune = PlayerImmunities(player, stats);
        bool usesArmour = shifted.Components.Any(c => !immune.Contains(c.Type)
            && PlayerHitMitigation.ArmourPercent(player.Pools, c.Type, Get) > 0);
        var armour = IncomingArmourLocked(player, usesArmour, player.RecentEnemyHits.Count(now), stats);
        var taken = shifted.Select((type, range) =>
        {
            int damage = immune.Contains(type) ? 0 : MitigateTakenWithArmour(player, type, (int)range.Min,
                Resistance(player, type), MaxResistance(player, type), armour.EffectiveArmour, stats, attack: false);
            return new(damage, damage);
        });
        RecoupHitLocked(player, taken, now, stats);
        PreventedDamageOf(player).Add(now, shifted[DamageType.Physical].Min - taken[DamageType.Physical].Min);
        long lifeBefore = player.PlayerLife;
        int toLife = DamagePlayerHitTyped(player, (int)taken[DamageType.Physical].Min,
            ActorScale.Saturate((long)(taken.MaxTotal - taken[DamageType.Physical].Min - taken[DamageType.Chaos].Min)),
            (int)taken[DamageType.Chaos].Min, now, stats, taken);
        player.PlayerLife = (uint)Math.Max(0, lifeBefore - toLife);
        TrimLeechLocked(player);
        RecordIncomingHitLocked(player, now, spell: false, blocked: false, suppressed: false, lifeDamage: toLife, enemy: false);
        RecentHitLifeLossOnHitLocked(player, now);
        UnhingedHitRecoveryLocked(player, now, stats);
        if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
        RecordHardcoreDeath(player);
    }
}
