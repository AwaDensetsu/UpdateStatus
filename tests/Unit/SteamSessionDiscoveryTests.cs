using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class SteamSessionDiscoveryTests
    {
        private static string Read(params SteamSessionDiscovery.ProcessEntry[] entries)
        {
            int size = Marshal.SizeOf(typeof(SteamSessionDiscovery.ProcessEntry));
            IntPtr buffer = Marshal.AllocHGlobal(size * entries.Length);
            try
            {
                for (int index = 0; index < entries.Length; index++)
                    Marshal.StructureToPtr(entries[index], IntPtr.Add(buffer, index * size), false);
                return SteamSessionDiscovery.ReadBuffer(buffer, entries.Length, 7, "steam.exe");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [TestCase("steam.exe", true)]
        [TestCase("STEAM.EXE", true)]
        [TestCase("steamwebhelper.exe", false)]
        [TestCase("steam", false)]
        [TestCase("other-steam.exe", false)]
        [TestCase("steam.exe.old", false)]
        [TestCase("Παιχνίδια.exe", false)]
        [TestCase(null, false)]
        public void OnlyTheExactSteamExecutableSuppliesSessionIdentity(string executable, bool matches)
        {
            IntPtr name = executable == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(executable);
            try
            {
                var row = new SteamSessionDiscovery.ProcessEntry { ProcessId = 42, SessionId = 7, ProcessName = name };
                Assert.That(Read(row), Is.EqualTo(matches ? "42" : ""));
            }
            finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); }
        }

        [Test]
        public void ForeignWindowsSessionsAreExcludedAndLocalIdsRemainSorted()
        {
            IntPtr name = Marshal.StringToHGlobalUni("steam.exe");
            try
            {
                Assert.That(Read(
                    new SteamSessionDiscovery.ProcessEntry { ProcessId = 4, SessionId = 8, ProcessName = name },
                    new SteamSessionDiscovery.ProcessEntry { ProcessId = 99, SessionId = 7, ProcessName = name },
                    new SteamSessionDiscovery.ProcessEntry { ProcessId = 13, SessionId = 7, ProcessName = name }), Is.EqualTo("13,99"));
            }
            finally { Marshal.FreeHGlobal(name); }
        }

        [Test]
        public void EmptySnapshotMeansTheLauncherIsClosed()
        {
            Assert.That(SteamSessionDiscovery.ReadBuffer(IntPtr.Zero, 0, 7, "steam.exe"), Is.Empty);
        }

        [Test]
        public void SuccessiveSnapshotsExposeExitAndReplacementImmediately()
        {
            IntPtr name = Marshal.StringToHGlobalUni("steam.exe");
            try
            {
                var row = new SteamSessionDiscovery.ProcessEntry { ProcessId = 13, SessionId = 7, ProcessName = name };
                Assert.That(Read(row), Is.EqualTo("13"));
                Assert.That(Read(), Is.Empty);
                row.ProcessId = 99;
                Assert.That(Read(row), Is.EqualTo("99"));
            }
            finally { Marshal.FreeHGlobal(name); }
        }

        [Test]
        public void NativeRowUsesTheCorrectPointerAlignment()
        {
            Assert.That(Marshal.SizeOf(typeof(SteamSessionDiscovery.ProcessEntry)), Is.EqualTo(IntPtr.Size == 8 ? 24 : 16));
            Assert.That(Marshal.OffsetOf(typeof(SteamSessionDiscovery.ProcessEntry), "ProcessName").ToInt32(), Is.EqualTo(8));
        }
    }
}
