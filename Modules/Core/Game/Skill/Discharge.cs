// Discharge (and Discharge of Misery): a spell whose base damage and area grow per removable Endurance, Frenzy and
// Power Charge (SkillHitPlan.ChargeBase, SkillArea), which it spends when it lands. Converted charges (Brutal,
// Affliction, Absorption) are not removable and stay.
// One use's count of its charges, shared by its repeats (Spell Echo) and cascade areas (Spell Cascade): they all deal
// damage for these charges, and the first of them to land spends them, once.
sealed class DischargeUse(IReadOnlyDictionary<ChargeType, int> charges)
{
    int spent;
    public IReadOnlyDictionary<ChargeType, int> Charges { get; } = charges;
    public int Total => Charges.Values.Sum();
    public bool TakeSpend() => Interlocked.Exchange(ref spent, 1) == 0;
}

static partial class Game
{
    // The charges a use can spend, per type: the held ones above the minimum.
    internal static Dictionary<ChargeType, int> RemovableCharges(ClientState conn)
    {
        var removable = new Dictionary<ChargeType, int>();
        lock (conn.Monsters)
        {
            var session = ChargesOf(conn);
            foreach (var type in Charges.Types)
                removable[type] = session.Converted(type) ? 0 : Math.Max(0, session.Effective(type) - session.Limits(type).Minimum);
        }
        return removable;
    }

    // A use of a charge-spending skill counts its charges once (null for other skills).
    internal static DischargeUse? DischargeUseFor(ClientState conn, int effect, int level, int quality) =>
        SkillHitPlan.ForSkill(SkillsFor(conn), effect, level, quality).SpendsCharges ? new(RemovableCharges(conn)) : null;

    // The worn skill effect of the gem's base (one Cosmetics tab slot covers Discharge and its transfigured versions),
    // for the log: the client draws Discharge's art from the skill record's skill_art_variation (0: the default).
    static string DischargeSkin(ClientState conn, uint gem) =>
        CharacterSaves.SkillCosmetics(CharacterName(conn)).Where(kv => CharacterSaves.SkillCosmeticGem(kv.Key) == gem)
            .Select(kv => Mtx.Resolve(kv.Value)).FirstOrDefault(r => r is not null) is { } worn
            ? $"mtx 0x{worn.MtxTypeHash:x8} art variation {SkillArtVariation(conn, gem)}" : "none";

    // A use landed: its first landed sweep spends the charges it counted, unless the gem's quality roll keeps them
    // (discharge_chance_not_to_consume_charges_%). The caster's own charges: only its connection spends them.
    internal static void SpendDischargedCharges(string remote, ClientState conn, string skill, uint gem, DischargeUse use,
        int keepChance, int hit)
    {
        if (!use.TakeSpend()) return;
        bool keep = keepChance > 0 && Random.Shared.Next(100) < keepChance;
        if (!keep)
            foreach (var (type, count) in use.Charges)
                if (count > 0) ChargesOf(conn).Enqueue(new ChargeEvent.Spend(type, count, skill));
        Console.WriteLine($"[gateway] {remote} {skill} discharged {string.Join(", ", use.Charges.Select(d => $"{d.Value} {Charges.Name(d.Key)}"))}"
            + $", hit {hit} monster(s){(keep ? $", charges kept ({keepChance}% chance)" : "")}, skin {DischargeSkin(conn, gem)}");
    }
}
