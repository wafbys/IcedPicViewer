// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;

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
/// A 400 px BitmapImage averages ~150-400 KB. We target roughly 1 %
/// of available physical memory for the cache (so 1 GB free → ~10 MB
/// of thumbnails → ~40-60 entries; 4 GB free → ~40 MB → ~150-260
/// entries; 8 GB+ free → capped at the upper bound). The actual cap
/// is clamped to <see cref="MinCapacity"/> / <see cref="MaxCapacity"/>
/// so we never end up with a degenerate cache on either end (a 5-entry
/// cache thrashes on a big gallery; a 5000-entry cache wastes a GB on
/// a machine that doesn't have it).
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
    /// corresponds to ~100 MB of thumbnail memory at the high end of
    /// the per-entry size range. Beyond this, the marginal hit rate
    /// gain doesn't justify the working-set pressure.</summary>
    private const int MaxCapacity = 500;

    /// <summary>Average per-entry footprint used to translate the
    /// memory budget into an entry count. ~300 KB lands in the middle
    /// of the 150-400 KB range observed for 400 px BitmapImage across
    /// JPEG / PNG / WebP inputs.</summary>
    private const int AvgEntryBytes = 300 * 1024;

    /// <summary>Fraction of available physical memory to dedicate to
    /// the cache. 1 % is a conservative number — bigger would risk
    /// pushing the OS into swapping when the user opens a video at
    /// the same time as scrolling a full gallery.</summary>
    private const double MemoryFraction = 0.01;

    private readonly int _capacity;

    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly object _lock = new();

    private readonly record struct Entry(string Key, BitmapImage Image);

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

    public bool TryGet(string key, out BitmapImage? image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Hit: move to tail (most recently used). _order.Remove
                // is O(1) for a doubly-linked list, so the cost is
                // bounded regardless of cache size.
                _order.Remove(node);
                _order.AddLast(node);
                image = node.Value.Image;
                return true;
            }
            image = null;
            return false;
        }
    }

    public void Store(string key, BitmapImage image)
    {
        lock (_lock)
        {
            // Replace an existing entry: drop the old node before
            // adding the new one so the count invariant stays tight.
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                // Evict the least-recently-used entry (head of the
                // linked list). _order.First is null only on an empty
                // list, which the capacity check above rules out.
                var oldest = _order.First;
                if (oldest is not null)
                {
                    _order.RemoveFirst();
                    _map.Remove(oldest.Value.Key);
                }
            }

            var node = new LinkedListNode<Entry>(new Entry(key, image));
            _order.AddLast(node);
            _map[key] = node;
        }
    }
}
