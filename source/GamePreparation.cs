using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Windows.ApplicationModel;

namespace GoW3Installer
{
    static class GamePreparation
    {
        static string PackageRoot => Package.Current.InstalledLocation.Path;
        internal static string PatchXmlPath => Path.Combine(PackageRoot, "Patches", "God_of_War_III_Remastered.xml");

        internal static bool IsImported()
        {
            string root = GameStorage.ImportedGamePath;
            return File.Exists(Path.Combine(root, "import-receipt.txt")) &&
                   File.Exists(Path.Combine(root, "eboot.bin")) &&
                   File.Exists(Path.Combine(root, "sce_sys", "param.sfo"));
        }

        internal static bool IsPrepared()
        {
            string outDir = GameStorage.PreparedOutPath;
            return File.Exists(Path.Combine(outDir, "prepared-ok.txt")) &&
                   File.Exists(Path.Combine(outDir, "boot-linked.bin")) &&
                   File.Exists(Path.Combine(outDir, "patches.bin")) &&
                   File.Exists(Path.Combine(outDir, "content.bin")) &&
                   File.Exists(Path.Combine(outDir, "GoW3StartupModule.dll"));
        }

        internal static Ps4SfoInfo ReadImportedSfo()
        {
            string sfoPath = Path.Combine(GameStorage.ImportedGamePath, "sce_sys", "param.sfo");
            if (!File.Exists(sfoPath)) return new Ps4SfoInfo();
            return GoW3PreparationEngine.ParseSfo(File.ReadAllBytes(sfoPath));
        }

        internal static void VerifyImportedFiles(Action<long, long, string> progress, CancellationToken cancel)
        {
            string root = GameStorage.ImportedGamePath;
            string manifestPath = Path.Combine(root, "manifest-hashes.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("Missing manifest-hashes.json. Import the game folder first.");

            using (var stream = File.OpenRead(manifestPath))
            {
                var rows = ImportEngine.LoadManifest(stream);
                long total = rows.Sum(r => r.Size);
                long done = 0;
                foreach (var row in rows)
                {
                    cancel.ThrowIfCancellationRequested();
                    string path = ImportEngine.SafeTargetPath(root, row.Path);
                    if (!File.Exists(path))
                        throw new FileNotFoundException("Missing imported file: " + row.Path);
                    var info = new FileInfo(path);
                    if (info.Length != row.Size)
                        throw new InvalidDataException($"Size mismatch on {row.Path} (expected {row.Size}, found {info.Length}).");

                    if (!string.IsNullOrEmpty(row.Hash))
                    {
                        using (var fs = File.OpenRead(path))
                        using (var sha = SHA256.Create())
                        {
                            byte[] buf = new byte[1024 * 1024];
                            int read;
                            long fileDone = 0;
                            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                sha.TransformBlock(buf, 0, read, null, 0);
                                fileDone += read;
                                progress?.Invoke(done + fileDone, total, "Verifying " + row.Path);
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            string actual = ImportEngine.Hex(sha.Hash);
                            if (!string.Equals(actual, row.Hash, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("SHA-256 mismatch on " + row.Path);
                        }
                    }
                    done += row.Size;
                    progress?.Invoke(done, total, "Verified " + row.Path);
                }
            }
        }

        internal static void Prepare(GoW3Settings settings, Action<string> log, CancellationToken cancel)
        {
            if (!IsImported())
                throw new InvalidOperationException("Import your God of War III Remastered folder (CUSA01715 / CUSA01623) over Wi-Fi or USB before preparing.");

            GoW3PreparationEngine.PrepareAll(
                GameStorage.ImportedGamePath,
                GameStorage.PreparedOutPath,
                PatchXmlPath,
                settings,
                log,
                cancel);
        }
    }
}
