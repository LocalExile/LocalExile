using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core;

/// <summary>
/// The Spirit chieftain skills that Ancestral Commander's Ancestral Spirit options trigger (Summon Spirit of Kahuturoa,
/// Utula, Kiloava, Rakiata and Kaom, Agents/docs/game/ancestral-commander.md): one cooldown that the five share
/// (GrantedEffectsPerLevel CooldownGroup 24, 20 s, one use) and the Leadership buff the summoned chieftain gives the
/// player for the skill's duration, while the immortal chieftain it summons fights beside him (PlayerMinions,
/// Game.MinionAttacks). The buff's <c>ancestral_buff_cooldown_does_not_tick</c> stops that cooldown while it lasts.
/// Guarded by the Monsters lock; times are the connection's game seconds.
/// </summary>
public sealed class SpiritChieftains
{
    /// <summary>A Leadership buff: its BuffDefinitions row, its stats by client key and when it ends.</summary>
    public sealed record Leadership(ushort Row, IReadOnlyList<(uint Stat, int Value)> Stats, double Until);

    /// <summary>A chieftain to summon (Program.SpiritChieftainMinion): the skill, its MonsterVarieties row, the minion's
    /// level and until when it lives.</summary>
    public sealed record Summon(string Skill, int Variety, byte Level, double Until);

    SingleUseCooldownState? cooldown;
    Leadership? buff;
    /// <summary>The summon a trigger queued; the next chieftain step makes it.</summary>
    public Summon? Pending;
    /// <summary>The buff the client was last sent and its instance (Program.SpiritChieftainPresentation).</summary>
    public Leadership? Sent;
    public ushort SentInstance;

    public Leadership? Active(double now) => buff is { } b && now < b.Until ? b : null;

    /// <summary>A trigger: true when the shared cooldown was ready, which it then spends, and the buff starts. The cooldown
    /// recovers at the plan's rate except while a buff lasts.</summary>
    public bool TryTrigger(double now, SingleUseCooldown plan, ushort row, IReadOnlyList<(uint Stat, int Value)> stats, double seconds)
    {
        Settle(now, plan.RecoveryRate);
        cooldown ??= new(now, plan.RecoveryRate);
        // While a buff lasts the cooldown is frozen, so it is not ready; TryUse would also reset its rate.
        if (Active(now) is not null || !cooldown.TryUse(now, plan)) return false;
        buff = new(row, stats, now + seconds);
        cooldown.Refresh(now, 0);
        return true;
    }

    /// <summary>The buff ends now (the player died: the chieftain dies with him); the cooldown recovers from here.</summary>
    public void End(double now)
    {
        if (buff is { } b && b.Until > now) buff = b with { Until = now };
    }

    void Settle(double now, double rate)
    {
        if (cooldown is null) return;
        if (buff is { } b && b.Until <= now) { cooldown.Refresh(b.Until, rate); buff = null; }
        cooldown.Refresh(now, buff is null ? rate : 0);
    }

    /// <summary>The Leadership a Spirit skill's summon grants: the BuffTemplates row whose value stats (StatsKey) the skill
    /// has (Kahuturoa's <c>ancestral_buff_armour_%_applies_to_all_damage_from_hits</c> 10 gives RongokuraisEmbrace), each of
    /// its BuffDefinitions StatsKeys taking the skill's value of the matching StatsKey stat, and the chieftain minion that
    /// carries it (AncestralEmbraceVariations: the row with that BuffTemplate, its Minion; null without one). Null when
    /// none.</summary>
    public static (ushort Row, IReadOnlyList<(uint Stat, int Value)> Stats, int? Minion)? LeadershipOf(IReadOnlyDictionary<uint, int> own)
    {
        foreach (var (row, values, stats, minion) in Templates.Value)
            if (values.All(own.ContainsKey))
                return (row, [.. stats.Zip(values, (stat, value) => (stat, own[value]))], minion);
        return null;
    }

    static readonly Lazy<(ushort Row, uint[] Values, uint[] Stats, int? Minion)[]> Templates = new(Load);

    static (ushort Row, uint[] Values, uint[] Stats, int? Minion)[] Load()
    {
        if (!ClientTables.Has("BuffTemplates") || !ClientTables.Has("BuffDefinitions")) return [];
        var definitions = ClientTables.Table("BuffDefinitions");
        var minions = new Dictionary<int, int>();
        foreach (var e in (IEnumerable<ClientRow>?)ClientTables.TableOrNull("AncestralEmbraceVariations") ?? [])
            if (e.IntOrNull("BuffTemplate") is int t && e.IntOrNull("Minion") is int m)
                minions.TryAdd(t, m);
        static uint[] Keys(ClientRow row, string column) => [.. row.Ints(column).Select(e => (uint)e + 1)];
        var result = new List<(ushort, uint[], uint[], int?)>();
        foreach (var template in ClientTables.Table("BuffTemplates"))
        {
            int row = template.Index;
            if (template.IntOrNull("BuffDefinitionsKey") is not int key) continue;
            uint[] values = Keys(template, "StatsKey"), stats = Keys(definitions[key], "StatsKeys");
            if (values.Length > 0 && values.Length == stats.Length)
                result.Add(((ushort)key, values, stats, minions.TryGetValue(row, out int minion) ? minion : null));
        }
        return [.. result];
    }
}
