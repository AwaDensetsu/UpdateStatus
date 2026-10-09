using System;
using System.IO;
using System.Text;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamLocationStore
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly Action<string,bool> log;
        private string saved;
        public string Folder { get { lock (gate) return saved; } }
        public SteamLocationStore(string path, Action<string,bool> log)
        {
            this.path = path; this.log = log;
            try {
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 8192) throw new FormatException("Location data too large");
                var rows = File.ReadAllLines(path);
                if (rows.Length != 2 || rows[0] != "SteamLocation|1" || !SteamLocator.IsValidRoot(rows[1])) throw new FormatException("Invalid location");
                saved = SteamLocator.NormalizeRoot(rows[1]);
            }
            catch (IOException) { log("Stored Steam location unavailable; using discovery",true); }
            catch (UnauthorizedAccessException) { log("Stored Steam location inaccessible; using discovery",true); }
            catch (FormatException) { log("Invalid stored Steam location ignored",true); }
            catch (ArgumentException) { log("Invalid stored Steam path ignored",true); }
        }
        public void Remember(string folder)
        {
            if (!SteamLocator.IsValidRoot(folder)) return;
            folder = SteamLocator.NormalizeRoot(folder);
            lock (gate)
            {
                if (string.Equals(folder,saved,StringComparison.OrdinalIgnoreCase)) return;
                string temporary = path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    File.WriteAllLines(temporary,new[] { "SteamLocation|1",folder },new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
                    saved = folder;
                }
                catch (IOException) { log("Steam location could not be saved; discovery remains available",true); }
                catch (UnauthorizedAccessException) { log("Steam location save unavailable; discovery remains available",true); }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
    }
}
