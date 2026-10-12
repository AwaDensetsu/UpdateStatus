using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

internal sealed class SteamDebugProcess
{
    internal uint Id { get; }
    internal int Session { get; }
    internal string Executable { get; }
    internal string CommandLine { get; }
    internal string User { get; }
    internal SteamDebugProcess(uint id, int session, string executable, string commandLine, string user)
    { Id = id; Session = session; Executable = executable; CommandLine = commandLine; User = user; }
}
internal static class SteamEndpointSelection
{
    internal static Dictionary<uint,int> Candidates(string root, int session, string user, IEnumerable<SteamDebugProcess> processes)
    {
        var candidates = new Dictionary<uint,int>();
        foreach (var process in processes)
        {
            if (process.User != user || string.IsNullOrEmpty(user)) continue;
            int? port = DebugPort(root,session,process);
            if (port.HasValue) candidates[process.Id] = port.Value;
        }
        return candidates;
    }
    internal static int? DebugPort(string root, int session, SteamDebugProcess process)
    {
        if (process.Id == 0 || process.Session != session || string.IsNullOrWhiteSpace(process.Executable)) return null;
        string path;
        try { path = Path.GetFullPath(process.Executable); }
        catch (ArgumentException) { return null; }
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path),"steamwebhelper.exe",StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(process.CommandLine ?? "", @"--remote-debugging-port[= ](\d{1,5})(?:\s|""|$)");
        int port;
        return match.Success && int.TryParse(match.Groups[1].Value,out port) && port >= 1 && port <= 65535 ? (int?)port : null;
    }
}
