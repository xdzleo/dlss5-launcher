; ============================================================================
;  RenoDX Launcher - instalador (Inno Setup 6.x)
;
;  Compila com:
;     ISCC.exe installer\RenoDXLauncher.iss /DSourceDir=..\publish /DAppVersion=1.11.2
;  ou, mais simples, com tools\build-installer.ps1 (que publica antes).
;
;  Por que instalador e nao mais so o zip portable: um .exe solto, sem assinatura,
;  extraido de um zip e rodado de Downloads e o pior caso possivel para heuristica
;  de antivirus e para o SmartScreen. Instalar em Program Files por um instalador
;  Inno Setup - formato que todo motor de AV ve milhares de vezes por dia em software
;  legitimo - troca esse caminho por um que ja tem reputacao. Ver docs\antivirus.md
;  para o que isso resolve e o que so a assinatura resolve.
; ============================================================================

#define AppName       "DLSS 5 Launcher"
#define AppExeName    "RenoDXLauncher.exe"
#define AppPublisher  "xdzleo"
#define AppURL        "https://github.com/xdzleo/renodx-launcher"
#define AppCopyright  "Copyright (c) 2026 xdzleo. MIT License."

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppExePath SourceDir + "\" + AppExeName

; Falha na hora de compilar (e nao na hora de instalar) se o publish nao existe.
#if !FileExists(AppExePath)
  #error Nao achei o publish em SourceDir. Rode: dotnet publish src\RenoDXLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#endif

; VersionInfo* do PE tem que ser numerico. Vem sempre do binario ja compilado, entao
; a versao do setup.exe nunca diverge da versao do app que ele carrega dentro.
#define FileVer GetVersionNumbersString(AppExePath)

; Versao de exibicao: a tag do release quando o CI passa /DAppVersion, senao a do binario.
#ifndef AppVersion
  #define AppVersion FileVer
#endif

[Setup]
; AppId identifica o produto entre versoes - nao mude, ou o upgrade vira instalacao
; paralela e o Programas e Recursos passa a listar duas entradas.
AppId={{9F2C1E7A-4B63-4E51-9C0D-6D2A8F1B7E44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright={#AppCopyright}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\LICENSE

OutputDir={#OutputDir}
OutputBaseFilename=RenoDXLauncher-{#AppVersion}-setup
SetupIconFile=..\src\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern

Compression=lzma2/max
SolidCompression=yes

; x64compatible cobre x64 e ARM64 rodando x64 por emulacao (Inno 6.3+).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Padrao: Program Files para todos (local que AV e SmartScreen tratam como confiavel).
; O usuario sem admin, ou que prefira nao ver UAC, escolhe "so para mim" no primeiro
; dialogo e cai em %LocalAppData%\Programs.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline

; Atualizar por cima com o launcher aberto: o Restart Manager fecha o app em vez de
; falhar com "arquivo em uso". RestartApplications=no porque reabrir sozinho, elevado,
; faria o app gravar config no %LocalAppData% do admin.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

SetupMutex=RenoDXLauncherSetup,Global\RenoDXLauncherSetup

; Metadata do PE do proprio setup.exe. Instalador com VersionInfo em branco e um dos
; sinais mais baratos que motor de heuristica pontua - todo campo aqui e preenchido.
VersionInfoVersion={#FileVer}
VersionInfoProductVersion={#FileVer}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} - instalador
VersionInfoCopyright={#AppCopyright}
VersionInfoOriginalFileName=RenoDXLauncher-{#AppVersion}-setup.exe

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; O publish e self-contained (o .NET vai junto), entao e a pasta inteira, recursiva.
; O .pdb fica de fora: ele nao serve para nada em maquina de usuario, e um arquivo a
; mais sem assinatura na pasta instalada. Ele vai anexado ao GitHub Release, que e
; onde alguem triando um crash vai busca-lo.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; runasoriginaluser e obrigatorio: sem ele o app abriria com o token elevado do setup e
; gravaria config.json, cache de capas e log no %LocalAppData% do administrador, nao no
; do usuario - e na proxima abertura normal tudo apareceria vazio.
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// Desinstalar remove o programa. Os dados do usuario (perfil de nits, exes fixados,
// pastas manuais, cache) ficam - quem desinstala para reinstalar nao quer perder isso.
// So apaga se a pessoa pedir, e nunca em desinstalacao silenciosa.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if UninstallSilent then
    Exit;

  DataDir := ExpandConstant('{localappdata}\RenoDXLauncher');
  if not DirExists(DataDir) then
    Exit;

  if MsgBox('Remover também suas configurações do RenoDX Launcher?' + #13#10 + #13#10 +
            'Isso apaga o perfil de brilho do monitor, os executáveis fixados por jogo,' + #13#10 +
            'as pastas adicionadas à mão e o cache de capas:' + #13#10 +
            DataDir + #13#10 + #13#10 +
            'Escolha Não se você pretende reinstalar depois.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
