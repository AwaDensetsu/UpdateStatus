using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Security.Principal;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class SteamCefTransport
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 32 };
    public static Uri FindEndpoint(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Steam installation unavailable");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int session;
        using (var current = Process.GetCurrentProcess()) session = current.SessionId;
        string user = WindowsIdentity.GetCurrent().User.Value;
        var observed = new List<SteamDebugProcess>();
        using (var search = new ManagementObjectSearcher("SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='steamwebhelper.exe'"))
        using (var processes = Get(search))
        {
            foreach (ManagementObject process in processes)
            using (process)
            {
                if (process["SessionId"] == null) continue;
                var candidate = new SteamDebugProcess(Convert.ToUInt32(process["ProcessId"]),Convert.ToInt32(process["SessionId"]),
                    process["ExecutablePath"] as string,process["CommandLine"] as string,null);
                if (!SteamEndpointSelection.DebugPort(root,session,candidate).HasValue) continue;
                try
                {
                    string owner = ProcessUserSid(candidate.Id);
                    if (owner != null) observed.Add(new SteamDebugProcess(candidate.Id,candidate.Session,candidate.Executable,candidate.CommandLine,owner));
                }
                catch (ManagementException) { } // Unverifiable ownership never authorizes an endpoint.
            }
        }
        var candidates = SteamEndpointSelection.Candidates(root,session,user,observed);
        Uri selected = null;
        uint selectedOwner = 0;
        if (candidates.Count == 0) throw new InvalidOperationException("No Steam UI debugging process available");
        using (var search = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT LocalAddress, LocalPort, OwningProcess FROM MSFT_NetTCPConnection WHERE State=2"))
        using (var listeners = Get(search))
        {
            foreach (ManagementObject listener in listeners)
            {
                using (listener)
                {
                    int expected;
                    uint owner = Convert.ToUInt32(listener["OwningProcess"], CultureInfo.InvariantCulture);
                    int port = Convert.ToInt32(listener["LocalPort"], CultureInfo.InvariantCulture);
                    
                    IPAddress address;
                    if (!candidates.TryGetValue(owner, out expected) || port != expected ||
                        !IPAddress.TryParse(listener["LocalAddress"] as string, out address) || !IPAddress.IsLoopback(address)) continue;
                    Uri target = FindSharedTarget(new UriBuilder("http", address.ToString(), port).Uri);
                    if (target == null) continue;
                    if (selected != null && (selectedOwner != owner || selected.Port != target.Port || selected.PathAndQuery != target.PathAndQuery))
                        throw new InvalidOperationException("Ambiguous Steam UI connection");
                    selected = target; selectedOwner = owner;
                }
            }
        }
        return selected ?? throw new InvalidOperationException("Steam-owned loopback connection unavailable");
    }

    private static string ProcessUserSid(uint id)
    {
        // Projected WMI rows need not contain an invokable instance path.
        // Bind to the process key before requesting ownership.
        using (var identity = new ManagementObject("Win32_Process.Handle='" + id.ToString(CultureInfo.InvariantCulture) + "'"))
        using (var owner = identity.InvokeMethod("GetOwnerSid",(ManagementBaseObject)null,null))
            return owner != null && Convert.ToUInt32(owner["ReturnValue"]) == 0 ? owner["Sid"] as string : null;
    }

    private static ManagementObjectCollection Get(ManagementObjectSearcher search)
    {
        search.Options.Timeout = TimeSpan.FromSeconds(2);
        return search.Get();
    }

    private static Uri FindSharedTarget(Uri server)
    {
        using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) })
        {
            var json = client.GetStringAsync(new Uri(server, "json/list")).GetAwaiter().GetResult();
            var targets = Json.DeserializeObject(json) as object[];
            
            if (targets == null) return null;
            foreach (var entry in targets.OfType<Dictionary<string, object>>())
            {
                object title, raw;
                if (!entry.TryGetValue("title", out title) || !Equals(title, "SharedJSContext") ||
                    !entry.TryGetValue("webSocketDebuggerUrl", out raw)) continue;
                Uri socket;
                if (Uri.TryCreate(raw as string, UriKind.Absolute, out socket) && socket.Scheme == "ws" &&
                    socket.Port == server.Port && (socket.Host == "localhost" ||
                        IPAddress.TryParse(socket.Host, out var address) && IPAddress.IsLoopback(address)))
                    return new UriBuilder(socket) { Host = server.Host }.Uri;
            }
            return null;
        }
    }

    public static string Evaluate(ClientWebSocket socket, string expression, int id)
    {
        var command = new Dictionary<string, object> {
            { "id", id }, { "method", "Runtime.evaluate" },
            { "params", new Dictionary<string, object> { { "expression", expression }, { "returnByValue", true }, { "awaitPromise", true } } }
        };
        byte[] request = Encoding.UTF8.GetBytes(Json.Serialize(command));
        using (var timeout = new CancellationTokenSource(4000))
        {
            socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
            var buffer = new byte[8192];
            while (true)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                        if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 1048576)
                            throw new InvalidDataException("Invalid Steam UI response");
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    var reply = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                    object requestId, result;
                    if (!reply.TryGetValue("id", out requestId) || Convert.ToInt32(requestId, CultureInfo.InvariantCulture) != id) continue;
                    if (!reply.TryGetValue("result", out result)) throw new InvalidDataException("Steam UI evaluation failed");
                    var evaluation = (Dictionary<string, object>)result;
                    if (evaluation.ContainsKey("exceptionDetails")) throw new InvalidOperationException("Steam download callback unavailable");
                    var value = (Dictionary<string, object>)evaluation["result"];
                    return value["value"] as string ?? throw new InvalidDataException("Missing Steam UI snapshot");
                }
            }
        }
    }
}
