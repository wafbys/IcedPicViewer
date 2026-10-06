// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Binary, per-root <see cref="IScanCache"/>. One file per scan root under an
/// injected cache directory (the composition root passes
/// <c>AppDataPaths.ScanCacheDir</c>; tests pass a temp dir).
///
/// <para>
/// The format is a small header (magic / version / root / extension signature /
/// timestamp) followed by the per-directory index. All reads are guarded: a
/// bad magic, version or count, a truncated file, or any I/O error makes
/// <see cref="Load"/> return null and the caller falls back to a full scan —
/// the cache is a hint, never a source of truth.
/// </para>
///
/// <para>
/// Writes go to a sibling temp file and are then <c>File.Move</c>d over the
/// destination, so a crash mid-write can never leave a half-written snapshot
/// in place.
/// </para>
/// </summary>
public sealed class FileScanCache : IScanCache
{
    private const string Magic = "IPVS";
    private const int Version = 1;

    // Safety net only: the data is already validated by directory mtime (dirs /
    // loose files) and archive (size, mtime). The TTL bounds the damage if the
    // filesystem did not update a directory mtime for some operation.
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private const int MaxFiles = 128;
    private const long MaxTotalBytes = 64L * 1024 * 1024;

    // Sanity caps so a corrupt count cannot trigger a huge allocation.
    private const int MaxDirectories = 5_000_000;
    private const int MaxListItems = 20_000_000;

    private readonly string _cacheRoot;
    private readonly object _ioLock = new();

    public FileScanCache(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(cacheRoot);
    }

    public ScanCacheSnapshot? Load(string rootPath, string extensionsSignature)
    {
        try
        {
            var file = PathFor(rootPath);
            if (!File.Exists(file)) return null;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Ttl)
            {
                TryDelete(file);
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || Encoding.ASCII.GetString(magic) != Magic) return null;
            if (reader.ReadInt32() != Version) return null;

            var storedRoot = reader.ReadString();
            if (!string.Equals(Normalize(storedRoot), Normalize(rootPath), StringComparison.OrdinalIgnoreCase))
                return null;

            var storedSignature = reader.ReadString();
            if (!string.Equals(storedSignature, extensionsSignature, StringComparison.Ordinal))
                return null;

            var snapshot = new ScanCacheSnapshot
            {
                RootPath = storedRoot,
                ExtensionsSignature = storedSignature,
                SavedUtcTicks = reader.ReadInt64(),
            };

            var dirCount = ReadCount(reader, MaxDirectories);
            for (var i = 0; i < dirCount; i++)
            {
                var dir = ReadDirectory(reader);
                snapshot.Directories[dir.RelativePath] = dir;
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"FileScanCache.Load failed for {rootPath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Save(ScanCacheSnapshot snapshot)
    {
        var file = PathFor(snapshot.RootPath);
        var temp = file + ".tmp";
        try
        {
            lock (_ioLock)
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Encoding.ASCII.GetBytes(Magic));
                    writer.Write(Version);
                    writer.Write(snapshot.RootPath);
                    writer.Write(snapshot.ExtensionsSignature);
                    writer.Write(snapshot.SavedUtcTicks);
                    writer.Write(snapshot.Directories.Count);
                    foreach (var dir in snapshot.Directories.Values)
                        WriteDirectory(writer, dir);
                }

                File.Move(temp, file, overwrite: true);
            }

            Prune();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"FileScanCache.Save failed for {snapshot.RootPath}: {ex.GetType().Name}: {ex.Message}");
            TryDelete(temp);
        }
    }

    public void Invalidate(string rootPath)
    {
        try
        {
            lock (_ioLock)
            {
                TryDelete(PathFor(rootPath));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"FileScanCache.Invalidate failed for {rootPath}: {ex.Message}");
        }
    }

    private static void WriteDirectory(BinaryWriter writer, CachedDirectory dir)
    {
        writer.Write(dir.RelativePath);
        writer.Write(dir.DirMtimeUtcTicks);

        writer.Write(dir.Subdirectories.Count);
        foreach (var name in dir.Subdirectories) writer.Write(name);

        writer.Write(dir.Files.Count);
        foreach (var f in dir.Files)
        {
            writer.Write(f.Name);
            writer.Write((byte)f.Kind);
        }

        writer.Write(dir.Archives.Count);
        foreach (var a in dir.Archives)
        {
            writer.Write(a.Name);
            writer.Write(a.Size);
            writer.Write(a.MtimeUtcTicks);
            writer.Write(a.Entries.Count);
            foreach (var e in a.Entries)
            {
                writer.Write(e.Key);
                writer.Write((byte)e.Kind);
            }
        }
    }

    private static CachedDirectory ReadDirectory(BinaryReader reader)
    {
        var dir = new CachedDirectory
        {
            RelativePath = reader.ReadString(),
            DirMtimeUtcTicks = reader.ReadInt64(),
        };

        var subdirCount = ReadCount(reader, MaxListItems);
        for (var i = 0; i < subdirCount; i++) dir.Subdirectories.Add(reader.ReadString());

        var fileCount = ReadCount(reader, MaxListItems);
        for (var i = 0; i < fileCount; i++)
            dir.Files.Add(new CachedFile(reader.ReadString(), (MediaKind)reader.ReadByte()));

        var archiveCount = ReadCount(reader, MaxListItems);
        for (var i = 0; i < archiveCount; i++)
        {
            var archive = new CachedArchive
            {
                Name = reader.ReadString(),
                Size = reader.ReadInt64(),
                MtimeUtcTicks = reader.ReadInt64(),
            };
            var entryCount = ReadCount(reader, MaxListItems);
            for (var j = 0; j < entryCount; j++)
                archive.Entries.Add(new CachedArchiveEntry(reader.ReadString(), (MediaKind)reader.ReadByte()));
            dir.Archives.Add(archive);
        }

        return dir;
    }

    private static int ReadCount(BinaryReader reader, int max)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > max)
            throw new InvalidDataException($"Scan cache count out of range: {count}");
        return count;
    }

    private string PathFor(string rootPath)
    {
        var normalized = Normalize(rootPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
        return Path.Combine(_cacheRoot, $"scan_{hash}.bin");
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_cacheRoot).GetFiles("scan_*.bin");
            var total = files.Sum(f => f.Length);
            if (files.Length <= MaxFiles && total <= MaxTotalBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (files.Length - 1 <= MaxFiles && total <= MaxTotalBytes) break;
                var length = file.Length;
                TryDelete(file.FullName);
                total -= length;
                files = files.Where(f => !f.FullName.Equals(file.FullName, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"FileScanCache.Prune failed: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"FileScanCache delete failed for {path}: {ex.Message}");
        }
    }
}
