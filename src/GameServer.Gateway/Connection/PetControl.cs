using GameServer.Modding;

// Both lifetimes own a Gateway receipt, never a closure into either reloadable assembly.
public sealed class PetControl(ClientState conn, uint id, IModuleLifetime owner, IModuleLifetime provider) : IDisposable
{
    int disposed;

    public static bool Claim(ClientState conn, uint id, IModuleLifetime owner, IModuleLifetime provider, double until)
    {
        lock (conn.Pets)
        {
            if (!owner.IsActive || !provider.IsActive || !conn.Pets.TryGetValue(id, out var pet)) return false;
            if (pet.ControlOwner is { IsActive: true } other && !ReferenceEquals(other, owner)
                && pet.ControlUntil > conn.Clock.Elapsed.TotalSeconds) return false;
            if (!ReferenceEquals(pet.ControlOwner, owner) || !ReferenceEquals(pet.ControlProvider, provider))
            {
                pet.ControlHandle?.Dispose();
                pet.ControlOwner = owner;
                pet.ControlProvider = provider;
                var receipt = new PetControl(conn, id, owner, provider);
                var ownerHandle = owner.Own(receipt);
                var providerHandle = provider.Own(receipt);
                if (!owner.IsActive || !provider.IsActive || !ReferenceEquals(pet.ControlOwner, owner)
                    || !ReferenceEquals(pet.ControlProvider, provider))
                {
                    ownerHandle.Dispose(); providerHandle.Dispose();
                    return false;
                }
                pet.ControlHandle = ownerHandle;
                pet.ControlProviderHandle = providerHandle;
            }
            if (!owner.IsActive || !provider.IsActive || !ReferenceEquals(pet.ControlOwner, owner)) return false;
            pet.ControlUntil = until;
            return true;
        }
    }

    public static void Release(ClientState conn, uint id, IModuleLifetime owner, IModuleLifetime provider)
    {
        lock (conn.Pets)
            if (conn.Pets.TryGetValue(id, out var pet) && ReferenceEquals(pet.ControlOwner, owner)
                && ReferenceEquals(pet.ControlProvider, provider))
                pet.ControlHandle?.Dispose();
    }

    public static void Clear(ClientState conn)
    {
        lock (conn.Pets)
            foreach (var pet in conn.Pets.Values) pet.ControlHandle?.Dispose();
    }

    public void Dispose()
    {
        lock (conn.Pets)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (!conn.Pets.TryGetValue(id, out var pet) || !ReferenceEquals(pet.ControlOwner, owner)
                || !ReferenceEquals(pet.ControlProvider, provider)) return;
            var handle = pet.ControlHandle;
            var providerHandle = pet.ControlProviderHandle;
            pet.ControlOwner = null; pet.ControlHandle = null; pet.ControlUntil = 0; pet.Target = null;
            pet.ControlProvider = null; pet.ControlProviderHandle = null;
            pet.ReturnToOwner = true;
            handle?.Dispose();
            providerHandle?.Dispose();
        }
    }
}
