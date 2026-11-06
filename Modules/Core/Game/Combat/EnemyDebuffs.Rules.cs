using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
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
        if (!CurrentGameWork || conn.PlayerLife == 0 || seconds <= 0) return 0;
        int taunted = 0;
        foreach (var m in conn.Monsters.Values)
        {
            if (m.Dead || m.Life == 0 || !m.Hostile || Dist(m.Walk.Position(now), at) > radius) continue;
            m.TauntedUntil = now + seconds / m.RecipientDebuffRate;
            m.TakenFromPlayer = m.TakenFromPlayer with { All = conn.EnemyDebuffs.TauntedDamageTakenPct };
            taunted++;
        }
        return taunted;
    }

    // The player tick (RunPlayerTick, 250 ms, not while paused) steps the player's debuffs on monsters.
    internal static Task TickPassiveAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        EnemyDebuffsTick(remote, stream, ciphers, conn, AreaTime(conn));

    // One step of the player's debuffs on monsters, worked out at the send gate so a removed monster gets none: a taunt
    // shows the taunted buff (408) for its time left and goes when it ends, the monster dies or the player dies (wiki: a
    // taunt ends when its source dies), and his hits' Intimidate, Unnerve and Blind (HitDebuffs) the same way; each enemy aura of his passives (Light of Divinity's Augury of Penitence, 2551)
    // and Born under Fire's) is on every hostile living monster within its radius of the player while he lives, and the
    // blind aura (SaboteurBlind) blinds them, shown with the blinded buff (PassiveBuffAuras.BlindState, Game.Blinded).
    // Pride adds its own enemy aura (BuffDefinitions 1331) with the ramp each monster keeps while it stays inside.
    // Each monster then keeps the increased damage taken they give (SpawnedMonster.TakenFromPlayer), which its hits and
    // burns read. A refund or a death takes the auras off; a new area is a new connection.
    // How many u32 stat values the client reads in Pride's enemy buff (BuffDefinitions 1331) before its counted list.
    const int PrideEnemyValues = 15;

    internal static async Task EnemyDebuffsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        ushort[] nodes;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
            nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        }
        // Outside the lock: the first call loads the aura table from PassiveSkills.
        var all = PassiveBuffAuras.EnemyAurasFor(nodes, LeagueFor(conn).HardMode, EnemyAuraActs);
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
                now = AreaTime(conn);
                ushort[] currentNodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
                var current = nodes.SequenceEqual(currentNodes) ? all
                    : PassiveBuffAuras.EnemyAurasFor(currentNodes, LeagueFor(conn).HardMode, EnemyAuraActs);
                var auras = current.Where(a => a.Template != PassiveBuffAuras.BlindTemplate).ToList();
                int? blindRadius = PassiveBuffAuras.BlindRadius(current);
                var config = conn.EnemyDebuffs;
                bool alive = conn.PlayerLife > 0;
                var here = conn.Walk.Position(now);
                uint? elementalKey = Stats.Key("elemental_damage_taken_+%");
                // Pride is a skill aura whose effect is on enemies: a synthetic enemy aura carries its buff, and its ramp
                // is kept per monster.
                AuraCatalog.Aura? pride = conn.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == "Pride");
                IReadOnlyList<PassiveBuffAuras.Aura> enemyAuras = auras;
                int prideRadius = 0;
                (int Min, int Max) prideValues = default;
                if (pride is not null)
                {
                    var skills = SkillsFor(conn);
                    prideRadius = AuraRadius.Of(pride, conn.Pools.AuraAreaPct, skills, Stats);
                    prideValues = AuraCatalog.PridePhysicalDamageTaken(pride, conn.Pools.AuraEffectPct, skills, Stats);
                    enemyAuras = [.. auras, new PassiveBuffAuras.Aura(0, "Pride", AuraCatalog.PrideBuffRow,
                        prideRadius, [3], [])];
                }
                // Flesh and Stone is an enemy aura too: Blood Stance's increased Physical Damage taken, Sand Stance's
                // blind (below, with the passive blind aura's radius). Its stance comes from the activation (ToggleAura).
                AuraCatalog.Aura? fleshAndStone = conn.Auras.Values.FirstOrDefault(a =>
                    AuraCatalog.Family(a.EffectId) == "FleshAndStone");
                int fleshRadius = 0, fleshPhysical = 0;
                bool fleshBlood = false;
                if (fleshAndStone is not null)
                {
                    var fleshSkills = SkillsFor(conn);
                    fleshRadius = AuraRadius.Of(fleshAndStone, conn.Pools.AuraAreaPct, fleshSkills, Stats);
                    fleshBlood = fleshAndStone.Stance != "Sand";
                    if (fleshBlood)
                        fleshPhysical = AuraCatalog.FleshAndStonePhysicalTaken(fleshAndStone, conn.Pools.AuraEffectPct,
                            fleshSkills, Stats);
                }
                var bytes = new List<byte>();
                int added = 0, removed = 0;
                // One timed debuff on a monster: its buff shown for the time left, shown again when that changes, taken
                // off when it ends or the monster or the player dies. Whether it is on.
                bool Timed(uint id, bool living, ref double until, ref double shown, ref ushort buff, ushort row,
                    IReadOnlyList<uint>? values, double rate = 1)
                {
                    bool on = living && until > now;
                    if (!on) until = double.NegativeInfinity;
                    if (buff != 0 && (!on || shown != until))
                    {
                        bytes.AddRange(InstanceEntities.RemoveBuff(id, buff));
                        buff = 0;
                        removed++;
                    }
                    if (on && buff == 0)
                    {
                        shown = until;
                        bytes.AddRange(InstanceEntities.AddBuff(id, buff = conn.NextBuff++, row, PlayerId, values: values,
                            u68: BitConverter.SingleToUInt32Bits((float)((until - now) * rate))));
                        added++;
                    }
                    return on;
                }
                // Its Withered stacks (WitherOnHitLocked): one buff with their count and the time the last one has left,
                // shown again when either changes; off when none runs or it or the player dies.
                void Withers(uint id, bool living, SpawnedMonster m)
                {
                    if (m.Withered.Count == 0 && m.WitherBuff == 0) return;
                    if (living) m.Withered.RemoveAll(s => s.Until <= now);
                    else m.Withered.Clear();
                    int stacks = m.Withered.Count;
                    double until = stacks > 0 ? m.Withered.Max(s => s.Until) : 0;
                    if (m.WitherBuff != 0 && (stacks != m.WitherShownStacks || until != m.WitherShownUntil))
                    {
                        bytes.AddRange(InstanceEntities.RemoveBuff(id, m.WitherBuff));
                        m.WitherBuff = 0;
                        removed++;
                    }
                    if (stacks == 0 || m.WitherBuff != 0) return;
                    (m.WitherShownStacks, m.WitherShownUntil) = (stacks, until);
                    bytes.AddRange(InstanceEntities.AddBuff(id, m.WitherBuff = conn.NextBuff++, WitheredBuffRow, PlayerId,
                        values: [(uint)Math.Round(m.Withered[^1].Pct), 1], u68: BitConverter.SingleToUInt32Bits((float)(until - now)),
                        stacks: (ushort)stacks, u82: 0));
                    added++;
                }
                foreach (var (id, m) in conn.Monsters)
                {
                    bool living = alive && !m.Dead && m.Life > 0 && m.Hostile;
                    bool taunted = Timed(id, living, ref m.TauntedUntil, ref m.TauntShownUntil, ref m.TauntBuff,
                        FlaskGrounds.TauntedBuffRow, TauntedBuffValues, m.RecipientDebuffRate);
                    Timed(id, living, ref m.IntimidatedUntil, ref m.IntimidateShownUntil, ref m.IntimidateBuff, IntimidatedBuffRow, null);
                    Timed(id, living, ref m.UnnervedUntil, ref m.UnnerveShownUntil, ref m.UnnerveBuff, UnnervedBuffRow, null);
                    Timed(id, living, ref m.CrushedUntil, ref m.CrushShownUntil, ref m.CrushBuff, CrushedBuffRow, null);
                    Timed(id, living, ref m.MaimedUntil, ref m.MaimShownUntil, ref m.MaimBuff, MaimedBuffRow, null);
                    Timed(id, living, ref m.BlindedUntil, ref m.BlindShownUntil, ref m.BlindBuff, FlaskGrounds.BlindedBuffRow, null);
                    for (int i = 0; i < ExposureTypes.Length; i++)
                        if (!Timed(id, living, ref m.ExposureUntil[i], ref m.ExposureShownUntil[i], ref m.ExposureBuff[i],
                                ExposureBuffRows[i], null))
                            EndExposureLocked(m, ExposureTypes[i]);
                    Withers(id, living, m);
                    double distance = living && (enemyAuras.Count > 0 || m.PlayerAuras.Keys.Any(k => k.Peer == conn.PeerId))
                        ? Dist(m.Walk.Position(now), here) : double.PositiveInfinity;
                    if (m.PlayerAuras.Count > 0)
                        foreach (var ((peer, definition), buff) in m.PlayerAuras.ToArray())
                            if (peer == conn.PeerId && (enemyAuras.FirstOrDefault(a => a.Definition == definition) is not { } kept || distance > kept.Radius))
                            {
                                bytes.AddRange(InstanceEntities.RemoveBuff(id, buff));
                                m.PlayerAuras.Remove((peer, definition));
                                removed++;
                            }
                    int elemental = 0;
                    foreach (var aura in enemyAuras)
                    {
                        if (distance > aura.Radius) continue;
                        if (!m.PlayerAuras.ContainsKey((conn.PeerId, aura.Definition)))
                        {
                            // Pride's row (1331) has the bool at +0x7d (__col25 in the vendored schema) that, as IsRecovery
                            // does, makes the client read its stat values: fifteen u32 before the counted list, whatever the
                            // message holds (a probe on 0x141fdf870, live 2026-11-05), not the row's three GrantedStats (pride_physical_damage_taken_+%_final,
                            // base_additional_physical_damage_reduction_%, receive_bleeding_chance_%_when_hit_by_attack).
                            // Without them it read 12 bytes of the next message ("Unable to deserialise packet with pid
                            // 1032. Last PID was 449", live 2026-11-05, Dominus' second form walking into the aura).
                            uint[]? values = aura.Definition == AuraCatalog.PrideBuffRow ? [(uint)Math.Max(0, prideValues.Min), .. new uint[PrideEnemyValues - 1]] : null;
                            bytes.AddRange(InstanceEntities.AddBuff(id, m.PlayerAuras[(conn.PeerId, aura.Definition)] = conn.NextBuff++, aura.Definition, PlayerId,
                                values: values));
                            added++;
                        }
                        elemental += aura.Stats.Where(s => s.Key == elementalKey).Sum(s => s.Value);
                    }
                    if (pride is not null && living && distance <= prideRadius)
                    {
                        if (double.IsNaN(m.PrideSince)) m.PrideSince = now;
                        m.PridePhysicalMore = AuraCatalog.PridePhysicalMore(prideValues.Min, prideValues.Max, now - m.PrideSince);
                    }
                    else
                    {
                        m.PrideSince = double.NaN;
                        m.PridePhysicalMore = 0;
                    }
                    // Blood Stance: the increased Physical Damage taken falls off with the monster's distance from the
                    // player, full at 0 and nothing at the aura's edge (the data's ..._from_player_distance).
                    int physical = fleshBlood && living && fleshPhysical > 0 && distance <= fleshRadius
                        ? (int)Math.Round(fleshPhysical * (1 - distance / fleshRadius), MidpointRounding.AwayFromZero)
                        : 0;
                    m.TakenFromPlayer = (taunted ? config.TauntedDamageTakenPct : 0, elemental, physical);
                }
                // Sand Stance blinds the monsters within its radius, on top of the passive blind aura (if any).
                int blindReach = Math.Max(blindRadius ?? 0, fleshAndStone is not null && !fleshBlood ? fleshRadius : 0);
                int? radius = conn.PlayerLife > 0 && blindReach > 0 ? blindReach : null;
                HashSet<uint> inside = radius is int r
                    ? conn.Monsters.Where(kv => kv.Value is { Hostile: true, Dead: false, Life: > 0 } && kv.Value.Mods.Effects["cannot_be_blinded"] == 0 && Dist(kv.Value.Walk.Position(now), here) <= r)
                        .Select(kv => kv.Key).ToHashSet()
                    : [];
                foreach (var (id, buff, blinded) in conn.BlindAura.Reconcile(inside, conn.Monsters.ContainsKey, () => conn.NextBuff++))
                {
                    bytes.AddRange(blinded ? InstanceEntities.AddBuff(id, buff, FlaskGrounds.BlindedBuffRow, PlayerId) : InstanceEntities.RemoveBuff(id, buff));
                    if (blinded) added++; else removed++;
                }
                if (bytes.Count == 0) return null;
                Console.WriteLine($"[gateway] {remote} enemy debuffs: {added} added, {removed} removed (t={now:0.000})");
                return [.. bytes];
            }
        });
    }
}
