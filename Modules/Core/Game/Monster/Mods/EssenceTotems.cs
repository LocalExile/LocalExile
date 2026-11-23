using GameServer.Core;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Ground-effect essence totems whose primary spell has no hit damage (dropped by <see cref="BossKits.SummonKit"/>):
/// Sorrow's Vortex totems (<c>EssenceTotemSummonVortex</c> → <c>EssenceTotemCastVortexGroundEffect</c>, GroundEffects 164
/// <c>sml_maelstrom.ao</c>, cold DoT + chill at the player) and Spite's shocked-ground totem
/// (<c>EssenceTotemSummonGroundEffectShockedGround</c> → <c>EssenceTotemCastGroundEffectShockedGround</c>, GroundEffects 47
/// <c>lightning_ground_01.ao</c>, shocked ground alongside its kit's Arc).</summary>
public static partial class MapEssences
{
    public const string SorrowVortexTotem = "Metadata/Monsters/Totems/EssenceTotemSummonVortex";
    public const string SpiteShockedTotem = "Metadata/Monsters/Totems/EssenceTotemSummonGroundEffectShockedGround";
    const string SorrowVortexArt = "Metadata/Effects/Spells/frost_bolt/sml_maelstrom.ao";
    const string SpiteShockedGroundArt = "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao";

    /// <summary>Sorrow (4) and Spite (14) are the two essence types whose summoned totems cast ground effects.</summary>
    static readonly HashSet<int> GroundTotemTypes = [4, 14];

    sealed class TotemCastState { public double NextCast; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, TotemCastState> TotemCasts = new();

    static void TickTotems(Player conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        var rares = new HashSet<uint>();
        foreach (var mono in area.Monoliths)
        {
            if (mono.Stage < 4 || mono.Monster == 0) continue;
            foreach (int row in mono.Essences)
                if (MapEssenceCatalog.Find(row) is { } e && GroundTotemTypes.Contains(e.Type)) { rares.Add(mono.Monster); break; }
        }
        if (rares.Count == 0) return;
        SpawnedMonster[] totems = [.. conn.Area.Monsters.Values.Where(m => !m.Dead && m.Life > 0 && m.Hostile
            && m.SummonerId is uint s && rares.Contains(s)
            && m.Variety?.Id is SorrowVortexTotem or SpiteShockedTotem)];
        if (totems.Length == 0) return;
        double now = Now;
        double areaTime = AreaTime(conn);
        var catalog = SkillsFor(conn);
        foreach (var totem in totems)
        {
            var state = TotemCasts.GetOrCreateValue(totem);
            if (now < state.NextCast) continue;
            var from = totem.Walk.Position(areaTime);
            var target = Members(conn).Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && !c.Immune
                && Dist(c.Walk.Position(AreaTime(c)), from) <= 60)
                .OrderBy(c => Dist(c.Walk.Position(AreaTime(c)), from)).FirstOrDefault();
            if (target is null) continue;
            var aim = target.Walk.Position(AreaTime(target));
            if (totem.Variety?.Id == SorrowVortexTotem)
            {
                state.NextCast = now + 3.0;
                int effect = catalog.EffectRow("EssenceTotemCastVortexGroundEffect");
                var dot = EssenceSkillDot(conn, effect, totem.Level);
                AddGroundZone(conn, area, aim, SorrowVortexArt, radius: 15, duration: 4.5, dot,
                    PlayerElementalAilments.Kind.Chill, 20, scale: 1.3f);
            }
            else
            {
                state.NextCast = now + 3.5;
                AddGroundZone(conn, area, aim, SpiteShockedGroundArt, radius: 14, duration: 5.0, DamagePacket.Empty,
                    PlayerElementalAilments.Kind.Shock, 15, scale: 1.2f);
            }
        }
    }
}