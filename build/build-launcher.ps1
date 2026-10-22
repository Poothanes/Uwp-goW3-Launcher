param(
    [string]$VisualStudio = 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise',
    [string]$CombinedRuntimePayloadRoot = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path $VisualStudio)) {
    $vsCommunity = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
    if (Test-Path $vsCommunity) { $VisualStudio = $vsCommunity }
}

$msbuild = Join-Path $VisualStudio 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found at $msbuild. Install Visual Studio 2022 with UWP workload or run the GitHub Actions workflow (.github/workflows/build-msix.yml)."
}

& (Join-Path $PSScriptRoot 'make-icons.ps1')

$payloadDir = if ($CombinedRuntimePayloadRoot) { $CombinedRuntimePayloadRoot } else { Join-Path $repo 'runtime-payload' }
$startupDir = Join-Path $payloadDir 'StartupContent'
New-Item -ItemType Directory -Path $startupDir -Force | Out-Null

# Compile GoW3NativeStartup.exe using MSVC cl.exe if available
$vcvars = Join-Path $VisualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$nativeSrc = Join-Path $repo 'native\GoW3NativeStartup.cpp'
$nativeExe = Join-Path $startupDir 'GoW3NativeStartup.exe'
if (Test-Path $vcvars) {
    cmd /c "`"$vcvars`" && cl.exe /nologo /O2 /EHsc `"$nativeSrc`" /link /SUBSYSTEM:WINDOWS user32.lib /OUT:`"$nativeExe`""
}

$options = @(
    '/restore',
    '/m:2',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    '/p:AppxPackageSigningEnabled=false',
    '/p:UapAppxPackageBuildMode=SideloadOnly',
    '/p:CombinedRuntimeBuild=true',
    "/p:CombinedRuntimePayloadRoot=$payloadDir",
    '/verbosity:minimal',
    '/nologo'
)

& $msbuild (Join-Path $repo 'source\GoW3Installer.csproj') @options
if ($LASTEXITCODE -ne 0) { throw 'UWP-GoW3-Launcher MSIX build failed.' }
Write-Output 'UWP-GoW3-Launcher MSIX build complete under packages/!'
