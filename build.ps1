param([switch]$Test, [switch]$Integration, [switch]$Package, [string]$ReleaseName = 'VideoArchive-win-x64')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = $PSScriptRoot
if ($ReleaseName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'ReleaseName must be a simple folder name.' }
$sdkExecutable = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExecutable)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot '.tools') | Out-Null
    $installer = Join-Path $projectRoot '.tools\dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Channel 10.0 -InstallDir (Join-Path $projectRoot '.tools\dotnet') -NoPath
}
Push-Location $projectRoot
try {
    & $sdkExecutable build 'src\VideoArchive\VideoArchive.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Test -or $Integration) {
        $testArguments = @('run', '--project', 'tests\VideoArchive.Tests', '-c', 'Release')
        if ($Integration) { $testArguments += @('--', '--integration') }
        & $sdkExecutable @testArguments
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    if ($Package) {
        $publishDirectory = Join-Path $projectRoot ('artifacts\' + $ReleaseName)
        & $sdkExecutable publish 'src\VideoArchive\VideoArchive.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        $toolDirectory = Join-Path $publishDirectory 'tools'
        $licenseDirectory = Join-Path $publishDirectory 'licenses'
        New-Item -ItemType Directory -Force -Path $toolDirectory,$licenseDirectory | Out-Null
        foreach ($toolName in @('ffmpeg','ffprobe')) {
            $toolPath = (Get-Command $toolName -ErrorAction Stop).Source
            Copy-Item -LiteralPath $toolPath -Destination (Join-Path $toolDirectory ($toolName + '.exe')) -Force
        }
        $ffmpegRoot = Split-Path (Split-Path (Get-Command ffmpeg).Source -Parent) -Parent
        $distributionLicense = Join-Path $ffmpegRoot 'LICENSE'
        if (Test-Path -LiteralPath $distributionLicense) { Copy-Item -LiteralPath $distributionLicense -Destination (Join-Path $licenseDirectory 'FFmpeg-LICENSE.txt') -Force }
        Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'README.txt') -Destination (Join-Path $licenseDirectory 'FFmpeg-distribution-README.txt') -Force
        & (Join-Path $toolDirectory 'ffmpeg.exe') -version 2>&1 | Out-File -LiteralPath (Join-Path $licenseDirectory 'FFmpeg-build.txt') -Encoding utf8
        $sourceArchive = Join-Path $licenseDirectory 'ffmpeg-8.1.1-source.tar.xz'
        if (-not (Test-Path -LiteralPath $sourceArchive)) {
            $existingSource = Join-Path $projectRoot 'artifacts\VideoArchive-win-x64\licenses\ffmpeg-8.1.1-source.tar.xz'
            if (Test-Path -LiteralPath $existingSource) { Copy-Item -LiteralPath $existingSource -Destination $sourceArchive }
            else { Invoke-WebRequest -Uri 'https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz' -OutFile $sourceArchive }
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $licenseDirectory -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory -Force
        foreach ($document in @('CHANGELOG.md','VERIFICATION.md','CONTRIBUTING.md','SECURITY.md','LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $publishDirectory -Force }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $licenseDirectory 'VideoArchive-LICENSE.txt') -Force
        $runtimeLicense = Join-Path $projectRoot '.tools\dotnet\LICENSE.txt'
        if (Test-Path -LiteralPath $runtimeLicense) { Copy-Item -LiteralPath $runtimeLicense -Destination (Join-Path $licenseDirectory 'dotnet-LICENSE.txt') -Force }
        foreach ($notice in @('THIRD-PARTY-NOTICES.txt','ThirdPartyNotices.txt')) {
            $noticePath = Join-Path $projectRoot ('.tools\dotnet\' + $notice)
            if (Test-Path -LiteralPath $noticePath) { Copy-Item -LiteralPath $noticePath -Destination (Join-Path $licenseDirectory 'dotnet-THIRD-PARTY-NOTICES.txt') -Force }
        }
        $zipPath = Join-Path $projectRoot ('artifacts\' + $ReleaseName + '.zip')
        Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipPath -Force
        $sourcePackage = Join-Path $projectRoot 'artifacts\source-package\VideoArchive-source'
        New-Item -ItemType Directory -Force -Path $sourcePackage | Out-Null
        $sourceFiles = @('.gitignore', '.gitattributes', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'VERIFICATION.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'SECURITY.md', 'build.ps1')
        $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests'),(Join-Path $projectRoot 'docs'),(Join-Path $projectRoot '.github') -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) }
        foreach ($relative in $sourceFiles) {
            $target = Join-Path $sourcePackage $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath (Join-Path $projectRoot $relative) -Destination $target -Force
        }
        Compress-Archive -LiteralPath $sourcePackage -DestinationPath (Join-Path $projectRoot 'artifacts\VideoArchive-source.zip') -Force
        Write-Host "Published: $zipPath"
    }
} finally { Pop-Location }
