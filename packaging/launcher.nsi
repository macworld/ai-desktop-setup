; This wrapper only prepares and runs the existing WPF assistant. It does not
; install a runtime, change accounts, request elevation, or write configuration.
Unicode true
RequestExecutionLevel user
SilentInstall silent
AutoCloseWindow true
CRCCheck force
SetCompressor /SOLID lzma
SetCompressorDictSize 8
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2

!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef ARCH
  !error "ARCH is required"
!endif

Name "AI Desktop Setup"
OutFile "${OUTPUT_FILE}"
Icon "../App/Assets/app.ico"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "AI Desktop Setup"
VIAddVersionKey "CompanyName" "AI Desktop Setup"
VIAddVersionKey "FileDescription" "AI Desktop Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "AI Desktop Setup"

Var PayloadPath

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ; Do not resolve libraries relative to the browser's Downloads directory.
  SetOutPath "$SYSDIR"
  ${IfNot} ${AtLeastBuild} 19041
    MessageBox MB_OK|MB_ICONINFORMATION "安装助手需要 Windows 10 2004 或更新版本。请先完成 Windows 更新。"
    SetErrorLevel 2
    Quit
  ${EndIf}
  ${GetNativeMachineArchitecture} $0
!if "${ARCH}" == "arm64"
  ${If} $0 != 43620
    MessageBox MB_OK|MB_ICONINFORMATION "这个文件适用于 ARM64 电脑。请下载适合此电脑架构的版本。"
    SetErrorLevel 2
    Quit
  ${EndIf}
!else
  ${If} $0 != 34404
  ${AndIf} $0 != 43620
    MessageBox MB_OK|MB_ICONINFORMATION "安装助手需要 64 位 Windows 10 或 Windows 11。"
    SetErrorLevel 2
    Quit
  ${EndIf}
!endif

  ClearErrors
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  IfErrors runtime_missing
!if "${ARCH}" == "arm64"
  IntCmp $0 533320 runtime_ready runtime_missing runtime_ready
!else
  IntCmp $0 528040 runtime_ready runtime_missing runtime_ready
!endif
  runtime_missing:
!if "${ARCH}" == "arm64"
    MessageBox MB_OK|MB_ICONINFORMATION "请先通过 Windows 更新将电脑更新到 Windows 11 22H2 或更新版本，再打开安装助手。"
!else
    MessageBox MB_OK|MB_ICONINFORMATION "请先通过 Windows 更新将电脑更新到 Windows 10 2004 或更新版本，再打开安装助手。"
!endif
    SetErrorLevel 3
    Quit
  runtime_ready:
FunctionEnd

Section
  InitPluginsDir
  StrCpy $PayloadPath "$PLUGINSDIR\app"
  ; Create a new directory with a protected DACL BEFORE extracting executable
  ; bytes. The owner, SYSTEM, and Administrators can access it. Administrators
  ; must retain access for UAC consent using another administrator's account.
  System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "D:P(A;OICI;FA;;;OW)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", i 1, *p .r1, p 0) i.r0'
  ${If} $0 = 0
    Goto preparation_failed
  ${EndIf}
  System::Alloc 12
  Pop $2
  System::Call '*$2(i 12, p $1, i 0)'
  System::Call 'kernel32::CreateDirectoryW(w "$PayloadPath", p $2) i.r0'
  System::Free $2
  System::Call 'kernel32::LocalFree(p $1)'
  ${If} $0 = 0
    Goto preparation_failed
  ${EndIf}

  ClearErrors
  SetOutPath "$PayloadPath"
  File /r "${PAYLOAD_DIR}/*"
  IfErrors preparation_failed
  ; Keep the complete payload, including .exe.config and DLLs, alive until the
  ; UI exits. Its elevated helper is launched from the inner executable and is
  ; awaited by the UI; no credentials or command-line arguments pass via NSIS.
  ClearErrors
  ExecWait '"$PayloadPath\AI.Desktop.Setup.exe"' $0
  IfErrors launch_failed
  SetErrorLevel $0
  Goto cleanup

  preparation_failed:
    MessageBox MB_OK|MB_ICONEXCLAMATION "安装助手未能打开。请检查磁盘空间，重新下载后再试。"
    SetErrorLevel 4
    Goto cleanup
  launch_failed:
    MessageBox MB_OK|MB_ICONEXCLAMATION "安装助手未能启动。请完成 Windows 更新后重试，或联系 AI Desktop Setup 支持。"
    SetErrorLevel 5
  cleanup:
    ; Leaving the payload directory releases the working-directory handle.
    SetOutPath "$SYSDIR"
    ; NSIS owns $PLUGINSDIR and removes it after this process exits. Never remove
    ; any user-supplied or predictable persistent installation directory.
SectionEnd
