using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const int AstralProjectileCast = 12736, AstralProjectileImpact = 12737,
        AstralMineImpact = 12738, AstralMineCast = 12739, PustuleCast = 12743, PustuleImpact = 12744;
    const int AstralMineMax = 3;
    const double AstralMineArm = .35, AstralMineChainDelay = .18, PustuleGrowth = .8, MarkerLifetime = 4;
    const string MineMarker = "Metadata/Effects/Spells/barrage_mine/targetzone.ao";
    const string PustuleMarker = "Metadata/Effects/Spells/monsters_effects/League_Faridun/FaridunWarlock/SwarmPustules/pustule_explode.ao";

    sealed record SpectreTimedPayload(uint Id, int Effect, int Group, (int X, int Y) At, double Arm, double Until)
    {
        public double ChainAt = double.PositiveInfinity;
    }
    sealed class SpectrePayloadSet
    {
        public readonly List<SpectreTimedPayload> Active = [];
        public int Group;
    }
    static readonly RuleTable<GolemState.Golem, SpectrePayloadSet> SpectreTimedPayloads = new();

    static bool ReleaseSpectreTimedPayloadLocked(Player player, GolemState.Golem spectre, GolemState.Action action, double now)
    {
        int effect = action.Ability.Effect;
        if (effect == AstralProjectileCast)
        {
            if (spectre.Abilities.FirstOrDefault(a => a.Effect == AstralProjectileImpact) is { } impact
                && action.Ability.Projectile is { } flight)
                GolemLaunch(player, spectre, impact with { Projectile = flight }, action.Aim, now);
            return true;
        }
        if (effect is not (AstralMineCast or PustuleCast)) return false;
        if (!GolemCurrent(player, spectre)) return true;
        var markers = SpectreTimedPayloads.GetOrCreateValue(spectre);
        int group = ++markers.Group;
        var at = action.Aim;
        if (effect == PustuleCast)
        {
            Place(at, PustuleImpact, PustuleGrowth, PustuleMarker);
            return true;
        }
        var origin = spectre.Monster.Walk.Position(now);
        double length = Math.Max(1, Dist(origin, at));
        double dx = (at.X - origin.X) / length, dy = (at.Y - origin.Y) / length;
        for (int offset = 0; offset < AstralMineMax; offset++)
        {
            int spacing = offset * 8;
            Place((at.X + (int)Math.Round(dx * spacing), at.Y + (int)Math.Round(dy * spacing)),
                AstralMineImpact, AstralMineArm, MineMarker);
        }
        return true;

        void Place((int X, int Y) cell, int impact, double arm, string animation)
        {
            _ = ProjectilesFor(player);
            uint id = player.TakeObjectId();
            player.ProjectileObjects.Add(id);
            var visual = new EffectObject(cell.X, cell.Y, animation);
            player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), visual.Blob()), null, WorldId: id, CreatesObject: true));
            player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
            markers.Active.Add(new(id, impact, group, cell, now + arm, now + MarkerLifetime));
        }
    }

    static void TickSpectreTimedPayloadsLocked(Player player, GolemState.Golem spectre, double now)
    {
        if (!SpectreTimedPayloads.TryGetValue(spectre, out var markers)) return;
        foreach (var marker in markers.Active.ToArray())
        {
            if (now >= marker.Until)
            {
                RemoveSpectreMarker(player, markers, marker);
                continue;
            }
            if (now < marker.Arm || spectre.Abilities.FirstOrDefault(a => a.Effect == marker.Effect) is not { } impact) continue;
            var victims = player.Area.MonstersNear(marker.At, impact.Radius + 8)
                .Where(pair => pair.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(pair.Value.Walk.Position(now), marker.At) <= impact.Radius + LivingCollision.Size(pair.Value)).ToArray();
            bool mine = marker.Effect == AstralMineImpact;
            if (mine && (double.IsFinite(marker.ChainAt) ? now < marker.ChainAt : victims.Length == 0
                || markers.Active.Any(other => other.Group == marker.Group && double.IsFinite(other.ChainAt)))) continue;
            foreach (var (id, monster) in victims) GolemHitLocked(player, spectre, impact, id, monster, now);
            RemoveSpectreMarker(player, markers, marker);
            if (mine && markers.Active.Where(other => other.Group == marker.Group && other.Effect == AstralMineImpact
                    && !double.IsFinite(other.ChainAt))
                .OrderBy(other => Dist(other.At, marker.At)).FirstOrDefault() is { } next)
                next.ChainAt = now + AstralMineChainDelay;
        }
        if (markers.Active.Count == 0) SpectreTimedPayloads.Remove(spectre);
    }

    static void RemoveSpectreMarker(Player player, SpectrePayloadSet markers, SpectreTimedPayload marker)
    {
        markers.Active.Remove(marker);
        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(marker.Id), null, WorldId: marker.Id, RemovesObject: true));
    }

    static void ClearSpectreTimedPayloadsLocked(Player player, GolemState.Golem spectre)
    {
        if (!SpectreTimedPayloads.TryGetValue(spectre, out var markers)) return;
        foreach (var marker in markers.Active.ToArray()) RemoveSpectreMarker(player, markers, marker);
        SpectreTimedPayloads.Remove(spectre);
    }
}
