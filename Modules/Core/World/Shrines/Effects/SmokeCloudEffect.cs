using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The Shrouded shrine's effect (Shrines row 16 <c>Smoke</c>, whose BuffTemplates 402 <c>smoke_player</c> grants
/// BuffDefinitions 253 <c>shrine_smoke</c>, "Smoke shrouds you", for the row's 45 s). The row summons MonsterVarieties
/// 2922 <c>Metadata/Monsters/Daemon/ShrineDaemonSmoke</c>, whose GrantedEffects 2942 <c>ShrineSmokeCloud</c> (ActiveSkill
/// 375 <c>shrine_smoke_cloud</c>, cast time 1000 ms, stat set 2951: <c>base_skill_effect_duration</c> 4000) is the cloud.
/// <para>The cloud is one object, drawn at the shrine while it stands unclaimed and around the player once its buff is up,
/// kept where the caster is rather than re-cast: the skill lasts 4 s and is cast every second, which on screen is one
/// unbroken shroud.</para>
/// <para>What the smoke does is the Blind the client's own smoke gives (<c>Game.Blinded</c>, buff 179 <c>blinded</c>): a
/// player who walks into an unclaimed shrine's fog is blinded, and once the shrine is claimed the monsters standing in the
/// fog are. Both are the engine's timed Blind (<c>InflictPlayerBlind</c> / <c>InflictMonsterBlind</c>), refreshed while the
/// victim stays in the fog and lapsing a moment after it leaves.</para>
/// </summary>
sealed class SmokeCloudEffect : IShrineEffect
{
    /// <summary>GrantedEffects row of the cloud (<c>ShrineSmokeCloud</c>).</summary>
    internal const int Skill = 2942;

    /// <summary>MonsterVarieties row the Shrines row summons for the unclaimed shrine; its attack time is the cast
    /// cadence.</summary>
    internal const string Daemon = "Metadata/Monsters/Daemon/ShrineDaemonSmoke";

    /// <summary>GroundEffects row 117 (GroundEffectTypes 4 Smoke, its largest variation):
    /// <c>Metadata/Effects/Spells/ground_effects_v2/smoke_blind_fog_of_war_2/smoke_blind_fog_of_war_large.ao</c>, the fog
    /// that shrouds a whole area.</summary>
    internal const string CloudArt =
        "Metadata/Effects/Spells/ground_effects_v2/smoke_blind_fog_of_war_2/smoke_blind_fog_of_war_large.ao";

    /// <summary>The radius the art is drawn at, in world units: that row's <c>__col4</c> 350, the field
    /// <c>FlaskGrounds</c> takes its grounds' size from. The same radius is the fog's reach for the Blind.</summary>
    internal const double CloudRadiusUnits = 350;

    /// <summary>World units per cell in the client's tables (250/23).</summary>
    internal const double UnitsPerCell = 250d / 23;

    /// <summary>The fog's reach in cells, about 32.</summary>
    internal static double CloudRadiusCells => CloudRadiusUnits / UnitsPerCell;

    /// <summary>How far, in cells, the caster may walk before the cloud is laid again under them, so the claimed buff's
    /// shroud stays around the player instead of being left behind.</summary>
    internal const double FollowCells = 3;

    /// <summary>How often the Blind is refreshed while a victim stays in the fog, and how long each refresh lasts: a
    /// little past the interval, so the Blind is unbroken in the fog and lapses shortly after leaving it. The client's own
    /// blind of a hit is 4 s (<c>BlindInputs</c>); the smoke's is as long as the fog covers.</summary>
    internal const double BlindRefresh = 1;
    internal const double BlindSeconds = 1.5;

    uint _cloud;
    (int X, int Y) _at;
    double _nextBlind;
    int _blindedCount = -1;

    public Task OnSpawn(ShrineContext ctx, ShrineInstance shrine)
    {
        Console.WriteLine($"[gateway] shrine {shrine.Definition.Id}: smoke cloud {Skill} ({CloudArt}), daemon " +
            $"{(Monsters.Find(Daemon) is null ? "MISSING" : "found")}, reaches {CloudRadiusCells:0.#} cells");
        return Task.CompletedTask;
    }

    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        if (conn.Closed) return;
        double now = ctx.Now;
        var inFog = new List<(uint Id, SpawnedMonster Monster)>();
        bool playerInFog = false;
        double reach = CloudRadiusCells;
        lock (conn.Monsters)
        {
            // A connection that has left the area keeps none of it: the cloud went with the area.
            if (conn.LeavingArea)
            {
                _cloud = 0;
                _nextBlind = 0;
                return;
            }
            // The standing shrine shrouds its statue; the claimed buff's cloud follows the player.
            var at = shrine.State == ShrineState.Inactive ? shrine.At : conn.Walk.Position(now);
            if (_cloud == 0 || Dist(_at, at) > FollowCells)
            {
                if (_cloud != 0) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(_cloud), null));
                _cloud = conn.NextObjectId++;
                _at = at;
                var cloud = new EffectObject(at.X, at.Y, CloudArt) { Flags0 = EffectObject.FixedRotation };
                conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(_cloud,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath), cloud.Blob()), null));
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(_cloud), null));
                Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine {shrine.Definition.Id} smoke cloud " +
                    $"0x{_cloud:x} at ({at.X}, {at.Y}) {shrine.State}");
            }
            if (now < _nextBlind) return;
            _nextBlind = now + BlindRefresh;
            // The unclaimed shrine's fog blinds whoever walks into it; the claimed shrine's blinds the monsters in it.
            if (shrine.State == ShrineState.Inactive)
                playerInFog = Dist(conn.Walk.Position(now), _at) <= reach;
            else
                foreach (var (id, m) in conn.Monsters)
                    if (m is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(m.Walk.Position(now), _at) <= reach)
                        inFog.Add((id, m));
        }
        if (playerInFog) InflictPlayerBlind(conn, BlindSeconds);
        int blinded = 0;
        foreach (var (id, m) in inFog)
            if (InflictMonsterBlind(conn, id, m, now, (100, 0, BlindSeconds))) blinded++;
        // Logged when the count changes rather than on every refresh, which would be a line a second.
        if (blinded == _blindedCount) return;
        _blindedCount = blinded;
        Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine {shrine.Definition.Id}: the smoke blinds " +
            $"{blinded} monster(s){(playerInFog ? " and the player" : "")}");
        await Task.CompletedTask;
    }

    /// <summary>Claiming and expiring both end the cloud this effect laid.</summary>
    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine)
    {
        _cloud = 0;
        _nextBlind = 0;
        _blindedCount = -1;
        return Task.CompletedTask;
    }

    Task Clear(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return Task.CompletedTask;
        lock (conn.Monsters)
        {
            if (_cloud != 0)
            {
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(_cloud), null));
                _cloud = 0;
            }
            _nextBlind = 0;
            _blindedCount = -1;
        }
        return Task.CompletedTask;
    }
}
