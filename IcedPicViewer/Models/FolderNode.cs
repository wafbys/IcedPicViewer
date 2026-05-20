using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace IcedPicViewer.Models;

public partial class FolderNode : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    public bool IsArchive { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<FolderNode> Children { get; } = new();
    public ObservableCollection<ImageItem> Images { get; } = new();

    public FolderNode(string path, string name, bool isArchive)
    {
        Path = path;
        Name = name;
        IsArchive = isArchive;
    }
}
