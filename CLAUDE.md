# Clementine — convertitore di file "zero-click" per Windows

Clone funzionale di **Tangerine: File Converter** (macOS, LEAP STUDIO) per Windows 10/11.
Obiettivo: parità di esperienza d'uso, NON copia di codice (Tangerine è closed source).
Nome, icona e grafica sono originali. Uso personale.

## Esperienza utente

Tre ingressi, un solo motore. Nessun hook globale su mouse o tastiera: tutto passa da
eventi drag-and-drop WPF standard (`DragEnter`/`DragOver`/`Drop`, formato `FileDrop`)
e dal menu contestuale di Esplora file.

### 1. L'isola (sempre presente)
- Piccola finestra flottante, senza bordi, topmost, trasparente dove non c'è grafica,
  stile "companion" (ispirata a Coucou): un cerchio/pillola di ~56 px con l'icona.
- Spostabile con il drag; si aggancia ai bordi dello schermo e si **ritrae** lasciando
  visibile una linguetta; riappare al passaggio del mouse. Posizione salvata.
- Non ruba il focus (`WS_EX_NOACTIVATE`, `ShowActivated=false`), non compare in Alt+Tab
  (`WS_EX_TOOLWINDOW`), non si vede nella barra delle applicazioni.
- **Clic sinistro** → menu a comparsa con: ultimi 5 file lavorati (ripeti/apri cartella),
  "Converti dagli appunti" (se negli appunti c'è un file o un'immagine), coda lavori in
  corso, Impostazioni, Esci.
- Icona anche nella system tray (stesso menu) per quando l'isola è nascosta.

### 2. Drag sull'isola → ruota radiale (la via veloce)
1. L'utente trascina uno o più file (Esplora file, desktop, browser, qualsiasi sorgente OLE).
2. Quando il cursore **entra sopra l'isola** (`DragEnter`) l'isola si espande in una
   **ruota radiale** centrata sul cursore, con spicchi: le conversioni valide per quel tipo
   di file (es. HEIC → JPG, PNG, WebP, PDF…). Animazione di apertura ≤ 150 ms.
3. Mentre il file è sopra la ruota, i **modificatori** letti da `DragEventArgs.KeyStates`
   cambiano l'anello:
   - nessuno / `Shift` → **Conversioni**
   - `Ctrl` → **Strumenti** (comprimi, ritaglia, unisci, metadati…)
   - `Alt` → **Azioni** (copia in…, sposta in…, copia percorso, apri terminale, condividi)
   - anelli raggiungibili anche senza tastiera: un'icona al centro della ruota li cicla
     fermandosi sopra 1,5 s (400 e 800 ms provati: troppo rapidi).
4. Lo spicchio sotto il cursore si evidenzia subito (`DragOver`, nessun ritardo
   percepibile); gli spicchi non applicabili sono assenti, non disabilitati.
5. **Rilascio sullo spicchio** (`Drop`) → l'operazione parte in locale. Se il cursore esce
   dalla ruota (`DragLeave`) la ruota si richiude e il drag prosegue normalmente altrove:
   l'isola non deve mai "catturare" un drag non voluto.
6. Note di implementazione: la ruota è la stessa finestra dell'isola ridimensionata
   (`Width/Height` + `Left/Top` ricalcolati per restare centrata sul cursore e dentro lo
   schermo); `AllowDrop=true`; `DragOver` imposta `e.Effects = DragDropEffects.Copy` solo
   sugli spicchi e `None` altrove; hit-test per angolo e raggio sul punto del cursore.

### 3. Clic destro in Esplora file
- Voce "Clementine" nel menu contestuale dei file (Windows 11: menu classico via `Shift+F10` o "Mostra altre opzioni";
  registrazione per estensione in `HKCU\Software\Classes\SystemFileAssociations`). Lista unica: conversioni
  ("Converti in …"), strumenti, azioni, separati da una linea. Esplora file mostra max 16 voci in tutto un menu a
  cascata statico, sottomenu annidati compresi: oltre, restano fuori le ultime azioni ("Copia percorso" per prima).
  Il menu nuovo di Windows 11 richiederebbe un pacchetto MSIX firmato con `desktop4:FileExplorerContextMenus`.
- Il comando lancia l'eseguibile con `--op <id> <percorsi>`; se l'app è già aperta, i
  parametri vengono inoltrati all'istanza in esecuzione (named pipe) e il lavoro entra
  nella coda.

### Regole comuni
- Il risultato viene salvato **accanto all'originale** con suffisso
  (`foto.heic` → `foto.jpg`, `video.mp4` → `video (compresso).mp4`); l'originale **non viene
  mai sovrascritto**; se il nome esiste già si aggiunge ` (2)`.
- Toast di notifica (Windows) con progresso e pulsante "Mostra in cartella"; l'isola mostra
  un anello di avanzamento mentre lavora.
- Batch: più file dello stesso tipo → stessa operazione su tutti; tipi misti → la ruota
  mostra solo le operazioni comuni (es. "Comprimi", "Zip", "Rimuovi metadati").
- Tutto offline. Nessun account, nessuna telemetria, nessun upload.

## Architettura

```
Clementine/
├── src/
│   ├── Clementine.App/          # WPF, isola, ruota, tray, impostazioni
│   │   ├── Island/              # finestra isola + stati (ritratta, idle, ruota, lavoro)
│   │   ├── Wheel/               # layout radiale, hit-test, animazioni
│   │   ├── Shell/               # registrazione menu contestuale, named pipe
│   │   ├── Tray/
│   │   └── Settings/
│   ├── Clementine.Core/         # modello: FileKind, Operation, Pipeline, Registry
│   ├── Clementine.Engines/      # adapter verso i motori esterni
│   │   ├── FfmpegEngine.cs      # audio/video/gif
│   │   ├── MagickEngine.cs      # immagini (Magick.NET); HEIC in scrittura via encoder WinRT di Windows
│   │   ├── PdfiumEngine.cs      # PDF → immagini/testo (PDFium), merge/split e PDF → Word (PdfPig)
│   │   ├── LibreOfficeEngine.cs # docx/pptx/xlsx/pages/epub ↔ pdf (soffice --headless)
│   │   ├── ArchiveEngine.cs     # zip/tar/gz (SharpCompress), rar solo in lettura
│   │   └── MetadataEngine.cs    # ExifTool
│   └── Clementine.Tests/
├── tools/                       # binari portabili: ffmpeg, exiftool (download script)
├── docs/
└── CLAUDE.md
```

### Stack
- **.NET 8, C#, WPF** (isola con `AllowsTransparency`, `Topmost`, `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`).
- Stati dell'isola: `Collapsed → Idle → WheelOpen → Dropped|Cancelled → Working → Idle`.
- **Consegna dei file**: eventi drag-and-drop WPF (`AllowDrop`, `DataFormats.FileDrop`). Nessun hook globale, nessun `IDropTarget` custom, nessuna lettura degli appunti durante il drag.
- Vincolo UAC: l'app gira senza privilegi elevati (altrimenti Esplora file non consegna il drop).
- Esecuzione operazioni in background (`Channel<Job>`), UI mai bloccata; cancellazione con `CancellationToken`.
- Impostazioni in `%APPDATA%\Clementine\settings.json`.
- Avvio automatico opzionale (chiave `Run` del registro).

### Note sui motori (verificate in M2)
- libvips precompilato (NetVips.Native) non legge HEIC né BMP; ImageMagick (Magick.NET) legge tutto ma
  nessuno dei due scrive HEIC (encoder HEVC escluso per brevetti). HEIC in uscita usa l'encoder di Windows
  (estensioni Store "HEIF Image" + "HEVC Video"): se manca, lo spicchio HEIC non compare.
- Smart App Control bloccava DLL/exe nuovi non firmati (errore 0x800711C7), dal 26/10/2026 anche a ogni build: disattivato sul PC di sviluppo.
- ExifTool (Perl) scrive nei HEIC dell'encoder di Windows solo con ordine degli hash fisso
  (`PERL_HASH_SEED=0`, impostato in `ProcessRunner`): senza, fallisce a caso (~60%).
- PDF: rendering e testo con PDFium (Docnet.Core); PDF a più pagine → cartella "nome (PNG)" con un'immagine per pagina.
- Niente PDFsharp: dalla 6.5 è AGPL e chiede una chiave di licenza. Unisci/dividi con PdfPig (Apache 2.0).
- PDF → Word: `PdfToWord` ricostruisce paragrafi, titoli, tabelle e immagini con l'analisi di layout di PdfPig e scrive il .docx a mano.
  L'importazione PDF di LibreOffice fa caselle di testo: per questo PDF → pptx/epub sono stati tolti. Disegni vettoriali e OCR non gestiti.
- Strumenti senza parametri (M4); quelli che chiedono un editor o valori (ritaglia, annota, trim, velocità…)
  sono rimandati: vedi il commento `shortcut:` in `Toolbox.cs`.

### Mappa formati (parità con Tangerine 2.0.1)
| Tipo | Ingresso | Uscita |
|---|---|---|
| Immagini | jpg png webp heic tiff avif bmp svg gif | jpg png webp heic tiff avif bmp |
| Audio | mp3 m4a wav flac ogg opus aiff wma | stessi |
| Video | mp4 mov mkv webm avi wmv gif | stessi + gif |
| PDF | pdf | docx txt md jpg/png 300 dpi |
| Documenti | docx odt txt md epub (pages: via LibreOffice se supportato) | docx pdf txt md epub |
| Presentazioni | pptx odp | pdf jpg/png per slide |
| Fogli | xlsx ods csv tsv | xlsx ods csv tsv pdf |
| Sottotitoli | srt vtt | srt vtt |
| Archivi | zip tar gz rar(solo in) | zip tar gz |

### Strumenti (ruota Ctrl), per tipo
- Immagini: comprimi (%, o a dimensione esatta), ridimensiona, ritaglia, ruota, rimuovi metadati, annota, redazione (box neri), crea PDF da immagini, collage.
- Video: comprimi, trim, crop, velocità, split, join, estrai fotogrammi, estrai audio.
- Audio: normalizza (EBU R128), trim, bleep, mono/stereo.
- PDF: unisci, dividi, riordina, comprimi, estrai testo.
- Tutti: leggi/modifica/rimuovi metadati, leggi QR.

### Azioni (ruota Alt)
Copia in…, Sposta in…, Copia percorso, Apri terminale qui, Condividi (Nearby Share/Windows), Nuova cartella con i file.

## Convenzioni per Claude Code
- Lavora per **milestone** (sotto), ognuna con test e un commit.
- Il test di velocità (HEIC → JPG < 1 s) gira solo con `CLEMENTINE_PERF=1`, a PC tranquillo: subito dopo una
  build Windows scansiona i binari nuovi e la misura sfora.
- Ogni engine implementa `IEngine { bool CanHandle(Operation); Task RunAsync(Job, IProgress<double>, CancellationToken); }`.
- Niente servizi cloud, niente API a pagamento, niente telemetria.
- I binari esterni stanno in `tools/` e vengono scaricati da `tools/get-tools.ps1` (ffmpeg, exiftool); LibreOffice è opzionale e rilevato se installato.
- Installazione: `installer/build.ps1` pubblica un exe unico autonomo in `.build/Clementine` con "Installa Clementine.cmd"
  (`--install`): Clementine si copia da sé in `%LOCALAPPDATA%\Programs\Clementine`, menu Start, "Programmi installati"
  (`--uninstall`). Niente Inno Setup: Smart App Control bloccava il suo eseguibile temporaneo non firmato.
- Log in `%APPDATA%\Clementine\logs\` con Serilog.
- UI in italiano e inglese (resx).

## Milestone
1. **Isola + ruota**: isola flottante con aggancio ai bordi, tray, `DragEnter` apre la ruota con spicchi finti, `Drop` stampa i path ricevuti e lo spicchio scelto, `DragLeave` richiude. Nessuna conversione.
2. **Engine immagini + ffmpeg**: conversioni base, salvataggio accanto all'originale, toast, anello di avanzamento sull'isola.
3. **PDF + archivi + metadati**; menu clic sinistro sull'isola (ultimi file, appunti).
4. **Anelli Strumenti e Azioni** (modificatori + icona centrale), batch, coda, cancellazione.
5. **Clic destro in Esplora file** (registro + named pipe verso l'istanza aperta).
6. **LibreOffice** per documenti Office, impostazioni UI, avvio automatico, installer (Inno Setup o MSIX).
7. Rifinitura: animazioni, tema chiaro/scuro, preset ("web-optimized"), onboarding al primo avvio.
   Fatto: GIF 25 fps/640 px, strumento "Per il web" (immagini → WebP 1920 px, video → MP4 1280 px, senza metadati),
   icona vera (`Assets/clementine.svg` → `installer/make-icon.ps1`), tema dalla modalità app di Windows (ruota, menu,
   finestre), isola che scivola sul bordo, benvenuto al primo avvio e da "Come si usa".
8. Strumenti con parametri (finestra dopo il rilascio, valori letti da `ToolArgs`): Taglia, Velocità, Comprimi a…,
   Ritaglia (proporzioni), Pagine PDF; Registra schermo (ffmpeg gdigrab, microfono facoltativo, niente audio del PC);
   Leggi QR (ZXing.Net, negli appunti) e Metadati… (ExifTool, scrive una copia).
   Poi: Ridimensiona… (`1080`, `1920x1080`, `50%`) e `ImageEditor` per Ritaglia… (riquadro libero o per proporzioni) e
   Annota… (freccia, rettangolo, testo, Oscura = box nero; toglie la miniatura EXIF). Ridimensiona e Annota solo sulla ruota.
   Scartati dall'utente: collage, unisci/dividi video, bip, comprimi PDF.

## Criteri di "parità"
- Da ingresso sull'isola a ruota visibile: < 150 ms.
- HEIC → JPG di una foto iPhone: < 1 s.
- Nessun file originale modificato, mai.
- Funziona senza connessione.
