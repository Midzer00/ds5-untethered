; SPDX-License-Identifier: GPL-3.0-or-later
; Inno Setup 6 (6.3 or newer) script for the DS5 Untethered installer. Built by tools\publish.ps1, which passes:
;   /DAppVersion=0.1.0-preview      shown version (InformationalVersion)
;   /DAppVersionNumeric=0.1.0       file version of Setup.exe (Version)
;   /DStagingDir=<published folder> the self-contained app folder (with LICENSE, THIRD-PARTY-NOTICES.md, README.txt)
;   /DOutputDir=<artifacts folder>
; No absolute paths in here: everything else is relative to this script's folder.
;
; Per-user install, no administrator rights. The two drivers (usbip-win2, HidHide) are not installed here; the app's
; Setup page guides that.

#ifndef AppVersion
  #error Pass /DAppVersion (run tools\publish.ps1)
#endif
#ifndef AppVersionNumeric
  #error Pass /DAppVersionNumeric (run tools\publish.ps1)
#endif
#ifndef StagingDir
  #error Pass /DStagingDir (run tools\publish.ps1)
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "DS5 Untethered"
#define AppExe "Kopru.App.exe"
; Held by every running copy (SingleInstance.MutexName in Kopru.Core; session namespace, no Global\ prefix).
#define AppMutex "DS5Untethered.Running"
; HKCU Run value written by the app's "Start with Windows" (RunKeyStartupRegistration.ValueName).
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "Untethered"
; The app's data folder (AppPaths.FolderName under %LOCALAPPDATA%).
#define DataFolder "Untethered"

[Setup]
; Fixed for every version: upgrades install over the previous one.
AppId={{4B19CEA3-5728-4B63-AA22-BC72FDA10B4B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL=https://github.com/Midzer00/ds5-untethered
AppSupportURL=https://github.com/Midzer00/ds5-untethered/issues
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
LicenseFile={#StagingDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=DS5-Untethered-{#AppVersion}-Setup
SetupIconFile=..\..\src\Kopru.App\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
; Light or dark like Windows. The logo (tools\installer\make-wizard-images.ps1 regenerates the images): the tall Welcome /
; Finished image is the logo on the app's graphite backdrop (a dark side panel in both appearances); the small top-right
; image is the controller mark on a transparent background (32-bit, straight alpha), so it fits the light or dark header.
; Every size Inno Setup documents for 100-250 % DPI; Setup picks the best fit.
WizardStyle=modern dynamic
WizardImageFile=wizard\WizardImage-164x314.bmp,wizard\WizardImage-192x386.bmp,wizard\WizardImage-246x459.bmp,wizard\WizardImage-273x556.bmp,wizard\WizardImage-328x604.bmp,wizard\WizardImage-355x700.bmp,wizard\WizardImage-410x797.bmp
WizardSmallImageFile=wizard\WizardSmall-55x55.bmp,wizard\WizardSmall-64x68.bmp,wizard\WizardSmall-83x80.bmp,wizard\WizardSmall-92x97.bmp,wizard\WizardSmall-110x106.bmp,wizard\WizardSmall-119x123.bmp,wizard\WizardSmall-138x140.bmp
WizardImageAlphaFormat=defined
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; A running copy (from any folder) holds this mutex (SingleInstance in the app). Setup and the uninstaller ask the user
; to exit it from the tray first: Restart Manager would only hide it to the tray or kill it, and a killed app leaves the
; real controller hidden by HidHide. Exiting from the tray runs the app's shutdown (virtual controller unplugged, HidHide
; entries undone).
AppMutex={#AppMutex}
CloseApplications=no
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Messages]
en.SetupAppRunningError=%1 is running.%n%nRight-click its tray icon and choose Exit (this releases the controller), then click OK to continue, or Cancel to exit.
tr.SetupAppRunningError=%1 çalışıyor.%n%nSistem tepsisindeki simgesine sağ tıklayıp Çıkış'ı seçin (kumanda serbest bırakılır), sonra devam etmek için Tamam'a, çıkmak için İptal'e tıklayın.
en.UninstallAppRunningError=%1 is running.%n%nRight-click its tray icon and choose Exit (this releases the controller), then click OK to continue, or Cancel to exit.
tr.UninstallAppRunningError=%1 çalışıyor.%n%nSistem tepsisindeki simgesine sağ tıklayıp Çıkış'ı seçin (kumanda serbest bırakılır), sonra devam etmek için Tamam'a, çıkmak için İptal'e tıklayın.

[CustomMessages]
en.AppRunning=DS5 Untethered is running. Right-click its tray icon and choose Exit (this releases the controller), then click Retry.
tr.AppRunning=DS5 Untethered çalışıyor. Sistem tepsisindeki simgesine sağ tıklayıp Çıkış'ı seçin (kumanda serbest bırakılır), sonra Yeniden Dene'ye tıklayın.
en.AppStillRunning=DS5 Untethered is still running. Exit it from the tray icon and run Setup again.
tr.AppStillRunning=DS5 Untethered hâlâ çalışıyor. Sistem tepsisindeki simgesinden çıkıp kurulumu yeniden başlatın.
en.DeleteData=Also delete DS5 Untethered's settings, profiles and logs?
tr.DeleteData=DS5 Untethered'ın ayarları, profilleri ve günlükleri de silinsin mi?
en.DriversNote=DS5 Untethered was removed. The two drivers it used were left installed: if no other program needs them, you can remove "USBip" and "HidHide" in Windows Settings > Apps > Installed apps.
tr.DriversNote=DS5 Untethered kaldırıldı. Kullandığı iki sürücü kurulu bırakıldı: başka bir program kullanmıyorsa "USBip" ve "HidHide"ı Windows Ayarlar > Uygulamalar > Yüklü uygulamalar'dan kaldırabilirsiniz.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// AppMutex is checked when Setup starts; the app may have been started again since (e.g. from the Start menu), so it is
// checked once more right before files are replaced.
function WaitForAppExit(): Boolean;
begin
  Result := True;
  while CheckForMutexes('{#AppMutex}') do
  begin
    if MsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WaitForAppExit() then
    Result := CustomMessage('AppStillRunning');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // "Start with Windows": removed only when it points at this install (a copy elsewhere may own the value).
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#RunValue}', Command) then
      if Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0 then
        RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent() then
    begin
      DataDir := ExpandConstant('{localappdata}\{#DataFolder}');
      if DirExists(DataDir) then
        if MsgBox(CustomMessage('DeleteData') + #13#10#13#10 + DataDir, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
          DelTree(DataDir, True, True, True);
      MsgBox(CustomMessage('DriversNote'), mbInformation, MB_OK);
    end;
  end;
end;
