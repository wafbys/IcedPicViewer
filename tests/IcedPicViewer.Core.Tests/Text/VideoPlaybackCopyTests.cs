// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Core.Tests.Text;

public sealed class VideoPlaybackCopyTests
{
    [Fact]
    public void GetCodecSpecificHint_ProRes()
        => Assert.Contains("ProRes", VideoPlaybackCopy.GetCodecSpecificHint("prores_422"), StringComparison.Ordinal);

    [Fact]
    public void ClassifyPrePlay_FileNotFound()
        => Assert.Equal(
            VideoPlaybackCopy.PrePlayFileNotFound(),
            VideoPlaybackCopy.ClassifyPrePlayException(new FileNotFoundException("missing")));

    [Fact]
    public void AboutCopy_Title()
        => Assert.Equal("关于 IcedPicViewer", AboutCopy.Title);
}
