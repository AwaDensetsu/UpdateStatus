using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Read a fresh Windows process snapshot without constructing Process objects
    // and collecting their full performance data on every Steam status sample.
    internal static class SteamSessionDiscovery
    {
        // A process remains in its Windows session for its lifetime. Launcher IDs
        // are still enumerated afresh on every call; no Steam result is cached.
        private static readonly uint? WindowsSession = ReadWindowsSession();

        private static uint? ReadWindowsSession()
        {
            try
            {
                uint session;
                return ProcessIdToSessionId(GetCurrentProcessId(), out session) ? (uint?)session : null;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessEntry
        {
            internal uint SessionId;
            internal uint ProcessId;
            internal IntPtr ProcessName;
            internal IntPtr UserSid;
        }

        internal static bool TryRead(string executable, out string session)
        {
            session = null;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!WindowsSession.HasValue)
                    return false;

                int count;
                if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out buffer, out count))
                    return false;
                if (count < 0 || count > 0 && buffer == IntPtr.Zero)
                    return false;

                session = ReadBuffer(buffer, count, WindowsSession.Value, executable);
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            finally
            {
                if (buffer != IntPtr.Zero)
                    WTSFreeMemory(buffer);
            }
        }

        internal static string ReadBuffer(IntPtr buffer, int count, uint windowsSession, string executable)
        {
            var ids = new List<uint>();
            int stride = Marshal.SizeOf(typeof(ProcessEntry));
            for (int index = 0; index < count; index++)
            {
                var process = Marshal.PtrToStructure<ProcessEntry>(IntPtr.Add(buffer, index * stride));
                if (process.SessionId != windowsSession || process.ProcessName == IntPtr.Zero)
                    continue;
                string name = Marshal.PtrToStringUni(process.ProcessName);
                if (string.Equals(name, executable, StringComparison.OrdinalIgnoreCase))
                    ids.Add(process.ProcessId);
            }
            ids.Sort();
            var values = new string[ids.Count];
            for (int index = 0; index < ids.Count; index++)
                values[index] = ids[index].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", values);
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSEnumerateProcesses(IntPtr server, uint reserved, uint version,
            out IntPtr processes, out int count);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        private static extern void WTSFreeMemory(IntPtr memory);
    }
}
