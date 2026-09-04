<#
.SYNOPSIS
  Builds, tests and publishes every executable in this repo as a SINGLE self-contained file.

.DESCRIPTION
  One .exe per app, with no .dll beside it and no .NET install needed on the target machine.
  That costs size - each exe carries its own runtime - and it is the point: these are handed to
  people who should not have to install anything.

  How the "no DLLs" part is actually achieved, because each switch matters:

    PublishSingleFile                    packs the managed assemblies into the exe
    IncludeNativeLibrariesForSelfExtract native libraries too (Avalonia and SkiaSharp ship several;
                                         without this they land beside the exe as loose .dll files)
    SatelliteResourceLanguages=en        stops localised resource assemblies creating one
                                         subdirectory per language, each holding a .dll
    DebugType=none                       no .pdb beside the exe
    EnableCompressionInSingleFile        compresses the bundle; roughly halves the size

  NOT used, deliberately: PublishTrimmed. Avalonia and the protocol registries resolve types by
  reflection, so trimming removes code that is only ever reached that way and the failure appears
  at runtime, in the app, not here. Do not add it without testing every window.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Runtime
  RID to publish for. Default win-x64. linux-x64 and osx-arm64 also work for the console apps.

.PARAMETER Output
  Where the exes go. Default: publish\<Runtime> in the repo root.

.PARAMETER FrameworkDependent
  Publish small exes that need a matching .NET runtime installed, instead of self-contained ones.

.PARAMETER SkipTests
  Do not run the test suites first. The default is to run them and stop on failure - publishing
  binaries nobody tested is how a bad build reaches a machine.

.PARAMETER Clean
  Delete the output directory first.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Configuration Debug -SkipTests
  .\build.ps1 -Runtime linux-x64 -Output E:\drop
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = '',
    [switch]$FrameworkDependent,
    [switch]$SkipTests,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root (Join-Path 'publish' $Runtime) }

# nodeReuse:false on every call. Node reuse is ON by default and leaves MSBuild worker processes
# behind; each one holds a DLL open, so the NEXT build silently keeps the old binary and the tests
# then pass against stale code.
$common = @('-nodeReuse:false')

function Write-Head($text) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- what to publish

# Every project that produces an executable, found by reading the csproj rather than by keeping a
# list here that would rot the next time a project is added. Test projects are excluded: they are
# OutputType Exe under the modern SDK but they are not apps.
function Get-ExecutableProjects {
    $found = @()
    foreach ($proj in Get-ChildItem $root -Recurse -Filter *.csproj -File) {
        if ($proj.FullName -match '\\(obj|bin)\\') { continue }
        $text = Get-Content $proj.FullName -Raw
        if ($text -notmatch '<OutputType>\s*(Win)?Exe\s*</OutputType>') { continue }
        if ($text -match '<IsTestProject>\s*true\s*</IsTestProject>') { continue }
        if ($proj.FullName -match '\\tests\\' -or $proj.BaseName -match '\.Tests$') { continue }

        $name = if ($text -match '<AssemblyName>\s*([^<]+?)\s*</AssemblyName>') { $Matches[1] } else { $proj.BaseName }
        $found += [pscustomobject]@{ Name = $name; Path = $proj.FullName }
    }
    return $found | Sort-Object Name
}

$projects = Get-ExecutableProjects
if (-not $projects) { throw "no executable projects found under $root" }

Write-Head "$(Split-Path -Leaf $root) - $($projects.Count) executable(s), $Configuration, $Runtime"
$projects | ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.Name, $_.Path.Substring($root.Length + 1)) }

if ($Clean -and (Test-Path $Output)) {
    Write-Host "cleaning $Output"
    Remove-Item $Output -Recurse -Force
}

# ---------------------------------------------------------------- build and test

Write-Head 'Restore and build'
dotnet build -c $Configuration @common
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (-not $SkipTests) {
    Write-Head 'Test'
    # The same category filter CI uses: everything that needs Azure, an external TNFS daemon, a
    # real network, a spawned process or a human is excluded.
    dotnet test -c $Configuration --no-build @common `
        --filter "Category!=Azure&Category!=ExternalTNFSD&Category!=RealNetwork&Category!=Manual&Category!=ProcessSpawn"
    if ($LASTEXITCODE -ne 0) { throw 'tests failed - nothing published. Re-run with -SkipTests only if you know why.' }
}

# ---------------------------------------------------------------- publish

$selfContained = -not $FrameworkDependent

$publishArgs = @(
    '-c', $Configuration
    '-r', $Runtime
    '--self-contained', $selfContained.ToString().ToLowerInvariant()
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:SatelliteResourceLanguages=en'
    '-p:DebugType=none'
    '-p:GenerateDocumentationFile=false'
)

New-Item -ItemType Directory -Force $Output | Out-Null

foreach ($p in $projects) {
    Write-Head "Publish $($p.Name)"
    dotnet publish $p.Path @publishArgs -o $Output @common
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $($p.Name)" }
}

# ---------------------------------------------------------------- report

Write-Head 'Published'

$exes = Get-ChildItem $Output -Filter *.exe -File | Sort-Object Name
if (-not $exes) { $exes = Get-ChildItem $Output -File | Where-Object { -not $_.Extension } | Sort-Object Name }

$exes | ForEach-Object {
    Write-Host ("  {0,-22} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}

# Loose DLLs beside the exes mean the single-file switches did not do their job. Say so loudly
# rather than let someone copy one exe to another machine and find out there.
$loose = Get-ChildItem $Output -Recurse -File | Where-Object { $_.Extension -in '.dll', '.pdb' }
Write-Host ''
if ($loose) {
    Write-Host "  WARNING: $($loose.Count) loose .dll/.pdb file(s) in the output:" -ForegroundColor Yellow
    $loose | Select-Object -First 15 | ForEach-Object {
        Write-Host ("    " + $_.FullName.Substring($Output.Length + 1)) -ForegroundColor Yellow
    }
    Write-Host '  These are NOT bundled. The exes are not standalone.' -ForegroundColor Yellow
} else {
    Write-Host '  No loose .dll or .pdb - every exe is standalone.' -ForegroundColor Green
}

Write-Host ''
Write-Host "  Output: $Output"
Write-Host ''
