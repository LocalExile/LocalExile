using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.CosmeticsAndPets;

internal sealed class CosmeticPetControls(ClientState conn, IModuleLifetime lifetime) : IPlayerPets
{
    bool Current(IModuleLifetime? owner = null) => lifetime.IsActive && CurrentGameRules
        && !conn.Closed && !conn.RageRetired && conn.PlayerSent && !conn.LeavingArea
        && !conn.Paused && conn.PlayerLife > 0 && !HardcoreProgressBlocked(conn) && (owner?.IsActive ?? true);

    public IReadOnlyList<PetInfo> Nearby
    {
        get
        {
            lock (conn.Monsters)
            lock (conn.Pets)
            {
                if (!Current()) return [];
                double now = conn.Clock.Elapsed.TotalSeconds;
                return [.. conn.Pets.Select(p => { var at = p.Value.Walk.Position(now); return new PetInfo(p.Key, at.X, at.Y); })];
            }
        }
    }

    public async Task<bool> MovePet(uint id, int x, int y, IModuleLifetime owner)
    {
        byte[] message;
        lock (conn.Monsters)
        lock (conn.Pets)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!Current(owner) || !conn.Pets.TryGetValue(id, out var pet)
                || Dist(conn.Walk.Position(now), (x, y)) > 80) return false;
            bool renew = ReferenceEquals(pet.ControlOwner, owner) && ReferenceEquals(pet.ControlProvider, lifetime)
                && pet.ControlUntil > now && pet.Target == (x, y);
            if (!PetControl.Claim(conn, id, owner, lifetime, now + 1)) return false;
            if (renew) return true;
            pet.Target = (x, y);
            pet.Walk.SetSpeed(Math.Max(conn.Walk.Speed, 1), now);
            var from = pet.Walk.Order(x, y, now);
            message = InstanceProtocol.ActionStarted(id, from.X, from.Y, x, y, 0x2909, 0xffff, 0x0408);
        }
        if (conn.Wire.Stream is not null)
            await global::Game.SendWorldRecord(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, id, [], null,
                atGate: () => ControlsTarget(id, x, y, owner) ? message : null);
        return true;
    }

    bool ControlsTarget(uint id, int x, int y, IModuleLifetime owner)
    {
        lock (conn.Pets)
            return Current(owner) && conn.Pets.TryGetValue(id, out var pet) && pet.Target == (x, y)
                && ReferenceEquals(pet.ControlOwner, owner) && ReferenceEquals(pet.ControlProvider, lifetime)
                && pet.ControlUntil > conn.Clock.Elapsed.TotalSeconds;
    }

    public void ReleasePet(uint id, IModuleLifetime owner) => PetControl.Release(conn, id, owner, lifetime);

    internal (int X, int Y)? Collector(uint id, IModuleLifetime owner)
    {
        lock (conn.Pets)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            return Current(owner) && conn.Pets.TryGetValue(id, out var pet)
                && ReferenceEquals(pet.ControlOwner, owner) && ReferenceEquals(pet.ControlProvider, lifetime)
                && pet.ControlUntil > now ? pet.Walk.Position(now) : null;
        }
    }

    bool FollowsTarget(uint id, (int X, int Y) target)
    {
        lock (conn.Pets)
            return Current() && conn.Pets.TryGetValue(id, out var pet) && pet.Target == target
                && !(pet.ControlOwner is { IsActive: true } && pet.ControlProvider is { IsActive: true }
                    && pet.ControlUntil > conn.Clock.Elapsed.TotalSeconds);
    }

    internal async Task Follow()
    {
        var messages = new List<(uint Id, (int X, int Y) To, byte[] Data)>();
        lock (conn.Monsters)
        lock (conn.Pets)
        {
            if (!Current()) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var player = conn.Walk.Position(now);
            foreach (var (id, pet) in conn.Pets)
            {
                if (pet.ControlOwner is { IsActive: true } && pet.ControlProvider is { IsActive: true }
                    && pet.ControlUntil > now) continue;
                pet.ControlHandle?.Dispose();
                var here = pet.Walk.Position(now);
                if (Dist(here, player) <= PetFollowRange && !pet.ReturnToOwner) continue;
                pet.ReturnToOwner = false;
                var to = CosmeticsFeature.PetSpot(conn, player, pet.Side);
                if (pet.Target is { } last && Dist(last, to) < 3) continue;
                pet.Target = to;
                pet.Walk.SetSpeed(Math.Max(conn.Walk.Speed, 1), now);
                var from = pet.Walk.Order(to.X, to.Y, now);
                messages.Add((id, to, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408)));
            }
        }
        foreach (var (id, to, message) in messages)
            if (conn.Wire.Stream is not null)
                await global::Game.SendWorldRecord(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, id, [], null,
                    atGate: () => FollowsTarget(id, to) ? message : null);
    }
}
