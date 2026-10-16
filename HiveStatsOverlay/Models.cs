namespace HiveStatsOverlay;

internal enum HiveGame
{
    Unknown,
    BedWars,
    SkyWars,
    MurderMystery,
    HideAndSeek,
    SurvivalGames,
    Bridge,
    CaptureTheFlag
}

internal static class HiveGames
{
    public static string ApiCode(this HiveGame game) => game switch
    {
        HiveGame.BedWars => "bed",
        HiveGame.SkyWars => "sky",
        HiveGame.MurderMystery => "murder",
        HiveGame.HideAndSeek => "hide",
        HiveGame.SurvivalGames => "sg",
        HiveGame.Bridge => "bridge",
        HiveGame.CaptureTheFlag => "ctf",
        _ => ""
    };

    public static string Title(this HiveGame game) => game switch
    {
        HiveGame.BedWars => "BedWars",
        HiveGame.SkyWars => "SkyWars",
        HiveGame.MurderMystery => "Murder Mystery",
        HiveGame.HideAndSeek => "Hide and Seek",
        HiveGame.SurvivalGames => "Survival Games",
        HiveGame.Bridge => "The Bridge",
        HiveGame.CaptureTheFlag => "Capture The Flag",
        _ => "Hive"
    };

    public static HiveGame FromServer(string server)
    {
        string upper = server.Trim().ToUpperInvariant();
        int dash = upper.IndexOf('-');
        string root = dash < 0 ? upper : upper[..dash];
        root = root.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return root switch
        {
            "BED" => HiveGame.BedWars,
            "SKY" => HiveGame.SkyWars,
            "MURDER" => HiveGame.MurderMystery,
            "HIDE" => HiveGame.HideAndSeek,
            "SG" => HiveGame.SurvivalGames,
            "BRIDGE" => HiveGame.Bridge,
            "CTF" => HiveGame.CaptureTheFlag,
            _ => HiveGame.Unknown
        };
    }

    public static bool RemovesEliminatedPlayers(this HiveGame game) =>
        game is HiveGame.BedWars or HiveGame.SkyWars or HiveGame.SurvivalGames;
}

internal enum PlayerStatsStatus
{
    Loading,
    Ready,
    NoData,
    RateLimited,
    Unavailable,
    InvalidResponse,
    Nicked
}

internal enum PlayerStatsMode
{
    AllTime,
    Monthly
}

internal enum PlayerSortMode
{
    Fkdr = 0,
    Kdr = 1,
    WinRate = 2,
    Level = 3,
    Name = 5,
    Wins = 6,
    GamesPlayed = 8,
    Murders = 9,
    Deaths = 10,
    Edr = 11,
    CoinsPerGame = 12,
    Coins = 13,
    MurdererEliminations = 14,
    HiderKills = 15,
    Goals = 16,
    FlagsCaptured = 17
}

internal enum StatColumn
{
    Player,
    Wins,
    Losses,
    WinRate,
    GamesPlayed,
    Kdr,
    Fkdr,
    Level,
    Beds,
    Murders,
    Deaths,
    Edr,
    CoinsPerGame,
    Coins,
    MurdererEliminations,
    HiderKills,
    Goals,
    FlagsCaptured
}

internal sealed record PlayerStats
{
    public string Username { get; init; } = "";
    public int Level { get; init; }
    public int Prestige { get; init; }
    public int Played { get; init; }
    public int Victories { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int FinalKills { get; init; }
    public int BedsDestroyed { get; init; }
    public int Murders { get; init; }
    public int Coins { get; init; }
    public int MurdererEliminations { get; init; }
    public int HiderKills { get; init; }
    public int Goals { get; init; }
    public int FlagsCaptured { get; init; }
    public PlayerStatsStatus Status { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public int Losses => Math.Max(Played - Victories, 0);
    public double WinRate => Played == 0 ? 0 : Victories * 100d / Played;
    public double Kdr => Deaths == 0 ? Kills : Kills / (double)Deaths;
    public double EstimatedFkdr => Losses == 0 ? FinalKills : FinalKills / (double)Losses;
    public double Edr => Deaths == 0 ? MurdererEliminations : MurdererEliminations / (double)Deaths;
    public double CoinsPerGame => Played == 0 ? 0 : Coins / (double)Played;

    public static PlayerStats Loading(string username) => new()
    {
        Username = username,
        Status = PlayerStatsStatus.Loading
    };
}

internal sealed record OverlayRow(string Username, PlayerStats Stats);

internal sealed record CacheEntry(PlayerStats Stats, DateTimeOffset ExpiresAt, DateTimeOffset RetainUntil);
