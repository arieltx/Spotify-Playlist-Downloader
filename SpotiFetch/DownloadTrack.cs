using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LaurusDownloader;

public sealed class DownloadTrack : INotifyPropertyChanged
{
    private string _status = "Na fila";
    private double _progress;
    private bool _isError;

    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string? ArtworkUrl { get; init; }
    public string? SourceUrl { get; init; }
    public double? DurationSeconds { get; init; }

    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_progress - clamped) < 0.1) return;
            _progress = clamped;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public bool IsError
    {
        get => _isError;
        set
        {
            if (_isError == value) return;
            _isError = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
