using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed class ChargePoolRefresh { public bool Pending; }
    static readonly Store<Player, ChargePoolRefresh> ChargePoolRefreshes = new();

    // Resolve the batch once, before any output await. Only current-state presentation remains queued, so a delayed
    // hook/send cannot replay historical events through the live ticker or resurrect their expired deadlines.
    static void CommitAcceptedChargeBatchLocked(Player player, IReadOnlyList<PlayerMinions.AcceptedCharges> accepted,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        if (player.PlayerLife == 0) return;
        foreach (var e in accepted) TryGainElusiveLocked(player, e.Critical && !e.KillEvent, e.KillEvent, e.At);
        var session = ChargesOf(player);
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        session.Observe(Get);
        var killSkill = accepted.Any(e => e.KillEvent && e.Skill is null) ? TakeAcceptedKillSkill(player) : null;
        Func<uint, int>? SkillLookup(PlayerMinions.AcceptedCharges e)
        {
            var skill = e.Skill ?? (e.KillEvent ? killSkill : null);
            return skill is null ? null : name => name is var key ? skill.GetValueOrDefault(key) : 0;
        }
        var events = accepted.Select(e => e.KillEvent
            ? (ChargeEvent)new ChargeEvent.Kill(e.Rarity, e.Frozen, Skill: SkillLookup(e))
            : new ChargeEvent.Hit(e.Critical, true, null, e.Rarity, e.Effect, SkillLookup(e), e.Killed, e.Primary));
        var step = session.Admit(accepted.Max(e => e.At), events, Get, () => WeaponProfileFor(player), player.PlayerLife > 0, AreaRng(player));
        foreach (string line in step.Log) Console.WriteLine($"[gateway] {player.Connection.Remote} {line} (accepted contact)");
        // Charge counts change on almost every hit; the charge ticker publishes the stats once per step instead of
        // rebuilding the full skill list per hit (the 2026-11-05 log: 4 PlayerSkills builds in 80 ms, each inside the
        // send gate, and 0x0025 handlers at 50-200 ms).
        if (step.CountsChanged) { ChargePoolRefreshes.GetOrCreateValue(player).Pending = true; WakeCharges(player); }
        player.Minions.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.Charges, Charges: accepted));
    }

    static byte[]? AcceptedChargePresentationLocked(Player player, string remote)
    {
        if (player.PlayerLife == 0) return null;
        // The charge ticker publishes the stats at its own cadence; a per-hit revision here rebuilt PlayerSkills for
        // every charge change, inside the send gate.
        var (packet, _) = CurrentChargePresentationLocked(player, remote);
        return packet;
    }

    // A hint only: no buff ids or shown records change until a sender owns the real cipher gate.
    static bool ChargePresentationPending(ChargeSession session) => Charges.Types.Any(type =>
    {
        int count = session.Effective(type); var old = session.State.Presented[(int)type];
        double expires = session.State.Held(type) > session.Limits(type).Minimum ? session.State.ExpiresAt(type) : double.PositiveInfinity;
        return (count != 0 || old.Buff != 0) && !(old.Count == count &&
            (count == 0 || old.ExpiresAt == expires && old.Converted == session.Converted(type)));
    });

    // All consumers build/acknowledge the entire native change in the area's frame at their actual send gate.
    // No producer may buffer a tail of this presentation for separate later sends.
    static (byte[]? Packet, bool CountsChanged) CurrentChargePresentationLocked(Player player, string remote)
    {
        var session = ChargesOf(player); double now = ProjectileWorldTime(player);
        var step = session.Refresh(now);
        if (step.CountsChanged) { ChargePoolRefreshes.GetOrCreateValue(player).Pending = true; WakeCharges(player); }
        foreach (var change in session.Presentation(now, () => 1))
            Console.WriteLine($"[gateway] {remote} => charge buff {ChargeBuffs.Apply(change, player, type => ChargeArtVisual(player, type))}");
        return (null, step.CountsChanged);
    }

    static void CommitAcceptedCritFlasksLocked(Player player, IReadOnlyDictionary<uint, int> playerStats, double at)
    {
        if (!player.FlaskChargeCarry.CritReady(at)) return;
        var bar = CritFlaskBar(player);
        if (bar.All(f => f.Full)) return;
        bool precision = AffectingAuras(player).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, PlayerFlaskCritSources(player, playerStats, precision), player.FlaskChargeCarry, at, AreaRng(player).Next);
        if (gains.Count > 0) player.Minions.FlaskRewards.Add(new(MonsterRarity.Normal, gains));
    }

    static List<(uint Id, int Chance, bool Full)> CritFlaskBar(Player player)
    {
        var catalog = ItemsFor(player);
        return ItemsOf(player).Items(PlayerInventories.Flask).Select(flask =>
        {
            var local = ItemModStats.Local(flask.Item, catalog.Mods);
            int maximum = catalog.ByHash(flask.Item.TypeHash) is { } type && catalog.FlaskChargesOf(type) is { } basis
                ? FlaskCharges.Of(basis, local).MaxCharges : 0;
            return (flask.Id, local.GetValueOrDefault(Stat.LocalRechargeOnCritPct), flask.Item.Charges >= maximum);
        }).ToList();
    }
}
