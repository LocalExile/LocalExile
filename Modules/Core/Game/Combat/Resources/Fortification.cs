using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static readonly Store<Player, FortificationSession> FortificationSessions = new();
    public static readonly ConcurrentDictionary<uint, (DateTime At, FortificationState.Transfer State)> CarriedFortification = new();
    public static FortificationSession FortificationOf(Player player) =>
        FortificationSessions.GetValue(player, c => new()
        {
            Changed = () => MarkPlayerStats(c, PlayerStatStep.TimedRecovery),
        });

    public static void EnsureFortificationPools(Player player)
    {
        var session = FortificationOf(player);
        session.Read(AreaTime(player));
        if (player.Pools.FortificationRevision != session.Revision) player.Pools = PoolsFor(player, player.Pools.Level);
    }

    public static decimal RawFortificationDamage(DamagePacket rolled, bool critical, int multiplier)
    {
        decimal total = rolled.Components.Sum(c => (decimal)c.Range.Min);
        return Math.Max(0, total) * (critical ? Math.Max(0, multiplier) / 100m : 1m);
    }

    public static Dictionary<uint, int> WithFortification(Player player, Dictionary<uint, int> sources)
    {
        int Get(uint key) => sources.GetValueOrDefault(key);
        var session = FortificationOf(player);
        if (player.PlayerLife == 0 && session.Value.Uncapped > 0) session.Clear();
        var value = session.Observe(AreaTime(player), Get);
        void Add(uint name, int added)
        {
            if (name is var key)
                sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + added, int.MinValue, int.MaxValue);
        }
        Add(FortificationSession.AttackSpeed, value.IncreasedAttackSpeedPercent(session.SpeedPerStack));
        AddFortificationCounts(new PlainStatMap(sources), value);
        return FortificationAllyPlayerStats(player, sources);
    }

    public static void AddFortificationCounts(StatMap stats, FortificationState.Value value)
    {
        foreach (var (name, amount) in new[] { (FortificationSession.Current, value.Current),
            (FortificationSession.Uncapped, (int)Math.Min(int.MaxValue, value.Uncapped)),
            (FortificationSession.Maximum, value.Maximum),
            (Stat.HasFortification, value.Current > 0 ? 1 : 0) })
            if (name is var key) stats[key] = amount;
    }

    // Called only at an accepted melee damage commit, before any notification can yield to another action/area.
    public static bool FortificationOnHitLocked(Player player, SpawnedMonster target, decimal rawDamage,
        bool melee, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill = null, Random? random = null)
    {
        if (!melee || !target.Hostile || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return false;
        int Get(uint name)
        {
            if (!(name is var key)) return 0;
            return (int)Math.Clamp((long)playerStats.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0),
                int.MinValue, int.MaxValue);
        }
        return FortificationOf(player).Hit(AreaTime(player), rawDamage, target.MaxLife,
            target.Rarity, Get, random ?? AreaRng(player));
    }

    /// <summary>A successful melee stun (HitReaction.TryFlinch): roll the character's chance to Fortify and grant the
    /// hit's damage-based stacks. Called under the owner's Monsters lock.</summary>
    internal static bool FortificationFromMeleeStun(Player player, decimal rawDamage, decimal maximumLife,
        MonsterRarity rarity, Random? random = null)
    {
        if (player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return false;
        var playerStats = CharacterPlayerStats(player);
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        return FortificationOf(player).FortifyFromStun(AreaTime(player), rawDamage, maximumLife, rarity,
            Get, random ?? AreaRng(player));
    }

    public static int FortificationDamageTaken(Player player, int damage) =>
        FortificationDamageTakenLocked(player, Math.Max(0, damage), AreaTime(player));

    public static void ConfigureFortificationRecovery(Player player, uint maximumLife,
        IReadOnlyDictionary<uint, int> sources)
    {
        var recovery = new Dictionary<uint, int>(sources);
        AddItemUtilityFlaskRawStats(new PlainStatMap(recovery),
            player.UtilityFlaskEffects.Stats().Concat(player.FlaskGrounds.PlayerStats()), Stats);
        if (MapModsOf(player) is { } map)
        {
            recovery[Stat.MapPlayersNoRegenerationIncludingEs] = map.Get(Stat.MapPlayersNoRegenerationIncludingEs);
            recovery[Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal] = map.Get(Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal);
        }
        ConfigureFortificationRecovery(player, maximumLife,
            name => name is var key ? recovery.GetValueOrDefault(key) : 0);
    }

    public static void ConfigureFortificationRecovery(Player player, uint maximumLife, Func<uint, int> get)
    {
        FortificationOf(player).ConfigureRecovery(AreaTime(player), maximumLife,
            false,
            PanelStats.LifeRegenerationMultiplier(get));
    }

    public static void CarryFortification(Player player, uint ticket)
    {
        var session = FortificationOf(player);
        if (player.PlayerLife > 0)
        {
            var transfer = session.State.Carry(AreaTime(player));
            if (transfer.Grants.Count > 0) CarriedFortification[ticket] = (DateTime.UtcNow, transfer);
        }
        session.Clear();
        foreach (var (old, entry) in CarriedFortification)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedFortification.TryRemove(old, out _);
    }

    public static void RestoreFortification(Player player)
    {
        if (CarriedFortification.TryRemove(player.Ticket, out var entry)
            && DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && player.PlayerLife > 0)
            FortificationOf(player).Restore(entry.State, AreaTime(player));
    }

    // Construct buffs, stats and Actor records together inside the send gate from the current owner state. A stat
    // presentation: without Fortification shown and with nothing changed since the last presentation (every gain moves
    // the revision) there is nothing to send, so the send gate and the area's frame are left alone.
    public static Task SendFortification(Player player) =>
        FortificationOf(player) is { Buff: 0 } idle && idle.PresentedRevision == idle.Revision && !HasGuardianOrPeerFortificationAura(player) ? Task.CompletedTask : Send(player, [], null, atGate: () =>
        {
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired || player.Paused) return null;
                var session = FortificationOf(player);
                double now = AreaTime(player);
                using var snapshot = session.Capture(now);
                if (player.PlayerLife == 0) { if (session.Value.Uncapped > 0) session.Clear(); }
                session.Read(now);
                bool permanentDirty = FortificationPresentationDirtyLocked(player, session, now);
                if (session.PresentedRevision == session.Revision && !permanentDirty) return null;
                ReconcileOwnAuras(player);
                player.Pools = PoolsFor(player, player.Pools.Level);
                return WithOwnAuraChanges(player, () =>
                {
                    // Build and acknowledge the tail AFTER any aura prefix, in the same order as its wire packets.
                    var value = EvaluateFortificationLocked(player, now).Value;
                    var packets = new List<byte[]>();
                    session.Buff = (ushort)(value.Current > 0 ? 1 : 0);
                    if (value.Current > 0)
                        player.Area.Buffs.Set(player.Id, new(FortificationBuffRow, player.Id,
                            Visual: BuffArtVisual(player, "fortify_buff", Stat.VirtualFortifyArtVariation),
                            U68: value.LatestExpiry is double expiry ? Buff.Seconds((expiry - now) * session.State.ExpiryRate) : null,
                            Stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
                    else player.Area.Buffs.Clear(player.Id, FortificationBuffRow, player.Id);
                    // Fortification changes on every melee hit; rebuilding the whole skill list here held the
                    // Monsters lock and the send gate for 50-110 ms (2026-11-05), starving input and the other sends.
                    // Its stacks and attack speed are in the 0x1b3 stats; the server keeps the authoritative action
                    // timings, so the skill records are left to the next gem/equipment refresh.
                    packets.Add(PlayerStatsOffFrames(player));
                    session.PresentedRevision = session.Revision;
                    MarkFortificationPresentedLocked(player, session, now);
                    return packets.SelectMany(p => p).ToArray();
                });
            }
        });
}
