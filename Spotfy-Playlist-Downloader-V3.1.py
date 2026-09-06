# =============================================================================
#  SPOTIFY PLAYLIST DOWNLOADER V3.1
# =============================================================================
#
#  Recursos:
#    - Autenticação Spotify via PKCE
#    - Leitura de playlists do usuário / colaborativas
#    - Paginação para playlists grandes
#    - Download em thread separada da GUI
#    - Cancelamento com threading.Event
#    - Barra de progresso da playlist e da música
#    - Barras de progresso verdes
#    - Detecção de arquivos já existentes
#    - Detecção de duplicados mesmo com pequenas diferenças de nome
#    - Log detalhado
#    - Resumo final
#    - Nomes de arquivos seguros para Windows
#
#  Instalação:
#    pip install -U spotipy yt-dlp python-dotenv ttkbootstrap
#
#  config.env:
#    SPOTIPY_CLIENT_ID=seu_client_id
#    FFMPEG_PATH=C:\ffmpeg\bin
#    SAVE_PATH=C:\Musicas
#
#  Redirect URI do Spotify Developer Dashboard:
#    http://127.0.0.1:8888/callback
#
# =============================================================================

import os
import re
import threading
import unicodedata
from pathlib import Path
from queue import Empty, Queue
from urllib.parse import urlparse

import tkinter as tk
from tkinter import messagebox, simpledialog

import ttkbootstrap as ttk
from ttkbootstrap.constants import *

import spotipy
from dotenv import load_dotenv
from spotipy.exceptions import SpotifyException
from spotipy.oauth2 import SpotifyPKCE
import yt_dlp


# =============================================================================
# COMO MANTER / EDITAR ESTE ARQUIVO
# =============================================================================
#
# Antes de modificar, use este mapa:
#
# 1. CONFIGURAÇÃO GERAL
#    Nome, Redirect URI, permissões e localização do config.env.
#
# 2. SPOTIFY
#    Autenticação e leitura de playlists/faixas.
#
# 3. FFMPEG / ARQUIVOS
#    Conversão, pastas, nomes e comparação de arquivos existentes.
#
# 4. DOWNLOAD MANAGER
#    Thread, cancelamento, progresso e resultados do download.
#
# 5. GUI
#    Toda a aparência e interação da janela.
#
# REGRA DE OURO:
#    Se você só quer mudar o visual, mexa na seção GUI.
#    Se quer mudar como o download funciona, mexa no DOWNLOAD MANAGER/yt-dlp.
#    Se quer mudar Spotify/OAuth, mexa em CONFIGURAÇÃO GERAL + SPOTIFY.
#
# Evite alterar várias seções de uma vez. Faça uma mudança, rode o programa
# e teste. Isso facilita descobrir exatamente qual alteração causou um problema.
# =============================================================================


# =============================================================================
# CONFIGURAÇÃO GERAL
# =============================================================================
#
# >>> ÁREA SEGURA PARA PERSONALIZAÇÃO <<<
#
# As constantes abaixo são os primeiros lugares para mexer quando você quiser
# mudar o comportamento ou a aparência básica do programa.
#
# APP_NAME       -> nome exibido na barra de título da janela.
# REDIRECT_URI   -> endereço usado pelo OAuth/PKCE. NÃO altere sem atualizar
#                   também o mesmo endereço no Spotify Developer Dashboard.
# SPOTIFY_SCOPE  -> permissões solicitadas ao Spotify.
# BASE_DIR       -> pasta onde este .py está localizado.
# CONFIG_PATH    -> localização do arquivo config.env.
#
# Para customizações visuais, procure mais abaixo por:
#   class SpotifyDownloaderApp
#
# Lá estão as cores, tamanhos, fontes, espaçamentos e estilos da HUD.
# =============================================================================

# Nome da aplicação mostrado no título da janela.
APP_NAME = "Spotify Playlist Downloader V3.1"

# URI usada no fluxo OAuth PKCE.
# IMPORTANTE: deve ser idêntica à cadastrada no Spotify Developer Dashboard.
REDIRECT_URI = "http://127.0.0.1:8888/callback"

# Permissões solicitadas para ler playlists privadas e colaborativas.
# Para adicionar/remover permissões, consulte a documentação do Spotify e
# mantenha apenas os scopes realmente necessários.
SPOTIFY_SCOPE = (
    "playlist-read-private "
    "playlist-read-collaborative"
)

# Pasta deste próprio script.
BASE_DIR = Path(__file__).resolve().parent

# Arquivo de configuração externo.
# Primeiro tenta encontrar junto do .py; se não existir, tenta a pasta atual.
CONFIG_PATH = BASE_DIR / "config.env"
if not CONFIG_PATH.exists():
    CONFIG_PATH = Path.cwd() / "config.env"

# Carrega SPOTIPY_CLIENT_ID, FFMPEG_PATH e SAVE_PATH do config.env.
load_dotenv(CONFIG_PATH)


# =============================================================================
# EXCEÇÕES
# =============================================================================

class DownloadCancelled(Exception):
    """Indica que o usuário solicitou o cancelamento do download."""


# =============================================================================
# SPOTIFY
# =============================================================================
#
# Funções desta seção:
#   configurar_spotify()                -> cria o cliente autenticado por PKCE.
#   extrair_id_spotify()                -> valida URLs e extrai IDs.
#   extrair_informacoes_musica()        -> pega título/artista de uma faixa.
#   extrair_musicas_e_nome_playlist()   -> carrega todas as faixas da playlist
#                                        usando paginação.
#
# Caso você queira alterar permissões do Spotify, a constante SPOTIFY_SCOPE
# fica na seção CONFIGURAÇÃO GERAL, no começo do arquivo.
# =============================================================================

def configurar_spotify():
    client_id = os.getenv("SPOTIPY_CLIENT_ID")

    if not client_id:
        raise ValueError(
            "SPOTIPY_CLIENT_ID não encontrado no arquivo config.env."
        )

    auth_manager = SpotifyPKCE(
        client_id=client_id,
        redirect_uri=REDIRECT_URI,
        scope=SPOTIFY_SCOPE,
        cache_path=str(BASE_DIR / ".spotify_cache"),
        open_browser=True,
    )

    return spotipy.Spotify(
        auth_manager=auth_manager,
        requests_timeout=15,
        retries=3,
    )


def extrair_id_spotify(url, tipo_esperado=None):
    """Extrai e valida o ID de um recurso do Spotify."""

    url = str(url).strip()
    parsed = urlparse(url)

    hosts_validos = {
        "open.spotify.com",
        "spotify.com",
        "www.spotify.com",
    }

    if parsed.netloc.lower() not in hosts_validos:
        raise ValueError("URL do Spotify inválida.")

    partes = [parte for parte in parsed.path.split("/") if parte]

    if len(partes) < 2:
        raise ValueError(
            "Não foi possível identificar o tipo e o ID do Spotify."
        )

    tipo = partes[0].lower()
    spotify_id = partes[1]

    if tipo_esperado and tipo != tipo_esperado:
        raise ValueError(
            f"Era esperada uma URL do tipo '{tipo_esperado}', "
            f"mas foi recebida uma URL do tipo '{tipo}'."
        )

    if not re.fullmatch(r"[A-Za-z0-9]+", spotify_id):
        raise ValueError("ID do Spotify inválido.")

    return spotify_id


def extrair_informacoes_musica(spotify, musica_url):
    """Obtém título e artista de uma música do Spotify."""

    track_id = extrair_id_spotify(musica_url, "track")

    try:
        track = spotify.track(track_id)
    except SpotifyException as erro:
        if erro.http_status == 403:
            raise RuntimeError(
                "O Spotify recusou o acesso a esta música."
            ) from erro
        raise

    if not track:
        raise ValueError("Música não encontrada.")

    titulo = track.get("name")
    artistas = track.get("artists", [])

    if not titulo or not artistas:
        raise ValueError(
            "Não foi possível obter título e artista da música."
        )

    artista = artistas[0].get(
        "name",
        "Artista desconhecido",
    )

    return titulo, artista


def extrair_musicas_e_nome_playlist(spotify, playlist_url):
    """
    Obtém todas as músicas da playlist usando paginação.

    A API atual utiliza:
        items[].item
    """

    playlist_id = extrair_id_spotify(
        playlist_url,
        "playlist",
    )

    try:
        playlist = spotify.playlist(
            playlist_id,
            fields="name",
        )

        playlist_nome = playlist.get(
            "name",
            "Playlist sem nome",
        )

        resultado = spotify.playlist_items(
            playlist_id,
            limit=100,
        )

    except SpotifyException as erro:
        if erro.http_status == 403:
            raise RuntimeError(
                "O Spotify recusou o acesso a esta playlist.\n\n"
                "Verifique se a playlist pertence à sua conta "
                "ou é colaborativa."
            ) from erro
        raise

    musicas = []

    while resultado:

        for item in resultado.get("items", []):
            track = item.get("item")

            if not track:
                continue

            if track.get("type") != "track":
                continue

            titulo = track.get("name")
            artistas = track.get("artists", [])

            if not titulo or not artistas:
                continue

            artista = artistas[0].get(
                "name",
                "Artista desconhecido",
            )

            musicas.append((titulo, artista))

        proximo = resultado.get("next")

        if not proximo:
            break

        resultado = spotify.next(resultado)

    return playlist_nome, musicas


# =============================================================================
# FFMPEG / ARQUIVOS
# =============================================================================
#
# FFMPEG_PATH no config.env = caminho do ffmpeg.exe ou da pasta que o contém.
# SAVE_PATH   no config.env = pasta raiz onde os downloads serão armazenados.
#
# Funções desta seção:
#   obter_caminho_ffmpeg() -> encontra/valida o FFmpeg.
#   limpar_nome()          -> remove caracteres proibidos no Windows.
#   normalizar_texto()     -> deixa textos comparáveis sem acentos/capitalização.
#   chave_musica()         -> cria a identidade lógica de uma música.
#   criar_pasta()          -> cria a pasta da playlist.
#   salvar_musicas_em_txt()-> exporta a lista para TXT.
# =============================================================================

def obter_caminho_ffmpeg():
    ffmpeg_path = os.getenv("FFMPEG_PATH")

    if not ffmpeg_path:
        raise FileNotFoundError(
            "FFMPEG_PATH não foi definido no arquivo config.env."
        )

    caminho = Path(ffmpeg_path).expanduser()

    if caminho.is_file():
        return str(caminho)

    if caminho.is_dir():
        ffmpeg_exe = caminho / "ffmpeg.exe"
        if ffmpeg_exe.exists():
            return str(ffmpeg_exe)

        ffmpeg_bin = caminho / "ffmpeg"
        if ffmpeg_bin.exists():
            return str(ffmpeg_bin)

    raise FileNotFoundError(
        f"FFmpeg não encontrado em:\n{caminho}"
    )


SAVE_PATH = Path(
    os.getenv(
        "SAVE_PATH",
        str(BASE_DIR / "downloads"),
    )
).expanduser()

SAVE_PATH.mkdir(
    parents=True,
    exist_ok=True,
)


def limpar_nome(nome, max_length=180):
    """Remove caracteres inválidos para nomes de arquivos/pastas."""

    nome = str(nome).strip()

    nome = re.sub(
        r'[<>:"/\\|?*\x00-\x1F]',
        "_",
        nome,
    )

    nome = nome.rstrip(" .")

    reservados = {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4",
        "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }

    if nome.upper() in reservados:
        nome = f"_{nome}"

    return nome[:max_length] or "Sem nome"


def normalizar_texto(texto):
    """
    Normaliza para comparação:
      - remove acentos;
      - ignora maiúsculas/minúsculas;
      - mantém somente letras e números.
    """

    texto = unicodedata.normalize(
        "NFKD",
        str(texto),
    )

    texto = "".join(
        char
        for char in texto
        if not unicodedata.combining(char)
    )

    texto = texto.casefold()

    texto = re.sub(
        r"\(\s*\d+\s*\)$",
        "",
        texto,
    )

    return re.sub(
        r"[^a-z0-9]+",
        "",
        texto,
    )


def chave_musica(titulo, artista):
    return normalizar_texto(
        f"{titulo} - {artista}"
    )


def normalizar_nome_arquivo(nome):
    return normalizar_texto(
        Path(str(nome)).stem
    )


def criar_pasta(nome_pasta):
    pasta = SAVE_PATH / limpar_nome(nome_pasta)

    pasta.mkdir(
        parents=True,
        exist_ok=True,
    )

    return pasta


def salvar_musicas_em_txt(musicas, pasta_destino):
    arquivo_txt = Path(pasta_destino) / "musicas.txt"

    with open(
        arquivo_txt,
        "w",
        encoding="utf-8",
    ) as arquivo:
        for titulo, artista in musicas:
            arquivo.write(
                f"{titulo} - {artista}\n"
            )

    return arquivo_txt


# =============================================================================
# DOWNLOAD MANAGER
# =============================================================================
#
# Esta classe é responsável pelo trabalho pesado fora da GUI.
#
# Thread      -> roda o download sem congelar a janela.
# cancel_event-> sinaliza que o usuário apertou Cancelar.
# ui_queue    -> fila de eventos entre a thread e a GUI.
#
# Fluxo principal de playlist:
#   iniciar_playlist()
#       ↓
#   _worker_playlist()
#       ↓
#   verifica índice de arquivos existentes
#       ↓
#   baixa/pula cada música
#       ↓
#   envia eventos para a GUI
# =============================================================================

class DownloadManager:
    """Controla o download e comunica eventos para a GUI."""

    def __init__(self, ui_queue):
        self.ui_queue = ui_queue
        self.cancel_event = threading.Event()
        self.thread = None

    @property
    def executando(self):
        return (
            self.thread is not None
            and self.thread.is_alive()
        )

    def cancelar(self):
        self.cancel_event.set()

    def _enviar_ui(self, evento, *args):
        self.ui_queue.put(
            (evento, args)
        )

    def iniciar_playlist(
        self,
        playlist_nome,
        musicas,
        pasta_destino,
    ):
        if self.executando:
            return False

        self.cancel_event.clear()

        self.thread = threading.Thread(
            target=self._worker_playlist,
            args=(
                playlist_nome,
                musicas,
                pasta_destino,
            ),
            daemon=True,
        )

        self.thread.start()
        return True

    def iniciar_musica(
        self,
        titulo,
        artista,
        pasta_destino,
    ):
        if self.executando:
            return False

        self.cancel_event.clear()

        self.thread = threading.Thread(
            target=self._worker_musica,
            args=(
                titulo,
                artista,
                pasta_destino,
            ),
            daemon=True,
        )

        self.thread.start()
        return True

    def criar_indice_musicas(self, pasta_destino):
        """
        Lê os MP3 existentes uma única vez.

        Isso evita ter que procurar no disco a cada música.
        """

        pasta_destino = Path(pasta_destino)
        indice = set()

        if not pasta_destino.exists():
            return indice

        for arquivo in pasta_destino.glob("*.mp3"):
            indice.add(
                normalizar_nome_arquivo(
                    arquivo.name
                )
            )

        return indice

    def _worker_playlist(
        self,
        playlist_nome,
        musicas,
        pasta_destino,
    ):
        total = len(musicas)

        if total == 0:
            self._enviar_ui(
                "erro",
                "A playlist não possui músicas válidas.",
            )
            self._enviar_ui("finalizado")
            return

        baixadas = 0
        puladas = 0
        erros = 0

        indice_existentes = self.criar_indice_musicas(
            pasta_destino
        )

        self._enviar_ui(
            "inicio",
            playlist_nome,
            total,
        )

        self._enviar_ui(
            "log",
            f"Arquivos existentes encontrados: "
            f"{len(indice_existentes)}",
        )

        try:
            for indice, (titulo, artista) in enumerate(
                musicas,
                start=1,
            ):
                if self.cancel_event.is_set():
                    self._enviar_ui("cancelado")
                    break

                self._enviar_ui(
                    "musica",
                    indice,
                    total,
                    titulo,
                    artista,
                )

                chave = chave_musica(
                    titulo,
                    artista,
                )

                # ----------------------------------------------------
                # JÁ EXISTE
                # ----------------------------------------------------

                if chave in indice_existentes:
                    puladas += 1

                    self._enviar_ui(
                        "log",
                        f"[{indice}/{total}] "
                        f"Já existe: {titulo} - {artista}",
                    )

                    self._enviar_ui(
                        "progresso",
                        (indice / total) * 100,
                    )

                    self._enviar_ui(
                        "estatisticas",
                        baixadas,
                        puladas,
                        erros,
                    )

                    continue

                # ----------------------------------------------------
                # DOWNLOAD
                # ----------------------------------------------------

                try:
                    resultado = self.baixar_musica(
                        titulo,
                        artista,
                        pasta_destino,
                    )

                    if resultado == "exists":
                        puladas += 1

                        self._enviar_ui(
                            "log",
                            f"[{indice}/{total}] "
                            f"Já existe: {titulo} - {artista}",
                        )

                    elif resultado == "success":
                        baixadas += 1

                        indice_existentes.add(
                            chave
                        )

                        self._enviar_ui(
                            "log",
                            f"[{indice}/{total}] "
                            f"Baixada: {titulo} - {artista}",
                        )

                    else:
                        erros += 1

                except DownloadCancelled:
                    self._enviar_ui("cancelado")
                    break

                except Exception as erro:
                    erros += 1

                    self._enviar_ui(
                        "log",
                        f"[{indice}/{total}] "
                        f"ERRO: {titulo} - {artista} -> {erro}",
                    )

                progresso = (
                    indice / total
                ) * 100

                self._enviar_ui(
                    "progresso",
                    progresso,
                )

                self._enviar_ui(
                    "estatisticas",
                    baixadas,
                    puladas,
                    erros,
                )

            else:
                self._enviar_ui(
                    "playlist_concluida",
                    playlist_nome,
                    total,
                    baixadas,
                    puladas,
                    erros,
                )

        finally:
            self._enviar_ui("finalizado")

    def _worker_musica(
        self,
        titulo,
        artista,
        pasta_destino,
    ):
        self._enviar_ui(
            "inicio",
            "Download individual",
            1,
        )

        self._enviar_ui(
            "musica",
            1,
            1,
            titulo,
            artista,
        )

        try:
            resultado = self.baixar_musica(
                titulo,
                artista,
                pasta_destino,
            )

            if resultado == "exists":
                mensagem = "Arquivo já existente."

                self._enviar_ui(
                    "log",
                    f"Já existe: {titulo} - {artista}",
                )

            else:
                mensagem = "Download concluído."

                self._enviar_ui(
                    "log",
                    f"Baixada: {titulo} - {artista}",
                )

            self._enviar_ui(
                "progresso",
                100,
            )

            self._enviar_ui(
                "download_progresso",
                100,
            )

            self._enviar_ui(
                "individual_concluida",
                mensagem,
            )

        except DownloadCancelled:
            self._enviar_ui("cancelado")

        except Exception as erro:
            self._enviar_ui(
                "erro",
                f"Não foi possível baixar:\n{erro}",
            )

        finally:
            self._enviar_ui("finalizado")

    def baixar_musica(
        self,
        titulo,
        artista,
        pasta_destino,
    ):
        """
        Baixa o resultado correspondente usando yt-dlp.
        """

        ffmpeg_path = obter_caminho_ffmpeg()

        titulo_seguro = limpar_nome(titulo)
        artista_seguro = limpar_nome(artista)

        nome_base = (
            f"{titulo_seguro} - {artista_seguro}"
        )

        pasta_destino = Path(pasta_destino)

        arquivo_final = (
            pasta_destino /
            f"{nome_base}.mp3"
        )

        if arquivo_final.exists():
            return "exists"

        consulta = (
            f"ytsearch1:"
            f"{titulo} {artista} official audio"
        )

        output_template = str(
            pasta_destino /
            f"{nome_base}.%(ext)s"
        )

        def progress_hook(data):
            if self.cancel_event.is_set():
                raise DownloadCancelled()

            status = data.get("status")

            if status == "downloading":
                percentual_str = (
                    data.get("_percent_str", "0%")
                    .replace("%", "")
                    .strip()
                )

                try:
                    percentual = float(
                        percentual_str
                    )
                except (TypeError, ValueError):
                    percentual = 0.0

                self._enviar_ui(
                    "download_progresso",
                    max(0.0, min(100.0, percentual)),
                )

            elif status == "finished":
                self._enviar_ui(
                    "download_progresso",
                    100.0,
                )

        # =====================================================================
        # CONFIGURAÇÕES DO YT-DLP
        # =====================================================================
        # É aqui que você ajusta o comportamento do mecanismo de download.
        #
        # format           -> formato/qualidade de mídia buscada.
        # noplaylist       -> impede que uma busca individual vire playlist.
        # outtmpl          -> padrão do nome/caminho do arquivo de saída.
        # ffmpeg_location  -> caminho do FFmpeg.
        # retries          -> tentativas adicionais em caso de falha.
        # preferredquality -> bitrate escolhido para o MP3.
        # postprocessor    -> conversão final para MP3.
        # progress_hooks   -> recebe atualizações da barra de progresso.
        # =====================================================================
        opcoes = {
            "format": "bestaudio/best",
            "noplaylist": True,
            "quiet": True,
            "no_warnings": True,
            "outtmpl": output_template,
            "ffmpeg_location": ffmpeg_path,
            "progress_hooks": [
                progress_hook
            ],
            "postprocessors": [
                {
                    "key": "FFmpegExtractAudio",
                    "preferredcodec": "mp3",
                    "preferredquality": "192",
                }
            ],
            "retries": 3,
            "fragment_retries": 3,
            "overwrites": False,
            "postprocessor_args": [
                "-metadata",
                f"title={titulo}",
                "-metadata",
                f"artist={artista}",
            ],
        }

        with yt_dlp.YoutubeDL(opcoes) as ydl:
            resultado = ydl.download(
                [consulta]
            )

        if resultado != 0:
            raise RuntimeError(
                f"yt-dlp retornou código {resultado}."
            )

        if not arquivo_final.exists():
            candidatos = list(
                pasta_destino.glob(
                    f"{nome_base}.*"
                )
            )

            mp3s = [
                arquivo
                for arquivo in candidatos
                if arquivo.suffix.lower() == ".mp3"
            ]

            if not mp3s:
                raise FileNotFoundError(
                    "O download terminou, mas o MP3 não "
                    "foi encontrado no diretório esperado."
                )

        return "success"


# =============================================================================
# GUI
# =============================================================================
#
# MAPA RÁPIDO DA HUD
# -----------------------------------------------------------------------------
#
# Se você quiser personalizar o visual depois, praticamente tudo está nesta
# classe. Os nomes abaixo indicam exatamente o que cada parte controla:
#
# BG             -> fundo geral da janela.
# PANEL          -> fundo dos cards/painéis de STATUS e LOG.
# PANEL_2        -> cabeçalho interno do LOG.
# BORDER         -> cor das bordas dos painéis.
# TEXT           -> texto principal.
# MUTED          -> texto secundário/cinza.
# GREEN          -> cor principal de destaque e progresso.
# LOG_BG         -> fundo preto do terminal/log.
#
# Tamanho da janela:
#   root.geometry("860x650")
# Tamanho mínimo:
#   root.minsize(760, 560)
#
# Título:
#   font=("Segoe UI", 24, "bold")
#
# Versão:
#   font=("Segoe UI", 9, "bold")
#
# Botões:
#   bootstyle=SUCCESS / SECONDARY / INFO / DANGER
#   width=...
#
# Barras:
#   bootstyle=SUCCESS
#
# Log:
#   bg / fg / insertbackground / selectbackground / font
#
# IMPORTANTE:
# O tema principal vem do ttkbootstrap (darkly), mas vários elementos da HUD
# são widgets Tk nativos de propósito. Assim o fundo preto/cinza permanece
# consistente mesmo no Windows.
# =============================================================================

class SpotifyDownloaderApp:
    """Interface principal do Spotify Playlist Downloader."""

    # =====================================================================
    # CORES DA HUD — EDITE AQUI PARA MUDAR A APARÊNCIA
    # =====================================================================
    # Formato: hexadecimal #RRGGBB
    # Exemplo: #ff0000 = vermelho
    #
    # BG       = fundo geral
    # PANEL    = cartões/painéis principais
    # PANEL_2  = cabeçalhos internos
    # BORDER   = linhas/bordas
    # TEXT     = texto principal
    # MUTED    = textos secundários
    # GREEN    = destaque, status pronto e progresso
    # LOG_BG   = fundo do terminal/log
    # =====================================================================
    BG = "#0b0f14"
    PANEL = "#111820"
    PANEL_2 = "#151e28"
    BORDER = "#26313d"
    TEXT = "#e6edf3"
    MUTED = "#8b98a8"
    GREEN = "#22c55e"
    LOG_BG = "#080b0f"

    def __init__(self, root):
        self.root = root

        # -----------------------------------------------------------------
        # JANELA PRINCIPAL
        # -----------------------------------------------------------------
        # geometry = tamanho inicial: largura x altura
        # minsize  = menor tamanho que o usuário poderá deixar a janela
        self.root.title(APP_NAME)
        self.root.geometry("860x650")
        self.root.minsize(760, 560)
        self.root.configure(background=self.BG)

        self.ui_queue = Queue()
        self.download_manager = DownloadManager(self.ui_queue)

        self.spotify = None
        self.total_musicas = 0
        self.download_em_andamento = False
        self.baixadas = 0
        self.puladas = 0
        self.erros = 0

        self.inicializar_spotify()
        self.criar_interface()

        self.root.after(100, self.processar_fila)
        self.root.protocol("WM_DELETE_WINDOW", self.fechar)

    # -------------------------------------------------------------------------
    # Spotify
    # -------------------------------------------------------------------------

    def inicializar_spotify(self):
        try:
            self.spotify = configurar_spotify()
        except Exception as erro:
            messagebox.showerror(
                "Erro de configuração",
                str(erro),
                parent=self.root,
            )
            self.root.destroy()

    # -------------------------------------------------------------------------
    # Interface
    # -------------------------------------------------------------------------

    def criar_interface(self):
        # O tema darkly do ttkbootstrap cuida do visual base.
        # Os frames Tk abaixo garantem o preto/cinza mesmo em Windows.

        # -----------------------------------------------------------------
        # Cabeçalho
        # -----------------------------------------------------------------
        # HEADER = título + versão no topo da aplicação.
        header = tk.Frame(
            self.root,
            bg=self.BG,
        )
        header.pack(fill="x", padx=24, pady=(18, 8))

        tk.Label(
            header,
            text="Spotify Playlist Downloader",
            bg=self.BG,
            fg=self.TEXT,
            font=("Segoe UI", 24, "bold"),
        ).pack()

        tk.Label(
            header,
            text="V3.1  •  DARK HUD",
            bg=self.BG,
            fg=self.GREEN,
            font=("Segoe UI", 9, "bold"),
        ).pack(pady=(2, 0))

        # -----------------------------------------------------------------
        # Área principal
        # -----------------------------------------------------------------
        main = tk.Frame(
            self.root,
            bg=self.BG,
        )
        main.pack(fill="both", expand=True, padx=24, pady=(0, 18))

        # -----------------------------------------------------------------
        # Botões
        # -----------------------------------------------------------------
        # BOTÕES = ações principais da aplicação.
        # Para trocar cores/estilos, veja bootstyle=... em cada botão.
        frame_botoes = tk.Frame(
            main,
            bg=self.BG,
        )
        frame_botoes.pack(fill="x", pady=(2, 12))

        for coluna in range(4):
            frame_botoes.columnconfigure(coluna, weight=1, uniform="buttons")

        self.btn_playlist = ttk.Button(
            frame_botoes,
            text="Baixar Playlist",
            command=self.baixar_playlist,
            # SUCCESS = verde. Troque por INFO, SECONDARY, WARNING ou DANGER
            # para experimentar outro estilo do ttkbootstrap.
            bootstyle=SUCCESS,
            width=20,
        )
        self.btn_playlist.grid(row=0, column=0, padx=(0, 5), sticky="ew")

        self.btn_lista = ttk.Button(
            frame_botoes,
            text="Listar Playlist .txt",
            command=self.listar_playlist,
            bootstyle=SECONDARY,
            width=20,
        )
        self.btn_lista.grid(row=0, column=1, padx=5, sticky="ew")

        self.btn_musica = ttk.Button(
            frame_botoes,
            text="Baixar Música",
            command=self.baixar_musica,
            bootstyle=INFO,
            width=20,
        )
        self.btn_musica.grid(row=0, column=2, padx=5, sticky="ew")

        self.btn_cancelar = ttk.Button(
            frame_botoes,
            text="Cancelar",
            command=self.cancelar_download,
            bootstyle=DANGER,
            state="disabled",
            width=18,
        )
        self.btn_cancelar.grid(row=0, column=3, padx=(5, 0), sticky="ew")

        # -----------------------------------------------------------------
        # Status
        # -----------------------------------------------------------------
        # STATUS = informações da playlist e estatísticas do download.
        frame_status = tk.Frame(
            main,
            bg=self.PANEL,
            highlightthickness=1,
            highlightbackground=self.BORDER,
        )
        frame_status.pack(fill="x", pady=(0, 12))

        status_title = tk.Frame(frame_status, bg=self.PANEL)
        status_title.pack(fill="x", padx=14, pady=(10, 4))

        tk.Label(
            status_title,
            text="STATUS",
            bg=self.PANEL,
            fg=self.GREEN,
            font=("Consolas", 9, "bold"),
        ).pack(side="left")

        self.label_status = tk.Label(
            status_title,
            text="● READY",
            bg=self.PANEL,
            fg=self.GREEN,
            font=("Consolas", 9, "bold"),
        )
        self.label_status.pack(side="right")

        status_body = tk.Frame(frame_status, bg=self.PANEL)
        status_body.pack(fill="x", padx=14, pady=(2, 12))

        self.label_playlist = tk.Label(
            status_body,
            text="Playlist: —",
            bg=self.PANEL,
            fg=self.TEXT,
            anchor="w",
            font=("Segoe UI", 10),
        )
        self.label_playlist.pack(fill="x")

        self.label_musica = tk.Label(
            status_body,
            text="Música: —",
            bg=self.PANEL,
            fg=self.MUTED,
            anchor="w",
            font=("Segoe UI", 10),
        )
        self.label_musica.pack(fill="x", pady=(5, 0))

        self.label_contador = tk.Label(
            status_body,
            text="0 / 0",
            bg=self.PANEL,
            fg=self.MUTED,
            anchor="w",
            font=("Consolas", 10, "bold"),
        )
        self.label_contador.pack(fill="x", pady=(5, 0))

        self.label_estatisticas = tk.Label(
            status_body,
            text="Baixadas: 0   |   Já existentes: 0   |   Erros: 0",
            bg=self.PANEL,
            fg=self.GREEN,
            anchor="w",
            font=("Consolas", 9, "bold"),
        )
        self.label_estatisticas.pack(fill="x", pady=(5, 0))

        # PROGRESSO = duas barras independentes:
        #   1) playlist inteira
        #   2) música atual
        # O bootstyle=SUCCESS deixa as barras verdes no tema darkly.
        # -----------------------------------------------------------------
        # Progresso
        # -----------------------------------------------------------------
        tk.Label(
            main,
            text="PROGRESSO DA PLAYLIST",
            bg=self.BG,
            fg=self.MUTED,
            anchor="w",
            font=("Consolas", 9, "bold"),
        ).pack(fill="x", pady=(0, 4))

        self.progress_bar = ttk.Progressbar(
            main,
            orient="horizontal",
            mode="determinate",
            maximum=100,
            bootstyle=SUCCESS,
        )
        self.progress_bar.pack(fill="x")

        self.progress_label = tk.Label(
            main,
            text="0%",
            bg=self.BG,
            fg=self.GREEN,
            anchor="e",
            font=("Consolas", 9, "bold"),
        )
        self.progress_label.pack(fill="x", pady=(1, 7))

        tk.Label(
            main,
            text="PROGRESSO DA MÚSICA",
            bg=self.BG,
            fg=self.MUTED,
            anchor="w",
            font=("Consolas", 9, "bold"),
        ).pack(fill="x", pady=(0, 4))

        self.current_progress = ttk.Progressbar(
            main,
            orient="horizontal",
            mode="determinate",
            maximum=100,
            bootstyle=SUCCESS,
        )
        self.current_progress.pack(fill="x")

        self.current_progress_label = tk.Label(
            main,
            text="0%",
            bg=self.BG,
            fg=self.MUTED,
            anchor="e",
            font=("Consolas", 8),
        )
        self.current_progress_label.pack(fill="x", pady=(1, 8))

        # LOG = terminal interno.
        # O Text é nativo do Tkinter para permitir personalização completa
        # do fundo, fonte, seleção e cursor.
        # -----------------------------------------------------------------
        # Log
        # -----------------------------------------------------------------
        frame_log = tk.Frame(
            main,
            bg=self.PANEL,
            highlightthickness=1,
            highlightbackground=self.BORDER,
        )
        frame_log.pack(fill="both", expand=True, pady=(0, 8))

        log_header = tk.Frame(frame_log, bg=self.PANEL_2)
        log_header.pack(fill="x")

        tk.Label(
            log_header,
            text="  LOG",
            bg=self.PANEL_2,
            fg=self.MUTED,
            anchor="w",
            font=("Consolas", 9, "bold"),
            pady=6,
        ).pack(side="left", fill="x", expand=True)

        self.log_text = tk.Text(
            frame_log,
            height=10,
            wrap="none",
            state="disabled",
            font=("Consolas", 9),
            bg=self.LOG_BG,
            fg="#cfd6de",
            insertbackground=self.TEXT,
            selectbackground="#26303a",
            selectforeground=self.TEXT,
            relief="flat",
            bd=0,
            highlightthickness=0,
            padx=9,
            pady=8,
        )
        self.log_text.pack(side="left", fill="both", expand=True)

        scrollbar = tk.Scrollbar(
            frame_log,
            orient="vertical",
            command=self.log_text.yview,
            bg=self.PANEL_2,
            troughcolor=self.LOG_BG,
            activebackground="#344150",
            relief="flat",
            bd=0,
            width=13,
        )
        scrollbar.pack(side="right", fill="y")

        self.log_text.configure(
            yscrollcommand=scrollbar.set,
        )

        # -----------------------------------------------------------------
        # Rodapé
        # -----------------------------------------------------------------
        footer = tk.Frame(main, bg=self.BG)
        footer.pack(fill="x")

        tk.Label(
            footer,
            text=f"Salvar em: {SAVE_PATH}",
            bg=self.BG,
            fg="#687585",
            anchor="w",
            font=("Segoe UI", 8),
        ).pack(side="left", fill="x", expand=True)

        tk.Label(
            footer,
            text="PKCE • READY",
            bg=self.BG,
            fg=self.GREEN,
            font=("Consolas", 8, "bold"),
        ).pack(side="right")

    # -------------------------------------------------------------------------
    # GUI utilities
    # -------------------------------------------------------------------------

    def adicionar_log(self, mensagem):
        # Adiciona uma linha ao terminal sem permitir edição pelo usuário.
        self.log_text.configure(state="normal")
        self.log_text.insert("end", f"{mensagem}\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def alterar_estado_botoes(self, executando):
        # Habilita/desabilita os botões durante um download e atualiza
        # o indicador READY/DOWNLOADING.
        self.download_em_andamento = executando

        estado = "disabled" if executando else "normal"
        self.btn_playlist.configure(state=estado)
        self.btn_lista.configure(state=estado)
        self.btn_musica.configure(state=estado)
        self.btn_cancelar.configure(
            state="normal" if executando else "disabled"
        )

        self.label_status.configure(
            text="● DOWNLOADING" if executando else "● READY",
            fg="#facc15" if executando else self.GREEN,
        )

    def resetar_progresso(self):
        # Volta toda a HUD para o estado inicial antes de um novo trabalho.
        self.progress_bar["value"] = 0
        self.current_progress["value"] = 0

        self.progress_label.configure(text="0%")
        self.current_progress_label.configure(text="0%")
        self.label_playlist.configure(text="Playlist: —")
        self.label_musica.configure(text="Música: —")
        self.label_contador.configure(text="0 / 0")
        self.label_estatisticas.configure(
            text="Baixadas: 0   |   Já existentes: 0   |   Erros: 0"
        )

        self.baixadas = 0
        self.puladas = 0
        self.erros = 0

    # -------------------------------------------------------------------------
    # Actions
    # -------------------------------------------------------------------------

    def baixar_playlist(self):
        # Fluxo: pede URL -> consulta Spotify -> cria pasta -> inicia thread.
        if self.download_em_andamento:
            return

        url = simpledialog.askstring(
            "Baixar Playlist",
            "Digite o link da playlist do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        self.adicionar_log("Obtendo informações da playlist...")

        try:
            playlist_nome, musicas = extrair_musicas_e_nome_playlist(
                self.spotify,
                url,
            )

            if not musicas:
                messagebox.showwarning(
                    "Playlist vazia",
                    "Nenhuma música válida foi encontrada.",
                    parent=self.root,
                )
                return

            pasta = criar_pasta(playlist_nome)

            self.resetar_progresso()
            self.label_playlist.configure(
                text=f"Playlist: {playlist_nome}"
            )
            self.label_contador.configure(
                text=f"0 / {len(musicas)}"
            )

            self.adicionar_log(f"Playlist encontrada: {playlist_nome}")
            self.adicionar_log(f"Músicas encontradas: {len(musicas)}")
            self.adicionar_log(f"Destino: {pasta}")

            self.alterar_estado_botoes(True)

            iniciado = self.download_manager.iniciar_playlist(
                playlist_nome,
                musicas,
                pasta,
            )

            if not iniciado:
                self.alterar_estado_botoes(False)

        except Exception as erro:
            messagebox.showerror(
                "Erro",
                "Não foi possível carregar a playlist.\n\n"
                f"{erro}",
                parent=self.root,
            )

    def listar_playlist(self):
        # Fluxo: pede URL -> consulta Spotify -> salva um .txt com as faixas.
        if self.download_em_andamento:
            return

        url = simpledialog.askstring(
            "Listar Playlist",
            "Digite o link da playlist do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        try:
            playlist_nome, musicas = extrair_musicas_e_nome_playlist(
                self.spotify,
                url,
            )

            pasta = criar_pasta(playlist_nome)
            arquivo = salvar_musicas_em_txt(musicas, pasta)

            messagebox.showinfo(
                "Sucesso",
                "Playlist salva em:\n\n"
                f"{arquivo}\n\n"
                f"Total de músicas: {len(musicas)}",
                parent=self.root,
            )

        except Exception as erro:
            messagebox.showerror(
                "Erro",
                "Não foi possível listar a playlist.\n\n"
                f"{erro}",
                parent=self.root,
            )

    def baixar_musica(self):
        # Fluxo: pede URL de uma faixa -> consulta metadata -> inicia thread.
        if self.download_em_andamento:
            return

        url = simpledialog.askstring(
            "Baixar Música",
            "Digite o link da música do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        try:
            titulo, artista = extrair_informacoes_musica(
                self.spotify,
                url,
            )

            pasta = criar_pasta("Musicas Individuais")

            self.resetar_progresso()
            self.label_playlist.configure(
                text="Playlist: Download individual"
            )

            self.alterar_estado_botoes(True)

            iniciado = self.download_manager.iniciar_musica(
                titulo,
                artista,
                pasta,
            )

            if not iniciado:
                self.alterar_estado_botoes(False)

        except Exception as erro:
            messagebox.showerror(
                "Erro",
                "Não foi possível obter a música.\n\n"
                f"{erro}",
                parent=self.root,
            )

    def cancelar_download(self):
        # O cancelamento usa threading.Event; a thread verifica esse evento
        # antes/durante o trabalho.
        if not self.download_em_andamento:
            return

        self.download_manager.cancelar()

        self.adicionar_log(
            "Solicitação de cancelamento enviada..."
        )

        self.btn_cancelar.configure(state="disabled")

    # -------------------------------------------------------------------------
    # UI queue
    # -------------------------------------------------------------------------

    def processar_fila(self):
        # Faz a ponte segura entre a thread de download e a GUI.
        # O Tkinter deve ser atualizado pela thread principal.
        try:
            while True:
                evento, args = self.ui_queue.get_nowait()
                self.processar_evento(evento, args)
        except Empty:
            pass

        try:
            if self.root.winfo_exists():
                self.root.after(100, self.processar_fila)
        except tk.TclError:
            pass

    def processar_evento(self, evento, args):
        # Cada evento recebido da thread atualiza uma parte específica da HUD.
        # Exemplos: progresso, log, estatisticas, erro, finalizado.
        if evento == "inicio":
            playlist_nome, total = args
            self.total_musicas = total
            self.label_playlist.configure(
                text=f"Playlist: {playlist_nome}"
            )
            self.label_contador.configure(
                text=f"0 / {total}"
            )
            return

        if evento == "musica":
            indice, total, titulo, artista = args
            self.label_musica.configure(
                text=f"Música: {titulo} — {artista}"
            )
            self.label_contador.configure(
                text=f"{indice} / {total}"
            )
            self.current_progress["value"] = 0
            self.current_progress_label.configure(text="0%")
            return

        if evento == "progresso":
            percentual = float(args[0])
            self.progress_bar["value"] = percentual
            self.progress_label.configure(
                text=f"{percentual:.0f}%"
            )
            return

        if evento == "download_progresso":
            percentual = float(args[0])
            self.current_progress["value"] = percentual
            self.current_progress_label.configure(
                text=f"{percentual:.1f}%"
            )
            return

        if evento == "estatisticas":
            self.baixadas, self.puladas, self.erros = args
            self.label_estatisticas.configure(
                text=(
                    f"Baixadas: {self.baixadas}   |   "
                    f"Já existentes: {self.puladas}   |   "
                    f"Erros: {self.erros}"
                )
            )
            return

        if evento == "log":
            self.adicionar_log(args[0])
            return

        if evento == "playlist_concluida":
            (
                playlist_nome,
                total,
                baixadas,
                puladas,
                erros,
            ) = args

            self.progress_bar["value"] = 100
            self.progress_label.configure(text="100%")

            self.baixadas = baixadas
            self.puladas = puladas
            self.erros = erros

            self.label_estatisticas.configure(
                text=(
                    f"Baixadas: {baixadas}   |   "
                    f"Já existentes: {puladas}   |   "
                    f"Erros: {erros}"
                )
            )

            self.adicionar_log("")
            self.adicionar_log("=" * 52)
            self.adicionar_log("DOWNLOAD FINALIZADO")
            self.adicionar_log(f"Playlist: {playlist_nome}")
            self.adicionar_log(f"Total: {total}")
            self.adicionar_log(f"Baixadas: {baixadas}")
            self.adicionar_log(f"Já existentes: {puladas}")
            self.adicionar_log(f"Erros: {erros}")
            self.adicionar_log("=" * 52)

            messagebox.showinfo(
                "Download concluído",
                (
                    f"Playlist '{playlist_nome}' concluída.\n\n"
                    f"Total: {total}\n"
                    f"Baixadas: {baixadas}\n"
                    f"Já existentes: {puladas}\n"
                    f"Erros: {erros}"
                ),
                parent=self.root,
            )
            return

        if evento == "individual_concluida":
            messagebox.showinfo(
                "Download concluído",
                args[0],
                parent=self.root,
            )
            return

        if evento == "cancelado":
            self.adicionar_log("Download cancelado pelo usuário.")
            messagebox.showinfo(
                "Cancelado",
                "O download foi cancelado.",
                parent=self.root,
            )
            return

        if evento == "erro":
            erro = args[0]
            self.adicionar_log(f"ERRO: {erro}")
            messagebox.showerror(
                "Erro",
                erro,
                parent=self.root,
            )
            return

        if evento == "finalizado":
            self.alterar_estado_botoes(False)

    # -------------------------------------------------------------------------
    # Fechamento
    # -------------------------------------------------------------------------

    def fechar(self):
        # Evita fechar a janela silenciosamente enquanto há trabalho ativo.
        if self.download_em_andamento:
            resposta = messagebox.askyesno(
                "Download em andamento",
                "Existe um download em andamento.\n\n"
                "Deseja cancelar e fechar?",
                parent=self.root,
            )

            if not resposta:
                return

            self.download_manager.cancelar()

        self.root.destroy()


# =============================================================================
# MAIN
# =============================================================================

def main():
    # ttkbootstrap fornece o tema darkly real para os widgets ttk.
    root = ttk.Window(themename="darkly")
    SpotifyDownloaderApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
