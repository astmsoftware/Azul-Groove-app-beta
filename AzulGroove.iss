; Instalador do Azul Groove (Inno Setup 6.6 ou mais novo)
; Gera: installer\AzulGroove-Setup.exe

; Modo "um clique" (estilo Chrome): sem etapas, instala sozinho e abre o app.
; Para voltar ao assistente normal, apague a linha abaixo.
#define OneClick

#define AppName "Azul Groove"
#define AppVersion "1.0.0"
#define AppPublisher "ASTM Software"
#define AppExe "AzulGroove.exe"

[Setup]
AppId={{7B2E6D3A-5C1F-4E8B-9A41-0A2B6C0DE001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://azul-groove.vercel.app
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=app.ico
OutputDir=installer
OutputBaseFilename=AzulGroove-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Instala só para o usuário atual (não pede senha de administrador); o usuário pode escolher "para todos"
PrivilegesRequired=lowest
#ifndef OneClick
PrivilegesRequiredOverridesAllowed=dialog
#endif
; Visual novo, claro ou escuro conforme o Windows
WizardStyle=modern dynamic
; Imagens do assistente (varios tamanhos, para telas com zoom 100% a 250%)
WizardImageFile=installer-assets\wizard-164x314.bmp,installer-assets\wizard-192x386.bmp,installer-assets\wizard-246x459.bmp,installer-assets\wizard-273x556.bmp,installer-assets\wizard-328x604.bmp,installer-assets\wizard-355x632.bmp,installer-assets\wizard-410x797.bmp
WizardSmallImageFile=installer-assets\small-55x55.bmp,installer-assets\small-64x68.bmp,installer-assets\small-83x80.bmp,installer-assets\small-92x97.bmp,installer-assets\small-110x106.bmp,installer-assets\small-119x123.bmp,installer-assets\small-138x140.bmp
DisableProgramGroupPage=yes
CloseApplications=yes
#ifdef OneClick
ShowLanguageDialog=no
DisableStartupPrompt=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
#endif

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

#ifndef OneClick
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
#endif

[Files]
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
#ifdef OneClick
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"
#else
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
#endif

[Run]
#ifdef OneClick
Filename: "{app}\{#AppExe}"; Flags: nowait skipifsilent runasoriginaluser
#else
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
#endif

; Animação (barras de equalizador na tela de boas-vindas e de conclusão)
#include "installer-animation.iss"
