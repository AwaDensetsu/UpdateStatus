using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    internal static class SteamSignedFile
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo { public uint Size; public IntPtr Path, File, Subject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData {
            public uint Size; public IntPtr Policy, Sip;
            public uint UI, Revocation, Choice; public IntPtr File;
            public uint Action; public IntPtr State, URL;
            public uint Flags, UIContext; public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        public static byte[] Read(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            // Keep the file unchanged while trust and code are inspected. All
            // signature checks are offline and show no Windows prompts.
            using (var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if (stream.Length < 256 || stream.Length > 128*1024*1024) throw new NotSupportedException("Invalid Steam file size");
                var info = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = Marshal.StringToCoTaskMemUni(path) };
                IntPtr record = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
                Marshal.StructureToPtr(info,record,false);
                var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UI = 2, Choice = 1,
                    File = record, Action = 1, Flags = 0x1010 };
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                try {
                    if (WinVerifyTrust(new IntPtr(-1),ref action,ref data) != 0) throw new NotSupportedException("Steam file signature could not be verified offline");
                    using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path))) {
                        string publisher = certificate.GetNameInfo(X509NameType.SimpleName,false);
                        if (publisher != "Valve Corp." && publisher != "Valve Corporation") throw new NotSupportedException("Steam file is not signed by Valve");
                    }
                }
                finally {
                    data.Action = 2; WinVerifyTrust(new IntPtr(-1),ref action,ref data);
                    Marshal.FreeHGlobal(record); Marshal.FreeCoTaskMem(info.Path);
                }
                var bytes = new byte[checked((int)stream.Length)]; int read = 0;
                while (read < bytes.Length) { int count = stream.Read(bytes,read,bytes.Length-read); if (count == 0) throw new EndOfStreamException(); read += count; }
                return bytes;
            }
        }
    }
}
