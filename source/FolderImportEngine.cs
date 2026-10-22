using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace GoW3Installer
{
    public interface IFolderImportSource
    {
        Task<long?> TryGetSizeAsync(string relativePath);
        Task<Stream> OpenReadAsync(string relativePath);
    }

    public static class FolderImportEngine
    {
        // Scans a selected StorageFolder (e.g. CUSA01715 on USB) and builds the ExpectedFile manifest dynamically
        public static async Task<IList<ExpectedFile>> ScanPs4FolderAsync(StorageFolder root, Action<string> status = null)
        {
            var list = new List<ExpectedFile>();
            await ScanRecursiveAsync(root, "", list, status);
            ImportEngine.ValidateManifest(list, requireHashes: false);
            return list;
        }

        static async Task ScanRecursiveAsync(StorageFolder folder, string prefix, List<ExpectedFile> list, Action<string> status)
        {
            var items = await folder.GetItemsAsync();
            foreach (var item in items)
            {
                string rel = string.IsNullOrEmpty(prefix) ? item.Name : prefix + "/" + item.Name;
                if (item.IsOfType(StorageItemTypes.Folder))
                {
                    // Skip any previous 'out' folder inside the source dump
                    if (rel.Equals("out", StringComparison.OrdinalIgnoreCase)) continue;
                    await ScanRecursiveAsync((StorageFolder)item, rel, list, status);
                }
                else if (item.IsOfType(StorageItemTypes.File))
                {
                    var props = await item.GetBasicPropertiesAsync();
                    list.Add(new ExpectedFile { Path = rel, Size = (long)props.Size });
                    if (list.Count % 25 == 0) status?.Invoke($"Scanned {list.Count} files ({rel})...");
                }
            }
        }

        public static async Task ImportAsync(
            IFolderImportSource source,
            string destinationRoot,
            IList<ExpectedFile> rows,
            long availableBytes,
            Action<long, long, string> progress = null,
            CancellationToken cancel = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            ImportEngine.ValidateManifest(rows, requireHashes: false);
            if (File.Exists(destinationRoot)) throw new IOException("Destination path is an existing file.");
            if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
                throw new IOException("Destination folder is not empty. Delete imported files first.");

            long total = rows.Sum(r => r.Size);
            if (availableBytes < total + ImportEngine.Margin)
                throw new IOException("Not enough free storage for the imported files and safety margin.");

            foreach (var row in rows)
            {
                cancel.ThrowIfCancellationRequested();
                long? actualSize = await source.TryGetSizeAsync(row.Path);
                if (!actualSize.HasValue) throw new InvalidDataException("Missing source file: " + row.Path);
                if (actualSize.Value != row.Size) throw new InvalidDataException("Unexpected file size for " + row.Path);
            }

            Directory.CreateDirectory(destinationRoot);
            long copied = 0;
            progress?.Invoke(0, total, "Starting folder import");
            try
            {
                foreach (var row in rows)
                {
                    cancel.ThrowIfCancellationRequested();
                    string target = ImportEngine.SafeTargetPath(destinationRoot, row.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string temp = target + ".part";
                    using (var input = await source.OpenReadAsync(row.Path))
                    using (var sha = SHA256.Create())
                    {
                        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            copied += ImportEngine.CopyAndHash(input, output, sha, row.Size, row.Path,
                                done => progress?.Invoke(copied + done, total, row.Path), cancel);
                        }
                        row.Hash = ImportEngine.Hex(sha.Hash);
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                }
                ImportEngine.SaveManifest(Path.Combine(destinationRoot, "manifest-hashes.json"), rows);
                File.WriteAllText(Path.Combine(destinationRoot, "import-receipt.txt"),
                    "Verified files: " + rows.Count + Environment.NewLine + "Total bytes: " + total + Environment.NewLine);
            }
            catch
            {
                ImportEngine.TryClean(destinationRoot);
                throw;
            }
        }
    }
}
