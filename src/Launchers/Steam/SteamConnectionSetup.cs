using System;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Record only a marker created here. An existing/legacy marker is never adopted.
    public sealed class SteamConnectionSetup
    {
        private const uint CreateNew = 1, OpenExisting = 3, GenericRead = 0x80000000, GenericWrite = 0x40000000,
            DeleteAccess = 0x00010000, ShareRead = 1, NormalAttributes = 0x80;
        private const int FileDispositionInfo = 4;
        private readonly string root, marker, journal;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16384 };
        public SteamConnectionSetup(string root)
        {
            if (!SteamLocator.IsValidRoot(root)) throw new ArgumentException("Select a valid Steam installation folder first.");
            this.root = SteamLocator.NormalizeRoot(root);
            marker = Path.Combine(this.root,".cef-enable-remote-debugging");
            journal = Path.Combine(this.root,".update-status-connection.json");
        }
        public void Enable()
        {
            if (SteamUiAccess.Configured(root)) return;
            var previous = Load();
            if (previous != null && !OurRecord(previous)) throw new IOException("Existing Steam setup ownership was preserved.");
            using (var handle = OpenMarker(CreateNew)) // CREATE_NEW is atomic; never adopt a competing creation.
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 80 || error == 183) return;
                    ThrowOpenError(error);
                }
                try
                {
                    Save(new Ownership { Root = root, User = WindowsIdentity.GetCurrent().User.Value, Fingerprint = Fingerprint(handle) });
                }
                catch
                {
                    // The held handle cannot be renamed/replaced while recording ownership.
                    MarkForDeletion(handle);
                    throw;
                }
            }
        }
        public bool Remove()
        {
            var record = Load();
            if (!SteamUiAccess.Configured(root))
            { if (record != null && OurRecord(record)) File.Delete(journal); return true; }
            if (record == null || !OurRecord(record) || Directory.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) return false;
            using (var handle = OpenMarker(OpenExisting)) // OPEN_EXISTING, with no write/delete sharing.
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2) { File.Delete(journal); return true; }
                    ThrowOpenError(error);
                }
                NativeFileInfo info;
                if (!GetFileInformationByHandle(handle,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (info.SizeHigh != 0 || info.SizeLow != 0 || Fingerprint(info) != record.Fingerprint) return false;
                // Delete the verified object, rather than reopening a potentially changed path.
                MarkForDeletion(handle);
                File.Delete(journal);
                return true;
            }
        }
        private bool OurRecord(Ownership record) => string.Equals(record.Root,root,StringComparison.OrdinalIgnoreCase) && record.User == WindowsIdentity.GetCurrent().User.Value;
        private Ownership Load()
        {
            if (!File.Exists(journal)) return null;
            try
            {
                var info = new FileInfo(journal);
                if (info.Length > 16384) throw new IOException("Steam setup ownership record is invalid; the existing setup was kept.");
                var record = json.Deserialize<Ownership>(File.ReadAllText(journal));
                if (record == null || string.IsNullOrEmpty(record.Root) || string.IsNullOrEmpty(record.User) || string.IsNullOrEmpty(record.Fingerprint))
                    throw new IOException("Steam setup ownership record is invalid; the existing setup was kept.");
                return record;
            }
            catch (ArgumentException error) { throw new IOException("Steam setup ownership record is unreadable; the existing setup was kept.",error); }
        }
        private void Save(Ownership record)
        {
            string temporary = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary,json.Serialize(record));
                if (File.Exists(journal)) File.Replace(temporary,journal,null);
                else File.Move(temporary,journal);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private SafeFileHandle OpenMarker(uint creation) => CreateFile(marker,GenericRead | DeleteAccess | (creation == CreateNew ? GenericWrite : 0u),ShareRead,IntPtr.Zero,creation,NormalAttributes,IntPtr.Zero);
        private static void ThrowOpenError(int error)
        {
            if (error == 5) throw new UnauthorizedAccessException("Steam setup needs permission to change the installation folder.");
            throw new Win32Exception(error);
        }
        private static string Fingerprint(SafeFileHandle handle)
        {
            NativeFileInfo info;
            if (!GetFileInformationByHandle(handle,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Fingerprint(info);
        }
        private static string Fingerprint(NativeFileInfo info) => string.Format(CultureInfo.InvariantCulture,"{0}:{1}:{2}:{3}:{4}:{5}:{6}:{7}",
            info.Attributes,info.Volume,info.IndexHigh,info.IndexLow,info.CreatedHigh,info.CreatedLow,info.WrittenHigh,info.WrittenLow);
        private static void MarkForDeletion(SafeFileHandle handle)
        {
            var disposition = new Disposition { Delete = true };
            if (!SetFileInformationByHandle(handle,FileDispositionInfo,ref disposition,1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public sealed class Ownership
        {
            public string Root { get; set; }
            public string User { get; set; }
            public string Fingerprint { get; set; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInfo
        {
            public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh,
                Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out NativeFileInfo info);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref Disposition info,uint size);
    }
}
