using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicTransportTests
    {
        private static async Task<string> ReadResponse(string response)
        {
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            var reply = Task.Run(async () => {
                using (var client = await server.AcceptTcpClientAsync())
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[4096];
                    await stream.ReadAsync(buffer,0,buffer.Length);
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(bytes,0,bytes.Length);
                }
            });
            try
            {
                using (var timeout = new CancellationTokenSource(2000))
                    return await EpicCefClient.ReadAsync(new EpicEndpoint(port, 0), timeout.Token);
            }
            finally { server.Stop(); await reply; }
        }
        private static string Response(string body) => "HTTP/1.1 200 OK\r\nContent-Length: " +
            Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;

        [Test]
        public void InvalidTargetListIsRejectedBeforeWebSocketEvaluation()
        {
            Assert.That(async () => await ReadResponse(Response("{}")), Throws.InstanceOf<InvalidDataException>());
        }
        [Test]
        public void ForeignTargetCannotRedirectEvaluation()
        {
            var body = "[{\"type\":\"page\",\"url\":\"https://launcher.store.epicgames.com/\",\"webSocketDebuggerUrl\":\"ws://192.168.1.2:9222/devtools/page/one\"}]";
            Assert.That(async () => await ReadResponse(Response(body)), Throws.InstanceOf<IOException>().With.Message.Contains("Epic connection is unavailable"));
        }
        [Test]
        public void OversizedHttpBodyIsRefusedFromHeaders()
        {
            Assert.That(async () => await ReadResponse("HTTP/1.1 200 OK\r\nContent-Length: 2097152\r\nConnection: close\r\n\r\n"),
                Throws.InstanceOf<InvalidDataException>());
        }
        [Test]
        public async Task UnresponsiveEndpointIsCancelledWithinTheCallerBudget()
        {
            var server = new TcpListener(IPAddress.Loopback,0);
            server.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            try
            {
                using (var timeout = new CancellationTokenSource(1000))
                {
                    var pending = EpicCefClient.ReadAsync(new EpicEndpoint(port,0), timeout.Token);
                    using (var accepted = await server.AcceptTcpClientAsync())
                    {
                        timeout.Cancel();
                        Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
                    }
                }
            }
            finally { server.Stop(); }
        }
    }
}
