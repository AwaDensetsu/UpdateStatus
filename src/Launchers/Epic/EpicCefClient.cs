using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public static class EpicCefClient
    {
        private static readonly string Expression = LoadExpression();
        private static string LoadExpression()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EpicUiBridge.js"))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
        public static bool IsAllowedSocket(Uri socket, int port) =>
            socket != null && socket.Scheme == "ws" && socket.Host == "127.0.0.1" && socket.Port == port &&
            string.IsNullOrEmpty(socket.UserInfo) && socket.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal);
        public static async Task<string> ReadAsync(EpicEndpoint endpoint, CancellationToken token)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 1048576 };
            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            using (var response = await client.GetAsync("http://127.0.0.1:" + endpoint.Port + "/json/list", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 1048576) throw new InvalidDataException("Oversized target list");
                string text;
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    text = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
                var targets = json.DeserializeObject(text) as object[];
                if (targets == null) throw new InvalidDataException("Invalid target list");
                Uri address = null;
                foreach (var item in targets)
                {
                    var target = item as Dictionary<string, object>;
                    object type, url, raw;
                    Uri page, socket;
                    if (target == null || !target.TryGetValue("type", out type) || !Equals(type, "page") ||
                        !target.TryGetValue("url", out url) || !Uri.TryCreate(url as string, UriKind.Absolute, out page) ||
                        page.Scheme != "https" || page.Host != "launcher.store.epicgames.com" ||
                        !target.TryGetValue("webSocketDebuggerUrl", out raw) ||
                        !Uri.TryCreate(raw as string, UriKind.Absolute, out socket) || !IsAllowedSocket(socket, endpoint.Port)) continue;
                    if (address != null) throw new InvalidDataException("Ambiguous Epic page");
                    address = socket;
                }
                if (address == null) throw new EpicConnectionException(EpicConnectionFailure.ConnectionUnavailable);
                token.ThrowIfCancellationRequested();
                using (var socket = new ClientWebSocket())
                {
                    socket.Options.Proxy = null;
                    await socket.ConnectAsync(address, token).ConfigureAwait(false);
                    var request = json.Serialize(new { id = 1, method = "Runtime.evaluate",
                        @params = new { expression = Expression, awaitPromise = true, returnByValue = true } });
                    var bytes = Encoding.UTF8.GetBytes(request);
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                    for (int count = 0; count < 32; count++)
                    {
                        var reply = json.DeserializeObject(await ReceiveAsync(socket, token).ConfigureAwait(false)) as Dictionary<string,object>;
                        object id;
                        if (reply == null || !reply.TryGetValue("id", out id) || !Equals(id, 1)) continue;
                        if (reply.ContainsKey("error")) throw new InvalidDataException("CDP rejected Epic read");
                        var result = reply["result"] as Dictionary<string,object>;
                        if (result == null || result.ContainsKey("exceptionDetails")) throw new EpicConnectionException(EpicConnectionFailure.InterfaceUnavailable);
                        var value = result["result"] as Dictionary<string,object>;
                        object raw;
                        if (value == null || !value.TryGetValue("value", out raw) || !(raw is string))
                            throw new InvalidDataException("Epic response unavailable");
                        return (string)raw;
                    }
                    throw new InvalidDataException("No matching CDP reply");
                }
            }
        }
        private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
        {
            using (var data = new MemoryStream())
            {
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    data.Write(buffer, 0, count);
                    if (data.Length > 1048576) throw new InvalidDataException("Oversized target list");
                }
                return Encoding.UTF8.GetString(data.ToArray());
            }
        }
        private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            using (var data = new MemoryStream())
            {
                var buffer = new byte[4096];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Unexpected CDP frame");
                    data.Write(buffer, 0, result.Count);
                    if (data.Length > 1048576) throw new InvalidDataException("Oversized CDP reply");
                } while (!result.EndOfMessage);
                return Encoding.UTF8.GetString(data.ToArray());
            }
        }
    }
}
