# Memos

Editor de texto y de Markdown para Windows 11, nativo (C#, .NET 10, WinUI 3 / Windows App SDK). Una alternativa moderna al Bloc de notas: ligera, con pestañas, sin conexión y **sin inteligencia artificial, telemetría, cuentas ni publicidad**.

## Funcionalidades

**Documentos y pestañas**

- Varias pestañas en la barra de título (TabView): reordenar, cerrar, cerrar las demás, cerrar las de la derecha, reabrir la última cerrada (`Ctrl+Mayús+T`), botón «+».
- Abrir varios archivos a la vez, archivos recientes, arrastrar y soltar, apertura desde el Explorador y «Abrir con». Un segundo arranque entrega sus archivos a la ventana ya abierta.
- Indicador de cambios sin guardar, confirmación al cerrar, guardar / guardar como / guardar todo, guardado automático opcional.
- Restauración de sesión opcional: recupera pestañas, texto sin guardar, cursor y pestaña activa (con copia de seguridad periódica frente a cierres inesperados).
- Aviso cuando otro programa modifica el archivo abierto; nunca se sobrescribe sin preguntar.

**Editor de texto**

- UTF-8 (con y sin BOM), UTF-16 LE/BE (con BOM o detectado sin él) y Windows-1252 como respaldo. Detección de BOM, validación de secuencias inválidas, «reabrir con codificación» y «guardar con codificación», con aviso antes de conversiones con pérdida.
- Saltos de línea CRLF / LF / CR detectados, conservados y convertibles desde la barra de estado.
- Buscar y reemplazar integrado: incremental, resaltado de coincidencias, mayúsculas/minúsculas, palabra completa, expresiones regulares (con grupos `$1`), contador, reemplazar uno/todos.
- Ir a línea, números de línea, ajuste de línea, zoom, sangría automática, tabulaciones o espacios, tamaño de tabulación.
- Duplicar, eliminar y mover líneas; MAYÚSCULAS/minúsculas; quitar espacios finales; aumentar/reducir sangría; convertir sangría entre espacios y tabulaciones.
- Deshacer/rehacer por pasos (la escritura se agrupa por palabras).
- Barra de estado: línea y columna (por grafemas), caracteres, palabras, codificación, salto de línea y zoom.
- Fuente, tamaño y zoom son **preferencias visuales**: un `.txt` nunca recibe formato ni marcado.

**Editor Markdown**

- Tres modos: solo edición, vista dividida y solo vista previa. Vista previa en tiempo real que actualiza solo los bloques que cambian.
- CommonMark + extensiones GFM: tablas, listas de tareas, tachado, enlaces automáticos, notas al pie, identificadores de encabezado.
- Barra de formato: encabezados, negrita, cursiva, tachado, código, enlace, imagen, listas, tareas, cita, bloque de código, tabla y separador. Solo escribe Markdown estándar.
- Resaltado de sintaxis en el editor, tabla de contenidos navegable, desplazamiento sincronizado (el editor guía a la vista previa), imágenes locales, palabras y tiempo de lectura.
- Exportación a HTML (autónomo, claro/oscuro) y a PDF; copiar como HTML.

**Windows 11**

- Controles Fluent nativos, Mica / Mica Alt / Acrílico, tema claro, oscuro o del sistema, color de acento, DPI por monitor.
- Interfaz en español e inglés. Configuración persistente que se aplica sin reiniciar (salvo el idioma).

## Requisitos

- Windows 11 (compilación 22000 o posterior), x64 o ARM64.
- Para compilar: Visual Studio 2022/2026 con la carga de trabajo de desarrollo de aplicaciones WinUI, o solo el SDK de .NET 10 para la compilación sin empaquetar.
- Microsoft Edge WebView2 Runtime (incluido en Windows 11) para la vista previa y el PDF.

## Estructura

```
Memos.slnx
Memos/
  Memos.Core/        Lógica sin interfaz: Models, Services, Helpers, Markdown, ViewModels
  Memos/             Aplicación WinUI 3: Views, Services (diálogos, selectores, tema), Helpers,
                     Resources, Themes, Strings (es-ES, en-US), Assets
  Memos (Package)/   Proyecto de empaquetado MSIX (manifiesto, logotipos)
  Memos.Tests/       Pruebas xUnit de Memos.Core
docs/ARQUITECTURA.md
```

## Compilar y ejecutar

**Visual Studio:** abre `Memos.slnx`, elige `x64` (o `ARM64`), establece «Memos (Package)» como proyecto de inicio y pulsa F5.

**Línea de comandos, sin instalar ningún paquete:**

```bash
dotnet build Memos/Memos/Memos.csproj -c Debug -p:Platform=x64 -p:MemosUnpackaged=true
```

```bash
Memos\Memos\bin\x64\Debug\net10.0-windows10.0.26100.0\Memos.exe
```

`MemosUnpackaged=true` copia el Windows App SDK junto al ejecutable. Se pueden pasar rutas de archivos como argumentos. La variable de entorno `MEMOS_DATA_DIR` redirige la carpeta de datos (por defecto `%LOCALAPPDATA%\Memos`).

## Pruebas

```bash
dotnet test Memos/Memos.Tests
```

157 pruebas: codificaciones y BOM, Unicode (emojis, RTL, combinados), saltos de línea, documentos vacíos y grandes, solo lectura, rutas inexistentes, archivos bloqueados, búsqueda y reemplazo, deshacer, operaciones de línea, renderizado y seguridad de Markdown, exportación HTML, cierre con cambios pendientes, recuperación de sesión y persistencia de preferencias.

## Empaquetado MSIX

```bash
msbuild "Memos\Memos (Package)\Memos (Package).wapproj" /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false
```

El paquete queda en `Memos\Memos (Package)\AppPackages\`. Para instalarlo hay que firmarlo con un certificado cuyo sujeto coincida con `Publisher` en `Package.appxmanifest`:

1. Crea o importa un certificado de firma de código y confía en él en el equipo de destino.
2. Compila con `/p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=<huella>`, o firma el `.msix` con `signtool sign /fd SHA256 /sha1 <huella> <paquete>.msix`.
3. Instala con doble clic o `Add-AppxPackage`. La carpeta `Dependencies` contiene el runtime del Windows App SDK.

Para ARM64 usa `/p:Platform=ARM64`. La identidad, la versión y las asociaciones de archivos están en `Package.appxmanifest`. Las asociaciones solo añaden Memos a «Abrir con»; nunca cambian la aplicación predeterminada.

## Nombre e icono

- Nombre y versión: `Product`, `AssemblyName` y `Version` en `Memos/Memos/Memos.csproj`; `DisplayName` e `Identity` en `Package.appxmanifest`.
- Icono: `Memos/Memos/Assets/Memos.ico` y las imágenes de `Memos (Package)/Images`.

## Atajos de teclado

| Atajo | Acción | Atajo | Acción |
|---|---|---|---|
| Ctrl+N | Nuevo texto | Ctrl+Mayús+N | Nuevo Markdown |
| Ctrl+O | Abrir | Ctrl+S / Ctrl+Mayús+S / Ctrl+Alt+S | Guardar / como / todo |
| Ctrl+W | Cerrar pestaña | Ctrl+Mayús+T | Reabrir pestaña |
| Ctrl+Tab / Ctrl+Mayús+Tab | Pestaña siguiente / anterior | Ctrl+, | Configuración |
| Ctrl+Z / Ctrl+Y | Deshacer / rehacer | Ctrl+F / Ctrl+H / F3 | Buscar / reemplazar / siguiente |
| Ctrl+G | Ir a línea | Ctrl++ / Ctrl+- / Ctrl+0 | Zoom |
| Ctrl+D | Duplicar línea | Ctrl+Mayús+K | Eliminar línea |
| Alt+↑ / Alt+↓ | Mover línea | Ctrl+U / Ctrl+Mayús+U | minúsculas / MAYÚSCULAS |
| Ctrl+] / Ctrl+[ | Sangría | Alt+Z | Ajuste de línea |
| Ctrl+B / Ctrl+I / Ctrl+K | Negrita / cursiva / enlace (Markdown) | Tab / Mayús+Tab | Sangrar selección |

## Límites y funciones no incluidas

Declarados para que nada parezca implementado sin estarlo:

- **Tamaño de archivo.** Se pide confirmación por encima de 2 MB y se rechazan los de más de 10 MB. El control de edición (RichEditBox) maqueta el documento completo al abrirlo. Medido en este equipo: 1 MB ASCII ≈ 1 s; 5,6 MB ASCII ≈ 4 s; 1 MB con muchos acentos ≈ 5–9 s; 5,6 MB con muchos acentos ≈ 2,5 min con la ventana sin responder. Una vez abierto, escribir en un archivo de 1 MB es fluido. El resaltado Markdown en el editor se desactiva por encima de 300 000 caracteres.
- **Texto enriquecido (RTF):** no incluido. La aplicación trabaja con texto plano y Markdown.
- **Caracteres invisibles** y **estado de inserción/sobrescritura:** el control de edición no los expone.
- **HTML dentro de Markdown:** por seguridad se muestra como texto, no se interpreta.
- **Desplazamiento sincronizado:** del editor a la vista previa, no al revés.
- **Saltos de línea mezclados:** se detectan y se avisa; al guardar se unifican con el predominante.
- **Vista previa de imágenes locales:** las situadas en la carpeta del documento o por debajo de ella; un documento sin guardar no tiene carpeta.
- **Imprimir:** no incluido (se puede exportar a PDF).
- **Mayúsculas/minúsculas en búsqueda:** no equipara formas Unicode precompuestas y descompuestas.

## Privacidad y seguridad

- Sin peticiones de red propias. La vista previa bloquea las imágenes remotas salvo que se active en Configuración; los enlaces web solo se abren, en el navegador, cuando el usuario hace clic.
- La vista previa no ejecuta scripts: HTML sin interpretar, esquemas peligrosos (`javascript:`, `data:`…) neutralizados, scripts desactivados en WebView2 y política CSP `default-src 'none'`.
- Guardado mediante archivo temporal y reemplazo, para no dejar archivos a medias.
- Los datos de la aplicación (preferencias, recientes, sesión) se guardan solo en `%LOCALAPPDATA%\Memos`.

## Dependencias

| Paquete | Motivo |
|---|---|
| Windows App SDK (componentes WinUI, Foundation, InteractiveExperiences, Runtime) | Plataforma. Se referencian los componentes sueltos y no el metapaquete, que arrastraría los de IA y ML. |
| CommunityToolkit.Mvvm | MVVM con generadores de código. |
| Microsoft.Extensions.DependencyInjection | Inyección de dependencias. |
| Markdig | Motor Markdown mantenido y conforme a CommonMark. |
| ColorCode.HTML | Resaltado de bloques de código sin JavaScript. |

Más detalle en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md).
