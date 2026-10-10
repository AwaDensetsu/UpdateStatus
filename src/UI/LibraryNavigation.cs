using Playnite.SDK;

namespace PlayniteUpdateStatus.UI
{
    public static class LibraryNavigation
    {
        public static void Return(IPlayniteAPI api)
        {
            if (api.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                // Startup cancellation clears the host's status through its SDK
                // event. Only the public details/list toggle is needed here.
                if (api.MainView.ActiveFullscreenView == FullscreenView.Details) api.MainView.ToggleFullscreenView();
                return;
            }
            api.MainView.SwitchToLibraryView();
        }
    }
}
