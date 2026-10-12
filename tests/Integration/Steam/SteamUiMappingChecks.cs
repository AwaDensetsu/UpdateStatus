using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PlayniteUpdateStatus.Launchers.Steam;

internal static partial class Program
{
    private static void SteamUiMappingTests()
    {
        var helper = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamUpdateReader.exe"));
        var snapshotType = helper.GetType("SteamUiSnapshot", true);
        var deserialize = typeof(SteamJson).GetMethod("Deserialize").MakeGenericMethod(snapshotType);
        var map = helper.GetType("SteamUiClient", true).GetMethod("CreateSample", BindingFlags.NonPublic | BindingFlags.Static);
        Func<bool?, bool?, bool, SteamLiveSample> sample = (completed, installed, running) =>
        {
            var app = new Dictionary<string, object>
            {
                {
                    "AppId",
                    550u
                },
                {
                    "Installed",
                    installed
                },
                {
                    "ItemPresent",
                    completed.HasValue
                },
                {
                    "Completed",
                    completed
                },
                {
                    "ContentRequired",
                    true
                },
                {
                    "Active",
                    completed == false
                },
                {
                    "Paused",
                    false
                },
                {
                    "QueueIndex",
                    0
                },
                {
                    "WorkPercent",
                    93
                },
                {
                    "Download",
                    new
                    {
                        Done = 62,
                        Total = 100
                    }
                },
                {
                    "Staging",
                    new
                    {
                        Done = 93,
                        Total = 100
                    }
                }
            };
            var snapshot = deserialize.Invoke(null, new object[] { SteamJson.Serialize(new { Available = true, DownloadsEnabled = false, ActiveAppId = 550, State = "Updating", UpdateState = 97, WorkPercent = 93, Download = new { Done = 62, Total = 100 }, Staging = new { Done = 93, Total = 100 }, BytesPerSecond = 500000, Apps = new[] { app } }) });
            return SteamJson.Deserialize<SteamLiveSample>(SteamJson.Serialize(map.Invoke(null, new object[] { snapshot, 550u, new Func<uint, bool>(id => running), new Func<uint, uint, uint>((id, fallback) => 6u) })));
        };
        Test("Steam UI pending state vetoes completion despite installed flag", () =>
        {
            var value = sample(false, true, false);
            Eq(true, (value.InstallState & 2) != 0);
            Eq(62, SteamLivePresentation.Build(value).DownloadPercent);
            Eq(93, value.UiWorkPercent);
            Eq(false, value.DownloadsEnabled);
            Eq(550u, value.NetworkAppId);
        });
        Test("Steam UI disappearance uses manifest authority instead of inferring completion", () =>
        {
            Eq(6u, sample(null, true, false).InstallState);
        });
        Test("Steam UI completed item cannot override a pending manifest", () =>
        {
            Eq(6u, sample(true, true, false).InstallState);
        });
        Test("Steam UI running registry state suppresses pending controls", () =>
        {
            var value = sample(false, true, true);
            Eq(8196u, value.InstallState);
            Eq(false, value.HasCounters);
        });
        Test("paused UI work percentage stays separate from downloaded bytes", () =>
        {
            var value = sample(false, true, false);
            Eq(true, SteamLivePresentation.Build(value).Paused);
            Eq(93, SteamLivePresentation.Build(value).WorkPercent);
        });
    }
}
