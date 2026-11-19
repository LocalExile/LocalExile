using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    sealed record RighteousFireRemoval(PlayerUpdatesState Updates, RighteousFireState Source,
        Dictionary<uint, int> Stats, double EnemyDps, double SelfDps, int Bypass);

    // Resolve the physical source while its original links and equipment are readable. Preparing a refused
    // inventory operation neither spends the fractional carries nor consumes the source's timed support history.
    static RighteousFireRemoval? CaptureSocketedRighteousFireLocked(ClientState conn, IReadOnlyList<PlayerSkillSource> sources)
    {
        if (conn.RighteousFire is not { SourceLifetime: { } lifetime } rf
            || !sources.Any(source => source.Identity == lifetime.Key)
            || conn.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(conn, updates)
            || !CurrentGameWork || InGracePeriod(conn)) return null;

        using var mutation = ProjectileMutation(conn);
        if (CurrentRighteousFireSkillLocked(conn, rf) is not { } supported) return null;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var stats = CharacterPlayerStats(conn);
        int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return new(updates, rf, stats, RighteousFireEnemyDps(conn, rf, stats, supported.Stats),
            RighteousFireSelfDps(conn, rf) * ResourceDamageTakenFactor(conn, Stat),
            Stat(IncomingResourceDefences.BypassStat));
    }

    // The successful inventory write and this settlement hold the same actor lock. A later monster, shield gain,
    // or new source cannot enter the old interval. The resource worker retains any fractional self-burn.
    static RighteousFireBurnOutput SettleSocketedRighteousFireLocked(string remote, NetworkStream stream,
        ConnectionCiphers ciphers, ClientState conn, RighteousFireRemoval fire)
    {
        var rf = fire.Source;
        var updates = fire.Updates;
        var output = new RighteousFireBurnOutput(rf);
        if (updates.RighteousFireEnemyThrough is double lastEnemy)
        {
            double end = updates.Schedule.Now(conn);
            double seconds = RighteousFireActiveSeconds(rf, end, end - lastEnemy);
            if (seconds > 0)
                BurnNearbyLocked(remote, stream, ciphers, conn, output, fire.Stats, fire.EnemyDps, end, seconds, end,
                    showAuras: false);
            updates.RighteousFireEnemyThrough = Math.Max(lastEnemy, end);
        }
        if (updates.RighteousFireSelfThrough is double lastSelf)
        {
            double end = updates.Schedule.ResourceNow(conn);
            double seconds = RighteousFireActiveSeconds(rf, end, end - lastSelf);
            if (seconds > 0)
            {
                uint life = conn.PlayerLife, shield = conn.EnergyShield.Current;
                int loss = RighteousFireLifeBurn(conn, fire.SelfDps * conn.ElementalAilments.DamageTakenFactor(end)
                    * seconds, fire.Bypass, end, ref updates.RighteousFireSelfCarry);
                conn.PlayerLife = (uint)Math.Max(1L, (long)life - loss);
                output.SelfLifeChanged = conn.PlayerLife != life;
                output.SelfShieldChanged = conn.EnergyShield.Current != shield;
            }
            updates.RighteousFireSelfThrough = Math.Max(lastSelf, end);
        }
        return output;
    }

    static double ResourceDamageTakenFactor(ClientState conn, Func<string, int> get) =>
        get("base_cannot_be_damaged") > 0 ? 0 : Math.Max(0, 1 + get(IncomingResourceDefences.TakenStat) / 100.0)
        * Math.Max(0, 1 + get(IncomingResourceDefences.ConditionalTakenStat) / 100.0)
        * Math.Max(0, 1 - IncomingResourceDefences.LessTakenPct(conn.Pools, get) / 100.0);

    static double RighteousFireSelfDps(ClientState conn, RighteousFireState rf) =>
        DamageModel.Mitigate((conn.PlayerMaxLife * (double)rf.BurnPctPerMinute
            + conn.Pools.EnergyShield * (double)rf.EnergyShieldBurnPctPerMinute) / 6000,
            Resistance(conn, DamageType.Fire), MaxResistance(conn, DamageType.Fire));

    static int RighteousFireLifeBurn(ClientState conn, double damage, int bypass, double at, ref double carry)
    {
        carry += damage;
        int whole = (int)Math.Truncate(carry);
        carry -= whole;
        return IncomingResourceDefences.DamageOverTime(conn.EnergyShield, whole, bypass, at, ref conn.NonChaosDotBypassCarry);
    }
}
