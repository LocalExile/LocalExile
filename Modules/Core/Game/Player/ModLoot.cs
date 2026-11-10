using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

static partial class Game
{
    internal sealed class ModLoot(ClientState conn, IModuleLifetime? rules) : IPlayerLoot
    {
        bool Current(IModuleLifetime? owner = null) => rules is { IsActive: true }
            && ReferenceEquals(CombatLifetime, rules) && CurrentGameRules && !conn.Closed && !conn.RageRetired
            && conn.PlayerSent && !conn.LeavingArea && !conn.Paused && conn.PlayerLife > 0
            && !HardcoreProgressBlocked(conn) && (owner?.IsActive ?? true);

        bool Visible(uint id)
        {
            var origin = conn.Instance.Ground.Origin(id);
            return origin.Shared || ReferenceEquals(origin.By, conn);
        }

        GroundItem? Snapshot(uint id, WorldItemObject item)
        {
            if (!Visible(id) || ItemsFor(conn).ByHash(item.Item.TypeHash) is not { } type) return null;
            return new(id, type.Id, type.Name, type.Class, item.Item.StackSize, type.MaxStack,
                IsGold(item), item.PlayerDropped, item.GridX, item.GridY, type.Quest is not null);
        }

        public IReadOnlyList<GroundItem> Nearby(double radius)
        {
            lock (conn.Monsters)
            {
                if (!Current() || !double.IsFinite(radius) || radius <= 0) return [];
                var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                return [.. conn.Instance.Ground.Within(at.X, at.Y, Math.Min(radius, 80), _ => true)
                    .Select(g => Snapshot(g.Id, g.Item)).OfType<GroundItem>()];
            }
        }

        public async Task<bool> PickUp(uint id, IModuleLifetime owner, uint? petId = null)
        {
            var messages = new List<(byte[] Data, string Log)>();
            lock (conn.Monsters)
            {
                if (!Current(owner) || conn.Instance.Ground.Get(id) is not { } ground || !Visible(id)
                    || ground.PlayerDropped || ItemsFor(conn).ByHash(ground.Item.TypeHash) is not { Quest: null } type
                    || (!IsGold(ground) && type.MaxStack <= 1)) return false;
                double now = conn.Clock.Elapsed.TotalSeconds;
                var player = conn.Walk.Position(now);
                var collector = player;
                if (petId is uint pet)
                {
                    var position = GameEvents.PetCollector.For(conn).Select(run => run(conn, pet, owner))
                        .FirstOrDefault(p => p is not null);
                    if (position is not { } at) return false;
                    collector = at;
                }
                if (Dist(player, (ground.GridX, ground.GridY)) > 80 || Dist(collector, (ground.GridX, ground.GridY)) > 2)
                    return false;
                if (!TryTakeGroundStack(conn, id, ground, messages)) return false;
            }
            // A committed save must still reach the client if the mod is stopped while its output is queued.
            foreach (var (data, log) in messages)
                if (conn.Wire.Stream is not null) await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, data, log);
            return true;
        }
    }
}
