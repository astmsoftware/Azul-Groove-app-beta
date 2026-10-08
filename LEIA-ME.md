# Azul Groove — app para Windows (WebView2)

Abre https://azul-groove.vercel.app dentro de uma janela própria, sem barra de endereço.
Atualizou o site na Vercel? O app já mostra a versão nova, sem gerar outro instalador.

## Três formas de entregar o app
| Versão | Arquivo | Para quem |
|---|---|---|
| **Instalador** | `AzulGroove-Setup.exe` | Quem quer instalar (Menu Iniciar, atalho, desinstalar pelo Windows) |
| **Portátil** | `AzulGroove-Portable.exe` | Quem quer só abrir, sem instalar |
| **Microsoft Store (MSIX)** | `AzulGroove_1.0.0.0_x64.msix` | Publicação na loja |

Para gerar as duas primeiras de uma vez: dê dois cliques em **build-all.bat**. Os arquivos saem na pasta `dist`.
Precisa do **.NET 8 SDK** e do **Inno Setup 6.6+** só no seu PC. Quem for usar não precisa instalar nada do .NET.
Sem o Inno Setup, o `build-all.bat` ainda gera a versão portátil.

Para só a versão portátil: `build.bat` (sai em `publish\AzulGroove.exe`). Para testar sem gerar: `dotnet run`.

## Como gerar o instalador (Setup.exe)
1. Instale o **Inno Setup 6.6 ou mais novo** (grátis): https://jrsoftware.org/isdl.php
2. Dê dois cliques em **build-installer.bat** (ele gera o app e depois o instalador).
3. Envie **somente** `installer\AzulGroove-Setup.exe`.

O instalador: instala sem pedir senha de administrador (só para o usuário), cria atalho no Menu Iniciar,
atalho opcional na área de trabalho, aparece em *Configurações → Aplicativos* para desinstalar,
fala português ou inglês conforme o Windows, e segue o tema claro/escuro do Windows.

## MSIX (Microsoft Store ou instalação moderna)
Precisa do **.NET 8 SDK** e do **Windows SDK** (traz o `makeappx.exe`): https://developer.microsoft.com/windows/downloads/windows-sdk/

**Testar no seu PC:** dê dois cliques em `build-msix.bat`. Ele gera `msix\out\AzulGroove_1.0.0.0_x64.msix` assinado com um certificado de teste
e mostra os 2 comandos (PowerShell como administrador) para confiar no certificado e instalar.

**Enviar para a Store:**
1. Crie a conta em https://storedeveloper.microsoft.com e reserve o nome "Azul Groove".
2. No Partner Center, abra *Identidade do produto* e copie **Package/Identity/Name**, **Publisher** (começa com `CN=`) e **PublisherDisplayName**.
3. No PowerShell, dentro desta pasta:
   `.\build-msix.ps1 -IdentityName "O_NAME_COPIADO" -Publisher "CN=O_PUBLISHER_COPIADO" -PublisherDisplayName "O_NOME_COPIADO"`
4. Envie `msix\out\AzulGroove_1.0.0.0_x64.msix` no Partner Center (sem assinar — a Store assina).
5. Na ficha da loja use as imagens de `msix\Assets` (`StoreListing_300x300.png` e `StoreListing_1080x1080.png`), mais capturas de tela e a política de privacidade.
A cada versão nova, aumente `-Version` (ex.: `1.0.1.0`).

No MSIX da Store o app **não** baixa o WebView2 sozinho (a Store não gosta disso). O Windows 11 e quase todos os Windows 10 já têm.
Os ícones em `msix\Assets` são provisórios — troque pelos seus se quiser.

### Visual e animação do instalador
- O painel da esquerda usa as imagens de `installer-assets` (degradê azul-roxo com o nome do app), em vários tamanhos para telas com zoom.
- Nas telas de **boas-vindas** e de **conclusão**, barras de equalizador dançam no "visor" do painel (código em `installer-animation.iss`).
- Na tela **Instalando**, aparece uma faixa animada (barras dançando, "Instalando o Azul Groove..." com pontinhos e a porcentagem real) com uma barra de progresso que enche conforme instala.
- Se der erro de compilação nessa animação, apague a linha `#include "installer-animation.iss"` no fim do `AzulGroove.iss`: o instalador continua igual, só sem o movimento.

## Em outros PCs
- Windows 10/11 de 64 bits. Não precisa instalar o .NET.
- O app usa o **WebView2 Runtime** (já vem no Windows 11 e na maioria dos Windows 10).
  Se faltar, o próprio app baixa e instala sozinho na primeira abertura (precisa de internet).
- Na primeira abertura o Windows SmartScreen pode avisar "editor desconhecido" porque o .exe não é assinado:
  clique em *Mais informações → Executar assim mesmo*.

## Login
- **Discord, Google e GitHub** funcionam dentro do app.
- No Discord Developer Portal, o redirect continua `https://azul-groove.vercel.app/chat`.
- Login do Google/GitHub: confirme no Firebase (Authentication → Settings → Authorized domains) que `azul-groove.vercel.app` está na lista.
- Links externos (Top.gg, etc.) abrem no navegador padrão.

## Animação de abertura
Ao abrir o app, aparece uma animação (logo com barras de equalizador, brilho e barra de carregamento) por ~2,5 s
e depois entra no site. Ela segue o tema claro/escuro do Windows. Para mudar o tempo, edite `Task.Delay(2300)` em `MainForm.cs`.

## Atalho de teclado para abrir o chat
- Padrão: **Ctrl + Alt + A** — funciona de qualquer lugar do Windows, mesmo com o app minimizado ou na bandeja.
  Ele traz a janela para a frente e abre o chat.
- O app fica com um ícone na **bandeja** (perto do relógio). Clique com o botão direito para:
  abrir o chat ou o início, ligar/desligar o atalho, escolher a tecla (Ctrl+Alt+A, Ctrl+Alt+Espaço ou Ctrl+Shift+A)
  e escolher se o X da janela manda o app para a bandeja ou fecha de vez. Duplo clique no ícone abre o chat.
- Abrir o .exe de novo (ou o atalho do Menu Iniciar) também traz o app já aberto para a frente.
- As escolhas ficam salvas em `%LocalAppData%\AzulGroove\settings.json`.
- Se outro programa já usa a combinação, o app avisa e você escolhe outra no menu da bandeja.

## Tema claro/escuro (menu da bandeja)
No ícone da bandeja → **Tema (claro / escuro)**: *Automático (segue o Windows)*, *Claro* ou *Escuro*.
A escolha vale para a janela e para o chat do site (e fica salva). O início e o assistente só têm visual escuro por enquanto.

### Como a janela escolhe o tema
A barra de título e o fundo da janela seguem o tema do site (o chat tem modo claro/escuro/automático).
Antes da página carregar, e nas páginas só escuras (início e assistente), seguem o tema do Windows.

## Personalizar
- Endereço do site: `HomeUrl` em `MainForm.cs`.
- Ícone: troque o `app.ico` (use um .ico com vários tamanhos, até 256x256).
- Dados do app (login, configurações): `%LocalAppData%\AzulGroove` — apagar essa pasta desloga.
