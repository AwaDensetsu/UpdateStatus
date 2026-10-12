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
    private static int passed, failed;
    private sealed class TestWindow : Window
    {
        public TestWindow()
        {
            Left = -32000;
            Top = -32000;
            ShowActivated = false;
            ShowInTaskbar = false;
        }
    }

    private static void Test(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + e);
        }
    }

    private static void Eq<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("Line " + line + ": Expected " + expected + "; got " + actual);
    }

    private static void Render(FrameworkElement element, string name)
    {
        string outDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "artifacts", "validation"));
        Directory.CreateDirectory(outDir);
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(outDir, name)))
            encoder.Save(stream);
    }

    private static UpdateDisplay DisplayFixture()
    {
        return new UpdateDisplay
        {
            Downloaded = 3354187488,
            DownloadTotal = 5362895728,
            DownloadPercent = 62,
            Stage = "Updating files",
            WorkPercent = 93
        };
    }

    private static void TestTheme(Window window)
    {
        // A hostile global template must not erase our independently styled close glyph.
        window.Resources[typeof(Button)] = (Style)System.Windows.Markup.XamlReader.Parse("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border Background='Transparent'/></ControlTemplate></Setter.Value></Setter></Style>");
        window.Resources["FontMain"] = new System.Windows.Media.FontFamily("Segoe UI");
        window.Resources["Accent"] = System.Windows.Media.Colors.CornflowerBlue;
        window.Resources["TextColor"] = System.Windows.Media.Colors.White;
        window.Resources["TextSecondaryColor"] = System.Windows.Media.Colors.LightGray;
        window.Resources["ControlBackgroundColor"] = System.Windows.Media.Color.FromRgb(19, 22, 30);
        window.Resources["TextBrush"] = System.Windows.Media.Brushes.White;
    }

    private static ControlTemplate BareWindowTemplate()
    {
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Window'><ContentPresenter Content='{TemplateBinding Content}'/></ControlTemplate>");
    }

    private static void Until(Func<bool> condition)
    {
        for (int i = 0; i < 250; i++)
        {
            if (condition())
                return;
            Thread.Sleep(20);
        }

        throw new Exception("Timed out waiting for fixture state");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new Exception("Timed out awaiting UI lifecycle state");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    private sealed class StepSession : IUpdateSession
    {
        private readonly UpdateObservation[] observations;
        public volatile int Step;
        public volatile bool Disposed;
        public StepSession(UpdateObservation[] observations)
        {
            this.observations = observations;
        }

        public UpdateObservation Poll()
        {
            return observations[Step];
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class DisplaySession : IUpdateSession
    {
        public readonly UpdateDisplay Display = DisplayFixture();
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                Display = Display,
                Percent = Display.WorkPercent
            };
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedSession : IUpdateSession
    {
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating
            };
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingSession : IUpdateSession
    {
        public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        public UpdateObservation Poll()
        {
            Entered.Set();
            Release.WaitOne(2000);
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                Percent = 71
            };
        }

        public void Dispose()
        {
        }
    }

    private static IEnumerable<T> Visuals<T>(System.Windows.DependencyObject node)
        where T : System.Windows.DependencyObject
    {
        var match = node as T;
        if (match != null)
            yield return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var item in Visuals<T>(System.Windows.Media.VisualTreeHelper.GetChild(node, i)))
                yield return item;
    }
}
