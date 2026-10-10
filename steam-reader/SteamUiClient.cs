using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using PlayniteUpdateStatus.Launchers.Steam;

internal sealed class SteamUiCounter
{
    public ulong? Done { get; set; }
    public ulong? Total { get; set; }
    public bool Valid { get { return Done.HasValue && Total.HasValue && Done <= Total; } }
}
internal sealed class SteamUiApp
{
    public uint AppId { get; set; }
    public bool? Installed { get; set; }
    public bool ItemPresent { get; set; }
    public bool? Completed { get; set; }
    public bool? Paused { get; set; }
    public bool? Active { get; set; }
    public bool? ContentRequired { get; set; }
    public int? QueueIndex { get; set; }
    public uint? Result { get; set; }
    public uint? SourceBuild { get; set; }
    public uint? TargetBuild { get; set; }
    public int? WorkPercent { get; set; }
    public int? SecondsRemaining { get; set; }
    public SteamUiCounter Download { get; set; }
    public SteamUiCounter Staging { get; set; }
}
internal sealed class SteamUiSnapshot
{
    public bool Available { get; set; }
    public bool Controls { get; set; }
    public string Command { get; set; }
    public bool? DownloadsEnabled { get; set; }
    public uint? ActiveAppId { get; set; }
    public uint? QueueCount { get; set; }
    public uint? UpdateState { get; set; }
    public string State { get; set; }
    public bool NonGameContent { get; set; }
    public SteamUiCounter Download { get; set; }
    public SteamUiCounter Staging { get; set; }
    public int? WorkPercent { get; set; }
    public uint? BytesPerSecond { get; set; }
    public int? SecondsRemaining { get; set; }
    public SteamUiApp[] Apps { get; set; }
}
internal sealed class SteamUiClient : IDisposable
{
    private readonly ClientWebSocket socket = new ClientWebSocket();
    private readonly string script, root;
    private readonly SteamLocator locator = new SteamLocator();
    private int sequence;
    private readonly Dictionary<uint, string> manifests = new Dictionary<uint, string>();

    public SteamUiClient(string root)
    {
        if (!SteamLocator.IsValidRoot(root)) throw new NotSupportedException("Steam UI connection unavailable: invalid installation");
        this.root = root;
        socket.Options.Proxy = null;
        using (var timeout = new CancellationTokenSource(3000))
            socket.ConnectAsync(SteamCefTransport.FindEndpoint(root), timeout.Token).GetAwaiter().GetResult();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamReader.SteamUiBridge.js"))
        using (var reader = new StreamReader(stream)) script = reader.ReadToEnd();
    }

    public SteamUiSnapshot Read(uint[] ids, string action = null, uint selectedApp = 0)
    {
        string expression = script + "(" + SteamJson.Serialize(ids) + "," + SteamJson.Serialize(action) + "," + selectedApp.ToString(CultureInfo.InvariantCulture) + ")";
        var snapshot = SteamJson.Deserialize<SteamUiSnapshot>(SteamCefTransport.Evaluate(socket, expression, ++sequence));
        if (snapshot == null || !snapshot.Available || snapshot.Apps == null ||
            !snapshot.Apps.Select(app => app.AppId).OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id)) ||
            snapshot.QueueCount > 100000 || snapshot.WorkPercent < 0 || snapshot.WorkPercent > 100)
            throw new NotSupportedException("Steam UI download data unavailable");
        return snapshot;
    }

    public SteamLiveSample Sample(SteamUiSnapshot snapshot, uint id)
    { return CreateSample(snapshot, id, SteamLocator.IsAppRunning, ManifestState); }

    internal static SteamLiveSample CreateSample(SteamUiSnapshot snapshot, uint id, Func<uint, bool> isRunning, Func<uint, uint, uint> manifestState)
    {
        var app = snapshot.Apps.Single(row => row.AppId == id);
        var sample = new SteamLiveSample { AppId = id, DownloadsEnabled = snapshot.DownloadsEnabled,
            SourceBuild = app.SourceBuild ?? 0, TargetBuild = app.TargetBuild ?? 0, Result = app.Result ?? 0 };
        if (isRunning(id)) { sample.InstallState = 8196; return sample; }
        uint installed = app.Installed == true ? 4u : 1u;
        bool active = snapshot.ActiveAppId == id && snapshot.State != "None";
        if (app.ItemPresent && app.Completed == false && app.ContentRequired != false) {
            sample.InstallState = installed | 2u;
            if (app.QueueIndex >= 0) sample.InstallState |= 8;
            if (app.Active == true || active) sample.InstallState |= 256;
            if (app.Paused == true) sample.InstallState |= 512;
        } else if (app.Completed == true && app.Installed == true) {
            sample.InstallState = manifestState(id, 4);
        } else if (!app.ItemPresent) {
            sample.InstallState = manifestState(id, 0);
        } else sample.InstallState = 0;
        if (active && !snapshot.NonGameContent) {
            sample.UpdateState = snapshot.UpdateState ?? (snapshot.State == "None" ? 0u : 2048u);
            ApplyCounters(sample, snapshot.Download, snapshot.Staging);
            sample.UiWorkPercent = snapshot.WorkPercent;
            sample.SecondsRemaining = snapshot.SecondsRemaining ?? -1;
            sample.NetworkBytesPerSecond = snapshot.BytesPerSecond;
            sample.NetworkAppId = sample.NetworkBytesPerSecond.HasValue ? id : 0;
        } else if (app.ItemPresent && app.Completed == false) {
            ApplyCounters(sample, app.Download, app.Staging);
            sample.UiWorkPercent = app.WorkPercent >= 0 && app.WorkPercent <= 100 ? app.WorkPercent : null;
            sample.SecondsRemaining = app.SecondsRemaining ?? -1;
        }
        return sample;
    }

    private uint ManifestState(uint id, uint fallback)
    {
        try {
            string path;
            if (!manifests.TryGetValue(id, out path) || !File.Exists(path)) {
                path = locator.FindManifest(root, id, null);
                manifests[id] = path;
            }
            if (path == null) return fallback;
            var manifest = SteamManifestParser.Parse(SteamManifestParser.ReadShared(path), id);
            return manifest.KnownFlags && manifest.Flags <= uint.MaxValue ? (uint)manifest.Flags.Value : 0;
        } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } catch (FormatException) { return 0; }
    }

    private static void ApplyCounters(SteamLiveSample sample, SteamUiCounter download, SteamUiCounter staging)
    {
        if (download == null || staging == null || !download.Valid || !staging.Valid ||
            download.Total == 0 && staging.Total == 0) return;
        sample.HasCounters = true;
        sample.Downloaded = download.Done.Value; sample.DownloadTotal = download.Total.Value;
        sample.Staged = staging.Done.Value; sample.StageTotal = staging.Total.Value;
    }

    public void Dispose() { socket.Abort(); socket.Dispose(); }
}
