using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

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
    internal static bool SpendDischargedCharges(string remote, ClientState conn, string skill, uint gem, DischargeUse use,
        int keepChance, int hit)
    {
        if (!use.TakeSpend()) return false;
        bool keep = keepChance > 0 && Random.Shared.Next(100) < keepChance;
        if (!keep)
            foreach (var (type, count) in use.Charges)
                if (count > 0) ChargesOf(conn).Enqueue(new ChargeEvent.Spend(type, count, skill));
        Console.WriteLine($"[gateway] {remote} {skill} discharged {string.Join(", ", use.Charges.Select(d => $"{d.Value} {Charges.Name(d.Key)}"))}"
            + $", hit {hit} monster(s){(keep ? $", charges kept ({keepChance}% chance)" : "")}, skin {DischargeSkin(conn, gem)}");
        return true;
    }

    // MtxTypes HASH32 of the two Discharge skins (MicrotransactionFlareDischarge, MicrotransactionCelestialDischargeEffect).
    const uint FlareDischargeHash = 0xefeb283a, CelestialDischargeHash = 0xe393e8e7;

    // A triggered Discharge (Cast On Critical Strike) runs its action without the cast clip, and the clip is what draws
    // the explosion: the server draws it instead (MiscAnimated DischargeFire/Cold/Lightning and the skins' rows), one
    // per charge type spent, at the area centre and scaled to the radius. A cast by hand draws its own.
    internal static async Task ShowTriggeredDischarge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint gem, DischargeUse use, (int X, int Y) at, int radius)
    {
        uint skin = CharacterSaves.SkillCosmetics(CharacterName(conn)).Where(kv => CharacterSaves.SkillCosmeticGem(kv.Key) == gem)
            .Select(kv => Mtx.Resolve(kv.Value)?.MtxTypeHash ?? 0).FirstOrDefault(h => h != 0);
        var arts = new List<string>();
        foreach (var (type, count) in use.Charges)
            if (count > 0) arts.AddRange(DischargeArt(type, skin));
        var ids = new List<uint>();
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            foreach (var _ in arts) ids.Add(conn.NextObjectId++);
        }
        for (int i = 0; i < arts.Count; i++)
        {
            // Native area art radii are world units; the area radius is in grid cells. The cold and lightning cores share
            // the fire core's 200 (only its row carries one); art with no radius plays at its own size.
            int? native = Art.ArtRadius(arts[i]) ?? (arts[i].StartsWith(DischargeCore, StringComparison.Ordinal) ? 200 : null);
            float scale = native is int n and > 0 ? (float)(radius * (250d / 23) / n) : 1;
            await Send(remote, stream, ciphers, CreateEffectObject(ids[i], new EffectObject(at.X, at.Y, arts[i]) { Scale = scale }),
                $"0x02bb triggered discharge effect 0x{ids[i]:x} {arts[i]} at ({at.X}, {at.Y}), scale {scale:0.###}");
        }
        Console.WriteLine($"[gateway] {remote} triggered discharge art: {arts.Count} effect(s), skin 0x{skin:x8}, radius {radius}");
        var created = ids.ToArray();
        SendAfter(TriggeredDischargeArtSeconds, async () =>
        {
            if (conn.Closed || conn.LeavingArea) return;
            foreach (uint id in created) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
        });
    }

    const double TriggeredDischargeArtSeconds = 2;
    const string DischargeCore = "Metadata/Effects/Spells/discharge_03/discharge_core";

    // The art of one charge type's explosion: lightning for Power, fire for Endurance, cold for Frenzy, in the worn skin.
    static IEnumerable<string> DischargeArt(ChargeType type, uint skin)
    {
        const string fx = "Metadata/Effects/Spells/discharge_03/", mtx = "Metadata/Effects/Microtransactions/Spells/discharge/";
        string element = type switch { ChargeType.Power => "lightning", ChargeType.Endurance => "fire", _ => "cold" };
        if (skin == CelestialDischargeHash)
            return [$"{mtx}celestial2/{element}_discharge_core.ao"];
        if (skin == FlareDischargeHash)
        {
            string folder = element == "cold" ? "ice" : element;
            return [$"{mtx}solar_flare/{folder}/solarflare_{folder}.ao", $"{mtx}solar_flare/{folder}/dischargeshockwave.ao"];
        }
        return element switch
        {
            "fire" => [$"{fx}discharge_core.ao", $"{fx}discharge_wave.ao"],
            "cold" => [$"{fx}discharge_core_ice.ao", $"{fx}ice/discharge_ice_wave.ao"],
            _ => [$"{fx}discharge_core_lightning.ao", $"{fx}lightning/discharge_lightning_wave.ao"],
        };
    }
}
