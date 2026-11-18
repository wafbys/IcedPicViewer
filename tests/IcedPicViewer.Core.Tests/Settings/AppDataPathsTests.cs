// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Settings;

namespace IcedPicViewer.Core.Tests.Settings;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Root_LivesNextToTheExecutable()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "Data");

        Assert.Equal(expected, AppDataPaths.Root);
        Assert.Equal("Data", Path.GetFileName(AppDataPaths.Root));
    }

    [Fact]
    public void FileAndFolderHelpers_HangOffRoot()
    {
        var root = AppDataPaths.Root;

        Assert.Equal(Path.Combine(root, "settings.json"), AppDataPaths.SettingsFile);
        Assert.Equal(Path.Combine(root, "TempVideo"), AppDataPaths.TempVideoDir);
        Assert.Equal(Path.Combine(root, "crash.log"), AppDataPaths.CrashLogFile);
    }
}
