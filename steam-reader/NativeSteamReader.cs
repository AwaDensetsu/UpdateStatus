using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PlayniteUpdateStatus.Launchers.Steam;

internal sealed class NativeSteamReader : IDisposable
{
    private readonly SteamResolvedLayout profile;
    private IntPtr module, manager;
    private int pipe;
    private PipeRelease release;
    private AppState state;
    private UpdateInfo update;
    private DownloadingEnabled downloadingEnabled;
    private CurrentDownload downloadingApp;
    private DownloadStats downloadStats;
    private DownloadQueueIndex queueIndex;
    private CurrentDownload queueCount;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Factory([MarshalAs(UnmanagedType.LPStr)] string version, IntPtr returnCode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PipeCreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UserConnect(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool PipeRelease(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AppManager(IntPtr self, int user, int pipe, IntPtr unusedVersion);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AppState(IntPtr self, uint appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool UpdateInfo(IntPtr self, uint appId, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool DownloadingEnabled(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CurrentDownload(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DownloadQueueIndex(IntPtr self, uint appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool DownloadStats(IntPtr self, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool SetDownloadingEnabled(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool QueuePlacement(IntPtr self, uint appId, int placement);
    public static bool SupportsControl(string root)
    { return SteamAbiResolver.SupportsControl(root); }
    public bool Control(bool pause)
    {
        ValidateControl();
        // Same as Steam Downloads' Pause/Resume: preserve the queue placement.
        return Method<SetDownloadingEnabled>(manager,profile.SetDownloadsEnabled)(manager,!pause);
    }
    public void ValidateControl() { if (!profile.Controls) throw new NotSupportedException("Steam controls unavailable"); Method<SetDownloadingEnabled>(manager,profile.SetDownloadsEnabled); }
    public void ValidateSelection() { ValidateControl(); ValidateQueue(); Method<QueuePlacement>(manager,profile.QueuePlacement); }
    public bool SelectUpdate(uint appId, bool enabled, bool active)
    {
        if (!active && !Method<QueuePlacement>(manager,profile.QueuePlacement)(manager,appId,1)) return false;
        return enabled || Control(false);
    }
    public bool RemoveUpdate(uint appId)
    { return Method<QueuePlacement>(manager,profile.QueuePlacement)(manager,appId,6); }
    public bool DownloadsEnabled() { return downloadingEnabled(manager); }
    public void ValidateQueue()
    {
        if (!profile.Queue) throw new NotSupportedException("Steam queue unavailable");
        downloadingApp = Method<CurrentDownload>(manager,profile.DownloadingApp);
        queueIndex = Method<DownloadQueueIndex>(manager,profile.QueueIndex);
        queueCount = Method<CurrentDownload>(manager,profile.QueueCount);
    }
    public uint DownloadingApp() { return downloadingApp(manager); }
    public uint QueueCount() { return queueCount(manager); }
    public int QueueIndex(uint appId) { return queueIndex(manager,appId); }
    public uint? NetworkSpeed(out uint appId)
    {
        appId = 0;
        if (downloadStats == null) return null;
        uint before = downloadingApp(manager);
        // SteamUI has a separate LAN hosting meter when no app is downloading.
        // Do not substitute that meter, or invent zero for an unavailable source.
        if (before == 0) return null;
        IntPtr output = Marshal.AllocHGlobal(540);
        try
        {
            byte[] bytes = new byte[540]; Marshal.Copy(bytes,0,output,bytes.Length);
            if (!downloadStats(manager,output) || downloadingApp(manager) != before) return null;
            Marshal.Copy(output,bytes,0,bytes.Length);
            appId = before;
            // Verified SteamUI network serializer reads uint32 DownloadStats +4.
            return BitConverter.ToUInt32(bytes,4);
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    public NativeSteamReader(string root)
    {
        if (IntPtr.Size != 8) throw new NotSupportedException("The Steam reader must run as x64");
        var processes = Process.GetProcessesByName("steam"); bool running = processes.Length > 0; foreach (var p in processes) p.Dispose();
        if (!running) throw new InvalidOperationException("Steam is not running; reader never starts it");
        string file = Path.GetFullPath(Path.Combine(root, "steamclient64.dll"));
        profile = SteamAbiResolver.Read(root,false);
        if (profile == null) throw new NotSupportedException("Unverified Steam client ABI; private calls refused");
        module = LoadLibraryEx(file, IntPtr.Zero, 0x1100);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        release = Export<PipeRelease>("Steam_BReleaseSteamPipe", profile.ReleasePipe);
        var factory = Export<Factory>("CreateInterface", profile.Factory);
        IntPtr engine = factory("CLIENTENGINE_INTERFACE_VERSION005", IntPtr.Zero);
        if (engine == IntPtr.Zero) throw new InvalidOperationException("Steam ClientEngine interface unavailable");
        if (Marshal.ReadIntPtr(engine).ToInt64() - module.ToInt64() != profile.Engine.Table) throw new NotSupportedException("Unexpected ClientEngine vtable");
        var getManager = Method<AppManager>(engine,profile.Engine);
        pipe = Export<PipeCreate>("Steam_CreateSteamPipe", profile.CreatePipe)();
        if (pipe == 0) throw new InvalidOperationException("Could not create read-only Steam IPC connection");
        int user = Export<UserConnect>("Steam_ConnectToGlobalUser", profile.ConnectUser)(pipe);
        if (user == 0) throw new InvalidOperationException("Steam global user unavailable");
        manager = getManager(engine, user, pipe, IntPtr.Zero);
        if (manager == IntPtr.Zero || Marshal.ReadIntPtr(manager).ToInt64() - module.ToInt64() != profile.ManagerTable) throw new NotSupportedException("Unexpected AppManager proxy");
        state = Method<AppState>(manager,profile.AppState);
        update = Method<UpdateInfo>(manager,profile.UpdateInfo);
        downloadingEnabled = Method<DownloadingEnabled>(manager,profile.DownloadsEnabled);
        if (profile.Network)
        {
            // Bind only after both client ABI and frontend field mapping are verified.
            downloadingApp = Method<CurrentDownload>(manager,profile.DownloadingApp);
            downloadStats = Method<DownloadStats>(manager,profile.DownloadStats);
        }
        Console.Error.WriteLine("Steam functions discovered and contracts verified; network=" + profile.Network + "; queue=" + profile.Queue + "; controls=" + profile.Controls);
    }
    private T Export<T>(string name, long expected) where T : class
    {
        IntPtr address = GetProcAddress(module, name);
        if (address == IntPtr.Zero || address.ToInt64() - module.ToInt64() != expected) throw new NotSupportedException("Unexpected export: " + name);
        return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }
    private T Method<T>(IntPtr instance, SteamPortableImage.Method method) where T : class
    {
        if (method == null) throw new NotSupportedException("Steam method unavailable");
        int slot = method.Slot; long expected = method.Address;
        IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * 8);
        if (address.ToInt64() - module.ToInt64() != expected) throw new NotSupportedException("Unexpected read-only method at slot " + slot);
        return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }
    public uint State(uint appId) { return state(manager, appId); }
    public byte[] Update(uint appId)
    {
        IntPtr output = Marshal.AllocHGlobal(248);
        try
        {
            byte[] bytes = new byte[248]; Marshal.Copy(bytes, 0, output, bytes.Length);
            if (!update(manager, appId, output)) return null;
            Marshal.Copy(output, bytes, 0, bytes.Length); return bytes;
        }
        finally { Marshal.FreeHGlobal(output); }
    }
    public void Dispose()
    {
        if (pipe != 0 && release != null) { release(pipe); pipe = 0; }
        // DLL remains loaded until this isolated process exits; no global Steam shutdown.
    }
}
