static partial class Game
{
    /// <summary>
    /// The player's totems as a test can see them, one line each: id, variety, level, life/maximum life, cell, seconds
    /// left and the skill each carries. They live in <c>ClientState.Totems</c> and never in
    /// <c>ClientState.Monsters</c> — that is the point of the separate collection (area hits, monster AI, loot,
    /// experience and the instance save must not see them) — so <c>/mobs</c> and the <c>/state</c> <c>monsters</c>
    /// array cannot list one and this is how "was the totem summoned at all?" is answered. Read-only; the Monsters lock
    /// guards the collection and the connection clock, as everywhere else that reads them.
    /// </summary>
    internal static string TotemsListing(ClientState conn)
    {
        var lines = new List<string>();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var totem in conn.Totems.Living(now).OrderBy(t => t.Id))
            {
                string variety = totem.VarietyId.Length == 0 ? "-" : totem.VarietyId;
                double left = Math.Max(0, totem.Until - now);
                lines.Add($"0x{totem.Id:x} {variety}#{totem.Row} lvl {totem.Level} life {totem.Life}/{totem.MaxLife} " +
                    $"at ({totem.At.X},{totem.At.Y}) left {left:0.0}s skill {totem.SkillId} " +
                    $"({TotemSkillLabel(conn, totem.EffectRow)}) {TotemEffect(totem)}");
            }
        }
        if (lines.Count == 0) return "No totems.";
        lines.Add($"{lines.Count} totem(s).");
        return string.Join('\n', lines);
    }

    /// <summary>What a totem does with its skill, for the /totems line: it flies its spell through the projectile
    /// runtime, or it makes the contact its captured hit describes, or it eats corpses, or it carries an aura, or it
    /// only places. <c>Effect &gt;= 0</c> is the executor's own marker and is mutually exclusive with <c>Strike</c>.</summary>
    static string TotemEffect(TotemState.Totem totem) => totem.Effect >= 0
        ? totem.Ballista ? "native ballista (flies)" : "adopted projectile spell (flies)"
        : totem.Strike is { } strike
            ? $"{(strike.Spell ? "spell" : "attack")} r{strike.Radius} every {strike.Interval:0.##}s"
            : totem.Devour is { } devour ? $"devours {devour.Life} life/{devour.Mana} mana a corpse"
            : totem.Aura is { } aura ? $"aura {aura.Row} r{aura.Radius}"
            : "no effect of its own";

    /// <summary>The name the skill a totem carries goes into the /totems line under: the GrantedEffects id of the row
    /// the totem was summoned with, else its row number. It is read from the effect stored on the totem rather than
    /// looked up among the connection's gems: a totem outlives the socket it came from and the gem list is the
    /// player's, and an adopted projectile spell's gem may not survive the summon at all.</summary>
    internal static string TotemSkillLabel(ClientState conn, int effectRow) =>
        effectRow < 0 ? "totem" : SkillsFor(conn).EffectId(effectRow) ?? $"effect {effectRow}";
}
