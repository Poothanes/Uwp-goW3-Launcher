$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$binSource = Join-Path (Split-Path $repo -Parent) 'bin'
$startupDest = Join-Path $repo 'runtime-payload\StartupContent'

New-Item -ItemType Directory -Path $startupDest -Force | Out-Null

if (-not (Test-Path $binSource)) {
    throw "No se encontró la carpeta bin en $binSource"
}

$count = 0
foreach ($file in Get-ChildItem -Path $binSource -File) {
    $destFile = Join-Path $startupDest $file.Name
    if (Test-Path $destFile) { Remove-Item $destFile -Force }
    try {
        # Usar HardLink para no consumir espacio adicional en el disco C:
        New-Item -ItemType HardLink -Path $destFile -Target $file.FullName | Out-Null
    } catch {
        Copy-Item -LiteralPath $file.FullName -Destination $destFile -Force
    }
    $count++
}
Write-Output "Vinculados $count archivos del runtime desde $binSource hacia $startupDest (0 MB extra en disco usando HardLinks)."
