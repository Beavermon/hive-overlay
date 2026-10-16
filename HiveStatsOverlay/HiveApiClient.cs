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
    private readonly Dictionary<HiveGame, HiveLevelCalculator> levels = new()
    {
        [HiveGame.BedWars] = new(100),
        [HiveGame.SkyWars] = new(100),
        [HiveGame.MurderMystery] = new(100),
        [HiveGame.HideAndSeek] = new(100),
        [HiveGame.SurvivalGames] = new(50),
        [HiveGame.Bridge] = new(20),
        [HiveGame.CaptureTheFlag] = new(50)
    };
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<PlayerStats>> inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly string? cachePath;
    private readonly CancellationToken shutdownToken;
    private Task? initializationTask;
    private bool disposed;

    public HiveApiClient(string? cachePath, CancellationToken shutdownToken)
    {
        this.cachePath = cachePath;
        this.shutdownToken = shutdownToken;
        http = new HttpClient
        {
            BaseAddress = new Uri("https://api.playhive.com/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OnixHiveStatsOverlay/1.1.2");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        LoadCache();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            initializationTask ??= InitializeCoreAsync(shutdownToken);
            return cancellationToken.CanBeCanceled
                ? initializationTask.WaitAsync(cancellationToken)
                : initializationTask;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(levels.Select(pair => LoadMetadataAsync(pair.Key, pair.Value, cancellationToken)));
    }

    private async Task LoadMetadataAsync(HiveGame game, HiveLevelCalculator calculator, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync($"v0/game/meta/{game.ApiCode()}", cancellationToken);
            if (response.IsSuccessStatusCode)
                calculator.TryUpdateFromMetadata(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { }
    }

    public Task<PlayerStats> GetPlayerAsync(string username, HiveGame game, PlayerStatsMode mode, CancellationToken cancellationToken)
    {
        string normalized = Normalize(username);
        string cacheKey = $"{game.ApiCode()}-v3:{(int)mode}:{normalized}";
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
            if (result.Status != PlayerStatsStatus.Ready &&
                cache.TryGetValue(cacheKey, out CacheEntry? stale) &&
                stale.Stats.Status == PlayerStatsStatus.Ready &&
                stale.RetainUntil > now)
            {
                ttl = result.Status == PlayerStatsStatus.RateLimited ? RateLimitTtl(result) : FailureTtl;
                result = stale.Stats with { Username = username };
            }

            DateTimeOffset retainUntil = result.Status == PlayerStatsStatus.Ready ? now + SuccessRetention : now + ttl;
            cache[cacheKey] = new CacheEntry(result, now + ttl, retainUntil);
            TrimCache(now);
            SaveCache();
        }
        return result;
    }

    private async Task<PlayerStats> FetchPlayerAsync(string username, HiveGame game, PlayerStatsMode mode, CancellationToken cancellationToken)
    {
        await InitializeAsync();
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
            using HttpResponseMessage response = await http.GetAsync($"v0/game/all/{game.ApiCode()}/{escaped}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return await ResolveMissingPlayerAsync(username, escaped, cancellationToken);
            if ((int)response.StatusCode == 429)
                return Error(username, PlayerStatsStatus.RateLimited, GetRetryAt(response));
            if (!response.IsSuccessStatusCode)
                return Error(username, PlayerStatsStatus.Unavailable);

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

    private async Task<PlayerStats> ResolveMissingPlayerAsync(string username, string escaped, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync($"v0/game/all/main/{escaped}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Error(username, PlayerStatsStatus.Nicked);
        if ((int)response.StatusCode == 429)
            return Error(username, PlayerStatsStatus.RateLimited, GetRetryAt(response));
        return response.IsSuccessStatusCode
            ? Error(username, PlayerStatsStatus.NoData)
            : Error(username, PlayerStatsStatus.Unavailable);
    }

    private async Task<PlayerStats> FetchMonthlyAsync(string username, HiveGame game, PlayerStats allTime, CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            string escaped = Uri.EscapeDataString(username);
            using HttpResponseMessage response = await http.GetAsync($"v1/player/game/{game.ApiCode()}/monthly/{escaped}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ClearMatchStats(allTime);
            if ((int)response.StatusCode == 429)
                return Error(username, PlayerStatsStatus.RateLimited, GetRetryAt(response));
            if (!response.IsSuccessStatusCode)
                return Error(username, PlayerStatsStatus.Unavailable);

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return MergeMonthly(allTime, json);
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
            JsonElement root = response.TryGetProperty(game.ApiCode(), out JsonElement gameStats) ? gameStats : response;
            if (root.ValueKind != JsonValueKind.Object ||
                (!root.TryGetProperty("xp", out _) && !root.TryGetProperty("played", out _)))
                return Error(username, PlayerStatsStatus.NoData);

            return ReadStats(username, root) with
            {
                Level = levels[game].GetLevel(Int(root, "xp")),
                Prestige = Int(root, "prestige")
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error(username, PlayerStatsStatus.InvalidResponse);
        }
    }

    internal static PlayerStats MergeMonthly(PlayerStats allTime, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            PlayerStats monthly = ReadStats(allTime.Username, root);
            return monthly with { Level = allTime.Level, Prestige = allTime.Prestige };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Error(allTime.Username, PlayerStatsStatus.InvalidResponse);
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

        Task? init;
        Task<PlayerStats>[] inflight;
        lock (gate)
        {
            init = initializationTask;
            inflight = inFlight.Values.ToArray();
        }

        List<Task> pending = new(inflight.Length + 1);
        pending.AddRange(inflight);
        if (init is not null)
            pending.Add(init);

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
        concurrency.Dispose();
    }
}
