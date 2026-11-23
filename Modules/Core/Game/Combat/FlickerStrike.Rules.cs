using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The client's Flicker Strike action selects these MiscAnimated resources by skill_art_variation.
    // In is the departure effect and Out is the arrival effect (their sound events confirm the direction).
    internal static async Task FlickerArt(Player player,
        uint gem, (int X, int Y) from, (int X, int Y) to)
    {
        int variation = SkillArtVariation(player, gem);
        var (departure, arrival) = variation switch
        {
            1 => ("Metadata/Effects/Microtransactions/Spells/flicker_strike/ninja_smoke/rig_in.ao", "Metadata/Effects/Microtransactions/Spells/flicker_strike/ninja_smoke/rig_out.ao"),
            2 => ("Metadata/Effects/Microtransactions/Spells/flicker_strike/necro_smoke/rig_in.ao", "Metadata/Effects/Microtransactions/Spells/flicker_strike/necro_smoke/rig_out.ao"),
            4 => ("Metadata/Effects/Spells/monsters_effects/act10/Kitava/unique_minion/flicker_strike/disappear.ao", "Metadata/Effects/Spells/monsters_effects/act10/Kitava/unique_minion/flicker_strike/appear.ao"),
            _ => ("Metadata/Effects/Spells/flicker_strike_new/Flicker_In.ao", "Metadata/Effects/Spells/flicker_strike_new/Flicker_Out.ao")
        };
        uint first, second;
        if (player.Closed || player.LeavingArea) return;
        first = player.TakeObjectId();
        second = player.TakeObjectId();
        await Send(player,
            [.. InstanceEntities.CreateObject(first, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(from.X, from.Y, departure).Blob()),
             .. InstanceProtocol.EnterWakeZone(first),
             .. InstanceEntities.CreateObject(second, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(to.X, to.Y, arrival).Blob()),
             .. InstanceProtocol.EnterWakeZone(second)],
            $"Flicker Strike art variation {variation}: departure ({from.X}, {from.Y}), arrival ({to.X}, {to.Y})");
        SendAfter(player, 2, async () =>
        {
            if (player.Closed || player.LeavingArea) return;
            await Send(player, [.. InstanceProtocol.RemoveObject(first), .. InstanceProtocol.RemoveObject(second)], null);
        });
    }

    /// <summary>Where Flicker Strike takes the player: the monster the client's action picks (FlickerStrike) and the
    /// landing next to it. Null when no monster is in range: the use is then refused (FlickerRefused).</summary>
    public static (uint Id, (int X, int Y) At, (int X, int Y) Landing)? FlickerDestination(Player player, int effect,
        ushort skillId, (int X, int Y) playerAt, (int X, int Y) aim, uint clicked, double now)
    {
        var stats = SupportedSkill(player, skillId).Stats;
        int range = FlickerStrike.Range(GolemStat(stats, Stat.FlickerStrikeTeleportRangePlusPct),
            GolemStat(stats, Stat.ApplyCutDebuffOnHit) != 0);
        var monsters = new List<(uint, (int, int), int, bool)>();
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (m.Dead || m.Life <= 0 || m.OwnerId is not null || MonsterAsleep(player, id)
                || player.Instance is not null && !MonsterShown(player, id)) continue;
            monsters.Add((id, m.Walk.Position(now), Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127), m.Hostile));
        }
        if (FlickerStrike.Pick(monsters, playerAt, aim, range, clicked) is not { } pick) return null;
        return (pick.Id, pick.At, MovementLanding(player, effect, playerAt, pick.At));
    }

    // A Flicker Strike use refused for want of a monster in range (the client sent it for one it had not yet seen die):
    // nothing is paid, so the mana the client spent when it started the use is given back, as for an attack-move.
    public static Task FlickerRefused(Player player,
        string skill)
    {
        Console.WriteLine($"[gateway] {player.Connection.Remote} {skill}: refused, no monster in range");
        return SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} ({skill} refused, cost back)");
    }

    // Flicker Strike's buff, BuffDefinitions 1344 flicker_strike_speed_boost ("You have increased Movement Speed",
    // StatsKeys base_movement_velocity_+%): each use gives it again for the skill's duration, with the gem's
    // flicker_strike_buff_movement_speed_+%. Network stats run no calculators, so the bonus goes into the movement speed
    // the server sends (the pools, FlickerMovementPct) while the buff lasts, as Shield Charge's does.
    const ushort FlickerSpeedBuffRow = 1344;
    sealed class FlickerSpeed { public int Percent, Token; public ushort Buff; }
    static readonly Store<Player, FlickerSpeed> FlickerSpeeds = new();

    // The running buff's movement bonus, for the pools (PoolsFor); 0 when none runs.
    public static int FlickerMovementPct(Player player) => FlickerSpeeds.TryGetValue(player, out var s) ? s.Percent : 0;

    // After an accepted Flicker Strike use: the buff and the faster movement speed, and both gone when it runs out
    // (a later use's buff takes over).
    public static async Task GrantFlickerSpeed(Player player,
        ushort skillId)
    {
        var own = SupportedSkill(player, skillId).Stats;
        int percent = GolemStat(own, Stat.FlickerStrikeBuffMovementSpeedPlusPct);
        if (percent == 0) return;
        var playerStats = CharacterPlayerStats(player);
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(GolemStat(own, Stat.BaseSkillEffectDuration), Player,
            name => GolemStat(own, name));
        if (seconds <= 0) return;
        var speed = FlickerSpeeds.GetOrCreateValue(player);
        int token;
        token = ++speed.Token;
        // The use's message keeps one stats snapshot for its whole flow (PlayerStatScope), taken before this bonus, and the
        // end below runs on that flow too: both build theirs fresh, or the panel kept the old speed (live 2026-11-09:
        // 123% after the buff had ended, until a charge change sent new stats).
        using var fresh = new PlayerStatScope(player, fresh: true);
        await Send(player, [], $"0x01c1 Flicker Strike buff: +{percent}% movement speed for {seconds:0.##} s",
            atGate: () =>
            {
                if (player.Closed || player.LeavingArea || player.RageRetired || speed.Token != token) return null;
                var bytes = new List<byte>();
                speed.Buff = 1;
                player.Area.Buffs.Set(player.Id, new(FlickerSpeedBuffRow, player.Id, skillId, U68: Buff.Seconds(seconds)));
                if (speed.Percent != percent)
                {
                    speed.Percent = percent;
                    RecomputePoolsAndSpeed(player);
                    bytes.AddRange(PlayerStatsOffFrames(player));
                }
                return [.. bytes];
            });
        SendAfter(player, seconds, async () =>
        {
            using var ended = new PlayerStatScope(player, fresh: true);
            await Send(player, [], "0x01c2 Flicker Strike buff ends", atGate: () =>
            {
                if (player.Closed || player.LeavingArea || player.RageRetired || speed.Token != token || speed.Buff == 0) return null;
                var bytes = new List<byte>();
                player.Area.Buffs.Clear(player.Id, FlickerSpeedBuffRow, player.Id);
                speed.Buff = 0;
                speed.Percent = 0;
                RecomputePoolsAndSpeed(player);
                bytes.AddRange(PlayerStatsOffFrames(player));
                return [.. bytes];
            });
        });
    }
}
