using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Jellyfin.Plugin.VgmLoop.Loop;

/// <summary>
/// A cache of per-file results keyed by path + size + mtime, so an edited file is read again. Concurrent requests for
/// the same file share one read. When full it starts over, which is cheap next to reading files.
/// </summary>
/// <typeparam name="T">Cached value.</typeparam>
public sealed class FileCache<T>
{
    private readonly ConcurrentDictionary<string, Lazy<T>> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileCache{T}"/> class.
    /// </summary>
    /// <param name="capacity">Entries kept before the cache starts over.</param>
    public FileCache(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>Gets the number of cached entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets the cached value for a file, reading it with <paramref name="read"/> when the file is new or changed.
    /// A read that throws is not cached.
    /// </summary>
    /// <param name="file">The file (must exist).</param>
    /// <param name="read">Reads the value; gets the file and its version string (<c>size-mtimeTicks</c>).</param>
    /// <returns>The value.</returns>
    public T GetOrRead(FileInfo file, Func<FileInfo, string, T> read)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(read);
        var version = Version(file);
        var key = file.FullName + "|" + version;
        if (!_entries.TryGetValue(key, out var lazy))
        {
            if (_entries.Count >= _capacity)
            {
                _entries.Clear();
            }

            lazy = _entries.GetOrAdd(key, _ => new Lazy<T>(() => read(file, version), LazyThreadSafetyMode.ExecutionAndPublication));
        }

        try
        {
            return lazy.Value;
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>
    /// The version string of a file: size and last write time.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <returns><c>size-mtimeTicks</c>.</returns>
    public static string Version(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return string.Create(CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");
    }
}
