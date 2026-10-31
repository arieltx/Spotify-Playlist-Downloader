using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace SpotiFetch;

public sealed class DownloadEngine
{
    private static readonly HttpClient Http = new();

    public async Task DownloadAsync(
        DownloadTrack track,
        string outputDirectory,
        string bitrate,
        CancellationToken cancellationToken,
        Action<string>? statusChanged = null,
        Action<double>? progressChanged = null,
        bool useChromeCookies = false,
        Action<DownloadProgressInfo>? telemetryChanged = null)
    {
        var ytdlp = FindExecutable("yt-dlp.exe", "yt-dlp");
        if (ytdlp is null)
            throw new FileNotFoundException("yt-dlp.exe não foi encontrado. Coloque-o junto do aplicativo ou adicione-o ao PATH.");

        var ffmpeg = FindExecutable("ffmpeg.exe", "ffmpeg");
        if (ffmpeg is null)
            throw new FileNotFoundException("ffmpeg.exe não foi encontrado. Coloque-o junto do aplicativo ou adicione-o ao PATH.");

        Directory.CreateDirectory(outputDirectory);
        track.Bitrate = $"{bitrate} kbps";
        var safeTitle = SafeFilePart(track.Title);
        var fileName = string.IsNullOrWhiteSpace(track.Artist)
            ? safeTitle
            : $"{safeTitle} - {SafeFilePart(track.Artist)}";
        var workRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotiFetch", "Work");
        var workDirectory = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(workDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string input;
            if (!string.IsNullOrWhiteSpace(track.SourceUrl))
            {
                input = track.SourceUrl!;
            }
            else
            {
                try
                {
                    input = await FindBestVideoAsync(ytdlp, track, cancellationToken, statusChanged, useChromeCookies);
                }
                catch (InvalidOperationException ex) when (
                    useChromeCookies && ex.Message.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase))
                {
                    statusChanged?.Invoke("Chrome está usando os cookies; tentando buscar sem autenticação do navegador");
                    useChromeCookies = false;
                    input = await FindBestVideoAsync(ytdlp, track, cancellationToken, statusChanged, useChromeCookies: false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var downloadArguments = new[]
            {
                "--no-playlist", "--no-continue", "--extract-audio", "--audio-format", "mp3",
                "--audio-quality", $"{bitrate}K", "--ffmpeg-location", ffmpeg,
                "--newline", "--no-warnings", "--no-embed-thumbnail", "--retry-sleep", "http:2",
                "--output", Path.Combine(workDirectory, fileName + ".%(ext)s"), input
            };
            var (exitCode, diagnostic) = await RunDownloadProcessAsync(
                ytdlp, workDirectory, downloadArguments, useChromeCookies,
                cancellationToken, statusChanged, progressChanged, telemetryChanged);

            if (exitCode != 0 && useChromeCookies && IsChromeCookieCopyError(diagnostic))
            {
                statusChanged?.Invoke("Não foi possível acessar os cookies do Chrome; tentando o download sem cookies");
                useChromeCookies = false;
                (exitCode, diagnostic) = await RunDownloadProcessAsync(
                    ytdlp, workDirectory, downloadArguments, useChromeCookies: false,
                    cancellationToken, statusChanged, progressChanged, telemetryChanged);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (exitCode != 0)
                throw new InvalidOperationException(FormatYtDlpError(diagnostic));

            var audioPath = Path.Combine(workDirectory, fileName + ".mp3");
            if (!File.Exists(audioPath))
                throw new InvalidOperationException("O download terminou, mas o arquivo MP3 não foi gerado.");
            statusChanged?.Invoke("Convertendo e aplicando tags");
            await ApplyTagsAsync(ffmpeg, audioPath, track, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = MoveToAvailableFileName(audioPath, outputDirectory, fileName);
            track.FileSizeBytes = new FileInfo(finalPath).Length;
            track.FilePath = finalPath;
            track.CompletedAt = DateTime.Now;
            progressChanged?.Invoke(100);
        }
        finally
        {
            await CleanupWorkDirectoryAsync(workDirectory, statusChanged);
        }
    }

    private static async Task<(int ExitCode, string Diagnostic)> RunDownloadProcessAsync(
        string ytdlp,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool useChromeCookies,
        CancellationToken cancellationToken,
        Action<string>? statusChanged,
        Action<double>? progressChanged,
        Action<DownloadProgressInfo>? telemetryChanged)
    {
        var start = new ProcessStartInfo
        {
            FileName = ytdlp,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var i = 0; i < arguments.Count; i++)
        {
            if (useChromeCookies && i == arguments.Count - 1)
            {
                start.ArgumentList.Add("--cookies-from-browser");
                start.ArgumentList.Add("chrome");
            }
            start.ArgumentList.Add(arguments[i]);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => ReportLine(e.Data, statusChanged, progressChanged, telemetryChanged);
        var errorLines = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (errorLines) errorLines.Add(e.Data);
            ReportLine(e.Data, statusChanged, progressChanged, telemetryChanged);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var killOnCancel = cancellationToken.Register(() => TryKillProcessTree(process));
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            await EnsureProcessStoppedAsync(process);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        string diagnostic;
        lock (errorLines) diagnostic = string.Join(" | ", errorLines.TakeLast(5));
        return (process.ExitCode, diagnostic);
    }

    private static bool IsChromeCookieCopyError(string detail) =>
        detail.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> FindBestVideoAsync(
        string ytdlp, DownloadTrack track, CancellationToken token, Action<string>? statusChanged, bool useChromeCookies)
    {
        statusChanged?.Invoke("Procurando a melhor correspondência de áudio");
        var query = string.Join(" ", new[] { track.Title, track.Artist }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var start = new ProcessStartInfo
        {
            FileName = ytdlp, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "--dump-single-json", "--flat-playlist", "--skip-download", "--no-warnings", "--quiet",
            "--retry-sleep", "http:2"
        })
            start.ArgumentList.Add(arg);
        if (useChromeCookies)
        {
            start.ArgumentList.Add("--cookies-from-browser");
            start.ArgumentList.Add("chrome");
        }
        start.ArgumentList.Add($"ytsearch5:{query}");

        using var process = new Process { StartInfo = start };
        process.Start();
        using var killOnCancel = token.Register(() =>
            TryKillProcessTree(process));
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            await EnsureProcessStoppedAsync(process);
            throw;
        }
        token.ThrowIfCancellationRequested();
        var json = await stdout;
        var diagnostic = await stderr;
        if (process.ExitCode == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var entries = document.RootElement.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().ToArray()
                    : [document.RootElement];
                var best = entries
                    .Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out _))
                    .Select(entry => (Entry: entry, Score: ScoreCandidate(entry, track)))
                    .OrderByDescending(result => result.Score)
                    .FirstOrDefault();
                if (best.Entry.ValueKind == JsonValueKind.Object)
                {
                    if (best.Entry.TryGetProperty("webpage_url", out var page) && page.ValueKind == JsonValueKind.String)
                        return page.GetString()!;
                    var id = best.Entry.GetProperty("id").GetString();
                    if (!string.IsNullOrWhiteSpace(id)) return $"https://www.youtube.com/watch?v={id}";
                }
            }
            catch (JsonException) { }
        }
        if (!string.IsNullOrWhiteSpace(diagnostic) && process.ExitCode != 0)
            throw new InvalidOperationException(FormatYtDlpError(diagnostic));
        return $"ytsearch1:{query}";
    }

    private static string FormatYtDlpError(string detail)
    {
        if (IsChromeCookieCopyError(detail))
            return "O yt-dlp não conseguiu ler o banco de cookies do Chrome. O SpotiFetch tentou continuar sem cookies, mas ainda houve falha. Feche o Chrome ou desative o uso dos cookies nas configurações e tente novamente. Detalhe: " + detail;
        if (detail.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase))
            return "O Windows/Chrome protegeu os cookies com uma chave que o yt-dlp não conseguiu descriptografar. Use uma sessão do YouTube autenticada no Edge ou importe um arquivo cookies.txt exportado localmente. Detalhe: " + detail;
        if (detail.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sign in to confirm", StringComparison.OrdinalIgnoreCase))
            return "O YouTube bloqueou a busca automatizada. Ative 'Usar cookies do Chrome' nas configurações, entre no YouTube pelo Chrome e tente novamente. Detalhe: " + detail;
        return string.IsNullOrWhiteSpace(detail)
            ? "yt-dlp terminou com erro sem informar detalhes. Verifique a aba Atividade."
            : "yt-dlp terminou com erro: " + detail;
    }

    private static double ScoreCandidate(JsonElement candidate, DownloadTrack track)
    {
        var title = candidate.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? "" : "";
        var queryTokens = Tokens($"{track.Title} {track.Artist}");
        var titleTokens = Tokens(title);
        var overlap = queryTokens.Count == 0 ? 0 : queryTokens.Intersect(titleTokens).Count() / (double)queryTokens.Count;
        var score = overlap * 0.72;
        if (track.DurationSeconds is > 0 && candidate.TryGetProperty("duration", out var durationValue) &&
            durationValue.TryGetDouble(out var duration) && duration > 0)
        {
            var relativeDifference = Math.Abs(duration - track.DurationSeconds.Value) / track.DurationSeconds.Value;
            score += Math.Max(0, 1 - relativeDifference) * 0.28;
        }
        else score += overlap * 0.28;
        return score;
    }

    private static HashSet<string> Tokens(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        return Regex.Matches(builder.ToString().ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(token => token.Length > 1)
            .ToHashSet();
    }

    private static async Task ApplyTagsAsync(string ffmpeg, string audioPath, DownloadTrack track, CancellationToken token)
    {
        var coverPath = Path.Combine(Path.GetTempPath(), $"Fetch-cover-{Guid.NewGuid():N}.jpg");
        var taggedPath = Path.Combine(Path.GetDirectoryName(audioPath)!, $".Fetch-{Guid.NewGuid():N}.mp3");
        var hasCover = false;
        try
        {
            if (Uri.TryCreate(track.ArtworkUrl, UriKind.Absolute, out var artworkUri) && artworkUri.Scheme == Uri.UriSchemeHttps)
            {
                var data = await Http.GetByteArrayAsync(artworkUri, token);
                await File.WriteAllBytesAsync(coverPath, data, token);
                hasCover = true;
            }

            var start = new ProcessStartInfo
            {
                FileName = ffmpeg, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var arg in new[] { "-y", "-i", audioPath }) start.ArgumentList.Add(arg);
            if (hasCover) { start.ArgumentList.Add("-i"); start.ArgumentList.Add(coverPath); }
            foreach (var arg in new[] { "-map", "0:a:0" }) start.ArgumentList.Add(arg);
            if (hasCover) { start.ArgumentList.Add("-map"); start.ArgumentList.Add("1:v:0"); }
            foreach (var arg in new[] { "-c:a", "copy" }) start.ArgumentList.Add(arg);
            if (hasCover)
                foreach (var arg in new[] { "-c:v", "mjpeg", "-disposition:v:0", "attached_pic" }) start.ArgumentList.Add(arg);
            foreach (var arg in new[] { "-id3v2_version", "3", "-metadata", $"title={track.Title}", "-metadata", $"artist={track.Artist}" }) start.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(track.Album))
                foreach (var arg in new[] { "-metadata", $"album={track.Album}" }) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(taggedPath);

            using var process = new Process { StartInfo = start };
            process.Start();
            using var killOnCancel = token.Register(() => TryKillProcessTree(process));
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException)
            {
                await EnsureProcessStoppedAsync(process);
                throw;
            }
            _ = await stdout;
            _ = await stderr;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode == 0 && File.Exists(taggedPath)) File.Move(taggedPath, audioPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(coverPath)) File.Delete(coverPath);
            if (File.Exists(taggedPath)) File.Delete(taggedPath);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task EnsureProcessStoppedAsync(Process process)
    {
        TryKillProcessTree(process);
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string MoveToAvailableFileName(string sourcePath, string outputDirectory, string baseName)
    {
        for (var suffix = 1; ; suffix++)
        {
            var fileName = suffix == 1 ? $"{baseName}.mp3" : $"{baseName} ({suffix}).mp3";
            var destination = Path.Combine(outputDirectory, fileName);
            if (File.Exists(destination)) continue;
            try
            {
                if (string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                    File.Move(sourcePath, destination);
                else
                    File.Copy(sourcePath, destination);
                return destination;
            }
            catch (IOException) when (File.Exists(destination)) { }
        }
    }

    private static async Task CleanupWorkDirectoryAsync(string path, Action<string>? statusChanged)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(150 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(150 * (attempt + 1));
            }
            catch (Exception ex)
            {
                statusChanged?.Invoke($"Não foi possível remover arquivos temporários: {ex.Message}");
                return;
            }
        }
        statusChanged?.Invoke("Alguns arquivos temporários ficaram em uma pasta .Fetch-* dentro da pasta de saída.");
    }

    private static void ReportLine(string? line, Action<string>? callback, Action<double>? progressChanged,
        Action<DownloadProgressInfo>? telemetryChanged)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = Regex.Match(line, @"(?<percent>\d{1,3}(?:\.\d+)?)%");
        if (match.Success && double.TryParse(match.Groups["percent"].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var percent))
        {
            progressChanged?.Invoke(percent);
            var speedMatch = Regex.Match(line, @"\bat\s+(?<speed>[\d.,]+\s*(?:KiB/s|MiB/s|GiB/s|kB/s|MB/s|GB/s))", RegexOptions.IgnoreCase);
            var totalMatch = Regex.Match(line, @"\bof\s+(?<total>[\d.,]+\s*(?:KiB|MiB|GiB|kB|MB|GB))", RegexOptions.IgnoreCase);
            var speed = speedMatch.Success ? speedMatch.Groups["speed"].Value : "—";
            var total = totalMatch.Success ? ParseByteSize(totalMatch.Groups["total"].Value) : null;
            long? downloaded = total is > 0 ? (long)(total.Value * percent / 100d) : null;
            telemetryChanged?.Invoke(new DownloadProgressInfo(percent, speed, downloaded, total));
            callback?.Invoke(speed == "—" ? $"Baixando {percent:0}%" : $"Baixando {percent:0}% · {speed}");
        }
    }

    private static long? ParseByteSize(string value)
    {
        var match = Regex.Match(value, @"(?<number>[\d.,]+)\s*(?<unit>KiB|MiB|GiB|kB|MB|GB)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups["number"].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var amount)) return null;
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        var multiplier = unit switch
        {
            "kib" => 1024d, "mib" => 1024d * 1024, "gib" => 1024d * 1024 * 1024,
            "kb" => 1000d, "mb" => 1000d * 1000, "gb" => 1000d * 1000 * 1000,
            _ => 1d
        };
        return (long)(amount * multiplier);
    }

    private static string? FindExecutable(string fileName, string pathName)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && folder is not null; i++, folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return FindOnPath(pathName);
    }

    private static string? FindOnPath(string name)
    {
        var extensions = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory.Trim('"'), name + extension);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Faixa" : cleaned;
    }
}
