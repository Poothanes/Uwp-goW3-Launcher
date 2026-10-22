using System.IO;
using Windows.Storage;
using Windows.System.Profile;

namespace GoW3Installer
{
    static class GameStorage
    {
        // On Xbox Developer Mode, D:\DevelopmentFiles has full read/write space on the dev storage partition
        // and survives app updates/reinstalls so the 39+ GB PS4 dump does not need to be re-transferred.
        internal static string Root =>
            AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox" && Directory.Exists(@"D:\DevelopmentFiles")
                ? @"D:\DevelopmentFiles\GoW3Installer-Imports"
                : Path.Combine(ApplicationData.Current.LocalFolder.Path, "Imports");

        internal static string ImportedGamePath => Path.Combine(Root, "GodOfWar3Remastered");

        internal static string PreparedOutPath => Path.Combine(ImportedGamePath, "out");
    }
}
