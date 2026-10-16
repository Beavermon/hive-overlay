using System.Globalization;
using OnixRuntime.Api.Maths;
using OnixRuntime.Api.OnixClient;
using OnixRuntime.Api.Rendering;

namespace HiveStatsOverlay;

internal sealed class HiveStatsVisual : IDisposable
{
    private const float CellPadding = 2f;
    private const float TextScale = 0.75f;
    private const float TitleTextScale = 0.9f;
    private const float BaseScale = 1.5f;
    private const float TitleInset = 2f;
    private const float TitleGap = 3f;

    private static readonly ColorF Text = new(255, 255, 255, 255);
    private static readonly ColorF Gray = new(160, 160, 160, 255);
    private static readonly ColorF DarkBlue = new(38, 93, 255, 255);
    private static readonly ColorF Red = new(255, 102, 102, 255);
    private static readonly ColorF Green = new(76, 217, 100, 255);
    private static readonly ColorF Gold = new(255, 183, 77, 255);
    private static readonly ColorF Magenta = new(213, 128, 255, 255);
    private static readonly ColorF Aqua = new(77, 208, 225, 255);
    private static readonly ColorF Yellow = new(255, 213, 79, 255);
    private static readonly ColorF LightGray = new(192, 192, 192, 255);
    private static readonly IReadOnlyList<OverlayRow> DummyRows = [
        Dummy("PrestigeV", 100, 5, 1200, 1080, 12000, 800, 8400, 2600),
        Dummy("PrestigeIV", 80, 4, 900, 720, 5200, 650, 3000, 1400),
        Dummy("PrestigeIII", 50, 3, 600, 360, 2100, 600, 1200, 700),
        Dummy("PrestigeII", 30, 2, 300, 135, 700, 350, 420, 210),
        Dummy("PrestigeI", 25, 1, 180, 63, 300, 180, 180, 90),
        Dummy("YellowLevel", 20, 0, 100, 25, 120, 100, 60, 40),
        Dummy("GoldLevel", 10, 0, 50, 8, 40, 50, 18, 12),
        Dummy("PossibleNick", PlayerStatsStatus.Nicked)
    ];

    private readonly HiveSessionController session;
    private readonly OnixRuntime.Plugin.OnixPluginSaveManager saveManager;
    private readonly OnixModuleVisual module;
    private readonly HiveStatsSettings settings;

    public HiveStatsVisual(HiveSessionController session, OnixRuntime.Plugin.OnixPluginSaveManager saveManager, HiveStatsSettings settings)
    {
        this.session = session;
        this.saveManager = saveManager;
        this.settings = settings;
        module = new OnixModuleVisual(
            "Hive Stats Overlay",
            "Displays Hive stats of players in your lobby",
            new Vec2(20, 50),
            OnixModuleVisual.VisualAnchor.TopLeft,
            new Vec2(300, 45),
            "hive_stats_overlay",
            true
        );

        module.Render += Render;
        module.Enabled = true;
        saveManager.TrackModule(module);
    }

    private void Render(OnixModuleVisual visual, RendererCommon2D gfx, float _)
    {
        bool renderDummy = settings.RenderDummyTable.Value;
        if (!renderDummy && !session.IsInMatch)
        {
            visual.Size = new Vec2(0, 0);
            return;
        }

        HiveGame game = session.CurrentGame;
        if (game == HiveGame.Unknown)
            game = HiveGame.BedWars;
        float scale = settings.UiScale.Value * BaseScale;
        IReadOnlyList<OverlayRow> rows = SortRows(renderDummy ? DummyRows : session.Snapshot(), game);
        List<TableColumn> columns = GetColumns(game);
        FitColumns(gfx, columns, rows, scale);
        int paneCount = settings.LandscapeMode.Value && rows.Count > 1 ? 2 : 1;
        int rowsPerPane = (rows.Count + paneCount - 1) / paneCount;
        float paneWidth = columns.Sum(column => column.Width) * scale * settings.TableWidth.Value;
        float tableWidth = paneWidth * paneCount;
        float cellPadding = CellPadding * scale * settings.TableHeight.Value;
        float tableTextHeight = gfx.MeasureText("Ag", TextScale * scale).Y;
        float headerHeight = tableTextHeight + cellPadding * 2;
        float rowHeight = tableTextHeight + cellPadding * 2;
        string gameTitle = game.Title();
        float titleScale = TitleTextScale * scale;
        Vec2 titleSize = gfx.MeasureText(gameTitle, titleScale);
        float titleHeight = titleSize.Y * settings.TableHeight.Value;
        float height = titleHeight + headerHeight + rowsPerPane * rowHeight;
        visual.Size = new Vec2(tableWidth, height);

        Rect area = visual.Area;
        ColorF background = new(15, 15, 15, Alpha(settings.BackgroundOpacity.Value));
        ColorF grid = new(79, 79, 89, Alpha(settings.BorderOpacity.Value));
        gfx.FillRoundedRectangle(area, background, settings.Rounding.Value * scale);

        float titleY = area.Y + (titleHeight - titleSize.Y) / 2;
        float titleX = area.X + TitleInset * scale;
        gfx.RenderText(new Vec2(titleX, titleY), Gold, gameTitle, titleScale);
        string modeTitle = settings.Mode == PlayerStatsMode.Monthly ? "monthly stats" : "all-time stats";
        gfx.RenderText(new Vec2(titleX + titleSize.X + TitleGap * scale, titleY), LightGray, modeTitle, titleScale);

        float tableLeft = area.X;
        float tableTop = area.Y + titleHeight;
        DrawBorder(gfx, tableLeft, area.Y, tableLeft + tableWidth, tableTop, scale, grid);
        for (int pane = 0; pane < paneCount; pane++)
            DrawPane(gfx, columns, rows.Skip(pane * rowsPerPane).Take(rowsPerPane).ToArray(), tableLeft + pane * paneWidth, tableTop, paneWidth, rowsPerPane, headerHeight, rowHeight, cellPadding, scale, grid);
    }

    private void DrawPane(
        RendererCommon2D gfx,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<OverlayRow> rows,
        float left,
        float top,
        float width,
        int rowCount,
        float headerHeight,
        float rowHeight,
        float cellPadding,
        float scale,
        ColorF grid
    )
    {
        float right = left + width;
        float bottom = top + headerHeight + rowCount * rowHeight;
        float backgroundOpacity = settings.BackgroundOpacity.Value;
        ColorF header = new(48, 48, 51, Alpha(backgroundOpacity));
        ColorF alternateRow = new(35, 35, 37, Alpha(backgroundOpacity * 235f / 255f));
        ColorF nickRow = new(78, 52, 21, Alpha(backgroundOpacity * 235f / 255f));
        gfx.FillRectangle(new Rect(left, top, right, top + headerHeight), header);
        for (int index = 0; index < rows.Count; index++)
        {
            ColorF? background = IsProbableNick(rows[index]) ? nickRow : (index & 1) == 1 ? alternateRow : null;
            if (background is { } color)
            {
                float rowTop = top + headerHeight + index * rowHeight;
                gfx.FillRectangle(new Rect(left, rowTop, right, rowTop + rowHeight), color);
            }
        }

        DrawGrid(gfx, columns, left, top, right, bottom, rowCount, headerHeight, rowHeight, scale, grid, settings.TableWidth.Value);
        DrawColumns(gfx, columns, left, top + cellPadding, scale, null, settings.TableWidth.Value);
        float y = top + headerHeight + cellPadding;
        foreach (OverlayRow row in rows)
        {
            DrawColumns(gfx, columns, left, y, scale, row, settings.TableWidth.Value);
            y += rowHeight;
        }
    }

    private List<TableColumn> GetColumns(HiveGame game)
    {
        List<TableColumn> columns = [new("Player", StatColumn.Player)];
        GameDisplaySettings display = settings.ForGame(game);
        foreach (StatColumn column in ColumnsFor(game))
        {
            if (display.Shows(column))
                columns.Add(new(HeaderFor(column, game), column));
        }
        return columns;
    }

    private static StatColumn[] ColumnsFor(HiveGame game) => game switch
    {
        HiveGame.BedWars => [StatColumn.Fkdr, StatColumn.Kdr, StatColumn.WinRate, StatColumn.Level, StatColumn.Beds, StatColumn.GamesPlayed],
        HiveGame.SkyWars => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.Kdr, StatColumn.Fkdr],
        HiveGame.MurderMystery => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.Murders, StatColumn.Deaths, StatColumn.Edr, StatColumn.CoinsPerGame, StatColumn.Coins, StatColumn.MurdererEliminations],
        HiveGame.HideAndSeek => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.HiderKills],
        HiveGame.SurvivalGames => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.Kdr],
        HiveGame.Bridge => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.Kdr, StatColumn.Goals],
        HiveGame.CaptureTheFlag => [StatColumn.Wins, StatColumn.Losses, StatColumn.WinRate, StatColumn.GamesPlayed, StatColumn.Level, StatColumn.Kdr, StatColumn.FlagsCaptured],
        _ => []
    };

    private static string HeaderFor(StatColumn column, HiveGame game) => column switch
    {
        StatColumn.Wins => "Wins",
        StatColumn.Losses => "Losses",
        StatColumn.WinRate => "Winrate",
        StatColumn.GamesPlayed => "Games",
        StatColumn.Kdr => game == HiveGame.BedWars ? "KD" : "KDR",
        StatColumn.Fkdr => "FKDR",
        StatColumn.Level => "Level",
        StatColumn.Beds => "Beds",
        StatColumn.Murders => "Murders",
        StatColumn.Deaths => "Deaths",
        StatColumn.Edr => "EDR",
        StatColumn.CoinsPerGame => "CPG",
        StatColumn.Coins => "Coins",
        StatColumn.MurdererEliminations => "Murder Elims",
        StatColumn.HiderKills => "Hiders Killed",
        StatColumn.Goals => "Goals",
        StatColumn.FlagsCaptured => "Flags",
        _ => "Player"
    };

    private void FitColumns(RendererCommon2D gfx, IReadOnlyList<TableColumn> columns, IReadOnlyList<OverlayRow> rows, float scale)
    {
        float textScale = TextScale * scale;
        foreach (TableColumn column in columns)
        {
            float contentWidth = gfx.MeasureText(column.Header, textScale).X;
            foreach (OverlayRow row in rows)
                contentWidth = Math.Max(contentWidth, gfx.MeasureText(GetValue(column.Kind, row), textScale).X);
            column.Width = contentWidth / scale + CellPadding * 2;
        }
    }

    private void DrawColumns(RendererCommon2D gfx, IReadOnlyList<TableColumn> columns, float left, float y, float scale, OverlayRow? row, float widthScale)
    {
        float x = left;
        foreach (TableColumn column in columns)
        {
            string value = row is null ? column.Header : GetValue(column.Kind, row);
            ColorF color = row is null ? Text : GetColor(column.Kind, row);
            float textX = x + CellPadding * scale;
            if (row is not null && column.Kind != StatColumn.Player)
            {
                float textWidth = gfx.MeasureText(value, TextScale * scale).X;
                textX = x + column.Width * scale * widthScale - CellPadding * scale - textWidth;
            }
            Draw(gfx, textX, y, value, color, scale);
            x += column.Width * scale * widthScale;
        }
    }

    private string GetValue(StatColumn kind, OverlayRow row) => kind switch
    {
        StatColumn.Player => row.Username,
        StatColumn.Wins => Count(row.Stats, stats => stats.Victories),
        StatColumn.Losses => Count(row.Stats, stats => stats.Losses),
        StatColumn.WinRate => Metric(row.Stats, stats => stats.WinRate, "0") + (HasNumericStats(row.Stats) ? "%" : ""),
        StatColumn.GamesPlayed => Count(row.Stats, stats => stats.Played),
        StatColumn.Kdr => Metric(row.Stats, stats => stats.Kdr, "0.00"),
        StatColumn.Fkdr => Metric(row.Stats, stats => stats.EstimatedFkdr, "0.00"),
        StatColumn.Level => LevelText(row.Stats),
        StatColumn.Beds => Count(row.Stats, stats => stats.BedsDestroyed),
        StatColumn.Murders => Count(row.Stats, stats => stats.Murders),
        StatColumn.Deaths => Count(row.Stats, stats => stats.Deaths),
        StatColumn.Edr => Metric(row.Stats, stats => stats.Edr, "0.00"),
        StatColumn.CoinsPerGame => Metric(row.Stats, stats => stats.CoinsPerGame, "0.00"),
        StatColumn.Coins => Count(row.Stats, stats => stats.Coins),
        StatColumn.MurdererEliminations => Count(row.Stats, stats => stats.MurdererEliminations),
        StatColumn.HiderKills => Count(row.Stats, stats => stats.HiderKills),
        StatColumn.Goals => Count(row.Stats, stats => stats.Goals),
        StatColumn.FlagsCaptured => Count(row.Stats, stats => stats.FlagsCaptured),
        _ => ""
    };

    private ColorF GetColor(StatColumn kind, OverlayRow row)
    {
        if (kind == StatColumn.Player)
            return IsProbableNick(row) ? Gold : Text;
        if (row.Stats.Status != PlayerStatsStatus.Ready)
            return Text;
        return kind switch
        {
            StatColumn.Fkdr => RatioColor(row.Stats.EstimatedFkdr),
            StatColumn.Kdr => RatioColor(row.Stats.Kdr),
            StatColumn.Edr => RatioColor(row.Stats.Edr),
            StatColumn.CoinsPerGame => RatioColor(row.Stats.CoinsPerGame),
            StatColumn.WinRate => WinRateColor(row.Stats.WinRate),
            StatColumn.Level => LevelColor(row.Stats.Level),
            StatColumn.Beds => BedsColor(row.Stats.BedsDestroyed),
            _ => Text
        };
    }

    private IReadOnlyList<OverlayRow> SortRows(IReadOnlyList<OverlayRow> rows, HiveGame game)
    {
        string localName = session.LocalUsername;
        IOrderedEnumerable<OverlayRow> ordered = rows
            .OrderBy(row => localName.Length != 0 && row.Username.Equals(localName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => row.Stats.Status == PlayerStatsStatus.Ready ? 0 : 1);
        ordered = settings.ForGame(game).Sort switch
        {
            PlayerSortMode.Fkdr => ordered.ThenByDescending(row => row.Stats.EstimatedFkdr),
            PlayerSortMode.Kdr => ordered.ThenByDescending(row => row.Stats.Kdr),
            PlayerSortMode.WinRate => ordered.ThenByDescending(row => row.Stats.WinRate),
            PlayerSortMode.Level => ordered.ThenByDescending(row => row.Stats.Level),
            PlayerSortMode.Wins => ordered.ThenByDescending(row => row.Stats.Victories),
            PlayerSortMode.GamesPlayed => ordered.ThenByDescending(row => row.Stats.Played),
            PlayerSortMode.Murders => ordered.ThenByDescending(row => row.Stats.Murders),
            PlayerSortMode.Deaths => ordered.ThenByDescending(row => row.Stats.Deaths),
            PlayerSortMode.Edr => ordered.ThenByDescending(row => row.Stats.Edr),
            PlayerSortMode.CoinsPerGame => ordered.ThenByDescending(row => row.Stats.CoinsPerGame),
            PlayerSortMode.Coins => ordered.ThenByDescending(row => row.Stats.Coins),
            PlayerSortMode.MurdererEliminations => ordered.ThenByDescending(row => row.Stats.MurdererEliminations),
            PlayerSortMode.HiderKills => ordered.ThenByDescending(row => row.Stats.HiderKills),
            PlayerSortMode.Goals => ordered.ThenByDescending(row => row.Stats.Goals),
            PlayerSortMode.FlagsCaptured => ordered.ThenByDescending(row => row.Stats.FlagsCaptured),
            PlayerSortMode.Name => ordered.ThenBy(row => row.Username, StringComparer.OrdinalIgnoreCase),
            _ => ordered
        };
        return ordered.ThenBy(row => row.Username, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Draw(RendererCommon2D gfx, float x, float y, string value, ColorF color, float scale) =>
        gfx.RenderText(new Vec2(x, y), color, value, TextScale * scale);

    private static void DrawGrid(
        RendererCommon2D gfx,
        IReadOnlyList<TableColumn> columns,
        float left,
        float top,
        float right,
        float bottom,
        int rowCount,
        float headerHeight,
        float rowHeight,
        float scale,
        ColorF grid,
        float widthScale
    )
    {
        float lineWidth = Math.Max(scale, 1f);
        float x = left;
        gfx.FillRectangle(new Rect(x, top, x + lineWidth, bottom), grid);
        for (int index = 0; index < columns.Count; index++)
        {
            x += columns[index].Width * scale * widthScale;
            float lineX = index == columns.Count - 1 ? right - lineWidth : x;
            gfx.FillRectangle(new Rect(lineX, top, lineX + lineWidth, bottom), grid);
        }

        gfx.FillRectangle(new Rect(left, top, right, top + lineWidth), grid);
        gfx.FillRectangle(new Rect(left, top + headerHeight, right, top + headerHeight + lineWidth), grid);
        for (int i = 1; i <= rowCount; i++)
        {
            float lineY = top + headerHeight + i * rowHeight;
            gfx.FillRectangle(new Rect(left, lineY, right, lineY + lineWidth), grid);
        }
    }

    private static void DrawBorder(RendererCommon2D gfx, float left, float top, float right, float bottom, float scale, ColorF grid)
    {
        float lineWidth = Math.Max(scale, 1f);
        gfx.FillRectangle(new Rect(left, top, right, top + lineWidth), grid);
        gfx.FillRectangle(new Rect(left, bottom, right, bottom + lineWidth), grid);
        gfx.FillRectangle(new Rect(left, top, left + lineWidth, bottom), grid);
        gfx.FillRectangle(new Rect(right - lineWidth, top, right, bottom), grid);
    }

    private string LevelText(PlayerStats stats)
    {
        if (!HasNumericStats(stats))
            return StatusText(stats.Status);
        string prestige = stats.Prestige switch
        {
            1 => "[I] ",
            2 => "[II] ",
            3 => "[III] ",
            4 => "[IV] ",
            5 => "[V] ",
            > 5 => $"[{stats.Prestige}] ",
            _ => ""
        };
        return $"{prestige}{stats.Level}";
    }

    private string Metric(PlayerStats stats, Func<PlayerStats, double> selector, string format) =>
        HasNumericStats(stats) ? selector(stats).ToString(format) : StatusText(stats.Status);

    private string Count(PlayerStats stats, Func<PlayerStats, int> selector) =>
        HasNumericStats(stats) ? CompactCount(selector(stats)) : StatusText(stats.Status);

    private static string CompactCount(int value)
    {
        if (value < 1000)
            return value.ToString(CultureInfo.InvariantCulture);
        if (value < 1_000_000)
            return $"{(value / 1000d).ToString("0.#", CultureInfo.InvariantCulture)}k";
        return $"{(value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}m";
    }

    private static bool HasNumericStats(PlayerStats stats) =>
        stats.Status is PlayerStatsStatus.Ready or PlayerStatsStatus.NoData;

    private string StatusText(PlayerStatsStatus status) => status switch
    {
        PlayerStatsStatus.Loading => "...",
        PlayerStatsStatus.NoData => "N/A",
        PlayerStatsStatus.RateLimited => "429",
        PlayerStatsStatus.InvalidResponse => "ERR",
        PlayerStatsStatus.Nicked => "N/A",
        _ => "OFF"
    };

    private bool IsProbableNick(OverlayRow row) => settings.NickDetection.Value && row.Stats.Status == PlayerStatsStatus.Nicked;

    private static ColorF RatioColor(double value) =>
        value >= 10 ? DarkBlue : value >= 5 ? Red : value >= 3 ? Green : value >= 1.5 ? Gold : Gray;

    private static ColorF WinRateColor(double value) =>
        value >= 90 ? DarkBlue : value >= 80 ? Red : value >= 50 ? Green : value >= 20 ? Gold : Gray;

    private static ColorF LevelColor(int value) =>
        value >= 100 ? DarkBlue : value >= 80 ? Red : value >= 50 ? Green : value >= 30 ? Magenta :
        value >= 25 ? Aqua : value >= 20 ? Yellow : value >= 10 ? Gold : Gray;

    private static ColorF BedsColor(int value) =>
        value >= 5000 ? DarkBlue : value >= 3000 ? Red : value >= 1000 ? Green : value >= 500 ? Magenta :
        value >= 200 ? Aqua : value >= 100 ? Yellow : value >= 50 ? Gold : Gray;

    private static byte Alpha(float opacity) => (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255);

    private static OverlayRow Dummy(
        string username,
        int level,
        int prestige,
        int played,
        int victories,
        int kills,
        int deaths,
        int finalKills,
        int beds
    ) => new(username, new PlayerStats
    {
        Username = username,
        Level = level,
        Prestige = prestige,
        Played = played,
        Victories = victories,
        Kills = kills,
        Deaths = deaths,
        FinalKills = finalKills,
        BedsDestroyed = beds,
        Murders = finalKills / 3,
        Coins = played * 14,
        MurdererEliminations = victories / 2,
        HiderKills = kills / 4,
        Goals = victories * 3,
        FlagsCaptured = victories / 2,
        Status = PlayerStatsStatus.Ready
    });

    private static OverlayRow Dummy(string username, PlayerStatsStatus status) => new(username, new PlayerStats
    {
        Username = username,
        Status = status
    });

    public void Dispose()
    {
        module.Render -= Render;
        saveManager.UntrackModule(module);
        module.Dispose();
    }

    private sealed class TableColumn(string header, StatColumn kind)
    {
        public string Header { get; } = header;
        public StatColumn Kind { get; } = kind;
        public float Width { get; set; }
    }
}
