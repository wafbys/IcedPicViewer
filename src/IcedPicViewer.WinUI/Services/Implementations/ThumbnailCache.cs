// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Hand-rolled LRU for the thumbnail cache. Capacity is sized to
/// available physical memory at construction time so a 32 GB
/// workstation with 4 GB free gets a bigger working set than a
/// 4 GB laptop that's already swapping — the cap is a soft "what's
/// reasonable to spend on thumbnails" budget, not a hard code-level
/// constant.
///
/// <para>
/// A 768-edge SoftwareBitmap is ~1–2 MB raw. Budget is ~1 % of
/// available physical memory, clamped to
/// <see cref="MinCapacity"/> / <see cref="MaxCapacity"/>.
/// </para>
///
/// <para>
/// Implementation is a <see cref="Dictionary{TKey,TValue}"/> + doubly-
/// linked <see cref="LinkedList{T}"/> under a single lock.
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// would avoid the lock but the LRU's whole point is the move-to-front
/// on hit — that's not lock-free, and a lock is simpler than a hand-
/// rolled CAS loop. The critical sections are 1-2 dictionary/linkedlist
/// operations each, so contention is well under the 6-wide semaphore
/// the gallery already uses to bound in-flight thumbnail loads.
/// </para>
/// </summary>
public sealed class ThumbnailCache : IThumbnailCache
{
    /// <summary>Minimum capacity — even a memory-constrained machine
    /// gets enough room to avoid thrashing on a small gallery.</summary>
    private const int MinCapacity = 50;

    /// <summary>Maximum capacity — a generous upper bound that
    /// corresponds to ~240 MB of 768-edge BGRA at the high end.</summary>
    private const int MaxCapacity = 200;

    /// <summary>Average per-entry footprint for a 768-edge BGRA thumb.</summary>
    private const int AvgEntryBytes = 1200 * 1024;

    /// <summary>Fraction of available physical memory to dedicate to
    /// the cache. 1 % is a conservative number — bigger would risk
    /// pushing the OS into swapping when the user opens a video at
    /// the same time as scrolling a full gallery.</summary>
    private const double MemoryFraction = 0.01;

    private readonly int _capacity;

    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly object _lock = new();

    private readonly record struct Entry(string Key, CachedThumb Thumb);

    public ThumbnailCache() : this(ComputeCapacityFromAvailableMemory()) { }

    /// <summary>
    /// Test-friendly constructor that takes an explicit capacity
    /// (so unit tests can pin a known size without depending on
    /// the host machine's free RAM).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ThumbnailCache(int capacity)
    {
        _capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
    }

    /// <summary>
    /// Query the system for available physical memory and translate
    /// that into a cache capacity. P/Invokes <c>GlobalMemoryStatusEx</c>
    /// from kernel32; on any failure (unlikely on a real Windows
    /// install) falls back to the <see cref="MaxCapacity"/> default so
    /// the gallery at least gets a usable cache.
    /// </summary>
    private static int ComputeCapacityFromAvailableMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                Trace.TraceWarning("ThumbnailCache: GlobalMemoryStatusEx failed, using MaxCapacity default");
                return MaxCapacity;
            }
            // ullAvailPhys is the physical memory not currently in use
            // — the most relevant number for "how much can I spend
            // before the OS starts swapping". On a 4 GB laptop with
            // 1 GB free this gives ~10 MB of cache (50 entries); on a
            // workstation with 16 GB free it gives ~160 MB (capped at
            // MaxCapacity = 500 entries ≈ 100-150 MB).
            var availableBytes = (long)status.ullAvailPhys;
            var budget = (long)(availableBytes * MemoryFraction);
            var entries = (int)(budget / AvgEntryBytes);
            return entries;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ThumbnailCache: capacity probe failed, using MaxCapacity default: {ex.Message}");
            return MaxCapacity;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public bool TryGet(string key, out CachedThumb? thumb)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                thumb = node.Value.Thumb;
                return true;
            }
            thumb = null;
            return false;
        }
    }

    public void Store(string key, CachedThumb thumb)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                var oldest = _order.First;
                if (oldest is not null)
                {
                    _order.RemoveFirst();
                    _map.Remove(oldest.Value.Key);
                }
            }

            var node = new LinkedListNode<Entry>(new Entry(key, thumb));
            _order.AddLast(node);
            _map[key] = node;
        }
    }
}
