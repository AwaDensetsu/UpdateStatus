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
    private static void ForegroundPolicy()
    {
        var now = new DateTime(2026, 10, 6);
        var state = new UpdateState
        {
            Phase = UpdatePhase.Updating
        };
        var policy = new ForegroundRestorePolicy();
        Eq(true, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now));
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddMilliseconds(750)));
        Eq(true, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(2)));
        Eq(true, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(4)));
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(6)));
        policy.Reset();
        Eq(true, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now));
        policy.RecordResult(true);
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(2)));
        policy.Reset();
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Other, false, now));
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(2)));
        policy.Reset();
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Frontend, false, now));
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now.AddSeconds(9)));
        policy.Reset();
        state.Display = new UpdateDisplay
        {
            Paused = true
        };
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now));
        state.Display.Paused = false;
        state.Display.WaitingForData = true;
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now));
        state.Display = null;
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, true, now));
        state.Phase = UpdatePhase.Launching;
        Eq(false, policy.ShouldAttempt(state, ForegroundOwner.Launcher, false, now));
    }

    private static void RegistryProviders()
    {
        var registry = new LauncherRegistry();
        var first = new LauncherRegistration("steam", new TestAdapter("Steam fixture"), g => g.GameId == "steam-fixture" ? new LaunchIdentity { GameId = g.Id, LauncherId = "550", Name = g.Name } : null, new[] { "steam" }, null, UpdateCapabilities.Phases);
        var second = new LauncherRegistration("future", new TestAdapter("Future launcher fixture"), g => g.GameId == "future-fixture" ? new LaunchIdentity { GameId = g.Id, LauncherId = "catalog-item", Name = g.Name } : null, new[] { "futureclient" }, null, UpdateCapabilities.Phases | UpdateCapabilities.DownloadBytes);
        registry.Register(first);
        registry.Register(second);
        var game = new Game
        {
            Id = Guid.NewGuid(),
            GameId = "future-fixture",
            Name = "Future game fixture"
        };
        var match = registry.Resolve(game, null);
        Eq("future", match.Identity.LauncherKey);
        Eq("catalog-item", match.Identity.LauncherId);
        Eq(game.Id, match.Identity.GameId);
        Eq("Future launcher fixture", match.Identity.LauncherName);
        using (var session = match.Registration.Adapter.Open(match.Identity))
            Eq(UpdatePhase.Updating, session.Poll().Phase);
        Eq((LauncherMatch)null, registry.Resolve(game, e => e.Key != "future"));
        Eq((LauncherMatch)null, registry.Resolve(new Game { GameId = "unsupported" }, null));
        bool duplicate = false;
        try
        {
            registry.Register(new LauncherRegistration("FUTURE", new TestAdapter("duplicate"), g => null, null, null, UpdateCapabilities.None));
        }
        catch (ArgumentException)
        {
            duplicate = true;
        }

        Eq(true, duplicate);
        var ambiguous = new LauncherRegistry();
        ambiguous.Register(second);
        ambiguous.Register(new LauncherRegistration("other", new TestAdapter("other"), g => new LaunchIdentity { GameId = g.Id, LauncherId = "different" }, null, null, UpdateCapabilities.Phases));
        Eq((LauncherMatch)null, ambiguous.Resolve(game, null));
    }

    private sealed class TestAdapter : ILauncherUpdateAdapter
    {
        public string Name { get; private set; }

        public TestAdapter(string name)
        {
            Name = name;
        }

        public IUpdateSession Open(LaunchIdentity identity)
        {
            return new FixedSession();
        }
    }
}
