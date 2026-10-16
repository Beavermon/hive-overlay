using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OnixRuntime.Api;
using OnixRuntime.Api.Entities;
using OnixRuntime.Api.Events.Session;
using OnixRuntime.Api.UI;

namespace HiveStatsOverlay;

internal sealed partial class HiveSessionController : IDisposable
{
    private static readonly TimeSpan RosterSettleDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan FailedStatsRetryInterval = TimeSpan.FromSeconds(21);
    private static readonly TimeSpan RosterAbsenceGrace = TimeSpan.FromSeconds(3);

    private readonly HiveApiClient api;
    private readonly CancellationToken pluginEjectionToken;
    private readonly ObservedTaskCollection backgroundTasks = new();
    private readonly ConcurrentDictionary<string, OverlayRow> rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> missingPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> eliminatedPlayers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource sessionCancellation;
    private DateTimeOffset nextRosterPoll = DateTimeOffset.MinValue;
    private DateTimeOffset rosterCaptureReadyAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextFailedStatsRetry = DateTimeOffset.MinValue;
    private int requestedStatsMode;
    private int statsModeRefreshRequested;
    private int currentGame;
    private int dimensionTransitionGame;
    private long lobbyGeneration;
    private string gameCode = "";
    private string localUsername = "";
    private volatile bool isHive;
    private volatile bool isInMatch;
    private bool matchStarted;
    private bool connectionPollingEnabled = true;
    private bool waitingForNextGame;
    private string completedServer = "";
    private bool disposed;

    public HiveSessionController(HiveApiClient api, CancellationToken pluginEjectionToken)
    {
        this.api = api;
        this.pluginEjectionToken = pluginEjectionToken;
        sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginEjectionToken);
    }

    public bool IsInMatch => isInMatch;
    public HiveGame CurrentGame => (HiveGame)Volatile.Read(ref currentGame);
    public string LocalUsername => Volatile.Read(ref localUsername);

    public IReadOnlyList<OverlayRow> Snapshot() => rows.Values.ToArray();

    public void Start()
    {
        Onix.Events.Session.Tick += OnTick;
        Onix.Events.Session.SessionJoined += OnSessionJoined;
        Onix.Events.Session.SessionLeft += OnSessionLeft;
        Onix.Events.Session.DimensionChanged += OnDimensionChanged;
        Onix.Events.Session.Title.Title += OnTitleChanged;
        Onix.Events.Session.Title.Subtitle += OnSubtitleChanged;
        Onix.Events.Session.Title.ActionBar += OnActionBarChanged;
        Onix.Events.Session.Chat.CommandResponse += OnCommandResponse;
        Onix.Events.Session.Chat.Receive += OnChatReceive;
        Onix.Events.Session.Chat.SendCommand += OnSendCommand;
        backgroundTasks.Track(InitializeBackgroundAsync());
        ResetLobby();
    }

    private async Task InitializeBackgroundAsync()
    {
        try
        {
            await api.InitializeAsync(pluginEjectionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pluginEjectionToken.IsCancellationRequested) { }
    }

    private void OnSessionJoined()
    {
        waitingForNextGame = false;
        completedServer = "";
        Volatile.Write(ref dimensionTransitionGame, (int)HiveGame.Unknown);
        ResetLobby();
    }

    private void OnSessionLeft()
    {
        CancelSessionWork();
        ClearRoster();
        gameCode = "";
        Volatile.Write(ref currentGame, (int)HiveGame.Unknown);
        Volatile.Write(ref dimensionTransitionGame, (int)HiveGame.Unknown);
        localUsername = "";
        isHive = false;
        isInMatch = false;
        matchStarted = false;
        connectionPollingEnabled = false;
        waitingForNextGame = false;
        completedServer = "";
    }

    private void OnDimensionChanged(OnixRuntime.Api.World.Dimension _)
    {
        HiveGame previousGame = CurrentGame;
        bool leftWaitingLobby = isInMatch && !matchStarted && previousGame != HiveGame.Unknown;
        Volatile.Write(ref dimensionTransitionGame, leftWaitingLobby ? (int)previousGame : (int)HiveGame.Unknown);
        ResetLobby();
    }

    private void ResetLobby()
    {
        ReplaceSessionCancellation();
        Interlocked.Increment(ref lobbyGeneration);
        ClearRoster();
        gameCode = "";
        Volatile.Write(ref currentGame, (int)HiveGame.Unknown);
        isInMatch = false;
        matchStarted = false;
        connectionPollingEnabled = true;
        nextRosterPoll = DateTimeOffset.MinValue;
        rosterCaptureReadyAt = DateTimeOffset.MinValue;
        nextFailedStatsRetry = DateTimeOffset.MinValue;
    }

    private void OnTick()
    {
        if (Interlocked.Exchange(ref statsModeRefreshRequested, 0) != 0)
            RefreshStatsMode();

        bool wasHive = isHive;
        isHive = IsHiveConnection();
        if (!isHive)
        {
            if (wasHive || isInMatch || rows.Count != 0 || eliminatedPlayers.Count != 0)
                ClearRoster();
            gameCode = "";
            Volatile.Write(ref currentGame, (int)HiveGame.Unknown);
            Volatile.Write(ref dimensionTransitionGame, (int)HiveGame.Unknown);
            localUsername = "";
            isInMatch = false;
            matchStarted = false;
            connectionPollingEnabled = false;
            waitingForNextGame = false;
            completedServer = "";
            return;
        }

        if (!wasHive)
            connectionPollingEnabled = true;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (connectionPollingEnabled)
        {
            connectionPollingEnabled = false;
            Onix.Game.ExecuteCommand("/connection");
        }

        if (UpdateHubStateFromInventory())
            return;
        UpdateMatchStartFromPlayer();

        if (now < nextRosterPoll)
            return;

        nextRosterPoll = now + TimeSpan.FromMilliseconds(250);
        RefreshRoster();
    }

    private void RefreshRoster()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HiveGame game = CurrentGame;
        if (game == HiveGame.Unknown || now < rosterCaptureReadyAt || Onix.World is not { } world)
            return;

        bool retryFailedStats = now >= nextFailedStatsRetry;
        if (retryFailedStats)
            nextFailedStatsRetry = now + FailedStatsRetryInterval;

        var connected = world.ServerConnectedPlayers;
        HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);

        foreach (var player in connected)
        {
            string username = CleanUsername(player.Username);
            if (username.Length == 0)
                continue;
            current.Add(username);
        }

        if (Onix.LocalPlayer is { } localPlayer)
        {
            string localUsername = CleanUsername(localPlayer.Username);
            if (localUsername.Length != 0)
            {
                Volatile.Write(ref this.localUsername, localUsername);
                if (matchStarted && game.RemovesEliminatedPlayers() && localPlayer.GameMode == GameMode.Spectator)
                    MarkEliminated(localUsername);
                else
                    current.Add(localUsername);
            }
        }

        current.RemoveWhere(eliminatedPlayers.ContainsKey);

        if (!matchStarted || !game.RemovesEliminatedPlayers())
        {
            missingPlayers.Clear();
            foreach (string oldName in rows.Keys)
            {
                if (!current.Contains(oldName))
                    rows.TryRemove(oldName, out _);
            }
        }
        else
            ReconcileRosterAbsences(current, now);

        long generation = Volatile.Read(ref lobbyGeneration);
        PlayerStatsMode mode = (PlayerStatsMode)Volatile.Read(ref requestedStatsMode);
        foreach (string username in current)
        {
            if (eliminatedPlayers.ContainsKey(username))
                continue;
            if (rows.TryAdd(username, new OverlayRow(username, PlayerStats.Loading(username))))
                backgroundTasks.Track(FetchForRosterAsync(username, game, mode, generation, sessionCancellation.Token));
            else if (retryFailedStats && rows.TryGetValue(username, out OverlayRow? row) && IsRetryable(row.Stats.Status))
            {
                OverlayRow loading = new(username, PlayerStats.Loading(username));
                if (rows.TryUpdate(username, loading, row))
                    backgroundTasks.Track(FetchForRosterAsync(username, game, mode, generation, sessionCancellation.Token));
            }
        }
    }

    private void ReconcileRosterAbsences(IReadOnlySet<string> current, DateTimeOffset now)
    {
        foreach (string username in current)
            missingPlayers.TryRemove(username, out _);

        string[] absent = rows.Keys.Where(username => !current.Contains(username)).ToArray();
        bool credible = current.Count > 0 && (absent.Length <= 4 || current.Count * 2 >= rows.Count);
        if (!credible)
        {
            missingPlayers.Clear();
            return;
        }

        foreach (string username in absent)
        {
            DateTimeOffset missingSince = missingPlayers.GetOrAdd(username, now);
            if (now - missingSince >= RosterAbsenceGrace)
                MarkEliminated(username);
        }
    }

    private void MarkEliminated(string username)
    {
        eliminatedPlayers[username] = 0;
        missingPlayers.TryRemove(username, out _);
        rows.TryRemove(username, out _);
    }

    private static bool IsRetryable(PlayerStatsStatus status) =>
        status is PlayerStatsStatus.RateLimited or PlayerStatsStatus.Unavailable or PlayerStatsStatus.InvalidResponse;

    private async Task FetchForRosterAsync(string username, HiveGame game, PlayerStatsMode mode, long generation, CancellationToken cancellationToken)
    {
        try
        {
            PlayerStats stats = await api.GetPlayerAsync(username, game, mode, cancellationToken);
            if (generation != Volatile.Read(ref lobbyGeneration) || game != CurrentGame || cancellationToken.IsCancellationRequested)
                return;

            if (!eliminatedPlayers.ContainsKey(username) && rows.TryGetValue(username, out OverlayRow? current))
                rows.TryUpdate(username, new OverlayRow(username, stats), current);
        }
        catch (OperationCanceledException) { }
    }

    private bool OnCommandResponse(CommandResponseMessage[] messages, int _, int __)
    {
        foreach (CommandResponseMessage response in messages)
        {
            if (TryParseConnection(response.Message, out string parsed) ||
                response.MessageParams.Any(value =>
                    TryParseConnection(value, out parsed) || TryParseHiveServerToken(value, out parsed)))
            {
                ApplyConnectionServer(parsed);
                return true;
            }
        }

        return false;
    }

    private bool OnChatReceive(string message, string _, string __, ChatMessageType ___)
    {
        if (!isHive)
            return false;

        string cleaned = StripFormatting(message);
        ObserveHiveAutoQueueSignals(cleaned);
        ObserveMatchStartSignal(cleaned);
        ObserveRosterRemovalSignal(cleaned);
        ObserveMatchEndSignal(cleaned);
        bool isConnectionResponse = cleaned.Contains("You are connected to", StringComparison.OrdinalIgnoreCase);
        if (!isConnectionResponse)
            return false;

        if (TryParseConnection(cleaned, out string parsed))
            ApplyConnectionServer(parsed);

        return true;
    }

    private bool OnTitleChanged(string title)
    {
        ObserveMatchStartSignal(title);
        ObserveRosterRemovalSignal(title);
        ObserveMatchEndSignal(title);
        return false;
    }

    private bool OnSubtitleChanged(string subtitle)
    {
        ObserveMatchStartSignal(subtitle);
        ObserveRosterRemovalSignal(subtitle);
        ObserveMatchEndSignal(subtitle);
        return false;
    }

    private bool OnActionBarChanged(string actionBar)
    {
        ObserveMatchStartSignal(actionBar);
        ObserveRosterRemovalSignal(actionBar);
        ObserveMatchEndSignal(actionBar);
        return false;
    }

    private void ObserveMatchStartSignal(string value)
    {
        if (!isInMatch || matchStarted)
            return;

        string normalized = StripFormatting(value).Trim();
        if (normalized.Equals("GO!", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("GO", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("GAME HAS STARTED", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("GAME STARTED", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("MATCH HAS STARTED", StringComparison.OrdinalIgnoreCase))
            MarkMatchStarted();
    }

    private void ObserveHiveAutoQueueSignals(string message)
    {
        if (!isHive)
            return;

        if (message.Contains("Voting has ended!", StringComparison.OrdinalIgnoreCase))
        {
            BeginNextGameDetection();
            return;
        }

        string localName = Onix.LocalPlayer is { } player ? CleanUsername(player.Username) : "";
        if (localName.Length != 0 &&
            message.Contains(" joined.", StringComparison.OrdinalIgnoreCase) &&
            message.Contains(localName, StringComparison.OrdinalIgnoreCase))
            BeginNextGameDetection();
    }

    private void ObserveRosterRemovalSignal(string value)
    {
        if (!isInMatch || !matchStarted || !CurrentGame.RemovesEliminatedPlayers())
            return;

        string message = StripFormatting(value).Trim();
        foreach (string username in rows.Keys)
        {
            if (RosterSignalParser.IsRemovalSignalForPlayer(message, username))
                MarkEliminated(username);
        }
    }

    private bool UpdateHubStateFromInventory()
    {
        if (!isHive || Onix.LocalPlayer is not { } player)
            return false;

        bool hasGameSelector = player.Inventory.Slots.Any(stack =>
            StripFormatting(stack.CustomName).Trim().Equals("Game Selector [Use]", StringComparison.OrdinalIgnoreCase));
        if (!hasGameSelector)
            return false;

        if (isInMatch || !gameCode.Contains("HUB", StringComparison.OrdinalIgnoreCase))
        {
            gameCode = "HUB";
            Volatile.Write(ref currentGame, (int)HiveGame.Unknown);
            Volatile.Write(ref dimensionTransitionGame, (int)HiveGame.Unknown);
            isInMatch = false;
            matchStarted = false;
            ClearRoster();
        }

        connectionPollingEnabled = false;
        return true;
    }

    private void ScheduleConnectionProbe()
    {
        connectionPollingEnabled = true;
    }

    private void BeginNextGameDetection()
    {
        waitingForNextGame = false;
        completedServer = "";
        ScheduleConnectionProbe();
    }

    private void UpdateMatchStartFromPlayer()
    {
        if (!isInMatch || matchStarted || Onix.LocalPlayer is not { } player)
            return;

        if (player.Inventory.Slots.Any(stack =>
            (stack.Item?.Name ?? "").EndsWith("_sword", StringComparison.OrdinalIgnoreCase)))
            MarkMatchStarted();
    }

    private void MarkMatchStarted()
    {
        matchStarted = true;
        connectionPollingEnabled = false;
    }

    private bool OnSendCommand(string command)
    {
        string normalized = command.Trim();
        if (normalized.Equals("/hub", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/hub ", StringComparison.OrdinalIgnoreCase))
            HideUntilNextGame(probeConnection: true);
        return false;
    }

    private void ObserveMatchEndSignal(string value)
    {
        if (!isInMatch)
            return;

        string normalized = StripFormatting(value).Trim();
        if (normalized.Contains("GAME OVER", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("VICTORY", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("DEFEAT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("WON THE GAME", StringComparison.OrdinalIgnoreCase))
            HideUntilNextGame(probeConnection: false);
    }

    private void HideUntilNextGame(bool probeConnection)
    {
        if (!isHive)
            return;

        waitingForNextGame = true;
        completedServer = gameCode;
        isInMatch = false;
        matchStarted = false;
        gameCode = "";
        Volatile.Write(ref currentGame, (int)HiveGame.Unknown);
        ClearRoster();
        connectionPollingEnabled = probeConnection;
    }

    private void ApplyConnectionServer(string server)
    {
        bool isHub = server.Contains("HUB", StringComparison.OrdinalIgnoreCase);
        HiveGame detectedGame = isHub ? HiveGame.Unknown : HiveGames.FromServer(server);
        if (waitingForNextGame && detectedGame != HiveGame.Unknown && server.Equals(completedServer, StringComparison.OrdinalIgnoreCase))
        {
            connectionPollingEnabled = false;
            return;
        }
        if (waitingForNextGame && detectedGame != HiveGame.Unknown)
        {
            waitingForNextGame = false;
            completedServer = "";
        }

        bool wasInMatch = isInMatch;
        HiveGame previousGame = CurrentGame;
        string previousServer = gameCode;
        gameCode = server;
        HiveGame transitionGame = (HiveGame)Interlocked.Exchange(ref dimensionTransitionGame, (int)HiveGame.Unknown);
        bool startedFromDimensionChange = detectedGame != HiveGame.Unknown && transitionGame == detectedGame;
        Volatile.Write(ref currentGame, (int)detectedGame);
        isInMatch = detectedGame != HiveGame.Unknown;

        if (!isInMatch)
        {
            matchStarted = false;
            connectionPollingEnabled = false;
            ClearRoster();
            return;
        }

        connectionPollingEnabled = false;

        if (!wasInMatch || previousGame != detectedGame || !previousServer.Equals(server, StringComparison.OrdinalIgnoreCase))
        {
            ReplaceSessionCancellation();
            Interlocked.Increment(ref lobbyGeneration);
            ClearRoster();
            rosterCaptureReadyAt = DateTimeOffset.UtcNow + RosterSettleDelay;
            nextRosterPoll = rosterCaptureReadyAt;
            nextFailedStatsRetry = rosterCaptureReadyAt + FailedStatsRetryInterval;
        }

        RefreshRoster();
        if (startedFromDimensionChange)
            MarkMatchStarted();
    }

    private static bool IsHiveConnection()
    {
        string connected = Onix.ConnectionInfo.ConnectedIp ?? "";
        string joined = Onix.ConnectionInfo.JoinedIp ?? "";
        return connected.Contains("hive", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("hive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseConnection(string message, out string game)
    {
        game = "";
        Match match = ConnectionResponseRegex().Match(StripFormatting(message));
        if (!match.Success)
            return false;

        return TryParseHiveServerToken(match.Groups["server"].Value, out game);
    }

    private static bool TryParseHiveServerToken(string value, out string game)
    {
        game = StripFormatting(value).Trim().TrimEnd('.', ',', ';', ':').ToUpperInvariant();
        if (game.StartsWith("HUB", StringComparison.Ordinal) || HiveGames.FromServer(game) != HiveGame.Unknown)
            return true;

        game = "";
        return false;
    }

    private static string CleanUsername(string value) => StripFormatting(value).Trim();

    private static string StripFormatting(string value) => FormattingRegex().Replace(value, "");

    private void CancelSessionWork()
    {
        sessionCancellation.Cancel();
    }

    private void ReplaceSessionCancellation()
    {
        sessionCancellation.Cancel();
        sessionCancellation.Dispose();
        sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginEjectionToken);
    }

    private void ClearRoster()
    {
        rows.Clear();
        missingPlayers.Clear();
        eliminatedPlayers.Clear();
    }

    public void RequestStatsMode(PlayerStatsMode mode)
    {
        Volatile.Write(ref requestedStatsMode, (int)mode);
        Interlocked.Exchange(ref statsModeRefreshRequested, 1);
    }

    private void RefreshStatsMode()
    {
        ReplaceSessionCancellation();
        Interlocked.Increment(ref lobbyGeneration);
        rows.Clear();
        missingPlayers.Clear();
        if (isInMatch)
        {
            rosterCaptureReadyAt = DateTimeOffset.MinValue;
            nextRosterPoll = DateTimeOffset.MinValue;
            nextFailedStatsRetry = DateTimeOffset.UtcNow + FailedStatsRetryInterval;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Onix.Events.Session.Tick -= OnTick;
        Onix.Events.Session.SessionJoined -= OnSessionJoined;
        Onix.Events.Session.SessionLeft -= OnSessionLeft;
        Onix.Events.Session.DimensionChanged -= OnDimensionChanged;
        Onix.Events.Session.Title.Title -= OnTitleChanged;
        Onix.Events.Session.Title.Subtitle -= OnSubtitleChanged;
        Onix.Events.Session.Title.ActionBar -= OnActionBarChanged;
        Onix.Events.Session.Chat.CommandResponse -= OnCommandResponse;
        Onix.Events.Session.Chat.Receive -= OnChatReceive;
        Onix.Events.Session.Chat.SendCommand -= OnSendCommand;
        CancelSessionWork();
        backgroundTasks.TryWaitAll(TimeSpan.FromSeconds(5));
        sessionCancellation.Dispose();
    }

    [GeneratedRegex("You are connected to server(?: name)?\\s+(?<server>[A-Z][A-Z0-9-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionResponseRegex();

    [GeneratedRegex("(?:§|&)[0-9A-FK-ORP-T]", RegexOptions.IgnoreCase)]
    private static partial Regex FormattingRegex();
}
