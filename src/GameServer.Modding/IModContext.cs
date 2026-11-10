namespace GameServer.Modding;

/// <summary>
/// What the host gives a mod (<see cref="IMod.Load"/>), a module or a league's code: who it is, where its files are, its settings and the
/// log. What it hooks into is the Gateway's <c>GameEvents</c>, with <see cref="ModManifest.Id"/> as the owner: the host removes those
/// subscriptions, and the server switches the owner set, when it unloads (Agents/docs/dev/modding.md, "Hooks").
/// </summary>
public interface IModContext
{
    /// <summary>The mod's <c>mod.json</c>.</summary>
    ModManifest Manifest { get; }

    /// <summary>The host-issued lifetime of this load, for generation checks, cancellation and owned registrations.</summary>
    IModuleLifetime Lifetime { get; }

    /// <summary>The mod's folder (<c>Mods/Binaries/&lt;id&gt;</c>), for its own files.</summary>
    string Folder { get; }

    /// <summary>Prepare and confirm a validated character build, scoped to this load.</summary>
    ICharacterBuilds CharacterBuilds => throw new NotSupportedException("This host does not provide character build imports.");

    /// <summary>Writes a line to the Gateway's log, as <c>[mods] &lt;id&gt;: message</c>.</summary>
    void Log(string message);

    /// <summary>The settings its <c>settings.json</c> declares, with the user's values (<c>data/mods/&lt;id&gt;.json</c>, for a
    /// module <c>data/modules/&lt;id&gt;.json</c>) over the defaults, read into <typeparamref name="T"/> by name (case does not
    /// matter). A value that does not fit its declaration is logged and its default used. A change to either file reloads
    /// the mod, so the new load reads them again (Agents/docs/dev/modding.md, "Settings").</summary>
    /// <exception cref="FormatException"><c>settings.json</c> is missing or declares something wrong: the load fails.</exception>
    T Settings<T>();

    /// <summary>Writes these values as the user's (a chat command that changes a setting), the ones equal to their default
    /// left out; the write reloads the mod. Throws <see cref="ArgumentException"/>, writing nothing, when one does not fit
    /// its declaration.</summary>
    void SaveSettings<T>(T settings);
}

/// <summary>What the host gives a module or a league's own code beyond a mod's: the rules it gives leagues, and the player's world.</summary>
public interface IModuleContext : IModContext
{
    /// <summary>The rules this module gives the leagues that use it; the first in a league's list that gives any wins.</summary>
    void ProvideLeagueRules(Func<LeagueRules> rules);

    /// <summary>What the module can do with this player's world.</summary>
    IPlayerWorld World(IPlayer player);
}
