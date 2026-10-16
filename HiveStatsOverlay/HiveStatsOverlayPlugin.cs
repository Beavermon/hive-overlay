using OnixRuntime.Plugin;

namespace HiveStatsOverlay;

public sealed class HiveStatsOverlayPlugin : OnixPluginBase
{
    private HiveApiClient? api;
    private HiveSessionController? session;
    private HiveStatsVisual? visual;
    private HiveStatsSettings? settings;

    public HiveStatsOverlayPlugin(OnixPluginInitInfo initInfo) : base(initInfo)
    {
        DisablingShouldUnloadPlugin = false;
    }

    protected override void OnLoaded()
    {
        settings = new HiveStatsSettings(PluginDisplayModule);
    }

    protected override void OnEnabled()
    {
        if (session is not null)
            return;

        api ??= new HiveApiClient(Path.Combine(PluginPersistentDataPath, "stats-cache.json"), PluginEjectionCancellationToken);
        settings ??= new HiveStatsSettings(PluginDisplayModule);
        session = new HiveSessionController(api, PluginEjectionCancellationToken);
        settings.StatsModeChanged += session.RequestStatsMode;
        session.RequestStatsMode(settings.Mode);
        session.Start();
        visual ??= new HiveStatsVisual(session, SaveManager, settings);
    }

    protected override void OnDisabled()
    {
        visual?.Dispose();
        visual = null;
        if (settings is not null && session is not null)
            settings.StatsModeChanged -= session.RequestStatsMode;
        session?.Dispose();
        session = null;
    }

    protected override void OnUnloaded()
    {
        OnDisabled();
        api?.Dispose();
        api = null;
        settings = null;
    }
}
