# UWP-GoW3-Launcher (Xbox Series S | X)

Launcher UWP nativo, receptor de transferencia directa por Wi-Fi y motor de preparación/enlace en consola para **God of War III Remastered** (`CUSA01715` / `CUSA01623`), basado en la arquitectura de [`UWP-Spider-Launcher`](https://github.com/BBCPrince/UWP-Spider-Launcher) y el runtime de [`gow3_pc`](https://github.com/FelipePrado0/gow3_pc).

---

## Características Principales

1. **Transferencia Directa por Wi-Fi (`http://<IP-de-tu-Xbox>:8077/`)**:
   - Envía tu carpeta descomprimida `CUSA01715` / `CUSA01623` directamente desde el navegador de tu PC hacia el almacenamiento de la Xbox (`D:\DevelopmentFiles\GoW3Installer-Imports\GodOfWar3Remastered`) sin crear archivos `.zip` ni duplicar espacio en el disco de tu PC.
   - Protegido con código de emparejamiento de 6 dígitos (`X-Transfer-Code`), código QR en pantalla y verificación SHA-256 al vuelo.
2. **Preparación Nativa 100% en la Xbox (`GoW3PreparationEngine.cs`)**:
   - Port completo en C# de los scripts de preparación de `gow3_pc` (`prepare.py`, `link_modules.py`, `patches.py`, `content_profile.py`), ejecutándose directamente en la Xbox Series S/X sin necesitar Python instalado:
     - Lee `sce_sys/param.sfo` y genera `out/content.bin` (`G3CONT01`).
     - Desempaqueta `eboot.bin`, `sce_module/libc.prx` y `sce_module/libSceFios2.prx` (soporta PS4 SELF en texto plano y ELF64).
     - Enlaza `eboot.bin` + `libc.prx` + `libSceFios2.prx` y reescribe las cargas TCB `mov rax, fs:[0]` -> `gs:[0]`, produciendo `out/boot-linked.bin` (`G3PROBE5`).
     - Compila los parches por firma desde `Patches/God_of_War_III_Remastered.xml` (`Bug Fix - Startup Black Screen`, parches de resolución `720p`/`1080p`/`1440p` y `60 FPS++`/`120 FPS++`) en `out/patches.bin` (`G3PATCH2`).
     - Construye un módulo PE64 válido (`out/GoW3StartupModule.dll`) para cargar el código ejecutable con `LoadLibraryExW` cumpliendo las políticas de Arbitrary Code Guard (ACG) de Xbox UWP.
3. **Configuración Específica para Xbox Series S**:
   - Presupuesto de memoria directa (`GOW3_DMEM_MB=2560` y `FLEX_SPAN=256 MB`) ajustado a los límites de RAM de UWP en Xbox Series S.
   - Iconos de botones de Xbox (`GOW3_GLYPH_STYLE=xbox`) activados por defecto.

---

## Cómo Compilar el `.msix` sin instalar Visual Studio 2022 en tu PC (GitHub Actions)

Dado que el SDK de UWP + Visual Studio 2022 ocupa más de 20 GB en disco, este repositorio incluye el flujo automatizado [`.github/workflows/build-msix.yml`](.github/workflows/build-msix.yml):

1. Sube esta carpeta (`UWP-GoW3-Launcher`) a un repositorio en tu cuenta de GitHub:
   ```powershell
   cd c:\Users\Marlon\Documents\gow3-windows\UWP-GoW3-Launcher
   git init
   git add .
   git commit -m "Initial UWP-GoW3-Launcher for Xbox Series S"
   git branch -M main
   git remote add origin https://github.com/<TU_USUARIO>/UWP-GoW3-Launcher.git
   git push -u origin main
   ```
2. Ve a la pestaña **Actions** de tu repositorio en GitHub -> **Build UWP-GoW3-Launcher MSIX (Xbox Series S/X)**.
3. Al finalizar (~2 minutos), descarga el artefacto **`UWP-GoW3-Launcher-Xbox-MSIX`**, que contiene:
   - `UWP-GoW3-Launcher-1.0.0.0-x64.msix`
   - `UWP-GoW3-Launcher.cer` (certificado de firma si Xbox Device Portal lo solicita).

---

## Instalación y Uso en Xbox Series S (Dev Mode)

1. Abre **Xbox Device Portal** en el navegador de tu PC (`https://<IP-Xbox>:11443`).
2. En **My games & apps** -> **Add**, sube `UWP-GoW3-Launcher-1.0.0.0-x64.msix`.
3. Abre **God of War III Launcher** en tu Xbox Series S.
4. Pulsa **Transferir Carpeta por Wi-Fi (Recomendado)**:
   - Se mostrará en pantalla la dirección `http://<IP-Xbox>:8077/` y un código de 6 dígitos.
   - Abre esa URL en el navegador de tu PC, escribe el código de 6 dígitos y selecciona tu carpeta **`CUSA01715`**.
5. Al terminar la transferencia, el launcher ejecutará automáticamente la preparación en la Xbox (`boot-linked.bin`, `patches.bin`, `content.bin` y `GoW3StartupModule.dll`).
6. Configura la resolución (`1920x1080`) y los iconos (`Xbox Series S/X`) y pulsa **Jugar God of War III**.
