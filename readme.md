# instalando o FFmpeg
## passo 1: instale o FFmpeg
Acesse o [BtbN/FFmpeg-Builds/releases](https://github.com/BtbN/FFmpeg-Builds/releases)
instale a versão para seu dispositivo
## passo 2: mudando o diretorio no codigo
1. copie o diretorio onde foi instalado o ffmpeg
2. na linha 74 **'ffmpeg_location': r'C:\Users\Home\Desktop\Spotify-Playlist-Downloader\ffmpeg-\bin',** Substitua/atualize o caminho do seu FFmpeg

# Configurando o arquivo `config.env`

Para que o projeto funcione corretamente, você precisa configurar suas credenciais do Spotify. Siga os passos abaixo para obter as credenciais e criar o arquivo `config.env`:

**DICA** TRADUZA A PAGINA

## Passo 1: Criar um aplicativo no Spotify Developers
1. Acesse o [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
2. Faça login com sua conta do Spotify.
3. Clique em **Create an App**.
4. Preencha os campos com:
   - Nome: Escolha um nome para o seu aplicativo.
   - Descrição: Uma breve descrição do que o app faz.
   - Redirecionar URIs: http://localhost:8080/callback
5. Clique em **Create**.
6. Após criar o aplicativo, copie os valores de:
   - **Client ID**
   - **Client Secret** **NOTA** ESTE VALOR FICA OCULTO! 

## Passo 2: Criar o arquivo `config.env`
1. Na raiz do projeto, crie um arquivo chamado `config.env`.
2. Cole os seguintes dados no arquivo e substitua os valores placeholders pelas suas credenciais:
**NOTA** existe um arquivo exemplo, remova a linha com @@@ e substitua os valores

3. Salve o arquivo.

## Passo 3: Verificar
Certifique-se de que o arquivo `config.env` está salvo na pasta raiz do projeto e que o conteúdo está correto.

**Nota:** Nunca compartilhe suas credenciais publicamente!

## Passo 4: Executar o programa
Agora você pode executar o programa normalmente! As credenciais serão carregadas automaticamente do arquivo `config.env`.
