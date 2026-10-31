using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// The Spirit chieftain skills that Ancestral Commander's Ancestral Spirit options trigger (Summon Spirit of Kahuturoa,
/// Utula, Kiloava, Rakiata and Kaom, Agents/docs/game/ancestral-commander.md): one cooldown that the five share
/// (GrantedEffectsPerLevel CooldownGroup 24, 20 s, one use) and the Leadership buff the summoned chieftain gives the
/// player for the skill's duration, while the immortal chieftain it summons fights beside him (PlayerMinions,
/// Game.MinionAttacks). The buff's <c>ancestral_buff_cooldown_does_not_tick</c> stops that cooldown while it lasts.
/// Guarded by the Monsters lock; times are the connection's game seconds.
/// </summary>
sealed class SpiritChieftains
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

    static readonly Lazy<(ushort Row, uint[] Values, uint[] Stats, int? Minion)[]> Templates = new(() => Load("data/tables"));

    static (ushort Row, uint[] Values, uint[] Stats, int? Minion)[] Load(string tables)
    {
        string Path(string name) => System.IO.Path.Combine(tables, name + ".json");
        if (!File.Exists(Path("BuffTemplates")) || !File.Exists(Path("BuffDefinitions"))) return [];
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Path("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path("BuffDefinitions")));
        var minions = new Dictionary<int, int>();
        if (File.Exists(Path("AncestralEmbraceVariations")))
        {
            using var embraces = JsonDocument.Parse(File.ReadAllBytes(Path("AncestralEmbraceVariations")));
            foreach (var e in embraces.RootElement.EnumerateArray())
                if (e.TryGetProperty("BuffTemplate", out var t) && t.ValueKind == JsonValueKind.Number
                    && e.TryGetProperty("Minion", out var m) && m.ValueKind == JsonValueKind.Number)
                    minions.TryAdd(t.GetInt32(), m.GetInt32());
        }
        static uint[] Keys(JsonElement row, string column) => row.TryGetProperty(column, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => (uint)e.GetInt32() + 1)] : [];
        var result = new List<(ushort, uint[], uint[], int?)>();
        int index = 0;
        foreach (var template in templates.RootElement.EnumerateArray())
        {
            int row = index++;
            if (template.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) continue;
            uint[] values = Keys(template, "StatsKey"), stats = Keys(definitions.RootElement[key.GetInt32()], "StatsKeys");
            if (values.Length > 0 && values.Length == stats.Length)
                result.Add(((ushort)key.GetInt32(), values, stats, minions.TryGetValue(row, out int minion) ? minion : null));
        }
        return [.. result];
    }
}
