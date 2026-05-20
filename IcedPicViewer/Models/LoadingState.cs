namespace IcedPicViewer.Models;

public enum ImageSourceType
{
    FileSystem
}

public enum LoadingState
{
    Idle,
    Scanning,
    LoadingImages,
    Error,
    Completed
}
