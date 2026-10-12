using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;
using static UpdateStatus.Tests.Support.SteamSamples;

internal static partial class Program
{
    private static void PluginSmoke(string playnite, bool sdkOnly = false, bool handoffOnly = false, bool badgeSettingsOnly = false, bool resumeUiOnly = false, bool shutdownOnly = false, bool closeReviewOnly = false)
    {
        if (sdkOnly)
            Console.WriteLine("SDK runtime " + typeof(IPlayniteAPI).Assembly.GetName().Version + "; plugin reference " + typeof(PlayniteUpdateStatus.Plugin).Assembly.GetReferencedAssemblies().Single(reference => reference.Name == "Playnite.SDK").Version);
        ResolveEventHandler resolve = (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            if (sdkOnly && name.StartsWith("Playnite", StringComparison.Ordinal) && name != "Playnite.SDK")
                throw new InvalidOperationException("SDK-only fixture requested a private host assembly: " + name);
            string path = Path.Combine(playnite, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        try
        {
            LogManager.Init((ILogProvider)new ApiProxy(typeof(ILogProvider)).GetTransparentProxy());
            object serializer;
            if (sdkOnly)
                serializer = new SdkJsonSerializer(playnite).GetTransparentProxy();
            else
            {
                var hostAssembly = Assembly.LoadFrom(Path.Combine(playnite, "Playnite.dll"));
                Type[] hostTypes;
                try
                {
                    hostTypes = hostAssembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    hostTypes = e.Types.Where(t => t != null).ToArray();
                }

                var serializerType = hostTypes.First(t => !t.IsAbstract && typeof(Playnite.SDK.Data.IDataSerializer).IsAssignableFrom(t));
                serializer = Activator.CreateInstance(serializerType);
            }

            typeof(Playnite.SDK.Data.Serialization).GetField("serializer", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, serializer);
            if (closeReviewOnly) { CloseReviewTests(); return; }
            SdkSettingsEditing();
            EpicBetaChecks();
            ConnectionManagementChecks();
            ConnectionRemovalChecks();
            if (shutdownOnly)
            {
                ExitPluginTests();
                return;
            }

            if (badgeSettingsOnly)
            {
                Test("badge settings", CoverBadgeSettings);
                return;
            }

            if (resumeUiOnly)
            {
                Test("resumed replacement clears launching", SelectedResumeUiReplacement);
                Test("initial UI replacement clears launching", () => SelectedSerializedUiPlay(0));
                Test("rapid UI replacement keeps latest Play", () => SelectedSerializedUiPlay(1));
                Test("UI replacement refusal", () => SelectedSerializedUiPlay(2));
                Test("repeated UI Resume and close", () => SelectedSerializedUiPlay(3));
                Test("disable cancels pending UI replacement", () => SelectedSerializedUiPlay(4));
                Test("UI replacement SDK failure", () => SelectedSerializedUiPlay(5));
                Test("UI replacement waits for database cleanup", () => SelectedSerializedUiPlay(6));
                Test("late UI replacement cannot replace newer Play", () => SelectedSerializedUiPlay(7));
                return;
            }

            if (handoffOnly)
            {
                Test("native library handoff", SelectedPluginHandoff);
                Test("handoff confirmation/refusal", SelectedHandoffConfirmation);
                Test("rapid handoff", SelectedRapidHandoff);
                Test("UI handoff", SelectedUiHandoff);
                Test("updated/unsupported next game", SelectedHandoffNormalGame);
                Test("Pause cancels and Resume restarts through SDK", () => SelectedPluginUiResume());
                Test("paused missing counters retain SDK Resume in both modes", () => SelectedPluginUiResume(true));
                Test("resumed replacement clears launching", SelectedResumeUiReplacement);
                Test("browsing retains resumed completion", SelectedResumeUiCompletion);
                Test("paused close and replacement", SelectedPausedCloseAndReplacement);
                Test("late resumed callback", SelectedLateResume);
                Test("SDK restart failure stays visible", SelectedResumeFailure);
                Test("missing/running database game cannot restart", SelectedResumeDatabaseGuards);
                Test("late cancellation acknowledgement", SelectedSdkOldAcknowledgement);
                Test("same-game late acknowledgement", () => SelectedSdkOldAcknowledgement(true));
                for (int scenario = 0; scenario < 8; scenario++)
                {
                    int selected = scenario;
                    Test("serialized UI lifecycle " + scenario, () => SelectedSerializedUiPlay(selected));
                }

                return;
            }

            var migrated = Playnite.SDK.Data.Serialization.FromJson<PlayniteUpdateStatus.Settings>("{\"Enabled\":true,\"SteamEnabled\":false,\"UpdateControls\":false,\"AnikiIntegration\":true,\"DebugLogging\":true}");
            Eq(SpeedUnit.BytesPerSecond, migrated.SpeedUnit);
            Eq(true, migrated.CoverBadges);
            Eq(true, migrated.Enabled);
            string migratedJson = Playnite.SDK.Data.Serialization.ToJson(migrated);
            Eq(false, migratedJson.Contains("SteamEnabled"));
            Eq(false, migratedJson.Contains("UpdateControls"));
            Eq(false, migratedJson.Contains("AnikiIntegration"));
            Eq(false, migratedJson.Contains("DebugLogging"));
            var api = (IPlayniteAPI)new ApiProxy(typeof(IPlayniteAPI)).GetTransparentProxy();
            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                typeof(PlayniteUpdateStatus.Settings).GetMethod("Copy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(plugin.Settings, new object[] { migrated });
                plugin.ApplySettings();
                Eq(false, plugin.Settings.EpicEnabled);
                Eq(false, plugin.EpicCatalog.Current.Fresh);
                Eq(null, plugin.EpicCatalog.GetType().GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin.EpicCatalog));
                Eq(true, plugin.Monitor.ControlsEnabled);
                Eq(true, plugin.Properties.HasSettings);
                Eq(true, plugin.GetSettings(false) != null);
                Eq(true, plugin.GetSettingsView(false) != null);
                Eq(true, plugin.GetGameViewControl(new GetGameViewControlArgs { Name = "Status" }) is StatusControl);
                plugin.GetSettings(false).BeginEdit();
                plugin.Settings.PollingInterval = 1;
                List<string> errors;
                Eq(false, plugin.GetSettings(false).VerifySettings(out errors));
                plugin.GetSettings(false).CancelEdit();
                Eq(1000, plugin.Settings.PollingInterval);
                var preferencesView = plugin.GetSettingsView(false);
                var preferencesWindow = new TestWindow
                {
                    Content = preferencesView,
                    Width = 600,
                    Height = 700,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                try
                {
                    preferencesWindow.Show();
                    preferencesWindow.UpdateLayout();
                    var preferences = (StackPanel)preferencesView.FindName("GeneralSettingsPanel");
                    var units = preferences.Children.OfType<ComboBox>().Single(combo => combo.Name != "ExitActionInput");
                    units.SelectedIndex = 0;
                    units.SelectedIndex = 1;
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() =>
                    {
                    }));
                    Eq(SpeedUnit.BitsPerSecond, plugin.Settings.SpeedUnit);
                }
                finally
                {
                    preferencesWindow.Close();
                }

                plugin.GetSettings(false).EndEdit();
                Eq(SpeedUnit.BitsPerSecond, plugin.Monitor.SpeedUnit);
                var controlSession = new ControlSession();
                var controlId = Guid.NewGuid();
                plugin.Monitor.Start(new LaunchIdentity { GameId = controlId, Name = "Menu fixture" }, controlSession, 500);
                Until(() => plugin.Monitor.Current.CanPause);
                var menuItems = plugin.GetMainMenuItems(new GetMainMenuItemsArgs()).Where(item => item.Description == "Pause").ToArray();
                Eq(1, menuItems.Length);
                Eq("Pause", menuItems[0].Description);
                var viewState = plugin.Monitor.Current;
                Eq(true, plugin.Monitor.SetStatusDismissed(controlId, viewState.SessionId, true));
                var show = plugin.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(item => item.Description == "Show update status");
                show.Action(null);
                Eq(false, plugin.Monitor.Current.Dismissed);
                plugin.ApplySettings();
                Eq(true, plugin.Monitor.ControlsEnabled);
                Eq(1, plugin.GetMainMenuItems(new GetMainMenuItemsArgs()).Count(item => item.Description == "Pause"));
                var replacement = new ControlSession();
                plugin.Monitor.Start(new LaunchIdentity { GameId = Guid.NewGuid() }, replacement, 500);
                menuItems[0].Action(null);
                Eq(0, controlSession.Calls);
                Eq(0, replacement.Calls);
                var game = new Game
                {
                    Id = Guid.NewGuid(),
                    PluginId = SteamGameIdentity.LibraryId,
                    GameId = "550",
                    Name = "Smoke fixture"
                };
                var starting = Event<OnGameStartingEventArgs>(game);
                plugin.OnGameStarting(starting);
                Eq(false, starting.CancelStartup);
                Eq(game.Id, plugin.Monitor.Current.GameId);
                plugin.OnGameStartupCancelled(Event<OnGameStartupCancelledEventArgs>(game));
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
                plugin.OnApplicationStopped(new OnApplicationStoppedEventArgs());
                Eq(UpdatePhase.Idle, plugin.Monitor.Current.Phase);
            }

            using (var plugin = new PlayniteUpdateStatus.Plugin(api))
            {
                Eq(SpeedUnit.BitsPerSecond, plugin.Settings.SpeedUnit);
                Eq(SpeedUnit.BitsPerSecond, plugin.Monitor.SpeedUnit);
            }

            Test("normal Play callback never waits for a paused update or sends an extra command", () => NormalPlay(UpdatePhase.Updating));
            Test("a saved Steam update error cannot cancel the normal Play action", () => NormalPlay(UpdatePhase.Error));
            Test("confirmed close still returns to Fullscreen library without intercepting Play", CloseLibrary);
            Test("native Steam-library Pause/Resume retains Play and close cancels recovery", ResumeLaunchPlugin);
            Test("SDK close preserves forwarded/running tracking and uses public library navigation", SilentCloseGuards);
            Test("plugin starts/stops background catalog independently of game observation", PluginCatalogLifecycle);
            Test("selected Steam update retries saved error once and retains Pause/Resume through completion", SelectedSessionLifecycle);
            Test("selected update rejects stale, repeated, wrong-app and contradictory completion", SelectedCompletionEvidence);
            Test("selected completion requires replies received after selection acknowledgement", SelectedPreAcknowledgementCompletion);
            Test("failed Pause command cannot permanently block completed selected Play", SelectedPauseException);
            Test("catalog freshness rejects expired/future frames for startup and global-state merging", CatalogFreshness);
            Test("selected update refusal remains visible and never forwards Play", SelectedRefusal);
            Test("native library handoff cancels previous Play and releases only the new original action", SelectedPluginHandoff);
            Test("handoff waits for removal confirmation and rejects an unremoved previous update", SelectedHandoffConfirmation);
            Test("rapid A-B-C Play removes only the previous update and selects the latest game", SelectedRapidHandoff);
            Test("UI-thread handoff keeps dispatcher responsive until queue removal", SelectedUiHandoff);
            Test("playing an updated or unsupported game still removes the previous update", SelectedHandoffNormalGame);
            Test("Desktop selected-update close confirms pause and cancels original Play", () => SelectedPluginClose(ApplicationMode.Desktop));
            Test("Fullscreen selected-update close confirms pause and cancels original Play", () => SelectedPluginClose(ApplicationMode.Fullscreen));
            Test("library return waits for the SDK startup-cancel acknowledgement", () => SelectedSdkAckOrdering(false));
            Test("SDK acknowledgement before UI dismissal returns to Fullscreen once", () => SelectedSdkAckOrdering(true));
            Test("delayed cancelled-startup acknowledgement cannot close a replacement selection", SelectedSdkOldAcknowledgement);
            Test("delayed cancellation cannot close a replacement with the same game identity", () => SelectedSdkOldAcknowledgement(true));
            Test("disabling monitoring or shutting down cancels held Play without a later launch", SelectedSettingsStop);
            CloseReviewTests();
            ExitCoreTests();
            ExitPluginTests();
            Test("Pause cancels held startup and Resume restarts once after SDK cleanup, including completed updates", () => SelectedPluginUiResume());
            Test("paused missing counters retain SDK Resume in both modes", () => SelectedPluginUiResume(true));
            Test("synchronous UI SDK Resume cannot retain old launching while a replacement updates", SelectedResumeUiReplacement);
            Test("browsing retains the Resume intent and fresh completion releases one synchronous SDK Play", SelectedResumeUiCompletion);
            for (int scenario = 0; scenario < 8; scenario++)
            {
                int selected = scenario;
                Test("serialized UI lifecycle " + scenario, () => SelectedSerializedUiPlay(selected));
            }

            Test("paused startup cannot be revived after close or a replacement game", SelectedPausedCloseAndReplacement);
            Test("delayed resumed SDK startup cannot replace a newer game", SelectedLateResume);
            Test("failed SDK resume startup remains visible without an automatic retry", SelectedResumeFailure);
            Test("Resume declines a missing or already running database game without using event snapshots", SelectedResumeDatabaseGuards);
            Test("selected Steam command rejects unknown frontend and invalid arguments before loading", SelectedHelperGuards);
            Test("disabled Close preserves the original Play until normal update completion", DisabledClosePreservesSelectedPlay);
            Test("replacement during selection acknowledgement cannot restore the old startup", SelectedLateAcknowledgement);
            Test("no-counter completion uses only matching fresh catalog global state", SelectedCatalogGlobals);
            Test("Steam exit permanently interrupts selected Play instead of launching after reconnect", SelectedClientExit);
            Test("Steam cold bootstrap can hand over before IPC but exit/restart after connection cannot revive Play", SelectedColdBootstrap);
            Test("close is serialized after initial selection and cannot requeue it afterwards", SelectionCloseOrdering);
            Test("closing an unpolled selection cannot start its download or release Play", SelectionCloseBeforePoll);
            Test("cover settings migrate, cancel, save and toggle through the standard extension menu", CoverBadgeSettings);
            Test("plugin cover display refreshes independently of launch monitoring and cleans on shutdown", CoverBadgePluginLifecycle);
            Test("plugin saves cover history with badges hidden and reloads it across modes/restarts", CoverHistoryPluginReload);
            Test("manual Steam folder saves once across modes, validates paths and cancels edits", ManualSteamFolder);
            Test("Steam folder settings show detection and manual states, refresh and stop on close", SteamFolderStatusView);
            Test("first plugin load creates local Steam paths independently for fresh PCs without settings", FreshInstallationPaths);
            if (sdkOnly)
                Test("SDK integration does not load the private Playnite host assembly", () => Eq(false, AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Playnite")));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= resolve;
        }
    }

    private static T Event<T>(Game game)
        where T : new()
    {
        var result = new T();
        typeof(T).GetProperty("Game").SetValue(result, game, null);
        return result;
    }

    private static object LaunchCoordinatorFor(PlayniteUpdateStatus.Plugin plugin)
    {
        return typeof(PlayniteUpdateStatus.Plugin).GetField("launchCoordinator", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(plugin);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }

    private static void ConfigureSelection(PlayniteUpdateStatus.Plugin plugin, Dictionary<uint, SelectionFixture> fixtures)
    {
        plugin.Settings.PollingInterval = 500;
        SetField(LaunchCoordinatorFor(plugin), "selectionPreflight", new Func<LaunchIdentity, SteamCatalogSnapshot, bool>((id, catalog) => true));
        SetField(LaunchCoordinatorFor(plugin), "selectedSessionFactory", new Func<LaunchIdentity, IUpdateControlSession>(id => fixtures[uint.Parse(id.LauncherId)].Session));
        SetField(LaunchCoordinatorFor(plugin), "recoveryFactory", new Func<uint, Func<bool>, Action<string, bool>, SteamLaunchRecovery>((id, running, log) =>
        {
            throw new Exception("Selected updates must not capture/forward native launch");
        }));
    }

    private static Game SelectedGame(uint id)
    {
        return new Game
        {
            Id = Guid.NewGuid(),
            PluginId = SteamGameIdentity.LibraryId,
            GameId = id.ToString(),
            Name = "Selection fixture " + id,
            IsLaunching = true,
            IsInstalled = true
        };
    }

    private sealed class ApiCalls
    {
        public Dispatcher Dispatcher;
        public ApplicationMode Mode;
        public int Starts, LibraryReturns, FullscreenToggles;
        public FullscreenView FullscreenView;
        public Action OnFullscreenToggle;
        public Game DatabaseGame;
        public string DataPath;
        public string FolderChoice;
        public Action<Guid> OnStartGame;
        public Func<Guid, Game> OnGetGame;
    }

    private sealed class SdkJsonSerializer : RealProxy
    {
        private readonly Type json;
        public SdkJsonSerializer(string playnite) : base(typeof(Playnite.SDK.Data.IDataSerializer))
        {
            json = Assembly.LoadFrom(Path.Combine(playnite, "Newtonsoft.Json.dll")).GetType("Newtonsoft.Json.JsonConvert", true);
        }

        private string Serialize(object value)
        {
            return (string)json.GetMethod("SerializeObject", new[] { typeof(object) }).Invoke(null, new[] { value });
        }

        private object Deserialize(string value, Type type)
        {
            return json.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) }).Invoke(null, new object[] { value, type });
        }

        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            var method = (MethodInfo)call.MethodBase;
            object value;
            switch (method.Name)
            {
                case "ToJson":
                    value = Serialize(call.Args[0]);
                    break;
                case "FromJson":
                    value = Deserialize((string)call.Args[0], method.GetGenericArguments()[0]);
                    break;
                case "FromJsonFile":
                    value = Deserialize(File.ReadAllText((string)call.Args[0]), method.GetGenericArguments()[0]);
                    break;
                case "GetClone":
                    value = Deserialize(Serialize(call.Args[0]), method.GetGenericArguments().Last());
                    break;
                default:
                    throw new NotSupportedException("Unexpected SDK fixture serialization operation: " + method.Name);
            }

            return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
        }
    }

    private sealed class ApiProxy : RealProxy
    {
        private readonly ApiCalls calls;
        public ApiProxy(Type type, ApiCalls calls = null) : base(type)
        {
            this.calls = calls;
        }

        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            var method = (MethodInfo)call.MethodBase;
            Type result = method.ReturnType;
            object value = null;
            if (method.Name == "get_UIDispatcher")
                value = calls == null ? Dispatcher.CurrentDispatcher : calls.Dispatcher;
            else if (calls != null && method.Name == "get_Mode")
                value = calls.Mode;
            else if (calls != null && method.Name == "SwitchToLibraryView")
            {
                if (calls.Mode == ApplicationMode.Fullscreen)
                    throw new Exception("Desktop-only SDK method used in Fullscreen");
                calls.LibraryReturns++;
            }
            else if (calls != null && method.Name == "get_ActiveFullscreenView")
                value = calls.FullscreenView;
            else if (calls != null && method.Name == "ToggleFullscreenView")
            {
                calls.FullscreenToggles++;
                calls.FullscreenView = calls.FullscreenView == FullscreenView.Details ? FullscreenView.List : FullscreenView.Details;
                if (calls.OnFullscreenToggle != null)
                    calls.OnFullscreenToggle();
            }
            else if (calls != null && method.Name == "StartGame")
            {
                calls.Starts++;
                if (calls.OnStartGame != null)
                    calls.OnStartGame((Guid)call.Args[0]);
            }
            else if (calls != null && method.Name == "SelectFolder")
                value = calls.FolderChoice;
            else if (calls != null && method.Name == "Get" && result == typeof(Game) && call.Args.Length == 1)
                value = calls.OnGetGame != null ? calls.OnGetGame((Guid)call.Args[0]) : object.Equals(call.Args[0], calls.DatabaseGame == null ? Guid.Empty : calls.DatabaseGame.Id) ? calls.DatabaseGame : null;
            else if (method.Name == "get_ExtensionsDataPath")
                value = calls != null && calls.DataPath != null ? calls.DataPath : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "artifacts", "validation", "host-data"));
            else if (result.IsInterface)
                value = new ApiProxy(result, calls).GetTransparentProxy();
            else if (result != typeof(void) && result.IsValueType)
                value = Activator.CreateInstance(result);
            return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
        }
    }
}
