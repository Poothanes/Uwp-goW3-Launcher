using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GoW3Installer
{
    [DataContract]
    public sealed class BrowserFileEntry
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    public sealed class WifiFolderTransfer
    {
        readonly string destinationRoot;
        readonly long availableBytes;
        readonly Action<long, long, string> progress;
        readonly CancellationToken cancel;
        readonly HashSet<string> completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ExpectedFile> expectedByPath;
        IList<ExpectedFile> activeManifest;
        long totalBytes;
        long completedBytes;
        bool finished;

        public WifiFolderTransfer(string destinationRoot, long availableBytes, Action<long, long, string> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            this.destinationRoot = destinationRoot;
            this.availableBytes = availableBytes;
            this.progress = progress;
            this.cancel = cancel;
        }

        public string Begin(Stream manifestBody)
        {
            cancel.ThrowIfCancellationRequested();
            if (activeManifest != null) throw new InvalidDataException("Wi-Fi folder manifest was already submitted.");
            if (File.Exists(destinationRoot)) throw new IOException("Destination path is an existing file.");
            if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
                throw new IOException("Destination folder is not empty. Delete imported files first.");

            var serializer = new DataContractJsonSerializer(typeof(List<BrowserFileEntry>));
            var submitted = (List<BrowserFileEntry>)serializer.ReadObject(manifestBody);
            if (submitted == null || submitted.Count == 0)
                throw new InvalidDataException("No folder files were submitted.");

            // Normalize paths and strip any top-level folder name (e.g., "CUSA01715/eboot.bin" -> "eboot.bin")
            string prefix = DetectCommonPrefix(submitted);
            var candidates = new List<ExpectedFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in submitted)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
                string norm = item.Path.Replace('\\', '/').TrimStart('/');
                if (!string.IsNullOrEmpty(prefix) && norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    norm = norm.Substring(prefix.Length);
                if (string.IsNullOrWhiteSpace(norm)) continue;

                // Ignore previously generated 'out/' or launcher files if user re-transfers an existing folder
                if (norm.StartsWith("out/", StringComparison.OrdinalIgnoreCase) ||
                    norm.Equals("import-receipt.txt", StringComparison.OrdinalIgnoreCase) ||
                    norm.Equals("manifest-hashes.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                ImportEngine.ValidateRelativePath(norm);
                if (item.Size < 0) throw new InvalidDataException("Negative file size for " + norm);
                if (!seen.Add(norm)) throw new InvalidDataException("Duplicate file in selected folder: " + norm);
                candidates.Add(new ExpectedFile { Path = norm, Size = item.Size });
            }

            // Verify that required PS4 files (eboot.bin, sce_sys/param.sfo, sce_module/libc.prx, sce_module/libSceFios2.prx) exist
            ImportEngine.ValidateManifest(candidates, requireHashes: false);

            totalBytes = candidates.Sum(r => r.Size);
            if (availableBytes < totalBytes + ImportEngine.Margin)
                throw new IOException($"Not enough free storage on Xbox. Need {(totalBytes + ImportEngine.Margin) / (1024 * 1024)} MB.");

            activeManifest = candidates;
            expectedByPath = candidates.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(destinationRoot);
            progress?.Invoke(0, totalBytes, $"Folder checked ({candidates.Count} files) — starting Wi-Fi transfer");

            var sb = new StringBuilder();
            sb.Append("{\"files\":[");
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(candidates[i].Path)).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string DetectCommonPrefix(List<BrowserFileEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e?.Path == null) continue;
                string norm = e.Path.Replace('\\', '/').TrimStart('/');
                if (norm.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)) return "";
                if (norm.EndsWith("/eboot.bin", StringComparison.OrdinalIgnoreCase))
                    return norm.Substring(0, norm.Length - "eboot.bin".Length);
            }
            return "";
        }

        public void ReceiveFile(string relativePath, long contentLength, Stream body)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (activeManifest == null) throw new InvalidDataException("Submit the folder manifest before uploading files.");
                if (finished) throw new InvalidDataException("Wi-Fi folder transfer is already complete.");
                ImportEngine.ValidateRelativePath(relativePath);
                if (!expectedByPath.TryGetValue(relativePath, out var expected))
                    throw new InvalidDataException("Unexpected file path: " + relativePath);
                if (completed.Contains(expected.Path))
                    throw new InvalidDataException("Duplicate file upload: " + expected.Path);
                if (contentLength != expected.Size)
                    throw new InvalidDataException("Unexpected file size for " + expected.Path);

                string target = ImportEngine.SafeTargetPath(destinationRoot, expected.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string temp = target + ".part";
                using (var sha = SHA256.Create())
                {
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        ImportEngine.CopyAndHash(body, output, sha, expected.Size, expected.Path,
                            done => progress?.Invoke(completedBytes + done, totalBytes, expected.Path), cancel);
                    }
                    expected.Hash = ImportEngine.Hex(sha.Hash);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                completed.Add(expected.Path);
                completedBytes += expected.Size;
                progress?.Invoke(completedBytes, totalBytes, expected.Path);
            }
            catch
            {
                Abort();
                throw;
            }
        }

        public void Complete()
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (activeManifest == null) throw new InvalidDataException("No Wi-Fi folder transfer has started.");
                if (completed.Count != activeManifest.Count || completedBytes != totalBytes)
                    throw new InvalidDataException($"Transfer is incomplete ({completed.Count}/{activeManifest.Count} files received).");

                ImportEngine.SaveManifest(Path.Combine(destinationRoot, "manifest-hashes.json"), activeManifest);
                File.WriteAllText(Path.Combine(destinationRoot, "import-receipt.txt"),
                    "Verified files: " + activeManifest.Count + Environment.NewLine + "Total bytes: " + totalBytes + Environment.NewLine);
                finished = true;
            }
            catch
            {
                Abort();
                throw;
            }
        }

        public void Abort()
        {
            if (!finished) ImportEngine.TryClean(destinationRoot);
        }

        static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
