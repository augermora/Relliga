Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!ifndef VERSION
  !error "Use scripts/Build-WindowsInstaller.ps1 to supply version and verified publish paths."
!endif
Name "Relliga"
Icon "${PUBLISH_DIR}\Relliga.ico"
UninstallIcon "${PUBLISH_DIR}\Relliga.ico"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Relliga"
RequestExecutionLevel user
SetCompressor /SOLID zlib
BrandingText "Relliga contributors · GNU GPLv3"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Relliga"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "Relliga contributors"
VIAddVersionKey /LANG=1033 "FileDescription" "Relliga per-user installer (unsigned)"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Relliga contributors. GNU GPLv3."
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_TEXT "Relliga is installed for your user account. Open it from the Start Menu. Your personal library is stored separately and is preserved during upgrades and uninstallation."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PUBLISH_DIR}\LICENSE"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "Remove Relliga application files and its Start Menu shortcut? Your personal library, backups and exports will be kept."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro CheckApplicationClosed
  IfFileExists "$INSTDIR\Relliga.exe" 0 done
  ClearErrors
  FileOpen $0 "$INSTDIR\Relliga.exe" a
  IfErrors locked
  FileClose $0
  Goto done
  locked:
    MessageBox MB_OK|MB_ICONEXCLAMATION "Close Relliga before installing, upgrading or uninstalling. No application files were changed." /SD IDOK
    SetErrorLevel 2
    Abort
  done:
!macroend

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Relliga requires 64-bit Windows." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Relliga requires Windows 10 or newer." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  !insertmacro CheckApplicationClosed
FunctionEnd

Section "Relliga" Main
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PUBLISH_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Relliga.lnk" "$INSTDIR\Relliga.exe" "" "$INSTDIR\Relliga.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "DisplayName" "Relliga"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "Publisher" "Relliga contributors"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "DisplayIcon" "$INSTDIR\Relliga.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga" "NoRepair" 1
SectionEnd

Function un.onInit
  SetShellVarContext current
  !insertmacro CheckApplicationClosed
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Relliga.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Relliga"
SectionEnd
