
/// <summary>The server's validated character-build boundary. JSON uses the versioned native import contract;
/// mods never receive a save database, internal item types or a writable character record.</summary>
public interface ICharacterBuilds
{
    /// <summary>The admitted character's current record as JSON, for an external build converter.</summary>
    string Capture(IPlayer player);

    /// <summary>Validates a version-1 { Version, Record, Save, Summary, Warnings } result without changing the player.
    /// The returned single-use plan belongs to this mod load and expires after five minutes.</summary>
    ICharacterBuildPlan Prepare(IPlayer player, string json);
}

/// <summary>A validated preview, bound to one connection and mod generation. Disposing cancels it.</summary>
public interface ICharacterBuildPlan : IDisposable
{
    /// <summary>The validated preview text.</summary>
    string Summary { get; }
    /// <summary>Validated conversion warnings, exposed read-only.</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>Accepts once while the owner is loaded and the player is alive, connected and not transferring.
    /// Returns false for an expired, cancelled or unavailable plan. Acceptance closes the old connection; the server
    /// drains its writers and atomically installs the build before allowing re-entry. An accepted plan survives unload.</summary>
    bool Confirm();
}
