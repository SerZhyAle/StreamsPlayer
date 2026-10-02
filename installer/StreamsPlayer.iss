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
; Why a full-tree installer and not a single executable: LibVLCSharp resolves its natives from
; libvlc\win-x64\ *beside* the executable, and those DLLs arrive as MSBuild Content that no publish embeds.
; Every playback path - radio, the video player, grid previews - runs on them. Since SP-0119 a copy without
; them still starts, with the grid previews switched off and a notice, but it cannot play anything. The
; recursive [Files] line below is what delivers them.

#ifndef Version
  #error Version is not defined. Pass /DVersion=<version>.
#endif
#ifndef SourceDir
  #error SourceDir is not defined. Pass /DSourceDir=<absolute path to the staging tree>.
#endif

; SP-0136: the session-local lock a running copy holds (SP-0118, APP-ACTIVATION rule 2). It is an
; installer anchor as well as a runtime one: it must equal SingleInstanceIdentity.ProductMutexName
; character for character (Windows compares mutex names case-sensitively), and
; InstallerAppMutexTests holds the two together. Only the default profile's name is listed - a
; relocated test profile (SP-0133) carries a suffix and is not something this installer serves.
#define AppMutexName "Local\StreamsPlayerSingleInstance"

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
; SP-0136: Setup and Uninstall check the running copy's own lock before any file is touched and, while it
; is held, ask the user to close STREAMS Player and retry (Inno's localized "is currently running"
; OK/Cancel prompt, naming the product). Nothing is ever closed on the user's behalf. A silent run never
; reaches that prompt - see InitializeSetup / InitializeUninstall below.
AppMutex={#AppMutexName}
; The Restart Manager stays as the second line of defence for files held by some other process.
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
; The desktop shortcut is offered checked: a fresh install must leave both a Start menu group and a desktop
; shortcut (owner decision, 2026-09-27). The user may still untick it in the wizard; a silent run (winget)
; takes the default and therefore creates it. The Start menu group in [Icons] carries no task - it is
; always created. InstallerShortcutTests holds both.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

; SP-0156 (T-09): an upgrade must leave {app} holding exactly the new payload. Without this wipe, an
; upgraded install keeps every file an earlier version shipped that the new one dropped - including a
; media plugin a later engine removed, which the engine still loads at start - and the installer stops
; carrying the same payload as the archive it is published beside. Everything the user owns lives in
; %LOCALAPPDATA%\StreamsPlayer (see the [UninstallDelete] note below), so nothing user-made is here to
; lose. The wipe runs after the AppMutex checks above, so a running copy is asked about and never
; deleted out from under. SP-0183 D6: only when {app} contains a previous StreamsPlayer.exe or unins000.exe.
[InstallDelete]
Type: filesandordirs; Name: "{app}"; Check: FileExistsInApp('StreamsPlayer.exe') or FileExistsInApp('unins000.exe')

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

; [Code] stays last: Pascal does not read ';' as a comment, so nothing may follow it but code.
[Code]
{ SP-0136: a silent run with the application open fails instead of prompting. Both event functions run
  before Inno's own AppMutex check, so a silent run never reaches the "is currently running" message box -
  which /VERYSILENT without /SUPPRESSMSGBOXES would still show, and which nobody may be there to answer.
  Returning False ends Setup with exit code 1 and Uninstall with a non-zero code, before any file is
  touched; the package manager reports the failure, and the running copy is left alone. }

function InitializeSetup(): Boolean;
begin
  Result := True;
  if WizardSilent and CheckForMutexes('{#AppMutexName}') then
  begin
    Log('SP-0136: STREAMS Player is running and this is a silent install - exiting without changes. Close the application and run the installer again.');
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent and CheckForMutexes('{#AppMutexName}') then
  begin
    Log('SP-0136: STREAMS Player is running and this is a silent uninstall - exiting without changes. Close the application and run the uninstaller again.');
    Result := False;
  end;
end;

// SP-0183 D6: guard the [InstallDelete] wipe so it runs only when the app folder holds a previous installation.
// A line comment on purpose: a brace comment ends at the first closing brace, and the folder constant has one.
function FileExistsInApp(FileName: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\' + FileName));
end;
