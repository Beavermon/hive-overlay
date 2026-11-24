using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HiveStatsOverlay;

internal sealed class HiveApiClient : IDisposable
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(65);
    private static readonly TimeSpan SuccessRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan NoDataTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NickedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(20);
    private const int MaxCacheEntries = 1024;

    private readonly HttpClient http;
    private readonly SemaphoreSlim concurrency = new(3, 3);
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<PlayerStats>> inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly string? cachePath;
    private readonly CancellationToken shutdownToken;
    private bool disposed;

    public HiveApiClient(string? cachePath, CancellationToken shutdownToken)
    {
        this.cachePath = cachePath;
        this.shutdownToken = shutdownToken;
        http = new HttpClient
        {
            BaseAddress = new Uri("https://api.hivebackpack.com/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OnixHiveStatsOverlay/1.2.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        LoadCache();
    }

    public Task<PlayerStats> GetPlayerAsync(string username, HiveGame game, PlayerStatsMode mode, CancellationToken cancellationToken)
    {
        string normalized = Normalize(username);
        string cacheKey = $"hivebackpack-v1:{game.ApiCode()}:{(int)mode}:{normalized}";
        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (cache.TryGetValue(cacheKey, out CacheEntry? cached))
            {
                if (cached.ExpiresAt > now)
                    return Task.FromResult(cached.Stats with { Username = username });
                if (cached.RetainUntil <= now)
                    cache.Remove(cacheKey);
            }

            TrimCache(now);
            if (inFlight.TryGetValue(cacheKey, out Task<PlayerStats>? existing))
                return existing.WaitAsync(cancellationToken);

            Task<PlayerStats> request = FetchAndCacheAsync(username, game, mode, cacheKey, shutdownToken);
            inFlight[cacheKey] = request;
            return request.WaitAsync(cancellationToken);
        }
    }

    private async Task<PlayerStats> FetchAndCacheAsync(string username, HiveGame game, PlayerStatsMode mode, string cacheKey, CancellationToken cancellationToken)
    {
        PlayerStats result;
        TimeSpan ttl;
        try
        {
            result = await FetchPlayerAsync(username, game, mode, cancellationToken);
            ttl = result.Status switch
            {
                PlayerStatsStatus.Ready => SuccessTtl,
                PlayerStatsStatus.NoData => NoDataTtl,
                PlayerStatsStatus.Nicked => NickedTtl,
                PlayerStatsStatus.RateLimited => RateLimitTtl(result),
                _ => FailureTtl
            };
        }
        finally
        {
            lock (gate)
                inFlight.Remove(cacheKey);
        }

        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset retainUntil = result.Status == PlayerStatsStatus.Ready
                ? now + SuccessRetention
                : now + ttl;
            if (result.Status != PlayerStatsStatus.Ready &&
                cache.TryGetValue(cacheKey, out CacheEntry? stale) &&
                stale.Stats.Status == PlayerStatsStatus.Ready &&
                stale.RetainUntil > now)
            {
                ttl = result.Status == PlayerStatsStatus.RateLimited ? RateLimitTtl(result) : FailureTtl;
                result = stale.Stats with { Username = username };
                retainUntil = stale.RetainUntil;
            }

            DateTimeOffset expiresAt = now + ttl;
            if (expiresAt > retainUntil)
                expiresAt = retainUntil;
            cache[cacheKey] = new CacheEntry(result, expiresAt, retainUntil);
            TrimCache(now);
            SaveCache();
        }
        return result;
    }

    private async Task<PlayerStats> FetchPlayerAsync(string username, HiveGame game, PlayerStatsMode mode, CancellationToken cancellationToken)
    {
        if (mode == PlayerStatsMode.AllTime)
            return await FetchAllTimeAsync(username, game, cancellationToken);

        PlayerStats allTime = await GetPlayerAsync(username, game, PlayerStatsMode.AllTime, cancellationToken);
        if (allTime.Status != PlayerStatsStatus.Ready)
            return allTime;
        return await FetchMonthlyAsync(username, game, allTime, cancellationToken);
    }

    private async Task<PlayerStats> FetchAllTimeAsync(string username, HiveGame game, CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            string escaped = Uri.EscapeDataString(username);
            using HttpResponseMessage response = await http.GetAsync($"players/{escaped}/statistics/all", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await ErrorFromResponseAsync(username, response, cancellationToken);

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseAllTime(username, game, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(username, PlayerStatsStatus.Unavailable);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task<PlayerStats> FetchMonthlyAsync(string username, HiveGame game, PlayerStats allTime, CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            string escaped = Uri.EscapeDataString(username);
            using HttpResponseMessage response = await http.GetAsync($"players/{escaped}/statistics/monthly", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await ErrorFromResponseAsync(username, response, cancellationToken);

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return MergeMonthly(allTime, game, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(username, PlayerStatsStatus.Unavailable);
        }
        finally
        {
            concurrency.Release();
        }
    }

    internal PlayerStats ParseAllTime(string username, HiveGame game, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement response = document.RootElement;
            if (response.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
                return Error(username, PlayerStatsStatus.NoData);
            if (!response.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("statistics", out JsonElement statistics) ||
                statistics.ValueKind != JsonValueKind.Object)
                return Error(username, PlayerStatsStatus.InvalidResponse);
            if (!statistics.TryGetProperty(game.ApiCode(), out JsonElement root) || root.ValueKind == JsonValueKind.Null)
                return Error(username, PlayerStatsStatus.NoData);
            if (root.ValueKind != JsonValueKind.Object ||
                (!root.TryGetProperty("xp", out _) && !root.TryGetProperty("played", out _)))
                return Error(username, PlayerStatsStatus.NoData);

            return ReadStats(username, root) with
            {
                Level = WholeLevel(root, "level"),
                Prestige = Int(root, "prestige")
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error(username, PlayerStatsStatus.InvalidResponse);
        }
    }

    internal static PlayerStats MergeMonthly(PlayerStats allTime, HiveGame game, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement response = document.RootElement;
            if (!response.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("statistics", out JsonElement statistics) ||
                statistics.ValueKind != JsonValueKind.Object)
                return Error(allTime.Username, PlayerStatsStatus.InvalidResponse);
            if (!statistics.TryGetProperty(game.ApiCode(), out JsonElement root) || root.ValueKind == JsonValueKind.Null)
                return ClearMatchStats(allTime);
            if (root.ValueKind != JsonValueKind.Object)
                return Error(allTime.Username, PlayerStatsStatus.InvalidResponse);
            PlayerStats monthly = ReadStats(allTime.Username, root);
            return monthly with { Level = allTime.Level, Prestige = allTime.Prestige };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error(allTime.Username, PlayerStatsStatus.InvalidResponse);
        }
    }

    private static async Task<PlayerStats> ErrorFromResponseAsync(string username, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode == 429)
            return Error(username, PlayerStatsStatus.RateLimited, GetRetryAt(response));
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound))
            return Error(username, PlayerStatsStatus.Unavailable);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string message = document.RootElement.TryGetProperty("error", out JsonElement error) &&
                             error.ValueKind == JsonValueKind.Object &&
                             error.TryGetProperty("message", out JsonElement messageValue)
                ? messageValue.GetString() ?? ""
                : "";
            return Error(username,
                message.StartsWith("Invalid identifier", StringComparison.OrdinalIgnoreCase)
                    ? PlayerStatsStatus.Nicked
                    : PlayerStatsStatus.NoData);
        }
        catch (JsonException)
        {
            return Error(username, PlayerStatsStatus.NoData);
        }
    }

    private static PlayerStats ReadStats(string username, JsonElement root) => new()
    {
        Username = username,
        Played = Int(root, "played"),
        Victories = Int(root, "victories"),
        Kills = Int(root, "kills"),
        Deaths = Int(root, "deaths"),
        FinalKills = Int(root, "final_kills"),
        BedsDestroyed = Int(root, "beds_destroyed"),
        Murders = Int(root, "murders"),
        Coins = Int(root, "coins"),
        MurdererEliminations = Int(root, "murderer_eliminations"),
        HiderKills = Int(root, "hider_kills"),
        Goals = Int(root, "goals"),
        FlagsCaptured = Int(root, "flags_captured"),
        Status = PlayerStatsStatus.Ready
    };

    private static PlayerStats ClearMatchStats(PlayerStats stats) => stats with
    {
        Played = 0,
        Victories = 0,
        Kills = 0,
        Deaths = 0,
        FinalKills = 0,
        BedsDestroyed = 0,
        Murders = 0,
        Coins = 0,
        MurdererEliminations = 0,
        HiderKills = 0,
        Goals = 0,
        FlagsCaptured = 0
    };

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static int WholeLevel(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
            ? (int)Math.Floor(parsed)
            : 0;

    private static string Normalize(string username) => username.Trim().ToUpperInvariant();

    private void TrimCache(DateTimeOffset now)
    {
        foreach (string key in cache.Where(entry => entry.Value.RetainUntil <= now).Select(entry => entry.Key).ToArray())
            cache.Remove(key);

        int excess = cache.Count - MaxCacheEntries;
        if (excess <= 0)
            return;
        foreach (string key in cache.OrderBy(entry => entry.Value.RetainUntil).Take(excess).Select(entry => entry.Key).ToArray())
            cache.Remove(key);
    }

    private void LoadCache()
    {
        if (cachePath is null || !File.Exists(cachePath))
            return;

        try
        {
            Dictionary<string, CacheEntry>? stored = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cachePath));
            if (stored is null)
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string key, CacheEntry entry) in stored)
            {
                if (entry.RetainUntil > now)
                    cache[key] = entry;
            }
            TrimCache(now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveCache()
    {
        if (cachePath is null)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string temporaryPath = cachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cache));
            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static DateTimeOffset GetRetryAt(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date)
            return date;
        if (retry?.Delta is { } delta)
            return DateTimeOffset.UtcNow + delta;
        return DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
    }

    private static TimeSpan RateLimitTtl(PlayerStats stats)
    {
        TimeSpan remaining = (stats.RetryAt ?? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)) - DateTimeOffset.UtcNow;
        return remaining > FailureTtl ? remaining : FailureTtl;
    }

    private static PlayerStats Error(string username, PlayerStatsStatus status, DateTimeOffset? retryAt = null) => new()
    {
        Username = username,
        Status = status,
        RetryAt = retryAt
    };

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        Task<PlayerStats>[] inflight;
        lock (gate)
        {
            inflight = inFlight.Values.ToArray();
        }

        List<Task> pending = new(inflight.Length);
        pending.AddRange(inflight);

        if (pending.Count > 0)
        {
            try
            {
                Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException aggregate) when (aggregate.InnerExceptions.All(static ex => ex is OperationCanceledException))
            {
            }
        }

        http.Dispose();
    }
}
