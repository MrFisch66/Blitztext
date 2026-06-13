param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $Package
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root "artifacts\windows\$Runtime"

dotnet restore "$root\Blitztext.sln"
dotnet build "$root\Blitztext.sln" --configuration $Configuration --no-restore
dotnet test "$root\Blitztext.sln" --configuration $Configuration --no-build

if (Test-Path $publishDir) {
    $runningFromPublishDir = Get-Process Blitztext -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($publishDir, [System.StringComparison]::OrdinalIgnoreCase) }

    if ($runningFromPublishDir) {
        $ids = ($runningFromPublishDir | Select-Object -ExpandProperty Id) -join ", "
        throw "Blitztext is still running from $publishDir (PID: $ids). Close Blitztext from the tray, then run this script again."
    }

    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish "$root\src\Blitztext.Windows\Blitztext.Windows.csproj" `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output $publishDir

if ($Package) {
    $wapproj = Join-Path $root "src\Blitztext.Packaging\Blitztext.Packaging.wapproj"
    $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) {
        $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    }
    if (-not (Test-Path $msbuild)) {
        throw "MSBuild with Desktop Bridge targets was not found. Install Visual Studio with MSIX Packaging Tools."
    }

    & $msbuild $wapproj `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:UapAppxPackageBuildMode=StoreUpload `
        /p:AppxBundle=Always `
        /p:AppxBundlePlatforms=x64
}

Write-Host "Windows build output: $publishDir"
