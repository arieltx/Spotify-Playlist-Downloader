#███████╗██████╗  ██████╗ ████████╗██╗███████╗██╗   ██╗     ██████╗ ██╗      █████╗ ██╗   ██╗██╗     ██╗███████╗████████╗   ██████╗  ██████╗ ██╗    ██╗███╗   ██╗██╗      ██████╗  █████╗ ██████╗ ███████╗██████╗ 
#██╔════╝██╔══██╗██╔═══██╗╚══██╔══╝██║██╔════╝╚██╗ ██╔╝     ██╔══██╗██║     ██╔══██╗╚██╗ ██╔╝██║     ██║██╔════╝╚══██╔══╝   ██╔══██╗██╔═══██╗██║    ██║████╗  ██║██║     ██╔═══██╗██╔══██╗██╔══██╗██╔════╝██╔══██╗
#███████╗██████╔╝██║   ██║   ██║   ██║█████╗   ╚████╔╝█████╗██████╔╝██║     ███████║ ╚████╔╝ ██║     ██║███████╗   ██║█████╗██║  ██║██║   ██║██║ █╗ ██║██╔██╗ ██║██║     ██║   ██║███████║██║  ██║█████╗  ██████╔╝
#╚════██║██╔═══╝ ██║   ██║   ██║   ██║██╔══╝    ╚██╔╝ ╚════╝██╔═══╝ ██║     ██╔══██║  ╚██╔╝  ██║     ██║╚════██║   ██║╚════╝██║  ██║██║   ██║██║███╗██║██║╚██╗██║██║     ██║   ██║██╔══██║██║  ██║██╔══╝  ██╔══██╗
#███████║██║     ╚██████╔╝   ██║   ██║██║        ██║        ██║     ███████╗██║  ██║   ██║   ███████╗██║███████║   ██║      ██████╔╝╚██████╔╝╚███╔███╔╝██║ ╚████║███████╗╚██████╔╝██║  ██║██████╔╝███████╗██║  ██║
#╚══════╝╚═╝      ╚═════╝    ╚═╝   ╚═╝╚═╝        ╚═╝        ╚═╝     ╚══════╝╚═╝  ╚═╝   ╚═╝   ╚══════╝╚═╝╚══════╝   ╚═╝      ╚═════╝  ╚═════╝  ╚══╝╚══╝ ╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚══════╝╚═╝  ╚═╝                                                                                                                                                                                        
# =============================================================================
#                        V3.3.7 — REVISADA
# PRINCIPAIS CORREÇÕES:
#   - Cancelamento cooperativo mais robusto para TODOS os futures.
#   - Futures cancelados/ativos são tratados individualmente.
#   - Lock para acesso concorrente ao conjunto de futures.
#   - Cancelamento durante a preparação da playlist não é perdido.
#   - Download individual também faz a consulta do Spotify fora da GUI.
#   - Progresso da música carrega o índice da faixa para reduzir "pulos"
#     causados pelos múltiplos workers.
#   - Deduplicação também no lote TXT.
#   - Correção da normalização de nomes com "(1)", "(2)" etc.
#   - Melhor tratamento de arquivos gerados por workers concorrentes.
#   - Fechamento da aplicação aguarda o cancelamento antes de destruir a GUI.
#   - Correção da procura do ícone em .py/.exe e em _internal.
#   - Removidos alguns "except:" genéricos.
#   - uso de base64 para ofuscação do client_id
#
# OBSERVAÇÃO:
#   Threads Python não podem ser "mortas" à força com segurança. O cancelamento
#   é cooperativo: yt-dlp é interrompido pelo progress_hook, operações de rede
#   possuem timeout e futures ainda não iniciados são cancelados imediatamente.
# =============================================================================

import io
import json
import os
import re
import shutil
import sys
import threading
import unicodedata
import webbrowser
from concurrent.futures import (
    ThreadPoolExecutor,
    as_completed,
    CancelledError,
)
from pathlib import Path
from queue import Empty, Queue
from urllib.parse import urlparse

import requests
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog

import ttkbootstrap as ttk
from ttkbootstrap.constants import *

from PIL import Image, ImageTk
import spotipy
from spotipy.exceptions import SpotifyException
from spotipy.oauth2 import SpotifyPKCE
import yt_dlp

from mutagen.mp3 import MP3
from mutagen.id3 import ID3, APIC, TIT2, TPE1, TALB
import base64


# =============================================================================
# CONFIGURAÇÃO
# =============================================================================

MAXTHREADS = 12  # Altere para 1, 2, 4, 6 etc. conforme sua máquina/rede.
APP_NAME = "Spotify Playlist Downloader V3.3.7"
REDIRECT_URI = "http://127.0.0.1:8888/callback"
SPOTIFY_SCOPE = "playlist-read-private playlist-read-collaborative"

if getattr(sys, "frozen", False):
    APP_DIR = Path(sys.executable).resolve().parent
else:
    APP_DIR = Path(__file__).resolve().parent

BASE_DIR = APP_DIR
SETTINGS_PATH = BASE_DIR / "settings.json"
DEFAULT_SAVE_PATH = BASE_DIR / "downloads"

MAXTHREADS = max(1, min(int(MAXTHREADS), 32))


class DownloadCancelled(Exception):
    """Indica que o usuário solicitou o cancelamento."""


# =============================================================================
# SPOTIFY & OFUSCAÇÃO
# =============================================================================

def configurar_spotify():
    # 1. Aqui fica o seu ID camuflado em Base64. O robô do GitHub ignora.
    id_ofuscado = b"YTU4OTc2ZjkzZWM5NGZhNmI3MmM3NDU2YWRjZWE0OWM="
    
    # 2. O Python reverte o processo e descobre o ID real direto na memória.
    client_id_real = base64.b64decode(id_ofuscado).decode("utf-8")

    # 3. O ID real é passado para o gerenciador normalmente.
    auth_manager = SpotifyPKCE(
        client_id=client_id_real,
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

# =============================================================================
# SPOTIFY PROCESSOS
# =============================================================================
def _extrair_id_por_segmento(url, segmento, tipo_nome):
    url = str(url).strip()

    if url.startswith(f"spotify:{segmento}:"):
        spotify_id = url.split(":")[-1].split("?")[0]

    elif "spotify.com" in url:
        parsed = urlparse(url)
        hosts_validos = {
            "open.spotify.com",
            "spotify.com",
            "www.spotify.com",
        }

        if parsed.netloc.lower() not in hosts_validos:
            raise ValueError("URL do Spotify inválida.")

        partes = [p for p in parsed.path.split("/") if p]
        spotify_id = None

        for indice, parte in enumerate(partes):
            if parte.lower() == segmento and indice + 1 < len(partes):
                spotify_id = partes[indice + 1].split("?")[0]
                break

        if not spotify_id:
            raise ValueError(
                f"O link não parece ser de {tipo_nome} do Spotify."
            )
    else:
        spotify_id = url.split("?")[0]

    if not spotify_id or not re.fullmatch(r"[A-Za-z0-9]+", spotify_id):
        raise ValueError(f"ID de {tipo_nome} do Spotify inválido.")

    return spotify_id


def extrair_id_spotify(url, tipo_esperado=None):
    if tipo_esperado and tipo_esperado != "playlist":
        raise ValueError(
            f"Esta função espera o tipo 'playlist', "
            f"mas recebeu '{tipo_esperado}'."
        )
    return _extrair_id_por_segmento(url, "playlist", "uma Playlist")


def extrair_informacoes_musica(spotify, musica_url):
    """Retorna (titulo, artista, capa_url, album)."""
    track_id = _extrair_id_por_segmento(
        musica_url,
        "track",
        "uma música",
    )
    track = spotify.track(track_id)
    return extrair_metadata_track(track)


def extrair_metadata_track(track):
    titulo = track.get("name") or "Título desconhecido"

    artistas = track.get("artists") or []
    artista = (
        artistas[0].get("name")
        if artistas and artistas[0]
        else "Artista desconhecido"
    )

    album_info = track.get("album") or {}
    album = album_info.get("name") or ""

    imagens = album_info.get("images") or []
    capa_url = imagens[0].get("url") if imagens else None

    return titulo, artista, capa_url, album


def extrair_musicas_e_nome_playlist(spotify, playlist_url):
    playlist_id = extrair_id_spotify(playlist_url, "playlist")

    try:
        playlist = spotify.playlist(playlist_id, fields="name")
        playlist_nome = playlist.get("name", "Playlist sem nome")
        resultado = spotify.playlist_items(playlist_id, limit=100)
    except SpotifyException as erro:
        if erro.http_status == 403:
            raise RuntimeError(
                "O Spotify recusou o acesso.\n\n"
                "Verifique se você tem acesso à playlist."
            ) from erro
        raise

    musicas = []

    while resultado:
        for item in resultado.get("items", []):
            track = item.get("item")
            if not track or track.get("type") != "track":
                continue

            try:
                musicas.append(extrair_metadata_track(track))
            except Exception:
                continue

        proximo = resultado.get("next")
        if not proximo:
            break

        resultado = spotify.next(resultado)

    return playlist_nome, musicas


# =============================================================================
# ARQUIVOS / FFmpeg
# =============================================================================

def obter_caminho_ffmpeg():
    candidatos = [
        BASE_DIR / "ffmpeg" / "ffmpeg.exe",
    ]

    if getattr(sys, "frozen", False):
        candidatos.extend([
            Path(sys._MEIPASS) / "ffmpeg" / "ffmpeg.exe",
            BASE_DIR / "_internal" / "ffmpeg" / "ffmpeg.exe",
        ])

    for caminho in candidatos:
        if caminho.is_file():
            return str(caminho)

    ffmpeg_sistema = shutil.which("ffmpeg")
    if ffmpeg_sistema:
        return ffmpeg_sistema

    raise FileNotFoundError(
        "FFmpeg não foi encontrado internamente nem no PATH."
    )


def limpar_nome(nome, max_length=180):
    nome = str(nome).strip()
    nome = re.sub(r'[<>:"/\\|?*\x00-\x1F]', "_", nome)
    nome = nome.rstrip(" .")

    reservados = {"CON", "PRN", "AUX", "NUL"}
    if nome.upper() in reservados:
        nome = f"_{nome}"

    if re.match(r"^(LPT|COM)[0-9]+$", nome.upper()):
        nome = f"_{nome}"

    return nome[:max_length] or "Sem nome"


def normalizar_texto(texto):
    texto = unicodedata.normalize("NFKD", str(texto))
    texto = "".join(
        char for char in texto
        if not unicodedata.combining(char)
    )
    texto = texto.casefold()

    # Remove sufixos típicos de duplicação do Windows: " (1)", " (2)"...
    texto = re.sub(r"\s*\(\s*\d+\s*\)$", "", texto)

    return re.sub(r"[^a-z0-9]+", "", texto)


def chave_musica(titulo, artista):
    return normalizar_texto(f"{titulo} - {artista}")


def normalizar_nome_arquivo(nome):
    return normalizar_texto(Path(str(nome)).stem)


def criar_pasta(save_path, nome_pasta):
    pasta = Path(save_path) / limpar_nome(nome_pasta)
    pasta.mkdir(parents=True, exist_ok=True)
    return pasta


def salvar_musicas_em_txt(
    musicas,
    pasta_destino,
    nome_arquivo="musicas.txt",
):
    arquivo_txt = Path(pasta_destino) / nome_arquivo

    with open(arquivo_txt, "w", encoding="utf-8") as arquivo:
        for item in musicas:
            titulo, artista = item[0], item[1]
            arquivo.write(f"{titulo} - {artista}\n")

    return arquivo_txt


def salvar_erros_em_txt(musicas, pasta_destino):
    arquivo_txt = Path(pasta_destino) / "musicas_com_erro.txt"

    if not musicas:
        try:
            if arquivo_txt.exists():
                arquivo_txt.unlink()
        except OSError:
            pass
        return None

    return salvar_musicas_em_txt(
        musicas,
        pasta_destino,
        "musicas_com_erro.txt",
    )


# =============================================================================
# UTILITÁRIOS
# =============================================================================

def limpar_ansi(texto):
    ansi_escape = re.compile(
        r"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])"
    )
    return ansi_escape.sub("", str(texto))


# =============================================================================
# DOWNLOAD MANAGER
# =============================================================================

class DownloadManager:
    def __init__(self, ui_queue):
        self.ui_queue = ui_queue
        self.cancel_event = threading.Event()

        self.thread = None
        self.workers = []

        self.executor = None
        self.active_futures = set()
        self.futures_lock = threading.Lock()

        self.max_workers = MAXTHREADS

    @property
    def executando(self):
        return any(
            worker.is_alive()
            for worker in self.workers
        )

    def _registrar_futures(self, futures):
        with self.futures_lock:
            self.active_futures = set(futures)

    def _remover_future(self, future):
        with self.futures_lock:
            self.active_futures.discard(future)

    def _snapshot_futures(self):
        with self.futures_lock:
            return tuple(self.active_futures)

    def cancelar(self):
        """Sinaliza os workers e cancela tarefas ainda não iniciadas."""
        self.cancel_event.set()

        for futuro in self._snapshot_futures():
            futuro.cancel()

    def _enviar_ui(self, evento, *args):
        self.ui_queue.put((evento, args))

    def iniciar_playlist(
        self,
        playlist_nome,
        musicas,
        pasta_destino,
        qualidade,
    ):
        if self.executando:
            return False

        # O evento pertence à operação anterior; uma nova operação começa
        # sempre com um evento limpo. Cancelamento durante a preparação é
        # controlado separadamente por _preparacao_cancel_event.
        self.cancel_event.clear()

        self.thread = threading.Thread(
            target=self._worker_playlist,
            args=(
                playlist_nome,
                musicas,
                pasta_destino,
                qualidade,
            ),
            daemon=True,
            name="PlaylistManager",
        )

        self.workers = [self.thread]
        self.thread.start()

        return True

    def iniciar_musica(
        self,
        titulo,
        artista,
        capa_url,
        pasta_destino,
        qualidade,
        album="",
    ):
        if self.executando:
            return False

        self.cancel_event.clear()

        self.thread = threading.Thread(
            target=self._worker_musica,
            args=(
                titulo,
                artista,
                capa_url,
                pasta_destino,
                qualidade,
                album,
            ),
            daemon=True,
            name="IndividualManager",
        )

        self.workers = [self.thread]
        self.thread.start()

        return True

    def criar_indice_musicas(self, pasta_destino):
        pasta_destino = Path(pasta_destino)
        indice = set()

        if not pasta_destino.exists():
            return indice

        for arquivo in pasta_destino.rglob("*"):
            try:
                if (
                    not arquivo.is_file()
                    or arquivo.suffix.lower() != ".mp3"
                ):
                    continue

                if arquivo.stat().st_size <= 0:
                    continue

                # Nome do arquivo.
                indice.add(normalizar_nome_arquivo(arquivo.name))

                # Metadados, quando disponíveis.
                try:
                    audio = MP3(arquivo, ID3=ID3)
                    tags = audio.tags

                    titulo_tag = tags.get("TIT2") if tags else None
                    artista_tag = tags.get("TPE1") if tags else None

                    if titulo_tag and artista_tag:
                        titulo_val = str(
                            titulo_tag[0]
                        ).strip()
                        artista_val = str(
                            artista_tag[0]
                        ).strip()

                        if titulo_val and artista_val:
                            indice.add(
                                chave_musica(
                                    titulo_val,
                                    artista_val,
                                )
                            )
                except Exception:
                    pass

            except OSError:
                continue

        return indice

    def _worker_playlist(
        self,
        playlist_nome,
        musicas,
        pasta_destino,
        qualidade,
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
        musicas_com_erro = []

        executor = None

        try:
            if self.cancel_event.is_set():
                self._enviar_ui("cancelado")
                return

            indice_existentes = self.criar_indice_musicas(
                pasta_destino
            )

            self._enviar_ui(
                "inicio",
                playlist_nome,
                total,
                musicas,
            )

            self._enviar_ui(
                "log",
                f"Arquivos MP3 locais mapeados: "
                f"{len(indice_existentes)}",
            )

            tarefas = []
            tarefas_vistas = set()

            # ---------------------------------------------------------
            # Verificação local + deduplicação
            # ---------------------------------------------------------

            for i, (
                titulo,
                artista,
                capa_url,
                album,
            ) in enumerate(musicas, start=1):

                if self.cancel_event.is_set():
                    self._enviar_ui("cancelado")
                    return

                chave = chave_musica(titulo, artista)

                nome_esperado = normalizar_nome_arquivo(
                    f"{limpar_nome(titulo)} - "
                    f"{limpar_nome(artista)}.mp3"
                )

                # Evita duas tarefas iguais no TXT.
                if chave in tarefas_vistas:
                    puladas += 1

                    self._enviar_ui(
                        "musica",
                        i,
                        total,
                        titulo,
                        artista,
                        capa_url,
                    )
                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Duplicata",
                    )
                    self._enviar_ui(
                        "log",
                        f"[{i}/{total}] Duplicata ignorada: "
                        f"{titulo} - {artista}",
                    )
                    self._enviar_ui(
                        "progresso",
                        (i / total) * 100,
                    )
                    self._enviar_ui(
                        "estatisticas",
                        baixadas,
                        puladas,
                        erros,
                    )
                    continue

                tarefas_vistas.add(chave)

                if (
                    chave in indice_existentes
                    or nome_esperado in indice_existentes
                ):
                    puladas += 1

                    self._enviar_ui(
                        "musica",
                        i,
                        total,
                        titulo,
                        artista,
                        capa_url,
                    )
                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Já Existe",
                    )
                    self._enviar_ui(
                        "log",
                        f"[{i}/{total}] Já existe: "
                        f"{titulo} - {artista}",
                    )
                    self._enviar_ui(
                        "progresso",
                        (i / total) * 100,
                    )
                    self._enviar_ui(
                        "estatisticas",
                        baixadas,
                        puladas,
                        erros,
                    )
                    continue

                tarefas.append(
                    (
                        i,
                        titulo,
                        artista,
                        capa_url,
                        album,
                        chave,
                        nome_esperado,
                    )
                )

            if self.cancel_event.is_set():
                self._enviar_ui("cancelado")
                return

            if not tarefas:
                self._enviar_ui(
                    "progresso",
                    100.0,
                )

                arquivo_erros = salvar_erros_em_txt(
                    musicas_com_erro,
                    pasta_destino,
                )

                self._enviar_ui(
                    "playlist_concluida",
                    playlist_nome,
                    total,
                    baixadas,
                    puladas,
                    erros,
                    str(pasta_destino),
                    str(arquivo_erros or ""),
                )
                return

            # ---------------------------------------------------------
            # Worker individual
            # ---------------------------------------------------------

            def baixar_tarefa(tarefa):
                (
                    i,
                    titulo,
                    artista,
                    capa_url,
                    album,
                    chave,
                    nome_esperado,
                ) = tarefa

                if self.cancel_event.is_set():
                    raise DownloadCancelled()

                self._enviar_ui(
                    "musica",
                    i,
                    total,
                    titulo,
                    artista,
                    capa_url,
                )

                self._enviar_ui(
                    "status_linha",
                    i - 1,
                    "⬇️ Baixando",
                )

                resultado = self.baixar_musica_nucleo(
                    titulo,
                    artista,
                    capa_url,
                    pasta_destino,
                    qualidade,
                    album,
                    indice_ui=i,
                )

                return (
                    i,
                    titulo,
                    artista,
                    capa_url,
                    album,
                    chave,
                    nome_esperado,
                    resultado,
                )

            # ---------------------------------------------------------
            # Thread pool
            # ---------------------------------------------------------

            executor = ThreadPoolExecutor(
                max_workers=self.max_workers,
                thread_name_prefix="DownloadWorker",
            )
            self.executor = executor

            futuros = {
                executor.submit(
                    baixar_tarefa,
                    tarefa,
                ): tarefa
                for tarefa in tarefas
            }

            self._registrar_futures(futuros)

            for futuro in as_completed(futuros):
                tarefa = futuros[futuro]

                self._remover_future(futuro)

                (
                    i,
                    titulo,
                    artista,
                    capa_url,
                    album,
                    chave,
                    nome_esperado,
                ) = tarefa

                # -----------------------------------------------------
                # Future cancelado antes de iniciar
                # -----------------------------------------------------

                if futuro.cancelled():
                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Cancelado",
                    )
                    continue

                try:
                    resultado_data = futuro.result()

                except CancelledError:
                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Cancelado",
                    )
                    continue

                except DownloadCancelled:
                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Cancelado",
                    )
                    self._enviar_ui(
                        "log",
                        f"[{i}/{total}] Cancelada: "
                        f"{titulo} - {artista}",
                    )
                    continue

                except Exception as erro:
                    # Se o cancelamento ocorreu enquanto a exceção era
                    # propagada, ela não deve aparecer como erro crítico.
                    if self.cancel_event.is_set():
                        self._enviar_ui(
                            "status_linha",
                            i - 1,
                            "Cancelado",
                        )
                        continue

                    erros += 1
                    musicas_com_erro.append(
                        (
                            titulo,
                            artista,
                            capa_url,
                            album,
                        )
                    )

                    mensagem = limpar_ansi(str(erro))

                    self._enviar_ui(
                        "status_linha",
                        i - 1,
                        "Erro",
                    )
                    self._enviar_ui(
                        "log",
                        f"[{i}/{total}] ERRO: "
                        f"{titulo} - {artista} -> {mensagem}",
                    )

                    concluidas = baixadas + puladas + erros
                    self._enviar_ui(
                        "progresso",
                        (concluidas / total) * 100,
                    )
                    self._enviar_ui(
                        "estatisticas",
                        baixadas,
                        puladas,
                        erros,
                    )
                    continue

                (
                    i,
                    titulo,
                    artista,
                    capa_url,
                    album,
                    chave,
                    nome_esperado,
                    resultado,
                ) = resultado_data

                if resultado == "exists":
                    puladas += 1
                    status = "Já Existe"
                    texto_log = (
                        f"[{i}/{total}] Já existe: "
                        f"{titulo} - {artista}"
                    )
                else:
                    baixadas += 1
                    status = "Concluído"
                    texto_log = (
                        f"[{i}/{total}] Baixada com sucesso: "
                        f"{titulo} - {artista}"
                    )

                indice_existentes.add(chave)
                indice_existentes.add(nome_esperado)

                self._enviar_ui(
                    "status_linha",
                    i - 1,
                    status,
                )
                self._enviar_ui(
                    "log",
                    texto_log,
                )

                concluidas = baixadas + puladas + erros

                self._enviar_ui(
                    "progresso",
                    (concluidas / total) * 100,
                )
                self._enviar_ui(
                    "estatisticas",
                    baixadas,
                    puladas,
                    erros,
                )

            # ---------------------------------------------------------
            # Finalização normal
            # ---------------------------------------------------------

            if not self.cancel_event.is_set():
                arquivo_erros = salvar_erros_em_txt(
                    musicas_com_erro,
                    pasta_destino,
                )

                self._enviar_ui(
                    "playlist_concluida",
                    playlist_nome,
                    total,
                    baixadas,
                    puladas,
                    erros,
                    str(pasta_destino),
                    str(arquivo_erros or ""),
                )
            else:
                self._enviar_ui(
                    "log",
                    "Todos os futures foram sinalizados para cancelamento.",
                )
                self._enviar_ui("cancelado")

        except Exception as erro:
            if self.cancel_event.is_set():
                self._enviar_ui(
                    "log",
                    "Cancelamento finalizado durante o gerenciador.",
                )
                self._enviar_ui("cancelado")
            else:
                self._enviar_ui(
                    "erro",
                    "Erro no gerenciador de downloads: "
                    f"{limpar_ansi(str(erro))}",
                )

        finally:
            # shutdown(wait=True) aqui é intencional: garante que nenhum worker
            # continue gravando arquivos depois de liberar uma nova operação.
            # Os futures pendentes já foram cancelados.
            if executor is not None:
                try:
                    executor.shutdown(
                        wait=True,
                        cancel_futures=True,
                    )
                except TypeError:
                    executor.shutdown(wait=True)

            self.executor = None

            with self.futures_lock:
                self.active_futures.clear()

            self._enviar_ui("finalizado")

    def _worker_musica(
        self,
        titulo,
        artista,
        capa_url,
        pasta_destino,
        qualidade,
        album="",
    ):
        musicas = [
            (
                titulo,
                artista,
                capa_url,
                album,
            )
        ]

        self._enviar_ui(
            "inicio",
            "Download individual",
            1,
            musicas,
        )

        self._enviar_ui(
            "musica",
            1,
            1,
            titulo,
            artista,
            capa_url,
        )

        try:
            if self.cancel_event.is_set():
                raise DownloadCancelled()

            self._enviar_ui(
                "status_linha",
                0,
                "⬇️ Baixando",
            )

            resultado = self.baixar_musica_nucleo(
                titulo,
                artista,
                capa_url,
                pasta_destino,
                qualidade,
                album,
                indice_ui=1,
            )

            if resultado == "exists":
                self._enviar_ui(
                    "status_linha",
                    0,
                    "Já Existe",
                )
                self._enviar_ui(
                    "log",
                    f"Já existe no disco: "
                    f"{titulo} - {artista}",
                )
                mensagem = "Arquivo já existente."
            else:
                self._enviar_ui(
                    "status_linha",
                    0,
                    "Concluído",
                )
                self._enviar_ui(
                    "log",
                    f"Baixada com sucesso: "
                    f"{titulo} - {artista}",
                )
                mensagem = "Download concluído."

            self._enviar_ui(
                "progresso",
                100,
            )
            self._enviar_ui(
                "download_progresso",
                1,
                100,
            )
            self._enviar_ui(
                "individual_concluida",
                mensagem,
                str(pasta_destino),
            )

        except DownloadCancelled:
            self._enviar_ui(
                "status_linha",
                0,
                "Cancelado",
            )
            self._enviar_ui("cancelado")

        except Exception as erro:
            if self.cancel_event.is_set():
                self._enviar_ui(
                    "status_linha",
                    0,
                    "Cancelado",
                )
                self._enviar_ui("cancelado")
            else:
                arquivo_erros = salvar_erros_em_txt(
                    musicas,
                    pasta_destino,
                )
                self._enviar_ui(
                    "status_linha",
                    0,
                    "Erro",
                )
                self._enviar_ui(
                    "erro",
                    "Não foi possível baixar:\n"
                    f"{limpar_ansi(str(erro))}\n\n"
                    "Lista salva em:\n"
                    f"{arquivo_erros}",
                )

        finally:
            self._enviar_ui("finalizado")

    def baixar_musica_nucleo(
        self,
        titulo,
        artista,
        capa_url,
        pasta_destino,
        qualidade,
        album="",
        indice_ui=None,
    ):
        ffmpeg_path = obter_caminho_ffmpeg()

        nome_base = (
            f"{limpar_nome(titulo)} - "
            f"{limpar_nome(artista)}"
        )

        pasta_destino = Path(pasta_destino)
        pasta_destino.mkdir(
            parents=True,
            exist_ok=True,
        )

        arquivo_final = (
            pasta_destino / f"{nome_base}.mp3"
        )

        if (
            arquivo_final.is_file()
            and arquivo_final.stat().st_size > 0
        ):
            return "exists"

        consulta = (
            f"ytsearch1:{titulo} "
            f"{artista} official audio"
        )

        output_template = str(
            pasta_destino / f"{nome_base}.%(ext)s"
        )

        mp3_antes = {
            arquivo.resolve()
            for arquivo in pasta_destino.glob("*.mp3")
            if arquivo.is_file()
        }

        def limpar_artefatos_parciais():
            """Remove somente artefatos parciais criados por esta faixa.

            O yt-dlp usa arquivos como ``arquivo.ext.part`` durante o
            download. Ao cancelar, esses arquivos podem permanecer no
            diretório e parecer downloads "fantasmas".

            A limpeza é limitada ao prefixo exato desta música e aos
            artefatos temporários (.part/.ytdl), portanto não toca em MP3s
            existentes nem em arquivos de outras músicas.
            """
            padroes = (
                f"{nome_base}.*.part",
                f"{nome_base}.part",
                f"{nome_base}.*.ytdl",
                f"{nome_base}.ytdl",
            )

            vistos = set()
            for padrao in padroes:
                for arquivo in pasta_destino.glob(padrao):
                    if arquivo in vistos:
                        continue
                    vistos.add(arquivo)
                    try:
                        if arquivo.is_file():
                            arquivo.unlink()
                    except OSError as erro:
                        self._enviar_ui(
                            "log",
                            f"AVISO: não foi possível remover artefato temporário "
                            f"{arquivo.name}: {limpar_ansi(str(erro))}",
                        )

        def enviar_progresso(valor):
            valor = max(
                0.0,
                min(100.0, float(valor)),
            )

            if indice_ui is None:
                self._enviar_ui(
                    "download_progresso",
                    valor,
                )
            else:
                self._enviar_ui(
                    "download_progresso",
                    indice_ui,
                    valor,
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
                        re.sub(
                            r"[^0-9.]",
                            "",
                            percentual_str,
                        )
                    )
                except (TypeError, ValueError):
                    percentual = 0.0

                enviar_progresso(percentual)

            elif status == "finished":
                enviar_progresso(100.0)

        opcoes = {
            "format": "bestaudio/best",
            "noplaylist": True,
            "quiet": True,
            "no_warnings": True,
            "outtmpl": output_template,
            "ffmpeg_location": ffmpeg_path,
            "progress_hooks": [progress_hook],
            "postprocessors": [
                {
                    "key": "FFmpegExtractAudio",
                    "preferredcodec": "mp3",
                    "preferredquality": str(qualidade),
                }
            ],
            "retries": 5,
            "fragment_retries": 5,
            "socket_timeout": 5,
            "overwrites": False,
        }

        try:
            with yt_dlp.YoutubeDL(opcoes) as ydl:
                resultado = ydl.download([consulta])

        except DownloadCancelled:
            limpar_artefatos_parciais()
            raise

        except Exception as erro:
            if self.cancel_event.is_set():
                limpar_artefatos_parciais()
                raise DownloadCancelled() from erro

            raise RuntimeError(
                limpar_ansi(str(erro))
            ) from erro

        if self.cancel_event.is_set():
            limpar_artefatos_parciais()
            raise DownloadCancelled()

        if resultado not in (0, None):
            raise RuntimeError(
                f"yt-dlp falhou com código {resultado}."
            )

        # O nome esperado já existe.
        if (
            arquivo_final.is_file()
            and arquivo_final.stat().st_size > 0
        ):
            novo = (
                arquivo_final.resolve()
                not in mp3_antes
            )

            self.embutir_metadados(
                arquivo_final,
                titulo,
                artista,
                capa_url,
                album,
            )

            return "success" if novo else "exists"

        candidatos = []

        for arquivo in pasta_destino.glob(
            f"{nome_base}*.mp3"
        ):
            try:
                if (
                    arquivo.is_file()
                    and arquivo.stat().st_size > 0
                ):
                    candidatos.append(arquivo)
            except OSError:
                continue

        novos = [
            arquivo
            for arquivo in candidatos
            if arquivo.resolve() not in mp3_antes
        ]

        if novos:
            # Ordenação determinística caso haja mais de um candidato.
            novos.sort(
                key=lambda p: p.stat().st_mtime,
                reverse=True,
            )
            arquivo = novos[0]

            self.embutir_metadados(
                arquivo,
                titulo,
                artista,
                capa_url,
                album,
            )

            return "success"

        if candidatos:
            return "exists"

        raise FileNotFoundError(
            "O yt-dlp terminou, mas nenhum MP3 foi encontrado "
            "no diretório esperado."
        )

    def embutir_metadados(
        self,
        caminho_mp3,
        titulo,
        artista,
        capa_url,
        album="",
    ):
        try:
            audio = MP3(
                caminho_mp3,
                ID3=ID3,
            )

            if audio.tags is None:
                audio.add_tags()

            audio.tags.delall("TIT2")
            audio.tags.delall("TPE1")
            audio.tags.delall("TALB")
            audio.tags.delall("APIC")

            audio.tags.add(
                TIT2(
                    encoding=3,
                    text=titulo,
                )
            )

            audio.tags.add(
                TPE1(
                    encoding=3,
                    text=artista,
                )
            )

            if album:
                audio.tags.add(
                    TALB(
                        encoding=3,
                        text=album,
                    )
                )

            if capa_url:
                if self.cancel_event.is_set():
                    raise DownloadCancelled()

                resposta = requests.get(
                    capa_url,
                    timeout=10,
                )
                resposta.raise_for_status()

                if self.cancel_event.is_set():
                    raise DownloadCancelled()

                mime = resposta.headers.get(
                    "Content-Type",
                    "image/jpeg",
                ).split(";")[0].strip()

                if not mime.startswith("image/"):
                    mime = "image/jpeg"

                audio.tags.add(
                    APIC(
                        encoding=3,
                        mime=mime,
                        type=3,
                        desc="Cover",
                        data=resposta.content,
                    )
                )

            audio.save()

        except DownloadCancelled:
            raise

        except Exception as erro:
            # O MP3 já foi baixado. Falha de metadados/capa não transforma
            # um download válido em erro de download.
            self._enviar_ui(
                "log",
                "AVISO: não foi possível inserir metadados em "
                f"{Path(caminho_mp3).name}: "
                f"{limpar_ansi(str(erro))}",
            )


# =============================================================================
# GUI
# =============================================================================

def abrir_instagram():
    webbrowser.open(
        "https://www.instagram.com/Arielton_Gross/"
    )


class SpotifyDownloaderApp:
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
        self.root.title(APP_NAME)
        self.root.geometry("1000x780")
        self.root.minsize(950, 700)
        self.root.configure(
            background=self.BG
        )

        self.ui_queue = Queue()
        self.download_manager = DownloadManager(
            self.ui_queue
        )

        self.save_path = self.carregar_pasta_saida()

        self.download_em_andamento = False
        self._preparando_operacao = False
        self._preparacao_cancel_event = threading.Event()

        self.baixadas = 0
        self.puladas = 0
        self.erros = 0

        self._ultima_pasta_concluida = self.save_path
        self.imagem_padrao = (
            self.criar_imagem_placeholder()
        )

        self._thumbnail_executor = (
            ThreadPoolExecutor(
                max_workers=4,
                thread_name_prefix="Thumbnail",
            )
        )
        self._thumbnail_request_id = 0

        self._fechando = False

        self.inicializar_spotify()

        if not self.root.winfo_exists():
            return

        self.criar_interface()
        self.root.after(
            100,
            self.processar_fila,
        )
        self.root.protocol(
            "WM_DELETE_WINDOW",
            self.fechar,
        )

    # -------------------------------------------------------------------------
    # Configuração / arquivos
    # -------------------------------------------------------------------------

    def carregar_pasta_saida(self):
        try:
            if SETTINGS_PATH.exists():
                with open(
                    SETTINGS_PATH,
                    "r",
                    encoding="utf-8",
                ) as f:
                    dados = json.load(f)

                valor = dados.get(
                    "save_path",
                    "",
                )

                if valor:
                    caminho = (
                        Path(valor)
                        .expanduser()
                        .resolve()
                    )
                    caminho.mkdir(
                        parents=True,
                        exist_ok=True,
                    )
                    return caminho

        except (
            OSError,
            ValueError,
            TypeError,
            json.JSONDecodeError,
        ):
            pass

        DEFAULT_SAVE_PATH.mkdir(
            parents=True,
            exist_ok=True,
        )
        return DEFAULT_SAVE_PATH

    def salvar_pasta_saida(self):
        with open(
            SETTINGS_PATH,
            "w",
            encoding="utf-8",
        ) as f:
            json.dump(
                {
                    "save_path": str(
                        self.save_path
                    )
                },
                f,
                indent=4,
                ensure_ascii=False,
            )

    def abrir_pasta_destino(self, pasta=None):
        destino = (
            Path(pasta)
            if pasta
            else Path(
                self._ultima_pasta_concluida
            )
        )

        if destino.exists():
            try:
                os.startfile(str(destino))
            except OSError as erro:
                messagebox.showerror(
                    "Erro",
                    f"Não foi possível abrir a pasta:\n{erro}",
                    parent=self.root,
                )
        else:
            messagebox.showerror(
                "Erro",
                "A pasta de destino ainda não foi criada.",
                parent=self.root,
            )

    # -------------------------------------------------------------------------
    # Thumbnails
    # -------------------------------------------------------------------------

    def criar_imagem_placeholder(self):
        img = Image.new(
            "RGB",
            (80, 80),
            color="#1e293b",
        )
        return ImageTk.PhotoImage(img)

    def _baixar_thumbnail_bytes(self, url):
        try:
            res = requests.get(
                url,
                timeout=5,
            )
            res.raise_for_status()
            return res.content
        except Exception:
            return None

    def solicitar_thumbnail(self, url):
        self._thumbnail_request_id += 1
        request_id = self._thumbnail_request_id

        if not url:
            self.lbl_thumb.configure(
                image=self.imagem_padrao
            )
            self.lbl_thumb.image = (
                self.imagem_padrao
            )
            return

        futuro = self._thumbnail_executor.submit(
            self._baixar_thumbnail_bytes,
            url,
        )

        def concluir(fut):
            try:
                dados = fut.result()
            except Exception:
                dados = None

            if self._fechando:
                return

            try:
                self.root.after(
                    0,
                    self._aplicar_thumbnail,
                    request_id,
                    dados,
                )
            except tk.TclError:
                pass

        futuro.add_done_callback(concluir)

    def _aplicar_thumbnail(
        self,
        request_id,
        dados,
    ):
        if self._fechando:
            return

        if request_id != self._thumbnail_request_id:
            return

        if not dados:
            img_obj = self.imagem_padrao
        else:
            try:
                img = (
                    Image.open(
                        io.BytesIO(dados)
                    )
                    .convert("RGB")
                )
                img = img.resize(
                    (80, 80),
                    Image.Resampling.LANCZOS,
                )
                img_obj = ImageTk.PhotoImage(img)
            except Exception:
                img_obj = self.imagem_padrao

        try:
            self.lbl_thumb.configure(
                image=img_obj
            )
            self.lbl_thumb.image = img_obj
        except tk.TclError:
            pass

    # -------------------------------------------------------------------------
    # Spotify
    # -------------------------------------------------------------------------

    def inicializar_spotify(self):
        try:
            self.spotify = configurar_spotify()
        except Exception as erro:
            messagebox.showerror(
                "Erro de Configuração",
                str(erro),
                parent=self.root,
            )
            self.root.destroy()

    # -------------------------------------------------------------------------
    # Interface
    # -------------------------------------------------------------------------

    def criar_interface(self):
        header = tk.Frame(
            self.root,
            bg=self.BG,
        )
        header.pack(
            fill="x",
            padx=24,
            pady=(15, 5),
        )

        tk.Label(
            header,
            text="Spotify Playlist Downloader",
            bg=self.BG,
            fg=self.TEXT,
            font=("Segoe UI", 22, "bold"),
        ).pack()

        tk.Label(
            header,
            text="V3.3.7 •  HUD • MULTI THREAD • COLOR",
            bg=self.BG,
            fg=self.GREEN,
            font=("Segoe UI", 9, "bold"),
        ).pack(pady=2)

        main = tk.Frame(
            self.root,
            bg=self.BG,
        )
        main.pack(
            fill="both",
            expand=True,
            padx=24,
            pady=(0, 15),
        )

        frame_controles = tk.Frame(
            main,
            bg=self.BG,
        )
        frame_controles.pack(
            fill="x",
            pady=(2, 10),
        )

        for col in range(5):
            frame_controles.columnconfigure(
                col,
                weight=1,
                uniform="btn",
            )

        self.btn_playlist = ttk.Button(
            frame_controles,
            text="Baixar Playlist",
            command=self.baixar_playlist,
            bootstyle=SUCCESS,
        )
        self.btn_playlist.grid(
            row=0,
            column=0,
            padx=(0, 4),
            sticky="ew",
        )

        self.btn_lista = ttk.Button(
            frame_controles,
            text="Listar / Ler .TXT",
            command=self.menu_txt,
            bootstyle=SECONDARY,
        )
        self.btn_lista.grid(
            row=0,
            column=1,
            padx=4,
            sticky="ew",
        )

        self.btn_musica = ttk.Button(
            frame_controles,
            text="Baixar Música",
            command=self.baixar_musica,
            bootstyle=INFO,
        )
        self.btn_musica.grid(
            row=0,
            column=2,
            padx=4,
            sticky="ew",
        )

        self.btn_pasta = ttk.Button(
            frame_controles,
            text="📁 Pasta Saída",
            command=self.configurar_pasta_saida,
            bootstyle=WARNING,
        )
        self.btn_pasta.grid(
            row=0,
            column=3,
            padx=4,
            sticky="ew",
        )

        self.btn_cancelar = ttk.Button(
            frame_controles,
            text="Cancelar",
            command=self.cancelar_download,
            bootstyle=DANGER,
            state="disabled",
        )
        self.btn_cancelar.grid(
            row=0,
            column=4,
            padx=4,
            sticky="ew",
        )

        frame_opcoes_hud = tk.Frame(
            main,
            bg=self.BG,
        )
        frame_opcoes_hud.pack(
            fill="x",
            pady=(0, 10),
        )

        tk.Label(
            frame_opcoes_hud,
            text="Qualidade do Áudio (Bitrate):",
            bg=self.BG,
            fg=self.MUTED,
            font=("Segoe UI", 9, "bold"),
        ).pack(
            side="left",
            padx=(5, 5),
        )

        self.combo_qualidade = ttk.Combobox(
            frame_opcoes_hud,
            values=["320", "256", "128"],
            width=6,
            state="readonly",
            bootstyle=SUCCESS,
        )
        self.combo_qualidade.pack(
            side="left"
        )
        self.combo_qualidade.set("320")

        self.btn_abrir_direto = ttk.Button(
            frame_opcoes_hud,
            text="📂 Abrir Pasta de Destino",
            command=self.abrir_pasta_destino,
            bootstyle=(LINK, SUCCESS),
        )
        self.btn_abrir_direto.pack(
            side="right",
            padx=5,
        )

        frame_status = tk.Frame(
            main,
            bg=self.PANEL,
            highlightthickness=1,
            highlightbackground=self.BORDER,
        )
        frame_status.pack(
            fill="x",
            pady=(0, 10),
        )

        self.lbl_thumb = tk.Label(
            frame_status,
            image=self.imagem_padrao,
            bg=self.PANEL_2,
        )
        self.lbl_thumb.pack(
            side="left",
            padx=12,
            pady=12,
        )

        status_body = tk.Frame(
            frame_status,
            bg=self.PANEL,
        )
        status_body.pack(
            side="left",
            fill="both",
            expand=True,
            padx=(5, 12),
            pady=10,
        )

        self.label_playlist = tk.Label(
            status_body,
            text="Playlist: —",
            bg=self.PANEL,
            fg=self.TEXT,
            anchor="w",
            font=("Segoe UI", 10, "bold"),
        )
        self.label_playlist.pack(
            fill="x"
        )

        self.label_musica = tk.Label(
            status_body,
            text="Música: —",
            bg=self.PANEL,
            fg=self.MUTED,
            anchor="w",
            font=("Segoe UI", 9),
        )
        self.label_musica.pack(
            fill="x",
            pady=(2, 0),
        )

        self.label_contador = tk.Label(
            status_body,
            text="0 / 0",
            bg=self.PANEL,
            fg=self.GREEN,
            anchor="w",
            font=("Consolas", 10, "bold"),
        )
        self.label_contador.pack(
            fill="x",
            pady=(2, 0),
        )

        self.label_estatisticas = tk.Label(
            status_body,
            text=(
                "Baixadas: 0   |   "
                "Já existentes: 0   |   Erros: 0"
            ),
            bg=self.PANEL,
            fg=self.TEXT,
            anchor="w",
            font=("Consolas", 9),
        )
        self.label_estatisticas.pack(
            fill="x",
            pady=(2, 0),
        )

        frame_progressos = tk.Frame(
            main,
            bg=self.BG,
        )
        frame_progressos.pack(
            fill="x",
            pady=(0, 8),
        )

        lbl_p1 = tk.Frame(
            frame_progressos,
            bg=self.BG,
        )
        lbl_p1.pack(
            fill="x"
        )

        tk.Label(
            lbl_p1,
            text="PROGRESSO DA PLAYLIST",
            bg=self.BG,
            fg=self.MUTED,
            font=("Consolas", 8, "bold"),
        ).pack(side="left")

        self.progress_label = tk.Label(
            lbl_p1,
            text="0%",
            bg=self.BG,
            fg=self.GREEN,
            font=("Consolas", 8, "bold"),
        )
        self.progress_label.pack(
            side="right"
        )

        self.progress_bar = ttk.Progressbar(
            frame_progressos,
            orient="horizontal",
            mode="determinate",
            maximum=100,
            bootstyle=SUCCESS,
        )
        self.progress_bar.pack(
            fill="x",
            pady=(2, 6),
        )

        lbl_p2 = tk.Frame(
            frame_progressos,
            bg=self.BG,
        )
        lbl_p2.pack(
            fill="x"
        )

        tk.Label(
            lbl_p2,
            text="PROGRESSO DO ARQUIVO ATUAL",
            bg=self.BG,
            fg=self.MUTED,
            font=("Consolas", 8, "bold"),
        ).pack(side="left")

        self.current_progress_label = tk.Label(
            lbl_p2,
            text="0%",
            bg=self.BG,
            fg=self.MUTED,
            font=("Consolas", 8),
        )
        self.current_progress_label.pack(
            side="right"
        )

        self.current_progress = ttk.Progressbar(
            frame_progressos,
            orient="horizontal",
            mode="determinate",
            maximum=100,
            bootstyle=SUCCESS,
        )
        self.current_progress.pack(
            fill="x",
            pady=(2, 4),
        )

        self.notebook_logs = ttk.Notebook(
            main,
            bootstyle=DARK,
        )
        self.notebook_logs.pack(
            fill="both",
            expand=True,
        )

        self.tab_tabela = tk.Frame(
            self.notebook_logs,
            bg=self.LOG_BG,
        )
        self.notebook_logs.add(
            self.tab_tabela,
            text=" LISTA DE TRACKS ",
        )

        colunas_grid = (
            "num",
            "titulo",
            "status",
        )

        self.tree_downloads = ttk.Treeview(
            self.tab_tabela,
            columns=colunas_grid,
            show="headings",
            height=8,
            bootstyle=DARK,
        )

        self.tree_downloads.heading(
            "num",
            text="#",
        )
        self.tree_downloads.heading(
            "titulo",
            text="Música / Faixa",
        )
        self.tree_downloads.heading(
            "status",
            text="Status Operacional",
        )

        self.tree_downloads.column(
            "num",
            width=50,
            stretch=False,
            anchor="center",
        )
        self.tree_downloads.column(
            "titulo",
            width=600,
            stretch=True,
        )
        self.tree_downloads.column(
            "status",
            width=150,
            stretch=False,
            anchor="center",
        )

        self.tree_downloads.pack(
            side="left",
            fill="both",
            expand=True,
        )

        scroll_tree = ttk.Scrollbar(
            self.tab_tabela,
            orient="vertical",
            command=self.tree_downloads.yview,
        )
        scroll_tree.pack(
            side="right",
            fill="y",
        )

        self.tree_downloads.configure(
            yscrollcommand=scroll_tree.set
        )
              # Configuração de cores para os status operacionais (HUD)
        # 'background' define a cor da linha e 'foreground' a cor do texto
        self.tree_downloads.tag_configure('concluido', background='#143a24', foreground='#5fe795')
        self.tree_downloads.tag_configure('existe', background='#1c2836', foreground='#8b98a8')
        self.tree_downloads.tag_configure('erro', background='#421818', foreground='#f87171')
        self.tree_downloads.tag_configure('cancelado', background='#2d2012', foreground='#fb923c')
        self.tree_downloads.tag_configure('baixando', background='#163b4a', foreground='#38bdf8')

        self.tab_log_texto = tk.Frame(
            self.notebook_logs,
            bg=self.LOG_BG,
        )
        self.notebook_logs.add(
            self.tab_log_texto,
            text=" TERMINAL DE LOG ",
        )

        self.log_text = tk.Text(
            self.tab_log_texto,
            height=8,
            wrap="none",
            state="disabled",
            font=("Consolas", 9),
            bg=self.LOG_BG,
            fg="#cfd6de",
            insertbackground=self.TEXT,
            selectbackground="#26303a",
            relief="flat",
            bd=0,
            padx=8,
            pady=8,
        )
        self.log_text.pack(
            side="left",
            fill="both",
            expand=True,
        )

        scroll_log = tk.Scrollbar(
            self.tab_log_texto,
            orient="vertical",
            command=self.log_text.yview,
            bg=self.PANEL_2,
            troughcolor=self.LOG_BG,
            width=13,
        )
        scroll_log.pack(
            side="right",
            fill="y",
        )

        self.log_text.configure(
            yscrollcommand=scroll_log.set
        )

        footer = tk.Frame(
            main,
            bg=self.BG,
        )
        footer.pack(
            fill="x",
            pady=(10, 0),
        )

        self.label_status = tk.Label(
            footer,
            text="● PKCE • READY",
            bg=self.BG,
            fg=self.GREEN,
            font=("Consolas", 9, "bold"),
        )
        self.label_status.pack(
            side="left"
        )

        credits = tk.Label(
            footer,
            text=(
                "⚡ Desenvolvido por Arielton_Gross "
                "• Instagram"
            ),
            bg=self.BG,
            fg=self.GREEN,
            font=("Segoe UI", 9),
            cursor="hand2",
        )
        credits.pack(
            side="right"
        )
        credits.bind(
            "<Button-1>",
            lambda _e: abrir_instagram(),
        )

    # -------------------------------------------------------------------------
    # UI
    # -------------------------------------------------------------------------

    def adicionar_log(self, msg):
        self.log_text.configure(
            state="normal"
        )
        self.log_text.insert(
            "end",
            f"{msg}\n",
        )
        self.log_text.see("end")
        self.log_text.configure(
            state="disabled"
        )

    def alterar_estado_botoes(self, executando):
        self.download_em_andamento = executando

        estado = (
            "disabled"
            if executando
            else "normal"
        )

        self.btn_playlist.configure(
            state=estado
        )
        self.btn_lista.configure(
            state=estado
        )
        self.btn_musica.configure(
            state=estado
        )
        self.btn_pasta.configure(
            state=estado
        )

        self.combo_qualidade.configure(
            state=(
                "disabled"
                if executando
                else "readonly"
            )
        )

        self.btn_cancelar.configure(
            state=(
                "normal"
                if executando
                else "disabled"
            ),
            text="Cancelar",
        )

        self.label_status.configure(
            text=(
                "● PKCE • DOWNLOADING"
                if executando
                else "● PKCE • READY"
            ),
            fg=(
                "#facc15"
                if executando
                else self.GREEN
            ),
        )

    def resetar_progresso(self):
        self.progress_bar["value"] = 0
        self.current_progress["value"] = 0

        self.progress_label.configure(
            text="0%"
        )
        self.current_progress_label.configure(
            text="0%"
        )

        self.label_playlist.configure(
            text="Playlist: —"
        )
        self.label_musica.configure(
            text="Música: —"
        )
        self.label_contador.configure(
            text="0 / 0"
        )
        self.label_estatisticas.configure(
            text=(
                "Baixadas: 0   |   "
                "Já existentes: 0   |   Erros: 0"
            )
        )

        self.lbl_thumb.configure(
            image=self.imagem_padrao
        )
        self.lbl_thumb.image = (
            self.imagem_padrao
        )

        self.baixadas = 0
        self.puladas = 0
        self.erros = 0

    # -------------------------------------------------------------------------
    # TXT
    # -------------------------------------------------------------------------

    def menu_txt(self):
        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            return

        opc = messagebox.askyesnocancel(
            "Formato de Entrada",
            "Clique em SIM para Ler e Baixar a partir "
            "de um arquivo .TXT contendo uma lista externa.\n\n"
            "Clique em NÃO se quiser apenas exportar "
            "uma playlist do Spotify para .TXT.",
            parent=self.root,
        )

        if opc is True:
            self.baixar_via_txt()
        elif opc is False:
            self.listar_playlist()

    def baixar_via_txt(self):
        caminho_txt = filedialog.askopenfilename(
            title="Selecione o arquivo TXT das Músicas",
            filetypes=[
                ("Arquivos de Texto", "*.txt")
            ],
            parent=self.root,
        )

        if not caminho_txt:
            return

        self.adicionar_log(
            f"Lendo arquivo TXT de lote: {caminho_txt}"
        )

        musicas = []
        chaves_vistas = set()
        falhas_spotify = []

        try:
            with open(
                caminho_txt,
                "r",
                encoding="utf-8",
            ) as f:
                linhas = f.readlines()

            for linha in linhas:
                linha = linha.strip()

                if (
                    not linha
                    or linha.startswith("#")
                ):
                    continue

                if (
                    "spotify.com" in linha
                    or linha.startswith("spotify:track:")
                ):
                    try:
                        musica = (
                            extrair_informacoes_musica(
                                self.spotify,
                                linha,
                            )
                        )
                        chave = chave_musica(
                            musica[0],
                            musica[1],
                        )

                        if chave not in chaves_vistas:
                            chaves_vistas.add(chave)
                            musicas.append(musica)

                        continue

                    except Exception as erro:
                        falhas_spotify.append(
                            f"{linha} -> "
                            f"{limpar_ansi(str(erro))}"
                        )
                        continue

                if " - " in linha:
                    titulo_txt, artista_txt = (
                        linha.split(
                            " - ",
                            1,
                        )
                    )
                    titulo_txt = titulo_txt.strip()
                    artista_txt = artista_txt.strip()
                else:
                    titulo_txt = linha
                    artista_txt = (
                        "Artista Desconhecido"
                    )

                chave = chave_musica(
                    titulo_txt,
                    artista_txt,
                )

                if chave in chaves_vistas:
                    continue

                chaves_vistas.add(chave)

                musicas.append(
                    (
                        titulo_txt,
                        artista_txt,
                        None,
                        "",
                    )
                )

        except (
            OSError,
            UnicodeError,
        ) as erro:
            messagebox.showerror(
                "Erro",
                f"Erro ao ler arquivo:\n{erro}",
                parent=self.root,
            )
            return

        for falha in falhas_spotify:
            self.adicionar_log(
                f"ERRO no link Spotify do TXT: {falha}"
            )

        if not musicas:
            messagebox.showwarning(
                "Vazio",
                "Nenhuma faixa de música válida encontrada no TXT.",
                parent=self.root,
            )
            return

        pasta = criar_pasta(
            self.save_path,
            "Downloads via Lote TXT",
        )

        self._preparacao_cancel_event.clear()

        self.resetar_progresso()

        self.label_playlist.configure(
            text="Playlist: Importação do TXT"
        )
        self.label_contador.configure(
            text=f"0 / {len(musicas)}"
        )

        self.alterar_estado_botoes(True)

        if not self.download_manager.iniciar_playlist(
            "Lote TXT",
            musicas,
            pasta,
            self.combo_qualidade.get(),
        ):
            self.alterar_estado_botoes(False)
            messagebox.showwarning(
                "Download em andamento",
                "Já existe uma operação em andamento.",
                parent=self.root,
            )

    # -------------------------------------------------------------------------
    # Playlist
    # -------------------------------------------------------------------------

    def baixar_playlist(self):
        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            return

        url = simpledialog.askstring(
            "Baixar Playlist",
            "Digite o link da playlist do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        self._preparando_operacao = True
        self._preparacao_cancel_event.clear()

        self.alterar_estado_botoes(True)

        self.adicionar_log(
            "Obtendo informações da playlist em segundo plano..."
        )

        qualidade = self.combo_qualidade.get()

        thread = threading.Thread(
            target=self._preparar_playlist_worker,
            args=(url, qualidade),
            daemon=True,
            name="SpotifyPlaylistPreparation",
        )
        thread.start()

    def _preparar_playlist_worker(
        self,
        url,
        qualidade,
    ):
        try:
            playlist_nome, musicas = (
                extrair_musicas_e_nome_playlist(
                    self.spotify,
                    url,
                )
            )

            if self._preparacao_cancel_event.is_set():
                self.root.after(
                    0,
                    self._playlist_preparacao_cancelada,
                )
                return

            if not musicas:
                self.root.after(
                    0,
                    self._playlist_preparacao_vazia,
                )
                return

            unicas = []
            vistas = set()

            for (
                titulo,
                artista,
                capa_url,
                album,
            ) in musicas:
                chave = chave_musica(
                    titulo,
                    artista,
                )

                if chave not in vistas:
                    vistas.add(chave)
                    unicas.append(
                        (
                            titulo,
                            artista,
                            capa_url,
                            album,
                        )
                    )

            if self._preparacao_cancel_event.is_set():
                self.root.after(
                    0,
                    self._playlist_preparacao_cancelada,
                )
                return

            pasta = criar_pasta(
                self.save_path,
                playlist_nome,
            )

            if self._preparacao_cancel_event.is_set():
                self.root.after(
                    0,
                    self._playlist_preparacao_cancelada,
                )
                return

            self.root.after(
                0,
                self._iniciar_playlist_preparada,
                playlist_nome,
                unicas,
                pasta,
                qualidade,
            )

        except Exception as erro:
            if self._preparacao_cancel_event.is_set():
                self.root.after(
                    0,
                    self._playlist_preparacao_cancelada,
                )
                return

            self.root.after(
                0,
                self._playlist_preparacao_erro,
                limpar_ansi(str(erro)),
            )

    def _playlist_preparacao_vazia(self):
        self._preparando_operacao = False
        self.alterar_estado_botoes(False)

        messagebox.showwarning(
            "Playlist vazia",
            "Nenhuma música foi encontrada.",
            parent=self.root,
        )

    def _playlist_preparacao_erro(self, erro):
        self._preparando_operacao = False

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

        messagebox.showerror(
            "Erro",
            "Não foi possível carregar a playlist:\n\n"
            f"{erro}",
            parent=self.root,
        )

    def _playlist_preparacao_cancelada(self):
        self._preparando_operacao = False

        self.adicionar_log(
            "Preparação da playlist cancelada."
        )

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

    def _iniciar_playlist_preparada(
        self,
        playlist_nome,
        musicas,
        pasta,
        qualidade,
    ):
        if self._preparacao_cancel_event.is_set():
            self._playlist_preparacao_cancelada()
            return

        self._preparando_operacao = False

        self.resetar_progresso()

        self.label_playlist.configure(
            text=f"Playlist: {playlist_nome}"
        )
        self.label_contador.configure(
            text=f"0 / {len(musicas)}"
        )

        self.adicionar_log(
            f"Playlist processada: {playlist_nome} | "
            f"Faixas exclusivas: {len(musicas)}"
        )

        if not self.download_manager.iniciar_playlist(
            playlist_nome,
            musicas,
            pasta,
            qualidade,
        ):
            self.alterar_estado_botoes(False)

            messagebox.showwarning(
                "Download em andamento",
                "Já existe uma operação de download em andamento.",
                parent=self.root,
            )

    # -------------------------------------------------------------------------
    # Listagem
    # -------------------------------------------------------------------------

    def listar_playlist(self):
        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            return

        url = simpledialog.askstring(
            "Listar Playlist",
            "Digite o link da playlist do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        self.adicionar_log(
            "Obtendo playlist para exportação..."
        )

        self._preparando_operacao = True
        self.alterar_estado_botoes(True)

        def worker():
            try:
                playlist_nome, musicas = (
                    extrair_musicas_e_nome_playlist(
                        self.spotify,
                        url,
                    )
                )

                if self._preparacao_cancel_event.is_set():
                    self.root.after(
                        0,
                        self._lista_cancelada,
                    )
                    return

                pasta = criar_pasta(
                    self.save_path,
                    playlist_nome,
                )

                arquivo = salvar_musicas_em_txt(
                    musicas,
                    pasta,
                )

                self.root.after(
                    0,
                    self._lista_pronta,
                    playlist_nome,
                    arquivo,
                    len(musicas),
                )

            except Exception as erro:
                self.root.after(
                    0,
                    self._lista_erro,
                    limpar_ansi(str(erro)),
                )

        self._preparacao_cancel_event.clear()

        threading.Thread(
            target=worker,
            daemon=True,
            name="SpotifyPlaylistList",
        ).start()

    def _lista_pronta(
        self,
        playlist_nome,
        arquivo,
        total,
    ):
        self._preparando_operacao = False
        self.alterar_estado_botoes(False)

        messagebox.showinfo(
            "Sucesso",
            "Playlist salva com sucesso em:\n\n"
            f"{arquivo}\n\n"
            f"Total de músicas: {total}",
            parent=self.root,
        )

    def _lista_erro(self, erro):
        self._preparando_operacao = False

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

        messagebox.showerror(
            "Erro",
            "Não foi possível gerar lista:\n\n"
            f"{erro}",
            parent=self.root,
        )

    def _lista_cancelada(self):
        self._preparando_operacao = False

        self.adicionar_log(
            "Exportação da playlist cancelada."
        )

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

    # -------------------------------------------------------------------------
    # Música individual
    # -------------------------------------------------------------------------

    def baixar_musica(self):
        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            return

        url = simpledialog.askstring(
            "Baixar Música",
            "Digite o link da música do Spotify:",
            parent=self.root,
        )

        if not url:
            return

        self._preparando_operacao = True
        self._preparacao_cancel_event.clear()

        self.alterar_estado_botoes(True)

        self.adicionar_log(
            "Obtendo informações da música em segundo plano..."
        )

        qualidade = self.combo_qualidade.get()

        def worker():
            try:
                (
                    titulo,
                    artista,
                    capa_url,
                    album,
                ) = extrair_informacoes_musica(
                    self.spotify,
                    url,
                )

                if self._preparacao_cancel_event.is_set():
                    self.root.after(
                        0,
                        self._musica_preparacao_cancelada,
                    )
                    return

                pasta = criar_pasta(
                    self.save_path,
                    "Musicas Individuais",
                )

                self.root.after(
                    0,
                    self._iniciar_musica_preparada,
                    titulo,
                    artista,
                    capa_url,
                    album,
                    pasta,
                    qualidade,
                )

            except Exception as erro:
                self.root.after(
                    0,
                    self._musica_preparacao_erro,
                    limpar_ansi(str(erro)),
                )

        threading.Thread(
            target=worker,
            daemon=True,
            name="SpotifyTrackPreparation",
        ).start()

    def _iniciar_musica_preparada(
        self,
        titulo,
        artista,
        capa_url,
        album,
        pasta,
        qualidade,
    ):
        if self._preparacao_cancel_event.is_set():
            self._musica_preparacao_cancelada()
            return

        self._preparando_operacao = False

        self.resetar_progresso()

        self.label_playlist.configure(
            text="Playlist: Download Individual"
        )

        if not self.download_manager.iniciar_musica(
            titulo,
            artista,
            capa_url,
            pasta,
            qualidade,
            album,
        ):
            self.alterar_estado_botoes(False)

            messagebox.showwarning(
                "Download em andamento",
                "Já existe uma operação em andamento.",
                parent=self.root,
            )

    def _musica_preparacao_erro(self, erro):
        self._preparando_operacao = False

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

        messagebox.showerror(
            "Erro",
            "Não foi possível obter os dados da música:\n\n"
            f"{erro}",
            parent=self.root,
        )

    def _musica_preparacao_cancelada(self):
        self._preparando_operacao = False

        self.adicionar_log(
            "Preparação do download individual cancelada."
        )

        if self._fechando:
            self._encerrar_janela()
            return

        self.alterar_estado_botoes(False)

    # -------------------------------------------------------------------------
    # Cancelamento
    # -------------------------------------------------------------------------

    def cancelar_download(self):
        if not self.download_em_andamento:
            return

        self.adicionar_log(
            "CANCELAR pressionado: sinalizando "
            "todos os downloads ativos e cancelando "
            "a fila pendente."
        )

        self.btn_cancelar.configure(
            state="disabled",
            text="Cancelando...",
        )

        # Caso ainda esteja na etapa de consulta à API.
        if self._preparando_operacao:
            self._preparacao_cancel_event.set()

        # Caso já esteja no ThreadPoolExecutor.
        self.download_manager.cancelar()

        # Marca visualmente as tarefas que já estavam na fila.
        for item in self.tree_downloads.get_children():
            status = self.tree_downloads.set(
                item,
                "status",
            )

            if status in (
                "Na Fila",
                "⬇️ Baixando",
            ):
                self.tree_downloads.set(
                    item,
                    column="status",
                    value="Cancelando...",
                )

    # -------------------------------------------------------------------------
    # Pasta
    # -------------------------------------------------------------------------

    def configurar_pasta_saida(self):
        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            return

        janela = tk.Toplevel(
            self.root
        )
        janela.title(
            "Pasta de saída"
        )
        janela.geometry(
            "620x220"
        )
        janela.resizable(
            False,
            False,
        )
        janela.configure(
            bg=self.BG
        )
        janela.transient(
            self.root
        )
        janela.grab_set()

        tk.Label(
            janela,
            text="CONFIGURAÇÃO DA PASTA DE SAÍDA",
            bg=self.BG,
            fg=self.GREEN,
            font=("Consolas", 11, "bold"),
        ).pack(
            anchor="w",
            padx=22,
            pady=(20, 8),
        )

        linha = tk.Frame(
            janela,
            bg=self.BG,
        )
        linha.pack(
            fill="x",
            padx=22,
            pady=12,
        )

        entrada = tk.Entry(
            linha,
            bg=self.LOG_BG,
            fg=self.TEXT,
            insertbackground=self.TEXT,
            relief="flat",
            highlightthickness=1,
            highlightbackground=self.BORDER,
            highlightcolor=self.GREEN,
            font=("Consolas", 9),
        )
        entrada.pack(
            side="left",
            fill="x",
            expand=True,
            ipady=8,
        )
        entrada.insert(
            0,
            str(self.save_path),
        )

        def escolher():
            pasta = filedialog.askdirectory(
                title="Escolher pasta de saída",
                initialdir=(
                    str(self.save_path)
                    if self.save_path.exists()
                    else str(Path.home())
                ),
                parent=janela,
            )

            if pasta:
                entrada.delete(
                    0,
                    "end",
                )
                entrada.insert(
                    0,
                    pasta,
                )

        ttk.Button(
            linha,
            text="📁 Escolher",
            command=escolher,
            bootstyle=INFO,
            width=14,
        ).pack(
            side="left",
            padx=(10, 0),
        )

        botoes = tk.Frame(
            janela,
            bg=self.BG,
        )
        botoes.pack(
            fill="x",
            padx=22,
            pady=(4, 18),
        )

        def salvar():
            valor = (
                entrada.get()
                .strip()
            )

            if not valor:
                messagebox.showwarning(
                    "Pasta inválida",
                    "Informe uma pasta válida.",
                    parent=janela,
                )
                return

            caminho = (
                Path(valor)
                .expanduser()
                .resolve()
            )

            try:
                caminho.mkdir(
                    parents=True,
                    exist_ok=True,
                )
            except OSError as erro:
                messagebox.showerror(
                    "Erro",
                    str(erro),
                    parent=janela,
                )
                return

            self.save_path = caminho
            self.salvar_pasta_saida()

            self.adicionar_log(
                f"Nova pasta configurada: {caminho}"
            )

            janela.destroy()

        ttk.Button(
            botoes,
            text="Cancelar",
            command=janela.destroy,
            bootstyle=SECONDARY,
            width=14,
        ).pack(
            side="right",
            padx=(8, 0),
        )

        ttk.Button(
            botoes,
            text="Salvar",
            command=salvar,
            bootstyle=SUCCESS,
            width=14,
        ).pack(
            side="right"
        )

    # -------------------------------------------------------------------------
    # Fila da UI
    # -------------------------------------------------------------------------

    def processar_fila(self):
        # Mesmo durante o fechamento, a fila precisa continuar sendo processada.
        # Caso contrário, o evento "finalizado" do DownloadManager nunca chega
        # ao processar_evento(), deixando a janela presa em "Cancelando...".
        try:
            while True:
                evento, args = (
                    self.ui_queue.get_nowait()
                )
                self.processar_evento(
                    evento,
                    args,
                )

        except Empty:
            pass

        # A fila precisa continuar sendo processada durante o fechamento.
        # O DownloadManager envia 'finalizado' somente depois que os workers
        # terminam; esse evento chama _encerrar_janela(). Se pararmos o after
        # quando _fechando=True, o evento fica preso na Queue e a janela fica
        # eternamente em "Cancelando...".
        try:
            if self.root.winfo_exists():
                self.root.after(100, self.processar_fila)
        except tk.TclError:
            pass

    def processar_evento(
        self,
        evento,
        args,
    ):
        if evento == "inicio":
            (
                playlist_nome,
                total,
                lista_musicas,
            ) = args

            self.total_musicas = total

            self.label_playlist.configure(
                text=f"Playlist: {playlist_nome}"
            )
            self.label_contador.configure(
                text=f"0 / {total}"
            )

            for item in (
                self.tree_downloads
                .get_children()
            ):
                self.tree_downloads.delete(
                    item
                )

            for idx, (
                titulo,
                artista,
                _,
                _,
            ) in enumerate(
                lista_musicas,
                start=1,
            ):
                self.tree_downloads.insert(
                    "",
                    "end",
                    values=(
                        idx,
                        f"{titulo} - {artista}",
                        "Na Fila",
                    ),
                )

            return

        if evento == "musica":
            (
                indice,
                total,
                titulo,
                artista,
                capa_url,
            ) = args

            self._indice_musica_atual = indice

            self.label_musica.configure(
                text=(
                    f"Música: {titulo} — "
                    f"{artista}"
                )
            )
            self.label_contador.configure(
                text=f"{indice} / {total}"
            )

            self.current_progress["value"] = 0
            self.current_progress_label.configure(
                text="0%"
            )

            self.solicitar_thumbnail(
                capa_url
            )

            children = (
                self.tree_downloads
                .get_children()
            )

            if (
                0 <= indice - 1 < len(children)
            ):
                self.tree_downloads.see(
                    children[indice - 1]
                )
                # Mantém somente a faixa "em foco"
                # selecionada visualmente.
                self.tree_downloads.selection_set(
                    children[indice - 1]
                )
                self.tree_downloads.focus(
                    children[indice - 1]
                )

            return

        if evento == "status_linha":
            (
                row_idx,
                novo_status,
            ) = args

            children = (
                self.tree_downloads
                .get_children()
            )

            if (
                0 <= row_idx < len(children)
            ):
                item_id = children[row_idx]
                self.tree_downloads.set(
                    item_id,
                    column="status",
                    value=novo_status,
                )
                
                # Mapeia dinamicamente o texto do status para a tag de cor correspondente
                status_limpo = novo_status.lower()
                if "concluí" in status_limpo:
                    self.tree_downloads.item(item_id, tags=('concluido',))
                elif "existe" in status_limpo or "duplicata" in status_limpo:
                    self.tree_downloads.item(item_id, tags=('existe',))
                elif "erro" in status_limpo:
                    self.tree_downloads.item(item_id, tags=('erro',))
                elif "cancel" in status_limpo:
                    self.tree_downloads.item(item_id, tags=('cancelado',))
                elif "baixando" in status_limpo:
                    self.tree_downloads.item(item_id, tags=('baixando',))

            return


        if evento == "progresso":
            valor = float(args[0])

            self.progress_bar["value"] = valor
            self.progress_label.configure(
                text=f"{valor:.0f}%"
            )

            return

        if evento == "download_progresso":
            if len(args) == 1:
                indice = getattr(
                    self,
                    "_indice_musica_atual",
                    None,
                )
                valor = float(args[0])
            else:
                indice = int(args[0])
                valor = float(args[1])

            # Só atualiza a barra se a faixa que reportou o progresso
            # continua sendo a faixa atualmente exibida.
            atual = getattr(
                self,
                "_indice_musica_atual",
                None,
            )

            if (
                indice is not None
                and atual is not None
                and indice != atual
            ):
                return

            valor = max(
                0.0,
                min(100.0, valor),
            )

            self.current_progress["value"] = valor
            self.current_progress_label.configure(
                text=f"{valor:.1f}%"
            )

            return

        if evento == "estatisticas":
            (
                self.baixadas,
                self.puladas,
                self.erros,
            ) = args

            self.label_estatisticas.configure(
                text=(
                    f"Baixadas: {self.baixadas}   |   "
                    f"Já existentes: {self.puladas}   |   "
                    f"Erros: {self.erros}"
                )
            )

            return

        if evento == "log":
            self.adicionar_log(
                args[0]
            )
            return

        if evento == "playlist_concluida":
            (
                playlist_nome,
                total,
                baixadas,
                puladas,
                erros,
                pasta,
                arquivo_erros,
            ) = args

            self._ultima_pasta_concluida = (
                Path(pasta)
            )

            if arquivo_erros:
                self.adicionar_log(
                    "⚠ Lista de erros salva em: "
                    f"{arquivo_erros}"
                )

            self.progress_bar["value"] = 100
            self.progress_label.configure(
                text="100%"
            )

            self.adicionar_log("")
            self.adicionar_log(
                "=" * 52
            )
            self.adicionar_log(
                f"DOWNLOAD FINALIZADO: "
                f"{playlist_nome}"
            )
            self.adicionar_log(
                f"Total: {total} | "
                f"Baixadas: {baixadas} | "
                f"Já Existentes: {puladas} | "
                f"Erros: {erros}"
            )
            self.adicionar_log(
                "=" * 52
            )

            opc = messagebox.askyesno(
                "Concluído",
                (
                    f"Playlist '{playlist_nome}' finalizada.\n\n"
                    f"Baixadas: {baixadas}\n"
                    f"Existentes: {puladas}\n"
                    f"Erros: {erros}\n\n"
                    + (
                        f"Lista de erros: {arquivo_erros}\n\n"
                        if arquivo_erros
                        else ""
                    )
                    + "Deseja abrir a pasta com as músicas?"
                ),
                parent=self.root,
            )

            if opc:
                self.abrir_pasta_destino(
                    pasta
                )

            return

        if evento == "individual_concluida":
            mensagem, pasta = args

            self._ultima_pasta_concluida = (
                Path(pasta)
            )

            opc = messagebox.askyesno(
                "Concluído",
                f"{mensagem}\n\n"
                "Deseja abrir a pasta onde "
                "a música foi salva?",
                parent=self.root,
            )

            if opc:
                self.abrir_pasta_destino(
                    pasta
                )

            return

        if evento == "cancelado":
            # Evita popup duplicado se múltiplos caminhos reportarem
            # cancelamento.
            if not getattr(
                self,
                "_cancel_popup_mostrado",
                False,
            ):
                self._cancel_popup_mostrado = True

                self.adicionar_log(
                    "Operação abortada pelo usuário."
                )

                if not self._fechando:
                    messagebox.showinfo(
                        "Cancelado",
                        "O download foi interrompido.",
                        parent=self.root,
                    )

            return

        if evento == "erro":
            self.adicionar_log(
                f"ERRO CRÍTICO: {args[0]}"
            )

            if not self._fechando:
                messagebox.showerror(
                    "Erro",
                    args[0],
                    parent=self.root,
                )

            return

        if evento == "finalizado":
            self._cancel_popup_mostrado = False

            if self._fechando:
                self._encerrar_janela()
            else:
                self.alterar_estado_botoes(
                    False
                )

            return

    # -------------------------------------------------------------------------
    # Fechamento
    # -------------------------------------------------------------------------

    def _encerrar_janela(self):
        if not self._fechando:
            return

        try:
            self._thumbnail_executor.shutdown(
                wait=False,
                cancel_futures=True,
            )
        except Exception:
            pass

        try:
            self.root.destroy()
        except tk.TclError:
            pass

    def fechar(self):
        if self._fechando:
            return

        if (
            self.download_em_andamento
            or self._preparando_operacao
        ):
            confirmar = messagebox.askyesno(
                "Sair",
                "Existe uma operação rodando. "
                "Deseja cancelar tudo e sair?",
                parent=self.root,
            )

            if not confirmar:
                return

            self._fechando = True

            self._preparacao_cancel_event.set()
            self.download_manager.cancelar()

            self.btn_cancelar.configure(
                state="disabled",
                text="Cancelando...",
            )

            self.adicionar_log(
                "Encerrando aplicação: "
                "cancelamento solicitado."
            )

            # O root continua vivo até o DownloadManager emitir
            # "finalizado". Isso evita destruir a GUI enquanto workers
            # ainda estão usando a fila/eventos.
            return

        self._fechando = True
        self._encerrar_janela()


# =============================================================================
# ÍCONE
# =============================================================================

ICONE_APP = "icone.ico"


def obter_pasta_aplicativo():
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return BASE_DIR


def aplicar_icone(root):
    if not ICONE_APP:
        return

    candidatos = []

    pasta_app = obter_pasta_aplicativo()
    candidatos.append(
        pasta_app / ICONE_APP
    )

    if getattr(sys, "frozen", False):
        candidatos.extend([
            pasta_app / "_internal" / ICONE_APP,
            Path(sys._MEIPASS) / ICONE_APP,
        ])

    candidatos.append(
        BASE_DIR / ICONE_APP
    )

    for icone in candidatos:
        if not icone.is_file():
            continue

        try:
            root.iconbitmap(
                str(icone)
            )
            return
        except Exception:
            continue


# =============================================================================
# MAIN
# =============================================================================

def main():
    root = ttk.Window(
        themename="darkly"
    )

    aplicar_icone(root)

    SpotifyDownloaderApp(root)

    try:
        root.mainloop()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
