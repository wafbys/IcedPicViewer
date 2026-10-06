// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using IcedPicViewer.Core.Settings;

namespace IcedPicViewer.Core.Media;

/// <summary>Which part of producing one thumbnail a measurement belongs to.</summary>
public enum ThumbnailPhase
{
    /// <summary>Disk-cache lookup: read + WIC-decode a cached payload, or miss.</summary>
    CacheProbe,

    /// <summary>
    /// Container open + stream info + seek (FFmpeg only). This is the phase that
    /// reads megabytes before a single frame is decoded, so it is measured apart
    /// from the decode.
    /// </summary>
    SourceOpen,

    /// <summary>
    /// Producing the thumbnail bitmap from an image source (WIC decode + scale).
    /// Videos use <see cref="VideoFrame"/> instead, so the two can be told apart.
    /// </summary>
    ImageDecode,

    /// <summary>
    /// Producing the thumbnail bitmap from a video: frame decode + swscale + row
    /// copy (the FFmpeg work after <see cref="SourceOpen"/>).
    /// </summary>
    VideoFrame,

    /// <summary>Cloning the pixels for the background encode queue.</summary>
    Clone,

    /// <summary>Lossless PNG encode (runs on the background queue, off the display path).</summary>
    Encode,

    /// <summary>Atomic file write (also background).</summary>
    Write,

    /// <summary>Handing the bitmap to the UI (SoftwareBitmapSource upload + property set).</summary>
    UiHandoff,
}

/// <summary>Per-phase counters for one reporting window.</summary>
public readonly record struct ThumbnailPhaseStats(int Count, long TotalTicks, long MaxTicks)
{
    public double AverageMs => Count <= 0
        ? 0
        : TicksToMs(TotalTicks) / Count;

    public double MaxMs => TicksToMs(MaxTicks);

    internal static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>Immutable view of the accumulator, so the summary text can be unit-tested.</summary>
public sealed class ThumbnailTimingsSnapshot
{
    private readonly ThumbnailPhaseStats[] _phases;

    /// <summary>
    /// Public so the summary text can be unit-tested without touching the shared
    /// accumulator; <see cref="ThumbnailTimings.Snapshot"/> is the normal source.
    /// </summary>
    public ThumbnailTimingsSnapshot(int tries, int generated, ThumbnailPhaseStats[] phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        Tries = tries;
        Generated = generated;
        _phases = phases;
    }

    /// <summary>Thumbnail requests (one per disk-cache lookup).</summary>
    public int Tries { get; }

    /// <summary>Requests that had to produce the bitmap from the source (i.e. cache misses).</summary>
    public int Generated { get; }

    /// <summary>Cache hits.</summary>
    public int Hits => Math.Max(0, Tries - Generated);

    public ThumbnailPhaseStats Phase(ThumbnailPhase phase) => _phases[(int)phase];
}

/// <summary>
/// Phase timings for thumbnail production, accumulated per process and reported
/// as one line every <see cref="LogEveryTries"/> requests plus at each folder
/// boundary (see <see cref="LogSummaryAndReset"/>).
///
/// <para>
/// Exists because the interesting question — "where does the time actually go on
/// this machine?" — cannot be answered from the code: it depends on the drive
/// (seek-bound HDD vs SSD), the mix of images/videos and the cache hit rate. The
/// line is written to <c>Trace</c> and appended to
/// <c>&lt;app data&gt;\thumbnail-stats.log</c>, because a packaged app's Trace
/// output has no listener.
/// </para>
///
/// <para>
/// Cost is a handful of <see cref="Interlocked"/> operations per phase per
/// thumbnail; <see cref="Add"/> is safe from any thread.
/// </para>
/// </summary>
public static class ThumbnailTimings
{
    /// <summary>
    /// Report after this many requests (and again at every folder boundary). Kept
    /// coarse on purpose: one line per folder, plus a few during a long
    /// generation, is enough for a diagnostic — and it keeps the log small.
    /// </summary>
    public const int LogEveryTries = 1024;

    private const string LogFileName = "thumbnail-stats.log";
    private const long MaxLogBytes = 512 * 1024;

    private static readonly int PhaseCount = Enum.GetValues<ThumbnailPhase>().Length;

    /// <summary>Short keys used in the summary line (matches the enum order).</summary>
    private static readonly string[] PhaseKeys =
        ["cache", "open", "imgdecode", "videoframe", "clone", "encode", "write", "ui"];

    private static readonly int[] Counts = new int[PhaseCount];
    private static readonly long[] TotalTicks = new long[PhaseCount];
    private static readonly long[] MaxTicks = new long[PhaseCount];

    private static int _probes;   // CacheProbe count: one per thumbnail request
    private static int _logInitialized;

    /// <summary>Records one measurement. <paramref name="elapsedTicks"/> is a <see cref="Stopwatch.GetTimestamp"/> delta.</summary>
    public static void Add(ThumbnailPhase phase, long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        var index = (int)phase;

        Interlocked.Increment(ref Counts[index]);
        Interlocked.Add(ref TotalTicks[index], elapsedTicks);
        UpdateMax(index, elapsedTicks);

        if (phase != ThumbnailPhase.CacheProbe) return;

        var probes = Interlocked.Increment(ref _probes);
        if (probes % LogEveryTries == 0)
        {
            var line = FormatSummary(Snapshot());
            Trace.TraceInformation(line);
            AppendToLog(line);
        }
    }

    /// <summary>Current counters (cumulative since the last <see cref="Reset"/>).</summary>
    public static ThumbnailTimingsSnapshot Snapshot()
    {
        var phases = new ThumbnailPhaseStats[PhaseCount];
        for (var i = 0; i < PhaseCount; i++)
        {
            phases[i] = new ThumbnailPhaseStats(
                Volatile.Read(ref Counts[i]),
                Volatile.Read(ref TotalTicks[i]),
                Volatile.Read(ref MaxTicks[i]));
        }

        return new ThumbnailTimingsSnapshot(
            Volatile.Read(ref _probes),
            Volatile.Read(ref Counts[(int)ThumbnailPhase.ImageDecode])
            + Volatile.Read(ref Counts[(int)ThumbnailPhase.VideoFrame]),
            phases);
    }

    /// <summary>
    /// Reports the window that just ended (if anything was measured) and starts a
    /// fresh one. Called when a folder is opened or closed.
    /// </summary>
    public static void LogSummaryAndReset()
    {
        if (Volatile.Read(ref _probes) > 0)
        {
            var line = FormatSummary(Snapshot());
            Trace.TraceInformation(line);
            AppendToLog(line);
        }

        Reset();
    }

    /// <summary>Drops all counters (does not touch the log file).</summary>
    public static void Reset()
    {
        for (var i = 0; i < PhaseCount; i++)
        {
            Interlocked.Exchange(ref Counts[i], 0);
            Interlocked.Exchange(ref TotalTicks[i], 0);
            Interlocked.Exchange(ref MaxTicks[i], 0);
        }

        Interlocked.Exchange(ref _probes, 0);
    }

    /// <summary>
    /// One dense line: request/miss/hit counts, then per-phase <c>avg/max×count</c>
    /// in milliseconds, then the part of the work the user actually waits for
    /// (cache probe + decode + clone + UI handoff — encode/write happen on the
    /// background queue).
    /// </summary>
    public static string FormatSummary(ThumbnailTimingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder(256);
        sb.Append("thumbnail stats: tries=").Append(snapshot.Tries);
        sb.Append(" gen=").Append(snapshot.Generated);
        sb.Append(" hit=").Append(snapshot.Hits);
        if (snapshot.Tries > 0)
        {
            var percent = snapshot.Hits * 100.0 / snapshot.Tries;
            sb.Append('(').Append(percent.ToString("0", CultureInfo.InvariantCulture)).Append("%)");
        }

        for (var i = 0; i < PhaseCount; i++)
        {
            var stats = snapshot.Phase((ThumbnailPhase)i);
            if (stats.Count == 0) continue;
            var key = i < PhaseKeys.Length ? PhaseKeys[i] : ((ThumbnailPhase)i).ToString();
            sb.Append(" | ").Append(key).Append(' ');
            sb.Append(stats.AverageMs.ToString("0.0", CultureInfo.InvariantCulture)).Append('/');
            sb.Append(stats.MaxMs.ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append('x').Append(stats.Count);
        }

        if (snapshot.Generated > 0)
        {
            // Everything the user actually waits for. Encode/Write are excluded:
            // they run on the cache's background queue, after the clone. SourceOpen
            // belongs here because a video's probe happens before its first frame.
            var waitMs = snapshot.Phase(ThumbnailPhase.CacheProbe).AverageMs
                         + snapshot.Phase(ThumbnailPhase.SourceOpen).AverageMs
                         + snapshot.Phase(ThumbnailPhase.ImageDecode).AverageMs
                         + snapshot.Phase(ThumbnailPhase.VideoFrame).AverageMs
                         + snapshot.Phase(ThumbnailPhase.Clone).AverageMs
                         + snapshot.Phase(ThumbnailPhase.UiHandoff).AverageMs;
            sb.Append(" | user-wait avg=")
              .Append(waitMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" ms (cache+open+decode+videoframe+clone+ui)");
        }

        sb.Append(" [ms]");
        return sb.ToString();
    }

    private static void UpdateMax(int index, long value)
    {
        // Plain max under contention: read, and compare-exchange until we win.
        while (true)
        {
            var current = Volatile.Read(ref MaxTicks[index]);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref MaxTicks[index], value, current) == current) return;
        }
    }

    /// <summary>
    /// Deletes the diagnostics log. Called when the user clears the thumbnail
    /// cache: the log describes that cache's traffic, so it should not outlive it.
    /// Best-effort — diagnostics must never fail a user action.
    /// </summary>
    public static void ClearLog()
    {
        try
        {
            File.Delete(Path.Combine(AppDataPaths.Root, LogFileName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"ThumbnailTimings: clearing the log failed: {ex.Message}");
        }
    }

    private static void AppendToLog(string line)
    {
        try
        {
            var root = AppDataPaths.EnsureRoot();
            var path = Path.Combine(root, LogFileName);

            if (Interlocked.Exchange(ref _logInitialized, 1) == 0)
            {
                // Fresh file per process so a reader never mixes sessions.
                File.Delete(path);
            }

            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes) return;

            File.AppendAllText(
                path,
                $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostics must never break thumbnail production.
            Trace.TraceWarning($"ThumbnailTimings: log append failed: {ex.Message}");
        }
    }
}
