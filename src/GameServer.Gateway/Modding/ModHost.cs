using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Modding;

/// <summary>
/// The mods in <c>Mods/Binaries/&lt;id&gt;/</c> (Agents/docs/dev/modding.md): each one's <c>mod.json</c> and assembly, in a collectible
/// load context of its own (<see cref="ModLoadContext"/>), loaded after the mods it depends on. A change in a mod's folder
/// (a build, a new or deleted folder) reloads that mod and the mods that depend on it; a reload starts their state over.
/// </summary>
/// <remarks>
/// Every hook a mod registers is held here, in the host's lists, and nowhere else in the server: unloading a mod removes
/// them all, so no static of the server keeps one of its types alive and the context can be collected.
/// </remarks>
sealed class ModHost(string folder, Action<string> log) : IDisposable
{
    /// <summary>A change was applied (a mod loaded, unloaded or failed): the line it logged.</summary>
    public event Action<string>? Changed;

    /// <summary>The mods' folder, relative to the working directory (the repository root) unless absolute.</summary>
    public string Folder { get; } = folder;

    readonly Lock _gate = new();
    readonly Dictionary<string, LoadedMod> _loaded = new(StringComparer.Ordinal);
    // Why a folder's mod is not loaded, as last logged (logged again only when it changes).
    readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    readonly Dictionary<string, Task<bool>> _unloads = new(StringComparer.Ordinal);
    readonly Dictionary<string, (Timer Timer, int Busy)> _pending = new(StringComparer.Ordinal);
    // /mod_enable and /mod_disable's choices (StateFile), over each manifest's "enabled"; the folders turned off by either.
    readonly Dictionary<string, bool> _choices = new(StringComparer.Ordinal);
    readonly HashSet<string> _off = new(StringComparer.Ordinal);
    FileSystemWatcher? _watcher;

    // The hooks. Each list is swapped whole (ImmutableInterlocked), so a dispatch iterates a snapshot without a lock.
    ImmutableDictionary<string, CommandHook> _commands = ImmutableDictionary.Create<string, CommandHook>(StringComparer.OrdinalIgnoreCase);
    readonly HookList<Func<IPlayer, double, Task>> _ticks = new();
    readonly HookList<(ushort Opcode, Func<Packet, Task<bool>> Run)> _packets = new();
    readonly HookList<Func<IPlayer, Task>> _areas = new();
    readonly HookList<Func<MonsterKill, Task>> _kills = new();
    readonly HookList<Action<DropRoll>> _drops = new();
    readonly HookList<Action<ExperienceGain>> _experience = new();
    readonly HookList<Action<StatSheet>> _stats = new();

    sealed record CommandHook(LoadedMod Mod, string Usage, Func<ChatCommand, Task> Run);

    /// <summary>Folders that hold a <c>mod.json</c>, and how long a change waits for the rest of a build's files.</summary>
    public const string ManifestFile = "mod.json";
    /// <summary>The choices of /mod_enable and /mod_disable, in the mods' folder: an "id on" or "id off" per line.</summary>
    public const string StateFile = "mod-state.txt";
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);
    const int BusyRetries = 5;

    /// <summary>Loads every mod there is, then watches the folder (created when missing).</summary>
    public void Start(bool watch = true)
    {
        Directory.CreateDirectory(Folder);
        lock (_gate)
        {
            ReadChoices();
            LoadMissing();
        }
        if (!watch) return;
        _watcher = new FileSystemWatcher(Path.GetFullPath(Folder))
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, e) => Touched(e.FullPath);
        _watcher.Created += (_, e) => Touched(e.FullPath);
        _watcher.Deleted += (_, e) => Touched(e.FullPath);
        _watcher.Renamed += (_, e) => { Touched(e.OldFullPath); Touched(e.FullPath); };
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            foreach (var (timer, _) in _pending.Values) timer.Dispose();
            _pending.Clear();
            foreach (string id in UnloadOrder([.. _loaded.Keys])) Unload(id);
        }
    }

    /// <summary>A loaded mod's id, version, kind and hooks, or a folder's error.</summary>
    public sealed record Status(string Id, string? Version, ModKind? Kind, int Hooks, string? Error);

    public IReadOnlyList<Status> Mods()
    {
        lock (_gate)
            return [.. _loaded.Values.Select(m => new Status(m.Manifest.Id, m.Manifest.Version, m.Manifest.Kind, m.Hooks, null))
                .Concat(_failed.Select(f => new Status(f.Key, null, null, 0, f.Value)))
                .Concat(_off.Select(id => new Status(id, null, null, 0, "disabled"))).OrderBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>Reloads a mod (and the mods that depend on it) from its folder now; every mod when id is null.</summary>
    public void Reload(string? id = null)
    {
        lock (_gate)
        {
            if (id is null) foreach (string all in UnloadOrder([.. _loaded.Keys])) Unload(all);
            else foreach (string affected in UnloadOrder(Dependents(id))) Unload(affected);
            if (id is null) _failed.Clear(); else _failed.Remove(id);
            LoadMissing();
        }
    }

    /// <summary>Turns a mod on or off, now and at every start; off also unloads the mods that depend on it, and on loads
    /// them again. False when the folder has no such mod.</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            if (id != Path.GetFileName(id) || !File.Exists(Path.Combine(Folder, id, ManifestFile))) return false;
            _choices[id] = enabled;
            File.WriteAllLines(Path.Combine(Folder, StateFile), _choices.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {(c.Value ? "on" : "off")}"));
            if (!enabled) foreach (string affected in UnloadOrder(Dependents(id))) Unload(affected);
            _failed.Remove(id);
            LoadMissing();
            return true;
        }
    }

    void ReadChoices()
    {
        string path = Path.Combine(Folder, StateFile);
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var id, var state and ("on" or "off")]) _choices[id] = state == "on";
    }

    /// <summary>Completes when the mod's last unload was collected (true) or still was not after a few seconds
    /// (false: something outside the mod still holds one of its objects).</summary>
    public Task<bool> Unloaded(string id)
    {
        lock (_gate) return _unloads.GetValueOrDefault(id) ?? Task.FromResult(true);
    }

    // ---- What the Gateway calls -------------------------------------------------------------------------------------

    /// <summary>The mods' command usages, for /commands.</summary>
    public IEnumerable<(string Name, string Usage)> Commands => _commands.Select(c => (c.Key, c.Value.Usage));

    /// <summary>Runs a mod's command; false when no mod has one of that name.</summary>
    public async Task<bool> RunCommand(IPlayer player, string name, IReadOnlyList<string> args)
    {
        if (!_commands.TryGetValue(name, out var command)) return false;
        if (!await Guard(command.Mod, $"/{name}", () => command.Run(new ChatCommand(player, name, args))))
            await player.Tell($"/{name} failed (mod {command.Mod.Manifest.Id}); the error is in the Gateway's log. Usage: {command.Usage}");
        return true;
    }

    public async Task Tick(IPlayer player, double seconds)
    {
        foreach (var hook in _ticks.Items) await Guard(hook.Mod, "OnTick", () => hook.Run(player, seconds));
    }

    /// <summary>Whether a mod wants client messages with this opcode (saves building a <see cref="Packet"/> for each).</summary>
    public bool WantsPacket(ushort opcode)
    {
        foreach (var hook in _packets.Items) if (hook.Run.Opcode == opcode) return true;
        return false;
    }

    /// <summary>True when a mod took the message (the Gateway's handler is skipped).</summary>
    public async Task<bool> Packet(IPlayer player, ushort opcode, byte[] bytes)
    {
        var packet = new Packet(player, opcode, bytes);
        foreach (var hook in _packets.Items)
        {
            if (hook.Run.Opcode != opcode) continue;
            bool taken = false;
            if (await Guard(hook.Mod, $"OnPacket 0x{opcode:x4}", async () => taken = await hook.Run.Run(packet)) && taken) return true;
        }
        return false;
    }

    public async Task AreaGenerated(IPlayer player)
    {
        foreach (var hook in _areas.Items) await Guard(hook.Mod, "OnAreaGenerated", () => hook.Run(player));
    }

    public async Task MonsterKilled(MonsterKill kill)
    {
        foreach (var hook in _kills.Items) await Guard(hook.Mod, "OnMonsterKilled", () => hook.Run(kill));
    }

    /// <summary>The mods' changes to a kill's drops; null when no mod changes drops.</summary>
    public DropRoll? Drops(IPlayer player, MonsterInfo monster)
    {
        var hooks = _drops.Items;
        if (hooks.IsEmpty) return null;
        var roll = new DropRoll(player, monster);
        foreach (var hook in hooks) Guard(hook.Mod, "ModifyDrops", () => hook.Run(roll));
        return roll;
    }

    public uint Experience(IPlayer player, MonsterInfo monster, uint experience)
    {
        var hooks = _experience.Items;
        if (hooks.IsEmpty) return experience;
        var gain = new ExperienceGain(player, monster, experience);
        foreach (var hook in hooks) Guard(hook.Mod, "ModifyExperience", () => hook.Run(gain));
        return gain.Experience;
    }

    /// <summary>Whether a mod adds stats (saves building a <see cref="StatSheet"/> for every stat snapshot).</summary>
    public bool ChangesStats => !_stats.Items.IsEmpty;

    public IReadOnlyDictionary<string, int> Stats(IPlayer player)
    {
        var hooks = _stats.Items;
        if (hooks.IsEmpty) return ImmutableDictionary<string, int>.Empty;
        var sheet = new StatSheet(player);
        foreach (var hook in hooks) Guard(hook.Mod, "ModifyStats", () => hook.Run(sheet));
        return sheet.Added;
    }

    async Task<bool> Guard(LoadedMod mod, string hook, Func<Task> run)
    {
        try { await run(); return true; }
        catch (Exception ex) { Failed(mod, hook, ex); return false; }
    }

    bool Guard(LoadedMod mod, string hook, Action run)
    {
        try { run(); return true; }
        catch (Exception ex) { Failed(mod, hook, ex); return false; }
    }

    void Failed(LoadedMod mod, string hook, Exception ex) =>
        log($"[mods] {mod.Manifest.Id} {hook} failed (skipped): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    // ---- Loading ----------------------------------------------------------------------------------------------------

    void Touched(string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(Folder), path);
        string id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (id is "" or "." or ".." or StateFile || relative.StartsWith("..", StringComparison.Ordinal)) return;
        Later(id, busy: 0);
    }

    // One reload for a burst of changes (a build writes the dll, the pdb and the manifest one after another).
    void Later(string id, int busy)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var pending)) { pending.Timer.Change(Settle, Timeout.InfiniteTimeSpan); return; }
            var timer = new Timer(_ => Apply(id), null, Timeout.Infinite, Timeout.Infinite);
            _pending[id] = (timer, busy);
            timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    void Apply(string id)
    {
        bool busy;
        int tries;
        lock (_gate)
        {
            if (!_pending.Remove(id, out var pending)) return;
            pending.Timer.Dispose();
            tries = pending.Busy;
            foreach (string affected in UnloadOrder(Dependents(id))) Unload(affected);
            _failed.Remove(id);
            LoadMissing();
            busy = _failed.TryGetValue(id, out string? error) && error.StartsWith("busy:", StringComparison.Ordinal);
        }
        // A file still being written: try again a few times before leaving the error.
        if (busy && tries < BusyRetries) Later(id, tries + 1);
    }

    // Every folder whose mod is not loaded, each after the mods it depends on; what cannot load keeps its reason.
    void LoadMissing()
    {
        var waiting = new Dictionary<string, (ModManifest Manifest, string Folder)>(StringComparer.Ordinal);
        _off.Clear();
        foreach (string dir in Directory.Exists(Folder) ? Directory.GetDirectories(Folder) : [])
        {
            string id = Path.GetFileName(dir);
            if (_loaded.ContainsKey(id)) continue;
            string manifestPath = Path.Combine(dir, ManifestFile);
            if (!File.Exists(manifestPath)) { _failed.Remove(id); continue; }   // not a mod (yet)
            try
            {
                var manifest = ReadManifest(id, File.ReadAllText(manifestPath));
                if (_choices.GetValueOrDefault(id, manifest.Enabled)) waiting[id] = (manifest, dir);
                else { _off.Add(id); _failed.Remove(id); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(id, $"busy: {ex.Message}"); }
            catch (Exception ex) when (ex is JsonException or FormatException) { Fail(id, $"{ManifestFile}: {ex.Message}"); }
        }
        foreach (string gone in _failed.Keys.Where(id => !Directory.Exists(Path.Combine(Folder, id))).ToList()) _failed.Remove(gone);
        bool progress = true;
        while (progress && waiting.Count > 0)
        {
            progress = false;
            foreach (var (id, (manifest, dir)) in waiting.ToList())
            {
                if (!manifest.Depends.All(_loaded.ContainsKey)) continue;
                waiting.Remove(id);
                progress |= Load(manifest, dir);
            }
        }
        foreach (var (id, (manifest, _)) in waiting)
        {
            var missing = manifest.Depends.Where(d => !_loaded.ContainsKey(d)).ToList();
            Fail(id, $"needs {string.Join(", ", missing.Select(d => _off.Contains(d) ? $"{d} (disabled)" : _failed.ContainsKey(d) || waiting.ContainsKey(d) ? $"{d} (not loaded)" : $"{d} (no such mod)"))}");
        }
    }

    bool Load(ModManifest manifest, string dir)
    {
        string id = manifest.Id;
        var context = new ModLoadContext(id, Path.GetFullPath(dir), [.. manifest.Depends.Select(d => _loaded[d].Context)]);
        var mod = new LoadedMod(manifest, Path.GetFullPath(dir), context);
        try
        {
            string path = Path.Combine(mod.Folder, manifest.Assembly);
            if (!File.Exists(path)) throw new FileNotFoundException($"no {manifest.Assembly} in the mod's folder (built yet?)");
            var assembly = context.LoadCopy(path);
            var entries = assembly.GetExportedTypes()
                .Where(t => typeof(IMod).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToList();
            if (entries.Count == 0) throw new InvalidOperationException($"{manifest.Assembly} has no public class that implements IMod");
            var api = new Context(this, mod);
            foreach (var type in entries)
            {
                var entry = (IMod)Activator.CreateInstance(type)!;
                mod.Entries.Add(entry);
                entry.Load(api);
            }
        }
        catch (Exception ex)
        {
            mod.Unloaded = true;
            Remove(mod);
            foreach (var disposable in mod.Entries.OfType<IDisposable>()) Guard(mod, "Dispose", disposable.Dispose);
            _unloads[id] = Collect(Unload(mod));
            if (ex is TypeInitializationException or System.Reflection.TargetInvocationException && ex.InnerException is { } inner) ex = inner;
            Fail(id, ex is IOException and not FileNotFoundException ? $"busy: {ex.Message}" : $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        _loaded[id] = mod;
        _failed.Remove(id);
        Tell($"[mods] loaded {id} {manifest.Version} ({manifest.Kind.ToString().ToLowerInvariant()}, {mod.Hooks} hook(s))");
        return true;
    }

    void Unload(string id)
    {
        if (!_loaded.Remove(id, out var mod)) return;
        mod.Unloaded = true;
        Remove(mod);
        foreach (var disposable in mod.Entries.OfType<IDisposable>())
            Guard(mod, "Dispose", disposable.Dispose);
        _unloads[id] = Collect(Unload(mod));
        Tell($"[mods] unloaded {id}");
    }

    // Kept out of Unload(string) so no local of that frame holds the context while it is collected.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Context, string CopyDirectory) Unload(LoadedMod mod)
    {
        mod.Entries.Clear();
        var weak = new WeakReference(mod.Context);
        mod.Context.Unload();
        return (weak, mod.Context.CopyDirectory);
    }

    async Task<bool> Collect((WeakReference Context, string CopyDirectory) unloaded)
    {
        var (context, copyDirectory) = unloaded;
        for (int i = 0; i < 20 && context.IsAlive; i++)
        {
            await Task.Delay(i == 0 ? 0 : 100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        if (context.IsAlive) log("[mods] an unloaded mod is still in memory after 2 s: a thread, timer or task of it still runs, or something outside it holds one of its objects");
        else if (!ModLoadContext.TryDeleteCopy(copyDirectory)) log("[mods] could not remove an unloaded mod's private copy");
        return !context.IsAlive;
    }

    void Remove(LoadedMod mod)
    {
        ImmutableInterlocked.Update(ref _commands, c => c.RemoveRange(c.Where(e => e.Value.Mod == mod).Select(e => e.Key)));
        _ticks.Remove(mod); _packets.Remove(mod); _areas.Remove(mod); _kills.Remove(mod);
        _drops.Remove(mod); _experience.Remove(mod); _stats.Remove(mod);
    }

    void Fail(string id, string error)
    {
        if (_failed.TryGetValue(id, out string? last) && last == error) return;
        _failed[id] = error;
        if (!error.StartsWith("busy:", StringComparison.Ordinal)) Tell($"[mods] {id} not loaded: {error}");
    }

    void Write(string line) => log(line);

    void Tell(string line)
    {
        log(line);
        Changed?.Invoke(line);
    }

    // The mod and every loaded mod that depends on it, directly or not.
    List<string> Dependents(string id)
    {
        var all = new List<string> { id };
        for (int i = 0; i < all.Count; i++)
            foreach (var m in _loaded.Values)
                if (m.Manifest.Depends.Contains(all[i]) && !all.Contains(m.Manifest.Id)) all.Add(m.Manifest.Id);
        return all;
    }

    // Dependents before the mods they depend on.
    List<string> UnloadOrder(List<string> ids)
    {
        var order = new List<string>();
        void Visit(string id)
        {
            if (order.Contains(id)) return;
            foreach (var m in _loaded.Values)
                if (ids.Contains(m.Manifest.Id) && m.Manifest.Depends.Contains(id)) Visit(m.Manifest.Id);
            order.Add(id);
        }
        foreach (string id in ids) Visit(id);
        return order;
    }

    /// <summary>Reads a <c>mod.json</c>: <c>{"id", "version", "kind", "depends", "assembly", "enabled"}</c>, all optional; the id, when
    /// given, must be the folder's name.</summary>
    public static ModManifest ReadManifest(string folderName, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("not a JSON object");
        string? Text(string name) => doc.RootElement.EnumerateObject()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } p
            ? p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : throw new FormatException($"\"{name}\" is not a string")
            : null;
        string id = Text("id") ?? folderName;
        if (!id.Equals(folderName, StringComparison.Ordinal)) throw new FormatException($"id \"{id}\" is not the folder's name \"{folderName}\"");
        string kindText = Text("kind") ?? nameof(ModKind.Feature);
        if (!Enum.TryParse<ModKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            throw new FormatException($"kind \"{kindText}\" is not league, feature or script");
        var depends = new List<string>();
        if (doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("depends", StringComparison.OrdinalIgnoreCase)) is
            { Value.ValueKind: not JsonValueKind.Undefined } d)
        {
            if (d.Value.ValueKind != JsonValueKind.Array) throw new FormatException("\"depends\" is not a list of ids");
            foreach (var e in d.Value.EnumerateArray())
                depends.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FormatException("\"depends\" is not a list of ids"));
        }
        if (depends.Contains(id)) throw new FormatException("a mod cannot depend on itself");
        bool enabled = true;
        if (doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase)) is
            { Value.ValueKind: not JsonValueKind.Undefined } on)
            enabled = on.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException("\"enabled\" is not true or false"),
            };
        return new ModManifest(id, Text("version") ?? "0", kind, depends, Text("assembly") ?? id + ".dll", enabled);
    }

    sealed class LoadedMod(ModManifest manifest, string folder, ModLoadContext context)
    {
        public ModManifest Manifest { get; } = manifest;
        public string Folder { get; } = folder;
        public ModLoadContext Context { get; } = context;
        public List<IMod> Entries { get; } = [];
        public int Hooks;
        public volatile bool Unloaded;
    }

    sealed class HookList<T>
    {
        public sealed record Hook(LoadedMod Mod, T Run);
        ImmutableArray<Hook> _items = [];
        public ImmutableArray<Hook> Items => _items;
        public Hook Add(LoadedMod mod, T run)
        {
            var hook = new Hook(mod, run);
            ImmutableInterlocked.Update(ref _items, a => a.Add(hook));
            return hook;
        }
        public void Remove(Hook hook) => ImmutableInterlocked.Update(ref _items, a => a.Remove(hook));
        public void Remove(LoadedMod mod) => ImmutableInterlocked.Update(ref _items, a => a.RemoveAll(h => h.Mod == mod));
    }

    sealed class Handle(Action remove) : IDisposable
    {
        Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    // What a mod's Load gets. It refuses new hooks once its mod is unloaded (a task the mod left running).
    sealed class Context(ModHost host, LoadedMod mod) : IModContext
    {
        public ModManifest Manifest => mod.Manifest;
        public string Folder => mod.Folder;
        public void Log(string message) => host.Write($"[mods] {mod.Manifest.Id}: {message}");

        IDisposable Add<T>(HookList<T> list, T run)
        {
            if (mod.Unloaded) throw new InvalidOperationException($"mod {mod.Manifest.Id} is unloaded");
            var hook = list.Add(mod, run);
            Interlocked.Increment(ref mod.Hooks);
            return new Handle(() => { list.Remove(hook); Interlocked.Decrement(ref mod.Hooks); });
        }

        public IDisposable OnChatCommand(string name, string usage, Func<ChatCommand, Task> run)
        {
            if (mod.Unloaded) throw new InvalidOperationException($"mod {mod.Manifest.Id} is unloaded");
            name = name.TrimStart('/');
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) throw new ArgumentException($"\"{name}\" is not a command name");
            var hook = new CommandHook(mod, usage, run);
            if (!ImmutableInterlocked.TryAdd(ref host._commands, name, hook))
                throw new InvalidOperationException($"/{name} belongs to mod {host._commands[name].Mod.Manifest.Id}");
            Interlocked.Increment(ref mod.Hooks);
            return new Handle(() =>
            {
                ImmutableInterlocked.Update(ref host._commands, c => c.TryGetValue(name, out var h) && h == hook ? c.Remove(name) : c);
                Interlocked.Decrement(ref mod.Hooks);
            });
        }

        public IDisposable OnTick(Func<IPlayer, double, Task> tick) => Add(host._ticks, tick);
        public IDisposable OnPacket(ushort opcode, Func<Packet, Task<bool>> handle) => Add(host._packets, (opcode, handle));
        public IDisposable OnAreaGenerated(Func<IPlayer, Task> generated) => Add(host._areas, generated);
        public IDisposable OnMonsterKilled(Func<MonsterKill, Task> killed) => Add(host._kills, killed);
        public IDisposable ModifyDrops(Action<DropRoll> modify) => Add(host._drops, modify);
        public IDisposable ModifyExperience(Action<ExperienceGain> modify) => Add(host._experience, modify);
        public IDisposable ModifyStats(Action<StatSheet> modify) => Add(host._stats, modify);
    }
}
