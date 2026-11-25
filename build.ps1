param([switch]$Test, [switch]$Integration, [switch]$Package, [switch]$SingleExe, [string]$ReleaseName = 'VideoArchive-win-x64')
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
    if ($SingleExe) {
        $artifactsRoot = Join-Path $projectRoot 'artifacts'
        $stage = Join-Path $artifactsRoot ('VideoArchive-single-' + [guid]::NewGuid().ToString('N'))
        $content = Join-Path $stage 'content'
        $tools = Join-Path $content 'tools'
        $licenses = Join-Path $content 'licenses'
        New-Item -ItemType Directory -Force -Path $tools,$licenses | Out-Null
        $toolHashes = @{
            ffmpeg = '09948D4CDD0650DA6FF5A87577469F2A218DC2615AE379F8F734D24C49DE0F73'
            ffprobe = 'A6618E99BB58869DED3C6F37B53AA1A8D701C3591DBB7B5B317D47369C112BE2'
        }
        $commands = @{}
        foreach ($name in $toolHashes.Keys) {
            $commands[$name] = (Get-Command ($name + '.exe') -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        }
        if ($toolHashes.Keys.Where({ !$commands[$_] -or (Get-FileHash -LiteralPath $commands[$_]).Hash -ne $toolHashes[$_] }).Count -gt 0) {
            $archive = Join-Path $stage 'ffmpeg.zip'
            Invoke-WebRequest -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.1/ffmpeg-8.1.1-full_build.zip' -OutFile $archive
            if ((Get-FileHash -LiteralPath $archive).Hash -ne '49B28C5F16ADDD40239A66949973458769B7056FB7752C30AC0D53389D09A552') {
                throw 'FFmpeg archive checksum mismatch.'
            }
            Expand-Archive -LiteralPath $archive -DestinationPath $stage
            foreach ($name in $toolHashes.Keys) {
                $commands[$name] = Join-Path $stage "ffmpeg-8.1.1-full_build/bin/$name.exe"
            }
        }
        foreach ($name in $toolHashes.Keys) {
            if ((Get-FileHash -LiteralPath $commands[$name]).Hash -ne $toolHashes[$name]) { throw "$name checksum mismatch." }
            Copy-Item -LiteralPath $commands[$name] -Destination (Join-Path $tools "$name.exe")
        }
        $distribution = Split-Path (Split-Path $commands['ffmpeg'] -Parent) -Parent
        Copy-Item -LiteralPath (Join-Path $distribution 'LICENSE'),(Join-Path $distribution 'README.txt'),
            (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $licenses
        $sourceUri = 'https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz'
        $sourceCache = Join-Path $projectRoot '.tools/ffmpeg-8.1.1-source.tar.xz'
        if (!(Test-Path -LiteralPath $sourceCache)) { Invoke-WebRequest -Uri $sourceUri -OutFile $sourceCache }
        if ((Get-FileHash -LiteralPath $sourceCache).Hash -ne 'B6863ADDE98898F42602017462871B5F6333E65AEC803FDD7A6308639C52EDF3') {
            throw 'FFmpeg source checksum mismatch.'
        }
        Copy-Item -LiteralPath $sourceCache -Destination $licenses
        $publish = Join-Path $stage 'published'
        $publishOptions = @('-p:PublishSingleFile=true','-p:IncludeAllContentForSelfExtract=true','-p:EnableCompressionInSingleFile=true',
            '-p:PublishTrimmed=false','-p:DebugType=None','-p:DebugSymbols=false',"-p:VideoArchivePackageContent=$content")
        & $sdkExecutable publish 'src/VideoArchive/VideoArchive.csproj' -c Release -r win-x64 --self-contained true @publishOptions -o $publish --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Single EXE publish failed.' }
        $files = @(Get-ChildItem -LiteralPath $publish -Force)
        if ($files.Count -ne 1 -or $files[0].Name -ne 'VideoArchive.exe') { throw 'Expected only VideoArchive.exe.' }
        $target = Join-Path $artifactsRoot ($ReleaseName + '.exe')
        Move-Item -LiteralPath $files[0].FullName -Destination $target -Force
        Write-Host "Published: $target"
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
