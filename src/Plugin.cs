using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Launchers;
using PlayniteUpdateStatus.UI;
using PlayniteUpdateStatus.Launch;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public override Guid Id { get { return new Guid("5adef35e-891f-4f08-870e-4f2b6a64f7e7"); } }
        public Settings Settings { get { return settingsViewModel.Settings; } }
        private readonly SettingsViewModel settingsViewModel;
        public UpdateMonitor Monitor { get; private set; }
        public SteamUpdateCatalog SteamCatalog { get; private set; }
        public SteamCoverHistory CoverHistory { get; private set; }
        private readonly SteamLocationStore steamLocation;
        private readonly LaunchCoordinator launchCoordinator;
        private readonly UniversalStatusPresenter universal;
        private readonly CoverBadgePresenter coverPresenter;
        private readonly DispatcherTimer coverTimer;
        private readonly List<WeakReference> badgeControls = new List<WeakReference>();
        private readonly List<WeakReference> themeControls = new List<WeakReference>();
        private readonly DispatcherTimer uiTimer;
        private bool stopped;
        private string foregroundResult;
        private readonly ForegroundRestorePolicy foregroundPolicy = new ForegroundRestorePolicy();
        public Plugin(IPlayniteAPI api) : this(api, () => new SteamLocator().FindRoot()) { }
        internal Plugin(IPlayniteAPI api, Func<string> discoverSteam) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            settingsViewModel = new SettingsViewModel(LoadPluginSettings<Settings>() ?? new Settings(),
                save: settings => SavePluginSettings(settings), apply: ApplySettings, browse: SelectSteamFolder);
            SteamLocator.ManualRoot = Settings.SteamFolder;
            steamLocation = new SteamLocationStore(System.IO.Path.Combine(GetPluginUserDataPath(), "steam-location.v1"), Log);
            SteamLocator.CachedRoot = steamLocation.Folder;
            SteamLocator.Located += StoreSteamLocation;
            Monitor = new UpdateMonitor(Log);
            SteamCatalog = new SteamUpdateCatalog(Log);
            CoverHistory = new SteamCoverHistory(System.IO.Path.Combine(GetPluginUserDataPath(), "steam-update-badges.v1"), Log);
            SteamCatalog.Changed += CoverCatalogChanged;
            Monitor.SpeedUnit = Settings.SpeedUnit;
            launchCoordinator = new LaunchCoordinator(api, Monitor, LauncherCatalog.Create(Log), SteamCatalog, () => Settings, logger);
            launchCoordinator.MonitoringRequested += ApplyCoverSettings;
            launchCoordinator.ObservationStarted += reset =>
            {
                foregroundResult = null;
                if (reset) foregroundPolicy.Reset();
            };
            launchCoordinator.StatusRequested += StartStatusUI;
            universal = new UniversalStatusPresenter(Monitor, Log);
            coverPresenter = new CoverBadgePresenter(Log);
            coverTimer = new DispatcherTimer(DispatcherPriority.Background, api.MainView.UIDispatcher) { Interval = TimeSpan.FromSeconds(1) };
            coverTimer.Tick += (s, e) => RefreshCoverBadges();
            AddCustomElementSupport(new AddCustomElementSupportArgs { SourceName = "PlayniteUpdateStatus", ElementList = new List<string> { "Status", "Badge" } });
            Monitor.Changed += StateChanged;
            uiTimer = new DispatcherTimer(DispatcherPriority.Background, api.MainView.UIDispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
            uiTimer.Tick += (s, e) => RefreshUI();
            // First load after installation discovers this PC's folder before
            // settings or monitoring are opened. Only local metadata is read.
            try
            {
                string root = discoverSteam();
                if (SteamLocator.IsValidRoot(root))
                {
                    root = SteamLocator.NormalizeRoot(root); SteamLocator.CachedRoot = root;
                    StoreSteamLocation(root);
                }
            }
            catch (System.IO.IOException) { Log("Steam location initialization unavailable; automatic discovery will retry", true); }
            catch (UnauthorizedAccessException) { Log("Steam location initialization inaccessible; automatic discovery will retry", true); }
            catch (ArgumentException) { Log("Invalid initial Steam location ignored; automatic discovery will retry", true); }
        }
        private void Log(string message, bool warning)
        { if (warning) logger.Warn("[Update Status] " + message); else logger.Info("[Update Status] " + message); }
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        { Shutdown(); }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        { if (closing || stopped) return; if (Settings.Enabled) SteamCatalog.Start(); ApplyCoverSettings(); }
        public override void Dispose()
        {
            if (stopped) return;
            launchCoordinator.Dispose();
            stopped = true; SteamLocator.Located -= StoreSteamLocation; Monitor.Changed -= StateChanged; Monitor.Dispose(); SteamCatalog.Changed -= CoverCatalogChanged; SteamCatalog.Dispose();
            var dispatcher = PlayniteApi.MainView.UIDispatcher;
            Action cleanup = () => { uiTimer.Stop(); coverTimer.Stop(); coverPresenter.Dispose(); universal.Dispose(); ShutdownBadgeControls(); };
            if (dispatcher.CheckAccess()) cleanup();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(cleanup);
            base.Dispose();
        }
    }
}
