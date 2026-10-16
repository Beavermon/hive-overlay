using OnixRuntime.Api.OnixClient;
using OnixRuntime.Api.OnixClient.Settings;

namespace HiveStatsOverlay;

internal sealed class HiveStatsSettings
{
    public readonly OnixSettingFloat UiScale;
    public readonly OnixSettingFloat Rounding;
    public readonly OnixSettingFloat BackgroundOpacity;
    public readonly OnixSettingFloat BorderOpacity;
    public readonly OnixSettingBool LandscapeMode;
    public readonly OnixSettingFloat TableWidth;
    public readonly OnixSettingFloat TableHeight;
    public readonly OnixSettingBool NickDetection;
    public readonly OnixSettingEnum StatsMode;
    public readonly OnixSettingBool RenderDummyTable;
    public readonly OnixSettingCategory LayoutCategory;
    public readonly OnixSettingCategory MiscCategory;
    private readonly Dictionary<HiveGame, GameDisplaySettings> games = [];

    public event Action<PlayerStatsMode>? StatsModeChanged;

    public HiveStatsSettings(OnixModule module)
    {
        UiScale = new OnixSettingFloat(module, "UI Scale", "ui_scale", 1f, 0.5f, 2.5f);
        Rounding = new OnixSettingFloat(module, "Rounding", "rounding", 4f, 0f, 16f);
        BackgroundOpacity = new OnixSettingFloat(module, "Background Opacity", "background_opacity", 0.94f, 0f, 1f);
        BorderOpacity = new OnixSettingFloat(module, "Border Opacity", "border_opacity", 0.82f, 0f, 1f);
        LayoutCategory = new OnixSettingCategory(module, "Layout", "Customize the table dimensions and orientation.", 3, true);
        LandscapeMode = new OnixSettingBool(module, "Landscape Mode", "landscape_mode", false, "Split players into two side-by-side tables for a wider, shorter layout.");
        TableWidth = new OnixSettingFloat(module, "Table Width", "table_width", 1f, 1f, 3f, "Adjust horizontal table spacing. Use UI Scale to shrink the full overlay.");
        TableHeight = new OnixSettingFloat(module, "Table Length", "table_length", 1f, 1f, 3f, "Adjust vertical table spacing and total length. Use UI Scale to shrink the full overlay.");
        MiscCategory = new OnixSettingCategory(module, "Misc", "Other overlay options.", 3, true);
        StatsMode = new OnixSettingEnum(
            module,
            "Stats Mode",
            "stats_mode",
            (int)PlayerStatsMode.AllTime,
            Entries([(PlayerStatsMode.AllTime, "All Time"), (PlayerStatsMode.Monthly, "Monthly")]),
            "Choose all-time or current-month match statistics.",
            (_, setting, _) => StatsModeChanged?.Invoke((PlayerStatsMode)((OnixSettingEnum)setting).Value)
        );
        NickDetection = new OnixSettingBool(module, "Nick Detection (Experimental)", "nick_detection", true, "Highlights usernames missing from Hive's player registry. This is heuristic and may not be 100% accurate.");
        RenderDummyTable = new OnixSettingBool(module, "Render Dummy Table", "render_dummy_table", false, "Show sample rows anywhere for visual testing, including a probable nick.");
        AddGame(module, HiveGame.BedWars, "bed", PlayerSortMode.Wins,
            [(PlayerSortMode.Wins, "Wins"), (PlayerSortMode.Fkdr, "FKDR"), (PlayerSortMode.Kdr, "KD"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Fkdr, "FKDR"), (StatColumn.Kdr, "KD"), (StatColumn.WinRate, "Winrate"), (StatColumn.Level, "Level"), (StatColumn.Beds, "Beds"), (StatColumn.GamesPlayed, "Games Played")]);
        AddGame(module, HiveGame.SkyWars, "sky", PlayerSortMode.Wins,
            [(PlayerSortMode.Fkdr, "FKDR"), (PlayerSortMode.Kdr, "KDR"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.Wins, "Wins"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.Kdr, "KDR"), (StatColumn.Fkdr, "FKDR")]);
        AddGame(module, HiveGame.MurderMystery, "murder", PlayerSortMode.Wins,
            [(PlayerSortMode.Wins, "Wins"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Murders, "Murders"), (PlayerSortMode.Deaths, "Deaths"), (PlayerSortMode.Edr, "EDR"), (PlayerSortMode.CoinsPerGame, "CPG"), (PlayerSortMode.Coins, "Coins"), (PlayerSortMode.MurdererEliminations, "Murder Eliminations"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.Murders, "Murders"), (StatColumn.Deaths, "Deaths"), (StatColumn.Edr, "EDR"), (StatColumn.CoinsPerGame, "CPG"), (StatColumn.Coins, "Coins"), (StatColumn.MurdererEliminations, "Murder Eliminations")],
            [StatColumn.Deaths, StatColumn.Edr, StatColumn.CoinsPerGame, StatColumn.MurdererEliminations]);
        AddGame(module, HiveGame.HideAndSeek, "hide", PlayerSortMode.Wins,
            [(PlayerSortMode.Wins, "Wins"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.HiderKills, "Hiders Killed"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.HiderKills, "Hiders Killed")]);
        AddGame(module, HiveGame.SurvivalGames, "sg", PlayerSortMode.Wins,
            [(PlayerSortMode.Kdr, "KDR"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.Wins, "Wins"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.Kdr, "KDR")]);
        AddGame(module, HiveGame.Bridge, "bridge", PlayerSortMode.Wins,
            [(PlayerSortMode.Wins, "Wins"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Kdr, "KDR"), (PlayerSortMode.Goals, "Goals"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.Kdr, "KDR"), (StatColumn.Goals, "Goals")]);
        AddGame(module, HiveGame.CaptureTheFlag, "ctf", PlayerSortMode.Wins,
            [(PlayerSortMode.Wins, "Wins"), (PlayerSortMode.WinRate, "Winrate"), (PlayerSortMode.GamesPlayed, "Games Played"), (PlayerSortMode.Level, "Level"), (PlayerSortMode.Kdr, "KDR"), (PlayerSortMode.FlagsCaptured, "Flags"), (PlayerSortMode.Name, "Name")],
            [(StatColumn.Wins, "Wins"), (StatColumn.Losses, "Losses"), (StatColumn.WinRate, "Winrate"), (StatColumn.GamesPlayed, "Games Played"), (StatColumn.Level, "Level"), (StatColumn.Kdr, "KDR"), (StatColumn.FlagsCaptured, "Flags")]);
    }

    private void AddGame(
        OnixModule module,
        HiveGame game,
        string key,
        PlayerSortMode defaultSort,
        (PlayerSortMode Mode, string Label)[] sortEntries,
        (StatColumn Column, string Label)[] columns,
        StatColumn[]? disabledByDefault = null)
    {
        new OnixSettingCategory(module, game.Title(), $"Choose {game.Title()} columns and sorting.", columns.Length + 1, false);
        string sortKey = game == HiveGame.BedWars ? "sort_by" : $"{key}_sort_by";
        OnixSettingEnum sort = new(module, "Sort By", sortKey, (int)defaultSort, Entries(sortEntries));
        Dictionary<StatColumn, OnixSettingBool> toggles = [];
        foreach ((StatColumn column, string label) in columns)
            toggles[column] = new OnixSettingBool(module, $"Show {label}", SettingKey(game, key, column), disabledByDefault?.Contains(column) != true);
        games[game] = new GameDisplaySettings(sort, toggles);
    }

    private static string ColumnKey(StatColumn column) => column switch
    {
        StatColumn.WinRate => "winrate",
        StatColumn.GamesPlayed => "games_played",
        StatColumn.CoinsPerGame => "cpg",
        StatColumn.MurdererEliminations => "murder_eliminations",
        StatColumn.HiderKills => "hider_kills",
        StatColumn.FlagsCaptured => "flags_captured",
        _ => column.ToString().ToLowerInvariant()
    };

    private static string SettingKey(HiveGame game, string key, StatColumn column)
    {
        if (game != HiveGame.BedWars)
            return $"{key}_show_{ColumnKey(column)}";
        return column switch
        {
            StatColumn.Fkdr => "show_fkdr",
            StatColumn.Kdr => "show_kd",
            StatColumn.WinRate => "show_wr",
            StatColumn.Level => "show_level",
            StatColumn.Beds => "show_beds",
            StatColumn.GamesPlayed => "show_games_played",
            _ => $"show_{ColumnKey(column)}"
        };
    }

    private static OnixSettingEnum.EnumEntries Entries<T>((T Value, string Label)[] values) where T : Enum =>
        new(values.Select(value => new OnixSettingEnum.EnumEntry(Convert.ToInt32(value.Value), value.Label)).ToList());

    public PlayerStatsMode Mode => (PlayerStatsMode)StatsMode.Value;
    public GameDisplaySettings ForGame(HiveGame game) => games.TryGetValue(game, out GameDisplaySettings? value) ? value : games[HiveGame.BedWars];
}

internal sealed record GameDisplaySettings(
    OnixSettingEnum SortSetting,
    IReadOnlyDictionary<StatColumn, OnixSettingBool> Columns)
{
    public PlayerSortMode Sort => (PlayerSortMode)SortSetting.Value;
    public bool Shows(StatColumn column) => Columns.TryGetValue(column, out OnixSettingBool? setting) && setting.Value;
}
