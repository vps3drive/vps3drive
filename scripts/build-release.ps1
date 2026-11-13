param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ReleaseDir = Join-Path $RepoRoot "_releases\v$Version"
$PublishDir = Join-Path $RepoRoot "publish\win-x64"
$DepsDir = Join-Path $RepoRoot "installer\deps"

New-Item -ItemType Directory -Path $ReleaseDir, $PublishDir, $DepsDir -Force | Out-Null

$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:LOCALAPPDATA\llvm-mingw\bin;" + $env:PATH
if (-not $env:LIBCLANG_PATH -and (Test-Path "$env:LOCALAPPDATA\libclang\runtimes\win-x64\native")) {
    $env:LIBCLANG_PATH = "$env:LOCALAPPDATA\libclang\runtimes\win-x64\native"
}
if (-not $env:BINDGEN_EXTRA_CLANG_ARGS -and (Test-Path "$env:LOCALAPPDATA\llvm-mingw\include")) {
    $env:BINDGEN_EXTRA_CLANG_ARGS = "-fms-extensions -isystem $env:LOCALAPPDATA\llvm-mingw\include -Dstatic_assert=_Static_assert"
}

Push-Location $RepoRoot
try {
    cargo +stable-x86_64-pc-windows-gnullvm build --release
    if ($LASTEXITCODE -ne 0) { throw "Cargo build failed" }
} finally {
    Pop-Location
}

Remove-Item "$PublishDir\*" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $RepoRoot "target\release\vps3drive-rust.exe") $PublishDir -Force

$WinFspDll = "C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll"
if (Test-Path $WinFspDll) {
    Copy-Item $WinFspDll $PublishDir -Force
}

$PortableStage = Join-Path $RepoRoot "publish\portable-stage"
Remove-Item $PortableStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $PortableStage -Force | Out-Null
Copy-Item "$PublishDir\*" $PortableStage -Recurse -Force

$WinFspMsi = Join-Path $DepsDir "winfsp.msi"
if (Test-Path $WinFspMsi) {
    Copy-Item $WinFspMsi (Join-Path $PortableStage "winfsp.msi") -Force
}

$PortableZip = Join-Path $ReleaseDir "VPS3Drive-Rust-v$Version-win-x64-portable.zip"
Remove-Item $PortableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$PortableStage\*" -DestinationPath $PortableZip

$Iscc = @(
    "C:\Users\$env:USERNAME\AppData\Local\Programs\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($Iscc) {
    & $Iscc "/DMyAppVersion=$Version" (Join-Path $RepoRoot "installer\VPS3Drive-Rust.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
    Move-Item (Join-Path $RepoRoot "VPS3Drive-Rust-Setup-v$Version.exe") (Join-Path $ReleaseDir "VPS3Drive-Rust-Setup-v$Version.exe") -Force
} else {
    Write-Warning "Inno Setup ISCC.exe not found; skipped installer build."
}

Write-Output "Done. Artifacts in $ReleaseDir"
