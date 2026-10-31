$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "SpotiFetch.csproj"
$resolvedRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path

# Keep SDK first-run files and NuGet packages outside the repository. The CLI
# creates a .dotnet directory inside DOTNET_CLI_HOME, so prepare both levels
# before launching dotnet (otherwise first-run setup can fail on fresh clones).
$userBuildData = Join-Path $env:LOCALAPPDATA "SpotiFetch\Build"
$dependencyPath = Join-Path $userBuildData "Dependencies"
$buildWorkPath = Join-Path $userBuildData ("build-" + [Guid]::NewGuid().ToString("N"))
$env:DOTNET_CLI_HOME = Join-Path $userBuildData "DotnetCli"
$env:NUGET_PACKAGES = Join-Path $userBuildData "NuGet\packages"
$intermediatePath = Join-Path $buildWorkPath "obj"
$compileStagePath = Join-Path $buildWorkPath "publish"
$baseOutputPath = Join-Path $buildWorkPath "bin"
foreach ($requiredDirectory in @($env:DOTNET_CLI_HOME, (Join-Path $env:DOTNET_CLI_HOME ".dotnet"), $env:NUGET_PACKAGES)) {
    New-Item -ItemType Directory -Path $requiredDirectory -Force | Out-Null
}
New-Item -ItemType Directory -Path $intermediatePath -Force | Out-Null
New-Item -ItemType Directory -Path $dependencyPath -Force | Out-Null

function Install-SpotiFetchDependencies {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $hasNet10 = $false
    if ($systemDotnet) {
        $installedSdks = & $systemDotnet.Source --list-sdks 2>$null
        $hasNet10 = [bool]($installedSdks | Where-Object { $_ -match '^10\.' })
    }
    $localSdkRoot = Join-Path $userBuildData "dotnet-sdk"
    $localDotnet = Join-Path $localSdkRoot "dotnet.exe"
    $hasLocalNet10 = $false
    if (Test-Path -LiteralPath $localDotnet) {
        $localSdks = & $localDotnet --list-sdks 2>$null
        $hasLocalNet10 = [bool]($localSdks | Where-Object { $_ -match '^10\.' })
    }
    if (-not $hasNet10 -and -not $hasLocalNet10) {
        Write-Host "Baixando e instalando o SDK .NET 10 localmente (sem alterar o .NET do Windows)..." -ForegroundColor Cyan
        $installer = Join-Path $userBuildData "dotnet-install.ps1"
        Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
        try {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Channel "10.0" -Architecture "x64" -InstallDir $localSdkRoot -NoPath
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $localDotnet)) { throw "Falha ao instalar o SDK .NET 10 localmente." }
        }
        finally {
            if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
        }
    }
    else { Write-Host "SDK .NET 10 ja disponivel." -ForegroundColor Green }

    $existingYtdlp = @((Join-Path $PSScriptRoot "yt-dlp.exe"), (Join-Path $dependencyPath "yt-dlp.exe")) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $existingYtdlp) {
        $ytdlp = Join-Path $dependencyPath "yt-dlp.exe"
        Write-Host "Baixando yt-dlp oficial..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile $ytdlp
    }
    else { Write-Host "yt-dlp ja disponivel." -ForegroundColor Green }

    $existingFfmpeg = @((Join-Path $PSScriptRoot "ffmpeg.exe"), (Join-Path (Split-Path $PSScriptRoot -Parent) "ffmpeg.exe"), (Join-Path $dependencyPath "ffmpeg.exe")) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $existingFfmpeg) {
        $ffmpeg = Join-Path $dependencyPath "ffmpeg.exe"
        Write-Host "Baixando FFmpeg para Windows (build indicado pelo site oficial do FFmpeg)..." -ForegroundColor Cyan
        $dependencyRoot = Join-Path $PSScriptRoot (".deps-ffmpeg-" + [Guid]::NewGuid().ToString("N"))
        $archive = Join-Path $dependencyRoot "ffmpeg.zip"
        $extract = Join-Path $dependencyRoot "extract"
        New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
        try {
            Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $archive
            Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
            $ffmpegEntry = Get-ChildItem -LiteralPath $extract -Filter "ffmpeg.exe" -File -Recurse | Select-Object -First 1
            if (-not $ffmpegEntry) { throw "O arquivo ffmpeg.exe nao foi encontrado no pacote baixado." }
            Copy-Item -LiteralPath $ffmpegEntry.FullName -Destination $ffmpeg -Force
        }
        finally {
            $resolvedDependencyRoot = (Resolve-Path -LiteralPath $dependencyRoot -ErrorAction SilentlyContinue).Path
            if ($resolvedDependencyRoot -and $resolvedDependencyRoot.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $resolvedDependencyRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else { Write-Host "FFmpeg ja disponivel." -ForegroundColor Green }

    Write-Host "Dependencias prontas. Execute o build novamente e escolha 1 ou 2." -ForegroundColor Green
}

Write-Host "SpotiFetch - escolha o tipo de build" -ForegroundColor Cyan
Write-Host " [1] EXE unico (recomendado; FFmpeg e yt-dlp ficam ao lado)"
Write-Host " [2] Pasta completa (arquivos soltos)"
Write-Host " [3] Baixar dependencias (SDK .NET 10, yt-dlp e FFmpeg)"
$choice = Read-Host "Escolha 1, 2 ou 3 [1]"
if ([string]::IsNullOrWhiteSpace($choice)) { $choice = "1" }
if ($choice -eq "3") {
    Install-SpotiFetchDependencies
    if (Test-Path -LiteralPath $buildWorkPath) { Remove-Item -LiteralPath $buildWorkPath -Recurse -Force -ErrorAction SilentlyContinue }
    return
}
if ($choice -notin @("1", "2")) { throw "Opcao invalida. Execute novamente e escolha 1, 2 ou 3." }
if (Get-Process -Name "SpotiFetch" -ErrorAction SilentlyContinue) { throw "Feche o SpotiFetch antes de substituir a pasta dist e execute novamente." }
$singleFile = $choice -eq "1"

# Controlled Folder Access may protect Documents\GitHub and block dotnet or
# PowerShell from creating the distribution there. Prefer the repository when
# writable, and otherwise fall back to Downloads without changing Defender.
$outputRoot = $resolvedRoot
$writeProbe = Join-Path $outputRoot (".spotifetch-write-test-" + [Guid]::NewGuid().ToString("N"))
try {
    [void][IO.Directory]::CreateDirectory($writeProbe)
    [IO.Directory]::Delete($writeProbe)
}
catch {
    if (Test-Path -LiteralPath $writeProbe) { Remove-Item -LiteralPath $writeProbe -Force -Recurse -ErrorAction SilentlyContinue }
    $outputRoot = Join-Path $env:USERPROFILE "Downloads\SpotiFetch"
    [void][IO.Directory]::CreateDirectory($outputRoot)
    Write-Warning "O Windows bloqueou a gravacao dentro do repositorio. A distribuicao sera salva em Downloads\SpotiFetch."
}
$publishPath = Join-Path $outputRoot "dist"
$stagePath = Join-Path $outputRoot (".dist-staging-" + [Guid]::NewGuid().ToString("N"))
$backupPath = Join-Path $outputRoot (".dist-backup-" + [Guid]::NewGuid().ToString("N"))
$resolvedOutputRoot = (Resolve-Path -LiteralPath $outputRoot).Path

$localDotnet = Join-Path $userBuildData "dotnet-sdk\dotnet.exe"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) { throw "SDK do .NET 10 nao encontrado. Escolha a opcao 3 para baixa-lo." }
$installedSdks = & $dotnet --list-sdks 2>$null
if (-not ($installedSdks | Where-Object { $_ -match '^10\.' })) { throw "O SDK .NET 10 nao foi encontrado. Escolha a opcao 3 para prepara-lo localmente." }
$ytdlpSource = Join-Path $PSScriptRoot "yt-dlp.exe"
if (-not (Test-Path -LiteralPath $ytdlpSource)) {
    $ytdlpSource = Join-Path $dependencyPath "yt-dlp.exe"
}
if (-not (Test-Path -LiteralPath $ytdlpSource)) {
    Write-Host "Baixando yt-dlp oficial..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile $ytdlpSource
}
$ffmpegSource = Join-Path $PSScriptRoot "ffmpeg.exe"
if (-not (Test-Path -LiteralPath $ffmpegSource)) { $ffmpegSource = Join-Path (Split-Path $PSScriptRoot -Parent) "ffmpeg.exe" }
if (-not (Test-Path -LiteralPath $ffmpegSource)) { $ffmpegSource = Join-Path $dependencyPath "ffmpeg.exe" }
if (-not (Test-Path -LiteralPath $ffmpegSource)) { throw "ffmpeg.exe nao encontrado. Execute a opcao 3 para baixa-lo na pasta local do SpotiFetch." }

try {
    $publishSingleFile = if ($singleFile) { "true" } else { "false" }
    $includeNative = if ($singleFile) { "true" } else { "false" }
    & $dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:BaseIntermediateOutputPath="$intermediatePath\" -p:MSBuildProjectExtensionsPath="$intermediatePath\" -p:BaseOutputPath="$baseOutputPath\" -p:PublishSingleFile=$publishSingleFile -p:IncludeNativeLibrariesForSelfExtract=$includeNative -p:NuGetAudit=false -o $compileStagePath
    if ($LASTEXITCODE -ne 0) { throw "A compilacao falhou (codigo $LASTEXITCODE). A distribuicao anterior foi preservada." }

    $exePath = Join-Path $compileStagePath "SpotiFetch.exe"
    if (-not (Test-Path -LiteralPath $exePath)) { throw "O publish terminou sem gerar SpotiFetch.exe." }
    [void][IO.Directory]::CreateDirectory($stagePath)
    foreach ($publishedItem in Get-ChildItem -LiteralPath $compileStagePath -Force) {
        Copy-Item -LiteralPath $publishedItem.FullName -Destination $stagePath -Recurse -Force
    }
    Copy-Item -LiteralPath $ytdlpSource -Destination (Join-Path $stagePath "yt-dlp.exe") -Force
    Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $stagePath "ffmpeg.exe") -Force
    $iconSource = Join-Path $PSScriptRoot "icone.ico"
    if (-not (Test-Path -LiteralPath $iconSource)) { $iconSource = Join-Path (Split-Path $PSScriptRoot -Parent) "icone.ico" }
    if (Test-Path -LiteralPath $iconSource) { Copy-Item -LiteralPath $iconSource -Destination (Join-Path $stagePath "icone.ico") -Force }
    if ($singleFile) {
        Get-ChildItem -LiteralPath $stagePath -Filter "*.pdb" | Remove-Item -Force
        Get-ChildItem -LiteralPath $stagePath -Filter "*.deps.json" | Remove-Item -Force
        Get-ChildItem -LiteralPath $stagePath -Filter "*.runtimeconfig.json" | Remove-Item -Force
    }

    if (Test-Path -LiteralPath $publishPath) {
        $resolvedDist = (Resolve-Path -LiteralPath $publishPath).Path
        if (-not $resolvedDist.StartsWith($resolvedOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Caminho dist fora da pasta de saida; cancelando por seguranca." }
        Move-Item -LiteralPath $resolvedDist -Destination $backupPath
    }
    try { Move-Item -LiteralPath $stagePath -Destination $publishPath }
    catch {
        if (Test-Path -LiteralPath $backupPath) { Move-Item -LiteralPath $backupPath -Destination $publishPath }
        throw
    }
    if (Test-Path -LiteralPath $backupPath) {
        try { Remove-Item -LiteralPath $backupPath -Recurse -Force }
        catch { Write-Warning "Build pronto; backup antigo mantido em $backupPath (feche o app para remove-lo)." }
    }
    $modeText = if ($singleFile) { "EXE unico + FFmpeg/yt-dlp externos" } else { "pasta completa com arquivos soltos" }
    Write-Host "Build concluido: $modeText" -ForegroundColor Green
    Write-Host "Saida: $publishPath" -ForegroundColor Green
}
finally {
    if ((Test-Path -LiteralPath $stagePath) -and $stagePath.StartsWith($resolvedOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $buildWorkPath) { Remove-Item -LiteralPath $buildWorkPath -Recurse -Force -ErrorAction SilentlyContinue }
}
