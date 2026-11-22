using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed class ChargePoolRefresh { public bool Pending; }
    static readonly RuleTable<ClientState, ChargePoolRefresh> ChargePoolRefreshes = new();

    // Resolve the batch once, before any output await. Only current-state presentation remains queued, so a delayed
    // hook/send cannot replay historical events through the live ticker or resurrect their expired deadlines.
    static void CommitAcceptedChargeBatchLocked(ClientState conn, IReadOnlyList<PlayerMinions.AcceptedCharges> accepted,
        IReadOnlyDictionary<uint, int> player)
    {
        if (conn.PlayerLife == 0) return;
        foreach (var e in accepted) TryGainElusiveLocked(conn, e.Critical && !e.KillEvent, e.KillEvent, e.At);
        var session = ChargesOf(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        session.Observe(Get);
        ChargeEvent[] events = [.. accepted.Select(e => e.KillEvent
            ? (ChargeEvent)new ChargeEvent.Kill(e.Rarity, e.Frozen,
                Skill: e.Skill is { } killing ? name => Stats.Key(name) is uint key ? killing.GetValueOrDefault(key) : 0 : null)
            : new ChargeEvent.Hit(e.Critical, true, null, e.Rarity, e.Effect,
                name => Stats.Key(name) is uint key ? e.Skill!.GetValueOrDefault(key) : 0, e.Killed, e.Primary))];
        var step = session.Admit(accepted.Max(e => e.At), events, Get, () => WeaponProfileFor(conn), conn.PlayerLife > 0, Random.Shared);
        // A kill credited to the player through another path (a brand's, a herald's, a granted skill's) is the player's
        // kill for rules and mods too. Its hits were raised where they landed (HitLanded).
        foreach (var e in events)
            if (e is not ChargeEvent.Hit)
            {
                var raised = TriggerDepth != 0 ? e with { Depth = TriggerDepth } : e;
                EffectRules.Run(conn, raised);
                GameEvents.RaiseCombat(conn, raised);
            }
        foreach (string line in step.Log) Console.WriteLine($"[gateway] {conn.Wire.Remote} {line} (accepted contact)");
        // Charge counts change on almost every hit; the charge ticker publishes the stats once per step instead of
        // rebuilding the full skill list per hit (the 2026-11-05 log: 4 PlayerSkills builds in 80 ms, each inside the
        // send gate, and 0x0025 handlers at 50-200 ms).
        if (step.CountsChanged) ChargePoolRefreshes.GetOrCreateValue(conn).Pending = true;
        conn.Minions.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.Charges, Charges: accepted));
    }

    static byte[]? AcceptedChargePresentationLocked(ClientState conn, string remote)
    {
        if (conn.PlayerLife == 0) return null;
        // The charge ticker publishes the stats at its own cadence; a per-hit revision here rebuilt PlayerSkills for
        // every charge change, inside the send gate.
        var (packet, _) = CurrentChargePresentationLocked(conn, remote);
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

    // All consumers build/acknowledge the entire native change under Monsters at their actual send gate.
    // No producer may buffer a tail of this presentation for separate later sends.
    static (byte[]? Packet, bool CountsChanged) CurrentChargePresentationLocked(ClientState conn, string remote)
    {
        var session = ChargesOf(conn); double now = ProjectileWorldTime(conn);
        var step = session.Refresh(now);
        if (step.CountsChanged) ChargePoolRefreshes.GetOrCreateValue(conn).Pending = true;
        var packets = session.Presentation(now, () => conn.NextBuff++).SelectMany(b => ChargeBuffs.Packets(b, PlayerId, type => ChargeArtVisual(conn, type))).ToArray();
        foreach (var (packet, label) in packets)
            Console.WriteLine($"[gateway] {remote} => {label}: {Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 96)))}");
        return (packets.Length > 0 ? packets.SelectMany(p => p.Packet).ToArray() : null, step.CountsChanged);
    }

    static void CommitAcceptedCritFlasksLocked(ClientState conn, IReadOnlyDictionary<uint, int> player, double at)
    {
        if (!conn.FlaskChargeCarry.CritReady(at)) return;
        var bar = CritFlaskBar(conn);
        if (bar.All(f => f.Full)) return;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        bool precision = AffectingAuras(conn).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), conn.FlaskChargeCarry, at, Random.Shared.Next);
        if (gains.Count > 0) conn.Minions.FlaskRewards.Add(new(MonsterRarity.Normal, gains));
    }

    static List<(uint Id, int Chance, bool Full)> CritFlaskBar(ClientState conn)
    {
        var catalog = ItemsFor(conn);
        return ItemsOf(conn).Items(PlayerInventories.Flask).Select(flask =>
        {
            var local = ItemModStats.Local(flask.Item, catalog.Mods);
            int maximum = catalog.ByHash(flask.Item.TypeHash) is { } type && catalog.FlaskChargesOf(type) is { } basis
                ? FlaskCharges.Of(basis, local).MaxCharges : 0;
            return (flask.Id, local.GetValueOrDefault("local_recharge_on_crit_%"), flask.Item.Charges >= maximum);
        }).ToList();
    }
}
