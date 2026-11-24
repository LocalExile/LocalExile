using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Petrified Blood is a reservation buff, not an aura: reservation and the received buff are its own, and the
    // own-aura restriction does not touch it. Its hit prevention lives in PetrifiedBloodState (Gateway Combat).
    public static async Task<bool> TogglePetrifiedBlood(Player player, ushort skillId, int effect, string name, SkillCatalog.ReservationCost reservation, int level, int quality)
    {
        bool started;
        // Locked commit backstop: a source may have changed after dispatch/cooldown preflight.
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || InTown(player)
            || EternalBlessingDisables(player, skillId) || CastingSpell(player, effect) && SpellsDisabled(player)) return false;
        if (player.PetrifiedBlood.Active)
        {
            player.PetrifiedBlood.Suspend();
            started = false;
        }
        else
        {
            var stats = PetrifiedBloodEffectStats(player, effect, level, quality);
            var context = ReservationContext(player, skillId);
            if (!ReservationBudgetFits(player, reservation.ManaPercent, context.Life, context.Efficiency))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {name}: insufficient unreserved {(context.Life ? "life" : "mana")}");
                return false;
            }
            player.PetrifiedBlood.Activate(skillId, effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
                Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
                context.Life ? reservation.LifePercent : reservation.ManaPercent, LowLifeThresholdPct(CharacterPlayerStats(player)), player.NextBuff++,
                AreaTime(player), context.Life, context.Efficiency);
            ClampReservedResources(player);
            started = true;
        }
        // The client's own Life step reads life_recovery_per_minute; the full stat send below carries 0 while the
        // new buff blocks recovery and the real rate when it does not.
        player.AuraStatsDirty = true;
        CharacterSaves.SetPetrifiedBlood(CharacterName(player), started);
        Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {name}: {(started ? "on" : "off")}, " +
            $"Low Life threshold {player.PetrifiedBlood.LowLifeThresholdPct}%");
        await SendOwnAuraChanges(player);
        await SendPlayerMana(player,
            $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved (Petrified Blood {(started ? "on" : "off")})");
        return true;
    }

    // Saved intent restores the buff once its gem is socketed and enabled, outside town, after the auras so the
    // combined reservation is checked against the same maximum.
    public static void RestoreRememberedPetrifiedBlood(Player player)
    {
        if (player.PetrifiedBlood.Active || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0
            || InTown(player) || !CharacterSaves.Remembered(CharacterName(player)).PetrifiedBlood
            || SpellsDisabled(player)) return;
        var gem = GemSkillEffects(player).FirstOrDefault(g => SkillsFor(player).EffectId(g.Effect) == PetrifiedBlood.EffectId);
        if (gem.Name is null || GemSkillInstanceDisabled(player, gem.Effect, gem.Id) is not null
            || SupportedReservationCost(player, gem.Id) is not { } reservation) return;
        var context = ReservationContext(player, gem.Id);
        int reservationPct = context.Life ? reservation.LifePercent : reservation.ManaPercent;
        if (!ReservationBudgetFits(player, reservationPct, context.Life, context.Efficiency)) return;
        var stats = PetrifiedBloodEffectStats(player, gem.Effect, gem.Level, gem.Quality);
        player.PetrifiedBlood.Activate(gem.Id, gem.Effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
            Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
            reservationPct, LowLifeThresholdPct(CharacterPlayerStats(player)), player.NextBuff++,
            AreaTime(player), context.Life, context.Efficiency);
        ClampReservedResources(player);
        player.AuraStatsDirty = true;
        Console.WriteLine($"[gateway] {CharacterName(player)}: restored saved Petrified Blood");
    }

    // A gem removed or disabled, a town, or death ends the running buff; saved intent stays until death. Entering
    // town also drops any unpaid hit loss (the debuff does not follow the player through the portal).
    public static void SuspendPetrifiedBloodIfUnavailable(Player player)
    {
        if (!player.PetrifiedBlood.Active) return;
        bool available = GemSkillEffects(player).Any(g => SkillsFor(player).EffectId(g.Effect) == PetrifiedBlood.EffectId
            && GemSkillInstanceDisabled(player, g.Effect, g.Id) is null);
        if (InTown(player)) player.PetrifiedBlood.Clear();
        else if (!available || SpellsDisabled(player)) player.PetrifiedBlood.Suspend();
        else return;
        // The full stat send restores the client's real life_recovery_per_minute after the block.
        player.AuraStatsDirty = true;
    }

    static Dictionary<uint, int> PetrifiedBloodEffectStats(Player player, int effect, int level, int quality) =>
        SkillsFor(player).EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);

    static int Get(IReadOnlyDictionary<uint, int> stats, uint name) =>
        name is var key ? stats.GetValueOrDefault(key) : 0;

    internal static int LowLifeThresholdPct(IReadOnlyDictionary<uint, int> stats) =>
        LowLife.Threshold(Get(stats, Stat.LowLifeThresholdPctOverride), Get(stats, Stat.LowLifeThresholdPct));

    // The invisible slow_life_loss_from_hit_damage buff (BuffDefinitions 1796) draws the dark deferred-loss overlay
    // on the life orb. Its single value is the current deferred life loss per minute; the duration is the last
    // segment's end. Grace Period suppresses the loss, so it hides the overlay while protection runs.
    public static byte[]? PetrifiedBloodLossPresentation(Player player)
    {
        double now = AreaTime(player);
        if (player.PetrifiedBlood.NextEnd(now) is double ends) PresentIn(player, ends - now);
        var (perMinute, expiry) = player.PetrifiedBlood.Display(now);
        if (InGracePeriod(player)) perMinute = 0;
        var state = player.PetrifiedBlood;
        var packets = new List<byte[]>();
        bool show = perMinute > 0 && expiry > now;
        if (state.PresentedLossBuff != 0
            && (!show || perMinute != state.SentLossPerMinute || expiry != state.SentLossExpiry))
        {
            player.Area.Buffs.Clear(player.Id, PetrifiedBlood.SlowLossBuffRow, player.Id);
            state.PresentedLossBuff = 0;
            state.SentLossPerMinute = 0;
            state.SentLossExpiry = 0;
        }
        if (show && state.PresentedLossBuff == 0)
        {
            state.PresentedLossBuff = 1;
            player.Area.Buffs.Set(player.Id, new(PetrifiedBlood.SlowLossBuffRow, player.Id,
                Values: [(uint)Math.Min(perMinute, int.MaxValue)], U68: Buff.Seconds(expiry - now)));
            state.SentLossPerMinute = perMinute;
            state.SentLossExpiry = expiry;
        }
        return packets.Count == 0 ? null : [.. packets.SelectMany(packet => packet)];
    }

    public static Task SendPetrifiedBloodLoss(Player player) => Send(player, [], null, atGate: () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired
                || (!player.Connection.PlayerObjectCreated && !player.Connection.PlayerSent)) return null;
            return PetrifiedBloodLossPresentation(player);
        });
}
