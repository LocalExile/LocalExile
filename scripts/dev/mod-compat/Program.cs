using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

// Run after the Release solution build. An optional local ZIP directory enables offline checks.
if (args.Length > 2)
{
    Console.Error.WriteLine("Usage: ModCompat [server-checkout [release-zip-directory]]");
    return 2;
}
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var gateway = Path.Combine(root, "src/GameServer.Gateway/bin/Release/net10.0");
if (!File.Exists(Path.Combine(gateway, "GameServer.Gateway.dll")))
    throw new FileNotFoundException($"Build the Release server first: {gateway}");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(gateway);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var manifest in Directory.EnumerateFiles(Path.Combine(root, "Modules"), "module.json", SearchOption.AllDirectories))
{
    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
    if (!json.RootElement.TryGetProperty("assembly", out var assembly)) continue;
    var path = Path.Combine(Path.GetDirectoryName(manifest)!, assembly.GetString()!);
    modules.Add(Path.GetFileNameWithoutExtension(path));
    resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
}

var releases = JsonSerializer.Deserialize<Release[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "releases.json")),
    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
using var http = new HttpClient();
int failures = 0;
foreach (var release in releases)
{
    var file = $"{release.Id}-{release.Version}.zip";
    var url = $"https://github.com/LocalExile/LocalExile-Mods/releases/download/{release.Id}-v{release.Version}/{file}";
    byte[] bytes = args.Length == 2 ? File.ReadAllBytes(Path.Combine(args[1], file)) : await http.GetByteArrayAsync(url);
    if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"SHA-256 mismatch for {file}");
    using var zip = new ZipArchive(new MemoryStream(bytes));
    var entryManifest = zip.GetEntry("mod.json") ?? throw new InvalidDataException($"Missing mod.json in {file}");
    using var manifestStream = entryManifest.Open();
    using var manifest = JsonDocument.Parse(manifestStream);
    var info = manifest.RootElement;
    var owner = $"{release.Id} {release.Version}";
    var mainDll = info.TryGetProperty("assembly", out var entry) ? entry.GetString()! : release.Id + ".dll";
    if (info.GetProperty("id").GetString() != release.Id || info.GetProperty("version").GetString() != release.Version
        || zip.GetEntry(mainDll) is null)
        throw new InvalidDataException($"Unexpected mod manifest or missing {mainDll} in {file}");
    var dependencies = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
    if (info.TryGetProperty("depends", out var depends))
        foreach (var dependency in depends.EnumerateArray()) dependencies.Add(dependency.GetString()!);
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    int count = 0;
    foreach (var dll in zip.Entries.Where(e => e.FullName == e.Name && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
    {
        using var input = dll.Open();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        buffer.Position = 0;
        using var mod = ModuleDefinition.ReadModule(buffer, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var reference in mod.GetTypeReferences().Cast<MemberReference>().Concat(mod.GetMemberReferences()))
        {
            var type = reference as TypeReference ?? reference.DeclaringType;
            if (type.Scope is not AssemblyNameReference scope) continue;
            if (!scope.Name.StartsWith("GameServer.", StringComparison.Ordinal) && !dependencies.Contains(scope.Name)) continue;
            count++;
            if (!Resolves(reference)) missing.Add($"{dll.Name} requires [{scope.Name}] {reference.FullName}");
        }
    }
    Console.WriteLine($"{owner}: checked {count} server references, {missing.Count} missing");
    foreach (var message in missing) Console.Error.WriteLine($"{owner}: {message}");
    failures += missing.Count;
}
return failures == 0 ? 0 : 1;

static bool Resolves(MemberReference reference)
{
    try
    {
        var target = reference.Resolve();
        if (reference is MethodReference method && target is MethodDefinition definition)
            return method.HasThis == definition.HasThis;
        return target is not null;
    }
    catch (AssemblyResolutionException)
    {
        return false;
    }
}

record Release(string Id, string Version, string Sha256);
