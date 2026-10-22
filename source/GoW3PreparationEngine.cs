using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace GoW3Installer
{
    public sealed class Ps4SfoInfo
    {
        public string TitleId { get; set; } = "CUSA01715";
        public string Title { get; set; } = "God of War III Remastered";
        public string AppVer { get; set; } = "01.00";
        public string Version { get; set; } = "01.00";
        public uint Attribute { get; set; }
        public uint[] UserParams { get; set; } = new uint[4];
    }

    public sealed class PreparedSegment
    {
        public ulong Address;
        public ulong Size;
        public ulong Flags; // 1 = X, 2 = W, 4 = R
    }

    public sealed class PreparedReloc
    {
        public ulong Target;
        public ulong Kind; // 0 = base-relative, 1 = func import, 2 = data import
        public ulong Value;
        public ulong Addend;
    }

    public sealed class PreparedModuleMeta
    {
        public string Name;
        public ulong Base;
        public ulong Size;
        public ulong Init;
        public ulong TlsAddress;
        public ulong TlsMemsz;
        public ulong TlsFilesz;
        public ulong TlsModuleId;
        public byte[] Image;
        public List<PreparedSegment> Segments = new List<PreparedSegment>();
        public List<PreparedReloc> Relocs = new List<PreparedReloc>();
        public List<string> Imports = new List<string>();
        public Dictionary<string, (ulong Address, ulong Kind)> Exports = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
        public ulong Entry;
        public ulong ProcParam;
        public ulong[] MainTls = new ulong[4];
    }

    public static class GoW3PreparationEngine
    {
        const uint SelfMagic = 0x1D3D154F; // b"O\x15=\x1d"
        const uint ElfMagic = 0x464C457F;  // b"\x7fELF"
        const ulong EbootBase = 0x400000UL;
        const ulong PageSize = 0x1000UL;
        const ulong ModuleAlign = 0x10000UL;

        static readonly byte[] FsLoadPattern = new byte[] { 0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00 };

        public static Ps4SfoInfo ParseSfo(byte[] data)
        {
            if (data == null || data.Length < 20 || data[0] != 0x00 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F')
                throw new InvalidDataException("Invalid param.sfo header (expected \\0PSF).");

            uint keysOffset = BitConverter.ToUInt32(data, 8);
            uint valsOffset = BitConverter.ToUInt32(data, 12);
            uint count = BitConverter.ToUInt32(data, 16);
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);

            for (uint i = 0; i < count && 20 + (i + 1) * 16 <= data.Length; i++)
            {
                int entryOff = (int)(20 + i * 16);
                ushort kOff = BitConverter.ToUInt16(data, entryOff);
                ushort fmt = BitConverter.ToUInt16(data, entryOff + 2);
                uint len = BitConverter.ToUInt32(data, entryOff + 4);
                uint vOff = BitConverter.ToUInt32(data, entryOff + 12);

                int keyStart = (int)(keysOffset + kOff);
                int keyEnd = keyStart;
                while (keyEnd < data.Length && data[keyEnd] != 0) keyEnd++;
                string key = Encoding.UTF8.GetString(data, keyStart, keyEnd - keyStart);

                int valStart = (int)(valsOffset + vOff);
                if (valStart + len > data.Length) continue;

                if (fmt == 0x0404 && len == 4)
                    dict[key] = BitConverter.ToUInt32(data, valStart);
                else
                {
                    int strLen = (int)len;
                    while (strLen > 0 && data[valStart + strLen - 1] == 0) strLen--;
                    dict[key] = Encoding.UTF8.GetString(data, valStart, strLen);
                }
            }

            var info = new Ps4SfoInfo();
            if (dict.TryGetValue("TITLE_ID", out var tid) && tid is string sTid) info.TitleId = sTid;
            if (dict.TryGetValue("TITLE", out var title) && title is string sTitle) info.Title = sTitle;
            if (dict.TryGetValue("APP_VER", out var appVer) && appVer is string sAppVer) info.AppVer = sAppVer;
            if (dict.TryGetValue("VERSION", out var ver) && ver is string sVer) info.Version = sVer;
            if (dict.TryGetValue("ATTRIBUTE", out var attr) && attr is uint uAttr) info.Attribute = uAttr;
            for (int i = 0; i < 4; i++)
            {
                if (dict.TryGetValue($"USER_DEFINED_PARAM_{i + 1}", out var up) && up is uint uVal)
                    info.UserParams[i] = uVal;
            }
            return info;
        }

        public static byte[] UnpackSelfOrElf(byte[] data, string label)
        {
            if (data == null || data.Length < 64)
                throw new InvalidDataException($"{label} is too small to be a valid PS4 SELF or ELF.");

            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic == ElfMagic) return data;
            if (magic != SelfMagic)
                throw new InvalidDataException($"{label} is neither a plaintext PS4 SELF nor an x86-64 ELF (encrypted fake/retail PKG SELFs must be decrypted first).");

            ushort numSegs = BitConverter.ToUInt16(data, 0x18);
            var selfSegs = new List<(ulong Flags, ulong Offset, ulong CompSize, ulong DecompSize)>();
            for (int i = 0; i < numSegs; i++)
            {
                int off = 32 + i * 32;
                ulong flags = BitConverter.ToUInt64(data, off);
                ulong segOff = BitConverter.ToUInt64(data, off + 8);
                ulong compSz = BitConverter.ToUInt64(data, off + 16);
                ulong decompSz = BitConverter.ToUInt64(data, off + 24);
                selfSegs.Add((flags, segOff, compSz, decompSz));
            }

            int elfOff = 32 + numSegs * 32;
            while (elfOff + 4 <= data.Length && BitConverter.ToUInt32(data, elfOff) != ElfMagic)
                elfOff += 8;

            if (elfOff + 64 > data.Length || BitConverter.ToUInt32(data, elfOff) != ElfMagic)
                throw new InvalidDataException($"Embedded ELF64 header not found inside {label}.");

            ulong phoff = BitConverter.ToUInt64(data, elfOff + 32);
            ushort phentsize = BitConverter.ToUInt16(data, elfOff + 54);
            ushort phnum = BitConverter.ToUInt16(data, elfOff + 56);

            ulong totalOutSize = (ulong)(elfOff + (int)phoff + phnum * phentsize);
            var phdrs = new List<(int Index, uint Type, uint Flags, ulong Offset, ulong Vaddr, ulong Filesz, ulong Memsz, ulong Align)>();
            for (int i = 0; i < phnum; i++)
            {
                int p = elfOff + (int)phoff + i * phentsize;
                uint pType = BitConverter.ToUInt32(data, p);
                uint pFlags = BitConverter.ToUInt32(data, p + 4);
                ulong pOffset = BitConverter.ToUInt64(data, p + 8);
                ulong pVaddr = BitConverter.ToUInt64(data, p + 16);
                ulong pFilesz = BitConverter.ToUInt64(data, p + 32);
                ulong pMemsz = BitConverter.ToUInt64(data, p + 40);
                ulong pAlign = BitConverter.ToUInt64(data, p + 48);
                phdrs.Add((i, pType, pFlags, pOffset, pVaddr, pFilesz, pMemsz, pAlign));
                if (pFilesz > 0 && pOffset + pFilesz > totalOutSize)
                    totalOutSize = pOffset + pFilesz;
            }

            byte[] outElf = new byte[totalOutSize];
            int headerBytes = (int)phoff + phnum * phentsize;
            Buffer.BlockCopy(data, elfOff, outElf, 0, headerBytes);

            foreach (var ph in phdrs)
            {
                if (ph.Filesz == 0) continue;
                bool copied = false;
                foreach (var s in selfSegs)
                {
                    if ((s.Flags & 0x800UL) != 0 && (int)(s.Flags >> 20) == ph.Index && s.CompSize == ph.Filesz && s.DecompSize == ph.Filesz)
                    {
                        Buffer.BlockCopy(data, (int)s.Offset, outElf, (int)ph.Offset, (int)ph.Filesz);
                        copied = true;
                        break;
                    }
                }
                if (!copied && ph.Type == 0x61000000U) // PT_SCE_DYNLIBDATA
                {
                    var tail = selfSegs.LastOrDefault(s => (s.Flags & 0x800UL) == 0 && s.CompSize >= ph.Filesz);
                    if (tail.CompSize >= ph.Filesz)
                    {
                        Buffer.BlockCopy(data, (int)tail.Offset, outElf, (int)ph.Offset, (int)ph.Filesz);
                        copied = true;
                    }
                }
            }
            return outElf;
        }

        public static PreparedModuleMeta ParseAndPlaceModule(byte[] elf, ulong loadBase, bool isMainEboot, string label)
        {
            if (elf == null || elf.Length < 64 || BitConverter.ToUInt32(elf, 0) != ElfMagic)
                throw new InvalidDataException($"Invalid ELF64 image for {label}.");
            if (elf[4] != 2 || elf[5] != 1 || BitConverter.ToUInt16(elf, 18) != 62)
                throw new InvalidDataException($"{label} is not a 64-bit little-endian x86-64 ELF.");

            ulong entry = BitConverter.ToUInt64(elf, 24);
            ulong phoff = BitConverter.ToUInt64(elf, 32);
            ushort phentsize = BitConverter.ToUInt16(elf, 54);
            ushort phnum = BitConverter.ToUInt16(elf, 56);

            var phdrs = new List<(uint Type, uint Flags, ulong Offset, ulong Vaddr, ulong Filesz, ulong Memsz, ulong Align)>();
            for (int i = 0; i < phnum; i++)
            {
                int p = (int)phoff + i * phentsize;
                phdrs.Add((
                    BitConverter.ToUInt32(elf, p),
                    BitConverter.ToUInt32(elf, p + 4),
                    BitConverter.ToUInt64(elf, p + 8),
                    BitConverter.ToUInt64(elf, p + 16),
                    BitConverter.ToUInt64(elf, p + 32),
                    BitConverter.ToUInt64(elf, p + 40),
                    BitConverter.ToUInt64(elf, p + 48)
                ));
            }

            var loadPhdrs = phdrs.Where(p => (p.Type == 1U || p.Type == 0x61000010U) && p.Memsz > 0).ToList();
            if (loadPhdrs.Count == 0) throw new InvalidDataException($"No loadable segments in {label}.");

            ulong minVaddr = loadPhdrs.Min(p => p.Vaddr);
            ulong maxVaddr = loadPhdrs.Max(p => p.Vaddr + p.Memsz);
            ulong span = AlignUp(maxVaddr - minVaddr, PageSize);

            var meta = new PreparedModuleMeta
            {
                Name = label,
                Base = loadBase,
                Size = span,
                Entry = loadBase + (entry - minVaddr),
                Image = new byte[span]
            };

            foreach (var ph in loadPhdrs)
            {
                ulong start = (ph.Vaddr - minVaddr) & ~(PageSize - 1);
                ulong end = AlignUp((ph.Vaddr - minVaddr) + ph.Memsz, PageSize);
                ulong flags = ph.Type == 0x61000010U ? 4UL : (ph.Flags & 7UL); // RELRO becomes R--
                meta.Segments.Add(new PreparedSegment
                {
                    Address = loadBase + start,
                    Size = end - start,
                    Flags = flags
                });
                if (ph.Filesz > 0)
                {
                    Buffer.BlockCopy(elf, (int)ph.Offset, meta.Image, (int)(ph.Vaddr - minVaddr), (int)ph.Filesz);
                }
            }

            // Rewrite mov rax, fs:[0] (64 48 8b 04 25 00 00 00 00) -> mov rax, gs:[0] (0x65 prefix) in executable segments
            foreach (var seg in meta.Segments)
            {
                if ((seg.Flags & 1UL) == 0) continue;
                int segStart = (int)(seg.Address - loadBase);
                int segEnd = segStart + (int)seg.Size - FsLoadPattern.Length;
                for (int at = segStart; at <= segEnd; at++)
                {
                    if (meta.Image[at] == 0x64)
                    {
                        bool match = true;
                        for (int k = 1; k < FsLoadPattern.Length; k++)
                        {
                            if (meta.Image[at + k] != FsLoadPattern[k]) { match = false; break; }
                        }
                        if (match)
                        {
                            meta.Image[at] = 0x65;
                            at += FsLoadPattern.Length - 1;
                        }
                    }
                }
            }

            // TLS metadata
            var tlsPh = phdrs.FirstOrDefault(p => p.Type == 7U); // PT_TLS
            if (tlsPh.Type == 7U)
            {
                ulong tlsAddr = loadBase + (tlsPh.Vaddr - minVaddr);
                ulong tlsAlign = tlsPh.Align == 0 ? 1UL : tlsPh.Align;
                if (isMainEboot)
                {
                    meta.MainTls = new ulong[] { tlsAddr, tlsPh.Filesz, tlsPh.Memsz, tlsAlign };
                }
                else
                {
                    meta.TlsAddress = tlsAddr;
                    meta.TlsFilesz = tlsPh.Filesz;
                    meta.TlsMemsz = tlsPh.Memsz;
                }
            }

            // ProcParam metadata
            var procPh = phdrs.FirstOrDefault(p => p.Type == 0x61000001U); // PT_SCE_PROCPARAM
            if (procPh.Type == 0x61000001U)
            {
                meta.ProcParam = loadBase + (procPh.Vaddr - minVaddr);
            }

            // Dynamic & SCE Dynlib Data
            var dynPh = phdrs.FirstOrDefault(p => p.Type == 2U);
            var scePh = phdrs.FirstOrDefault(p => p.Type == 0x61000000U);
            if (dynPh.Type != 2U || scePh.Type != 0x61000000U)
                throw new InvalidDataException($"{label} is missing PT_DYNAMIC or PT_SCE_DYNLIBDATA.");

            var dyn = new Dictionary<ulong, ulong>();
            var libs = new Dictionary<ulong, int>();
            var mods = new Dictionary<ulong, int>();
            for (int off = 0; off + 16 <= (int)dynPh.Filesz; off += 16)
            {
                ulong tag = BitConverter.ToUInt64(elf, (int)dynPh.Offset + off);
                ulong val = BitConverter.ToUInt64(elf, (int)dynPh.Offset + off + 8);
                if (tag == 0) break;
                if (tag == 0x61000015UL) libs[val >> 48] = (int)(val & 0xFFFFFFFFUL);
                else if (tag == 0x61000047UL) mods[val >> 48] = (int)(val & 0xFFFFFFFFUL);
                else dyn[tag] = val;
            }

            if (dyn.TryGetValue(12UL, out ulong dtInit))
                meta.Init = loadBase + (dtInit - minVaddr);

            int dynlibOff = (int)scePh.Offset;
            int strOff = dynlibOff + (int)dyn[0x61000035UL];
            int symOff = dynlibOff + (int)dyn[0x61000039UL];
            int symCount = (int)(dyn[0x6100003fUL] / 24UL);

            string ReadCString(int start)
            {
                int end = start;
                while (end < elf.Length && elf[end] != 0) end++;
                return Encoding.ASCII.GetString(elf, start, end - start);
            }

            string DecodeSceSymbolName(string raw)
            {
                string[] parts = raw.Split('#');
                if (parts.Length == 3 && Base64Id(parts[1], out ulong lid) && Base64Id(parts[2], out ulong mid) &&
                    libs.TryGetValue(lid, out int lStr) && mods.TryGetValue(mid, out int mStr))
                {
                    return $"{ReadCString(strOff + mStr)}:{ReadCString(strOff + lStr)}:{parts[0]}";
                }
                return raw;
            }

            var symbols = new List<(string RawName, string ResolvedName, byte Info, ushort Shndx, ulong Value)>(symCount);
            for (int i = 0; i < symCount; i++)
            {
                int s = symOff + i * 24;
                uint stName = BitConverter.ToUInt32(elf, s);
                byte stInfo = elf[s + 4];
                ushort stShndx = BitConverter.ToUInt16(elf, s + 6);
                ulong stValue = BitConverter.ToUInt64(elf, s + 8);
                string raw = ReadCString(strOff + (int)stName);
                string resolved = DecodeSceSymbolName(raw);
                symbols.Add((raw, resolved, stInfo, stShndx, stValue));

                int bind = stInfo >> 4;
                int type = stInfo & 0xF;
                if (stShndx != 0 && (bind == 1 || bind == 2) && (type == 1 || type == 2) && raw.Contains("#"))
                {
                    string prefix = raw.Split('#')[0] + "#";
                    ulong kind = type == 2 ? 2UL : 1UL;
                    meta.Exports[prefix] = (loadBase + (stValue - minVaddr), kind);
                }
            }

            var importIndices = new Dictionary<string, ulong>(StringComparer.Ordinal);
            ulong GetImportIndex(string name)
            {
                if (!importIndices.TryGetValue(name, out ulong idx))
                {
                    idx = (ulong)meta.Imports.Count;
                    importIndices[name] = idx;
                    meta.Imports.Add(name);
                }
                return idx;
            }

            void ParseRelaBlock(ulong relaTag, ulong sizeTag)
            {
                if (!dyn.TryGetValue(relaTag, out ulong rOff) || !dyn.TryGetValue(sizeTag, out ulong rSz) || rSz == 0)
                    return;
                int baseOff = dynlibOff + (int)rOff;
                int count = (int)(rSz / 24UL);
                for (int i = 0; i < count; i++)
                {
                    int entryPos = baseOff + i * 24;
                    ulong rOffset = BitConverter.ToUInt64(elf, entryPos);
                    ulong rInfo = BitConverter.ToUInt64(elf, entryPos + 8);
                    long rAddend = BitConverter.ToInt64(elf, entryPos + 16);
                    uint rType = (uint)(rInfo & 0xFFFFFFFFUL);
                    int rSym = (int)(rInfo >> 32);

                    ulong target = loadBase + (rOffset - minVaddr);
                    if (rType == 8U) // R_X86_64_RELATIVE
                    {
                        meta.Relocs.Add(new PreparedReloc
                        {
                            Target = target,
                            Kind = 0,
                            Value = (ulong)((long)loadBase + (rAddend - (long)minVaddr)),
                            Addend = 0
                        });
                    }
                    else if (rType == 16U && rSym == 0) // R_X86_64_DTPMOD64
                    {
                        byte[] modBytes = BitConverter.GetBytes(meta.TlsModuleId);
                        Buffer.BlockCopy(modBytes, 0, meta.Image, (int)(rOffset - minVaddr), 8);
                    }
                    else if (rType == 1U || rType == 6U || rType == 7U) // R_X86_64_64, GLOB_DAT, JUMP_SLOT
                    {
                        var sym = symbols[rSym];
                        if (sym.Shndx != 0)
                        {
                            meta.Relocs.Add(new PreparedReloc
                            {
                                Target = target,
                                Kind = 0,
                                Value = (ulong)((long)loadBase + ((long)sym.Value + rAddend - (long)minVaddr)),
                                Addend = 0
                            });
                        }
                        else
                        {
                            bool isData = (rType == 1U || rType == 6U) && ((sym.Info & 0xF) == 1);
                            meta.Relocs.Add(new PreparedReloc
                            {
                                Target = target,
                                Kind = isData ? 2UL : 1UL,
                                Value = GetImportIndex(sym.ResolvedName),
                                Addend = (ulong)rAddend
                            });
                        }
                    }
                }
            }

            ParseRelaBlock(0x6100002fUL, 0x61000031UL); // DT_SCE_RELA
            ParseRelaBlock(0x61000029UL, 0x6100002dUL); // DT_SCE_JMPREL
            return meta;
        }

        static bool Base64Id(string text, out ulong value)
        {
            value = 0;
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";
            foreach (char c in text)
            {
                int idx = alphabet.IndexOf(c);
                if (idx < 0) return false;
                value = (value << 6) | (ulong)(uint)idx;
            }
            return true;
        }

        public static void PrepareAll(
            string gameDir,
            string outDir,
            string patchXmlPath,
            GoW3Settings settings,
            Action<string> log,
            CancellationToken cancel)
        {
            Directory.CreateDirectory(outDir);
            string sfoPath = Path.Combine(gameDir, "sce_sys", "param.sfo");
            string ebootPath = Path.Combine(gameDir, "eboot.bin");
            string libcPath = Path.Combine(gameDir, "sce_module", "libc.prx");
            string fiosPath = Path.Combine(gameDir, "sce_module", "libSceFios2.prx");

            foreach (var req in new[] { sfoPath, ebootPath, libcPath, fiosPath })
            {
                if (!File.Exists(req))
                    throw new FileNotFoundException($"Missing required PS4 file: {req}");
            }

            cancel.ThrowIfCancellationRequested();
            log("Reading sce_sys/param.sfo...");
            var sfo = ParseSfo(File.ReadAllBytes(sfoPath));
            log($"Detected {sfo.Title} ({sfo.TitleId}) AppVer {sfo.AppVer}");

            // 1. Write content.bin (G3CONT01)
            WriteContentBin(Path.Combine(outDir, "content.bin"), sfo);
            log("Generated out/content.bin (G3CONT01)");

            // 2. Unpack eboot.bin, libc.prx, libSceFios2.prx
            cancel.ThrowIfCancellationRequested();
            log("Unpacking eboot.bin (PS4 SELF/ELF)...");
            byte[] ebootElf = UnpackSelfOrElf(File.ReadAllBytes(ebootPath), "eboot.bin");
            File.WriteAllBytes(Path.Combine(outDir, "eboot.elf"), ebootElf);

            log("Unpacking sce_module/libc.prx and libSceFios2.prx...");
            byte[] libcElf = UnpackSelfOrElf(File.ReadAllBytes(libcPath), "libc.prx");
            byte[] fiosElf = UnpackSelfOrElf(File.ReadAllBytes(fiosPath), "libSceFios2.prx");

            // Verify libc _init_env is 0xC3 (RET)
            var ebootMod = ParseAndPlaceModule(ebootElf, 0UL, isMainEboot: true, label: "eboot.bin");
            ulong libcBase = AlignUp(ebootMod.Size, ModuleAlign);
            var libcMod = ParseAndPlaceModule(libcElf, libcBase, isMainEboot: false, label: "libc.prx");
            libcMod.TlsModuleId = 2;
            // Re-parse with TlsModuleId = 2 so DTPMOD64 relocations get 2
            libcMod = ParseModuleWithTlsId(libcElf, libcBase, 2, "libc.prx");

            ulong fiosBase = AlignUp(libcBase + libcMod.Size, ModuleAlign);
            var fiosMod = ParseModuleWithTlsId(fiosElf, fiosBase, 3, "libSceFios2.prx");

            // 3. Write standalone boot.bin (G3PROBE2) and linked boot-linked.bin (G3PROBE5)
            cancel.ThrowIfCancellationRequested();
            log("Linking eboot.bin + libc.prx + libSceFios2.prx (G3PROBE5)...");
            WriteBootProbe2(Path.Combine(outDir, "boot.bin"), ebootMod);
            var linkedInfo = WriteBootProbe5(Path.Combine(outDir, "boot-linked.bin"), ebootMod, new[] { libcMod, fiosMod });
            log($"Generated out/boot-linked.bin ({linkedInfo.TotalSize / (1024 * 1024)} MB image, {linkedInfo.TotalRelocs} relocs, {linkedInfo.BindingsCount} native libc bindings)");

            // 4. Compile XML patches -> out/patches.bin (G3PATCH2)
            cancel.ThrowIfCancellationRequested();
            log("Compiling XML patches (G3PATCH2)...");
            var patchWrites = CompilePatchesBin(
                patchXmlPath,
                ebootElf,
                sfo.AppVer,
                settings.GetSelectedPatchNames(),
                Path.Combine(outDir, "patches.bin"),
                log);

            // 5. Generate PE64 GoW3StartupModule.dll for Xbox UWP LoadLibraryExW (ACG-compliant executable image)
            cancel.ThrowIfCancellationRequested();
            log("Building Xbox UWP PE64 executable module (GoW3StartupModule.dll)...");
            string dllPath = Path.Combine(outDir, "GoW3StartupModule.dll");
            BuildPe64StartupModule(dllPath, linkedInfo.CombinedImage, linkedInfo.AllSegments, patchWrites, ebootMod.Entry);
            log("Generated out/GoW3StartupModule.dll for Xbox Series S/X UWP.");

            // Write preparation marker
            File.WriteAllText(Path.Combine(outDir, "prepared-ok.txt"),
                $"TitleId={sfo.TitleId}\nAppVer={sfo.AppVer}\nResolution={settings.Resolution}\nFPS={settings.FrameRatePatch}\nPreparedUtc={DateTime.UtcNow:O}\n");
            settings.Save();
        }

        static PreparedModuleMeta ParseModuleWithTlsId(byte[] elf, ulong loadBase, ulong tlsModuleId, string label)
        {
            var m = ParseAndPlaceModule(elf, loadBase, isMainEboot: false, label: label);
            m.TlsModuleId = tlsModuleId;
            // Patch DTPMOD64 slots in m.Image where needed
            ulong phoff = BitConverter.ToUInt64(elf, 32);
            ushort phentsize = BitConverter.ToUInt16(elf, 54);
            ushort phnum = BitConverter.ToUInt16(elf, 56);
            ulong minVaddr = ulong.MaxValue, dynOff = 0, dynSz = 0, sceOff = 0;
            for (int i = 0; i < phnum; i++)
            {
                int p = (int)phoff + i * phentsize;
                uint t = BitConverter.ToUInt32(elf, p);
                ulong off = BitConverter.ToUInt64(elf, p + 8);
                ulong vaddr = BitConverter.ToUInt64(elf, p + 16);
                ulong fsz = BitConverter.ToUInt64(elf, p + 32);
                ulong msz = BitConverter.ToUInt64(elf, p + 40);
                if ((t == 1U || t == 0x61000010U) && msz > 0 && vaddr < minVaddr) minVaddr = vaddr;
                if (t == 2U) { dynOff = off; dynSz = fsz; }
                if (t == 0x61000000U) sceOff = off;
            }
            ulong rOff = 0, rSz = 0;
            for (int off = 0; off + 16 <= (int)dynSz; off += 16)
            {
                ulong tag = BitConverter.ToUInt64(elf, (int)dynOff + off);
                ulong val = BitConverter.ToUInt64(elf, (int)dynOff + off + 8);
                if (tag == 0x6100002fUL) rOff = val;
                if (tag == 0x61000031UL) rSz = val;
            }
            for (int i = 0; i < (int)(rSz / 24UL); i++)
            {
                int pos = (int)sceOff + (int)rOff + i * 24;
                ulong targetVaddr = BitConverter.ToUInt64(elf, pos);
                ulong info = BitConverter.ToUInt64(elf, pos + 8);
                if ((uint)(info & 0xFFFFFFFFUL) == 16U && (info >> 32) == 0)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(tlsModuleId), 0, m.Image, (int)(targetVaddr - minVaddr), 8);
                }
            }
            return m;
        }

        static void WriteContentBin(string path, Ps4SfoInfo sfo)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("G3CONT01"));
                bw.Write(3U); // full SKU = 3
                for (int i = 0; i < 4; i++) bw.Write(sfo.UserParams[i]);
            }
        }

        static void WriteBootProbe2(string path, PreparedModuleMeta eboot)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("G3PROBE2"));
                bw.Write(eboot.Size);
                bw.Write(eboot.Entry);
                bw.Write((ulong)eboot.Segments.Count);
                bw.Write((ulong)eboot.Relocs.Count);
                bw.Write((ulong)eboot.Imports.Count);
                bw.Write(1UL); // capabilities = 1
                foreach (var s in eboot.Segments)
                {
                    bw.Write(s.Address);
                    bw.Write(s.Size);
                    bw.Write(s.Flags);
                }
                foreach (var imp in eboot.Imports)
                {
                    byte[] nameBytes = new byte[128];
                    byte[] ascii = Encoding.ASCII.GetBytes(imp);
                    Buffer.BlockCopy(ascii, 0, nameBytes, 0, Math.Min(127, ascii.Length));
                    bw.Write(nameBytes);
                }
                foreach (var r in eboot.Relocs)
                {
                    bw.Write(r.Target);
                    bw.Write(r.Kind);
                    bw.Write(r.Value);
                    bw.Write(r.Addend);
                }
                bw.Write(eboot.Image);
            }
        }

        sealed class LinkedBootSummary
        {
            public ulong TotalSize;
            public int TotalRelocs;
            public int BindingsCount;
            public byte[] CombinedImage;
            public List<PreparedSegment> AllSegments;
        }

        static LinkedBootSummary WriteBootProbe5(string path, PreparedModuleMeta eboot, PreparedModuleMeta[] modules)
        {
            var allSegments = new List<PreparedSegment>(eboot.Segments);
            var allRelocs = new List<PreparedReloc>(eboot.Relocs);
            var allImports = new List<string>(eboot.Imports);
            var importMap = new Dictionary<string, ulong>(StringComparer.Ordinal);
            for (int i = 0; i < allImports.Count; i++) importMap[allImports[i]] = (ulong)i;

            ulong totalSize = eboot.Size;
            foreach (var m in modules)
            {
                allSegments.AddRange(m.Segments);
                var remap = new ulong[m.Imports.Count];
                for (int i = 0; i < m.Imports.Count; i++)
                {
                    if (!importMap.TryGetValue(m.Imports[i], out ulong idx))
                    {
                        idx = (ulong)allImports.Count;
                        importMap[m.Imports[i]] = idx;
                        allImports.Add(m.Imports[i]);
                    }
                    remap[i] = idx;
                }
                foreach (var r in m.Relocs)
                {
                    allRelocs.Add(new PreparedReloc
                    {
                        Target = r.Target,
                        Kind = r.Kind,
                        Value = r.Kind != 0 ? remap[r.Value] : r.Value,
                        Addend = r.Addend
                    });
                }
                if (m.Base + m.Size > totalSize) totalSize = m.Base + m.Size;
            }

            // Bind libSceLibcInternal imports to libc.prx exports
            var libcExports = modules[0].Exports;
            var bindings = new List<(ulong Index, ulong Address, ulong Kind)>();
            for (int i = 0; i < allImports.Count; i++)
            {
                string name = allImports[i];
                if (!name.StartsWith("libc:libSceLibcInternal:", StringComparison.Ordinal)) continue;
                string symId = name.Substring("libc:libSceLibcInternal:".Length);
                if (libcExports.TryGetValue(symId + "#", out var exp))
                {
                    bindings.Add(((ulong)i, exp.Address, exp.Kind));
                }
            }

            byte[] combinedImage = new byte[totalSize];
            Buffer.BlockCopy(eboot.Image, 0, combinedImage, 0, eboot.Image.Length);
            foreach (var m in modules)
            {
                Buffer.BlockCopy(m.Image, 0, combinedImage, (int)m.Base, m.Image.Length);
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("G3PROBE5"));
                bw.Write(totalSize);
                bw.Write(eboot.Entry);
                bw.Write((ulong)allSegments.Count);
                bw.Write((ulong)allRelocs.Count);
                bw.Write((ulong)allImports.Count);
                bw.Write(1UL); // capabilities = 1
                bw.Write(eboot.ProcParam);
                for (int i = 0; i < 4; i++) bw.Write(eboot.MainTls[i]);
                bw.Write((ulong)modules.Length);
                foreach (var m in modules)
                {
                    bw.Write(m.Base);
                    bw.Write(m.Size);
                    bw.Write(m.Init);
                    bw.Write(m.TlsAddress);
                    bw.Write(m.TlsMemsz);
                    bw.Write(m.TlsFilesz);
                    bw.Write(m.TlsModuleId);
                }
                bw.Write((ulong)bindings.Count);
                foreach (var b in bindings)
                {
                    bw.Write(b.Index);
                    bw.Write(b.Address);
                    bw.Write(b.Kind);
                }
                foreach (var s in allSegments)
                {
                    bw.Write(s.Address);
                    bw.Write(s.Size);
                    bw.Write(s.Flags);
                }
                foreach (var imp in allImports)
                {
                    byte[] nameBytes = new byte[128];
                    byte[] ascii = Encoding.ASCII.GetBytes(imp);
                    Buffer.BlockCopy(ascii, 0, nameBytes, 0, Math.Min(127, ascii.Length));
                    bw.Write(nameBytes);
                }
                foreach (var r in allRelocs)
                {
                    bw.Write(r.Target);
                    bw.Write(r.Kind);
                    bw.Write(r.Value);
                    bw.Write(r.Addend);
                }
                bw.Write(combinedImage);
            }

            return new LinkedBootSummary
            {
                TotalSize = totalSize,
                TotalRelocs = allRelocs.Count,
                BindingsCount = bindings.Count,
                CombinedImage = combinedImage,
                AllSegments = allSegments
            };
        }

        public static List<(ulong Offset, byte[] Data)> CompilePatchesBin(
            string xmlPath,
            byte[] ebootElf,
            string appVer,
            IList<string> selectedNames,
            string outPatchesBinPath,
            Action<string> log)
        {
            var writes = new List<(ulong Offset, byte[] Data)>();
            if (!File.Exists(xmlPath))
            {
                WritePatchesFile(outPatchesBinPath, writes);
                return writes;
            }

            // Build flat eboot image & segments for signature matching
            ulong phoff = BitConverter.ToUInt64(ebootElf, 32);
            ushort phentsize = BitConverter.ToUInt16(ebootElf, 54);
            ushort phnum = BitConverter.ToUInt16(ebootElf, 56);
            var segs = new List<(ulong Start, ulong End)>();
            ulong maxEnd = 0;
            var loadList = new List<(ulong Offset, ulong Vaddr, ulong Filesz)>();
            for (int i = 0; i < phnum; i++)
            {
                int p = (int)phoff + i * phentsize;
                uint t = BitConverter.ToUInt32(ebootElf, p);
                if (t != 1U && t != 0x61000010U) continue;
                ulong off = BitConverter.ToUInt64(ebootElf, p + 8);
                ulong vaddr = BitConverter.ToUInt64(ebootElf, p + 16);
                ulong fsz = BitConverter.ToUInt64(ebootElf, p + 32);
                ulong msz = BitConverter.ToUInt64(ebootElf, p + 40);
                if (msz == 0) continue;
                ulong start = (vaddr - EbootBase) & ~(PageSize - 1);
                ulong end = AlignUp(vaddr - EbootBase + msz, PageSize);
                segs.Add((start, end));
                if (end > maxEnd) maxEnd = end;
                loadList.Add((off, vaddr, fsz));
            }

            byte[] image = new byte[maxEnd];
            foreach (var l in loadList)
            {
                if (l.Filesz > 0)
                    Buffer.BlockCopy(ebootElf, (int)l.Offset, image, (int)(l.Vaddr - EbootBase), (int)l.Filesz);
            }

            var doc = XDocument.Load(xmlPath);
            var wantSet = new HashSet<string>(selectedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var meta in doc.Descendants("Metadata"))
            {
                string name = (string)meta.Attribute("Name") ?? "";
                string ver = (string)meta.Attribute("AppVer") ?? "01.00";
                if (!wantSet.Contains(name)) continue;
                if (ver != appVer && ver != "01.00" && ver != "mask") continue;

                var patchLines = meta.Descendants("Line").ToList();
                var ours = new List<(ulong Offset, byte[] Data)>();
                bool ok = true;
                foreach (var line in patchLines)
                {
                    int? located = LocatePatchLine(image, line);
                    if (!located.HasValue) { ok = false; break; }
                    byte[] encoded = EncodePatchLine(line);
                    ours.Add(((ulong)located.Value, encoded));
                }
                if (ok && ours.Count > 0)
                {
                    writes.AddRange(ours);
                    log($"Applied patch: {name} ({ours.Count} writes)");
                }
            }

            WritePatchesFile(outPatchesBinPath, writes);
            return writes;
        }

        static int? LocatePatchLine(byte[] image, XElement line)
        {
            string patternAttr = (string)line.Attribute("Pattern");
            if (!string.IsNullOrEmpty(patternAttr))
            {
                int at = int.Parse((string)line.Attribute("At") ?? "0", CultureInfo.InvariantCulture);
                int? match = FindSignature(image, patternAttr, at);
                return match;
            }
            string targetAttr = (string)line.Attribute("Target");
            if (!string.IsNullOrEmpty(targetAttr))
            {
                int targetAt = int.Parse((string)line.Attribute("TargetAt") ?? "0", CultureInfo.InvariantCulture);
                int? targetMatch = FindSignature(image, targetAttr, targetAt);
                if (!targetMatch.HasValue) return null;
                byte[] tail = ParseHexBytes((string)line.Attribute("Tail") ?? "");
                int refDelta = int.Parse((string)line.Attribute("Ref") ?? "0", CultureInfo.InvariantCulture);
                int found = -1;
                for (int pos = 0; pos + 4 + tail.Length <= image.Length; pos++)
                {
                    bool tailOk = true;
                    for (int k = 0; k < tail.Length; k++)
                    {
                        if (image[pos + 4 + k] != tail[k]) { tailOk = false; break; }
                    }
                    if (!tailOk) continue;
                    int rel = BitConverter.ToInt32(image, pos);
                    if (pos + refDelta + rel == targetMatch.Value)
                    {
                        if (found >= 0) return null; // ambiguous
                        found = pos;
                    }
                }
                if (found < 0) return null;
                int offsetDelta = int.Parse((string)line.Attribute("At") ?? "0", CultureInfo.InvariantCulture);
                return found + offsetDelta;
            }
            string addrAttr = (string)line.Attribute("Address");
            if (!string.IsNullOrEmpty(addrAttr))
            {
                ulong addr = addrAttr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(addrAttr.Substring(2), 16)
                    : ulong.Parse(addrAttr, CultureInfo.InvariantCulture);
                if (addr >= EbootBase && addr - EbootBase < (ulong)image.Length)
                    return (int)(addr - EbootBase);
            }
            return null;
        }

        static int? FindSignature(byte[] image, string patternText, int delta)
        {
            string[] tokens = patternText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            byte?[] pat = new byte?[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "??" || tokens[i] == "?") pat[i] = null;
                else pat[i] = Convert.ToByte(tokens[i], 16);
            }

            int found = -1;
            int limit = image.Length - pat.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    if (pat[j].HasValue && image[i + j] != pat[j].Value) { match = false; break; }
                }
                if (match)
                {
                    if (found >= 0) return null; // must be unique
                    found = i;
                }
            }
            return found >= 0 ? found + delta : (int?)null;
        }

        static byte[] EncodePatchLine(XElement line)
        {
            string kind = (string)line.Attribute("Type") ?? "bytes";
            string val = (string)line.Attribute("Value") ?? "";
            switch (kind)
            {
                case "bytes": return ParseHexBytes(val);
                case "utf8": return Encoding.UTF8.GetBytes(val);
                case "bytes16": return BitConverter.GetBytes(ParseUInt64(val) & 0xFFFFUL).Take(2).ToArray();
                case "bytes32": return BitConverter.GetBytes((uint)(ParseUInt64(val) & 0xFFFFFFFFUL));
                case "bytes64": return BitConverter.GetBytes(ParseUInt64(val));
                case "float32": return BitConverter.GetBytes(float.Parse(val, CultureInfo.InvariantCulture));
                case "float64": return BitConverter.GetBytes(double.Parse(val, CultureInfo.InvariantCulture));
                case "nop":
                    int count = (int)ParseUInt64(val);
                    byte[] nops = new byte[count];
                    for (int i = 0; i < count; i++) nops[i] = 0x90;
                    return nops;
                default:
                    throw new InvalidDataException("Unsupported patch type: " + kind);
            }
        }

        static ulong ParseUInt64(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt64(text.Substring(2), 16);
            if (text.StartsWith("-"))
                return unchecked((ulong)long.Parse(text, CultureInfo.InvariantCulture));
            return ulong.Parse(text, CultureInfo.InvariantCulture);
        }

        static byte[] ParseHexBytes(string hex)
        {
            string clean = new string((hex ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            byte[] bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }

        static void WritePatchesFile(string path, List<(ulong Offset, byte[] Data)> writes)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("G3PATCH2"));
                bw.Write(EbootBase);
                bw.Write((ulong)writes.Count);
                foreach (var w in writes)
                {
                    bw.Write(w.Offset);
                    bw.Write((ulong)w.Data.Length);
                    bw.Write(w.Data);
                }
            }
        }

        // Emits a valid 64-bit Windows PE DLL (GoW3StartupModule.dll) containing the linked PS4 guest segments
        // so Xbox Series S UWP can map the executable code from disk via LoadLibraryExW without ACG violations.
        public static void BuildPe64StartupModule(
            string dllPath,
            byte[] combinedImage,
            List<PreparedSegment> segments,
            List<(ulong Offset, byte[] Data)> patchWrites,
            ulong entryOffset)
        {
            byte[] patchedImage = (byte[])combinedImage.Clone();
            foreach (var w in patchWrites)
            {
                if (w.Offset + (ulong)w.Data.Length <= (ulong)patchedImage.Length)
                    Buffer.BlockCopy(w.Data, 0, patchedImage, (int)w.Offset, w.Data.Length);
            }

            uint sectionAlignment = 0x1000;
            uint fileAlignment = 0x1000;
            uint headerSize = 0x1000;
            uint rvaBase = 0x1000;
            uint imageSpan = (uint)AlignUp((ulong)patchedImage.Length, sectionAlignment);
            uint sizeOfImage = rvaBase + imageSpan;

            using (var fs = new FileStream(dllPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                byte[] hdr = new byte[headerSize];
                // DOS header
                hdr[0] = (byte)'M'; hdr[1] = (byte)'Z';
                BitConverter.GetBytes(0x80).CopyTo(hdr, 0x3C); // e_lfanew = 0x80

                int pe = 0x80;
                hdr[pe] = (byte)'P'; hdr[pe + 1] = (byte)'E';
                BitConverter.GetBytes((ushort)0x8664).CopyTo(hdr, pe + 4); // IMAGE_FILE_MACHINE_AMD64
                BitConverter.GetBytes((ushort)1).CopyTo(hdr, pe + 6);      // NumberOfSections = 1 (.gow3)
                BitConverter.GetBytes((ushort)240).CopyTo(hdr, pe + 20);   // SizeOfOptionalHeader (PE32+)
                BitConverter.GetBytes((ushort)0x2022).CopyTo(hdr, pe + 22);// EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL

                int opt = pe + 24;
                BitConverter.GetBytes((ushort)0x020B).CopyTo(hdr, opt);    // PE32+ magic
                BitConverter.GetBytes(imageSpan).CopyTo(hdr, opt + 4);     // SizeOfCode
                BitConverter.GetBytes(rvaBase + (uint)entryOffset).CopyTo(hdr, opt + 16); // AddressOfEntryPoint
                BitConverter.GetBytes(rvaBase).CopyTo(hdr, opt + 20);      // BaseOfCode
                BitConverter.GetBytes(0x0000000180000000UL).CopyTo(hdr, opt + 24); // ImageBase
                BitConverter.GetBytes(sectionAlignment).CopyTo(hdr, opt + 32);
                BitConverter.GetBytes(fileAlignment).CopyTo(hdr, opt + 36);
                BitConverter.GetBytes((ushort)10).CopyTo(hdr, opt + 40);   // MajorOperatingSystemVersion
                BitConverter.GetBytes((ushort)10).CopyTo(hdr, opt + 48);   // MajorSubsystemVersion
                BitConverter.GetBytes(sizeOfImage).CopyTo(hdr, opt + 56);
                BitConverter.GetBytes(headerSize).CopyTo(hdr, opt + 60);   // SizeOfHeaders
                BitConverter.GetBytes((ushort)2).CopyTo(hdr, opt + 68);    // Subsystem = WINDOWS_GUI
                BitConverter.GetBytes((ushort)0x0160).CopyTo(hdr, opt + 70); // DllCharacteristics (HIGH_ENTROPY_VA | DYNAMIC_BASE | NX_COMPAT)
                BitConverter.GetBytes(0x100000UL).CopyTo(hdr, opt + 72);   // SizeOfStackReserve
                BitConverter.GetBytes(0x10000UL).CopyTo(hdr, opt + 80);    // SizeOfStackCommit
                BitConverter.GetBytes(0x100000UL).CopyTo(hdr, opt + 88);   // SizeOfHeapReserve
                BitConverter.GetBytes(0x10000UL).CopyTo(hdr, opt + 96);    // SizeOfHeapCommit
                BitConverter.GetBytes(16U).CopyTo(hdr, opt + 108);         // NumberOfRvaAndSizes

                int sec = opt + 240;
                Encoding.ASCII.GetBytes(".gow3\0\0\0").CopyTo(hdr, sec);
                BitConverter.GetBytes(imageSpan).CopyTo(hdr, sec + 8);     // VirtualSize
                BitConverter.GetBytes(rvaBase).CopyTo(hdr, sec + 12);      // VirtualAddress
                BitConverter.GetBytes(imageSpan).CopyTo(hdr, sec + 16);    // SizeOfRawData
                BitConverter.GetBytes(headerSize).CopyTo(hdr, sec + 20);   // PointerToRawData
                // IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE
                BitConverter.GetBytes(0xE0000020U).CopyTo(hdr, sec + 36);

                bw.Write(hdr);
                bw.Write(patchedImage);
                int pad = (int)imageSpan - patchedImage.Length;
                if (pad > 0) bw.Write(new byte[pad]);
            }
        }

        static ulong AlignUp(ulong value, ulong align)
        {
            return (value + align - 1UL) & ~(align - 1UL);
        }
    }
}
