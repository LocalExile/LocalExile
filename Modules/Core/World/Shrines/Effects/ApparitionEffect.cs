using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>The shrine daemon summons an eight-second projection every twelve seconds. These are allies of the
/// shrine, not player summons. Primary animations drive secondary geometry contacts; payloads never get their own AI.</summary>
public sealed class ApparitionEffect : IShrineEffect
{
    public sealed record Profile(string Kind, MonsterCatalog.Variety Body, int Primary, int[] Payloads,
        double CastSeconds, double Interval, bool Claimed);

    public static Profile Describe(Player player, ShrineCatalog.ShrineDefinition row, bool claimed)
    {
        int daemonRow = (claimed ? row.PlayerSummon : row.MonsterSummon)!.Value;
        var daemon = Monsters.Find(daemonRow.ToString())!;
        var catalog = SkillsFor(player);
        int summonEffect = daemon.GrantedEffects![0];
        var summon = MonsterSummonOf(catalog, summonEffect, ShrineManager.AreaLevel(player))!;
        var body = Monsters.Find(summon.Variety.ToString())!;
        int primary = row.Id switch
        {
            "Warlord" => claimed ? 12693 : 9852,
            "Crusader" => claimed ? 12681 : 9280,
            "Hunter" => claimed ? 12686 : 8112,
            "Redeemer" => claimed ? 12684 : 8092,
            _ => throw new ArgumentException("No apparition daemon for " + row.Id),
        };
        int[] payloads = row.Id switch
        {
            "Warlord" => [claimed ? 12692 : 7044],
            "Crusader" => [9315],
            "Hunter" => claimed ? [12687, 12688, 12689, 12690] : [7047, 7051, 7057, 7070],
            _ => [claimed ? 12685 : 9420],
        };
        return new(row.Id, body, primary, payloads, catalog.CastTimeMs(primary) / 1000d,
            catalog.CooldownMs(summonEffect, 1) / 1000d, claimed);
    }

    sealed record Contact(double At, (int X, int Y) Centre, int Effect, double Radius, double Angle = 0);
    readonly List<Contact> contacts = [];
    readonly Dictionary<int, MonsterAttack> attacks = [];
    Profile? profile;
    uint actor;
    SpawnedMonster? body;
    double nextSummon, expires, actionEnd;
    bool actionOpen;
    int generation;
    ushort tornadoSequence;
    readonly List<(ProjectileSession Session, object Payload)> flights = [];
    Player? producer;

    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        if (ctx.Player.Closed || ctx.Player.LeavingArea || ctx.Player.PlayerLife == 0) return;
        if (producer is not null && !ReferenceEquals(producer, ctx.Player)) await Remove(ctx.Player);
        producer = ctx.Player;
        var p = profile ??= Describe(ctx.Player, shrine.Definition, shrine.State != ShrineState.Inactive);
        if (actor != 0 && ctx.Now >= expires) await Remove(ctx.Player);
        if (actor == 0 && ctx.Now >= nextSummon)
        {
            var origin = p.Claimed ? ctx.Player.Walk.Position(ctx.Now) : shrine.At;
            var target = Target(ctx.Player, p, origin, ctx.Now);
            if (target is not null) await Summon(ctx, shrine, p, origin, target.Value);
        }
        if (body is null || actor == 0) return;
        if (actionOpen && ctx.Now >= actionEnd)
        {
            actionOpen = false;
            await SendWorldRecord(ctx.Player, actor, InstanceProtocol.ActionEnded(actor), "shrine apparition action ends");
        }
        foreach (var contact in contacts.Where(c => c.At <= ctx.Now).ToArray())
        {
            contacts.Remove(contact);
            if (p.Kind == "Redeemer") Tornadoes(ctx, p, contact);
            else await Hit(ctx, p, contact);
        }
    }

    static (int X, int Y)? Target(Player player, Profile profile, (int X, int Y) origin, double now)
    {
        if (!profile.Claimed)
            return ShrineManager.NearbyPlayers(player, origin, 70).MinBy(p => Dist(origin, p.Walk.Position(now)))?.Walk.Position(now);
        return player.Area.MonstersNear(origin, 70).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
            && !p.Value.NoRewards && !double.IsPositiveInfinity(p.Value.BusyUntil))
            .MinBy(p => Dist(origin, p.Value.Walk.Position(now))).Value?.Walk.Position(now);
    }

    async Task Summon(ShrineContext ctx, ShrineInstance shrine, Profile p, (int X, int Y) origin, (int X, int Y) aim)
    {
        var player = ctx.Player;
        byte level = ShrineManager.AreaLevel(player);
        var catalog = SkillsFor(player);
        var modifiers = !p.Claimed && MapModsOf(player) is { } map
            ? new MonsterModEffects(MapMonsterEffects(map)).Combine(MapBossEffects(map)) : MonsterModEffects.None;
        double damageFactor = !p.Claimed && MapDevices.IsMapArea(player.Layout.Hash) ? Monsters.MapBossScaling(level).Damage : 1;
        attacks.Clear();
        foreach (int effect in p.Payloads.Prepend(p.Primary))
        {
            var attack = BossKits.AttackFor(player, p.Body, level, effect, Skill(effect, p)) with { Effect = effect, Scripted = true };
            attacks[effect] = attack with { Damage = modifiers.Hit(Convert(player, effect, level, attack.Damage).Scale(damageFactor), level) };
        }
        // Stay beside the target for melee projections; the whirl launches from its summoning point.
        var at = p.Kind == "Redeemer" ? origin : player.Layout.Near((aim.X + 5, aim.Y));
        var skills = attacks.Select(pair =>
        {
            var native = GolemData.Skill(catalog, pair.Key, level);
            var stats = new Dictionary<uint, int>(native.Stats);
            foreach (var (key, value) in MonsterActionSkillStats(player, pair.Key, native.Level, pair.Value, modifiers)) stats[key] = value;
            return catalog.FindByRow(pair.Value.SkillId, pair.Key, native.Level, [.. stats.Select(s => (s.Key, s.Value))]);
        }).OfType<GrantedSkill>().ToArray();
        actor = await SpawnMonster(player, p.Body, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, log: false, alone: false, scripted: true, ownedSkills: skills,
            mods: new MonsterModSet([], [], modifiers), nonBlocking: true,
            placement: MonsterPlacement.Established, owned: (_, monster) =>
            {
                monster.NoRewards = true;
                monster.DamageImmune = true;
                monster.Attack = null;
                monster.SummonerId = shrine.ObjectId;
                monster.EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };
            });
        if (actor == 0 || !player.Area.Monsters.TryGetValue(actor, out body)) { actor = 0; body = null; return; }
        expires = ctx.Now + 8;
        nextSummon = ctx.Now + Math.Max(8, p.Interval);
        double castSeconds = modifiers.AttackSeconds(p.CastSeconds, catalog.IsSpell(p.Primary));
        actionEnd = ctx.Now + castSeconds;
        actionOpen = true;
        generation++;
        await SendWorldRecord(player, actor, InstanceProtocol.ActionStarted(actor, at.X, at.Y, aim.X, aim.Y,
            Skill(p.Primary, p), 0xffff, player.MonsterOrderFlags), $"shrine apparition {p.Kind} {(p.Claimed ? "ally" : "hostile")} effect {p.Primary}");
        // These timings/shapes are the local encounter sequence. The tables supply cast duration, damage,
        // conversion, summon cooldown and lifetime, but not the server's geometry callbacks.
        switch (p.Kind)
        {
            case "Warlord": contacts.Add(new(actionEnd, aim, p.Payloads[0], 20)); break;
            case "Crusader":
                contacts.Add(new(actionEnd, aim, p.Primary, 20));
                for (int i = 1; i <= 3; i++)
                {
                    var spot = Extend(at, aim, Dist(at, aim) + i * 12);
                    contacts.Add(new(actionEnd + i * .2, spot, p.Payloads[0], 15));
                }
                break;
            case "Hunter":
                for (int i = 0; i < p.Payloads.Length; i++)
                    contacts.Add(new(ctx.Now + castSeconds * (i + 1) / p.Payloads.Length, aim, p.Payloads[i], i == 0 ? 12 : 20));
                break;
            case "Redeemer":
                double step = Math.Max(.1, catalog.CastTimeMs(p.Payloads[0]) / 1000d);
                int count = Math.Max(1, (int)Math.Floor(castSeconds / step));
                for (int i = 0; i < count; i++) contacts.Add(new(ctx.Now + (i + 1) * step, at, p.Payloads[0], 0, i * Math.Tau / count));
                break;
        }
    }

    static ushort Skill(int effect, Profile p) => (ushort)(0x4000 + Array.IndexOf(p.Payloads.Prepend(p.Primary).ToArray(), effect));

    static (int X, int Y) Extend((int X, int Y) from, (int X, int Y) to, double length)
    {
        double distance = Math.Max(1, Dist(from, to));
        return ((int)Math.Round(from.X + (to.X - from.X) * length / distance),
            (int)Math.Round(from.Y + (to.Y - from.Y) * length / distance));
    }

    public static DamagePacket Convert(Player player, int effect, byte level, DamagePacket damage)
    {
        var own = GolemData.Skill(SkillsFor(player), effect, level).Stats;
        var conversion = new DamageConversion();
        foreach (var type in DamageTypes.ConversionOrder)
            if (type != DamageType.Physical)
                conversion = conversion.Add(DamageType.Physical, type, own.GetValueOrDefault(Stat.Of("skill_physical_damage_%_to_convert_to_" + type.ToString().ToLowerInvariant())));
        return damage.Convert(conversion);
    }

    async Task Hit(ShrineContext ctx, Profile p, Contact contact)
    {
        if (body is null) return;
        var attack = attacks[contact.Effect];
        if (p.Claimed)
        {
            foreach (var (id, target) in ctx.Player.Area.MonstersNear(contact.Centre, contact.Radius).ToArray())
                if (Dist(contact.Centre, target.Walk.Position(ctx.Now)) <= contact.Radius)
                    ScriptedAllyHitLocked(ctx.Player, actor, body, id, target, attack, ctx.Now, AreaRng(ctx.Player));
            await PublishMinionOutcomes(ctx.Player);
        }
        else
            foreach (var peer in ShrineManager.NearbyPlayers(ctx.Player, contact.Centre, contact.Radius + 20))
                await ScriptedEnemyContact(peer, actor, body, attack,
                    at => Dist(at, contact.Centre) <= contact.Radius, () => actor != 0 && body is { Dead: false });
    }

    void Tornadoes(ShrineContext ctx, Profile p, Contact contact)
    {
        ushort sequence = ++tornadoSequence;
        if (p.Claimed) TornadoesFor(ctx, p, contact, ctx.Player, true, sequence);
        else foreach (var peer in ShrineManager.NearbyPlayers(ctx.Player, contact.Centre, 150))
            TornadoesFor(ctx, p, contact, peer, ReferenceEquals(peer, ctx.Player), sequence);
    }

    public static SkillCatalog.MonsterProjectileInfo TornadoProjectile(Player player, int effect, byte level)
    {
        var catalog = SkillsFor(player);
        var art = catalog.SkillArtProjectile(effect, variant: catalog.ArtVariation(effect))
            ?? throw new InvalidOperationException("Missing apparition tornado art for " + effect);
        var own = GolemData.Skill(catalog, effect, level).Stats;
        int count = Math.Max(1, own.GetValueOrDefault(Stat.BaseNumberOfProjectiles)) + own.GetValueOrDefault(Stat.NumberOfAdditionalProjectiles);
        return new(art.Id, art.Speed, false, art.CollisionRadius) { Count = count, SpreadRadians = Math.Tau };
    }

    void TornadoesFor(ShrineContext ctx, Profile p, Contact contact, Player player, bool visible, ushort sequence)
    {
        var source = body!;
        uint id = actor;
        int token = generation;
        var session = ProjectilesFor(player);
        var attack = attacks[contact.Effect];
        var catalog = SkillsFor(player);
        byte level = ShrineManager.AreaLevel(player);
        var shot = TornadoProjectile(player, contact.Effect, level);
        var own = GolemData.Skill(catalog, contact.Effect, level).Stats;
        int Get(uint key) => own.GetValueOrDefault(key);
        double seconds = Get(Stat.BaseSkillEffectDuration) / 1000d;
        double speed = ProjectileSpeed.CellsPerSecond(shot.Speed, Get, Get);
        var recipe = new ProjectileProfile(shot.Metadata)
        {
            Count = Math.Max(1, shot.Count), Speed = speed, Radius = shot.CollisionRadius,
            SpreadRadians = Get(Stat.ProjectilesNova) != 0 ? Math.Tau : shot.SpreadRadians,
            Lifetime = ProjectileRuntime.Time(seconds), Range = speed * seconds,
            HideVisual = !visible,
            Pierce = -1, HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Window, ProjectileRuntime.Time(.1)),
            TerrainBounces = -1,
            Legs = i => new SparkLegs(sequence, i, id, seconds, Get(Stat.SparkRandomDurationPct), speed),
        };
        var owner = session.ActorKey(id, source);
        var offense = new ObjectProjectileOffense($"shrine apparition tornado {id:x}", (s, hit) =>
        {
            if (token != generation || actor != id || player.Closed || player.LeavingArea || player.PlayerLife == 0)
                return ProjectileHitResult.Miss;
            double now = Math.Max(AreaTime(player), hit.At / (double)ProjectileRuntime.QuantaPerSecond);
            if (p.Claimed)
            {
                if (player.Area.Monsters.GetValueOrDefault(hit.Target.Id) is not { } target
                    || !ScriptedAllyHitLocked(player, id, source, hit.Target.Id, target, attack, now, hit.Projectile.RandomSource, projectile: true))
                    return ProjectileHitResult.Miss;
                s.Enqueue(() => PublishMinionOutcomes(player));
                return ProjectileHitResult.Hit;
            }
            // The shared projectile session dispatches enemy hits through the ordinary incoming hit pipeline.
            return HostileHit(s, hit, new HostileProjectileOffense(id, source,
                attack with { Projectile = shot }, source.ActionGeneration, source.Label)
                { MinionDamageFactor = Math.Max(0, 1 + Get(Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d) });
        }) { Quiet = true, Presentation = MonsterProjectilePresentation(catalog.Clips(shot.Metadata), seconds) };
        flights.Add((session, offense));
        session.Schedule(session.Runtime.Now, () => token == generation && actor == id,
            () => new(recipe, owner, owner, p.Claimed ? 1 : 2, ProjectileSession.Vector(contact.Centre),
                ProjectileSession.Vector((contact.Centre.X + (int)Math.Round(30 * Math.Cos(contact.Angle)),
                    contact.Centre.Y + (int)Math.Round(30 * Math.Sin(contact.Angle))))) { Payload = offense, Sequence = sequence });
        session.Advance();
    }

    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Remove(ctx.Player);
    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Remove(ctx.Player);
    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) => Remove(ctx.Player);

    async Task Remove(Player player)
    {
        generation++;
        contacts.Clear();
        foreach (var (session, marker) in flights)
            session.Runtime.Discard(use => ReferenceEquals(use.Payload, marker));
        flights.Clear();
        if (body is not null) { body.Dead = true; body.Life = 0; body.ActionGeneration++; }
        if (actor != 0) producer?.MinionOrigins.TryRemove(actor, out _);
        if (actor != 0 && player.Area.Monsters.Remove(actor)) await SendWorldRemoval(player, actor, "shrine apparition ends");
        actor = 0; body = null; actionOpen = false;
    }
}
