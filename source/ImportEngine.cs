using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GoW3Installer
{
    [DataContract]
    public sealed class ExpectedFile
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "sha256")] public string Hash { get; set; }
    }

    public static class ImportEngine
    {
        public const long Margin = 512L * 1024 * 1024; // 512 MB safety margin

        // Core files required in every valid God of War III Remastered PS4 dump (CUSA01715 / CUSA01623 / CUSA01720)
        public static readonly string[] RequiredPs4Files = new[]
        {
            "eboot.bin",
            "sce_sys/param.sfo",
            "sce_module/libc.prx",
            "sce_module/libSceFios2.prx"
        };

        public static IList<ExpectedFile> LoadManifest(Stream json)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<ExpectedFile>));
            var rows = (IList<ExpectedFile>)serializer.ReadObject(json);
            ValidateManifest(rows, requireHashes: false);
            return rows;
        }

        public static void SaveManifest(string path, IList<ExpectedFile> rows)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<ExpectedFile>));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, rows.ToList());
            }
        }

        public static void ValidateManifest(IList<ExpectedFile> rows, bool requireHashes = false)
        {
            if (rows == null || rows.Count == 0) throw new InvalidDataException("Empty file manifest.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                ValidateRelativePath(row.Path);
                if (row.Size < 0) throw new InvalidDataException("Negative file size in manifest: " + row.Path);
                if (requireHashes && (string.IsNullOrEmpty(row.Hash) || row.Hash.Length != 64))
                    throw new InvalidDataException("Invalid SHA-256 in manifest: " + row.Path);
                if (!seen.Add(row.Path)) throw new InvalidDataException("Duplicate manifest path: " + row.Path);
            }

            foreach (var req in RequiredPs4Files)
            {
                if (!seen.Contains(req))
                {
                    throw new InvalidDataException(
                        $"Missing required PS4 file '{req}'. Select your unpacked CUSA01715 / CUSA01623 folder containing eboot.bin, sce_sys/param.sfo, sce_module/, and dvdroot_ps4/.");
                }
            }
        }

        public static void ImportZip(Stream archiveStream, string destinationRoot, long availableBytes, Action<long, long, string> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            if (File.Exists(destinationRoot)) throw new IOException("Destination path is an existing file.");
            if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
                throw new IOException("Destination folder is not empty. Delete imported files first.");

            using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, true))
            {
                // Detect optional top-level folder prefix (e.g. CUSA01715/eboot.bin)
                string rootPrefix = DetectZipRootPrefix(zip);
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                var rows = new List<ExpectedFile>();

                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue;
                    string rel = NormalizeEntryPath(entry.FullName);
                    if (!string.IsNullOrEmpty(rootPrefix) && rel.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring(rootPrefix.Length);
                    if (string.IsNullOrEmpty(rel)) continue;
                    ValidateRelativePath(rel);
                    if (entries.ContainsKey(rel)) throw new InvalidDataException("Duplicate ZIP entry: " + rel);
                    entries[rel] = entry;
                    rows.Add(new ExpectedFile { Path = rel, Size = entry.Length });
                }

                ValidateManifest(rows, requireHashes: false);
                long total = rows.Sum(r => r.Size);
                if (availableBytes < total + Margin)
                    throw new IOException($"Not enough free storage on Xbox. Required: {(total + Margin) / (1024 * 1024)} MB.");

                Directory.CreateDirectory(destinationRoot);
                long copied = 0;
                progress?.Invoke(0, total, "Starting ZIP import");
                try
                {
                    foreach (var row in rows)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var entry = entries[row.Path];
                        string target = SafeTargetPath(destinationRoot, row.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string temp = target + ".part";
                        using (var input = entry.Open())
                        using (var sha = SHA256.Create())
                        {
                            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                copied += CopyAndHash(input, output, sha, row.Size, row.Path, done => progress?.Invoke(copied + done, total, row.Path), cancel);
                            }
                            row.Hash = Hex(sha.Hash);
                        }
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(temp, target);
                    }
                    SaveManifest(Path.Combine(destinationRoot, "manifest-hashes.json"), rows);
                    File.WriteAllText(Path.Combine(destinationRoot, "import-receipt.txt"),
                        "Verified files: " + rows.Count + Environment.NewLine + "Total bytes: " + total + Environment.NewLine);
                }
                catch
                {
                    TryClean(destinationRoot);
                    throw;
                }
            }
        }

        static string DetectZipRootPrefix(ZipArchive zip)
        {
            foreach (var entry in zip.Entries)
            {
                string norm = NormalizeEntryPath(entry.FullName);
                if (norm.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)) return "";
                if (norm.EndsWith("/eboot.bin", StringComparison.OrdinalIgnoreCase))
                    return norm.Substring(0, norm.Length - "eboot.bin".Length);
            }
            return "";
        }

        internal static long CopyAndHash(Stream input, Stream output, HashAlgorithm sha, long expectedSize, string label, Action<long> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            byte[] buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                total += read;
                if (total > expectedSize) throw new InvalidDataException("File grew past expected size: " + label);
                sha.TransformBlock(buffer, 0, read, null, 0);
                output.Write(buffer, 0, read);
                progress?.Invoke(total);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (total != expectedSize) throw new InvalidDataException("Unexpected size while copying " + label);
            return total;
        }

        public static string Digest(Stream stream)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static string NormalizeEntryPath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            ValidateRelativePath(normalized);
            return normalized;
        }

        internal static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Blank relative path.");
            if (path.Contains("\\") || path.StartsWith("/") || path.EndsWith("/") || path.Contains(":"))
                throw new InvalidDataException("Unsafe path: " + path);
            foreach (var part in path.Split('/'))
                if (part == "" || part == "." || part == "..")
                    throw new InvalidDataException("Unsafe path segment: " + path);
        }

        internal static string SafeTargetPath(string root, string relative)
        {
            ValidateRelativePath(relative);
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString())) fullRoot += Path.DirectorySeparatorChar;
            string fullTarget = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Blocked path outside destination: " + relative);
            return fullTarget;
        }

        internal static void TryClean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
