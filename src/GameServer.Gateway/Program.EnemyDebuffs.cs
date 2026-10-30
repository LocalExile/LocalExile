using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>What the player's debuffs on monsters need from his stats: his passives' enemy auras (PassiveBuffAuras) and
/// taunted_enemies_damage_taken_+% (Bring the Battle), as last sent to the client (Program.PlayerStatsWithCleared).</summary>
sealed record EnemyDebuffs(IReadOnlyList<PassiveBuffAuras.Aura> Auras, int TauntedDamageTakenPct)
{
    public static readonly EnemyDebuffs None = new([], 0);
}

partial class Program
{
    // The taunted buff (BuffDefinitions 408) is IsRecovery (+0x7c), so 0x1c1 carries one u32 per GrantedStats entry
    // (taunt_target_id, is_taunted; reader 0x141fdf870): the taunter and 1.
    static readonly IReadOnlyList<uint> TauntedBuffValues = [PlayerId, 1];

    /// <summary>How long the player's taunts last: 3 s (wiki Taunt; the Basalt Flask's reminder text), raised by
    /// taunt_duration_+%.</summary>
    internal static double TauntSeconds(Func<string, int> get) =>
        FlaskGrounds.TauntSeconds * Math.Max(0, 1 + get("taunt_duration_+%") / 100.0);

    /// <summary>Taunts the hostile living monsters within <paramref name="radius"/> cells of <paramref name="at"/> for
    /// <paramref name="seconds"/> (a warcry, a Basalt Flask); a new taunt replaces the player's old one on a monster (wiki
    /// Taunt: "you can replace your own taunts"). The enemy-debuff step shows it and takes it off. Under the Monsters lock;
    /// the number taunted.</summary>
    internal static int TauntLocked(ClientState conn, (int X, int Y) at, int radius, double now, double seconds)
    {
        if (conn.PlayerLife == 0 || seconds <= 0) return 0;
        int taunted = 0;
        foreach (var m in conn.Monsters.Values)
        {
            if (m.Dead || m.Life == 0 || !m.Hostile || Dist(m.Walk.Position(now), at) > radius) continue;
            m.TauntedUntil = now + seconds;
            m.TakenFromPlayer = m.TakenFromPlayer with { All = conn.EnemyDebuffs.TauntedDamageTakenPct };
            taunted++;
        }
        return taunted;
    }

    // Every 100 ms of game time from area entry (a pause holds it): EnemyDebuffsTick. A failed step is logged and the
    // next one runs; a closed connection ends the loop.
    static async Task RunEnemyDebuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        while (!conn.Closed)
        {
            await Task.Delay(100);
            if (conn.Closed) break;
            if (conn.Paused) continue;
            try
            {
                await EnemyDebuffsTick(remote, stream, ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
            {
                if (!conn.Closed) Console.WriteLine($"[gateway] {remote} enemy debuffs: {ex.Message}");
            }
            catch (Exception) { break; } // the connection has gone
        }
    }

    // One step of the player's debuffs on monsters, worked out at the send gate so a removed monster gets none: a taunt
    // shows the taunted buff (408) for its time left and goes when it ends, the monster dies or the player dies (wiki: a
    // taunt ends when its source dies); each enemy aura of his passives (Light of Divinity's Augury of Penitence, 2551)
    // is on every hostile living monster within its radius of the player while he lives. Each monster then keeps the
    // increased damage taken they give (SpawnedMonster.TakenFromPlayer), which its hits and burns read.
    internal static Task EnemyDebuffsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now) =>
        Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea) return null;
                var config = conn.EnemyDebuffs;
                bool alive = conn.PlayerLife > 0;
                var here = conn.Walk.Position(now);
                uint? elementalKey = Stats.Key("elemental_damage_taken_+%");
                var bytes = new List<byte>();
                int added = 0, removed = 0;
                foreach (var (id, m) in conn.Monsters)
                {
                    bool living = alive && !m.Dead && m.Life > 0 && m.Hostile;
                    bool taunted = living && m.TauntedUntil > now;
                    if (!taunted) m.TauntedUntil = double.NegativeInfinity;
                    if (m.TauntBuff != 0 && (!taunted || m.TauntShownUntil != m.TauntedUntil))
                    {
                        bytes.AddRange(InstanceEntities.RemoveBuff(id, m.TauntBuff));
                        m.TauntBuff = 0;
                        removed++;
                    }
                    if (taunted && m.TauntBuff == 0)
                    {
                        m.TauntShownUntil = m.TauntedUntil;
                        bytes.AddRange(InstanceEntities.AddBuff(id, m.TauntBuff = conn.NextBuff++, FlaskGrounds.TauntedBuffRow, PlayerId,
                            values: TauntedBuffValues, u68: BitConverter.SingleToUInt32Bits((float)(m.TauntedUntil - now))));
                        added++;
                    }
                    double distance = living && (config.Auras.Count > 0 || m.PlayerAuras.Count > 0)
                        ? Dist(m.Walk.Position(now), here) : double.PositiveInfinity;
                    if (m.PlayerAuras.Count > 0)
                        foreach (var (definition, buff) in m.PlayerAuras.ToArray())
                            if (config.Auras.FirstOrDefault(a => a.Definition == definition) is not { } kept || distance > kept.Radius)
                            {
                                bytes.AddRange(InstanceEntities.RemoveBuff(id, buff));
                                m.PlayerAuras.Remove(definition);
                                removed++;
                            }
                    int elemental = 0;
                    foreach (var aura in config.Auras)
                    {
                        if (distance > aura.Radius) continue;
                        if (!m.PlayerAuras.ContainsKey(aura.Definition))
                        {
                            bytes.AddRange(InstanceEntities.AddBuff(id, m.PlayerAuras[aura.Definition] = conn.NextBuff++, aura.Definition, PlayerId));
                            added++;
                        }
                        elemental += aura.Stats.Where(s => s.Key == elementalKey).Sum(s => s.Value);
                    }
                    m.TakenFromPlayer = (taunted ? config.TauntedDamageTakenPct : 0, elemental);
                }
                if (bytes.Count == 0) return null;
                Console.WriteLine($"[gateway] {remote} enemy debuffs: {added} added, {removed} removed (t={now:0.000})");
                return [.. bytes];
            }
        });
}
