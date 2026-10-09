using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Static file inspection only: this class never loads or calls Steam code.
    public sealed class SteamPortableImage
    {
        public sealed class Function { public uint Start, End; }
        public sealed class Method { public uint Address, Table; public int Slot; }
        private sealed class Section { public uint Address, VirtualSize, Offset, Size; public bool Code; }
        private readonly byte[] bytes;
        private readonly List<Section> sections = new List<Section>();
        private readonly List<Function> functions = new List<Function>();
        public ulong ImageBase { get; private set; }
        public uint ImageSize { get; private set; }
        private readonly uint exportDirectory;
        public SteamPortableImage(byte[] data)
        {
            bytes = data;
            if (data == null || data.Length < 256 || data.Length > 128*1024*1024 || U16(0) != 0x5a4d) throw new FormatException("Invalid PE file");
            uint header = U32(0x3c);
            if (U32(header) != 0x4550 || U16(header+4) != 0x8664 || U16(header+24) != 0x20b) throw new FormatException("Unsupported PE machine");
            int count = U16(header+6); if (count < 1 || count > 96) throw new FormatException("Invalid sections");
            uint optional = header+24;
            ImageBase = U64(optional+24); ImageSize = U32(optional+56);
            if (ImageSize == 0 || ImageSize > 512*1024*1024) throw new FormatException("Invalid image size");
            exportDirectory = U32(optional+112);
            uint exceptions = U32(optional+112+3*8), exceptionBytes = U32(optional+116+3*8);
            uint sectionHeader = optional+U16(header+20);
            for (int i = 0; i < count; i++) {
                uint site = sectionHeader+(uint)i*40;
                var section = new Section { VirtualSize = U32(site+8), Address = U32(site+12), Size = U32(site+16), Offset = U32(site+20), Code = (U32(site+36)&0x20000000) != 0 };
                if ((ulong)section.Offset+section.Size > (ulong)bytes.Length || (ulong)section.Address+section.VirtualSize > ImageSize) throw new FormatException("Invalid section range");
                sections.Add(section);
            }
            if (exceptionBytes == 0 || exceptionBytes%12 != 0 || exceptionBytes/12 > 200000) throw new FormatException("Missing function boundaries");
            for (uint i = 0; i < exceptionBytes; i += 12) {
                uint site = Offset(exceptions+i,12);
                var function = new Function { Start = U32(site), End = U32(site+4) };
                if (function.End <= function.Start || !IsCode(function.Start) || !IsCode(function.End-1)) throw new FormatException("Invalid function range");
                functions.Add(function);
            }
            functions.Sort((a,b) => a.Start.CompareTo(b.Start));
        }
        private void Bound(uint offset, uint length)
        { if ((ulong)offset+length > (ulong)bytes.Length) throw new FormatException("Truncated PE data"); }
        private ushort U16(uint offset) { Bound(offset,2); return BitConverter.ToUInt16(bytes,(int)offset); }
        private uint U32(uint offset) { Bound(offset,4); return BitConverter.ToUInt32(bytes,(int)offset); }
        private ulong U64(uint offset) { Bound(offset,8); return BitConverter.ToUInt64(bytes,(int)offset); }
        private uint Offset(uint address, uint size)
        {
            foreach (var section in sections) if (address >= section.Address && (ulong)address+size <= (ulong)section.Address+section.Size)
                return section.Offset+address-section.Address;
            throw new FormatException("PE address is outside file data");
        }
        public bool IsCode(uint address)
        { return sections.Any(section => section.Code && address >= section.Address && (ulong)address < (ulong)section.Address+section.VirtualSize); }
        public int FileOffset(uint address, int size)
        { if (size < 0) throw new ArgumentOutOfRangeException("size"); return checked((int)Offset(address,(uint)size)); }
        public Function FindFunction(uint address)
        {
            int low = 0, high = functions.Count-1;
            while (low <= high) { int mid = (low+high)/2; var fn = functions[mid]; if (address < fn.Start) high = mid-1; else if (address >= fn.End) low = mid+1; else return fn; }
            return null;
        }
        private byte[] CodeBytes(Function function)
        {
            uint size = function.End-function.Start, offset = Offset(function.Start,size);
            var result = new byte[size]; Array.Copy(bytes,offset,result,0,size); return result;
        }
        public IEnumerable<Instruction> Instructions(Function fn)
        {
            var data = CodeBytes(fn); var decoder = Decoder.Create(64,data,ImageBase+fn.Start,DecoderOptions.None);
            while (decoder.IP < ImageBase+fn.End) {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID || instruction.Length == 0 || instruction.NextIP > ImageBase+fn.End) throw new FormatException("Invalid instruction boundary");
                yield return instruction;
            }
        }
        public string Fingerprint(Function fn)
        {
            var data = CodeBytes(fn); var decoder = Decoder.Create(64,data,ImageBase+fn.Start,DecoderOptions.None);
            using (var output = new MemoryStream()) {
                while (decoder.IP < ImageBase+fn.End) {
                    var instruction = decoder.Decode();
                    if (instruction.Code == Code.INVALID || instruction.Length == 0 || instruction.NextIP > ImageBase+fn.End) throw new FormatException("Invalid fingerprint instruction");
                    if (instruction.Mnemonic == Mnemonic.Nop || instruction.Mnemonic == Mnemonic.Int3) continue;
                    int start = checked((int)(instruction.IP-ImageBase-fn.Start));
                    var part = new byte[instruction.Length]; Array.Copy(data,start,part,0,part.Length);
                    var offsets = decoder.GetConstantOffsets(ref instruction);
                    if (instruction.IsIPRelativeMemoryOperand) Array.Clear(part,offsets.DisplacementOffset,offsets.DisplacementSize);
                    bool branch = instruction.Op0Kind == OpKind.NearBranch16 || instruction.Op0Kind == OpKind.NearBranch32 || instruction.Op0Kind == OpKind.NearBranch64;
                    if (branch && (instruction.NearBranchTarget < ImageBase+fn.Start || instruction.NearBranchTarget >= ImageBase+fn.End))
                        Array.Clear(part,offsets.ImmediateOffset,offsets.ImmediateSize);
                    for (int op = 0; op < instruction.OpCount; op++) {
                        if (instruction.GetOpKind(op) == OpKind.Immediate64 && instruction.Immediate64 >= ImageBase && instruction.Immediate64 < ImageBase+ImageSize)
                            Array.Clear(part,offsets.ImmediateOffset,offsets.ImmediateSize);
                    }
                    output.Write(part,0,part.Length);
                }
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(output.ToArray())).Replace("-", "");
            }
        }
        private IList<uint> StringSites(string name)
        {
            byte[] needle = Encoding.ASCII.GetBytes(name+"\0"); var found = new List<uint>();
            foreach (var section in sections.Where(section => !section.Code)) {
                int end = checked((int)(section.Offset+section.Size))-needle.Length;
                for (int at = (int)section.Offset; at <= end; at++) {
                    if (bytes[at] != needle[0]) continue;
                    int i = 1; while (i < needle.Length && bytes[at+i] == needle[i]) i++;
                    if (i == needle.Length) found.Add(section.Address+(uint)at-section.Offset);
                }
            }
            return found;
        }
        private IList<Function> StringReferences(string name)
        {
            var sites = new HashSet<uint>(StringSites(name)); var result = new Dictionary<uint,Function>();
            foreach (var section in sections.Where(section => section.Code)) {
                int end = checked((int)(section.Offset+section.Size))-7;
                for (int at = (int)section.Offset; at <= end; at++) {
                    if ((bytes[at] != 0x48 && bytes[at] != 0x4c) || (bytes[at+1] != 0x8d && bytes[at+1] != 0x8b) || (bytes[at+2]&0xc7) != 5) continue;
                    uint site = section.Address+(uint)at-section.Offset;
                    long target = (long)site+7+BitConverter.ToInt32(bytes,at+3);
                    if (target < 0 || target > uint.MaxValue || !sites.Contains((uint)target)) continue;
                    var fn = FindFunction(site);
                    if (fn != null && fn.End-fn.Start <= 2048) result[fn.Start] = fn;
                }
            }
            return result.Values.ToList();
        }
        private IEnumerable<uint> PointerSites(uint address)
        {
            ulong value = ImageBase+address;
            foreach (var section in sections.Where(section => !section.Code)) {
                uint end = section.Offset+section.Size;
                for (uint at = section.Offset; at+8 <= end; at += 8) if (U64(at) == value) yield return section.Address+at-section.Offset;
            }
        }
        private bool ReadPointer(uint address, out uint target)
        {
            target = 0;
            try { ulong value = U64(Offset(address,8)); if (value < ImageBase || value-ImageBase > uint.MaxValue) return false; target = (uint)(value-ImageBase); return IsCode(target); }
            catch (FormatException) { return false; }
        }
        private Method TableMethod(uint pointer, uint function, int minimum)
        {
            uint start = pointer, ignored;
            for (int i = 0; i < 160 && start >= 8 && ReadPointer(start-8,out ignored); i++) start -= 8;
            int count = 0; while (count < 256 && ReadPointer(start+(uint)count*8,out ignored)) count++;
            int slot = checked((int)((pointer-start)/8));
            return count >= minimum && slot < count ? new Method { Address = function, Table = start, Slot = slot } : null;
        }
        public Method NamedMethod(string name, string contract)
        {
            var found = new Dictionary<string,Method>();
            foreach (var fn in StringReferences(name)) {
                if (!string.Equals(Fingerprint(fn),contract,StringComparison.OrdinalIgnoreCase)) continue;
                foreach (uint pointer in PointerSites(fn.Start)) {
                    var method = TableMethod(pointer,fn.Start,61);
                    if (method != null) found[method.Address+":"+method.Table] = method;
                }
            }
            if (found.Count != 1) throw new NotSupportedException("Named Steam method is missing, changed or ambiguous: " + name);
            return found.Values.Single();
        }
        public Method EngineGetter(string contract)
        {
            var found = new Dictionary<string,Method>();
            foreach (var fn in functions.Where(fn => fn.End-fn.Start >= 100 && fn.End-fn.Start <= 400)) {
                var code = CodeBytes(fn); bool candidate = false;
                for (int i = 0; i+7 <= code.Length; i++) if (code[i] == 0x48 && code[i+1] == 0x8b && code[i+2] == 0x83 && code[i+3] == 0x90 && code[i+4] == 0 && code[i+5] == 0 && code[i+6] == 0) { candidate = true; break; }
                if (!candidate || !string.Equals(Fingerprint(fn),contract,StringComparison.OrdinalIgnoreCase)) continue;
                foreach (uint pointer in PointerSites(fn.Start)) {
                    var method = TableMethod(pointer,fn.Start,80);
                    if (method != null) found[method.Address+":"+method.Table] = method;
                }
            }
            if (found.Count != 1) throw new NotSupportedException("Steam engine accessor is missing, changed or ambiguous");
            return found.Values.Single();
        }
        public uint Export(string name)
        {
            uint offset = Offset(exportDirectory,40), count = U32(offset+24), functionsCount = U32(offset+20);
            uint names = U32(offset+32), ordinals = U32(offset+36), addresses = U32(offset+28);
            if (count > 10000 || functionsCount > 10000) throw new FormatException("Invalid PE exports");
            var sites = new HashSet<uint>(StringSites(name));
            for (uint i = 0; i < count; i++) if (sites.Contains(U32(Offset(names+i*4,4)))) {
                ushort ordinal = U16(Offset(ordinals+i*2,2));
                if (ordinal >= functionsCount) throw new FormatException("Invalid export ordinal");
                uint address = U32(Offset(addresses+(uint)ordinal*4,4));
                if (!IsCode(address)) throw new NotSupportedException("Forwarded or invalid Steam export");
                return address;
            }
            throw new NotSupportedException("Steam export unavailable: " + name);
        }
        public bool HasContract(string contract, int maximumSize)
        { return functions.Any(fn => fn.End-fn.Start <= maximumSize && string.Equals(Fingerprint(fn),contract,StringComparison.OrdinalIgnoreCase)); }
        public Function NamedFunction(string name, params string[] contracts)
        {
            var matches = StringReferences(name).Where(fn => contracts.Contains(Fingerprint(fn),StringComparer.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) throw new NotSupportedException("Steam frontend function missing, changed or ambiguous: " + name);
            return matches[0];
        }
        public Function ContractFunction(string contract, int minimum, int maximum)
        {
            var matches = functions.Where(fn => fn.End-fn.Start >= minimum && fn.End-fn.Start <= maximum &&
                MatchesContract(fn,contract)).ToList();
            if (matches.Count != 1) throw new NotSupportedException("Steam code contract missing or ambiguous");
            return matches[0];
        }
        private bool MatchesContract(Function function, string contract)
        { try { return string.Equals(Fingerprint(function),contract,StringComparison.OrdinalIgnoreCase); } catch (FormatException) { return false; } }
        public bool Calls(Function caller, Function target)
        { return Instructions(caller).Any(ins => ins.Mnemonic == Mnemonic.Call && ins.Op0Kind == OpKind.NearBranch64 && ins.NearBranch64 == ImageBase+target.Start); }
        public uint CheckedExport(string name, string contract)
        {
            uint address = Export(name); var function = FindFunction(address);
            if (function == null || function.Start != address || !string.Equals(Fingerprint(function),contract,StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Steam export contract changed: " + name);
            return address;
        }
        // Follow the exact callback registered under a Downloads method name.
        public Function BindingCallback(string name, string registrationContract, string callbackContract)
        {
            var registration = NamedFunction(name,registrationContract); var sites = new HashSet<uint>(StringSites(name));
            var instructions = Instructions(registration).ToList(); var found = new Dictionary<uint,Function>();
            for (int i = 0; i < instructions.Count; i++) {
                var ins = instructions[i];
                if (ins.Mnemonic != Mnemonic.Lea || ins.Op0Register != Register.RDX || !ins.IsIPRelativeMemoryOperand ||
                    !sites.Contains(checked((uint)(ins.IPRelativeMemoryAddress-ImageBase)))) continue;
                for (int j = i-1; j >= 0 && j >= i-18; j--) {
                    var candidate = instructions[j];
                    if (candidate.Mnemonic != Mnemonic.Lea || candidate.Op0Register != Register.RAX || !candidate.IsIPRelativeMemoryOperand) continue;
                    var fn = FindFunction(checked((uint)(candidate.IPRelativeMemoryAddress-ImageBase)));
                    if (fn != null && fn.Start == candidate.IPRelativeMemoryAddress-ImageBase &&
                        string.Equals(Fingerprint(fn),callbackContract,StringComparison.OrdinalIgnoreCase)) found[fn.Start] = fn;
                    break;
                }
            }
            if (found.Count != 1) throw new NotSupportedException("Steam Downloads callback changed: " + name);
            return found.Values.Single();
        }
        public Method VirtualImplementation(Function callback, string contract, int minimum, int maximum)
        {
            // The reviewed callback ends with a vcall. Verify the corresponding
            // native implementation in its table, including queue enum operands.
            var calls = Instructions(callback).Where(ins => ins.Mnemonic == Mnemonic.Call && ins.Op0Kind == OpKind.Memory && !ins.IsIPRelativeMemoryOperand).ToList();
            if (calls.Count != 1 || calls[0].MemoryDisplacement64%8 != 0) throw new NotSupportedException("Unknown Steam callback dispatch");
            int slot = checked((int)(calls[0].MemoryDisplacement64/8));
            var implementation = ContractFunction(contract,minimum,maximum);
            var matches = PointerSites(implementation.Start).Select(pointer => TableMethod(pointer,implementation.Start,8)).Where(method => method != null && method.Slot == slot).ToList();
            if (matches.Count != 1) throw new NotSupportedException("Steam callback implementation table changed");
            return matches[0];
        }
    }
}
