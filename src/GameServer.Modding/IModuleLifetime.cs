namespace GameServer.Modding;

/// <summary>The host-issued identity and resources of one loaded mod, module or league generation.</summary>
/// <remarks>
/// Stopping deactivates the generation, cancels <see cref="Stopping"/>, then disposes its owned resources.
/// Cancellation is cooperative: stopping does not wait for hooks or asynchronous work already in progress.
/// </remarks>
public interface IModuleLifetime
{
    /// <summary>The manifest id, shared by successive generations of the same module.</summary>
    string Id { get; }

    /// <summary>A host-issued, increasing identity for this load, including a load that fails or restores an old build.</summary>
    long Generation { get; }

    /// <summary>Whether this generation has not started stopping. Checking this does not reserve an execution slot.</summary>
    bool IsActive { get; }

    /// <summary>Cancelled after this generation becomes inactive and before its owned resources are disposed.</summary>
    CancellationToken Stopping { get; }

    /// <summary>
    /// Owns a resource until stopping. Disposing the returned handle removes and disposes the resource once.
    /// An already stopped generation disposes it immediately and returns an inert handle.
    /// </summary>
    IDisposable Own(IDisposable resource);
}
