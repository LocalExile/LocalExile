using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>The shrine daemons' own spells, independent of the player's spell and minion modifiers.</summary>
public static class ShrineNovaRules
{
    public sealed record Spell(int Effect, string Daemon, int Radius, int RadiusPercent, double CastSeconds,
        double Interval, int GroundCount, int GroundRadius, double GroundSeconds, DamageType Element);

    public static Spell Describe(SkillCatalog skills, bool cold, bool claimed)
    {
        int effect = cold ? claimed ? 102 : 101 : claimed ? 1068 : 1067;
        string daemon = (cold ? claimed ? 2921 : 2920 : claimed ? 2919 : 2918).ToString();
        var own = skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        // Monster ShockNova's contact (0x141404c60) scales a hardcoded 26-cell circle. It is NOT NewShockNova,
        // the player's gem action with a separate annulus. Its derived radius percentage is client stat 0x1591.
        int radius = SkillArea.Radius(cold ? Get("active_skill_base_area_of_effect_radius") : 26, Get, true);
        double cast = skills.CastTimeMs(effect) / 1000.0;
        return new(effect, daemon, radius, SkillArea.RadiusPercent(Get, true), cast,
            Math.Max(cast, (Monsters.Find(daemon)?.AttackMs ?? 1000) / 1000.0),
            cold ? 1 : Get("shock_nova_places_X_ground_lightning_patches"), cold ? radius : 6,
            Get("base_skill_effect_duration") / 1000.0, cold ? DamageType.Cold : DamageType.Lightning);
    }

    public static double HitDelay(Spell spell, double distance) => spell.CastSeconds
        + (spell.Element == DamageType.Cold ? IceNovaRing.Reach(distance, spell.Radius, 0) : 0);
}

/// <summary>Publish a real shrine daemon's action so the native client selects, scales and animates its nova.
/// Server contacts follow the release and, for Ice Nova, its expanding ring. Ground remains at the cast location.</summary>
abstract class ShrineNovaEffect(bool cold) : IShrineEffect
{
    sealed class Cast((int X, int Y) at, double began)
    {
        public (int X, int Y) At = at;
        public double Began = began;
        public bool Ground;
        public readonly HashSet<uint> HitMonsters = [];
        public readonly HashSet<ClientState> HitPlayers = [];
    }
    sealed record Ground((int X, int Y) At, int Radius, double Until, uint Object);
    readonly List<Cast> casts = [];
    readonly List<Ground> grounds = [];
    uint daemon;
    double nextCast;
    ShrineNovaRules.Spell? profile;

    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        if (conn.Closed || conn.LeavingArea) return;
        bool standing = shrine.State == ShrineState.Inactive;
        var spell = profile ??= ShrineNovaRules.Describe(SkillsFor(conn), cold, !standing);
        double now = ctx.Now;
        if (now >= nextCast)
        {
            var at = standing ? shrine.At : conn.Walk.Position(now);
            if (await Start(conn, shrine, spell, at, now))
            {
                casts.Add(new(at, now));
                nextCast = now + spell.Interval;
            }
        }

        foreach (var cast in casts.ToArray())
        {
            double elapsed = now - cast.Began;
            if (elapsed < spell.CastSeconds) continue;
            if (!cast.Ground)
            {
                cast.Ground = true;
                PlaceGround(conn, spell, cast.At, cast.Began + spell.CastSeconds);
            }
            double radius = cold ? spell.Radius * Math.Clamp((elapsed - spell.CastSeconds) / IceNovaRing.Seconds(0), 0, 1) : spell.Radius;
            var variety = Monsters.Find(spell.Daemon);
            if (variety is not null && Monsters.BaseDamage(variety, ShrineManager.AreaLevel(conn)) is { } damage)
            {
                var packet = DamagePacket.Of(spell.Element, damage.Item1, damage.Item2);
                if (standing)
                    await ShrineManager.HitPlayers(conn, cast.At, radius, spell.Element, packet,
                        player => cast.HitPlayers.Add(player));
                else
                {
                    (uint Id, SpawnedMonster Monster)[] targets;
                    lock (conn.Monsters)
                        targets = [.. conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                            && Dist(p.Value.Walk.Position(now), cast.At) <= radius && cast.HitMonsters.Add(p.Key))
                            .Select(p => (p.Key, p.Value))];
                    foreach (var (id, monster) in targets)
                    {
                        int dealt;
                        lock (conn.Monsters)
                        {
                            if (monster.Dead || monster.Invulnerable) continue;
                            var mitigated = DamageModel.Mitigate(packet.Roll(Random.Shared), type => MonsterHitResistance(monster, type, default));
                            dealt = MonsterTakesMitigated(monster, mitigated, spell: true);
                        }
                        if (dealt <= 0 || !await HitMonster(conn, id, monster, dealt, cold ? "shrine Ice Nova" : "shrine Shock Nova", log: false)) continue;
                        lock (conn.Monsters)
                            if (!monster.Dead && monster.Life > 0)
                                monster.Ailments.Inflict(DamagePacket.Of(spell.Element, dealt, dealt), false,
                                    monster.MaxLife, now, _ => 0, name => monster.Effects[name], Random.Shared);
                    }
                }
            }
            if (elapsed >= spell.CastSeconds + (cold ? IceNovaRing.Seconds(0) : 0)) casts.Remove(cast);
        }
        MaintainGround(conn, standing, now);
    }

    async Task<bool> Start(ClientState conn, ShrineInstance shrine, ShrineNovaRules.Spell spell, (int X, int Y) at, double now)
    {
        var catalog = SkillsFor(conn);
        ushort skillId = (ushort)(catalog.ActionHash(spell.Effect)!.Value & 0x3fff);
        if (daemon == 0)
        {
            if (Monsters.Find(spell.Daemon) is not { Supported: true } variety) return false;
            var stats = catalog.EffectStats(spell.Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
            var action = new MonsterAttack(skillId, spell.CastSeconds, spell.Radius, DamagePacket.Empty) { Spell = true };
            foreach (var (key, value) in action.InPlaceSkillStats()) stats[key] = value;
            void Set(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
            Set("active_skill_area_of_effect_radius", spell.Radius);
            Set("total_skill_area_of_effect_radius_+%_including_final", spell.RadiusPercent);
            var skill = catalog.FindByRow(skillId, spell.Effect, 1, [.. stats.Select(p => (p.Key, p.Value))]);
            if (skill is null) return false;
            daemon = await SpawnMonster(conn, variety,
                ShrineManager.AreaLevel(conn), MonsterRarity.Normal, at.X, at.Y, friendly: true, log: false, alone: false, scripted: true,
                ownedSkills: [skill], placement: MonsterPlacement.Established,
                owned: (_, m) =>
                {
                    m.NoRewards = true;
                    m.Attack = null;
                    m.SummonerId = shrine.ObjectId;
                    m.EncounterStats = new Dictionary<string, int> { ["base_cannot_be_damaged"] = 1 };
                });
            if (daemon == 0) return false;
        }
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(daemon, out var monster)) { daemon = 0; return false; }
            monster.Walk.Teleport(at.X, at.Y, now);
        }
        await SendWorldRecord(conn, daemon,
            [.. InstanceProtocol.ActionStarted(daemon, at.X, at.Y, at.X, at.Y, skillId, 0xffff, conn.MonsterOrderFlags),
                .. InstanceProtocol.ActionEnded(daemon)], $"shrine nova {spell.Effect}, radius {spell.Radius}");
        return true;
    }

    void PlaceGround(ClientState conn, ShrineNovaRules.Spell spell, (int X, int Y) at, double now)
    {
        lock (conn.Monsters)
        for (int i = 0; i < spell.GroundCount; i++)
        {
            // The claimed lightning daemon leaves ONE patch, underneath its caster. The standing one scatters five.
            double angle = i * Math.PI * 2 / Math.Max(1, spell.GroundCount);
            int ring = spell.GroundCount == 1 ? 0 : spell.Radius / 2;
            var spot = (X: at.X + (int)Math.Round(Math.Cos(angle) * ring), Y: at.Y + (int)Math.Round(Math.Sin(angle) * ring));
            if (conn.Layout.Walkable is { } walk && !walk(spot.X, spot.Y)) continue;
            int existing = grounds.FindIndex(g => g.At == spot && g.Until > now);
            if (existing >= 0) { grounds[existing] = grounds[existing] with { Until = now + spell.GroundSeconds }; continue; }
            uint id = conn.TakeObjectId();
            string art = cold ? "Metadata/Effects/Spells/ground_effects/ice/ice.ao"
                : "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao";
            var obj = new EffectObject(spot.X, spot.Y, art)
                { Flags0 = EffectObject.FixedRotation, Scale = spell.GroundRadius / (cold ? 4f : 6f) };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                obj.Blob()), null, WorldId: id, CreatesObject: true));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
            grounds.Add(new(spot, spell.GroundRadius, now + spell.GroundSeconds, id));
        }
    }

    void MaintainGround(ClientState conn, bool standing, double now)
    {
        var kind = cold ? MonsterElementalAilments.Kind.Chill : MonsterElementalAilments.Kind.Shock;
        int magnitude = cold ? 10 : 15;
        lock (conn.Monsters)
        {
            foreach (var ground in grounds.Where(g => g.Until <= now).ToArray())
            {
                grounds.Remove(ground);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ground.Object), null, WorldId: ground.Object, RemovesObject: true));
            }
            if (!standing)
                foreach (var monster in conn.Monsters.Values)
                    if (monster is { Hostile: true, Dead: false, Life: > 0 } && grounds.Any(g => Dist(g.At, monster.Walk.Position(now)) <= g.Radius)
                        && (monster.Ailments.Magnitude(kind, now) < magnitude || monster.Ailments.Remaining(kind, now) < .15))
                        monster.Ailments.WithoutDamage(kind, .3, now, _ => 0, name => monster.Effects[name], 1, magnitude);
        }
        if (standing)
            foreach (var player in ShrineManager.NearbyPlayers(conn, (0, 0), double.MaxValue))
            {
                // Each player's ailments and walk run on that player's own clock.
                double at = player.Clock.Elapsed.TotalSeconds;
                if (!grounds.Any(g => Dist(g.At, player.Walk.Position(at)) <= g.Radius)) continue;
                var stats = CharacterPlayerStats(player);
                var playerKind = cold ? PlayerElementalAilments.Kind.Chill : PlayerElementalAilments.Kind.Shock;
                lock (player.Monsters)
                {
                    if (player.ElementalAilments.Magnitude(playerKind, at) >= magnitude) continue;
                    ApplyPlayerAilmentsLocked(player, at, ailments => ailments.Inflict(playerKind, magnitude, .3, at,
                        name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, player.IncomingHitRandom));
                }
            }
    }

    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);
    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);
    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    Task Clear(ClientState conn)
    {
        lock (conn.Monsters)
        {
            foreach (var ground in grounds)
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ground.Object), null, WorldId: ground.Object, RemovesObject: true));
            grounds.Clear(); casts.Clear(); nextCast = 0;
            if (daemon != 0 && conn.Monsters.Remove(daemon, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                conn.MonsterViews.Remove(daemon); conn.AsleepMonsters.Remove(daemon);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(daemon), "shrine daemon ends", daemon, monster,
                    PlayerMinions.OutputKind.Remove, WorldId: daemon, RemovesObject: true));
            }
            daemon = 0;
        }
        return Task.CompletedTask;
    }
}
