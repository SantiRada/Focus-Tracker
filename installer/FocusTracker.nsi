; ============================================================
;  Focus Tracker — NSIS Installer Script v1.5.1
;  Requiere: NSIS 3.x  https://nsis.sourceforge.io/
;  Compilar: makensis.exe FocusTracker.nsi
; ============================================================

Unicode True

;-- Metadata --------------------------------------------------
!define APP_NAME        "Focus Tracker"
!define APP_VERSION     "1.5.1"
!define APP_PUBLISHER   "Tenzinn"
!define APP_URL         "https://santiagorada.com/focus-tracker"
!define APP_EXE         "FocusTracker.exe"
!define APP_ICON        "..\FocusTracker\Resources\icon.ico"
!define UNINSTALL_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\FocusTracker"
!define STARTMENU_DIR   "$SMPROGRAMS\Focus Tracker"
!define DATA_DIR        "$APPDATA\FocusTracker"

;-- General ---------------------------------------------------
Name                "${APP_NAME} ${APP_VERSION}"
OutFile             "FocusTracker_Setup_v${APP_VERSION}.exe"
InstallDir          "$PROGRAMFILES64\FocusTracker"
InstallDirRegKey    HKLM "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor       /SOLID lzma
SetCompressorDictSize 32

;-- Modern UI -------------------------------------------------
!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ICON                    "${APP_ICON}"
!define MUI_UNICON                  "${APP_ICON}"

; Welcome
!define MUI_WELCOMEPAGE_TITLE       "Instalacion de Focus Tracker"
!define MUI_WELCOMEPAGE_TEXT        "Este asistente te guiara para instalar Focus Tracker ${APP_VERSION} en tu computadora.$\r$\n$\r$\nFocus Tracker te permite saber exactamente cuanto tiempo pasas en cada app y sitio web.$\r$\n$\r$\nHace clic en Siguiente para continuar."

; Finish
!define MUI_FINISHPAGE_TITLE        "Instalacion completada!"
!define MUI_FINISHPAGE_TEXT         "Focus Tracker ${APP_VERSION} fue instalado correctamente.$\r$\n$\r$\nUsa el acceso directo del escritorio para abrirlo."
!define MUI_FINISHPAGE_RUN          "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT     "Abrir Focus Tracker ahora"
!define MUI_FINISHPAGE_LINK         "Visitar sitio web"
!define MUI_FINISHPAGE_LINK_LOCATION "${APP_URL}"

!define MUI_ABORTWARNING
!define MUI_ABORTWARNING_TEXT       "Seguro que queres cancelar la instalacion?"

; Uninstall: custom components page for data deletion
!define MUI_COMPONENTSPAGE_TEXT_TOP "Elegí qué querés eliminar al desinstalar Focus Tracker."

;-- Installer pages -------------------------------------------
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE       "LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

;-- Uninstaller pages -----------------------------------------
!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

;-- Language --------------------------------------------------
!insertmacro MUI_LANGUAGE "Spanish"

; ── Install ───────────────────────────────────────────────────
Section "Focus Tracker" SecMain
    SectionIn RO

    ; Cerrar app si está corriendo antes de actualizar
    ExecWait 'taskkill /F /IM "${APP_EXE}" /T'
    Sleep 1000

    SetOutPath "$INSTDIR"
    File "..\publish\${APP_EXE}"

    ; Acceso directo escritorio
    CreateShortcut "$DESKTOP\Focus Tracker.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL

    ; Menú inicio
    CreateDirectory "${STARTMENU_DIR}"
    CreateShortcut "${STARTMENU_DIR}\Focus Tracker.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
    CreateShortcut "${STARTMENU_DIR}\Desinstalar Focus Tracker.lnk" "$INSTDIR\Uninstall.exe"

    ; Registro — aparece en Panel de control > Programas
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayName"         "${APP_NAME}"
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayVersion"      "${APP_VERSION}"
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "Publisher"           "${APP_PUBLISHER}"
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "URLInfoAbout"        "${APP_URL}"
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "InstallLocation"     "$INSTDIR"
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "UninstallString"     '"$INSTDIR\Uninstall.exe"'
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayIcon"         "$INSTDIR\${APP_EXE}"
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify"            1
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair"            1

    WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

; ── Uninstall sections ────────────────────────────────────────

; Sección obligatoria: eliminar el programa
Section "un.Focus Tracker (programa)" UnSecMain
    SectionIn RO
    ExecWait 'taskkill /F /IM "${APP_EXE}" /T'

    Delete "$INSTDIR\${APP_EXE}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir  "$INSTDIR"

    Delete "$DESKTOP\Focus Tracker.lnk"
    Delete "${STARTMENU_DIR}\Focus Tracker.lnk"
    Delete "${STARTMENU_DIR}\Desinstalar Focus Tracker.lnk"
    RMDir  "${STARTMENU_DIR}"

    DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd

; Sección opcional: borrar datos del usuario
Section /o "un.Eliminar mis datos guardados (historial de sesiones)" UnSecData
    RMDir /r "${DATA_DIR}"
SectionEnd

; Descripciones para la página de componentes del desinstalador
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecMain} "Elimina el ejecutable y los accesos directos de Focus Tracker."
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecData} "Elimina permanentemente tu historial de sesiones, proyectos y configuraciones guardados en $APPDATA\FocusTracker. Esta accion no se puede deshacer."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

