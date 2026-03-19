# Focus Tracker

Trackea cuánto tiempo pasás en cada app y sitio web. Datos 100% locales, sin telemetría.

---

## Para usuarios finales

**Descargá el instalador** → `FocusTracker_Setup_v1.1.exe`

1. Doble click en el instalador
2. Siguiente → Siguiente → Instalar
3. ¡Listo! Aparece en el escritorio y en el menú inicio

No necesitás instalar .NET ni ningún otro programa.

---

## Para desarrolladores

### Requisitos
- .NET 9 SDK
- Windows 10/11 x64

### Compilar y correr
```bat
build.bat
```

### Generar EXE distribuible (self-contained, sin .NET)
```bat
publish.bat
```
Salida: `publish\FocusTracker.exe` (~120 MB self-contained)

### Crear el instalador
1. Instalá [NSIS 3.x](https://nsis.sourceforge.io/Download)
2. Corré `publish.bat` primero
3. Corré `installer\build_installer.bat`

Salida: `installer\FocusTracker_Setup_v1.1.exe` (~60 MB comprimido)

---

## Datos del usuario

Los datos se guardan en `%APPDATA%\FocusTracker\focustracker.db` (SQLite).
Al desinstalar, los datos **no se borran** para que no pierdas tu historial.
Para borrarlos manualmente: eliminá la carpeta `%APPDATA%\FocusTracker`.
