; SP-0092: the per-user setup executable published beside the portable archive.
;
; This script compiles a staging tree that has already been built elsewhere - the release workflow's
; stage/StreamsPlayer, or tools/build-installer.ps1's local equivalent. It never invokes dotnet, so the
; archive and the installer always carry byte-identical payloads from one publish.
;
; Required defines:
;   /DVersion=26.0820.1828      the release version, house stamp YY.MMDD.HHmm
;   /DSourceDir=<absolute path> the staging tree to package
;
; Why a full-tree installer and not a single-file executable: LibVLCSharp resolves its natives from
; libvlc\win-x64\ *beside* the executable, and those DLLs arrive as MSBuild Content, which
; PublishSingleFile does not embed. A lone StreamsPlayer.exe dies at startup in
; VideoFrameCaptureService..ctor. The recursive [Files] line below is what makes this channel work.

#ifndef Version
  #error Version is not defined. Pass /DVersion=<version>.
#endif
#ifndef SourceDir
  #error SourceDir is not defined. Pass /DSourceDir=<absolute path to the staging tree>.
#endif

[Setup]
; SP-0092 frozen anchor - generated once on 2026-08-21 and never again. Changing it does not produce an
; upgrade; it produces a second, parallel installation on every machine that already has this one.
AppId={{15F4F08C-E78B-41B7-9039-6A3332D7D080}
AppName=STREAMS Player
AppVersion={#Version}
AppVerName=STREAMS Player {#Version}
AppPublisher=Serhii Zhyhunenko
AppPublisherURL=https://github.com/SerZhyAle/StreamsPlayer
AppSupportURL=https://github.com/SerZhyAle/StreamsPlayer/issues
AppUpdatesURL=https://github.com/SerZhyAle/StreamsPlayer/releases
VersionInfoVersion=26.0.0
; VersionInfoVersion is deliberately NOT {#Version}. Inno requires a numeric dotted quad, and the house
; stamp YY.MMDD.HHmm would be read as 26.820.1828 - the leading zero in the date field is lost, so the
; installer's version resource would silently disagree with the application's own. The application
; carries the real stamp; this field only has to be well-formed and non-decreasing.

; No elevation. With lowest, {autopf} resolves to %LOCALAPPDATA%\Programs, which a user without
; administrator rights can always write to.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

DefaultDirName={autopf}\StreamsPlayer
DefaultGroupName=STREAMS Player
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile={#SourceDir}\LICENSE

; The payload is win-x64 only. Refusing a machine that cannot run it beats installing something that
; will not start.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputBaseFilename=StreamsPlayer-{#Version}-windows-x64-setup
Compression=lzma2/max
SolidCompression=yes
; Resolved from this script's own directory, not from SourceDir - the staging tree is a build output
; and its depth below the repository root is not ours to assume.
SetupIconFile={#SourcePath}\..\assets\streamsplayer.ico
UninstallDisplayName=STREAMS Player
UninstallDisplayIcon={app}\StreamsPlayer.exe
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
; The Inno-shipped wizard languages that overlap the product's own shipped set. The product ships
; thirteen interface languages; that list has one home in InterfaceLanguages (StreamsPlayer.Core) and is
; deliberately not restated here. Anything Inno does not carry falls back to English.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; One recursive line carries the whole self-contained publish, including libvlc\win-x64\ and
; THIRD-PARTY-NOTICES.txt. The notices requirement for a distributed package is met by this line - it
; needs no special case, because the staging tree already holds the file.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\STREAMS Player"; Filename: "{app}\StreamsPlayer.exe"
Name: "{group}\{cm:UninstallProgram,STREAMS Player}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\STREAMS Player"; Filename: "{app}\StreamsPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StreamsPlayer.exe"; Description: "{cm:LaunchProgram,STREAMS Player}"; Flags: nowait postinstall skipifsilent

; There is deliberately NO [UninstallDelete] section.
;
; The user's catalog state, manual and imported channels, pins, listening history, preview cache and
; diagnostic logs live in %LOCALAPPDATA%\StreamsPlayer. That folder is shared with the portable build
; and is not ours to remove: uninstalling one distribution channel must not destroy data the user
; created through another. Removing it here would be silent data loss, so its absence is a decision,
; not an oversight.
