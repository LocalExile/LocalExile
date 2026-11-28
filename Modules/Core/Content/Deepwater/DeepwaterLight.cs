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

    readonly ConditionalWeakTable<Player, Diver> _divers = new();
    // How many players have a slow: the stat source returns at once while none has (it runs on every stat lookup).
    int _slowed;

    public void Clear()
    {
        _divers.Clear();
        Volatile.Write(ref _slowed, 0);
    }

    static ushort NextBuffId(Player player)
    {
        ushort id = player.NextBuff++;
        return id == 0 ? player.NextBuff++ : id;
    }

    public bool Drowning(Player player, out double left)
    {
        left = 0;
        if (!_divers.TryGetValue(player, out var d) || d.DrownBuff == 0) return false;
        left = Math.Max(0, d.Since + DeepwaterConstants.DrowningSeconds - AreaTime(player));
        return true;
    }

    /// <summary>PlayerStatSources: the Drowning slow, which grows in five steps to
    /// <see cref="DeepwaterConstants.DrowningMaximumSlow"/> % over the Drowning's 5 s (G: the buff's stat names only the
    /// maximum; linear is a guess).</summary>
    public Dictionary<uint, int> WithSlow(Player player, Dictionary<uint, int> sources)
    {
        if (Volatile.Read(ref _slowed) == 0 || !_divers.TryGetValue(player, out var d) || d.Slow == 0) return sources;
        sources[DeepwaterConstants.ActionSpeedReduction] = sources.GetValueOrDefault(DeepwaterConstants.ActionSpeedReduction) + d.Slow;
        return sources;
    }

    /// <summary>One tick of a player in a run.</summary>
    public async Task Tick(IModuleContext module, Player player, DeepwaterRun run)
    {
        if (!player.Connection.PlayerSent || player.LeavingArea || player.Closed) return;
        var diver = _divers.GetValue(player, _ => new());
        double now = AreaTime(player);
        var at = player.Walk.Position(now);
        bool safe = player.PlayerLife == 0 || run.Phase == DeepwaterRun.RunPhase.Ended || run.Board.OnDeck(at);
        bool lit = !safe && run.InLight(at, now);

        if (lit != (diver.LightBuff != 0))
        {
            if (lit)
            {
                diver.LightBuff = NextBuffId(player);
                await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.AddBuff(player.Id, diver.LightBuff, DeepwaterConstants.LanternBuff),
                    $"0x01c1 deepwater_lantern_buff at ({at.X}, {at.Y})");
            }
            else
            {
                await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, diver.LightBuff),
                    $"0x01c2 deepwater_lantern_buff ends at ({at.X}, {at.Y})");
                diver.LightBuff = 0;
            }
        }

        bool dark = !safe && !lit;
        if (dark && diver.DrownBuff == 0)
        {
            diver.DrownBuff = NextBuffId(player);
            diver.Since = now;
            diver.Killed = false;
            await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.AddBuff(player.Id, diver.DrownBuff, DeepwaterConstants.DrowningBuff,
                u68: BitConverter.SingleToUInt32Bits((float)DeepwaterConstants.DrowningSeconds)), $"0x01c1 deepwater_drowning: out of light at ({at.X}, {at.Y})");
            if (!ClientHasQuestFlag(player, DeepwaterConstants.FirstDrowningHeard))
            {
                await SetQuestFlags(player, [DeepwaterConstants.FirstDrowningHeard]);
                await DeepwaterSpeech.FirstDrowning(player);
            }
        }
        else if (!dark && diver.DrownBuff != 0)
        {
            await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, diver.DrownBuff),
                $"0x01c2 deepwater_drowning ends ({(player.PlayerLife == 0 ? "dead" : safe ? "safe" : "back in light")} at ({at.X}, {at.Y}))");
            diver.DrownBuff = 0;
        }

        double elapsed = now - diver.Since;
        int slow = diver.DrownBuff == 0 ? 0
            : DeepwaterConstants.DrowningMaximumSlow * Math.Clamp((int)(elapsed / DeepwaterConstants.DrowningSeconds * 5) + 1, 1, 5) / 5;
        if (slow != diver.Slow) await SetSlow(player, diver, slow);

        if (diver.DrownBuff != 0 && !diver.Killed && elapsed >= DeepwaterConstants.DrowningSeconds)
        {
            diver.Killed = true;
            module.Log($"{player.Character.Name} drowned at ({at.X}, {at.Y}) after {elapsed:0.0} s out of light");
            await DeepwaterSpeech.PlayerDrowned(player);
            await Drown(player);
        }
    }

    /// <summary>The player left the area: its buffs went with it.</summary>
    public void Forget(Player player)
    {
        if (!_divers.TryGetValue(player, out var d)) return;
        if (d.Slow != 0) Interlocked.Decrement(ref _slowed);
        _divers.Remove(player);
    }

    async Task SetSlow(Player player, Diver diver, int slow)
    {
        if ((diver.Slow == 0) != (slow == 0)) Interlocked.Add(ref _slowed, slow == 0 ? -1 : 1);
        diver.Slow = slow;
        MarkPlayerStats(player, PlayerStatStep.Sources);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
            $"0x01b3 player stats (Drowning slow {slow}%)");
    }

    // An execute, not damage (the wiki: "will die soon"): life to 0, then the ordinary death.
    static async Task Drown(Player player)
    {
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return;
        player.PlayerLife = 0;
        player.EnergyShield.Empty();
        uint reserved = player.PlayerReservedLife;
        await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.LifeUpdate(player.Id, 0, reserved), "player life 0 (drowned)");
        await PlayerDied(player, "drowned in the Fathomless Depths");
    }
}
