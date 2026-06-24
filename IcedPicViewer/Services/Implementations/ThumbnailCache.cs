// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Hand-rolled LRU for the thumbnail cache. Cap is intentionally modest:
/// a 400px BitmapImage averages ~150-400 KB, so 200 entries ≈ 30-80 MB
/// worst case instead of "unbounded" growth on a long session.
///
/// <para>
/// Implementation is a <see cref="Dictionary{TKey,TValue}"/> + doubly-
/// linked <see cref="LinkedList{T}"/> under a single lock. <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// would avoid the lock but the LRU's whole point is the move-to-front
/// on hit — that's not lock-free, and a lock is simpler than a hand-
/// rolled CAS loop. The critical sections are 1-2 dictionary/linkedlist
/// operations each, so contention is well under the 6-wide semaphore
/// the gallery already uses to bound in-flight thumbnail loads.
/// </para>
/// </summary>
public sealed class ThumbnailCache : IThumbnailCache
{
    private const int Capacity = 200;

    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly object _lock = new();

    private readonly record struct Entry(string Key, BitmapImage Image);

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
            else if (_map.Count >= Capacity)
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
