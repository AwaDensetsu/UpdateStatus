using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamResolvedLayout
    {
        public uint Factory, ReleasePipe, CreatePipe, ConnectUser;
        public SteamPortableImage.Method Engine, AppState, UpdateInfo, DownloadsEnabled;
        public SteamPortableImage.Method SetDownloadsEnabled, DownloadStats, DownloadingApp, QueuePlacement, QueueIndex, QueueCount;
        public bool Network, Queue, Controls;
        public string ContentKey;
        public uint ManagerTable { get { return AppState.Table; } }
    }
    // Contracts describe calling convention, copy sizes and field operands.
    // Addresses, vtable slots and file versions are discovered, never prescribed.
    public static class SteamAbiResolver
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string,SteamResolvedLayout> cache = new Dictionary<string,SteamResolvedLayout>(StringComparer.OrdinalIgnoreCase);
        private const string Registration = "B77DD314AD4C5FE70795E8D8B09DD53DAC5054EF3A265198DB91D72A631E9618";
        public static SteamResolvedLayout Analyze(byte[] clientBytes, byte[] uiBytes)
        {
            var client = new SteamPortableImage(clientBytes); var ui = new SteamPortableImage(uiBytes);
            var result = new SteamResolvedLayout {
                Factory = client.CheckedExport("CreateInterface","595FF040C74690CD59AA0D275342DFE889F6B75F0C55BFB373B6FEE446997966"),
                CreatePipe = client.CheckedExport("Steam_CreateSteamPipe","D4CC5913DBE1E384A8F04036FE3330B5D38F109FDD5E96613C74B385E78D24C2"),
                ConnectUser = client.CheckedExport("Steam_ConnectToGlobalUser","E5262F591DFA9FC5B245AED16FB54FFF7D841ADF788F00E2D3A6A831B51993EF"),
                ReleasePipe = client.CheckedExport("Steam_BReleaseSteamPipe","E45DD593D3690226B5993A504C62F57884D00F72240CF9CE2F825B0D3077457A"),
                Engine = client.EngineGetter("B877948467950C205A6A87E82A797E785F23C1B1F9FB69F4426935192664FD34"),
                AppState = client.NamedMethod("GetAppInstallState","E6277A847394F1CA56D16DE06D78D9DEAD071613CF57457704F600A911EA58B0"),
                UpdateInfo = client.NamedMethod("GetUpdateInfo","D1CF6E1A77C58ADA814A0E3310FE86B5D281CFCCB3C4602B947330A166BC932A"),
                DownloadsEnabled = client.NamedMethod("BIsDownloadingEnabled","A2885CB9E005A36127B9ED38CB3A57756B66B3E8F8E17DBB6550FC51C46400BD")
            };
            SameTable(result,result.UpdateInfo,result.DownloadsEnabled);
            // Verify the actual serializer -> percent helper -> weighted work
            // calculation chain before interpreting the 248-byte reply.
            const string serializer = "EDC081EC6D2F70EAD9D2A82966E9F2855C42C5C6162AA01D4C0F7F81D29547F2";
            var progress = ui.NamedFunction("overall_percent_complete",serializer);
            if (ui.NamedFunction("overall_estimated_time_remaining_sec",serializer).Start != progress.Start) throw new NotSupportedException("Steam update fields disagree");
            var percent = ui.ContractFunction("C199D26FCA4A90D4235AF8EC0EFEBAA839F15493CFBD1FB0ECB59A28B60496EF",100,110);
            var work = ui.ContractFunction("ECC9C8EE4DF091A11A0A6298062B0598EB568348303214B507A5B3CA193E5E67",1360,1380);
            if (!ui.Calls(progress,percent) || !ui.Calls(percent,work)) throw new NotSupportedException("Steam progress call chain changed");
            try {
                result.DownloadingApp = client.NamedMethod("GetDownloadingAppID","C00F15870B0A8D86A490E146F24551BFF99C7E95AF138384CCEC35768ABB7DF6");
                result.QueueIndex = client.NamedMethod("GetAppDownloadQueueIndex","30EA9E8DBC5AD1211EBFF5E6CB1180EAD91468B6C632643612DA20F1691BFF3D");
                result.QueueCount = client.NamedMethod("GetNumAppsInDownloadQueue","B49AFDBAA61A909B30323CF75D83A8349673A489D8FB640429006DFB459427B6");
                SameTable(result,result.DownloadingApp,result.QueueIndex,result.QueueCount); result.Queue = true;
            } catch (NotSupportedException) { result.Queue = false; }
            try {
                result.DownloadStats = client.NamedMethod("GetDownloadStats","16D71A1F532B4A28C1A77FC7C249A320EA9F9FD40A8F689E9E448547A46E48D5");
                SameTable(result,result.DownloadStats);
                ui.NamedFunction("update_network_bytes_per_second","88A738DC6C5E827986EBA74671457919A6DE054007BC32DA178BBD039AFA398E",
                    "33E4C85492731BE0308ECBAC19206A96894ABCE8B6C8F845F297106516F97ECE");
                result.Network = result.Queue;
            } catch (NotSupportedException) { result.Network = false; }
            try {
                result.SetDownloadsEnabled = client.NamedMethod("SetDownloadingEnabled","1A64EAAE7F73C3FECC73536CBB2A6E2CF9EE21D30898714BDC06100DB3227A03");
                result.QueuePlacement = client.NamedMethod("ChangeAppDownloadQueuePlacement","C1FE914F15F1D2714D80A239E047A7B75BC50E9EBA5DE178AF6D9032687CFA9D");
                SameTable(result,result.SetDownloadsEnabled,result.QueuePlacement);
                var remove = ui.BindingCallback("Downloads.RemoveFromDownloadList",Registration,"7BF3088761DFB87BD9FA8FA8A914C1E9F6CEC301F1619B23C6A2F8CE1E7E2F9A");
                var removal = ui.VirtualImplementation(remove,"15E3C9DFD664545159F31C9F473EC261FB746B9EF890AC14E1F9DB2956BC980A",215,225);
                var enable = ui.BindingCallback("Downloads.EnableAllDownloads",Registration,"B6B732945A299A12C45A67C64FE34E6B61EC509F86514F37A72F2FD89A7A1232");
                var toggle = ui.VirtualImplementation(enable,"903C95A900D23E1FE0331843500713A751F9211805422E3C8E3FC0D496153764",50,60);
                if (toggle.Table != removal.Table) throw new NotSupportedException("Steam Downloads controls disagree");
                result.Controls = result.Queue;
            } catch (NotSupportedException) { result.Controls = false; }
            result.ContentKey = Hash(clientBytes)+":"+Hash(uiBytes);
            return result;
        }
        private static void SameTable(SteamResolvedLayout layout, params SteamPortableImage.Method[] methods)
        { if (methods.Any(method => method == null || method.Table != layout.ManagerTable)) throw new NotSupportedException("Steam methods use inconsistent proxy tables"); }
        private static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        public static SteamResolvedLayout Read(string root, bool requireControl)
        {
            try {
                // Hashes are cache keys only, not a whitelist of Steam versions.
                byte[] client = SteamSignedFile.Read(Path.Combine(root,"steamclient64.dll")), ui = SteamSignedFile.Read(Path.Combine(root,"SteamUI.dll"));
                string key = Hash(client)+":"+Hash(ui); SteamResolvedLayout result;
                lock (gate) {
                    if (!cache.TryGetValue(key,out result)) {
                        try { result = Analyze(client,ui); } catch (NotSupportedException) { result = null; } catch (FormatException) { result = null; }
                        if (cache.Count >= 8) cache.Clear(); cache[key] = result;
                    }
                }
                return result != null && (!requireControl || result.Controls) ? result : null;
            }
            catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
            catch (NotSupportedException) { return null; } catch (System.Security.Cryptography.CryptographicException) { return null; }
            catch (ArgumentException) { return null; } catch (OverflowException) { return null; }
        }
        public static bool SupportsControl(string root) { return Read(root,true) != null; }
    }
}
