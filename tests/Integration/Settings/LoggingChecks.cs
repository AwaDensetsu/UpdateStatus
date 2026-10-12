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
    private static void BasicErrorLogging()
    {
        var messages = new List<KeyValuePair<string, bool>>();
        using (var monitor = new UpdateMonitor((message, warning) =>
        {
            lock (messages)
                messages.Add(new KeyValuePair<string, bool>(message, warning));
        }))
        {
            var session = new StepSession(new[] { new UpdateObservation { Phase = UpdatePhase.Updating, Percent = 1 }, new UpdateObservation { Phase = UpdatePhase.Updating, Percent = 2 }, new UpdateObservation { Phase = UpdatePhase.Error, Detail = "Synthetic read failure" } });
            var id = Guid.NewGuid();
            monitor.Start(new LaunchIdentity { GameId = id, LauncherId = "550" }, session, 500);
            Until(() => monitor.Current.Percent == 1);
            session.Step = 1;
            Until(() => monitor.Current.Percent == 2);
            session.Step = 2;
            Until(() => monitor.Current.Phase == UpdatePhase.Error);
            monitor.Stop(id);
            lock (messages)
            {
                Eq(1, messages.Count(row => row.Key.StartsWith("Phase ") && row.Key.Contains("-> Updating")));
                Eq(true, messages.Any(row => row.Value && row.Key.Contains("Synthetic read failure")));
                Eq(false, messages.Any(row => row.Key.Contains(" raw:") || row.Key.StartsWith("Status ")));
            }
        }
    }
}
