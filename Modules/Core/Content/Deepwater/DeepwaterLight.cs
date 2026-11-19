using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Light and darkness in a run, per player and per tick: inside a lantern's light the invisible
/// <c>deepwater_lantern_buff</c> (2716); outside every light <c>deepwater_drowning</c> (2736, "Drowning", the Tangle
/// screen ramp) for <see cref="DeepwaterConstants.DrowningSeconds"/>, then death. Back in light the Drowning ends and
/// its timer starts again at the next exit. No drowning on the deck, while dead or after the run ended. Messages go only
/// when a state changes.</summary>
internal sealed class DeepwaterLight
{
    sealed class Diver
    {
        public ushort LightBuff, DrownBuff;
        public double Since;
        /// <summary>The action-speed slow now applied (action_speed_-%), 0 when none.</summary>
        public int Slow;
        public bool Killed;
    }

    readonly ConditionalWeakTable<ClientState, Diver> _divers = new();
    // How many players have a slow: the stat source returns at once while none has (it runs on every stat lookup).
    int _slowed;

    public bool Drowning(ClientState conn, out double left)
    {
        left = 0;
        if (!_divers.TryGetValue(conn, out var d) || d.DrownBuff == 0) return false;
        left = Math.Max(0, d.Since + DeepwaterConstants.DrowningSeconds - conn.Clock.Elapsed.TotalSeconds);
        return true;
    }

    /// <summary>PlayerStatSources: the Drowning slow, which grows in five steps to
    /// <see cref="DeepwaterConstants.DrowningMaximumSlow"/> % over the Drowning's 5 s (G: the buff's stat names only the
    /// maximum; linear is a guess).</summary>
    public Dictionary<uint, int> WithSlow(ClientState conn, Dictionary<uint, int> sources)
    {
        if (Volatile.Read(ref _slowed) == 0 || !_divers.TryGetValue(conn, out var d) || d.Slow == 0) return sources;
        if (Stats.Key(DeepwaterConstants.ActionSpeedReduction) is uint key) sources[key] = sources.GetValueOrDefault(key) + d.Slow;
        return sources;
    }

    /// <summary>One tick of a player in a run.</summary>
    public async Task Tick(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        if (!conn.PlayerSent || conn.LeavingArea || conn.Closed) return;
        var diver = _divers.GetValue(conn, _ => new());
        double now = conn.Clock.Elapsed.TotalSeconds;
        var at = conn.Walk.Position(now);
        bool safe = conn.PlayerLife == 0 || run.Phase == DeepwaterRun.RunPhase.Ended || run.Board.OnDeck(at);
        bool lit = !safe && run.InLight(at, now);
        var player = PlayerOf(conn);

        if (lit != (diver.LightBuff != 0))
        {
            if (lit)
            {
                diver.LightBuff = conn.NextBuff++;
                await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.AddBuff(PlayerId, diver.LightBuff, DeepwaterConstants.LanternBuff),
                    $"0x01c1 deepwater_lantern_buff at ({at.X}, {at.Y})");
            }
            else
            {
                await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.RemoveBuff(PlayerId, diver.LightBuff),
                    $"0x01c2 deepwater_lantern_buff ends at ({at.X}, {at.Y})");
                diver.LightBuff = 0;
            }
        }

        bool dark = !safe && !lit;
        if (dark && diver.DrownBuff == 0)
        {
            diver.DrownBuff = conn.NextBuff++;
            diver.Since = now;
            diver.Killed = false;
            await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.AddBuff(PlayerId, diver.DrownBuff, DeepwaterConstants.DrowningBuff,
                u68: BitConverter.SingleToUInt32Bits((float)DeepwaterConstants.DrowningSeconds)), $"0x01c1 deepwater_drowning: out of light at ({at.X}, {at.Y})");
        }
        else if (!dark && diver.DrownBuff != 0)
        {
            await global::Game.SendWorldRecord(player, PlayerId, InstanceEntities.RemoveBuff(PlayerId, diver.DrownBuff),
                $"0x01c2 deepwater_drowning ends ({(conn.PlayerLife == 0 ? "dead" : safe ? "safe" : "back in light")} at ({at.X}, {at.Y}))");
            diver.DrownBuff = 0;
        }

        double elapsed = now - diver.Since;
        int slow = diver.DrownBuff == 0 ? 0
            : DeepwaterConstants.DrowningMaximumSlow * Math.Clamp((int)(elapsed / DeepwaterConstants.DrowningSeconds * 5) + 1, 1, 5) / 5;
        if (slow != diver.Slow) await SetSlow(conn, diver, slow);

        if (diver.DrownBuff != 0 && !diver.Killed && elapsed >= DeepwaterConstants.DrowningSeconds)
        {
            diver.Killed = true;
            module.Log($"{player.Character} drowned at ({at.X}, {at.Y}) after {elapsed:0.0} s out of light");
            await Drown(conn);
        }
    }

    /// <summary>The player left the area: its buffs went with it.</summary>
    public void Forget(ClientState conn)
    {
        if (!_divers.TryGetValue(conn, out var d)) return;
        if (d.Slow != 0) Interlocked.Decrement(ref _slowed);
        _divers.Remove(conn);
    }

    async Task SetSlow(ClientState conn, Diver diver, int slow)
    {
        if ((diver.Slow == 0) != (slow == 0)) Interlocked.Add(ref _slowed, slow == 0 ? -1 : 1);
        diver.Slow = slow;
        lock (conn.Monsters)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        var w = conn.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Drowning slow {slow}%)");
    }

    // An execute, not damage (the wiki: "will die soon"): life to 0, then the ordinary death.
    static async Task Drown(ClientState conn)
    {
        uint reserved;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return;
            conn.PlayerLife = 0;
            conn.EnergyShield.Empty();
            reserved = conn.PlayerReservedLife;
        }
        var w = conn.Wire;
        await global::Game.SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, 0, reserved), "player life 0 (drowned)");
        await PlayerDied(w.Remote, w.Stream, w.Ciphers, conn, "drowned in the Fathomless Depths");
    }
}
