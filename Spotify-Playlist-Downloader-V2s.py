#███████╗██████╗  ██████╗ ████████╗██╗███████╗██╗   ██╗     ██████╗ ██╗      █████╗ ██╗   ██╗██╗     ██╗███████╗████████╗   ██████╗  ██████╗ ██╗    ██╗███╗   ██╗██╗      ██████╗  █████╗ ██████╗ ███████╗██████╗ 
#██╔════╝██╔══██╗██╔═══██╗╚══██╔══╝██║██╔════╝╚██╗ ██╔╝     ██╔══██╗██║     ██╔══██╗╚██╗ ██╔╝██║     ██║██╔════╝╚══██╔══╝   ██╔══██╗██╔═══██╗██║    ██║████╗  ██║██║     ██╔═══██╗██╔══██╗██╔══██╗██╔════╝██╔══██╗
#███████╗██████╔╝██║   ██║   ██║   ██║█████╗   ╚████╔╝█████╗██████╔╝██║     ███████║ ╚████╔╝ ██║     ██║███████╗   ██║█████╗██║  ██║██║   ██║██║ █╗ ██║██╔██╗ ██║██║     ██║   ██║███████║██║  ██║█████╗  ██████╔╝
#╚════██║██╔═══╝ ██║   ██║   ██║   ██║██╔══╝    ╚██╔╝ ╚════╝██╔═══╝ ██║     ██╔══██║  ╚██╔╝  ██║     ██║╚════██║   ██║╚════╝██║  ██║██║   ██║██║███╗██║██║╚██╗██║██║     ██║   ██║██╔══██║██║  ██║██╔══╝  ██╔══██╗
#███████║██║     ╚██████╔╝   ██║   ██║██║        ██║        ██║     ███████╗██║  ██║   ██║   ███████╗██║███████║   ██║      ██████╔╝╚██████╔╝╚███╔███╔╝██║ ╚████║███████╗╚██████╔╝██║  ██║██████╔╝███████╗██║  ██║
#╚══════╝╚═╝      ╚═════╝    ╚═╝   ╚═╝╚═╝        ╚═╝        ╚═╝     ╚══════╝╚═╝  ╚═╝   ╚═╝   ╚══════╝╚═╝╚══════╝   ╚═╝      ╚═════╝  ╚═════╝  ╚══╝╚══╝ ╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚══════╝╚═╝  ╚═╝
#                                                                                                                                                                                                              
# ================================ BIBLIOTECAS ==============================
import os
import requests
import spotipy
from spotipy.oauth2 import SpotifyClientCredentials
import yt_dlp
from dotenv import load_dotenv
import tkinter as tk
from tkinter import messagebox, simpledialog
from tkinter.ttk import Progressbar
from pathlib import Path
import threading
import tkinter as tk
# ================================ SEGURANÇA ==============================

# Obtendo o diretório base do script
base_dir = Path(__file__).resolve().parent
dotenv_path = base_dir / "config.env" if os.path.exists(base_dir / "config.env") else Path.cwd() / "config.env"
load_dotenv(dotenv_path)  # Carregar variáveis de ambiente do arquivo .env

def configurar_spotify():
    client_id = os.getenv("SPOTIPY_CLIENT_ID")
    client_secret = os.getenv("SPOTIPY_CLIENT_SECRET")

    if not client_id or not client_secret:
        raise ValueError("Faltando credenciais do Spotify no arquivo .env")

    auth_manager = SpotifyClientCredentials(client_id=client_id, client_secret=client_secret)
    return spotipy.Spotify(auth_manager=auth_manager)

# ========================== FFmpeg ==================================================

def obter_caminho_ffmpeg():
    ffmpeg_path = os.getenv("FFMPEG_PATH")
    if not ffmpeg_path or not os.path.exists(ffmpeg_path):
        raise FileNotFoundError("Caminho do FFmpeg inválido ou não encontrado no arquivo .env")
    return ffmpeg_path

# ============================Local Save ==============================================

save_path = os.getenv("SAVE_PATH", str(Path.cwd()))  # Diretório atual como padrão
if not os.path.exists(save_path):
    os.makedirs(save_path, exist_ok=True)

# ============================ Funções Principais =====================================

def extrair_informacoes_musica(spotify, musica_url):
    track_id = musica_url.split("/")[-1].split("?")[0]
    track = spotify.track(track_id)
    titulo = track["name"]
    artista = track["artists"][0]["name"]
    return titulo, artista

def extrair_musicas_e_nome_playlist(spotify, playlist_url):
    playlist_id = playlist_url.split("/")[-1].split("?")[0]
    playlist = spotify.playlist(playlist_id)
    playlist_nome = playlist["name"]
    musicas = [
        (item["track"]["name"], item["track"]["artists"][0]["name"])
        for item in playlist["tracks"]["items"]
    ]
    return playlist_nome, musicas

def criar_pasta(nome_pasta):
    pasta_path = Path(save_path) / nome_pasta
    pasta_path.mkdir(parents=True, exist_ok=True)
    return pasta_path

def salvar_musicas_em_txt(musicas, nome_pasta="Musicas"):
    arquivo_txt = os.path.join(nome_pasta, "musicas.txt")
    with open(arquivo_txt, "w", encoding="utf-8") as f:
        for titulo, artista in musicas:
            f.write(f"{titulo} - {artista}\n")
    return arquivo_txt

def baixar_musica(titulo, artista, pasta_destino):
    query = f"{titulo} {artista} lyrics"
    mp3_path = os.path.join(pasta_destino, f"{titulo} - {artista}.mp3")
    ffmpeg_path = obter_caminho_ffmpeg()
    options = {
        'format': 'bestaudio/best',
        'postprocessors': [{
            'key': 'FFmpegExtractAudio',
            'preferredcodec': 'mp3',
            'preferredquality': '192',
        }],
        'outtmpl': mp3_path,
        'quiet': True,
        'ffmpeg_location': ffmpeg_path
    }

    try:
        with yt_dlp.YoutubeDL(options) as ydl:
            ydl.download([f"ytsearch:{query}"])
        return f"Música baixada: {titulo} - {artista}"
    except Exception as e:
        return f"Erro ao baixar música: {e}"

# ========================== GUI ============================================

# Variável global para controle de cancelamento
cancelar_download = False

def baixar_playlist_gui():
    global cancelar_download
    playlist_url = simpledialog.askstring("Download Playlist", "Digite o link da playlist do Spotify:")
    if playlist_url:
        # Resetando o status de cancelamento
        cancelar_download = False
        
        # Criando janela para progresso
        progress_label.config(text="Progresso: 0%")
        progress_bar["value"] = 0
        progress_bar["maximum"] = 100
        root.update_idletasks()

        def download():
            global cancelar_download
            try:
                playlist_nome, musicas = extrair_musicas_e_nome_playlist(spotify, playlist_url)
                pasta_destino = criar_pasta(playlist_nome)
                total_musicas = len(musicas)

                for index, (titulo, artista) in enumerate(musicas, start=1):
                    if cancelar_download:
                        messagebox.showinfo("Cancelado", "O download foi cancelado.")
                        break
                    resultado = baixar_musica(titulo, artista, pasta_destino)
                    print(resultado)
                    progress_bar["value"] = int((index / total_musicas) * 100)
                    progress_label.config(text=f"Progresso: {int((index / total_musicas) * 100)}%")
                    root.update_idletasks()

                if not cancelar_download:
                    messagebox.showinfo("Sucesso", f"Playlist '{playlist_nome}' baixada com sucesso!")
            except Exception as e:
                messagebox.showerror("Erro", f"Não foi possível baixar a playlist.\n{e}")

        # Criar um thread para executar o download
        threading.Thread(target=download).start()

def listar_playlist_gui():
    playlist_url = simpledialog.askstring("Listar Playlist", "Digite o link da playlist do Spotify:")
    if playlist_url:
        try:
            playlist_nome, musicas = extrair_musicas_e_nome_playlist(spotify, playlist_url)
            pasta_destino = criar_pasta(playlist_nome)
            arquivo_txt = salvar_musicas_em_txt(musicas, pasta_destino)
            messagebox.showinfo("Sucesso", f"As músicas foram listadas em: {arquivo_txt}")
        except Exception as e:
            messagebox.showerror("Erro", f"Não foi possível listar as músicas.\n{e}")

def baixar_musica_gui():
    musica_url = simpledialog.askstring("Download Música", "Digite o link da música do Spotify:")
    if musica_url:
        try:
            titulo, artista = extrair_informacoes_musica(spotify, musica_url)
            pasta_destino = criar_pasta("Musicas Individuais")
            resultado = baixar_musica(titulo, artista, pasta_destino)
            messagebox.showinfo("Resultado", resultado)
        except Exception as e:
            messagebox.showerror("Erro", f"Não foi possível baixar a música.\n{e}")

def cancelar_download_gui():
    global cancelar_download
    cancelar_download = True

# ========================== Configuração do Spotify =======================
try:
    spotify = configurar_spotify()
except Exception as e:
    tk.messagebox.showerror("Erro de configuração", f"Erro ao configurar o Spotify.\n{e}")
    exit()

# ========================== GUI Principal =================================

root = tk.Tk()
root.title("Spotify-Playlist-Downloader-V2s")
root.geometry("325x280")

# Elementos principais da janela
tk.Label(root, text="Spotify Playlist Downloader", font=("Arial", 17)).pack(pady=10)
tk.Button(root, text="Baixar Playlist", command=baixar_playlist_gui, width=25).pack(pady=5)
tk.Button(root, text="Listar Playlist em .txt", command=listar_playlist_gui, width=25).pack(pady=5)
tk.Button(root, text="Baixar Música", command=baixar_musica_gui, width=25).pack(pady=5)
tk.Button(root, text="Cancelar", command=cancelar_download_gui, width=25).pack(pady=5)

# Barra de progresso e rótulo de status na janela principal
progress_bar = Progressbar(root, orient="horizontal", length=300, mode="determinate")
progress_bar.pack(pady=10)
progress_label = tk.Label(root, text="Progresso: 0%", font=("Arial", 10))
progress_label.pack(pady=5)

# ========================== Iniciar GUI ===================================

def start_gui():
    root.mainloop()

if __name__ == "__main__":
    start_gui()
