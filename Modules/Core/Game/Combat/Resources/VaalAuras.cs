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
    sealed record VaalAuraKind(ushort BuffRow, ushort Visual, uint Stat);
    static readonly Dictionary<string, VaalAuraKind> VaalAuras = new()
    {
        ["VaalDiscipline"] = new(160, 329, Stat.EnergyShieldRechargeNotDelayedByDamage),
    };
    internal sealed class VaalAuraState { public int Token; public ushort Buff, Source, Row; public double Until; public uint? Stat; }
    internal static readonly Store<Player, VaalAuraState> VaalAuraStates = new();

    static double VaalAuraSeconds(int baseMilliseconds, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds, n => GolemStat(player, n), n => GolemStat(own, n))
        * Math.Max(0, 1 + ((long)GolemStat(player, Stat.BuffDurationPlusPct) + GolemStat(own, Stat.BuffDurationPlusPct)) / 100d);

    static void WithVaalAura(Player player, StatMap stats)
    {
        if (!VaalAuraStates.TryGetValue(player, out var aura)) return;
        double now = AreaTime(player);
        if (player.PlayerLife > 0 && aura.Stat is uint key && aura.Until > now)
        {
            stats[key] = 1;
            StatsChangeIn(aura.Until - now);
        }
    }

    static async Task<bool> ActivateVaalAura(Player player,
        ushort skillId, Func<bool> current)
    {
        var gem = SupportedSkill(player, skillId);
        string name = SkillsFor(player).EffectId(gem.Effect) ?? "";
        if (!VaalAuras.TryGetValue(name, out var kind)) return false;
        double seconds = VaalAuraSeconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration), CharacterPlayerStats(player), gem.Stats);
        if (seconds <= 0) return false;
        var aura = VaalAuraStates.GetOrCreateValue(player);
        int token;
        token = ++aura.Token;
        // Built fresh, as Flicker Strike's buff: the use's message holds a stats snapshot from before the buff.
        using var fresh = new PlayerStatScope(player, fresh: true);
        await Send(player, [], $"0x01c1 {name}: {Stats.Name(kind.Stat)} for {seconds:0.##} s (buff row {kind.BuffRow})",
            still: current, atGate: () =>
            {
                if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || aura.Token != token) return null;
                double now = AreaTime(player);
                var bytes = new List<byte>();
                // A use while the last one still runs replaces it (a new countdown).
                if (aura.Buff != 0) ClearVaalAuraBuffs(player, aura);
                aura.Source = aura.Buff = 1;
                aura.Row = kind.BuffRow;
                aura.Until = now + seconds;
                aura.Stat = kind.Stat;
                // As the reservation auras (AuraCatalog.Buffs): the source draws the aura, the received buff the HUD icon,
                // here with its duration.
                player.Area.Buffs.Set(player.Id, new(kind.BuffRow, player.Id, skillId, AuraSource: true, DisplayState: 0,
                    Visual: kind.Visual, Tag: (ushort)BuffTag.VaalAura));
                player.Area.Buffs.Set(player.Id, new(kind.BuffRow, player.Id, skillId, U68: Buff.Seconds(seconds),
                    Visual: kind.Visual, Tag: (ushort)BuffTag.VaalAura));
                bytes.AddRange(PlayerStatsOffFrames(player));
                return [.. bytes];
            });
        return true;
    }

    internal static byte[]? ExpireVaalAura(Player player, double now)
    {
        if (!VaalAuraStates.TryGetValue(player, out var aura) || aura.Buff == 0
            || player.Paused || player.PlayerLife > 0 && now < aura.Until) return null;
        ClearVaalAuraBuffs(player, aura);
        aura.Buff = aura.Source = 0;
        aura.Until = 0;
        aura.Stat = null;
        ++aura.Token;
        return [];
    }

    static void ClearVaalAuraBuffs(Player player, VaalAuraState aura)
    {
        player.Area.Buffs.Clear(player.Id, aura.Row, player.Id, auraSource: true, tag: (ushort)BuffTag.VaalAura);
        player.Area.Buffs.Clear(player.Id, aura.Row, player.Id, tag: (ushort)BuffTag.VaalAura);
    }

    static async Task SendVaalAuraExpiry(Player player)
    {
        if (!VaalAuraStates.TryGetValue(player, out var aura) || aura.Buff == 0) return;
        using var fresh = new PlayerStatScope(player, fresh: true);
        await Send(player, [], "Vaal aura expired", atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired
                || ExpireVaalAura(player, AreaTime(player)) is not { } bytes) return null;
            return [.. bytes, .. PlayerStatsOffFrames(player)];
        });
    }
}
