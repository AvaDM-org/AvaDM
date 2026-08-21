; Inno Setup script for AvaDM's Windows installer.
;
; Invoked from CI (see .github/workflows/release.yml) as:
;   ISCC.exe packaging\windows\setup.iss /DMyAppVersion=1.2.3 /DSourceDir=<publish output dir> /DOutputDir=<where to drop the .exe>
;
; DefaultDirName uses {autopf}, which Inno resolves to Program Files when running elevated and to
; a per-user install location otherwise (PrivilegesRequired=lowest + the overrides-allowed dialog
; below let the user pick either without us hard-coding one). The [Setup] DisableDirPage is left
; at its default (enabled), so the wizard's directory-selection page - where the user can browse
; to a different folder - is shown.
;
; AutoStartService (src/AvaDM.UI/Services/AutoStartService.cs) resolves the exe path to register
; at the time the user enables "Start with System" in Settings, via Environment.ProcessPath - not
; a path baked in at install time - so installing to a user-chosen directory doesn't break it.
; [UninstallRun] below clears that registry entry on uninstall, in case it was ever enabled.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\"
#endif

#define MyAppName "AvaDM"
#define MyAppPublisher "AvaDM"
#define MyAppExeName "AvaDM.UI.exe"
#define MyAppURL "https://github.com/AvaDM-org/AvaDM"

[Setup]
; Fixed GUID - do not change between releases, or Windows will treat upgrades as unrelated installs.
AppId={{4B9D9F2E-6F0B-4C8A-9E7D-2E8B7C6A1F3D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=AvaDM-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\..\src\AvaDM.UI\Assets\avadm-logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
; The entry above carries `skipifsilent` (standard practice - an admin deploying this silently
; doesn't want an app window appearing), which would leave a self-update with no running app: the
; old AvaDM exits to release its files, and nothing ever starts the new one. UpdateService
; (src/AvaDM.UI/Services/UpdateService.cs) therefore passes /AVADMRELAUNCH=1 to mark "this silent
; run is an in-app update, relaunch when you're done". runasoriginaluser matters because a
; per-machine update elevates: without it the relaunched AvaDM would inherit the installer's admin
; token and write its settings/downloads database to the wrong user profile.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunchAfterSilentUpdate

[UninstallRun]
; Runs while the installed exe still exists (Inno executes [UninstallRun] entries before deleting
; files), so this reliably clears the HKCU\...\Run entry AutoStartService may have written,
; regardless of whether autostart was ever turned on (SetEnabled(false) is a no-op if it wasn't).
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAutostart"

; Must stay the last section in the file - Inno requires [Code] to come after every other section.
[Code]
function ShouldRelaunchAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:AVADMRELAUNCH|0}') = '1');
end;
