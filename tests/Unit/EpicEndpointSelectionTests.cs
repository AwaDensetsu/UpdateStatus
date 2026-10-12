using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicEndpointSelectionTests
    {
        private const string MainPath = @"C:\Epic\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
        private const string HelperPath = @"C:\Epic\Launcher\Engine\Binaries\Win64\EpicWebHelper.exe";
        private static readonly Type ProcessType = typeof(EpicLocalAccess).Assembly.GetType("PlayniteUpdateStatus.Launchers.Epic.EpicProcessInfo");
        private static readonly Type ListenerType = typeof(EpicLocalAccess).Assembly.GetType("PlayniteUpdateStatus.Launchers.Epic.EpicListenerInfo");
        private static object Process(uint id, string path = MainPath, int session = 1, uint parent = 0, string command = "-cefdebug=50001") =>
            Activator.CreateInstance(ProcessType, new object[] { id, parent, session, path, command });
        private static object Socket(uint owner, int port = 50001, string address = "127.0.0.1") =>
            Activator.CreateInstance(ListenerType, new object[] { owner, port, address });
        private static Array Typed(Type type, object[] values)
        {
            var array = Array.CreateInstance(type, values.Length);
            for (int index = 0; index < values.Length; index++) array.SetValue(values[index], index);
            return array;
        }
        private static EpicEndpoint Select(object[] processes, object[] listeners) =>
            (EpicEndpoint)typeof(EpicLocalAccess).GetMethod("SelectEndpoint", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { MainPath, 1, Typed(ProcessType, processes), Typed(ListenerType, listeners) });
        private static IEnumerable<T> UnavailableListeners<T>()
        {
            yield return ThrowUnavailable<T>();
        }
        private static T ThrowUnavailable<T>()
        {
            throw new InvalidOperationException("Socket enumeration must not run without a valid candidate");
        }
        [TestCase("absent")]
        [TestCase("foreign-session")]
        [TestCase("foreign-installation")]
        [TestCase("ambiguous-main")]
        [TestCase("debugging-disabled")]
        [TestCase("orphaned-helper")]
        public void NoCandidateSkipsSocketEnumeration(string kind)
        {
            var processes = kind == "absent" ? new object[0] :
                kind == "foreign-session" ? new[] { Process(1,session:2) } :
                kind == "foreign-installation" ? new[] { Process(1,path:@"D:\Other\EpicGamesLauncher.exe") } :
                kind == "ambiguous-main" ? new[] { Process(1), Process(2) } :
                kind == "orphaned-helper" ? new[] { Process(1,command:""),
                    Process(2,HelperPath,parent:99,command:"--remote-debugging-port=50001") } :
                new[] { Process(1,command:"") };
            var deferred = typeof(EpicEndpointSelectionTests).GetMethod("UnavailableListeners",
                BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(ListenerType).Invoke(null,null);
            var endpoint = typeof(EpicLocalAccess).GetMethod("SelectEndpoint", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { MainPath, 1, Typed(ProcessType,processes), deferred });
            Assert.That(endpoint, Is.Null);
        }
        [Test]
        public void SelectedMainOwnsItsMatchingLoopbackPort()
        {
            var endpoint = Select(new[] { Process(1) }, new[] { Socket(1) });
            Assert.That(endpoint.Owner, Is.EqualTo(1)); Assert.That(endpoint.Port, Is.EqualTo(50001));
        }
        [Test]
        public void CurrentMainOwnsItsSameSessionCanonicalHelper()
        {
            var endpoint = Select(new[] { Process(1), Process(2, HelperPath, parent:1, command:"--remote-debugging-port=50001") }, new[] { Socket(2) });
            Assert.That(endpoint.Owner, Is.EqualTo(2));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void AnotherInstallationCannotChangeSelectionRegardlessOfEnumerationOrder(bool reverse)
        {
            var processes = new[] { Process(1), Process(3, @"D:\Other\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe") };
            var listeners = new[] { Socket(1), Socket(3) };
            if (reverse) { Array.Reverse(processes); Array.Reverse(listeners); }
            Assert.That(Select(processes, listeners).Owner, Is.EqualTo(1));
            Assert.That(Select(processes, new[] { Socket(3) }), Is.Null);
        }
        [Test]
        public void MultipleMainsInTheSelectedInstallationAreAmbiguous()
        { Assert.That(Select(new[] { Process(1), Process(2) }, new[] { Socket(1) }), Is.Null); }
        [TestCase(false)]
        [TestCase(true)]
        public void MultipleOwnedEndpointsAreRejectedRegardlessOfEnumerationOrder(bool reverse)
        {
            var processes = new[] { Process(1, command:""),
                Process(2, HelperPath, parent:1, command:"--remote-debugging-port=50001"),
                Process(3, HelperPath, parent:1, command:"--remote-debugging-port=50002") };
            var listeners = new[] { Socket(2), Socket(3,50002) };
            if (reverse) { Array.Reverse(processes); Array.Reverse(listeners); }
            Assert.That(Select(processes, listeners), Is.Null);
        }
        [TestCase("0.0.0.0")]
        [TestCase("::")]
        [TestCase("10.0.0.1")]
        public void APortAlsoBoundOutsideLoopbackCannotBeRead(string address)
        { Assert.That(Select(new[] { Process(1) }, new[] { Socket(1), Socket(1, address:address) }), Is.Null); }
        [Test]
        public void AnotherWindowsSessionCannotSupplyAMainOrHelper()
        {
            Assert.That(Select(new[] { Process(1,session:2) }, new[] { Socket(1) }), Is.Null);
            Assert.That(Select(new[] { Process(1), Process(2,HelperPath,session:2,parent:1,command:"--remote-debugging-port=50001") }, new[] { Socket(2) }), Is.Null);
        }
        [Test]
        public void OrphanedHelperCannotBeBorrowedByANewMain()
        { Assert.That(Select(new[] { Process(1), Process(2,HelperPath,parent:99,command:"--remote-debugging-port=50001") }, new[] { Socket(2) }), Is.Null); }
        [TestCase(@"C:\Epic\Launcher-other\Engine\Binaries\Win64\EpicWebHelper.exe")]
        [TestCase(@"C:\Epic\Launcher\Other\EpicWebHelper.exe")]
        public void HelperPathsMustBelongToTheExpectedLauncherLayout(string path)
        { Assert.That(Select(new[] { Process(1), Process(2,path,parent:1,command:"--remote-debugging-port=50001") }, new[] { Socket(2) }), Is.Null); }
        [Test]
        public void HelperCannotSupplyADifferentPortThanTheMainRequests()
        { Assert.That(Select(new[] { Process(1), Process(2,HelperPath,parent:1,command:"--remote-debugging-port=50002") }, new[] { Socket(2,50002) }), Is.Null); }
        [Test]
        public void ForeignOwnerAndWrongPortAreRejected()
        {
            Assert.That(Select(new[] { Process(1) }, new[] { Socket(99) }), Is.Null);
            Assert.That(Select(new[] { Process(1) }, new[] { Socket(1,50002) }), Is.Null);
        }
        [Test]
        public void DuplicateRowsForOneEndpointDoNotInventAmbiguity()
        { Assert.That(Select(new[] { Process(1) }, new[] { Socket(1), Socket(1) }).Owner, Is.EqualTo(1)); }
    }
}
