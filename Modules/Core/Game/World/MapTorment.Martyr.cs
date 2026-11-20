using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapTorment
{
    sealed record MartyrViewer(ClientState Player, bool Awake, MonsterHitOutcome? Hit);
    sealed record MartyrDeath(uint Id, byte[] Skill, byte[] Action, List<MartyrViewer> Viewers);

    // SuicideExplosion's ordinary 3.29.3c contact uses 15 cells times the final skill radius factor.
    // Its candidate test adds the target's signed Positioned radius and includes the outermost cell.
    static bool InMartyrBlast((int X, int Y) from, (int X, int Y) target, int radius, int body)
    {
        long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y, reach = (long)radius + body;
        return dx * dx + dy * dy <= reach * reach;
    }

    // Called at the zero-life transition, under this instance's Monsters lock. Walking or changing an aura before
    // the next Torment tick cannot change who took the hit. The tick only delivers the committed presentation.
    internal static void MartyrDiedLocked(ClientState conn, SpawnedMonster monster)
    {
        uint id = monster.ObjectId;
        if (id == 0 || !monster.Hostile || monster.CorpseConsumed || TormentCatalog.MartyrGrantRow(conn, monster.Mods) is not int row) return;
        var origin = conn.Instance.World.MonsterOrigins[id].Object;
        var skill = origin.Skills!.Single(s => s.EffectsPerLevelRow == row);
        var source = monster.Effects;
        int radiusPct = SkillArea.RadiusPercent(name => source[name], spell: true);
        int radius = (int)(15L * (100L + radiusPct) / 100);
        uint radiusKey = Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value;
        uint fireKey = Stats.Key("suicide_explosion_%_of_default_monster_life_to_deal_as_fire")!.Value;
        var stats = skill.Stats!.ToDictionary(s => s.Key, s => s.Value);
        stats[radiusKey] = radiusPct;
        skill = skill with { Stats = [.. stats.Select(s => (s.Key, s.Value))] };
        int defaultLife = Monsters.DefaultLife(monster.Level) ?? throw new InvalidDataException($"No default monster life at level {monster.Level}");
        double fire = defaultLife * (double)stats[fireKey] / 100;
        var effects = monster.Mods.Effects.Plus("damage_+%", source["fire_damage_+%"] + source["elemental_damage_+%"] + source["area_damage_+%"]);
        var attack = new MonsterAttack(skill.Id, 0, radius, effects.Hit(DamagePacket.Of(DamageType.Fire, fire, fire))) { Spell = true };
        double now = ProjectileWorldTime(conn);
        var at = monster.Walk.Position(now);
        var area = Of(conn) ?? Areas.GetValue(conn.Instance, _ => new());
        // A restored host can die before the runner's first sight check rebuilds marks for the new object ids.
        Remark(conn, area, id, monster);
        monster.CorpseConsumed = true; // TormentTouchedMonsterMartyr: base_monster_will_be_deleted_on_death.
        var viewers = new List<MartyrViewer>();
        foreach (var player in Members(conn).Where(p => p.PlayerSent))
        {
            MonsterHitOutcome? hit = null;
            if (!player.Paused && !InGracePeriod(player))
            {
                foreach (var minion in player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values).ToArray())
                    if (!minion.Monster.Dead && InMartyrBlast(at, minion.Monster.Walk.Position(now), radius, LivingCollision.Size(minion.Monster)))
                        GolemTakeHitLocked(player, minion, id, monster, attack, now, area: true);
                var here = player.Walk.Position(now);
                if (player.PlayerLife > 0 && !player.Immune && InMartyrBlast(at, here, radius, LivingCollision.PlayerSize))
                    hit = CommitMonsterHitLocked(player.Wire.Remote, player, id, monster, attack, now, here, at, checkRange: false);
            }
            viewers.Add(new(player, player.MonsterViews.ContainsKey(id) && !player.AsleepMonsters.Contains(id), hit));
        }
        byte actor = checked((byte)origin.Components.ToList().IndexOf("Actor"));
        area.MartyrDeaths.Enqueue(new(id, InstanceEntities.ActorSkills(id, actor, [skill]),
            InstanceProtocol.InstantAction(id, skill.Id, at.X, at.Y, triggered: true), viewers));
    }

    static async Task PresentMartyr(ClientState runner, MartyrDeath death)
    {
        foreach (var (p, awake, hit) in death.Viewers)
        {
            bool Here() => CurrentGameWork && !p.Closed && !p.LeavingArea && p.Instance == runner.Instance;
            if (!Here()) continue;
            var w = p.Wire;
            if (awake) await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, p, death.Id, () =>
            {
                lock (p.Monsters)
                {
                    if (!p.MonsterViews.ContainsKey(death.Id) || p.AsleepMonsters.Contains(death.Id)) return null;
                    // A contact can kill the host before its first skill-list update reached this viewer.
                    return hit is null ? [death.Skill, death.Action, InstanceProtocol.LifeUpdate(death.Id, 0)]
                        : [death.Skill, death.Action, hit.HitPacket, InstanceProtocol.LifeUpdate(death.Id, 0)];
                }
            }, still: Here);
            if (hit is not null && Here()) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, death.Id, hit);
        }
        await Remove(runner, death.Id, "Martyr's Touch exploded");
    }
}
