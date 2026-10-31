using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core.Diagnostics;

static partial class CommandsRules
{
    // What MitigateTaken applied, for the dev commands' replies: armour or resistance, and the additional reduction.
    internal static string MitigationLabel(ClientState conn, DamageType type, int amount, int resistance, int maxResistance,
        int effectiveArmour, IReadOnlyDictionary<uint, int> stats)
    {
        var (physical, elemental) = PlayerDamageReduction(conn, stats);
        if (type == DamageType.Physical)
            return $"{DamageModel.ArmourReduction(effectiveArmour, amount)}% armour" + (physical > 0 ? $" + {physical}% physical damage reduction" : "");
        int share = PlayerHitMitigation.ArmourPercent(conn.Pools, type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
        return $"{DamageModel.Capped(resistance, maxResistance)}% resistance" +
            (share > 0
                ? $" + {DamageModel.ArmourReduction((int)((long)effectiveArmour * share / 100), DamageModel.Mitigate(amount, resistance, maxResistance))}% armour"
                : "") +
            (DamageTypes.IsElemental(type) && elemental > 0 ? $" + {elemental}% elemental damage reduction" : "");
    }

    internal const string ChargesUsage = "/charges [endurance|frenzy|power <count>] | /charges add <type> [count] | /charges clear " +
        "(dev: the charges held, their maximum, minimum and time left; set, gain or clear them)";

    // /charges: shows the charges, or sets (no refresh roll), gains (as a skill would: refreshes, can reach the maximum
    // chance) or clears them. The ticker applies the change within a step.
    internal static async Task ChargesCommand(ClientState conn, string[] args)
    {
        var session = ChargesOf(conn);
        if (args.Length == 0)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            string text;
            lock (conn.Monsters)
                text = string.Join("; ", Charges.Types.Select(t =>
                {
                    var l = session.Limits(t);
                    return $"{Charges.Name(t)}{(session.Converted(t) ? $" (as {Charges.SpecialName(t)})" : "")} {session.Effective(t)}/{l.Maximum} (minimum {l.Minimum}, " +
                        $"{l.DurationMs / 1000.0:0.###} s, {session.State.Remaining(t, now):0.0} s left)";
                }));
            await conn.Commands!.Reply($"Charges: {text}.");
            return;
        }
        if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            session.Enqueue(new ChargeEvent.Set(null, 0));
            await conn.Commands!.Reply("Charges cleared.");
            return;
        }
        bool add = args[0].Equals("add", StringComparison.OrdinalIgnoreCase);
        if (add) args = args[1..];
        if (args.Length == 0 || Charges.Parse(args[0]) is not { } type) throw new ArgumentException("endurance, frenzy or power");
        int count = args.Length > 1 ? int.Parse(args[1]) : 1;
        if (count < 0) throw new ArgumentException("a count of 0 or more");
        session.Enqueue(add ? new ChargeEvent.Grant(type, count, "/charges add") : new ChargeEvent.Set(type, count));
        await conn.Commands!.Reply($"{(add ? "Gaining" : "Setting")} {count} {Charges.Name(type)} charge(s).");
    }
}
