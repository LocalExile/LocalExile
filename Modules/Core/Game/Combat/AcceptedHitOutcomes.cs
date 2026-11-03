using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // Resolve the batch once, before any output await. Only current-state presentation remains queued, so a delayed
    // hook/send cannot replay historical events through the live ticker or resurrect their expired deadlines.
    static void CommitAcceptedChargeBatchLocked(ClientState conn, IReadOnlyList<PlayerMinions.AcceptedCharges> accepted,
        IReadOnlyDictionary<uint, int> player)
    {
        if (conn.PlayerLife == 0) return;
        var session = ChargesOf(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        session.Observe(Get);
        var events = accepted.Select(e => e.KillEvent ? (ChargeEvent)new ChargeEvent.Kill(e.Rarity, e.Frozen)
            : new ChargeEvent.Hit(e.Critical, true, null, e.Rarity, e.Effect,
                name => Stats.Key(name) is uint key ? e.Skill!.GetValueOrDefault(key) : 0, e.Killed, e.Primary));
        var step = session.Admit(accepted.Max(e => e.At), events, Get, () => WeaponProfileFor(conn), conn.PlayerLife > 0, Random.Shared);
        foreach (string line in step.Log) Console.WriteLine($"[gateway] {conn.Wire.Remote} {line} (accepted contact)");
        if (step.CountsChanged)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); conn.Minions.StatRevision++;
        }
        conn.Minions.OutputQueue.Add(new([], null, Kind: PlayerMinions.OutputKind.Charges, Charges: accepted));
    }

    static byte[]? AcceptedChargePresentationLocked(ClientState conn, string remote)
    {
        if (conn.PlayerLife == 0) return null;
        _ = CharacterPlayerStats(conn);
        var (packet, changed) = CurrentChargePresentationLocked(conn, remote);
        if (changed) conn.Minions.StatRevision++;
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
        if (step.CountsChanged)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed();
        }
        var packets = session.Presentation(now, () => conn.NextBuff++).SelectMany(b => ChargeBuffs.Packets(b, PlayerId)).ToArray();
        foreach (var (packet, label) in packets)
            Console.WriteLine($"[gateway] {remote} => {label}: {Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 96)))}");
        return (packets.Length > 0 ? packets.SelectMany(p => p.Packet).ToArray() : null, step.CountsChanged);
    }

    static void CommitAcceptedCritFlasksLocked(ClientState conn, IReadOnlyDictionary<uint, int> player, double at)
    {
        if (!conn.FlaskChargeCarry.CritReady(at)) return;
        var bar = ItemsOf(conn).Items(PlayerInventories.Flask).Select(f => (f.Id,
            Chance: ItemModStats.Local(f.Item, ItemsFor(conn).Mods).GetValueOrDefault("local_recharge_on_crit_%"),
            Full: ItemsFor(conn).ByHash(f.Item.TypeHash) is not { } type || ItemsFor(conn).FlaskChargesOf(type) is not { } basis
                || f.Item.Charges >= FlaskCharges.Of(basis, ItemModStats.Local(f.Item, ItemsFor(conn).Mods)).MaxCharges)).ToList();
        if (bar.All(f => f.Full)) return;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        bool precision = conn.Auras.Values.Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), conn.FlaskChargeCarry, at, Random.Shared.Next);
        if (gains.Count > 0) conn.Minions.FlaskRewards.Add(new(MonsterRarity.Normal, gains));
    }
}
