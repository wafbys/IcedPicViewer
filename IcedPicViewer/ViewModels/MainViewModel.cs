// Copyright (c) IcedPicViewer. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcedPicViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _title = "IcedPicViewer";

    public GalleryViewModel GalleryViewModel { get; }

    public MainViewModel(GalleryViewModel galleryViewModel)
    {
        GalleryViewModel = galleryViewModel;
    }
}
