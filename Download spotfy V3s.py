#███████╗██████╗  ██████╗ ████████╗██╗███████╗██╗   ██╗     ██████╗ ██╗      █████╗ ██╗   ██╗██╗     ██╗███████╗████████╗   ██████╗  ██████╗ ██╗    ██╗███╗   ██╗██╗      ██████╗  █████╗ ██████╗ ███████╗██████╗ 
#██╔════╝██╔══██╗██╔═══██╗╚══██╔══╝██║██╔════╝╚██╗ ██╔╝     ██╔══██╗██║     ██╔══██╗╚██╗ ██╔╝██║     ██║██╔════╝╚══██╔══╝   ██╔══██╗██╔═══██╗██║    ██║████╗  ██║██║     ██╔═══██╗██╔══██╗██╔══██╗██╔════╝██╔══██╗
#███████╗██████╔╝██║   ██║   ██║   ██║█████╗   ╚████╔╝█████╗██████╔╝██║     ███████║ ╚████╔╝ ██║     ██║███████╗   ██║█████╗██║  ██║██║   ██║██║ █╗ ██║██╔██╗ ██║██║     ██║   ██║███████║██║  ██║█████╗  ██████╔╝
#╚════██║██╔═══╝ ██║   ██║   ██║   ██║██╔══╝    ╚██╔╝ ╚════╝██╔═══╝ ██║     ██╔══██║  ╚██╔╝  ██║     ██║╚════██║   ██║╚════╝██║  ██║██║   ██║██║███╗██║██║╚██╗██║██║     ██║   ██║██╔══██║██║  ██║██╔══╝  ██╔══██╗
#███████║██║     ╚██████╔╝   ██║   ██║██║        ██║        ██║     ███████╗██║  ██║   ██║   ███████╗██║███████║   ██║      ██████╔╝╚██████╔╝╚███╔███╔╝██║ ╚████║███████╗╚██████╔╝██║  ██║██████╔╝███████╗██║  ██║
#╚══════╝╚═╝      ╚═════╝    ╚═╝   ╚═╝╚═╝        ╚═╝        ╚═╝     ╚══════╝╚═╝  ╚═╝   ╚═╝   ╚══════╝╚═╝╚══════╝   ╚═╝      ╚═════╝  ╚═════╝  ╚══╝╚══╝ ╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚══════╝╚═╝  ╚═╝
                                                                                                                                                                                                                                                                                                                                                                                                                         
# crie um arquivo chamado "config.env", e coloque suas credenciais.
#atualize a linha 74! com o seu diretorio do ffmpeg
import os
import requests
import spotipy
from spotipy.oauth2 import SpotifyClientCredentials
import yt_dlp
from dotenv import load_dotenv
#================================SEGURANÇA======================================================
# Carregar as variáveis de ambiente do arquivo .env
load_dotenv("config.env")  # carrega o arquivo com suas credenciais "config.env"   
def configurar_spotify():
    client_id = os.getenv("SPOTIPY_CLIENT_ID")
    client_secret = os.getenv("SPOTIPY_CLIENT_SECRET")
    
    if not client_id or not client_secret:
        raise ValueError("Faltando credenciais do Spotify no arquivo .env")
    
    auth_manager = SpotifyClientCredentials(client_id=client_id, client_secret=client_secret)
    return spotipy.Spotify(auth_manager=auth_manager)

# O restante do seu código segue normal

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
    if not os.path.exists(nome_pasta):
        os.makedirs(nome_pasta)
    return nome_pasta

def salvar_musicas_em_txt(musicas, nome_pasta="Musicas"):
    arquivo_txt = os.path.join(nome_pasta, "musicas.txt")
    with open(arquivo_txt, "w", encoding="utf-8") as f:
        for titulo, artista in musicas:
            f.write(f"{titulo} - {artista}\n")
    print(f"Músicas salvas em: {arquivo_txt}")

def baixar_musica(titulo, artista, pasta_destino):
    query = f"{titulo} {artista} lyrics"
    mp3_path = os.path.join(pasta_destino, f"{titulo} - {artista}.mp3")

    # Opções do yt_dlp
    options = {
        'format': 'bestaudio/best',
        'postprocessors': [{
            'key': 'FFmpegExtractAudio',
            'preferredcodec': 'mp3',
            'preferredquality': '192',
        }],
        'outtmpl': mp3_path,
        'quiet': True,
        'ffmpeg_location': r'C:\Users\Home\Desktop\Spotify-Playlist-Downloader\ffmpeg-\bin',  # Substitua/atualize o caminho do seu FFmpeg
    }

    try:
        with yt_dlp.YoutubeDL(options) as ydl:
            ydl.download([f"ytsearch:{query}"])
        print(f"Música baixada: {titulo} - {artista}")
    except Exception as e:
        print(f"Erro ao baixar música: {e}")

def menu():
    spotify = configurar_spotify()
    while True:
        print("\n ====================== AVISOS! =============================================")
        print("1. para cancelar o download aperte ctrl+c juntos")
        print("2. os arquivos são baixados duplicados e juntados depois")
        print("3. ao cancelar o download, a ultima musica pode sofrer com alterações!")
        print("\n   O DOWNLOAD É FEITO EM .MP3, MAS PODE SER ALTERADO NO CODIGO")
        print("\n=============== Menu ====================")
        print("1. Fazer download da playlist")
        print("2. Listar músicas em .txt")
        print("3. Fazer download de uma única música")
        print("4. Sair")
        opcao = input("Escolha uma opção: ")

        if opcao == "1":
            playlist_url = input("Digite o link da playlist do Spotify: ")
            playlist_nome, musicas = extrair_musicas_e_nome_playlist(spotify, playlist_url)
            pasta_destino = criar_pasta(playlist_nome)
            for titulo, artista in musicas:
                baixar_musica(titulo, artista, pasta_destino)

        elif opcao == "2":
            playlist_url = input("Digite o link da playlist do Spotify: ")
            playlist_nome, musicas = extrair_musicas_e_nome_playlist(spotify, playlist_url)
            criar_pasta(playlist_nome)
            salvar_musicas_em_txt(musicas, playlist_nome)

        elif opcao == "3":
            musica_url = input("Digite o link da música do Spotify: ")
            titulo, artista = extrair_informacoes_musica(spotify, musica_url)
            pasta_destino = criar_pasta("Musicas Individuais")
            baixar_musica(titulo, artista, pasta_destino)

        elif opcao == "4":
            print("Saindo... Até mais!")
            break

        else:
            print("Opção inválida. Tente novamente.")

if __name__ == "__main__":
    menu()