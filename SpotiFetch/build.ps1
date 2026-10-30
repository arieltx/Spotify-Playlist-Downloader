$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "LaurusDownloader.csproj"
$ytdlpPath = Join-Path $PSScriptRoot "yt-dlp.exe"
$publishPath = Join-Path $PSScriptRoot "dist"
$resolvedProject = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet-cli"
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot ".nuget"

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $dotnet = (Get-Command dotnet).Source
} elseif (Test-Path (Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe")) {
    $dotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
} elseif (Test-Path (Join-Path $env:USERPROFILE "AppData\Local\dotnet\dotnet.exe")) {
    $dotnet = Join-Path $env:USERPROFILE "AppData\Local\dotnet\dotnet.exe"
} else {
    throw "Instale o .NET 10 SDK para compilar este aplicativo: https://dotnet.microsoft.com/download/dotnet/10.0"
}

if (-not (Test-Path $ytdlpPath)) {
    Write-Host "Baixando yt-dlp oficial..." -ForegroundColor Cyan
    Invoke-WebRequest `
        -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" `
        -OutFile $ytdlpPath
}

& $dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:NuGetAudit=false -o $publishPath
if ($LASTEXITCODE -ne 0) { throw "A compilação falhou." }

Copy-Item -LiteralPath $ytdlpPath -Destination (Join-Path $publishPath "yt-dlp.exe") -Force

# Keep only the distributable build; remove SDK build/intermediate copies.
foreach ($generatedPath in @("bin", "obj")) {
    $candidate = Join-Path $PSScriptRoot $generatedPath
    if (Test-Path -LiteralPath $candidate) {
        $resolvedCandidate = (Resolve-Path -LiteralPath $candidate).Path
        if (-not $resolvedCandidate.StartsWith($resolvedProject + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Recusa limpar um caminho fora da pasta do projeto: $resolvedCandidate"
        }
        Remove-Item -LiteralPath $resolvedCandidate -Recurse -Force
    }
}

Write-Host "Aplicativo pronto em: $publishPath" -ForegroundColor Green
