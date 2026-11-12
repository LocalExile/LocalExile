using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>"Gain Soul Eater during any Flask Effect": while a flask effect is on the player, enemies killed within
    /// this radius feed a soul (poedb: 60 units). Each soul gives 5% increased attack and cast speed and 1% character
    /// size; one is lost every 0.5 s after 4 s without a kill.</summary>
    public const string SoulEaterStat = "gain_soul_eater_during_flask_effect";
    public const int SoulEaterRadius = 60;
    /// <summary>BuffDefinitions row "soul_eater_aura" (277): the aura around the eater. Invisible as an icon, its
    /// BuffVisuals 288 EPK draws the soul visuals (the same aura BuffTemplate "MapModPlayerSoulEater" gives a player).</summary>
    public const ushort SoulEaterAuraRow = 277;
    /// <summary>BuffDefinitions row "soul_eater_player" (279), the player's Soul Eater buff: ShowCount with
    /// <c>virtual_soul_eater_maximum_stacks</c> as its maximum.</summary>
    public const ushort SoulEaterBuffRow = 279;

    static bool SoulEaterStatPresent(IReadOnlyDictionary<uint, int> stats) =>
        Stats.Key(SoulEaterStat) is uint key && stats.GetValueOrDefault(key) != 0;

    // "Any Flask Effect": a running utility flask, a permanent one from a belt's always-apply effect, or a life/mana
    // flask still recovering. Life and mana flasks keep no utility effect here, but their recovery is a flask effect too.
    public static bool AnyFlaskEffect(ClientState conn) =>
        conn.UtilityFlaskEffects.Sources().Count > 0 || MagebloodSources(conn, _ => 0).Count > 0 || conn.FlaskEffects.Any;

    /// <summary>The client's own "during any Flask Effect" conditions and its native flask logic read <c>using_flask</c>
    /// (a stat, not the server's evaluated stats): 1 while any effect is on. Soul Eater's soul visuals are one of them.</summary>
    public static void WithUsingFlask(ClientState conn, Dictionary<uint, int> stats)
    {
        if (Stats.Key("using_flask") is uint key) stats[key] = AnyFlaskEffect(conn) ? 1 : 0;
    }

    static int SoulEaterMaximum(IReadOnlyDictionary<uint, int> stats)
    {
        int bonus = Stats.Key("soul_eater_maximum_stacks_+") is uint key ? stats.GetValueOrDefault(key) : 0;
        return (int)Math.Clamp(SoulEaterState.MaximumStacks + (long)bonus, 0, ushort.MaxValue);
    }

    public static bool SoulEaterActive(ClientState conn, IReadOnlyDictionary<uint, int> stats) =>
        SoulEaterStatPresent(stats) && AnyFlaskEffect(conn);

    /// <summary>The buff's attack speed part, added to the final main-hand value without rebuilding the pools.</summary>
    public static int SoulEaterAttackSpeed(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        if (!SoulEaterActive(conn, stats)) return 0;
        conn.SoulEater.SetMaximum(SoulEaterMaximum(stats));
        return 5 * conn.SoulEater.Stacks(conn.Clock.Elapsed.TotalSeconds);
    }

    /// <summary>The stat snapshot's part: the attack/cast speed buckets and the capped character size.</summary>
    public static void WithSoulEater(ClientState conn, Dictionary<uint, int> stats)
    {
        bool active = SoulEaterActive(conn, stats);
        conn.SoulEater.Enabled = active;
        if (!active) { conn.SoulEater.Clear(); return; }
        conn.SoulEater.SetMaximum(SoulEaterMaximum(stats));
        // The buff's ShowCount reads this virtual: the base maximum plus the Soul Ascension gloves' increase.
        if (Stats.Key("virtual_soul_eater_maximum_stacks") is uint maximumKey)
            stats[maximumKey] = conn.SoulEater.Limit;
        int stacks = conn.SoulEater.Stacks(conn.Clock.Elapsed.TotalSeconds);
        if (stacks <= 0) return;
        void Add(string name, int amount)
        {
            if (amount != 0 && Stats.Key(name) is uint key)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
        }
        Add("attack_speed_+%", 5 * stacks);
        Add("cast_speed_+%", 5 * stacks);
        Add("capped_actor_scale_+%", Math.Min(stacks, 50));
    }

    /// <summary>An enemy killed at <paramref name="at"/>: one soul when the buff is active and the player is near.</summary>
    public static void SoulEaterSoul(ClientState conn, uint id, (int X, int Y) at)
    {
        if (!conn.SoulEater.Enabled) return;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (Dist(at, conn.Walk.Position(now)) > SoulEaterRadius) return;
            conn.SoulEater.Soul(now);
            if (conn.SoulEater.Souls.Count >= 32) conn.SoulEater.Souls.RemoveAt(0);
            conn.SoulEater.Souls.Add((id, at.X, at.Y));
        }
    }

    /// <summary>The eaten-soul buff and, when its count or the aura changed, the stats and skills it changes. Sent from the
    /// player tick (250 ms) like Rage and Onslaught, so a pack's kills coalesce into one update instead of one per corpse.
    /// The aura is its own buff (as a self aura's source buff is): its EPK draws the soul visuals; the counted buff is the
    /// icon the client shows.</summary>
    public static byte[]? SoulEaterPresentation(ClientState conn, string remote, NetworkStream stream,
        ConnectionCiphers ciphers) => SoulEaterRecords(conn, new WorldRecordBuilder(conn, null)) is { } batch
            ? [.. batch.Records.SelectMany(r => r.CopyBytes())] : null;

    static OutputBatch? SoulEaterRecords(ClientState conn, WorldRecordBuilder bytes)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            // Refresh the source before deciding what to publish. Recovery flasks can start/end without a soul gain.
            var currentStats = CharacterPlayerStats(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool active = conn.SoulEater.Enabled && conn.PlayerLife > 0;
            bool usingFlask = Stats.Key("using_flask") is uint flaskKey && currentStats.GetValueOrDefault(flaskKey) != 0;
            int stacks = SoulEaterCount(conn, now);
            bool auraChanged = active != (conn.SoulEater.AuraBuff != 0);
            bool statsChanged = auraChanged || stacks != conn.SoulEater.SentStacks || usingFlask != conn.SoulEater.SentUsingFlask;
            if (!active) conn.SoulEater.Souls.Clear();
            if (!statsChanged && conn.SoulEater.Souls.Count == 0) return null;
            if (conn.SoulEater.Souls.Count > 0)
            {
                // One line per soul, from the corpse to the player, as the official server's ServerBeamEffect.
                var (px, py) = conn.Walk.Position(now);
                foreach (var (soulId, sx, sy) in conn.SoulEater.Souls)
                {
                    uint beam = conn.TakeObjectId();
                    bytes.Create(beam, InstanceEntities.CreateObject(beam, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath),
                        new BeamEffectObject(sx, sy, soulId, px * BeamEffectObject.WorldPerCell,
                            py * BeamEffectObject.WorldPerCell, PlayerId).Blob()));
                    bytes.Presentation(beam, InstanceProtocol.EnterWakeZone(beam));
                    // The connection owns the expiry so the replacement Game module can retire it after a reload.
                    conn.TemporarySkillEffects[beam] = AreaTime(conn) + 0.6;
                }
                conn.SoulEater.Souls.Clear();
            }
            if (auraChanged)
            {
                if (!active)
                {
                    bytes.World(PlayerId, InstanceEntities.RemoveBuff(PlayerId, conn.SoulEater.AuraBuff));
                    conn.SoulEater.AuraBuff = 0;
                }
                else
                {
                    conn.SoulEater.AuraBuff = conn.NextBuff++;
                    bytes.World(PlayerId, InstanceEntities.AddBuff(PlayerId, conn.SoulEater.AuraBuff, SoulEaterAuraRow, PlayerId,
                        auraSource: true, displayState: 0));
                }
            }
            if (stacks != conn.SoulEater.SentStacks)
            {
                if (stacks == 0)
                {
                    if (conn.SoulEater.Buff != 0) bytes.World(PlayerId, InstanceEntities.RemoveBuff(PlayerId, conn.SoulEater.Buff));
                    conn.SoulEater.Buff = 0;
                }
                else
                {
                    if (conn.SoulEater.Buff == 0) conn.SoulEater.Buff = conn.NextBuff++;
                    bytes.World(PlayerId, InstanceEntities.AddBuff(PlayerId, conn.SoulEater.Buff, SoulEaterBuffRow, PlayerId,
                        stacks: (ushort)Math.Min(ushort.MaxValue, stacks)));
                }
            }
            if (statsChanged)
            {
                // Attack and cast speed are in the character's stats; the skill list carries the attack time the client
                // swings with, so both go out when a soul is gained or lost.
                bytes.World(PlayerId, PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                bytes.World(PlayerId, PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
                conn.SoulEater.SentStacks = SoulEaterCount(conn, now);
                conn.SoulEater.SentUsingFlask = usingFlask;
                Console.WriteLine($"[gateway] {CharacterName(conn)} Soul Eater {conn.SoulEater.SentStacks}/{conn.SoulEater.Limit}");
            }
            return bytes.Build();
        }
    }

    public static Task SendSoulEater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendWorldBatch(conn, [], admission => SoulEaterRecords(conn, new(conn, admission)), transport: (remote, stream, ciphers));

    // The souls the client should see: none while the source is off or the player is dead, otherwise the decaying count.
    static int SoulEaterCount(ClientState conn, double now) =>
        conn.SoulEater.Enabled && conn.PlayerLife > 0 ? conn.SoulEater.Stacks(now) : 0;
}
