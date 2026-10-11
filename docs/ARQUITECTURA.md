# Arquitectura de Memos

## Visión general

La solución separa la lógica de la interfaz en dos ensamblados:

| Proyecto | Contenido | Depende de |
|---|---|---|
| `Memos.Core` (`net10.0`) | Modelos, servicios, operaciones de texto, Markdown y **ViewModels**. No referencia WinUI. | CommunityToolkit.Mvvm, Markdig, ColorCode |
| `Memos` (`net10.0-windows`) | Vistas XAML, servicios que necesitan la ventana (diálogos, selectores, tema, WebView2), recursos. | `Memos.Core`, Windows App SDK |
| `Memos.Tests` | Pruebas xUnit sobre `Memos.Core` con dobles de los servicios de interfaz. | `Memos.Core` |
| `Memos.UITests` | Pruebas de extremo a extremo sobre el ejecutable real, por UI Automation. | — |
| `Memos (Package)` | Empaquetado MSIX. | `Memos` |

Como los ViewModels viven en `Memos.Core` y hablan con la interfaz solo a través de interfaces (`IDialogService`, `IFilePickerService`, `IEditorSurface`, `IPdfExporter`…), flujos como «cerrar con cambios pendientes» o «recuperar sesión» se prueban sin abrir ninguna ventana.

```
Views (XAML + code-behind mínimo)
   │  x:Bind, comandos
ViewModels  MainViewModel ── DocumentViewModel ── FindReplaceViewModel
   │  interfaces
Services    IFileService · ISettingsService · ISessionService · IRecentFilesService
            ISearchService · IMarkdownService · (UI) IDialogService · IFilePickerService …
Helpers     TextOperations · TextDiff · UndoHistory · LineIndex · TextStatistics · Debouncer
Models      AppSettings · TextEdit · LoadedText · SessionState …
```

La composición está en `App.xaml.cs` (`ConfigureServices`) con `Microsoft.Extensions.DependencyInjection`. `Program.cs` define `Main` para la instancia única: un segundo proceso redirige su activación al primero y termina.

## El texto: una sola tubería

El documento tiene una representación canónica: una cadena con `\n` como único separador de línea, en `DocumentViewModel.Text`. La codificación y el tipo de salto de línea son metadatos que solo se aplican al leer y al escribir (`FileService`, `TextEncodings`, `LineEndings`).

Toda modificación recorre el mismo camino:

1. **Origen.** El usuario escribe en el control, o un comando produce un `TextEdit` (rango, texto nuevo, selección resultante) mediante una función pura de `TextOperations` o `MarkdownFormatting`.
2. **Aplicación.** Con editor adjunto, `IEditorSurface.ApplyEdit` reemplaza el rango en el control; sin él (pruebas, pestañas restauradas aún no mostradas) el ViewModel aplica el cambio a la cadena.
3. **Confirmación.** `DocumentViewModel.CommitText` calcula la diferencia mínima (`TextDiff`), la registra en `UndoHistory`, invalida el índice de líneas y programa estadísticas, análisis Markdown y búsqueda.

`TextDiff` nunca corta un par suplente, y columnas y recuentos se calculan por grafemas (`StringInfo`), no por unidades UTF-16.

### Deshacer

`UndoHistory` guarda reemplazos (posición, texto quitado, texto insertado). La escritura consecutiva se fusiona por palabras, los borrados consecutivos también, y los comandos son siempre un paso propio. El estado «modificado» sale de comparar el paso actual con el paso guardado, así que deshacer hasta el punto de guardado limpia el indicador.

En texto plano y Markdown el historial es propio (el del control está desactivado) por una razón concreta: el resaltado de sintaxis y el de búsqueda se aplican como formato de caracteres en el RichEditBox, y con el historial nativo cada recoloreado sería un paso de «deshacer». Los documentos RTF usan el historial nativo para conservar los cambios de formato.

## El control de edición

`EditorControl` envuelve un `RichEditBox`. En texto plano y Markdown:

- pegado siempre como texto plano, copia en texto plano, atajos de formato enriquecido bloqueados;
- canal de números de línea dibujado aparte, posicionado con `ITextRange.GetRect` (búsqueda binaria de la primera línea visible, así el coste no depende del tamaño del documento);
- `ApplySyntaxRuns` y `ApplySearchHighlights` solo tocan formato, nunca texto.

En RTF el control carga y guarda el flujo RTF, admite pegar con formato y usa el historial nativo (de él salen «deshacer» y «rehacer»). El ViewModel conserva el RTF junto con el texto visible para búsqueda y estadísticas. El guardado de sesión conserva el flujo RTF. Tres detalles que no son evidentes:

- El control reescribe el RTF a su manera. Lo que devuelve justo después de cargar pasa a ser la referencia de «sin cambios» (`OnEditorRichTextLoaded`); si no, todo archivo parecería modificado al abrirlo.
- Al entrar en la ventana, el RichEditBox aplica su propia fuente a todo lo que contiene. Por eso el RTF se carga después de `Loaded`, y en estos documentos no se fijan fuente ni tamaño del control (tampoco el zoom).
- El color «automático» del RTF es negro. El editor RTF fuerza el tema claro sobre fondo blanco para que el texto se lea con el tema oscuro y para que ningún color del tema acabe escrito en el archivo.

Sin control (pestañas aún no mostradas, pruebas), `RichTextCodec` lee del RTF el texto y los cuatro estilos; es también lo que usa la impresión (`RichTextPrintHtml`).

**Imprimir.** `WebViewPdfExporter.PrintAsync` abre una ventana propia, hija de la principal, con un WebView2 que carga la página y ejecuta `window.print()`. Esa llamada no vuelve hasta que la vista previa se cierra, y entonces se cierra la ventana y se borra la página temporal.

**Lo que el control no ofrece y se añade encima.**

- *Caracteres invisibles*: `UpdateInvisibles` recorre solo las líneas visibles, pide al control el rectángulo de cada tramo de espacios, cada tabulación y cada fin de línea, y lo dibuja todo como una única geometría (`Path`) en una capa transparente sobre el texto. Hay un tope de mediciones por pasada y las actualizaciones se agrupan por ráfaga.
- *Inserción/sobrescritura*: la tecla Insert no llega al control. El modo es una propiedad del documento (`IsOverwrite`); al confirmar texto tecleado, `DocumentViewModel` elimina tantos grafemas a la derecha como se escribieron, sin pasar del fin de línea, dentro del mismo paso de deshacer (`UndoHistory.ExtendLastRemoved`). Los comandos (pegar, etc.) siempre insertan.
- *Colores*: `SyntaxPalette` da los colores de cada tema (predeterminado, acento de Windows, monocromo) en claro y oscuro. El de acento usa los tonos oscuros del acento sobre fondo claro y los claros sobre fondo oscuro, y se reaplica cuando Windows avisa de un cambio de color.

**Por qué RichEditBox.** Es el control nativo de WinUI 3 para texto editable de varias líneas y el que da de serie lo que la especificación exige: IME para chino, japonés y coreano, texto bidireccional, emojis en color, selección y accesibilidad (UI Automation). Las alternativas se descartaron: `TextBox` no permite resaltado; los editores dibujados a mano con Win2D pierden IME, bidi y accesibilidad; un editor web en WebView2 contradice el objetivo de aplicación nativa.

**Su coste.** No virtualiza la maquetación: al cargar calcula la altura de todo el documento, y lo hace bastante más despacio cuando abundan los caracteres no ASCII. De ahí los límites de `FileLimits` (aviso a 2 MB, máximo 10 MB) y las mediciones del README. Tampoco expone caracteres invisibles ni el modo de sobrescritura.

## Markdown

`MarkdownService.Analyze` analiza el texto una sola vez con Markdig y devuelve un `MarkdownAnalysis`:

- **Bloques**: cada bloque de primer nivel renderizado a HTML con su línea de origen.
- **Encabezados**: para la tabla de contenidos.
- **Tramos de sintaxis** (`SyntaxRun`): estilos sin solapamiento para el editor, obtenidos pintando el árbol de padres a hijos.

- **Front matter**: el bloque YAML inicial (extensión `UseYamlFrontMatter` de Markdig) se aparta del cuerpo. `FrontMatter.Parse` extrae los pares de primer nivel para mostrarlos como tabla en la vista previa; la exportación lo omite y usa su `title`.

El análisis corre en segundo plano con retardo; cada edición cancela el anterior (`Debouncer`).

**Actualización incremental.**

- *Vista previa*: `MarkdownPreview` compara la lista de bloques con la que ya muestra y reemplaza solo el tramo central distinto. El desplazamiento se conserva y escribir no redibuja el documento.
- *Editor*: `HighlightPlanner` compara tramos antiguos y nuevos teniendo en cuenta el desplazamiento producido por la edición y devuelve el rango a recolorear. Escribir en un párrafo recolorea esa edición; abrir un bloque de código recolorea lo que el bloque se traga.

**Desplazamiento sincronizado.** Cada bloque lleva `data-line`; el editor informa de su primera línea visible (con fracción) y la vista previa interpola entre el bloque que la contiene y el siguiente.

### Seguridad de la vista previa

El contenido de un documento se trata como no confiable, con varias capas:

1. Markdig con `DisableHtml()`: el HTML en bruto se escapa.
2. Lista blanca de esquemas en enlaces e imágenes (`MarkdownService.IsSafeUrl`).
3. WebView2 con `IsScriptEnabled = false`. La aplicación actualiza la página con `ExecuteScriptAsync`, que no depende de ese ajuste.
4. CSP `default-src 'none'`: solo estilos en línea e imágenes de la carpeta del documento (servida por un host virtual), `data:` y, si el usuario lo activa, remotas.
5. Navegación interceptada: la vista previa nunca navega; los enlaces web se abren en el navegador y los archivos locales, como pestaña.
6. Permisos, descargas, ventanas nuevas, autocompletado y herramientas de desarrollo desactivados; argumentos de arranque que evitan tráfico de fondo.

El WebView2 se crea la primera vez que se muestra una vista previa: una sesión solo de texto no arranca ningún proceso de navegador.

### Exportación

`HtmlDocumentBuilder` genera el documento HTML completo (CSS incrustado, sin scripts, claro/oscuro con `prefers-color-scheme`). El PDF se imprime desde un WebView2 temporal e invisible (`WebViewPdfExporter`) con `PrintToPdfAsync`, siempre en tema claro.

## Persistencia

Archivos JSON en `%LOCALAPPDATA%\Memos` (en el paquete MSIX, Windows los redirige al almacén de la aplicación, por lo que desinstalar los elimina):

- `settings.json` — `AppSettings` es un `ObservableObject`; `JsonSettingsService` guarda con retardo tras cada cambio y las vistas reaccionan a `PropertyChanged`.
- `recent.json` — archivos recientes.
- `crash.log` — registro de errores inesperados (`ErrorLog`): hilo de interfaz, `AppDomain` y tareas sin observar. Se reinicia al pasar de 512 KB y se gestiona desde Configuración.
- `Session/` — `session.json` más una copia `.bak` por documento con cambios sin guardar. Solo se reescriben las copias cuyo texto cambió.

La serialización usa generadores de código de `System.Text.Json` (sin reflexión).

## Guardado seguro

`FileService.SaveAsync` escribe en un temporal junto al destino, vuelca a disco y sustituye con `File.Replace`. Si el sistema de archivos no admite el reemplazo, escribe directamente. Los archivos de solo lectura se rechazan antes de tocar nada. `MainViewModel.SaveDocumentAsync` comprueba además la marca de tiempo y tamaño registrados al leer.

## Localización

Los textos están en `Strings/<idioma>/Resources.resw`. El XAML los toma con la extensión de marcado `{h:Loc Key=…}` y el código con `ILocalizer`; ambos usan el `ResourceLoader` del Windows App SDK. El idioma elegido se aplica con `ApplicationLanguages.PrimaryLanguageOverride` antes de cargar ningún recurso.

Para añadir un idioma: copiar una carpeta de `Strings`, traducir los valores, añadir la opción en `SettingsPage.xaml.cs` y declararlo en `Package.appxmanifest`.

## Atajos de teclado

Se resuelven en `MainWindow.OnPreviewKeyDown`, mientras el evento baja desde la raíz y antes de que lo vea el control enfocado. Así el orden frente al tratamiento de teclas del RichEditBox es determinista. Los atajos de edición solo se aplican con el foco en el editor; con un diálogo abierto no se aplica ninguno.

## Pruebas

- **Unitarias** (`Memos.Tests`): servicios y funciones puras con archivos temporales reales, y ViewModels con dobles de diálogos y selectores.
- **De interfaz** (`Memos.UITests`): `MemosApp` arranca el ejecutable sin empaquetar con una carpeta de datos propia (`MEMOS_DATA_DIR`, que además aísla la instancia única), localiza elementos por su `AutomationId` (el `x:Name` del XAML), invoca menús y botones con patrones de UI Automation y envía el teclado directamente a la ventana de entrada de WinUI, sin depender de que esté en primer plano. Las comprobaciones se hacen sobre la barra de estado y sobre los archivos en disco.
- **No automatizadas**: el contenido de la vista previa y la impresión a PDF (su generación de HTML sí está cubierta), arrastrar y soltar y los atajos con modificadores.

## Búsqueda en todas las pestañas

`GlobalSearchViewModel` recorre los documentos abiertos con el mismo `ISearchService` que la búsqueda local, en segundo plano, y publica resultados (pestaña, línea, vista previa de la línea). El reemplazo global calcula primero los cambios, pide confirmación con el total y aplica un `TextEdit` por documento, de modo que cada pestaña lo deshace por separado; un documento editado mientras el diálogo estaba abierto se omite.

## Marca

`tools/New-BrandAssets.ps1` dibuja el icono con primitivas vectoriales (GDI+) a cuatro veces el tamaño y lo reduce, con un dibujo simplificado por debajo de 24 px. De ahí salen el `.ico` y todas las imágenes del paquete, así que cambiar la marca es cambiar un script.

## Cómo extender

- **Nuevo comando de edición**: función pura en `TextOperations` que devuelve `TextEdit?`, método en `DocumentViewModel`, `[RelayCommand]` en `MainViewModel`, entrada de menú y prueba.
- **Nueva codificación**: valor en `TextEncodingId` y casos en `TextEncodings`.
- **Nuevo ajuste**: propiedad en `AppSettings`, tarjeta en `SettingsPage.xaml` y cadenas en ambos `.resw`.
