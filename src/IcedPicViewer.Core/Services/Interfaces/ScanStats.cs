// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// P1 instrumentation: one measurement per completed directory scan,
/// reported through <see cref="IDirectoryScanner.ScanAsync"/>'s optional
/// <c>statsReporter</c>. Lets a caller answer "where did the scan time
/// go" (pure enumeration vs archive listing) without a profiler.
///
/// <para>
/// Counts are exact; the millisecond fields are wall-clock and only meant
/// for relative comparison, not for assertions.
/// </para>
/// </summary>
/// <param name="DirectoryCount">Directories visited (recycle bins excluded).</param>
/// <param name="FileCount">Loose files inspected (before extension filtering).</param>
/// <param name="ArchiveCount">Archive files opened and enumerated.</param>
/// <param name="MediaCount">Media sources yielded (loose + archive entries).</param>
/// <param name="ElapsedMs">Total scan wall-clock time.</param>
/// <param name="EnumerateMs">Time spent in filesystem enumeration, excluding archive listing.</param>
/// <param name="ArchiveMs">Time spent listing archive entries.</param>
public readonly record struct ScanStats(
    int DirectoryCount,
    int FileCount,
    int ArchiveCount,
    int MediaCount,
    long ElapsedMs,
    long EnumerateMs,
    long ArchiveMs);
