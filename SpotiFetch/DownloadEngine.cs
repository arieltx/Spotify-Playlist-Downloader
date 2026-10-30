using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace LaurusDownloader;

public sealed class DownloadEngine
{
    private static readonly HttpClient Http = new();

    public async Task DownloadAsync(
        DownloadTrack track,
        string outputDirectory,
        string bitrate,
        CancellationToken cancellationToken,
        Action<string>? statusChanged = null,
        Action<double>? progressChanged = null)
    {
        var ytdlp = FindExecutable("yt-dlp.exe", "yt-dlp");
        if (ytdlp is null)
            throw new FileNotFoundException("yt-dlp.exe não foi encontrado. Coloque-o junto do aplicativo ou adicione-o ao PATH.");

        var ffmpeg = FindExecutable("ffmpeg.exe", "ffmpeg");
        if (ffmpeg is null)
            throw new FileNotFoundException("ffmpeg.exe não foi encontrado. Coloque-o junto do aplicativo ou adicione-o ao PATH.");

        Directory.CreateDirectory(outputDirectory);
        var safeTitle = SafeFilePart(track.Title);
        var fileName = string.IsNullOrWhiteSpace(track.Artist)
            ? safeTitle
            : $"{safeTitle} - {SafeFilePart(track.Artist)}";
        var workDirectory = Path.Combine(outputDirectory, $".laurus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = !string.IsNullOrWhiteSpace(track.SourceUrl)
                ? track.SourceUrl!
                : await FindBestVideoAsync(ytdlp, track, cancellationToken, statusChanged);
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo
            {
                FileName = ytdlp,
                WorkingDirectory = workDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in new[]
            {
                "--no-playlist", "--no-continue", "--extract-audio", "--audio-format", "mp3",
                "--audio-quality", $"{bitrate}K", "--ffmpeg-location", ffmpeg,
                "--embed-metadata", "--embed-thumbnail", "--newline", "--no-warnings",
                "--output", Path.Combine(workDirectory, fileName + ".%(ext)s"), input
            }) start.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => ReportLine(e.Data, statusChanged, progressChanged);
            process.ErrorDataReceived += (_, e) => ReportLine(e.Data, statusChanged, progressChanged);
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
            if (process.ExitCode != 0)
                throw new InvalidOperationException("yt-dlp terminou com erro. Verifique a aba Atividade para detalhes.");

            var audioPath = Path.Combine(workDirectory, fileName + ".mp3");
            if (!File.Exists(audioPath))
                throw new InvalidOperationException("O download terminou, mas o arquivo MP3 não foi gerado.");
            statusChanged?.Invoke("Aplicando tags e capa");
            await ApplyTagsAsync(ffmpeg, audioPath, track, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            MoveToAvailableFileName(audioPath, outputDirectory, fileName);
            progressChanged?.Invoke(100);
        }
        finally
        {
            await CleanupWorkDirectoryAsync(workDirectory, statusChanged);
        }
    }

    private static async Task<string> FindBestVideoAsync(
        string ytdlp, DownloadTrack track, CancellationToken token, Action<string>? statusChanged)
    {
        statusChanged?.Invoke("Procurando a melhor correspondência de áudio");
        var query = string.Join(" ", new[] { track.Title, track.Artist }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var start = new ProcessStartInfo
        {
            FileName = ytdlp, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "--dump-single-json", "--flat-playlist", "--skip-download", "--no-warnings", "--quiet", $"ytsearch5:{query}" })
            start.ArgumentList.Add(arg);

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
        _ = await stderr;
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
        return $"ytsearch1:{query}";
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
        var coverPath = Path.Combine(Path.GetTempPath(), $"laurus-cover-{Guid.NewGuid():N}.jpg");
        var taggedPath = Path.Combine(Path.GetDirectoryName(audioPath)!, $".laurus-{Guid.NewGuid():N}.mp3");
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

    private static void MoveToAvailableFileName(string sourcePath, string outputDirectory, string baseName)
    {
        for (var suffix = 1; ; suffix++)
        {
            var fileName = suffix == 1 ? $"{baseName}.mp3" : $"{baseName} ({suffix}).mp3";
            var destination = Path.Combine(outputDirectory, fileName);
            if (File.Exists(destination)) continue;
            try
            {
                File.Move(sourcePath, destination);
                return;
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
        statusChanged?.Invoke("Alguns arquivos temporários ficaram em uma pasta .laurus-* dentro da pasta de saída.");
    }

    private static void ReportLine(string? line, Action<string>? callback, Action<double>? progressChanged)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = Regex.Match(line, @"(?<percent>\d{1,3}(?:\.\d+)?)%");
        if (match.Success && double.TryParse(match.Groups["percent"].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var percent))
        {
            progressChanged?.Invoke(percent);
            callback?.Invoke($"Baixando {percent:0}%");
        }
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
