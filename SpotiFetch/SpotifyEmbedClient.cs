using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotiFetch;

/// <summary>
/// Reads public Spotify embed metadata. This intentionally avoids Spotify's
/// Web API, whose Development Mode no longer exposes arbitrary playlist items.
/// Spotify may change this public page format at any time.
/// </summary>
public sealed class SpotifyEmbedClient
{
    public event Action<string>? Diagnostic;
    private const string PlaylistQueryHash = "91d4c2bc3e0cd1bc672281c4f1f59f43ff55ba726ca04a45810d99bd091f3f0e";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly Regex LinkPattern = new(
        @"(?:open\.spotify\.com/(?:intl-[^/]+/)?|spotify:)(?<kind>playlist|track|album):?/?(?<id>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<(string Name, List<DownloadTrack> Tracks)> ResolveAsync(string input, CancellationToken token)
    {
        input = input.Replace("\u200B", "", StringComparison.Ordinal)
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\u2060", "", StringComparison.Ordinal)
            .Trim();
        var match = LinkPattern.Match(input);
        if (!match.Success)
            throw new InvalidOperationException("O link do Spotify não parece ser de faixa, álbum ou playlist.");

        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        var url = $"https://open.spotify.com/embed/{kind}/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var response = await SendWithFriendlyNetworkErrorsAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"O Spotify respondeu HTTP {(int)response.StatusCode} ao abrir o link.");
        var html = await response.Content.ReadAsStringAsync(token);

        var json = ExtractNextData(html);
        if (json is null)
            throw new InvalidOperationException("Não encontrei os metadados públicos no embed do Spotify. Tente novamente mais tarde.");

        using var document = JsonDocument.Parse(json);
        var tracks = new List<DownloadTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTracks(document.RootElement, tracks, seen);

        if (kind == "playlist")
        {
            var tokenMatch = Regex.Match(html, "\\\"accessToken\\\"\\s*:\\s*\\\"(?<token>[^\\\"]+)\\\"", RegexOptions.IgnoreCase);
            if (!tokenMatch.Success)
                throw new InvalidOperationException("O Spotify não forneceu acesso de leitura à paginação desta playlist. Confirme se ela é pública e tente novamente.");
            tracks = await FetchAllPlaylistTracksAsync(id, tokenMatch.Groups["token"].Value, token);
        }

        if (kind == "track" && tracks.Count == 0)
            CollectTrackFromHtmlFallback(html, tracks);
        if (tracks.Count == 0)
            throw new InvalidOperationException("O embed não retornou faixas. Confirme se a playlist está pública ou se o Spotify alterou o formato da página.");

        for (var i = 0; i < tracks.Count; i++)
        {
            tracks[i] = CopyWithNumber(tracks[i], i + 1);
        }

        var name = FindCollectionName(document.RootElement) ?? tracks[0].Title;
        return (WebUtility.HtmlDecode(name), tracks);
    }

    private static async Task<HttpResponseMessage> SendWithFriendlyNetworkErrorsAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token); }
        catch (HttpRequestException ex) when (!token.IsCancellationRequested)
        {
            if (ex.InnerException is System.Net.Sockets.SocketException socket &&
                socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain)
                throw new InvalidOperationException("Não foi possível localizar o Spotify. Verifique sua conexão ou DNS e tente novamente.", ex);
            throw new InvalidOperationException($"Não foi possível conectar ao Spotify: {ex.Message}", ex);
        }
    }

    private async Task<List<DownloadTrack>> FetchAllPlaylistTracksAsync(string playlistId, string accessToken, CancellationToken token)
    {
        var tracks = new List<DownloadTrack>();
        var offset = 0;
        int? totalCount = null;
        do
        {
            token.ThrowIfCancellationRequested();
            using var response = await FetchPlaylistPageAsync(playlistId, offset, accessToken, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"O Spotify bloqueou a página {offset / 100 + 1} (HTTP {(int)response.StatusCode}); a playlist não foi carregada por completo.");

            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (page.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new InvalidOperationException($"O Spotify recusou a paginação: {errors[0].ToString()}");
            if (!page.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("playlistV2", out var playlist) ||
                !playlist.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("O Spotify retornou uma página de playlist em formato inesperado.");

            if (content.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var parsedTotal))
                totalCount = parsedTotal;
            if (totalCount is null)
                throw new InvalidOperationException("O Spotify não informou o total de faixas da playlist.");

            var pageCount = items.GetArrayLength();
            foreach (var wrapper in items.EnumerateArray())
            {
                if (!wrapper.TryGetProperty("itemV2", out var itemV2) ||
                    !itemV2.TryGetProperty("data", out var item) || item.ValueKind != JsonValueKind.Object)
                    continue;
                var type = ReadString(item, "__typename") ?? "";
                if (type.Length > 0 && !type.Equals("Track", StringComparison.OrdinalIgnoreCase)) continue;
                var title = ReadString(item, "name");
                var artist = ReadPathfinderArtists(item);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
                tracks.Add(new DownloadTrack
                {
                    Title = WebUtility.HtmlDecode(title).Trim(),
                    Artist = WebUtility.HtmlDecode(artist).Trim(),
                    Album = ReadPathfinderAlbum(item),
                    ArtworkUrl = ReadPathfinderArtwork(item),
                    DurationSeconds = ReadPathfinderDuration(item)
                });
            }

            if (pageCount == 0 && offset < totalCount.Value)
                throw new InvalidOperationException($"A paginação parou na faixa {offset} de {totalCount.Value}.");
            offset += pageCount;
            Diagnostic?.Invoke($"Playlist: {Math.Min(offset, totalCount.Value)} de {totalCount.Value} faixas lidas.");
        } while (offset < totalCount.Value);

        if (tracks.Count != totalCount)
            throw new InvalidOperationException($"O Spotify informa {totalCount} faixas, mas só {tracks.Count} puderam ser reconhecidas. A lista parcial não será tratada como completa.");
        Diagnostic?.Invoke($"Playlist lida por completo: {tracks.Count} faixas.");
        return tracks;
    }

    private static async Task<HttpResponseMessage> FetchPlaylistPageAsync(
        string playlistId, int offset, string accessToken, CancellationToken token)
    {
        var variables = JsonSerializer.Serialize(new { uri = $"spotify:playlist:{playlistId}", offset, limit = 100 });
        var extensions = JsonSerializer.Serialize(new { persistedQuery = new { version = 1, sha256Hash = PlaylistQueryHash } });
        var url = "https://api-partner.spotify.com/pathfinder/v1/query?operationName=fetchPlaylist" +
                  $"&variables={Uri.EscapeDataString(variables)}&extensions={Uri.EscapeDataString(extensions)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Referrer = new Uri("https://open.spotify.com/");
        return await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
    }

    private static string ReadPathfinderArtists(JsonElement track)
    {
        if (!track.TryGetProperty("artists", out var artists) ||
            !artists.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return "";
        return string.Join(", ", items.EnumerateArray()
            .Select(item => item.TryGetProperty("profile", out var profile) ? ReadString(profile, "name") : null)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadPathfinderAlbum(JsonElement track) =>
        track.TryGetProperty("albumOfTrack", out var album) ? ReadString(album, "name") ?? "" : "";

    private static string? ReadPathfinderArtwork(JsonElement track)
    {
        if (!track.TryGetProperty("albumOfTrack", out var album) ||
            !album.TryGetProperty("coverArt", out var cover) ||
            !cover.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return null;
        return sources.EnumerateArray().Select(source => ReadString(source, "url"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    private static double? ReadPathfinderDuration(JsonElement track) =>
        track.TryGetProperty("trackDuration", out var duration) &&
        duration.TryGetProperty("totalMilliseconds", out var milliseconds) &&
        milliseconds.TryGetDouble(out var value) && value > 0 ? value / 1000d : null;

    private static string? ExtractNextData(string html)
    {
        var match = Regex.Match(html, "<script[^>]*id=[\"']__NEXT_DATA__[\"'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) return WebUtility.HtmlDecode(match.Groups[1].Value);

        match = Regex.Match(html, "__NEXT_DATA__\\s*[:=]\\s*(\\{.*?\\})\\s*;", RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static void CollectTracks(JsonElement node, List<DownloadTrack> tracks, HashSet<string> seen)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var uri = ReadString(node, "uri") ?? "";
            var type = ReadString(node, "type") ?? "";
            if (uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase) || type.Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                var title = ReadString(node, "name") ?? ReadString(node, "title");
                var artist = ReadArtists(node);
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                {
                    var key = $"{title}|{artist}";
                    if (seen.Add(key))
                        tracks.Add(new DownloadTrack
                        {
                            Title = title.Trim(), Artist = artist.Trim(), Album = ReadAlbum(node),
                            ArtworkUrl = ReadArtwork(node), DurationSeconds = ReadDuration(node)
                        });
                }
            }

            foreach (var property in node.EnumerateObject())
                CollectTracks(property.Value, tracks, seen);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) CollectTracks(child, tracks, seen);
        }
    }

    private static string? ReadString(JsonElement node, string property)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string ReadArtists(JsonElement track)
    {
        foreach (var key in new[] { "artists", "artist", "subtitle" })
        {
            if (!track.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Array)
            {
                var names = value.EnumerateArray().Select(a =>
                    a.ValueKind == JsonValueKind.String ? a.GetString() : ReadString(a, "name"))
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                var joined = string.Join(", ", names);
                if (joined.Length > 0) return joined;
            }
            if (value.ValueKind == JsonValueKind.Object)
                return ReadString(value, "name") ?? "";
        }
        return "";
    }

    private static string ReadAlbum(JsonElement track)
    {
        foreach (var key in new[] { "album", "albumOfTrack" })
            if (track.TryGetProperty(key, out var album) && album.ValueKind == JsonValueKind.Object)
                return ReadString(album, "name") ?? "";
        return "";
    }

    private static string? ReadArtwork(JsonElement track)
    {
        foreach (var albumKey in new[] { "album", "albumOfTrack" })
        {
            if (!track.TryGetProperty(albumKey, out var album) || album.ValueKind != JsonValueKind.Object) continue;
            if (album.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                foreach (var image in images.EnumerateArray())
                    if (ReadString(image, "url") is { Length: > 0 } url) return url;
            if (album.TryGetProperty("coverArt", out var cover) && cover.ValueKind == JsonValueKind.Object &&
                cover.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                foreach (var source in sources.EnumerateArray())
                    if (ReadString(source, "url") is { Length: > 0 } coverUrl) return coverUrl;
        }
        if (track.TryGetProperty("coverArt", out var directCover) && directCover.ValueKind == JsonValueKind.Object &&
            directCover.TryGetProperty("sources", out var directSources) && directSources.ValueKind == JsonValueKind.Array)
            foreach (var source in directSources.EnumerateArray())
                if (ReadString(source, "url") is { Length: > 0 } url) return url;
        return null;
    }

    private static double? ReadDuration(JsonElement track)
    {
        foreach (var key in new[] { "duration_ms", "durationMs" })
            if (track.TryGetProperty(key, out var value) && value.TryGetDouble(out var milliseconds) && milliseconds > 0)
                return milliseconds / 1000d;
        if (track.TryGetProperty("duration", out var duration))
        {
            if (duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) && seconds > 0)
                return seconds;
            if (duration.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "totalMilliseconds", "milliseconds" })
                    if (duration.TryGetProperty(key, out var value) && value.TryGetDouble(out var milliseconds) && milliseconds > 0)
                        return milliseconds / 1000d;
        }
        return null;
    }

    private static string? FindCollectionName(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            {
                var nested = FindCollectionName(child);
                if (nested is not null) return nested;
            }
            return null;
        }
        if (node.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in node.EnumerateObject())
        {
            if (property.NameEquals("entity") && property.Value.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(property.Value, "name") ?? ReadString(property.Value, "title");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            var nested = FindCollectionName(property.Value);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void CollectTrackFromHtmlFallback(string html, List<DownloadTrack> tracks)
    {
        var title = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;
        title = WebUtility.HtmlDecode(Regex.Replace(title, "\\s*\\|\\s*Spotify\\s*$", "", RegexOptions.IgnoreCase)).Trim();
        var parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            tracks.Add(new DownloadTrack { Title = parts[0], Artist = parts[1] });
    }

    private static DownloadTrack CopyWithNumber(DownloadTrack track, int number) => new()
    {
        Number = number,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        ArtworkUrl = track.ArtworkUrl,
        SourceUrl = track.SourceUrl,
        DurationSeconds = track.DurationSeconds
    };
}
