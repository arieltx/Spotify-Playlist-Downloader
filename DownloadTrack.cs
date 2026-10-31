using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpotiFetch;

public sealed class DownloadTrack : INotifyPropertyChanged
{
    private string _status = "Aguardando";
    private double _progress;
    private bool _isError;

    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string? ArtworkUrl { get; init; }
    public string? SourceUrl { get; init; }
    public double? DurationSeconds { get; init; }
    private string _bitrate = "—";
    private string _speed = "—";
    private long? _fileSizeBytes;

    public string Bitrate
    {
        get => _bitrate;
        set
        {
            if (_bitrate == value) return;
            _bitrate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormatDisplay));
        }
    }

    public string Speed
    {
        get => _speed;
        set
        {
            if (_speed == value) return;
            _speed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedAndSize));
        }
    }

    public long? FileSizeBytes
    {
        get => _fileSizeBytes;
        set
        {
            if (_fileSizeBytes == value) return;
            _fileSizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(SpeedAndSize));
        }
    }

    public string ErrorDetails { get; set; } = "";
    public DateTime? StartedAt { get; set; }

    public string FormatDisplay => string.IsNullOrWhiteSpace(Bitrate) || Bitrate == "—" ? "MP3" : $"MP3 · {Bitrate}";
    public string SizeDisplay => FileSizeBytes is long bytes ? FormatSize(bytes) : "—";
    public string SpeedAndSize => FileSizeBytes is null
        ? Speed
        : Speed == "—" ? SizeDisplay : $"{Speed} · {SizeDisplay}";

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB" : $"{bytes} B";
    public string? FilePath { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string DurationDisplay => DurationSeconds is > 0
        ? TimeSpan.FromSeconds(DurationSeconds.Value).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "—";

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

public sealed record DownloadProgressInfo(double Percent, string Speed, long? DownloadedBytes, long? TotalBytes);
