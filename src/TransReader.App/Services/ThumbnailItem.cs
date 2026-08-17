using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace TransReader.App;

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private ImageSource? _image;
    private bool _isCurrent;
    private ThumbnailLoadState _state;
    private int _loadGeneration;

    public ThumbnailItem(uint pageIndex)
    {
        PageIndex = pageIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint PageIndex { get; }

    public string PageLabel => (PageIndex + 1).ToString();

    public ImageSource? Image
    {
        get => _image;
        set => SetField(ref _image, value);
    }

    public ThumbnailLoadState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(ImageVisibility));
                OnPropertyChanged(nameof(LoadingVisibility));
                OnPropertyChanged(nameof(FailedVisibility));
            }
        }
    }

    public Visibility ImageVisibility => State == ThumbnailLoadState.Loaded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadingVisibility => State is ThumbnailLoadState.Pending or ThumbnailLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailedVisibility => State == ThumbnailLoadState.Failed ? Visibility.Visible : Visibility.Collapsed;
    public int NextGeneration() => ++_loadGeneration;
    public bool IsGenerationCurrent(int generation) => generation == _loadGeneration;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ThumbnailLoadState { Pending, Loading, Loaded, Failed }
