// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Core.Tests.Media;

/// <summary>
/// The thumbnail phase timings exist so "where does the time actually go on this
/// machine?" can be answered from a log line. The accumulator and the summary
/// text are pinned here; the collection points (WIC / FFmpeg / cache) are
/// exercised by running the app.
/// </summary>
public sealed class ThumbnailTimingsTests
{
    public ThumbnailTimingsTests() => ThumbnailTimings.Reset();

    private static long Ticks(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void Add_AccumulatesCountAverageAndMax()
    {
        ThumbnailTimings.Add(ThumbnailPhase.ImageDecode, Ticks(10));
        ThumbnailTimings.Add(ThumbnailPhase.ImageDecode, Ticks(30));

        var stats = ThumbnailTimings.Snapshot().Phase(ThumbnailPhase.ImageDecode);

        Assert.Equal(2, stats.Count);
        Assert.Equal(20.0, stats.AverageMs, precision: 1);
        Assert.Equal(30.0, stats.MaxMs, precision: 1);
    }

    [Fact]
    public void Add_IgnoresNegativeDurations()
    {
        ThumbnailTimings.Add(ThumbnailPhase.Encode, -1);

        Assert.Equal(0, ThumbnailTimings.Snapshot().Phase(ThumbnailPhase.Encode).Count);
    }

    [Fact]
    public void Snapshot_TriesAreCacheProbes_HitsAreTheRest()
    {
        // 3 requests, 1 of which found a cached thumbnail (no source decode).
        ThumbnailTimings.Add(ThumbnailPhase.CacheProbe, Ticks(1));
        ThumbnailTimings.Add(ThumbnailPhase.ImageDecode, Ticks(50));
        ThumbnailTimings.Add(ThumbnailPhase.CacheProbe, Ticks(1));
        ThumbnailTimings.Add(ThumbnailPhase.VideoFrame, Ticks(60));
        ThumbnailTimings.Add(ThumbnailPhase.CacheProbe, Ticks(9));

        var snapshot = ThumbnailTimings.Snapshot();

        Assert.Equal(3, snapshot.Tries);
        Assert.Equal(2, snapshot.Generated);
        Assert.Equal(1, snapshot.Hits);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        ThumbnailTimings.Add(ThumbnailPhase.Encode, Ticks(5));
        ThumbnailTimings.Reset();

        var snapshot = ThumbnailTimings.Snapshot();
        Assert.Equal(0, snapshot.Tries);
        Assert.Equal(0, snapshot.Phase(ThumbnailPhase.Encode).Count);
    }

    [Fact]
    public void FormatSummary_ReportsCountsPhasesAndUserWait()
    {
        var phases = EmptyPhases();
        phases[(int)ThumbnailPhase.CacheProbe] = new ThumbnailPhaseStats(4, Ticks(20), Ticks(20));
        phases[(int)ThumbnailPhase.SourceOpen] = new ThumbnailPhaseStats(3, Ticks(18), Ticks(12));
        phases[(int)ThumbnailPhase.ImageDecode] = new ThumbnailPhaseStats(2, Ticks(150), Ticks(100));
        phases[(int)ThumbnailPhase.VideoFrame] = new ThumbnailPhaseStats(1, Ticks(90), Ticks(90));
        phases[(int)ThumbnailPhase.Clone] = new ThumbnailPhaseStats(3, Ticks(6), Ticks(3));
        phases[(int)ThumbnailPhase.Encode] = new ThumbnailPhaseStats(3, Ticks(210), Ticks(140));
        phases[(int)ThumbnailPhase.Write] = new ThumbnailPhaseStats(3, Ticks(3), Ticks(2));
        phases[(int)ThumbnailPhase.UiHandoff] = new ThumbnailPhaseStats(3, Ticks(12), Ticks(9));

        var line = ThumbnailTimings.FormatSummary(new ThumbnailTimingsSnapshot(4, 3, phases));

        // 4 tries, 3 produced from source (1 cache hit), per-phase avg/max×count,
        // then the part the user waits for (cache + open + decode + videoframe +
        // clone + ui).
        Assert.Equal(
            "thumbnail stats: tries=4 gen=3 hit=1(25%)" +
            " | cache 5.0/20.0x4" +
            " | open 6.0/12.0x3" +
            " | imgdecode 75.0/100.0x2" +
            " | videoframe 90.0/90.0x1" +
            " | clone 2.0/3.0x3" +
            " | encode 70.0/140.0x3" +
            " | write 1.0/2.0x3" +
            " | ui 4.0/9.0x3" +
            " | user-wait avg=182.0 ms (cache+open+decode+videoframe+clone+ui) [ms]",
            line);
    }

    [Fact]
    public void FormatSummary_SkipsPhasesThatNeverRan()
    {
        // A pure cache-hit window: only probes happened, so no decode/clone/...
        var phases = EmptyPhases();
        phases[(int)ThumbnailPhase.CacheProbe] = new ThumbnailPhaseStats(2, Ticks(4), Ticks(2));

        var line = ThumbnailTimings.FormatSummary(new ThumbnailTimingsSnapshot(2, 0, phases));

        Assert.Equal("thumbnail stats: tries=2 gen=0 hit=2(100%) | cache 2.0/2.0x2 [ms]", line);
    }

    /// <summary>
    /// Phase array sized from the enum, so adding a phase without updating these
    /// tests fails loudly rather than silently measuring the wrong slot.
    /// </summary>
    private static ThumbnailPhaseStats[] EmptyPhases()
        => new ThumbnailPhaseStats[Enum.GetValues<ThumbnailPhase>().Length];
}
