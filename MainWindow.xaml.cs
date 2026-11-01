using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Windows.Input;
using System.Windows.Threading;

namespace SpotiFetch;

public sealed record ActivityEntry(string Time, string Type, string Description);
public sealed record PlayerPlaylistOption(string Name, string? DirectoryPath);

public partial class MainWindow : Window
{
    private const string DiscordApplicationId = "1566458570340442245";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static readonly HttpClient ArtworkHttp = new();
    public ObservableCollection<DownloadTrack> Tracks { get; } = [];
    public ObservableCollection<DownloadTrack> CompletedHistory { get; } = [];
    public ObservableCollection<PlayerPlaylistOption> PlayerPlaylists { get; } = [];
    public ObservableCollection<DownloadTrack> PlayerTracks { get; } = [];
    private ICollectionView? _playerTracksView;

    private readonly UserSettings _settings;
    private readonly SpotifyEmbedClient _spotify = new();
    private readonly DownloadEngine _downloader = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _initializing = true;
    private bool _busy;
    private bool _cancelRequested;
    private Stopwatch? _queueClock;
    private int _activeDownloads;
    private readonly List<string> _recentLogs = [];
    private readonly List<string> _technicalLogs = [];
    private readonly Button[] _navigationButtons;
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly object _pauseLock = new();
    private bool _downloadsPaused;
    private TaskCompletionSource<bool> _pauseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _currentOutputDirectory = "";
    private int _pendingCount;
    private string? _detailCoverUrl;
    private DownloadTrack? _currentPlayerTrack;
    private bool _playerPaused = true;
    private bool _refreshingPlayerLibrary;
    private bool _playerSeeking;
    private bool _miniPlayerSeeking;
    private bool _playerMuted;
    private bool _shuffleEnabled;
    private int _repeatMode;
    private string _playerSearchQuery = "";
    private double _previousPlayerVolume = 0.8;
    private SystemMediaTransportControls? _systemMediaControls;
    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotiFetch", "history.json");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _pauseRelease.TrySetResult(true);
        _navigationButtons = [NavHome, NavDownloads, NavQueue, NavCompleted, NavActivity, NavLogs, NavPlayer, NavSettings, NavAbout];
        _spotify.Diagnostic += AddLog;
        _settings = UserSettings.Load();
        foreach (var completed in LoadCompletedHistory()) CompletedHistory.Add(completed);
        foreach (var line in LoadTechnicalLogs())
        {
            _technicalLogs.Add(line);
            DetailedLogsList.Items.Add(line);
        }
        OutputPathText.Text = _settings.OutputDirectory;
        ( _settings.Bitrate switch
        {
            "256" => Quality256,
            "128" => Quality128,
            _ => Quality320
        }).IsChecked = true;
        _settings.MaxWorkers = Math.Clamp(_settings.MaxWorkers, 1, 32);
        SkipExistingCheckBox.IsChecked = _settings.SkipExisting;
        UseChromeCookiesCheckBox.IsChecked = _settings.UseChromeCookies;
        AvoidLiveCheckBox.IsChecked = _settings.AvoidLiveTracks;
        DiscordPresenceCheckBox.IsChecked = _settings.DiscordPresenceEnabled;
        WorkersSlider.Value = _settings.MaxWorkers;
        WorkersValueText.Text = $"{_settings.MaxWorkers} thread{(_settings.MaxWorkers == 1 ? "" : "s")}";
        var workerPresets = new[] { 1, 4, 6, 8, 16 };
        var presetIndex = Array.IndexOf(workerPresets, _settings.MaxWorkers);
        CustomWorkersPanel.Visibility = presetIndex >= 0 ? Visibility.Collapsed : Visibility.Visible;
        (presetIndex switch
        {
            0 => Workers1,
            1 => Workers4,
            2 => Workers6,
            3 => Workers8,
            4 => Workers16,
            _ => WorkersCustom
        }).IsChecked = true;
        _initializing = false;
        DetailBitrateSummary.Text = $"{_settings.Bitrate} kbps";
        CompletedGrid.ItemsSource = CompletedHistory;
        PlayerPlaylistSelector.ItemsSource = PlayerPlaylists;
        _playerTracksView = CollectionViewSource.GetDefaultView(PlayerTracks);
        _playerTracksView.Filter = FilterPlayerTrack;
        PlayerTrackList.ItemsSource = _playerTracksView;
        RefreshPlayerLibrary();
        _playerTimer.Tick += PlayerTimer_Tick;
        _playerTimer.Start();
        ShowPage(HomePage, NavHome);
        AddLog("SpotiFetch pronto. Cole um link público ou digite uma busca.");
        Closed += (_, _) =>
        {
            _operationCancellation?.Cancel();
            _playerTimer.Stop();
            PlayerMedia.Stop();
            PlayerMedia.Close();
            _ = _discordPresence.DisposeAsync();
            if (_systemMediaControls is not null)
            {
                _systemMediaControls.ButtonPressed -= SystemMediaControls_ButtonPressed;
                _systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                _systemMediaControls.IsEnabled = false;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && TrackDetailsPanel.Visibility == Visibility.Visible)
            {
                CloseTrackDetails();
                e.Handled = true;
                return;
            }
            if (Keyboard.FocusedElement is TextBox or ComboBox or Slider or Button) return;
            if (PlayerPage.Visibility != Visibility.Visible && _currentPlayerTrack is null) return;

            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Space || e.Key == Key.MediaPlayPause)
                TogglePlayerPlayback();
            else if (e.Key is Key.MediaNextTrack || (control && e.Key == Key.Right))
                PlayNextTrack();
            else if (e.Key is Key.MediaPreviousTrack || (control && e.Key == Key.Left))
                PlayPreviousTrack();
            else if (control && e.Key == Key.Up)
                PlayerVolumeSlider.Value = Math.Min(1, PlayerVolumeSlider.Value + 0.05);
            else if (control && e.Key == Key.Down)
                PlayerVolumeSlider.Value = Math.Max(0, PlayerVolumeSlider.Value - 0.05);
            else return;
            e.Handled = true;
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            var result = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            if (result != 0)
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            try
            {
                _systemMediaControls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
                _systemMediaControls.IsEnabled = true;
                _systemMediaControls.IsPlayEnabled = true;
                _systemMediaControls.IsPauseEnabled = true;
                _systemMediaControls.IsNextEnabled = true;
                _systemMediaControls.IsPreviousEnabled = true;
                _systemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
            }
            catch (Exception ex)
            {
                _systemMediaControls = null;
                AddLog($"Controles de mídia do Windows indisponíveis: {ex.Message}");
            }
        }
        catch (DllNotFoundException)
        {
            // Keep the standard title bar on Windows versions without DWM support.
        }
        catch (EntryPointNotFoundException)
        {
            // Dark title bar support varies by Windows version.
        }
    }

    private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        Dispatcher.BeginInvoke(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (_playerPaused) TogglePlayerPlayback();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (!_playerPaused) TogglePlayerPlayback();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    PlayNextTrack();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PlayPreviousTrack();
                    break;
            }
        });
    }

    private async void UpdateSystemMediaSession(DownloadTrack track, MediaPlaybackStatus status)
    {
        var controls = _systemMediaControls;
        if (controls is null) return;

        try
        {
            controls.PlaybackStatus = status;
            var updater = controls.DisplayUpdater;
            updater.ClearAll();
            updater.AppMediaId = WindowsAppIdentity.AppUserModelId;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(track.Artist) ? "Artista desconhecido" : track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album ?? "";

            if (Uri.TryCreate(track.ArtworkUrl, UriKind.Absolute, out var artworkUri) && artworkUri.Scheme == Uri.UriSchemeHttps)
            {
                var artworkDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotiFetch", "Artwork");
                Directory.CreateDirectory(artworkDirectory);
                var artworkPath = Path.Combine(artworkDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artworkUri.AbsoluteUri))) + ".jpg");
                if (!File.Exists(artworkPath))
                {
                    var imageBytes = await ArtworkHttp.GetByteArrayAsync(artworkUri);
                    await File.WriteAllBytesAsync(artworkPath, imageBytes);
                }
                if (!ReferenceEquals(_currentPlayerTrack, track)) return;
                var artworkFile = await StorageFile.GetFileFromPathAsync(artworkPath);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(artworkFile);
            }
            if (ReferenceEquals(_currentPlayerTrack, track)) updater.Update();
        }
        catch
        {
            // A falha do painel de mídia do Windows não pode interromper o player do app.
        }
    }

    private async Task UpdateDiscordPresenceAsync(DownloadTrack track)
    {
        if (!_settings.DiscordPresenceEnabled)
        {
            await _discordPresence.ClearAsync();
            return;
        }

        try
        {
            var duration = PlayerMedia.NaturalDuration.HasTimeSpan
                ? PlayerMedia.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            await _discordPresence.UpdateAsync(
                DiscordApplicationId,
                track.Title,
                track.Artist,
                track.ArtworkUrl,
                PlayerMedia.Position,
                duration,
                !_playerPaused);
        }
        catch (Exception ex)
        {
            AddTechnicalLog($"Discord Rich Presence indisponível: {ex.Message}");
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await StartOperationAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartOperationAsync();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        InputHint.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async void ImportTxtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Importar lista de músicas",
            Filter = "Listas de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true);
        _currentOutputDirectory = _settings.OutputDirectory;
        _operationCancellation = new CancellationTokenSource();
        var token = _operationCancellation.Token;
        Tracks.Clear();
        ResetPlaylistArtwork();
        try
        {
            var lines = await File.ReadAllLinesAsync(dialog.FileName, token);
            var imported = new List<DownloadTrack>();
            foreach (var rawLine in lines)
            {
                token.ThrowIfCancellationRequested();
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase) || line.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
                {
                    var (_, resolved) = await _spotify.ResolveAsync(line, token);
                    imported.AddRange(resolved);
                }
                else
                {
                    var pieces = line.Split(" - ", 2, StringSplitOptions.TrimEntries);
                    imported.Add(new DownloadTrack
                    {
                        Number = imported.Count + 1,
                        Title = pieces[0],
                        Artist = pieces.Length > 1 ? pieces[1] : ""
                    });
                }
            }
            for (var i = 0; i < imported.Count; i++)
                Tracks.Add(new DownloadTrack
                {
                    Number = i + 1, Title = imported[i].Title, Artist = imported[i].Artist,
                    Album = imported[i].Album, ArtworkUrl = imported[i].ArtworkUrl,
                    SourceUrl = imported[i].SourceUrl, DurationSeconds = imported[i].DurationSeconds
                });
            if (Tracks.Count == 0) throw new InvalidOperationException("O arquivo não contém faixas válidas.");
            NowTitle.Text = "Lista importada";
            NowDetail.Text = Path.GetFileName(dialog.FileName);
            DownloadGroupTitle.Text = NowTitle.Text;
            QueueSummary.Text = $"{Tracks.Count} faixas encontradas";
            ShowPage(DownloadsPage, NavDownloads);
            await SetPlaylistArtworkAsync(Tracks.FirstOrDefault()?.ArtworkUrl);
            AddLog($"Importadas {Tracks.Count} faixas de {dialog.FileName}");
            await DownloadQueueAsync(Tracks.ToList(), token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "●  CANCELADO";
            AddLog("Importação e fila canceladas.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "●  ERRO";
            AddTechnicalLog($"Erro na importação TXT: {ex}");
            AddLog($"ERRO na importação TXT: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Erro ao importar TXT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async Task StartOperationAsync()
    {
        if (_busy) return;
        var input = InputBox.Text.Trim();
        if (input.Length == 0)
        {
            InputBox.Focus();
            return;
        }

        SetBusy(true);
        _operationCancellation = new CancellationTokenSource();
        var token = _operationCancellation.Token;
        Tracks.Clear();
        ResetPlaylistArtwork();
        OverallProgress.Value = 0;

        try
        {
            List<DownloadTrack> queue;
            string sourceName;
            var outputDirectory = _settings.OutputDirectory;
            if (input.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "●  LENDO EMBED PÚBLICO DO SPOTIFY";
                AddLog($"Lendo metadados públicos: {input}");
                (sourceName, queue) = await _spotify.ResolveAsync(input, token);
                if (IsSpotifyPlaylistInput(input))
                {
                    outputDirectory = Path.Combine(_settings.OutputDirectory, CreateSafeFolderName(sourceName));
                    AddLog($"Playlist será salva em: {outputDirectory}");
                }
            }
            else if (Uri.TryCreate(input, UriKind.Absolute, out var mediaUri) &&
                     mediaUri.Scheme is "https" or "http")
            {
                sourceName = "Link de áudio";
                queue = [new DownloadTrack
                {
                    Number = 1,
                    Title = "Áudio vinculado",
                    Artist = "",
                    SourceUrl = mediaUri.ToString()
                }];
            }
            else
            {
                sourceName = "Busca";
                var pieces = input.Split(" - ", 2, StringSplitOptions.TrimEntries);
                queue = [new DownloadTrack
                {
                    Number = 1,
                    Title = pieces[0],
                    Artist = pieces.Length > 1 ? pieces[1] : "",
                }];
            }

            foreach (var track in queue) Tracks.Add(track);
            _currentOutputDirectory = outputDirectory;
            NowTitle.Text = sourceName;
            NowDetail.Text = queue.Count == 1
                ? $"{queue[0].Title}{(string.IsNullOrWhiteSpace(queue[0].Artist) ? "" : $" — {queue[0].Artist}")}"
                : $"{queue.Count} faixas encontradas";
            QueueSummary.Text = $"{queue.Count} {(queue.Count == 1 ? "faixa" : "faixas")} na fila";
            DownloadGroupTitle.Text = sourceName;
            ShowPage(DownloadsPage, NavDownloads);
            RefreshTrackLists();
            await SetPlaylistArtworkAsync(queue.FirstOrDefault()?.ArtworkUrl);
            AddLog($"{sourceName}: {queue.Count} faixas prontas para download.");
            await DownloadQueueAsync(queue, token, outputDirectory);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "●  CANCELADO";
            AddLog("Operação cancelada.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "●  ERRO";
            AddTechnicalLog($"Falha ao processar a entrada: {ex}");
            AddLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Não foi possível processar a entrada", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async Task DownloadQueueAsync(
        IReadOnlyList<DownloadTrack> queue,
        CancellationToken token,
        string? outputDirectory = null)
    {
        outputDirectory ??= _settings.OutputDirectory;
        try { Directory.CreateDirectory(outputDirectory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"O Windows não permitiu gravar na pasta de saída:\n{outputDirectory}\n\n" +
                "Escolha outra pasta em Configurações ou permita o SpotiFetch no Acesso controlado a pastas do Windows.", ex);
        }
        var workerCount = Math.Clamp(_settings.MaxWorkers, 1, 32);
        DetailBitrateSummary.Text = $"{_settings.Bitrate} kbps";
        _activeDownloads = 0;
        lock (_pauseLock)
        {
            _downloadsPaused = false;
            _pauseRelease.TrySetResult(true);
        }
        PauseAllButton.Content = "Pausar fila";
        ActiveThreadsText.Text = "0";
        using var slots = new SemaphoreSlim(workerCount, workerCount);
        _queueClock = Stopwatch.StartNew();
        var existing = _settings.SkipExisting ? FindExistingTracks(queue, outputDirectory) : new HashSet<DownloadTrack>();
        foreach (var track in existing)
        {
            track.Status = "Já baixada";
            track.Progress = 100;
            if (track.FilePath is not null && File.Exists(track.FilePath))
            {
                track.CompletedAt = File.GetLastWriteTime(track.FilePath);
                AddCompletedHistory(track);
            }
        }
        UpdateQueueSummary(queue);
        UpdateOverallProgress(queue);
        DownloadGroupTitle.Text = NowTitle.Text;
        RefreshTrackLists();
        StatusText.Text = "●  BAIXANDO";
        var jobs = queue.Select(async track =>
        {
            if (existing.Contains(track)) return;
            var hasSlot = false;
            try
            {
                await slots.WaitAsync(token);
                hasSlot = true;
                await WaitIfDownloadsPausedAsync(token);
                var activeCount = Interlocked.Increment(ref _activeDownloads);
                await OnUiAsync(() => { ActiveThreadsText.Text = activeCount.ToString(); UpdateBadges(); });
                await OnUiAsync(() =>
                {
                    track.Status = "Buscando";
                    track.StartedAt = DateTime.Now;
                    track.Bitrate = $"{_settings.Bitrate} kbps";
                    UpdateQueueSummary(queue);
                });
                await _downloader.DownloadAsync(track, outputDirectory, _settings.Bitrate, token,
                    message => _ = OnUiAsync(() =>
                    {
                        track.Status = message;
                        if (ReferenceEquals(DownloadsGrid.SelectedItem, track)) RefreshTrackDetails(track);
                    }),
                    percent => _ = OnUiAsync(() =>
                    {
                        track.Progress = percent;
                        UpdateOverallProgress(queue);
                        if (ReferenceEquals(DownloadsGrid.SelectedItem, track)) RefreshTrackDetails(track);
                    }), _settings.UseChromeCookies,
                    telemetry => _ = OnUiAsync(() =>
                    {
                        track.Speed = telemetry.Speed;
                        if (telemetry.TotalBytes is > 0) track.FileSizeBytes = telemetry.TotalBytes;
                        UpdateAggregateSpeed(queue);
                        if (ReferenceEquals(DownloadsGrid.SelectedItem, track)) RefreshTrackDetails(track);
                    }), _settings.AvoidLiveTracks);
                await OnUiAsync(() =>
                {
                    track.Status = "Concluído"; track.Progress = 100; track.Speed = "—";
                    AddCompletedHistory(track);
                    UpdateAggregateSpeed(queue); UpdateQueueSummary(queue); UpdateOverallProgress(queue);
                    if (ReferenceEquals(DownloadsGrid.SelectedItem, track)) RefreshTrackDetails(track);
                });
                await OnUiAsync(() => UpdateQueueSummary(queue));
                AddLog($"Concluído: {track.Title}");
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() => { track.Status = "Cancelado"; track.Speed = "—"; UpdateQueueSummary(queue); UpdateAggregateSpeed(queue); });
                throw;
            }
            catch (Exception ex)
            {
                track.ErrorDetails = ex.ToString();
                AddTechnicalLog($"Falha em {track.Title} — {track.Artist}{Environment.NewLine}{ex}");
                await OnUiAsync(() =>
                {
                    track.Status = "Falhou"; track.IsError = true; track.Progress = 100; track.Speed = "—";
                    UpdateQueueSummary(queue); UpdateOverallProgress(queue); UpdateAggregateSpeed(queue);
                    if (ReferenceEquals(DownloadsGrid.SelectedItem, track)) RefreshTrackDetails(track);
                });
                AddLog($"Falha em {track.Title}: {ex.Message}");
            }
            finally
            {
                if (hasSlot)
                {
                    var activeCount = Interlocked.Decrement(ref _activeDownloads);
                    await OnUiAsync(() => { ActiveThreadsText.Text = Math.Max(0, activeCount).ToString(); UpdateBadges(); });
                }
                if (hasSlot) slots.Release();
            }
        });
        try
        {
            await Task.WhenAll(jobs);
            StatusText.Text = queue.Any(item => item.IsError) ? "●  CONCLUÍDO COM ERROS" : "●  CONCLUÍDO";
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() =>
            {
                foreach (var track in queue)
                    if (track.Status is not ("Concluído" or "Falhou")) track.Status = "Cancelado";
            });
            throw;
        }
        AddLog("Fila finalizada.");
    }

    private async Task SetPlaylistArtworkAsync(string? artworkUrl)
    {
        ResetPlaylistArtwork();
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            var bytes = await ArtworkHttp.GetByteArrayAsync(uri);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            PlaylistCoverImage.Source = bitmap;
            PlaylistCoverImage.Visibility = Visibility.Visible;
            PlaylistCoverFallback.Visibility = Visibility.Collapsed;
            DownloadGroupCover.Source = bitmap;
            DownloadGroupCoverFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { AddLog($"Capa da playlist indisponível: {ex.Message}"); }
    }

    private void ResetPlaylistArtwork()
    {
        if (PlaylistCoverImage is null) return;
        PlaylistCoverImage.Source = null;
        PlaylistCoverImage.Visibility = Visibility.Collapsed;
        PlaylistCoverFallback.Visibility = Visibility.Visible;
        DownloadGroupCover.Source = null;
        DownloadGroupCoverFallback.Visibility = Visibility.Visible;
    }

    private void DownloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is DownloadTrack track)
        {
            RefreshTrackDetails(track);
            TrackDetailsPanel.Visibility = Visibility.Visible;
            TrackDetailsColumn.Width = new GridLength(350);
        }
    }

    private void CloseTrackDetails_Click(object sender, RoutedEventArgs e) => CloseTrackDetails();

    private void CloseTrackDetails()
    {
        TrackDetailsPanel.Visibility = Visibility.Collapsed;
        TrackDetailsColumn.Width = new GridLength(0);
        DownloadsGrid.SelectedItem = null;
    }

    private void RefreshTrackDetails(DownloadTrack track)
    {
        DetailTrackTitle.Text = track.Title;
        DetailTrackArtist.Text = string.IsNullOrWhiteSpace(track.Artist) ? "Artista desconhecido" : track.Artist;
        DetailTrackStatus.Text = track.Status;
        DetailTrackBitrate.Text = track.Bitrate == "—" ? $"{_settings.Bitrate} kbps" : track.Bitrate;
        DetailTrackSize.Text = track.FileSizeBytes is long bytes ? FormatFileSize(bytes) : "—";
        DetailTrackSpeed.Text = track.Speed;
        DetailTrackAlbum.Text = string.IsNullOrWhiteSpace(track.Album) ? "" : $"Álbum · {track.Album}";
        DetailErrorLabel.Visibility = track.IsError ? Visibility.Visible : Visibility.Collapsed;
        DetailErrorText.Visibility = track.IsError ? Visibility.Visible : Visibility.Collapsed;
        DetailErrorText.Text = track.ErrorDetails;
        DetailTrackDuration.Text = track.DurationDisplay;
        DetailTrackProgress.Value = track.Progress;
        DetailTrackProgressText.Text = $"{track.Progress:0}%";
        DetailRemainingTime.Text = GetRemainingText(track);
        SetDetailCover(track.ArtworkUrl);
    }

    private static string GetRemainingText(DownloadTrack track)
    {
        if (track.IsError) return "—";
        if (track.Progress >= 100) return track.Status == "Concluído" || track.Status == "Já baixada" ? "Concluída" : "—";
        if (track.StartedAt is not DateTime started || track.Progress < 1) return track.StartedAt is null ? "Aguardando" : "Calculando…";
        var elapsed = DateTime.Now - started;
        return FormatDuration(TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - track.Progress) / track.Progress));
    }

    private void SetDetailCover(string? artworkUrl)
    {
        if (_detailCoverUrl == artworkUrl) return;
        _detailCoverUrl = artworkUrl;
        DetailTrackCover.Source = null;
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = 264;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            DetailTrackCover.Source = bitmap;
        }
        catch { }
    }

    private static string FormatFileSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB" : $"{bytes} B";

    private static bool IsSpotifyPlaylistInput(string input)
    {
        if (input.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("playlist", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Playlist";
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd('.', ' ');
        var deviceName = cleaned.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(deviceName, StringComparer.OrdinalIgnoreCase))
            cleaned = $"_{cleaned}";
        return cleaned;
    }

    private void UpdateOverallProgress(IReadOnlyList<DownloadTrack> queue)
    {
        var percent = queue.Count == 0 ? 0 : queue.Average(item => item.Progress);
        OverallProgress.Value = percent;
        DownloadProgress.Value = percent;
        var remaining = percent > 0 && _queueClock is not null
            ? TimeSpan.FromSeconds(_queueClock.Elapsed.TotalSeconds * (100 - percent) / percent)
            : (TimeSpan?)null;
        var summary = remaining is null
            ? $"{percent:0}% · calculando tempo restante"
            : $"{percent:0}% · restam {FormatDuration(remaining.Value)}";
        ProgressSummary.Text = summary;
        DownloadProgressSummary.Text = summary;
    }

    private void UpdateQueueSummary(IReadOnlyList<DownloadTrack> queue)
    {
        var errors = queue.Count(track => track.IsError);
        var completed = queue.Count(track => track.Status == "Concluído");
        var skipped = queue.Count(track => track.Status == "Já baixada");
        var pending = queue.Count(track => track.Status == "Aguardando");
        QueueSummary.Text = $"{queue.Count} {(queue.Count == 1 ? "faixa" : "faixas")} encontradas";
        CompletedSummary.Text = $"{completed} concluídas";
        SkippedSummary.Text = $"{skipped} já baixadas";
        PendingSummary.Text = $"{pending} pendentes";
        ErrorSummary.Text = errors == 0 ? "" : $"{errors} {(errors == 1 ? "erro" : "erros")}";
        DetailCompletedCount.Text = completed.ToString();
        DetailFailedCount.Text = errors.ToString();
        DownloadGroupSummary.Text = $"Total {queue.Count}  ·  Concluídas {completed}  ·  Já baixadas {skipped}  ·  Falhas {errors}  ·  {OverallSpeedText.Text}";
        QueuePageSummary.Text = $"{pending} itens aguardando · {_activeDownloads} ativos";
        CompletedPageSummary.Text = $"{CompletedHistory.Count} músicas no histórico";
        _pendingCount = pending;
        SidebarActiveText.Text = $"{_activeDownloads} ativo(s) · {queue.Count(track => track.Status == "Na fila" || track.Status == "Aguardando")} na fila";
        UpdateBadges();
        RefreshTrackLists();
    }

    private void UpdateBadges()
    {
        var active = Math.Max(0, _activeDownloads);
        DownloadsBadgeText.Text = active.ToString();
        DownloadsBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBadgeText.Text = _pendingCount.ToString();
        QueueBadge.Visibility = _pendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAggregateSpeed(IReadOnlyList<DownloadTrack> queue)
    {
        var bytesPerSecond = queue.Sum(track => ParseSpeed(track.Speed));
        OverallSpeedText.Text = bytesPerSecond <= 0 ? "—" : FormatSpeed(bytesPerSecond);
        GroupSpeedText.Text = OverallSpeedText.Text;
    }

    private static double ParseSpeed(string speed)
    {
        var match = Regex.Match(speed, @"(?<value>[\d.,]+)\s*(?<unit>KiB/s|MiB/s|GiB/s|kB/s|MB/s|GB/s)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "kib/s" => 1024d, "mib/s" => 1024d * 1024, "gib/s" => 1024d * 1024 * 1024,
            "kb/s" => 1000d, "mb/s" => 1000d * 1000, "gb/s" => 1000d * 1000 * 1000,
            _ => 1d
        };
        return value * multiplier;
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024d * 1024
        ? $"{bytesPerSecond / (1024d * 1024):0.00} MiB/s"
        : bytesPerSecond >= 1024d ? $"{bytesPerSecond / 1024d:0.0} KiB/s" : $"{bytesPerSecond:0} B/s";

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}min"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}min {duration.Seconds:D2}s" : $"{duration.Seconds}s";

    private static HashSet<DownloadTrack> FindExistingTracks(IReadOnlyList<DownloadTrack> tracks, string folder)
    {
        var result = new HashSet<DownloadTrack>();
        if (!Directory.Exists(folder)) return result;
        var existingFiles = Directory.EnumerateFiles(folder)
            .Where(path => Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => NormalizeFileName(Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var expected = NormalizeFileName(string.IsNullOrWhiteSpace(track.Artist)
                ? track.Title : $"{track.Title} - {track.Artist}");
            var legacyName = string.IsNullOrWhiteSpace(track.Artist) ? expected + "faixa" : "";
            if (existingFiles.TryGetValue(expected, out var path) ||
                (legacyName.Length > 0 && existingFiles.TryGetValue(legacyName, out path)))
            {
                track.FilePath = path;
                result.Add(track);
            }
        }
        return result;
    }

    private static string NormalizeFileName(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private void SkipExisting_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.SkipExisting = SkipExistingCheckBox.IsChecked == true;
        SaveSettings();
        AddLog(_settings.SkipExisting ? "Pular faixas já baixadas: ativado." : "Pular faixas já baixadas: desativado.");
    }

    private void UseChromeCookies_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.UseChromeCookies = UseChromeCookiesCheckBox.IsChecked == true;
        SaveSettings();
        AddLog(_settings.UseChromeCookies
            ? "Cookies do Chrome ativados para buscas e downloads do YouTube."
            : "Cookies do Chrome desativados.");
    }

    private void AvoidLive_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AvoidLiveTracks = AvoidLiveCheckBox.IsChecked == true;
        SaveSettings();
        AddLog(_settings.AvoidLiveTracks
            ? "Filtro para evitar gravações ao vivo ativado."
            : "Filtro para evitar gravações ao vivo desativado.");
    }

    private async void DiscordPresenceSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.DiscordPresenceEnabled = DiscordPresenceCheckBox.IsChecked == true;
        SaveSettings();

        if (_currentPlayerTrack is not null)
            await UpdateDiscordPresenceAsync(_currentPlayerTrack);
        else if (!_settings.DiscordPresenceEnabled)
            await _discordPresence.ClearAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = _operationCancellation;
        if (!_busy || cancellation is null || cancellation.IsCancellationRequested || _cancelRequested) return;
        _cancelRequested = true;
        CancelButton.Content = "Cancelando…";
        CancelButton.IsEnabled = false;
        StatusText.Text = "●  CANCELANDO";
        AddLog("Cancelamento solicitado; aguardando os processos ativos encerrarem.");
        cancellation.Cancel();
    }

    private async void StartAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var queue = Tracks.Where(track => track.Status is "Na fila" or "Aguardando" or "Falhou" or "Cancelado").ToList();
        if (queue.Count == 0) return;
        foreach (var track in queue)
        {
            track.Status = "Aguardando";
            track.IsError = false;
            track.Progress = 0;
        }
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true);
        ShowPage(DownloadsPage, NavDownloads);
        try { await DownloadQueueAsync(queue, _operationCancellation.Token, _currentOutputDirectory); }
        catch (OperationCanceledException) { StatusText.Text = "●  CANCELADO"; AddLog("Fila cancelada."); }
        catch (Exception ex) { AddTechnicalLog($"Falha ao reiniciar a fila: {ex}"); AddLog($"ERRO ao reiniciar fila: {ex.Message}"); }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void RetryTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadTrack track }) return;
        if (_busy)
        {
            AddLog("Aguarde a fila atual terminar para repetir esta faixa.");
            return;
        }
        track.Status = "Aguardando";
        track.IsError = false;
        track.ErrorDetails = "";
        track.Progress = 0;
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true);
        try { await DownloadQueueAsync([track], _operationCancellation.Token, _currentOutputDirectory); }
        catch (OperationCanceledException) { StatusText.Text = "●  CANCELADO"; AddLog("Repetição cancelada."); }
        catch (Exception ex) { AddTechnicalLog($"Falha ao repetir faixa: {ex}"); AddLog($"ERRO ao repetir faixa: {ex.Message}"); }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OpenTrackFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadTrack track } ||
            string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
        {
            AddLog("Arquivo da faixa não encontrado no disco.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.FilePath}\"") { UseShellExecute = true });
    }

    private void PauseAllButton_Click(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? release = null;
        lock (_pauseLock)
        {
            _downloadsPaused = !_downloadsPaused;
            if (_downloadsPaused)
                _pauseRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            else
                release = _pauseRelease;
        }
        if (release is not null) release.TrySetResult(true);
        PauseAllButton.Content = _downloadsPaused ? "Retomar fila" : "Pausar fila";
        StatusText.Text = _downloadsPaused ? "●  PAUSANDO FILA" : "●  BAIXANDO";
        AddLog(_downloadsPaused
            ? "Fila pausada após as faixas já iniciadas terminarem."
            : "Fila retomada.");
    }

    private async Task WaitIfDownloadsPausedAsync(CancellationToken token)
    {
        Task waitTask;
        lock (_pauseLock) waitTask = _downloadsPaused ? _pauseRelease.Task : Task.CompletedTask;
        await waitTask.WaitAsync(token);
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string pageName) return;
        if (FindName(pageName) is Grid page) ShowPage(page, button);
    }

    private void ShowPage(Grid page, Button selected)
    {
        foreach (var child in new[] { HomePage, DownloadsPage, QueuePage, CompletedPage, ActivityPage, LogsPage, PlayerPage, SettingsPage, AboutPage })
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _navigationButtons)
        {
            var active = ReferenceEquals(button, selected);
            button.Background = active ? (System.Windows.Media.Brush)FindResource("SurfaceRaisedBrush") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = (System.Windows.Media.Brush)FindResource(active ? "AccentBrush" : "TextBrush");
            button.BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0);
            button.BorderBrush = active ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
        }
        if (ReferenceEquals(page, QueuePage) || ReferenceEquals(page, CompletedPage)) RefreshTrackLists();
        if (ReferenceEquals(page, PlayerPage)) RefreshPlayerLibrary();
    }

    private void RefreshPlayerLibrary()
    {
        if (PlayerPlaylistSelector is null) return;
        var previousDirectory = (PlayerPlaylistSelector.SelectedItem as PlayerPlaylistOption)?.DirectoryPath;
        var available = CompletedHistory
            .Where(track => !string.IsNullOrWhiteSpace(track.FilePath) && File.Exists(track.FilePath))
            .ToList();
        var directories = available
            .Select(track => Path.GetDirectoryName(track.FilePath!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _refreshingPlayerLibrary = true;
        try
        {
            PlayerPlaylists.Clear();
            PlayerPlaylists.Add(new PlayerPlaylistOption("Todas as músicas", null));
            foreach (var directory in directories)
            {
                var label = string.Equals(directory, _settings.OutputDirectory, StringComparison.OrdinalIgnoreCase)
                    ? "Músicas avulsas"
                    : Path.GetFileName(directory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(label)) label = directory!;
                PlayerPlaylists.Add(new PlayerPlaylistOption(label, directory));
            }

            var selected = PlayerPlaylists.FirstOrDefault(item =>
                string.Equals(item.DirectoryPath, previousDirectory, StringComparison.OrdinalIgnoreCase)) ?? PlayerPlaylists[0];
            PlayerPlaylistSelector.SelectedItem = selected;
            UpdatePlayerTrackList(selected.DirectoryPath, available);
        }
        finally { _refreshingPlayerLibrary = false; }
    }

    private void PlayerPlaylist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPlayerLibrary || PlayerPlaylistSelector.SelectedItem is not PlayerPlaylistOption selected) return;
        var available = CompletedHistory
            .Where(track => !string.IsNullOrWhiteSpace(track.FilePath) && File.Exists(track.FilePath))
            .ToList();
        _refreshingPlayerLibrary = true;
        try { UpdatePlayerTrackList(selected.DirectoryPath, available); }
        finally { _refreshingPlayerLibrary = false; }
    }

    private void UpdatePlayerTrackList(string? directory, IReadOnlyCollection<DownloadTrack> available)
    {
        var selectedTracks = available
            .Where(track => directory is null || string.Equals(Path.GetDirectoryName(track.FilePath!), directory, StringComparison.OrdinalIgnoreCase));
        var ordered = directory is null
            ? selectedTracks.OrderByDescending(track => track.CompletedAt ?? DateTime.MinValue).ToList()
            : selectedTracks.OrderBy(track => track.Number).ThenBy(track => track.CompletedAt ?? DateTime.MinValue).ToList();
        PlayerTracks.Clear();
        foreach (var track in ordered) PlayerTracks.Add(track);
        _playerTracksView?.Refresh();
        PlayerLibrarySummary.Text = $"{PlayerTracks.Count} {(PlayerTracks.Count == 1 ? "música disponível" : "músicas disponíveis")}";
        PlayerTrackList.SelectedItem = _currentPlayerTrack is not null && PlayerTracks.Contains(_currentPlayerTrack)
            ? _currentPlayerTrack : null;
    }

    private bool FilterPlayerTrack(object item)
    {
        if (item is not DownloadTrack track || string.IsNullOrWhiteSpace(_playerSearchQuery)) return true;
        return new[] { track.Title, track.Artist, track.Album }
            .Any(value => value.Contains(_playerSearchQuery, StringComparison.CurrentCultureIgnoreCase));
    }

    private void PlayerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _playerSearchQuery = PlayerSearchBox.Text.Trim();
        _playerTracksView?.Refresh();
    }

    private void PlayerTrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPlayerLibrary || PlayerTrackList.SelectedItem is not DownloadTrack track) return;
        PlayPlayerTrack(track);
    }

    private void PlayPlayerTrack(DownloadTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
        {
            PlayerStatusText.Text = "O arquivo não está disponível";
            RefreshPlayerLibrary();
            return;
        }
        if (ReferenceEquals(_currentPlayerTrack, track) && !_playerPaused) return;

        _currentPlayerTrack = track;
        PlayerCurrentTitle.Text = track.Title;
        PlayerCurrentArtist.Text = track.Artist;
        PlayerCurrentAlbum.Text = string.IsNullOrWhiteSpace(track.Album) ? "" : track.Album;
        MiniPlayerTitle.Text = track.Title;
        MiniPlayerArtist.Text = track.Artist;
        MiniPlayerBar.Visibility = Visibility.Visible;
        PlayerPositionText.Text = "0:00";
        MiniPlayerPositionText.Text = "0:00";
        PlayerDurationText.Text = track.DurationDisplay == "—" ? "0:00" : track.DurationDisplay;
        MiniPlayerDurationText.Text = PlayerDurationText.Text;
        PlayerSeekSlider.Value = 0;
        MiniPlayerSeekSlider.Value = 0;
        PlayerStatusText.Text = "Carregando música…";
        _playerPaused = false;
        SetPlayerPlayPauseIcon(playing: true);
        UpdateSystemMediaSession(track, MediaPlaybackStatus.Playing);
        PlayerMedia.Stop();
        PlayerMedia.Source = new Uri(Path.GetFullPath(track.FilePath), UriKind.Absolute);
        PlayerMedia.Play();
        _ = SetPlayerArtworkAsync(track);
    }

    private async Task SetPlayerArtworkAsync(DownloadTrack track)
    {
        PlayerCoverImage.Source = null;
        PlayerCoverImage.Visibility = Visibility.Collapsed;
        PlayerCoverFallback.Visibility = Visibility.Visible;
        MiniPlayerCoverImage.Source = null;
        MiniPlayerCoverImage.Visibility = Visibility.Collapsed;
        MiniPlayerCoverFallback.Visibility = Visibility.Visible;
        if (!Uri.TryCreate(track.ArtworkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            var data = await ArtworkHttp.GetByteArrayAsync(uri);
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (!ReferenceEquals(_currentPlayerTrack, track)) return;
            PlayerCoverImage.Source = image;
            PlayerCoverImage.Visibility = Visibility.Visible;
            PlayerCoverFallback.Visibility = Visibility.Collapsed;
            MiniPlayerCoverImage.Source = image;
            MiniPlayerCoverImage.Visibility = Visibility.Visible;
            MiniPlayerCoverFallback.Visibility = Visibility.Collapsed;
        }
        catch { /* A capa é opcional; a música local deve tocar mesmo sem rede. */ }
    }

    private void TogglePlayerPlayback()
    {
        if (_currentPlayerTrack is null)
        {
            var selected = PlayerTrackList.SelectedItem as DownloadTrack ?? PlayerTracks.FirstOrDefault();
            if (selected is not null) PlayPlayerTrack(selected);
            else PlayerStatusText.Text = "Nenhuma música baixada disponível";
            return;
        }
        if (_playerPaused)
        {
            if (PlayerMedia.Source is null) { PlayPlayerTrack(_currentPlayerTrack); return; }
            if (PlayerMedia.NaturalDuration.HasTimeSpan && PlayerMedia.Position >= PlayerMedia.NaturalDuration.TimeSpan)
                PlayerMedia.Position = TimeSpan.Zero;
            PlayerMedia.Play();
            _playerPaused = false;
            SetPlayerPlayPauseIcon(playing: true);
            PlayerStatusText.Text = "Tocando";
            if (_currentPlayerTrack is not null) UpdateSystemMediaSession(_currentPlayerTrack, MediaPlaybackStatus.Playing);
            if (_currentPlayerTrack is not null) _ = UpdateDiscordPresenceAsync(_currentPlayerTrack);
        }
        else
        {
            PlayerMedia.Pause();
            _playerPaused = true;
            SetPlayerPlayPauseIcon(playing: false);
            PlayerStatusText.Text = "Pausado";
            if (_currentPlayerTrack is not null) UpdateSystemMediaSession(_currentPlayerTrack, MediaPlaybackStatus.Paused);
            if (_currentPlayerTrack is not null) _ = UpdateDiscordPresenceAsync(_currentPlayerTrack);
        }
    }

    private DownloadTrack[] CurrentPlayerSequence() =>
        _playerTracksView?.Cast<DownloadTrack>().ToArray() ?? PlayerTracks.ToArray();

    private void PlayNextTrack(bool automatic = false)
    {
        var sequence = CurrentPlayerSequence();
        if (sequence.Length == 0)
        {
            if (automatic)
            {
                _playerPaused = true;
                SetPlayerPlayPauseIcon(playing: false);
                PlayerStatusText.Text = "Concluído";
                if (_currentPlayerTrack is not null) UpdateSystemMediaSession(_currentPlayerTrack, MediaPlaybackStatus.Stopped);
                _ = _discordPresence.ClearAsync();
            }
            return;
        }
        var index = _currentPlayerTrack is null ? -1 : Array.IndexOf(sequence, _currentPlayerTrack);
        DownloadTrack next;
        if (_shuffleEnabled && sequence.Length > 1)
        {
            var candidates = sequence.Where(track => !ReferenceEquals(track, _currentPlayerTrack)).ToArray();
            next = candidates[Random.Shared.Next(candidates.Length)];
        }
        else
        {
            if (automatic && _repeatMode == 0 && index >= sequence.Length - 1)
            {
                _playerPaused = true;
                SetPlayerPlayPauseIcon(playing: false);
                PlayerStatusText.Text = "Concluído";
                UpdateSystemMediaSession(_currentPlayerTrack!, MediaPlaybackStatus.Stopped);
                _ = _discordPresence.ClearAsync();
                return;
            }
            next = sequence[(index + 1 + sequence.Length) % sequence.Length];
        }
        _refreshingPlayerLibrary = true;
        PlayerTrackList.SelectedItem = next;
        _refreshingPlayerLibrary = false;
        PlayPlayerTrack(next);
    }

    private void PlayPreviousTrack()
    {
        var sequence = CurrentPlayerSequence();
        if (sequence.Length == 0) return;
        if (PlayerMedia.Source is not null && PlayerMedia.Position.TotalSeconds > 3)
        {
            PlayerMedia.Position = TimeSpan.Zero;
            return;
        }
        var index = _currentPlayerTrack is null ? 0 : Array.IndexOf(sequence, _currentPlayerTrack);
        var previous = sequence[(index - 1 + sequence.Length) % sequence.Length];
        _refreshingPlayerLibrary = true;
        PlayerTrackList.SelectedItem = previous;
        _refreshingPlayerLibrary = false;
        PlayPlayerTrack(previous);
    }

    private void PlayerPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayerPlayback();
    private void PlayerNext_Click(object sender, RoutedEventArgs e) => PlayNextTrack();
    private void PlayerPrevious_Click(object sender, RoutedEventArgs e) => PlayPreviousTrack();

    private void PlayerShuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffleEnabled = !_shuffleEnabled;
        PlayerShuffleButton.IsChecked = _shuffleEnabled;
        MiniPlayerShuffleButton.IsChecked = _shuffleEnabled;
        var state = _shuffleEnabled ? "ativado" : "desativado";
        PlayerShuffleButton.ToolTip = MiniPlayerShuffleButton.ToolTip = $"Reprodução aleatória {state}";
    }

    private void PlayerRepeat_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = (_repeatMode + 1) % 3;
        var label = _repeatMode switch { 1 => "Repetir playlist", 2 => "Repetir música", _ => "Repetição desligada" };
        var icon = _repeatMode == 2 ? "↻¹" : "↻";
        PlayerRepeatButton.Content = icon;
        MiniPlayerRepeatButton.Content = icon;
        PlayerRepeatButton.IsChecked = MiniPlayerRepeatButton.IsChecked = _repeatMode != 0;
        PlayerRepeatButton.ToolTip = MiniPlayerRepeatButton.ToolTip = label;
    }

    private void PlayerOpen_Click(object sender, RoutedEventArgs e) => ShowPage(PlayerPage, NavPlayer);

    private void PlayerMute_Click(object sender, RoutedEventArgs e)
    {
        if (_playerMuted || PlayerVolumeSlider.Value <= 0)
            PlayerVolumeSlider.Value = Math.Clamp(_previousPlayerVolume, 0.05, 1);
        else
        {
            _previousPlayerVolume = PlayerVolumeSlider.Value;
            PlayerVolumeSlider.Value = 0;
        }
    }

    private void PlayerVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlayerMedia is null || PlayerVolumeText is null) return;
        PlayerMedia.Volume = Math.Clamp(e.NewValue, 0, 1);
        PlayerVolumeText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        MiniPlayerVolumeText.Text = PlayerVolumeText.Text;
        if (!ReferenceEquals(sender, PlayerVolumeSlider) && Math.Abs(PlayerVolumeSlider.Value - e.NewValue) > 0.001)
            PlayerVolumeSlider.Value = e.NewValue;
        if (!ReferenceEquals(sender, MiniPlayerVolumeSlider) && Math.Abs(MiniPlayerVolumeSlider.Value - e.NewValue) > 0.001)
            MiniPlayerVolumeSlider.Value = e.NewValue;
        _playerMuted = e.NewValue <= 0.001;
        if (!_playerMuted) _previousPlayerVolume = e.NewValue;
        if (PlayerVolumeIcon is not null)
        {
            PlayerVolumeIcon.Data = (System.Windows.Media.Geometry)FindResource(_playerMuted ? "PlayerMutedIcon" : "PlayerVolumeIcon");
            PlayerMuteButton.ToolTip = _playerMuted ? "Restaurar volume" : "Silenciar";
        }
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (_playerPaused || PlayerMedia.Source is null || !PlayerMedia.NaturalDuration.HasTimeSpan) return;
        var duration = PlayerMedia.NaturalDuration.TimeSpan;
        var position = PlayerMedia.Position;
        if (!_playerSeeking) PlayerSeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, Math.Max(1, duration.TotalSeconds));
        if (!_miniPlayerSeeking) MiniPlayerSeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, Math.Max(1, duration.TotalSeconds));
        PlayerPositionText.Text = FormatPlayerTime(position);
        MiniPlayerPositionText.Text = PlayerPositionText.Text;
        PlayerDurationText.Text = FormatPlayerTime(duration);
        MiniPlayerDurationText.Text = PlayerDurationText.Text;
    }

    private void PlayerMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!PlayerMedia.NaturalDuration.HasTimeSpan) return;
        PlayerSeekSlider.Maximum = Math.Max(1, PlayerMedia.NaturalDuration.TimeSpan.TotalSeconds);
        MiniPlayerSeekSlider.Maximum = PlayerSeekSlider.Maximum;
        PlayerDurationText.Text = FormatPlayerTime(PlayerMedia.NaturalDuration.TimeSpan);
        MiniPlayerDurationText.Text = PlayerDurationText.Text;
        PlayerStatusText.Text = _playerPaused ? "Pausado" : "Tocando";
        if (_currentPlayerTrack is not null) _ = UpdateDiscordPresenceAsync(_currentPlayerTrack);
    }

    private void PlayerMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_repeatMode == 2 && _currentPlayerTrack is not null)
        {
            // Reopen the same local file so the decoder reliably starts at zero.
            PlayPlayerTrack(_currentPlayerTrack);
            return;
        }
        // Normal playback advances through the selected library too; repeat-all
        // wraps at the end and shuffle chooses another track.
        PlayNextTrack(automatic: true);
    }

    private void PlayerMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playerPaused = true;
        SetPlayerPlayPauseIcon(playing: false);
        PlayerStatusText.Text = "Não foi possível reproduzir este arquivo";
        _ = _discordPresence.ClearAsync();
        if (e.ErrorException is not null) AddTechnicalLog($"Falha no player local: {e.ErrorException}");
    }

    private void PlayerSeekStart(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, MiniPlayerSeekSlider)) _miniPlayerSeeking = true;
        else _playerSeeking = true;
    }

    private void PlayerSeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, MiniPlayerSeekSlider)) _miniPlayerSeeking = false;
        else _playerSeeking = false;
        if (sender is Slider slider) SeekPlayerToSlider(slider);
    }

    private void PlayerSeekKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Slider slider) SeekPlayerToSlider(slider);
    }

    private void SeekPlayerToSlider(Slider slider)
    {
        if (PlayerMedia.Source is null || !PlayerMedia.NaturalDuration.HasTimeSpan) return;
        var seconds = Math.Clamp(slider.Value, 0, PlayerMedia.NaturalDuration.TimeSpan.TotalSeconds);
        PlayerMedia.Position = TimeSpan.FromSeconds(seconds);
        var position = FormatPlayerTime(PlayerMedia.Position);
        PlayerPositionText.Text = position;
        MiniPlayerPositionText.Text = position;
        if (!ReferenceEquals(slider, PlayerSeekSlider)) PlayerSeekSlider.Value = seconds;
        if (!ReferenceEquals(slider, MiniPlayerSeekSlider)) MiniPlayerSeekSlider.Value = seconds;
        if (_currentPlayerTrack is not null) _ = UpdateDiscordPresenceAsync(_currentPlayerTrack);
    }

    private void SetPlayerPlayPauseIcon(bool playing)
    {
        if (PlayerPlayPauseIcon is null) return;
        PlayerPlayPauseIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "PlayerPauseIcon" : "PlayerPlayIcon");
        MiniPlayerPlayPauseIcon.Data = PlayerPlayPauseIcon.Data;
        PlayerPlayPauseButton.ToolTip = playing ? "Pausar · Espaço" : "Reproduzir · Espaço";
    }

    private static string FormatPlayerTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private void RefreshTrackLists()
    {
        if (QueueGrid is null || CompletedGrid is null) return;
        var waiting = Tracks.Where(track => track.Status is "Na fila" or "Aguardando").ToList();
        QueueGrid.ItemsSource = waiting;
        CompletedGrid.ItemsSource = CompletedHistory;
        QueuePageSummary.Text = $"{waiting.Count} itens aguardando · {_activeDownloads} ativos";
        CompletedPageSummary.Text = $"{CompletedHistory.Count} músicas no histórico";
    }

    private void GoDownloads_Click(object sender, RoutedEventArgs e) => ShowPage(DownloadsPage, NavDownloads);
    private void GoSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, NavSettings);
    private void GoHome_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage, NavHome);

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_technicalLogs.Count == 0) { AddLog("Ainda não há diagnósticos técnicos para copiar."); return; }
        Clipboard.SetText(string.Join(Environment.NewLine, _technicalLogs));
        AddLog("Logs técnicos copiados para a área de transferência.");
    }

    private void DownloadGroup_Toggled(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid is null || DownloadGroupToggle is null) return;
        DownloadsListBorder.Visibility = DownloadGroupToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DownloadGroupToggle.Content = DownloadGroupToggle.IsChecked == true ? "▾" : "▸";
    }

    private void AddCompletedHistory(DownloadTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath)) return;
        var previous = CompletedHistory.FirstOrDefault(item =>
            string.Equals(item.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) CompletedHistory.Remove(previous);
        CompletedHistory.Insert(0, track);
        while (CompletedHistory.Count > 2000) CompletedHistory.RemoveAt(CompletedHistory.Count - 1);
        SaveCompletedHistory();
        RefreshPlayerLibrary();
    }

    private static List<DownloadTrack> LoadCompletedHistory()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return [];
            return JsonSerializer.Deserialize<List<DownloadTrack>>(File.ReadAllText(HistoryFilePath)) ?? [];
        }
        catch { return []; }
    }

    private static List<string> LoadTechnicalLogs()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotiFetch", "technical.log");
            return File.Exists(path) ? File.ReadLines(path).TakeLast(300).ToList() : [];
        }
        catch { return []; }
    }

    private void SaveCompletedHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(CompletedHistory,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AddLog($"Não foi possível salvar o histórico de concluídos: {ex.Message}"); }
    }

    private void CompletedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = CompletedGrid.SelectedItem is DownloadTrack { FilePath: not null } track && File.Exists(track.FilePath);
        OpenCompletedButton.IsEnabled = selected;
        OpenCompletedFolderButton.IsEnabled = selected;
    }

    private void OpenCompletedFile_Click(object sender, RoutedEventArgs e)
    {
        if (CompletedGrid.SelectedItem is not DownloadTrack track || string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath)) return;
        Process.Start(new ProcessStartInfo(track.FilePath) { UseShellExecute = true });
    }

    private void OpenCompletedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CompletedGrid.SelectedItem is not DownloadTrack track || string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.FilePath}\"") { UseShellExecute = true });
    }

    private void AddTechnicalLog(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var category = message.Contains("erro", StringComparison.OrdinalIgnoreCase) || message.Contains("falha", StringComparison.OrdinalIgnoreCase)
                ? "ERRO" : message.Contains("concluído", StringComparison.OrdinalIgnoreCase) ? "CONCLUÍDO" : "EVENTO";
            var entry = $"{DateTime.Now:HH:mm:ss}  {category}  {message}";
            _technicalLogs.Add(entry);
            if (_technicalLogs.Count > 300) _technicalLogs.RemoveAt(0);
            try
            {
                var logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotiFetch", "technical.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                File.AppendAllText(logFile, entry + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            DetailedLogsList.Items.Add(entry);
            if (DetailedLogsList.Items.Count > 300) DetailedLogsList.Items.RemoveAt(0);
            DetailedLogsList.ScrollIntoView(entry);
        }, DispatcherPriority.Background);
    }

    private void WorkersPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton item) return;
        var tag = item.Tag?.ToString();
        var custom = tag == "custom";
        CustomWorkersPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!_initializing && !custom && int.TryParse(tag, out var workers)) SetWorkerCount(workers);
        else WorkersValueText.Text = $"{(int)WorkersSlider.Value} thread{(WorkersSlider.Value == 1 ? "" : "s")}";
    }

    private void WorkersSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WorkersValueText is null) return;
        var value = (int)Math.Round(e.NewValue);
        WorkersValueText.Text = $"{value} thread{(value == 1 ? "" : "s")}";
        if (!_initializing && WorkersCustom?.IsChecked == true) SetWorkerCount(value);
    }

    private void SetWorkerCount(int value)
    {
        _settings.MaxWorkers = Math.Clamp(value, 1, 32);
        SaveSettings();
        AddLog($"Threads simultâneas: {_settings.MaxWorkers}.");
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha onde salvar as músicas",
            InitialDirectory = Directory.Exists(_settings.OutputDirectory)
                ? _settings.OutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.OutputDirectory = dialog.FolderName;
        Directory.CreateDirectory(_settings.OutputDirectory);
        OutputPathText.Text = _settings.OutputDirectory;
        SaveSettings();
        AddLog($"Pasta alterada: {_settings.OutputDirectory}");
    }

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton item) return;
        _settings.Bitrate = item.Tag?.ToString() ?? "320";
        DetailBitrateSummary.Text = $"{_settings.Bitrate} kbps";
        SaveSettings();
        AddLog($"Qualidade configurada: {_settings.Bitrate} kbps.");
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { AddLog($"Não foi possível salvar as configurações: {ex.Message}"); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _cancelRequested = false;
        SearchButton.IsEnabled = !busy;
        InputBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        DownloadCancelButton.IsEnabled = busy;
        PauseAllButton.IsEnabled = busy;
        StartAllButton.IsEnabled = !busy && Tracks.Any(track => track.Status is "Na fila" or "Aguardando" or "Falhou" or "Cancelado");
        CancelButton.Content = "Cancelar";
        if (busy) StatusText.Text = "●  PROCESSANDO";
        else if (StatusText.Text == "●  PROCESSANDO") StatusText.Text = "●  PRONTO";
        if (!busy && StatusText.Text == "●  PRONTO") { ProgressSummary.Text = ""; DownloadProgressSummary.Text = ""; }
        else if (!busy && StatusText.Text == "●  CANCELADO") { ProgressSummary.Text = $"{OverallProgress.Value:0}% · cancelado"; DownloadProgressSummary.Text = ProgressSummary.Text; }
        else if (!busy && StatusText.Text.Contains("CONCLUÍDO", StringComparison.Ordinal)) { ProgressSummary.Text = "100% · concluído"; DownloadProgressSummary.Text = ProgressSummary.Text; }
    }

    private void AddLog(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var eventType = message.Contains("falha", StringComparison.OrdinalIgnoreCase) ||
                            message.Contains("erro", StringComparison.OrdinalIgnoreCase) ? "ERRO" :
                            message.Contains("concluído", StringComparison.OrdinalIgnoreCase) ? "DOWNLOAD" :
                            message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "CONTROLE" :
                            message.Contains("playlist", StringComparison.OrdinalIgnoreCase) ? "PLAYLIST" : "INFO";
            var entry = $"{time}  {message}";
            ActivityList.Items.Add(new ActivityEntry(time, eventType, message));
            if (ActivityList.Items.Count > 300) ActivityList.Items.RemoveAt(0);
            ActivityList.ScrollIntoView(ActivityList.Items[^1]);
            HomeRecentList.Items.Add(entry);
            if (HomeRecentList.Items.Count > 12) HomeRecentList.Items.RemoveAt(0);
            HomeRecentList.ScrollIntoView(HomeRecentList.Items[^1]);
            _recentLogs.Add(entry);
            if (_recentLogs.Count > 30) _recentLogs.RemoveAt(0);
        }, DispatcherPriority.Background);
    }

    private Task OnUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.InvokeAsync(action).Task;
    }
}
