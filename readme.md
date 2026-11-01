# SpotiFetch 🎧

**SpotiFetch** é um aplicativo desktop para Windows que organiza downloads de áudio a partir de links e metadados do Spotify. O projeto é a evolução do **Spotify Playlist Downloader (SPD)**, originalmente desenvolvido em Python e posteriormente reconstruído em C# com WPF.

O repositório mantém as duas gerações: o SpotiFetch é o projeto principal e o código original do SPD está preservado em [`legacy/`](./legacy-SPD-python/).

> **Status:** SpotiFetch é a versão atual em desenvolvimento. A versão Python está arquivada para preservar a história e a evolução do projeto.

---

## ✨ SpotiFetch — versão atual

O SpotiFetch utiliza informações do Spotify para identificar faixas e o `yt-dlp` para localizar e obter áudio de fontes disponíveis na internet. Ele **não extrai áudio dos servidores do Spotify**.

### Recursos

- Abre links de faixas, álbuns e playlists públicas do Spotify.
- Pesquisa músicas por nome e artista.
- Importa listas de músicas por arquivos TXT.
- Organiza tarefas por playlist e exibe progresso, atividade e logs.
- Permite configurar a qualidade do áudio e a pasta de saída.
- Permite ignorar arquivos que já existem e configurar até 32 downloads simultâneos.
- Inclui um player integrado para ouvir os arquivos concluídos.
- Utiliza FFmpeg para processamento de áudio.
- Salva as preferências do usuário em `%LOCALAPPDATA%\SpotiFetch`.

### 🖥️ Compilar no Windows

**Requisitos:**
- Windows x64.
- Conexão com a internet.
- PowerShell.

O menu de build pode instalar um SDK .NET 10 local e baixar as dependências necessárias, incluindo `yt-dlp` e FFmpeg.

1. Clone ou baixe este repositório.
2. Abra a pasta raiz do projeto no PowerShell ou no Explorador de Arquivos.
3. Execute `build.bat` ou `build.ps1`.
4. Escolha uma das opções disponíveis:

| Opção | Resultado |
|---|---|
| **1 — EXE único** | Publica o aplicativo em um executável .NET autocontido. `ffmpeg.exe` e `yt-dlp.exe` ficam ao lado dele. |
| **2 — Pasta completa** | Publica os arquivos do aplicativo separadamente, com FFmpeg e yt-dlp ao lado. |
| **3 — Baixar dependências** | Prepara o SDK .NET 10, FFmpeg e yt-dlp. Depois, execute o build novamente e escolha 1 ou 2. |

O resultado da compilação fica em `dist`. Se a proteção de pastas do Windows impedir a escrita dentro do repositório, o script informa o problema e utiliza `Downloads\SpotiFetch\dist`.

As dependências do build e os arquivos temporários ficam fora do repositório, em `%LOCALAPPDATA%\SpotiFetch\Build`.

### Observações

- O SpotiFetch é um projeto independente, sem afiliação ou endosso do Spotify, YouTube, dos mantenedores do `yt-dlp` ou do FFmpeg.
- A leitura de playlists depende dos metadados públicos disponibilizados pelo Spotify e pode exigir ajustes caso o serviço altere sua página ou interface.
- A disponibilidade, o acesso e a qualidade das faixas dependem das fontes externas e podem mudar.
- Utilize o aplicativo somente com conteúdo que você tem direito de acessar e baixar, respeitando os termos dos serviços e a legislação aplicável.
- O Windows pode exibir avisos de editor desconhecido para executáveis não assinados. A identidade “SpotiFetch” exibida nos controles de mídia do Windows não equivale a uma assinatura digital de editor.

### Tecnologias

- C#
- WPF
- .NET 10
- `yt-dlp`
- FFmpeg

---

## 🕰️ SPD — projeto original em Python

O **Spotify Playlist Downloader (SPD)** é a primeira geração do projeto e a base da ideia que posteriormente evoluiu para o SpotiFetch.

Desenvolvido em Python, o SPD permitia converter e baixar músicas ou playlists identificadas a partir do Spotify para MP3, com bitrate selecionável de até 320 kbps.

A partir da versão 3.3, o projeto passou por uma reestruturação para utilizar concorrência *multi-threading*, interface gráfica escura, logs em tempo real, barras de progresso e incorporação automática de metadados e capas.

O código-fonte original está preservado em [`/legacy-SPD-python/`](./legacy-SPD-python/). Esta seção documenta a geração histórica; suas instruções e dependências não se aplicam ao SpotiFetch atual.

### 🖼️ Linha do tempo e evolução visual

#### 📦 O começo — versão V2s

A primeira geração contava com uma interface minimalista em coluna única, baseada nos componentes padrão do Tkinter, sem suporte a logs detalhados ou barras de progresso avançadas.

<p align="center">
  <img src="./legacy-SPD-python/screenshots/v2s.png" width="45%" alt="Interface minimalista da versão V2s" />
</p>

#### ❌ Desafios de API — versões V3 iniciais

Durante a transição para a arquitetura V3, o motor de requisições enfrentou instabilidades de autenticação, problemas de paginação e restrições de escopo de usuários, incluindo respostas HTTP 403 e erros de servidor.

<p align="center">
  <img src="./legacy-SPD-python/screenshots/erro_403.png" width="32%" alt="Erro de escopo HTTP 403" />
  <img src="./legacy-SPD-python/screenshots/erro_autenticacao.png" width="32%" alt="Erro de autenticação" />
  <img src="./legacy-SPD-python/screenshots/erro_vazio.png" width="32%" alt="Erro de playlist vazia" />
</p>

#### 🟢 Estabilização e modo escuro — versões V3.1 e V3.2

Implementação de um fluxo estável de paginação de faixas e introdução das primeiras variantes de interface escura (*Dark HUD*), inicialmente focadas no monitoramento por caixas de texto.

<p align="center">
  <img src="./legacy-SPD-python/screenshots/v3_funcionando.png" width="48%" alt="Versão V3 estável" />
  <img src="./legacy-SPD-python/screenshots/3.2.png" width="48%" alt="Interface HUD da versão V3.2" />
</p>

#### 🔵 Evolução multimídia — versões V3.3 e V3.3.6 MT

Evolução dos seletores visuais e construção de um painel multimídia com tabelas assíncronas e renderização de capas.

<p align="center">
  <img src="./legacy-SPD-python/screenshots/3.3.1.png" width="48%" alt="Interface multimídia da versão V3.3" />
  <img src="./legacy-SPD-python/screenshots/3.3.6MT.png" width="48%" alt="Versão V3.3.6 com multi-threading" />
</p>

#### ⚡ Comparativo entre gerações

<p align="center">
  <img src="./legacy-SPD-python/screenshots/comparativo.png" width="80%" alt="Comparativo visual entre gerações do SPD" />
</p>

### Recursos da versão V3.3.7

- **Arquitetura multi-threaded:** downloads de até 12 músicas simultaneamente.
- **Proteção de credenciais:** chaves de API e Client IDs protegidos por uma camada de ofuscação em tempo de execução.
- **Cancelamento cooperativo:** interrupção dos processos `yt-dlp` e dos workers ativos por meio de hooks de progresso, com limpeza de arquivos temporários `.part` e `.ytdl`.
- **Metadados com Mutagen:** inserção de tags ID3 nos arquivos MP3, incluindo título, artista, álbum e capa.
- **Deduplicação:** verificação de faixas repetidas nas listas e de arquivos locais, com normalização de nomes e remoção de sufixos redundantes do Windows, como `(1)` e `(2)`.
- **Importação e exportação em lote:** leitura de arquivos `.txt` com links ou nomes de músicas e exportação de playlists para texto.

### Como executar o SPD (versão arquivada)

> As instruções abaixo são referentes somente à versão Python antiga, não ao SpotiFetch atual.

**Pré-requisitos:**
- Python 3.10 ou superior.
- FFmpeg disponível no `PATH` do sistema ou em uma pasta `ffmpeg/` na raiz do projeto legado.

Na pasta do código legado, instale as dependências:

```bash
pip install -r requirements.txt
```

Depois, execute a versão correspondente:

```bash
python V3.3.7.py
```

### Gerar o executável do SPD

Para gerar o executável da versão Python no Windows, com a interface `ttkbootstrap` e o FFmpeg incluídos, o comando histórico utilizado era:

```powershell
Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue

pyinstaller --noconfirm --clean --windowed --icon="icone.ico" --add-data "icone.ico;." --collect-all ttkbootstrap --add-binary "ffmpeg.exe;ffmpeg" --name "Spotify Playlist Downloader" "V3.3.7.py"

pyinstaller --noconfirm --clean --log-level=INFO "Spotify Playlist Downloader.spec"
```

### Histórico de versões do SPD

#### 🟢 V3.3.7

- **Camada de ofuscação:** armazenamento do Client ID por codificação Base64, com decodificação em memória.
- **Sincronização de progresso:** aplicação de travas de concorrência (`futures_lock`) para corrigir atualizações visuais inconsistentes entre workers.
- **Fechamento cooperativo:** bloqueio que impede o encerramento prematuro da interface enquanto processos em segundo plano são interrompidos.

#### 🟡 V3.3.6 e V3.3.5 MT

- **Cancelamento real:** controle responsivo que altera o botão para “Cancelando...” e aguarda a finalização dos processos.
- **Limpeza de arquivos temporários:** remoção de fragmentos parciais de downloads cancelados.
- **Tratamento de exceções:** substituição de blocos `except:` genéricos para expor mensagens reais de erros de rede.

#### 🔵 V3.3.3 e V3.3

- **Transição multi-thread:** introdução de `ThreadPoolExecutor` e `as_completed`, isolando consultas à API do Spotify da linha principal da interface gráfica.
- **Interface HUD dinâmica:** substituição de textos simples por uma tabela `Treeview` para acompanhar estados individuais, como “Na Fila”, “Baixando” e “Concluído”.
- **Exibição de mídia:** renderização de capas das músicas (*thumbnails*) com Pillow.
- **Suporte a links:** compatibilidade com URLs internacionais e regionalizadas do Spotify, como `/intl-pt/track/ID`.
- **Persistência:** armazenamento automático das preferências de diretório em `settings.json`.

#### ⚪ V3.1 e V3.2

- Autenticação descentralizada pelo fluxo Spotify PKCE.
- Paginação estruturada para processar playlists com mais de 100 faixas.
- Implementação inicial de barras de progresso verdes e sanitização de strings para o padrão do Windows.

---

## 📜 Licença

Este repositório utiliza a licença MIT. Consulte o arquivo [`LICENSE`](./LICENSE) para ver o texto completo.

O código legado do SPD e a versão atual do SpotiFetch fazem parte da evolução do mesmo projeto. Componentes e ferramentas de terceiros, como .NET, `yt-dlp` e FFmpeg, mantêm suas próprias licenças e condições de uso.

## 👤 Créditos

Criado e desenvolvido por [Arielx](https://github.com/arieltx).

💬 **Discord:** [xeidisauu.](https://discord.com/users/1553862715443781657)


O SpotiFetch atual foi desenvolvido com C#, WPF e .NET, com apoio de ferramentas de IA durante o processo de implementação e depuração. A concepção, direção do projeto, decisões de desenvolvimento, integração e testes são conduzidos pelo autor.
