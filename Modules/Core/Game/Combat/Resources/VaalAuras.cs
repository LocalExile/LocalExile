using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // A Vaal aura is an instant, soul-paid temporary aura. VaalUses admits and pays the use (VaalExecutor); this puts its
    // buff on the player for the skill's duration (skill effect and buff duration modifiers) and adds the buff's stat to
    // the player's stats while it runs (WithVaalAura). Nearby allies (minions) do not get it yet.
    // BuffDefinitions 160 vaal_aura_energy_shield "Vaal Discipline Aura": energy_shield_recharge_not_delayed_by_damage,
    // which EnergyShield reads.
    // Visual: the BuffDefinitions row's BuffVisuals row, sent explicitly (bit4) as Righteous Fire's buffs do.
    sealed record VaalAuraKind(ushort BuffRow, ushort Visual, string Stat);
    static readonly Dictionary<string, VaalAuraKind> VaalAuras = new()
    {
        ["VaalDiscipline"] = new(160, 329, "energy_shield_recharge_not_delayed_by_damage"),
    };
    internal sealed class VaalAuraState { public int Token; public ushort Buff, Source; public double Until; public string? Stat; }
    internal static readonly Store<ClientState, VaalAuraState> VaalAuraStates = new();

    static double VaalAuraSeconds(int baseMilliseconds, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds, n => GolemStat(player, n), n => GolemStat(own, n))
        * Math.Max(0, 1 + ((long)GolemStat(player, "buff_duration_+%") + GolemStat(own, "buff_duration_+%")) / 100d);

    static void WithVaalAura(ClientState conn, StatMap stats)
    {
        if (!VaalAuraStates.TryGetValue(conn, out var aura)) return;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.PlayerLife > 0 && aura.Stat is string name && aura.Until > now && Stats.Key(name) is uint key)
            {
                stats[key] = 1;
                StatsChangeIn(aura.Until - now);
            }
        }
    }

    static async Task<bool> ActivateVaalAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, Func<bool> current)
    {
        var gem = SupportedSkill(conn, skillId);
        string name = SkillsFor(conn).EffectId(gem.Effect) ?? "";
        if (!VaalAuras.TryGetValue(name, out var kind)) return false;
        double seconds = VaalAuraSeconds(GolemStat(gem.Stats, "base_skill_effect_duration"), CharacterPlayerStats(conn), gem.Stats);
        if (seconds <= 0) return false;
        var aura = VaalAuraStates.GetOrCreateValue(conn);
        int token;
        lock (conn.Monsters) token = ++aura.Token;
        // Built fresh, as Flicker Strike's buff: the use's message holds a stats snapshot from before the buff.
        using var fresh = new PlayerStatScope(conn, fresh: true);
        await Send(remote, stream, ciphers, [], $"0x01c1 {name}: {kind.Stat} for {seconds:0.##} s (buff row {kind.BuffRow})",
            still: current, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || aura.Token != token) return null;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    var bytes = new List<byte>();
                    // A use while the last one still runs replaces it.
                    if (aura.Buff != 0 || aura.Source != 0)
                    {
                        if (aura.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, aura.Buff));
                        if (aura.Source != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, aura.Source));
                    }
                    aura.Source = conn.NextBuff++;
                    aura.Buff = conn.NextBuff++;
                    aura.Until = now + seconds;
                    aura.Stat = kind.Stat;
                    // As the reservation auras (AuraCatalog.StartPackets): the source draws the aura, the received buff
                    // the HUD icon, here with its duration.
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, aura.Source, kind.BuffRow, PlayerId, skillId,
                        auraSource: true, displayState: 0, visual: kind.Visual));
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, aura.Buff, kind.BuffRow, PlayerId, skillId,
                        u68: BitConverter.SingleToUInt32Bits((float)seconds), visual: kind.Visual));
                    bytes.AddRange(PlayerStatsOffFrames(conn));
                    return [.. bytes];
                }
            });
        return true;
    }

    internal static byte[]? ExpireVaalAura(ClientState conn, double now)
    {
        lock (conn.Monsters)
        {
            if (!VaalAuraStates.TryGetValue(conn, out var aura) || aura.Buff == 0
                || conn.Paused || conn.PlayerLife > 0 && now < aura.Until) return null;
            var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, aura.Buff));
            if (aura.Source != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, aura.Source));
            aura.Buff = aura.Source = 0;
            aura.Until = 0;
            aura.Stat = null;
            ++aura.Token;
            return [.. bytes];
        }
    }

    static async Task SendVaalAuraExpiry(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!VaalAuraStates.TryGetValue(conn, out var aura) || aura.Buff == 0) return;
        using var fresh = new PlayerStatScope(conn, fresh: true);
        await Send(remote, stream, ciphers, [], "Vaal aura expired", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired
                    || ExpireVaalAura(conn, AreaTime(conn)) is not { } bytes) return null;
                return [.. bytes, .. PlayerStatsOffFrames(conn)];
            }
        });
    }
}
