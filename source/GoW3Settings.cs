using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GoW3Installer
{
    public sealed class GoW3Settings
    {
        public string Resolution { get; set; } = "1920x1080";
        public string FrameRatePatch { get; set; } = "60 FPS++";
        public int TargetFps { get; set; } = 60;
        public string GlyphStyle { get; set; } = "xbox";
        public int DirectMemoryMb { get; set; } = 2560; // Safe default for Xbox Series S UWP
        public int AudioBufferMs { get; set; } = 80;
        public bool Fullscreen { get; set; } = true;
        public bool StartupBlackScreenFix { get; set; } = true;
        public bool DisableMotionBlur { get; set; } = false;
        public bool DisableDepthOfField { get; set; } = false;
        public bool SkipIntroLogos { get; set; } = true;

        public static string SettingsFilePath => Path.Combine(GameStorage.ImportedGamePath, "gow3-xbox.ini");

        public static GoW3Settings Load()
        {
            var s = new GoW3Settings();
            try
            {
                if (!File.Exists(SettingsFilePath)) return s;
                foreach (var raw in File.ReadAllLines(SettingsFilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "resolution": s.Resolution = val; break;
                        case "framerate_patch": s.FrameRatePatch = val; break;
                        case "target_fps": if (int.TryParse(val, out int fps)) s.TargetFps = fps; break;
                        case "glyph_style": s.GlyphStyle = val; break;
                        case "dmem_mb": if (int.TryParse(val, out int dmem)) s.DirectMemoryMb = dmem; break;
                        case "audio_buffer_ms": if (int.TryParse(val, out int ab)) s.AudioBufferMs = ab; break;
                        case "disable_motion_blur": s.DisableMotionBlur = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "disable_dof": s.DisableDepthOfField = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "skip_intro": s.SkipIntroLogos = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
            var sb = new StringBuilder();
            sb.AppendLine("# God of War III Remastered - Xbox Series S/X UWP Configuration");
            sb.AppendLine($"resolution={Resolution}");
            sb.AppendLine($"framerate_patch={FrameRatePatch}");
            sb.AppendLine($"target_fps={TargetFps}");
            sb.AppendLine($"glyph_style={GlyphStyle}");
            sb.AppendLine($"dmem_mb={DirectMemoryMb}");
            sb.AppendLine($"audio_buffer_ms={AudioBufferMs}");
            sb.AppendLine($"disable_motion_blur={(DisableMotionBlur ? 1 : 0)}");
            sb.AppendLine($"disable_dof={(DisableDepthOfField ? 1 : 0)}");
            sb.AppendLine($"skip_intro={(SkipIntroLogos ? 1 : 0)}");
            File.WriteAllText(SettingsFilePath, sb.ToString(), Encoding.UTF8);

            // Also write gow3.ini compatible with gow3-probe / GoW3NativeStartup
            string gow3IniPath = Path.Combine(GameStorage.ImportedGamePath, "gow3.ini");
            var ini = new StringBuilder();
            ini.AppendLine("[gow3]");
            ini.AppendLine($"GOW3_GAME_DIR={GameStorage.ImportedGamePath}");
            ini.AppendLine($"GOW3_GLYPH_STYLE={GlyphStyle}");
            ini.AppendLine($"GOW3_DMEM_MB={DirectMemoryMb}");
            ini.AppendLine($"GOW3_AUDIO_BUFFER_MS={AudioBufferMs}");
            ini.AppendLine($"GOW3_ENGINE_FPS={TargetFps}");
            ini.AppendLine($"GOW3_FULLSCREEN=1");
            ini.AppendLine($"GOW3_Extra={BuildExtraPatchList()}");
            File.WriteAllText(gow3IniPath, ini.ToString(), Encoding.UTF8);
        }

        public List<string> GetSelectedPatchNames()
        {
            var list = new List<string> { "Bug Fix - Startup Black Screen" };
            if (!string.IsNullOrEmpty(Resolution) && Resolution != "1920x1080 (Native)")
            {
                list.Add("Resolution Patch - " + Resolution);
            }
            if (!string.IsNullOrEmpty(FrameRatePatch) && FrameRatePatch != "Default (60 FPS)")
            {
                list.Add("Unlock FPS");
                list.Add(FrameRatePatch);
            }
            if (DisableMotionBlur) list.Add("Disable Motion Blur");
            if (DisableDepthOfField) list.Add("Disable Depth of Field");
            if (SkipIntroLogos) list.Add("Skip Intro Logos");
            return list;
        }

        public string BuildExtraPatchList()
        {
            return string.Join(";", GetSelectedPatchNames());
        }
    }
}
