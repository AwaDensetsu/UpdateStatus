using System;
using NUnit.Framework;
namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class SteamEndpointSelectionTests
    {
        [TestCase(2,@"C:\Steam\bin\steamwebhelper.exe","--remote-debugging-port=8080",true)]
        [TestCase(3,@"C:\Steam\bin\steamwebhelper.exe","--remote-debugging-port=8080",false)]
        [TestCase(2,@"C:\Steam-other\steamwebhelper.exe","--remote-debugging-port=8080",false)]
        [TestCase(2,@"C:\Steam\not-steam.exe","--remote-debugging-port=8080",false)]
        [TestCase(2,@"C:\Steam\bin\steamwebhelper.exe","--remote-debugging-port=0",false)]
        [TestCase(2,@"C:\Steam\bin\steamwebhelper.exe","--remote-debugging-port=65536",false)]
        [TestCase(2,@"C:\Steam\bin\steamwebhelper.exe","",false)]
        public void OnlyTheSelectedInstallationAndWindowsSessionCanSupplyAnEndpoint(int session, string path, string command, bool accepted)
        {
            var candidates = SteamEndpointSelection.Candidates(@"C:\Steam",2,"current",new[] { new SteamDebugProcess(11,session,path,command,"current") });
            Assert.That(candidates.ContainsKey(11),Is.EqualTo(accepted));
        }
        [TestCase("other")]
        [TestCase(null)]
        public void OtherOrUnknownUserCannotSupplyAnEndpointInTheSameSession(string user)
        {
            var candidates = SteamEndpointSelection.Candidates(@"C:\Steam",2,"current",new[] {
                new SteamDebugProcess(11,2,@"C:\Steam\steamwebhelper.exe","--remote-debugging-port=8080",user) });
            Assert.That(candidates,Is.Empty);
        }
        [Test]
        public void AForeignWindowsSessionCannotWinByEnumerationOrder()
        {
            var candidates = SteamEndpointSelection.Candidates(@"C:\Steam",2,"current",new[] {
                new SteamDebugProcess(10,3,@"C:\Steam\steamwebhelper.exe","--remote-debugging-port=8080","current"),
                new SteamDebugProcess(11,2,@"C:\Steam\steamwebhelper.exe","--remote-debugging-port=9090","current") });
            Assert.That(candidates.Keys,Is.EquivalentTo(new uint[] { 11 })); Assert.That(candidates[11],Is.EqualTo(9090));
        }
    }
}
