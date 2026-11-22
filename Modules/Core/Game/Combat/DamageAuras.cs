using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// A damage-over-time aura: a zone (AreaZones) around its owner that marks each hostile monster inside with a buff and
/// deals it damage over time by the elapsed game time, every frame. Righteous Fire is one; any burning or degenerating
/// radius around a player is another. The zone keeps who is inside (from the walkers' moves); the aura sends the marks
/// as they come and go and the damage of each frame.
/// </summary>
public sealed class DamageAura
{
    /// <summary>The aura's name in the log ("burnt to death by ...").</summary>
    public required string Name { get; init; }
    /// <summary>The radius in cells (it may change while the aura runs).</summary>
    public required Func<int> Radius { get; init; }
    /// <summary>The buff each monster inside wears: its row, the granting skill and the visual (null: the row's own).</summary>
    public required ushort BuffRow { get; init; }
    public required ushort Skill { get; init; }
    public ushort? Visual { get; init; }
    /// <summary>The marks on the monsters inside: monster id → its buff id. The owner's end removes them.</summary>
    public required Dictionary<uint, ushort> Marks { get; init; }
    /// <summary>Whether the aura still runs (in the area's frame); once false the zone goes.</summary>
    public required Func<bool> Running { get; init; }
    /// <summary>In the area's frame, before a frame's damage: false skips it (the owner may retire the aura here).</summary>
    public Func<bool> Valid { get; init; } = () => true;
    /// <summary>Whether it deals damage this frame (the owner is alive, out of the grace period, ...).</summary>
    public Func<bool> Active { get; init; } = () => true;
    /// <summary>The damage over <paramref name="seconds"/> and the owner's stats it was made with, in the area's frame.</summary>
    public required Func<double, (DamageOverTime Damage, Dictionary<uint, int> Stats)> Damage { get; init; }

    internal readonly List<(uint Id, SpawnedMonster M, ushort Buff)> Entered = [];
    internal readonly List<(uint Id, ushort Buff)> Left = [];
}

public static partial class Game
{
    /// <summary>Starts a damage aura around the player: its zone in the area and its step every frame while it runs.</summary>
    static void StartDamageAura(Player player, DamageAura aura)
    {
        var zone = new Zone
        {
            Follows = new EntityId(player.Id),
            Radius = aura.Radius,
            Reach = ReachesHostileMonsters(player),
            Entered = id =>
            {
                if (!aura.Running() || aura.Marks.ContainsKey(id.Value) || player.Area.Monsters.GetValueOrDefault(id.Value) is not { } m) return;
                aura.Marks[id.Value] = player.NextBuff;
                aura.Entered.Add((id.Value, m, player.NextBuff++));
            },
            Left = id => { if (aura.Marks.Remove(id.Value, out ushort buff)) aura.Left.Add((id.Value, buff)); },
        };
        InArea(player, a =>
        {
            a.Zones.Add(zone, CombatLifetime);
            EveryFrameAsPlayer(player, () =>
            {
                if (aura.Running()) return true;
                a.Zones.Remove(zone);
                return false;
            }, player => StepDamageAura(player, aura, zone, FrameSeconds));
        });
    }

    // One frame of a damage aura: the marks that came and went, then the damage to the monsters inside.
    static async Task StepDamageAura(Player player,
        DamageAura aura, Zone zone, double seconds)
    {
        if (!CurrentGameWork) return;
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        List<(uint Id, SpawnedMonster M, ushort Buff)> entered;
        List<(uint Id, ushort Buff)> left;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || !aura.Valid()) return;
            entered = [.. aura.Entered];
            aura.Entered.Clear();
            if (aura.Active())
            {
                double now = ProjectileWorldTime(player);
                var (damageOverTime, stats) = aura.Damage(seconds);
                foreach (var id in zone.Inside)
                {
                    if (!aura.Marks.ContainsKey(id.Value) || player.Area.Monsters.GetValueOrDefault(id.Value) is not { Dead: false, Life: > 0 } m) continue;
                    var (damage, carry) = MonsterBurn(m, damageOverTime, seconds, now);   // its resistance, its shock
                    m.Burn = carry;
                    if (damage == 0) continue;
                    (m.Life, bool stageStarts) = TakeOwnerDamageLocked(player, m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        CollectVaalSoulAtDeathLocked(player, m);
                        ShockOnKillLocked(player, m, stats, now);
                        if (aura.Marks.Remove(id.Value, out ushort b)) aura.Left.Add((id.Value, b));
                        TriggerElementalRelicLocked(player, m, hit: false, killed: true);   // damage over time is no hit
                    }
                    hits.Add((id.Value, m, m.Life, damage));
                    if (stageStarts) CommitBossStage(player, id.Value, m, now);
                }
            }
            left = [.. aura.Left];
            aura.Left.Clear();
        }
        foreach (var (id, m, buff) in entered)
            if (aura.Running() && player.Area.Monsters.GetValueOrDefault(id) is { } current && ReferenceEquals(current, m)
                && !current.Dead && current.Life > 0 && aura.Marks.TryGetValue(id, out ushort currentBuff) && currentBuff == buff)
                player.Area.Buffs.Set(id, new(aura.BuffRow, player.Id, aura.Skill, Visual: aura.Visual));
        foreach (var (id, _) in left) player.Area.Buffs.Clear(id, aura.BuffRow, player.Id);
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            Note(life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) burnt to death by {aura.Name}" : null);
            if (life == 0) await MonsterKilled(player, id, m, byHit: false);
        }
    }
}
