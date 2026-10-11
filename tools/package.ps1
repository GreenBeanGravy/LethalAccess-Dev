<#
Builds LethalAccess and packs dist\LethalAccess.zip, the asset the updater
installs. Paths in the zip are relative to the game folder, and VERSION at its
root names the release. Documentation (.md) never goes in.

    .\tools\package.ps1                 build and pack
    .\tools\package.ps1 -Publish        also create the GitHub release v<VERSION>
    .\tools\package.ps1 -Publish -Beta  publish it as a pre-release (Beta)

The game and BepInEx assemblies to compile against come from -GameRef, which
holds Managed\ and BepInEx\core\ (default: ..\_work\gameref beside the repo).
Publishing needs the GitHub CLI (gh), signed in.
#>
param(
    [switch]$Publish,
    [switch]$Beta,
    [switch]$NoBuild,
    [string]$GameRef = (Join-Path $PSScriptRoot '..\..\_work\gameref')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION holds '$version'. It must be MAJOR.MINOR.PATCH."
}

$plugin = Get-Content (Join-Path $root 'LethalAccess Remake\Plugin.cs') -Raw
if ($plugin -notmatch 'modVersion\s*=\s*"([^"]+)"' -or $Matches[1] -ne $version) {
    throw "Plugin.cs modVersion does not match VERSION ($version). Change both together."
}

if (-not $NoBuild) {
    $gameRef = Resolve-Path $GameRef
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild was not found. Install Visual Studio or its Build Tools.' }

    & dotnet build (Join-Path $root 'UnityA11y\UnityA11y.csproj') -c Release -nologo -v q `
        "-p:ManagedDir=$gameRef\Managed" "-p:BepInExDir=$gameRef\BepInEx\core"
    if ($LASTEXITCODE) { throw 'UnityA11y did not build.' }

    & $msbuild (Join-Path $root 'LethalAccess Remake.sln') /t:Build /p:Configuration=Release /nologo /v:m
    if ($LASTEXITCODE) { throw 'LethalAccess did not build.' }
}

# Game folder relative path => file in the repository.
$files = [ordered]@{
    'BepInEx/plugins/LethalAccess/LethalAccess Remake.dll'          = 'LethalAccess Remake\bin\Release\LethalAccess Remake.dll'
    'BepInEx/plugins/LethalAccess/UnityA11y.dll'                    = 'UnityA11y\bin\Release\UnityA11y.dll'
    'BepInEx/plugins/LethalSpeechOutput/Lethal Speech Output.dll'   = 'Lethal Speech Output.dll'
}
Get-ChildItem (Join-Path $root 'LethalAccess Remake\LethalAccessAssets') -File | ForEach-Object {
    $files["BepInEx/plugins/LethalAccess/LethalAccessAssets/$($_.Name)"] = "LethalAccess Remake\LethalAccessAssets\$($_.Name)"
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zipPath = Join-Path $dist 'LethalAccess.zip'
Remove-Item $zipPath -ErrorAction SilentlyContinue

# ZipFile rather than Compress-Archive: Windows PowerShell 5.1 writes
# backslashes into entry names.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $entry = $zip.CreateEntry('VERSION')
    $w = New-Object System.IO.StreamWriter($entry.Open())
    $w.Write("$version`n")
    $w.Dispose()
    foreach ($name in $files.Keys) {
        if ($name -like '*.md') { continue }
        $src = Join-Path $root $files[$name]
        if (-not (Test-Path $src)) { throw "$src is missing. Build first, or drop -NoBuild." }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $src, $name, 'Optimal') | Out-Null
    }
} finally {
    $zip.Dispose()
}

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Write-Output "Packed $zipPath"
Write-Output "LethalAccess $version, SHA-256 $hash"

if ($Publish) {
    # gh tags the newest commit of the default branch on GitHub, so push first.
    # The notes are the top CHANGELOG.txt entry, which must be for this version.
    $lines = Get-Content (Join-Path $root 'CHANGELOG.txt')
    $header = '^\d{1,2}/\d{1,2}/\d{4} #\d+.*:\s*$'
    $start = 0
    while ($start -lt $lines.Count -and $lines[$start] -notmatch $header) { $start++ }
    if ($start -ge $lines.Count -or $lines[$start] -notmatch [regex]::Escape("($version")) {
        throw "The top CHANGELOG.txt entry is not for $version."
    }
    $end = $start + 1
    while ($end -lt $lines.Count -and $lines[$end] -notmatch $header) { $end++ }
    $notes = @('New to LethalAccess? Download LethalAccessUpdater.exe from the latest updater release and run it. Already have it? Run it to update.', '') + $lines[($start + 1)..($end - 1)]
    $notesPath = Join-Path $env:TEMP "LethalAccess-$version-notes.txt"
    ($notes -join "`n").Trim() | Set-Content -Encoding utf8 $notesPath
    $ghArgs = @('release', 'create', "v$version", $zipPath, '--title', "LethalAccess $version", '--notes-file', $notesPath)
    if ($Beta) { $ghArgs += '--prerelease' }
    & gh @ghArgs
    if ($LASTEXITCODE) { throw 'The release was not created.' }
}
