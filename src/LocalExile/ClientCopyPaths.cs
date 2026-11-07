using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalExile;

/// <summary>Resuming a private copy must never write through links into an original client or another folder.</summary>
static class ClientCopyPaths
{
    static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static void CheckRoots(string source, string staging)
    {
        string original = ResolveDirectory(source), candidate = ResolveDirectory(staging);
        if (Contains(original, candidate) || Contains(candidate, original))
            throw Invalid("The unfinished copy and original client overlap. Choose a LocalExile installation folder outside the original client.", staging);
        CheckTree(staging);
    }

    static bool Contains(string parent, string child) => parent.Equals(child, Comparison)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);

    static string ResolveDirectory(string path, int redirects = 0)
    {
        if (redirects > 32) throw Invalid("Too many folder links while resolving the client location.", path);
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in Path.GetRelativePath(current, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(true) ?? throw Invalid("A client folder link could not be resolved.", current);
                current = ResolveDirectory(target.FullName, redirects + 1);
            }
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    public static void CheckTree(string staging)
    {
        var root = new DirectoryInfo(staging);
        CheckDirectory(root);
        if (!root.Exists) return;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in directory.EnumerateFileSystemInfos())
                if (entry is DirectoryInfo child)
                {
                    CheckDirectory(child);
                    pending.Push(child);
                }
                else CheckFile(new FileInfo(entry.FullName));
    }

    public static void CheckTarget(string staging, string target)
    {
        string root = Path.GetFullPath(staging), full = Path.GetFullPath(target);
        if (!Contains(root, full) || root.Equals(full, Comparison)) throw Invalid("The copy target leaves the unfinished client folder.", target);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)!); directory is not null; directory = directory.Parent)
        {
            CheckDirectory(directory);
            if (directory.FullName.Equals(root, Comparison)) break;
        }
        CheckFile(new FileInfo(full));
    }

    static void CheckDirectory(DirectoryInfo directory)
    {
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw Invalid("Client.partial contains a linked folder and was kept. Use a separate LocalExile installation folder.", directory.FullName);
    }

    static void CheckFile(FileInfo file)
    {
        if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            throw Invalid("Client.partial contains a linked file and was kept. Use a separate LocalExile installation folder.", file.FullName);
        if (!file.Exists) return;
        try
        {
            using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint links;
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                links = information.Links;
            }
            else if (OperatingSystem.IsLinux())
            {
                // statx has a fixed 256-byte ABI; request stx_nlink from the already-open descriptor.
                var information = new byte[256];
                if (Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 4, information) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if ((BinaryPrimitives.ReadUInt32LittleEndian(information) & 4) == 0)
                    throw new IOException("The filesystem did not report the file's hard-link count.");
                links = BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(16));
            }
            else throw new PlatformNotSupportedException("Cannot establish whether the staged file is independent.");
            if (links != 1)
                throw Invalid("Client.partial contains a shared file and was kept. Use a separate LocalExile installation folder.", file.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
        {
            throw Invalid("The unfinished copy could not be checked for linked files. It was kept.", $"{file.FullName}: {e.Message}");
        }
    }

    public static bool NeedsCopy(FileInfo source, FileInfo target) =>
        !target.Exists || target.Length != source.Length || target.LastWriteTimeUtc != source.LastWriteTimeUtc;

    static LauncherException Invalid(string message, string details) => new(ExitCode.Client, message, "staging-path-invalid", details);

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] information);
}
