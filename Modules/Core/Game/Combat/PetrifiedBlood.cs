using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Petrified Blood is a reservation buff, not an aura: reservation and the received buff are its own, and the
    // own-aura restriction does not touch it. Its hit prevention lives in PetrifiedBloodState (Gateway Combat).
    public static async Task<bool> TogglePetrifiedBlood(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        bool started;
        lock (conn.Monsters)
        {
            // Locked commit backstop: a source may have changed after dispatch/cooldown preflight.
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InTown(conn)
                || CastingSpell(conn, effect) && SpellsDisabled(conn)) return false;
            if (conn.PetrifiedBlood.Active)
            {
                conn.PetrifiedBlood.Suspend();
                started = false;
            }
            else
            {
                var stats = PetrifiedBloodEffectStats(conn, effect, level, quality);
                var context = ReservationContext(conn, skillId);
                if (!ReservationBudgetFits(conn, reservation.Percent, context.Life, context.Efficiency))
                {
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: insufficient unreserved {(context.Life ? "life" : "mana")}");
                    return false;
                }
                conn.PetrifiedBlood.Activate(skillId, effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
                    Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
                    reservation.Percent, LowLifeThresholdPct(CharacterPlayerStats(conn)), conn.NextBuff++,
                    conn.Clock.Elapsed.TotalSeconds, context.Life, context.Efficiency);
                ClampReservedResources(conn);
                started = true;
            }
            // The client's own Life step reads life_recovery_per_minute; the full stat send below carries 0 while the
            // new buff blocks recovery and the real rate when it does not.
            conn.AuraStatsDirty = true;
            CharacterSaves.SetPetrifiedBlood(CharacterName(conn), started);
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: {(started ? "on" : "off")}, " +
                $"Low Life threshold {conn.PetrifiedBlood.LowLifeThresholdPct}%");
        }
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved (Petrified Blood {(started ? "on" : "off")})");
        return true;
    }

    // Saved intent restores the buff once its gem is socketed and enabled, outside town, after the auras so the
    // combined reservation is checked against the same maximum.
    public static void RestoreRememberedPetrifiedBlood(ClientState conn)
    {
        if (conn.PetrifiedBlood.Active || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
            || InTown(conn) || CharacterSaves.Load(CharacterName(conn))?.PetrifiedBlood != true
            || SpellsDisabled(conn)) return;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => SkillsFor(conn).EffectId(g.Effect) == PetrifiedBlood.EffectId);
        if (gem.Name is null || GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) is not null
            || SupportedReservation(conn, gem.Id) is not { } reservation) return;
        var context = ReservationContext(conn, gem.Id);
        if (!ReservationBudgetFits(conn, reservation.Percent, context.Life, context.Efficiency)) return;
        var supported = SupportedSkill(conn, gem.Id);
        var stats = PetrifiedBloodEffectStats(conn, gem.Effect, supported.Level, supported.Quality);
        conn.PetrifiedBlood.Activate(gem.Id, gem.Effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
            Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
            reservation.Percent, LowLifeThresholdPct(CharacterPlayerStats(conn)), conn.NextBuff++,
            conn.Clock.Elapsed.TotalSeconds, context.Life, context.Efficiency);
        ClampReservedResources(conn);
        conn.AuraStatsDirty = true;
        Console.WriteLine($"[gateway] {CharacterName(conn)}: restored saved Petrified Blood");
    }

    // A gem removed or disabled, a town, or death ends the running buff; saved intent stays until death. Entering
    // town also drops any unpaid hit loss (the debuff does not follow the player through the portal).
    public static void SuspendPetrifiedBloodIfUnavailable(ClientState conn)
    {
        if (!conn.PetrifiedBlood.Active) return;
        bool available = GemSkillEffects(conn).Any(g => SkillsFor(conn).EffectId(g.Effect) == PetrifiedBlood.EffectId
            && GemSkillInstanceDisabled(conn, g.Effect, g.Id) is null);
        if (InTown(conn)) conn.PetrifiedBlood.Clear();
        else if (!available || SpellsDisabled(conn)) conn.PetrifiedBlood.Suspend();
        else return;
        // The full stat send restores the client's real life_recovery_per_minute after the block.
        conn.AuraStatsDirty = true;
    }

    static Dictionary<uint, int> PetrifiedBloodEffectStats(ClientState conn, int effect, int level, int quality) =>
        SkillsFor(conn).EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);

    static int Get(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static int LowLifeThresholdPct(IReadOnlyDictionary<uint, int> stats) =>
        LowLife.Threshold(Get(stats, "low_life_threshold_%_override"), Get(stats, "low_life_threshold_%"));

    // The invisible slow_life_loss_from_hit_damage buff (BuffDefinitions 1796) draws the dark deferred-loss overlay
    // on the life orb. Its single value is the current deferred life loss per minute; the duration is the last
    // segment's end. Grace Period suppresses the loss, so it hides the overlay while protection runs.
    public static byte[]? PetrifiedBloodLossPresentation(ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var (perMinute, expiry) = conn.PetrifiedBlood.Display(now);
        if (InGracePeriod(conn)) perMinute = 0;
        var state = conn.PetrifiedBlood;
        var packets = new List<byte[]>();
        bool show = perMinute > 0 && expiry > now;
        if (state.PresentedLossBuff != 0
            && (!show || perMinute != state.SentLossPerMinute || expiry != state.SentLossExpiry))
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedLossBuff));
            state.PresentedLossBuff = 0;
            state.SentLossPerMinute = 0;
            state.SentLossExpiry = 0;
        }
        if (show && state.PresentedLossBuff == 0)
        {
            state.PresentedLossBuff = conn.NextBuff++;
            packets.Add(InstanceEntities.AddBuff(PlayerId, state.PresentedLossBuff, PetrifiedBlood.SlowLossBuffRow,
                PlayerId, values: [(uint)Math.Min(perMinute, int.MaxValue)],
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, expiry - now))));
            state.SentLossPerMinute = perMinute;
            state.SentLossExpiry = expiry;
        }
        return packets.Count == 0 ? null : [.. packets.SelectMany(packet => packet)];
    }

    public static Task SendPetrifiedBloodLoss(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn) => Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.RageRetired
                    || (!conn.PlayerObjectCreated && !conn.PlayerSent)) return null;
                return PetrifiedBloodLossPresentation(conn);
            }
        });
}
