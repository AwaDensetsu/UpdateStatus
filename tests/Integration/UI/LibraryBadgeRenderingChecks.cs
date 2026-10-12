using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteUpdateStatus.UI;
using PlayniteUpdateStatus.Core;

internal static class LibraryBadgeRenderingChecks
{
    public static void Run()
    {
        var reused = new LibraryRowBadgeVisual();
        var updating = new CoverBadgeInfo(Guid.NewGuid(), "Updating", 93, new UpdateDisplay { Downloaded = 62, DownloadTotal = 100, WorkPercent = 93, Stage = "Updating files" });
        Compare(reused, updating, 16, 300, Brushes.White);
        // Constrain the actual drawing independently of the desired text width.
        // An ellipsis used on one render must not contaminate later measurements.
        Pixels(reused, 40);
        Compare(reused, updating, 16, 300, Brushes.White);
        Compare(reused, updating, 16, 55, Brushes.White);
        Compare(reused, updating, 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 80, DownloadTotal = 100, WorkPercent = 93, Stage = "Updating files" }), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 80, DownloadTotal = 200, WorkPercent = 93, Stage = "Updating files" }), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 100, DownloadTotal = 100, WorkPercent = 93, Stage = "Verifying files" }), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 100, DownloadTotal = 100, WorkPercent = 93, Stage = "Installing files" }), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 100, DownloadTotal = 100, WorkPercent = 93, Stage = "Finalizing" }), 16, 55, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Updating", 93, new UpdateDisplay { Downloaded = 62, DownloadTotal = 100, WorkPercent = 93, WaitingForData = true }), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Paused"), 16, 300, Brushes.White);
        Compare(reused, new CoverBadgeInfo(updating.GameId, "Update Required"), 16, 300, Brushes.White);
        Compare(reused, null, 16, 300, Brushes.White);
        Compare(reused, updating, 8, 300, Brushes.White);
        Compare(reused, updating, 40, 300, Brushes.Black);
        Compare(reused, updating, 16, 300, Brushes.White);
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            foreach (string culture in new[]
            {
                "el-GR",
                "ar-SA",
                "en-US"
            }

            )
            {
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
                Compare(reused, updating, 16, 300, Brushes.White);
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
        }
    }

    private static void Compare(LibraryRowBadgeVisual reused, CoverBadgeInfo state, double font, double available, Brush foreground)
    {
        var fresh = new LibraryRowBadgeVisual();
        double width = reused.Configure(state, font, available, foreground);
        double expected = fresh.Configure(state, font, available, foreground);
        if (width != expected || AutomationProperties.GetName(reused) != AutomationProperties.GetName(fresh))
            throw new Exception("Reused badge returned stale dimensions or accessibility text.");
        var actualPixels = Pixels(reused, Math.Max(1, width));
        if (actualPixels.Any(pixel => pixel != 0) != (state != null))
            throw new Exception("Badge must draw its state, or clear all pixels when absent.");
        if (!actualPixels.SequenceEqual(Pixels(fresh, Math.Max(1, expected))))
            throw new Exception("Reused badge drawing differs from a fresh badge.");
    }

    private static byte[] Pixels(LibraryRowBadgeVisual badge, double width)
    {
        badge.Width = width;
        badge.Height = 40;
        badge.Measure(new Size(width, 40));
        badge.Arrange(new Rect(0, 0, width, 40));
        badge.UpdateLayout();
        int pixelWidth = (int)Math.Ceiling(width);
        var bitmap = new RenderTargetBitmap(pixelWidth, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(badge);
        var pixels = new byte[pixelWidth * 40 * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        return pixels;
    }
}
