using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    sealed record RighteousFireRemoval(PlayerUpdatesState Updates, RighteousFireState Source, Item? SocketedGem,
        Dictionary<uint, int> Stats, double EnemyDps, double SelfDps, int Bypass);

    // Resolve the physical source while its original links and equipment are readable. Preparing a refused
    // inventory operation neither spends the fractional carries nor consumes the source's timed support history.
    static RighteousFireRemoval? CaptureSocketedRighteousFireLocked(ClientState conn, IReadOnlyList<PlayerSkillSource> sources)
    {
        return conn.RighteousFire is { SourceLifetime: { } lifetime } rf
            && sources.Any(source => source.Identity == lifetime.Key) ? CaptureRighteousFireRemovalLocked(conn, rf) : null;
    }

    static RighteousFireRemoval? CaptureEquippedRighteousFireLocked(ClientState conn, ReadOnlySpan<byte> affectedRows) =>
        conn.RighteousFire is { SourceLifetime.Key.Source: GemSkillIds.SourceKind.SocketedGem or GemSkillIds.SourceKind.WornGrant } rf
            && affectedRows.ContainsAny(ItemModStats.EquipmentRows) ? CaptureRighteousFireRemovalLocked(conn, rf) : null;

    // Any active equipment row can supply attributes to the host or its gem. The new qualification
    // uses one equipment snapshot before the old life, shield and timed support profile are recomputed.
    static bool EquippedRighteousFireQualifiesLocked(ClientState conn, RighteousFireRemoval fire)
    {
        uint host = fire.Source.SourceLifetime!.Key.Host;
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        if (!equipment.Any(entry => entry.Item.Id == host)) return false;
        var requirements = CurrentGemRequirements(conn, equipment);
        return !requirements.Inactive.Contains(host) && GemRequirementsUnmetAt(conn, fire.SocketedGem, requirements) is null;
    }

    static RighteousFireRemoval? CaptureRighteousFireRemovalLocked(ClientState conn, RighteousFireState rf)
    {
        if (conn.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(conn, updates)
            || !CurrentGameWork || InGracePeriod(conn)) return null;

        using var mutation = ProjectileMutation(conn);
        if (CurrentRighteousFireSkillLocked(conn, rf) is not { } supported) return null;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var stats = CharacterPlayerStats(conn);
        int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return new(updates, rf, supported.SourceGem, stats, RighteousFireEnemyDps(conn, rf, stats, supported.Stats),
            RighteousFireSelfDps(conn, rf) * ResourceDamageTakenFactor(conn, Stat),
            Stat(IncomingResourceDefences.BypassStat));
    }

    // The successful inventory write and this settlement hold the same actor lock. A later monster, shield gain,
    // or new source cannot enter the old interval. The resource worker retains any fractional self-burn.
    static RighteousFireBurnOutput SettleRighteousFireRemovalLocked(string remote, NetworkStream stream,
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
        get("base_cannot_be_damaged") > 0 ? 0 : IncomingResourceDefences.FinalTakenFactor(conn.Pools, get);

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
