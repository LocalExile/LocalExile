using GameServer.Core;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Insanity's Storm Call (<c>EssenceLightningBeacon</c>) and invulnerable Spectral Spirits
/// (<c>SpectralSummonedSkull</c>, whose <c>.ot</c> sets <c>base_cannot_be_damaged = 1</c>): places 3 delayed Storm Call
/// markers around the target, pulses halfway, then strikes each marker for level-scaled lightning damage.</summary>
public static partial class MapEssences
{
    public const string EssenceSpectralSkull = "Metadata/Monsters/SummonedSkull/SpectralSummonedSkull";
    const string StormCallIdleArt = "Metadata/Effects/Spells/monsters_effects/storm_call/stormcall_idle.ao";
    const string StormCallPulseArt = "Metadata/Effects/Spells/monsters_effects/storm_call/stormpulse.ao";
    const string StormCallImpactArt = "Metadata/Effects/Spells/monsters_effects/storm_call/stormcall_impact.ao";

    static async Task CastLightningBeacons(Player conn, SpawnedMonster monster, Player target, SpecialSpell spell)
    {
        var catalog = SkillsFor(conn);
        var native = GolemData.Skill(catalog, spell.Pulse, monster.Level);
        int Get(uint key) => native.Stats.GetValueOrDefault(key);
        var damage = EssenceSkillDamage(conn, spell.Pulse, monster.Level);
        if (damage.IsZero) return;
        int count = ScaledSpellCount(spell.Pulse, spell.Tier, Math.Max(1, Get(Stat.NumberOfBeacons)));
        int scatter = Math.Max(8, Get(Stat.BeaconPlacementRadius));
        double duration = Math.Max(0.8, Get(Stat.BaseSkillEffectDuration) / 1000.0);
        int radius = Math.Max(9, SkillArea.Radius(18, Get, true));
        var aim = target.Walk.Position(AreaTime(target));
        var markers = new List<((int X, int Y) Spot, uint Idle)>(count);
        for (int i = 0; i < count; i++)
        {
            (int X, int Y) spot = aim;
            if (i > 0)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    double angle = Random.Shared.NextDouble() * Math.Tau;
                    double d = 6 + Random.Shared.NextDouble() * Math.Max(4, scatter - 6);
                    var want = conn.Layout.Near((aim.X + (int)Math.Round(Math.Cos(angle) * d), aim.Y + (int)Math.Round(Math.Sin(angle) * d)));
                    if (markers.All(m => Dist(m.Spot, want) >= 5)) { spot = want; break; }
                }
            }
            markers.Add((spot, SpawnEffect(conn, spot, StormCallIdleArt)));
        }
        await AreaDelay(conn, duration * 0.5);
        if (!conn.Closed && !conn.LeavingArea)
        {
            foreach (var (spot, _) in markers)
            {
                uint pulse = SpawnEffect(conn, spot, StormCallPulseArt);
                _ = RemovePulseLater(conn, pulse, duration * 0.5);
            }
            await AreaDelay(conn, duration * 0.5);
        }
        foreach (var (_, idle) in markers) RemoveEffect(conn, idle);
        if (conn.Closed || conn.LeavingArea) return;
        foreach (var (spot, _) in markers)
        {
            uint impact = SpawnEffect(conn, spot, StormCallImpactArt);
            _ = RemovePulseLater(conn, impact, 0.9);
            await EssenceSpellHit(conn, monster, spell.Pulse, damage, radius, spot);
        }
        Console.WriteLine($"[map-content] essence insanity storm call: {markers.Count} strikes by 0x{monster.ObjectId:x} around ({aim.X}, {aim.Y})");
    }
}