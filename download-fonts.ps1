# download-fonts.ps1
# Descarga Fraunces y DM Sans de Google Fonts antes de compilar

$fontsDir = "$PSScriptRoot\FocusTracker\Resources\Fonts"
New-Item -ItemType Directory -Force -Path $fontsDir | Out-Null

$fonts = @(
    @{
        Name = "Fraunces-VariableFont.ttf"
        Url  = "https://fonts.gstatic.com/s/fraunces/v31/6NUh8FyLNQOQZAnv9bYEvDiIdE9Ea92uemAk_WBq8U_9v0c2Fo0Nv7TTsU8.ttf"
    },
    @{
        Name = "DMSans-VariableFont.ttf"
        Url  = "https://fonts.gstatic.com/s/dmsans/v14/rP2tp2ywxg089UriI5-g4vlH9VoD8CmsqHB-nqYKsJU.ttf"
    }
)

foreach ($font in $fonts) {
    $dest = Join-Path $fontsDir $font.Name
    if (-not (Test-Path $dest)) {
        Write-Host "Descargando $($font.Name)..."
        try {
            Invoke-WebRequest -Uri $font.Url -OutFile $dest -UseBasicParsing
            Write-Host "  OK - $($font.Name)"
        } catch {
            Write-Warning "  No se pudo descargar $($font.Name): $_"
        }
    } else {
        Write-Host "  Ya existe: $($font.Name)"
    }
}

Write-Host ""
Write-Host "Fuentes listas en: $fontsDir"
