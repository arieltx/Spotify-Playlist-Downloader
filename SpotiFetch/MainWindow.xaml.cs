using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LaurusDownloader;

public partial class MainWindow : Window
{
    public ObservableCollection<DownloadTrack> Tracks { get; } = [];

    private readonly UserSettings _settings;
    private readonly SpotifyEmbedClient _spotify = new();
    private readonly DownloadEngine _downloader = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _initializing = true;
    private bool _busy;
    private bool _cancelRequested;
    private Stopwatch? _queueClock;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _spotify.Diagnostic += AddLog;
        _settings = UserSettings.Load();
        OutputPathText.Text = _settings.OutputDirectory;
        ( _settings.Bitrate switch
        {
            "256" => Quality256,
            "128" => Quality128,
            _ => Quality320
        }).IsChecked = true;
        _settings.MaxWorkers = Math.Clamp(_settings.MaxWorkers, 1, 16);
        SkipExistingCheckBox.IsChecked = _settings.SkipExisting;
        WorkersSlider.Value = _settings.MaxWorkers;
        WorkersValueText.Text = $"{_settings.MaxWorkers} thread{(_settings.MaxWorkers == 1 ? "" : "s")}";
        var workerPresets = new[] { 1, 4, 6, 8 };
        var presetIndex = Array.IndexOf(workerPresets, _settings.MaxWorkers);
        CustomWorkersPanel.Visibility = presetIndex >= 0 ? Visibility.Collapsed : Visibility.Visible;
        (presetIndex switch
        {
            0 => Workers1,
            1 => Workers4,
            2 => Workers6,
            3 => Workers8,
            _ => WorkersCustom
        }).IsChecked = true;
        _initializing = false;
        Directory.CreateDirectory(_settings.OutputDirectory);
        AddLog("SpotiFetch pronto. Cole um link público ou digite uma busca.");
        Closed += (_, _) => _operationCancellation?.Cancel();
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
        _operationCancellation = new CancellationTokenSource();
        var token = _operationCancellation.Token;
        Tracks.Clear();
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
            QueueSummary.Text = $"{Tracks.Count} faixas na fila";
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
            NowTitle.Text = sourceName;
            NowDetail.Text = queue.Count == 1
                ? $"{queue[0].Title}{(string.IsNullOrWhiteSpace(queue[0].Artist) ? "" : $" — {queue[0].Artist}")}"
                : $"{queue.Count} faixas encontradas";
            QueueSummary.Text = $"{queue.Count} {(queue.Count == 1 ? "faixa" : "faixas")} na fila";
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
        Directory.CreateDirectory(outputDirectory);
        var workerCount = Math.Clamp(_settings.MaxWorkers, 1, 16);
        using var slots = new SemaphoreSlim(workerCount, workerCount);
        _queueClock = Stopwatch.StartNew();
        var existing = _settings.SkipExisting ? FindExistingTracks(queue, outputDirectory) : new HashSet<DownloadTrack>();
        foreach (var track in existing)
        {
            track.Status = "Já baixada";
            track.Progress = 100;
        }
        UpdateQueueSummary(queue);
        UpdateOverallProgress(queue);
        StatusText.Text = "●  BAIXANDO";
        var jobs = queue.Select(async track =>
        {
            if (existing.Contains(track)) return;
            var hasSlot = false;
            try
            {
                await slots.WaitAsync(token);
                hasSlot = true;
                await OnUiAsync(() => track.Status = "Baixando");
                await _downloader.DownloadAsync(track, outputDirectory, _settings.Bitrate, token,
                    message => _ = OnUiAsync(() => track.Status = message),
                    percent => _ = OnUiAsync(() =>
                    {
                        track.Progress = percent;
                        UpdateOverallProgress(queue);
                    }));
                await OnUiAsync(() => { track.Status = "Concluído"; track.Progress = 100; UpdateOverallProgress(queue); });
                await OnUiAsync(() => UpdateQueueSummary(queue));
                AddLog($"Concluído: {track.Title}");
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() => track.Status = "Cancelado");
                throw;
            }
            catch (Exception ex)
            {
                await OnUiAsync(() => { track.Status = "Erro"; track.IsError = true; UpdateQueueSummary(queue); });
                AddLog($"Falha em {track.Title}: {ex.Message}");
            }
            finally
            {
                if (hasSlot) slots.Release();
            }
        });
        try
        {
            await Task.WhenAll(jobs);
            StatusText.Text = queue.Any(item => item.Status == "Erro") ? "●  CONCLUÍDO COM ERROS" : "●  CONCLUÍDO";
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() =>
            {
                foreach (var track in queue)
                    if (track.Status is not ("Concluído" or "Erro")) track.Status = "Cancelado";
            });
            throw;
        }
        AddLog("Fila finalizada.");
    }

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
        var remaining = percent > 0 && _queueClock is not null
            ? TimeSpan.FromSeconds(_queueClock.Elapsed.TotalSeconds * (100 - percent) / percent)
            : (TimeSpan?)null;
        ProgressSummary.Text = remaining is null
            ? $"{percent:0}% · calculando tempo restante"
            : $"{percent:0}% · restam {FormatDuration(remaining.Value)}";
    }

    private void UpdateQueueSummary(IReadOnlyList<DownloadTrack> queue)
    {
        var errors = queue.Count(track => track.IsError);
        QueueSummary.Text = $"{queue.Count} {(queue.Count == 1 ? "faixa" : "faixas")} na fila";
        ErrorSummary.Text = errors == 0 ? "" : $"{errors} {(errors == 1 ? "erro" : "erros")}";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}min"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}min {duration.Seconds:D2}s" : $"{duration.Seconds}s";

    private static HashSet<DownloadTrack> FindExistingTracks(IReadOnlyList<DownloadTrack> tracks, string folder)
    {
        var result = new HashSet<DownloadTrack>();
        if (!Directory.Exists(folder)) return result;
        var existingNames = Directory.EnumerateFiles(folder)
            .Where(path => Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizeFileName(Path.GetFileNameWithoutExtension(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var expected = NormalizeFileName(string.IsNullOrWhiteSpace(track.Artist)
                ? track.Title : $"{track.Title} - {track.Artist}");
            var legacyUntitledArtist = string.IsNullOrWhiteSpace(track.Artist) && existingNames.Contains(expected + "faixa");
            if (existingNames.Contains(expected) || legacyUntitledArtist) result.Add(track);
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var show = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Opacity = show ? 1 : 0;
        SettingsButton.Content = "\uE713";
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
        _settings.MaxWorkers = Math.Clamp(value, 1, 16);
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
        CancelButton.Content = "Cancelar";
        if (busy) StatusText.Text = "●  PROCESSANDO";
        else if (StatusText.Text == "●  PROCESSANDO") StatusText.Text = "●  PRONTO";
        if (!busy && StatusText.Text == "●  PRONTO") ProgressSummary.Text = "";
        else if (!busy && StatusText.Text == "●  CANCELADO") ProgressSummary.Text = $"{OverallProgress.Value:0}% · cancelado";
        else if (!busy && StatusText.Text.Contains("CONCLUÍDO", StringComparison.Ordinal)) ProgressSummary.Text = "100% · concluído";
    }

    private void AddLog(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            ActivityList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            if (ActivityList.Items.Count > 300) ActivityList.Items.RemoveAt(0);
            ActivityList.ScrollIntoView(ActivityList.Items[^1]);
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
