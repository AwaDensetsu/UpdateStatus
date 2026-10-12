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
    private static string LibraryMetadata(params string[] paths)
    {
        return "libraryfolders { " + string.Join(" ", paths.Select((path, index) => index + " { path \"" + path.Replace("\\", "\\\\") + "\" }")) + " }";
    }

    private static void InvalidLibraryRows()
    {
        var paths = SteamLocator.ParseLibraries(@"C:\Steam", LibraryMetadata(@"C:\Steam", @"D:\bad|folder", @"D:relative", @"\relative", @"D:\Games\SteamLibrary"));
        Eq(2, paths.Count);
        Eq(@"C:\Steam", paths[0]);
        Eq(@"D:\Games\SteamLibrary", paths[1]);
    }

    private static void PartialLibraryMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "LibraryAudit-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        try
        {
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "libraryfolders { 0 { path \"");
            string manifest = Path.Combine(root, "steamapps", "appmanifest_550.acf");
            File.WriteAllText(manifest, Fixture(4, 0, 0, 0, 0));
            Eq(manifest, new SteamLocator().FindManifest(root, 550, null));
            Eq((uint)550, SteamCatalogDiscovery.Read(root, (s, d) =>
            {
            }).Single().AppId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void DuplicateManifestRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "DuplicateLibraryAudit-" + Guid.NewGuid());
        string secondary = Path.Combine(root, "Παιχνίδια 朋友");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(secondary, "steamapps"));
        try
        {
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), LibraryMetadata(root, secondary));
            string first = Path.Combine(root, "steamapps", "appmanifest_550.acf"), next = Path.Combine(secondary, "steamapps", "appmanifest_550.acf");
            File.WriteAllText(next, Fixture(4, 0, 0, 0, 0));
            foreach (string text in new[]
            {
                "AppState {",
                Fixture(4, 0, 0, 0, 0).Replace("550", "551")
            }

            )
            {
                File.WriteAllText(first, text);
                Eq(next, new SteamLocator().FindManifest(root, 550, null));
            }

            using (var locked = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Eq(next, new SteamLocator().FindManifest(root, 550, null));
            File.WriteAllText(first, Fixture(4, 0, 0, 0, 0));
            Eq(first, new SteamLocator().FindManifest(root, 550, null));
            Eq(next, new SteamLocator().FindManifest(root, 550, Path.Combine(secondary, "steamapps", "common", "Game")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
