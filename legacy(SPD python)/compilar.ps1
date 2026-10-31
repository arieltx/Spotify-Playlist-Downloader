# Garante que o console use codificação UTF-8 para exibir os acentos corretamente
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Mostrar-Menu {
    Clear-Host
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "       SPOTIFY PLAYLIST DOWNLOADER - MENU DE FERRAMENTAS" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host " [1] Baixar dependencias e Compilar (.exe)" -ForegroundColor Cyan
    Write-Host " [2] Compilar apenas (Roda sua rotina PyInstaller)" -ForegroundColor Cyan
    Write-Host " [3] Baixar apenas dependências (requirements.txt)" -ForegroundColor Cyan
    Write-Host " [0] Sair" -ForegroundColor Red
    Write-Host "==========================================================" -ForegroundColor Green
}

function Baixar-Dependencias {
    Write-Host ""
    Write-Host "[+] Verificando e instalando dependencias do requirements.txt..." -ForegroundColor Yellow
    pip install -r requirements.txt
    Write-Host "[!] Instalacao de dependencias finalizada." -ForegroundColor Green
}

function Executar-Compilacao {
    Write-Host ""
    Write-Host "[+] Limpando pastas de build anteriores..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue

    Write-Host "[+] Gerando arquivos de build (Etapa 1)..." -ForegroundColor Yellow
    pyinstaller --noconfirm --clean --windowed --icon=icone.ico --add-data "icone.ico;." --name "Spotify Playlist Downloader" --collect-all ttkbootstrap --add-binary "ffmpeg.exe;ffmpeg" "V3.3.7.py" 

    Write-Host "[+] Compilando executável final via .spec (Etapa 2)..." -ForegroundColor Yellow
    pyinstaller --noconfirm --clean --log-level=INFO "Spotify Playlist Downloader.spec"

    Write-Host ""
    Write-Host "[!] Processo de compilacao concluido! Verifique a pasta 'dist'." -ForegroundColor Green
}

# Loop principal do Menu
do {
    Mostrar-Menu
    $escolha = Read-Host "Digite a opcao desejada"

    switch ($escolha) {
        "1" {
            Baixar-Dependencias
            Executar-Compilacao
            Pause
        }
        "2" {
            Executar-Compilacao
            Pause
        }
        "3" {
            Baixar-Dependencias
            Pause
        }
        "0" {
            Write-Host ""
            Write-Host "Saindo... Até logo!" -ForegroundColor Yellow
            Break
        }
        Default {
            Write-Host ""
            Write-Host "[X] Opção inválida! Tente novamente." -ForegroundColor Red
            Start-Sleep -Seconds 2
        }
    }
} while ($escolha -ne "0")
