// GoW3NativeStartup.cpp
// Native UWP Host (EntryPoint="GoW3Runtime.App") for God of War III Remastered on Xbox Series S/X.
// Activated via protocol "uwp-gow3-combined-game:play" from GoW3Installer.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static std::string ReadTextFileTrimmed(const std::string& path, const std::string& fallback = "") {
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return fallback;
    char buf[1024] = {0};
    size_t n = std::fread(buf, 1, sizeof(buf) - 1, f);
    std::fclose(f);
    while (n > 0 && (buf[n - 1] == '\r' || buf[n - 1] == '\n' || buf[n - 1] == ' ')) {
        buf[--n] = 0;
    }
    return n > 0 ? std::string(buf) : fallback;
}

static void AppendStartupLog(const std::string& logPath, const char* fmt, ...) {
    FILE* f = std::fopen(logPath.c_str(), "ab");
    if (!f) return;
    va_list args;
    va_start(args, fmt);
    std::vfprintf(f, fmt, args);
    std::fprintf(f, "\r\n");
    va_end(args);
    std::fclose(f);
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    char localAppData[MAX_PATH] = {0};
    DWORD envLen = GetEnvironmentVariableA("LOCALAPPDATA", localAppData, MAX_PATH);
    std::string stateRoot = envLen > 0
        ? (std::string(localAppData) + "\\CombinedRuntime")
        : "D:\\DevelopmentFiles\\GoW3Installer-Imports";

    std::string gameDir = ReadTextFileTrimmed(
        stateRoot + "\\game-dir.txt",
        "D:\\DevelopmentFiles\\GoW3Installer-Imports\\GodOfWar3Remastered");
    std::string outDir = ReadTextFileTrimmed(
        stateRoot + "\\out-dir.txt",
        gameDir + "\\out");
    std::string dmemMb = ReadTextFileTrimmed(stateRoot + "\\dmem-mb.txt", "2560");
    std::string glyphStyle = ReadTextFileTrimmed(stateRoot + "\\glyph-style.txt", "xbox");
    std::string targetFps = ReadTextFileTrimmed(stateRoot + "\\target-fps.txt", "60");

    std::string logPath = gameDir + "\\native-startup.log";
    AppendStartupLog(logPath, "=== GoW3NativeStartup Xbox Series S/X Host ===");
    AppendStartupLog(logPath, "gameDir=%s", gameDir.c_str());
    AppendStartupLog(logPath, "outDir=%s", outDir.c_str());
    AppendStartupLog(logPath, "GOW3_DMEM_MB=%s, GOW3_GLYPH_STYLE=%s, GOW3_ENGINE_FPS=%s",
                     dmemMb.c_str(), glyphStyle.c_str(), targetFps.c_str());

    // Configure environment for gow3-probe / libgow3gpu on Xbox Series S
    _putenv_s("GOW3_GAME_DIR", gameDir.c_str());
    _putenv_s("GOW3_DMEM_MB", dmemMb.c_str());
    _putenv_s("GOW3_GLYPH_STYLE", glyphStyle.c_str());
    _putenv_s("GOW3_ENGINE_FPS", targetFps.c_str());
    _putenv_s("GOW3_AUDIO_BUFFER_MS", "80");
    _putenv_s("GOW3_FULLSCREEN", "1");

    std::string moduleDllPath = outDir + "\\GoW3StartupModule.dll";
    _putenv_s("GOW3_MODULE_DLL", moduleDllPath.c_str());

    // Verify GoW3StartupModule.dll can be mapped with executable permissions via LoadLibraryExW
    std::wstring wModulePath(moduleDllPath.begin(), moduleDllPath.end());
    HMODULE hGuestMod = LoadLibraryExW(wModulePath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (hGuestMod) {
        AppendStartupLog(logPath, "module_load_succeeded=true base=%p", (void*)hGuestMod);
        FreeLibrary(hGuestMod);
    } else {
        AppendStartupLog(logPath, "module_load_failed=%lu path=%s", GetLastError(), moduleDllPath.c_str());
    }

    // Launch gow3-probe.exe (if present in StartupContent) or in-process probe_main
    char exePath[MAX_PATH] = {0};
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string startupDir(exePath);
    size_t slash = startupDir.find_last_of("\\/");
    if (slash != std::string::npos) startupDir.resize(slash);

    std::string probeExe = startupDir + "\\gow3-probe.exe";
    std::string bootLinked = outDir + "\\boot-linked.bin";
    std::string contentBin = outDir + "\\content.bin";
    std::string patchesBin = outDir + "\\patches.bin";
    std::string userDir = gameDir + "\\user";
    CreateDirectoryA(userDir.c_str(), nullptr);

    std::string cmdLine = "\"" + probeExe + "\" \"" + bootLinked + "\""
        + " --content-profile \"" + contentBin + "\""
        + " --app0 \"" + gameDir + "\""
        + " --user \"" + userDir + "\""
        + " --patches \"" + patchesBin + "\"";

    AppendStartupLog(logPath, "Executing: %s", cmdLine.c_str());

    STARTUPINFOA si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};
    std::vector<char> mutableCmd(cmdLine.begin(), cmdLine.end());
    mutableCmd.push_back(0);

    if (CreateProcessA(nullptr, mutableCmd.data(), nullptr, nullptr, FALSE, 0, nullptr, gameDir.c_str(), &si, &pi)) {
        AppendStartupLog(logPath, "gow3-probe started (PID=%lu)", pi.dwProcessId);
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD exitCode = 0;
        GetExitCodeProcess(pi.hProcess, &exitCode);
        AppendStartupLog(logPath, "gow3-probe exited with code %lu", exitCode);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return (int)exitCode;
    } else {
        AppendStartupLog(logPath, "CreateProcessA failed (%lu)", GetLastError());
        return 1;
    }
}
