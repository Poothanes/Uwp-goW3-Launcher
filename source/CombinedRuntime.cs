using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

namespace GoW3Installer
{
    static class CombinedRuntime
    {
        const string LaunchUri = "uwp-gow3-combined-game:play";
        static string PackageRoot => Package.Current.InstalledLocation.Path;
        internal static string RuntimeStateRoot => Path.Combine(ApplicationData.Current.LocalFolder.Path, "CombinedRuntime");
        internal static bool Enabled => File.Exists(Path.Combine(PackageRoot, "StartupContent", "GoW3NativeStartup.exe"));

        internal static void SeedLaunchSettings(GoW3Settings settings)
        {
            Directory.CreateDirectory(RuntimeStateRoot);
            settings.Save();
            // Write a pointer file in LocalFolder\CombinedRuntime so GoW3NativeStartup.exe knows the exact game & out path
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "game-dir.txt"), GameStorage.ImportedGamePath);
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "out-dir.txt"), GameStorage.PreparedOutPath);
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "dmem-mb.txt"), settings.DirectMemoryMb.ToString());
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "glyph-style.txt"), settings.GlyphStyle);
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "target-fps.txt"), settings.TargetFps.ToString());
            File.WriteAllText(Path.Combine(RuntimeStateRoot, "resolution.txt"), settings.Resolution);
        }

        internal static async Task<bool> LaunchAsync(GoW3Settings settings)
        {
            SeedLaunchSettings(settings);
            var options = new LauncherOptions
            {
                TargetApplicationPackageFamilyName = Package.Current.Id.FamilyName
            };
            return await Launcher.LaunchUriAsync(new Uri(LaunchUri), options);
        }
    }
}
