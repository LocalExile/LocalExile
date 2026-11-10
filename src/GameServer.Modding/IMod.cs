namespace GameServer.Modding;

/// <summary>
/// A mod's entry point. The Gateway creates every public, non-abstract class of the mod's assembly that implements it
/// (with a parameterless constructor) and calls <see cref="Load"/> once. A mod subscribes to the Gateway's <c>GameEvents</c> with
/// its id as the owner; the host drops those subscriptions when the mod unloads (a reload, a changed or deleted folder). A class
/// that also implements <see cref="IDisposable"/> is disposed before that.
/// </summary>
/// <remarks>
/// Static fields of a mod live in its own load context: a reload starts them over, like the rest of its state.
/// </remarks>
public interface IMod
{
    /// <summary>Subscribes the mod to the events it uses. An exception here leaves the mod unloaded, with the error in its status.</summary>
    void Load(IModContext mod);
}

/// <summary>
/// A module's entry point: a game system, from an NPC's dialogue to Delve. Modules live in <c>Modules/Core/</c>, <c>Legacy/</c> and
/// <c>Custom/</c>, load only when a league lists them, and run only for those leagues' players (<see cref="IModuleContext"/>).
/// </summary>
public interface IModule
{
    /// <summary>Subscribes the module to the events it uses. An exception here leaves it unloaded, with the error in its status.</summary>
    void Load(IModuleContext module);
}

/// <summary>What kind of mod or module a folder holds (<c>mod.json</c> or <c>module.json</c> "kind").</summary>
public enum ModKind
{
    /// <summary>A feature others may depend on.</summary>
    Feature,
    /// <summary>A league: not a mod or a module, kept so an old manifest says what is wrong.</summary>
    League,
    /// <summary>A small script.</summary>
    Script,
}

/// <summary>A mod's <c>mod.json</c>.</summary>
/// <param name="Id">The folder name under <c>Mods/Binaries/</c>, lower case: what other mods depend on.</param>
/// <param name="Version">Free text, shown by /mod.</param>
/// <param name="Kind">See <see cref="ModKind"/>.</param>
/// <param name="Depends">Ids loaded before this mod; it does not load while one is missing or failed.</param>
/// <param name="Assembly">The file name of the mod's assembly in its folder, <c>&lt;id&gt;.dll</c> by default.</param>
/// <param name="Enabled">Whether it loads until /mod_enable or /mod_disable says otherwise; true by default.</param>
public sealed record ModManifest(string Id, string Version, ModKind Kind, IReadOnlyList<string> Depends, string Assembly, bool Enabled = true);

