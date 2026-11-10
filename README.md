# Simplex Suite

**Podcast-Studio, Video-Editor und PDF-Werkstatt in einer nativen Windows-App** – im originalen Windows‑11‑Design (WinUI 3, Mica, Fluent), läuft auf **Windows 10 (ab 1809) und Windows 11**. Komplett kostenlos, ohne Konto, ohne Cloud: alles – auch die KI – läuft lokal auf deinem PC.

## Download

Jeder Push baut über **GitHub Actions** automatisch:

| Datei | Beschreibung |
|---|---|
| `SimplexSuite-Setup-<version>-x64.exe` | Installer (ohne Adminrechte installierbar, Startmenü- und Desktop-Verknüpfung) |
| `SimplexSuite-Portable-<version>-x64.zip` | Portable Version – entpacken und `SimplexSuite.exe` starten |

Zu finden unter **Actions → letzter Lauf → Artifacts**. Für ein offizielles Release einen Tag pushen (`git tag v1.0.0 && git push --tags`) – die Dateien landen dann unter **Releases**.

Alles Nötige ist enthalten: .NET-Laufzeit, Windows App SDK, FFmpeg und das RNNoise-Modell. Es muss nichts zusätzlich installiert werden.

## Podcast-Studio

- Wellenform-Editor mit Zoom (Strg+Mausrad), Höhenzoom (Umschalt+Mausrad), Auswahl, Markern und Abspielkopf
- Schneiden, Kopieren, Einfügen, Löschen, Zuschneiden, Stille einfügen, Stummschalten, unbegrenztes Rückgängig
- **Aufnehmen** direkt vom Mikrofon (an der Cursorposition einfügen)
- Dateien einfügen oder **unterlegen** (Musik mit eigener Lautstärke und automatischen Blenden)
- **Effekte – alle Parameter frei einstellbar, mit „Vorhören“:**
  - Rauschunterdrückung spektral (mit gelerntem Rauschprofil oder automatisch)
  - **KI-Rauschunterdrückung (RNNoise)**, FFT-Entrauschen, Noise Gate, De-Esser
  - Parametrischer **Equalizer** mit beliebig vielen Bändern und Frequenzkurve
  - Kompressor, Limiter (Look-Ahead), Hall, Echo, Tempo, Tonhöhe
  - Verstärkung, Normalisieren, **Lautheit nach EBU R128 (LUFS)**, Blenden mit 4 Kurven
  - **Pausen kürzen** (automatisch), Umkehren, Phase, Kanäle, eigene FFmpeg-Filterketten
- **KI-Transkript (Whisper, lokal)** mit Wort-Zeitstempeln:
  - Wörter anklicken und **wie in einem Textdokument herausschneiden**
  - **Füllwörter (äh, ähm …) automatisch finden und entfernen**
  - Suche im Transkript, Export als SRT, WebVTT oder Text
- Lautheits-/Pegelanalyse, Kapitelmarker (in MP3/M4A eingebettet, Kapitelliste für Shownotes)
- Export: MP3, WAV (16/24/32-bit), FLAC, M4A/AAC, Opus, Ogg – mit Titel, Interpret, Cover-Bild

## Video-Editor

- Mehrspur-Timeline (Video-, Audio- und Textspuren) mit Ziehen, Trimmen, Einrasten, Spurwechsel, Teilen, Duplizieren, Ripple-Löschen
- Inspektor pro Clip: Zeit, Geschwindigkeit, Skalierung, Position, Drehung, Spiegeln, Zuschnitt, Füllen
- **Farbkorrektur**: Helligkeit, Kontrast, Sättigung, Gamma, Farbton, Farbtemperatur, Weichzeichnen, Schärfen, Vignette
- **Chroma Key** (Greenscreen), Deckkraft, Ein-/Ausblenden, Audio-Lautstärke, Audio-Blenden, Rauschunterdrückung
- **Titel und Texte** mit jeder installierten Schrift, Kontur, Schatten, Hintergrundbox, Blenden
- **Automatische Untertitel per KI** (lokal, optional ins Englische übersetzt) + SRT-Export
- Bildgenaue Vorschau und Wiedergabe, Standbild speichern
- Export: MP4 (H.264/H.265/AV1), WebM (VP9), MOV (ProRes), MKV, GIF oder nur Audio – auf Wunsch mit **GPU-Beschleunigung** (NVIDIA, Intel, AMD)
- Projektformate: 16:9, 4K, Hochformat 9:16 (Reels/Shorts/TikTok), 1:1, 4:5 …

## PDF-Werkstatt

- PDFs und Bilder öffnen, **zusammenführen**, Seiten per Drag & Drop sortieren, drehen, löschen, duplizieren, leere Seiten einfügen
- Anmerkungen: Text, Freihand, Textmarker, Rechteck, Ellipse, Linie, Pfeil, Bilder/Stempel
- **Unterschreiben** mit Maus, Stift oder Finger
- **Abdecken** (Text ersetzen) und **sicheres Schwärzen** – die Seite wird gerastert, der Inhalt ist danach wirklich entfernt
- **Texterkennung (OCR)** mit der Windows-OCR – macht gescannte PDFs **durchsuchbar**
- Speichern mit Metadaten, **Passwortschutz**, Berechtigungen, Wasserzeichen und Seitenzahlen
- Auszug als neue PDF, in Einzelseiten aufteilen, Seiten als PNG/JPEG exportieren

## Lokale KI-Modelle

Unter **Einstellungen → Spracherkennung** lassen sich Whisper-Modelle mit einem Klick laden:

| Modell | Größe | RAM | Empfehlung |
|---|---|---|---|
| Tiny | 75 MB | ≈ 0,4 GB | Entwürfe |
| Base | 142 MB | ≈ 0,5 GB | schnell |
| Small | 466 MB | ≈ 1 GB | ältere PCs |
| **Large v3 Turbo (Q5)** | 547 MB | ≈ 1,5 GB | **beste Wahl für 8‑GB‑PCs** |
| Medium | 1,5 GB | ≈ 2,6 GB | sehr genau |
| Large v3 Turbo | 1,6 GB | ≈ 3 GB | ab 16 GB RAM |

## Selbst bauen

Voraussetzungen: Windows 10/11, .NET 8 SDK.

```powershell
dotnet test tests/SimplexSuite.Core.Tests
dotnet publish src/SimplexSuite/SimplexSuite.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish/SimplexSuite
```

FFmpeg (`ffmpeg.exe`, `ffprobe.exe`) gehört in `publish/SimplexSuite/ffmpeg/` – oder in den Einstellungen einen eigenen Pfad angeben. Den kompletten Ablauf inkl. Installer zeigt `.github/workflows/build.yml`.

## Aufbau

```
src/SimplexSuite.Core   Plattformunabhängige Logik: Audio-Engine & DSP, Lautheit, Effekte,
                        Whisper, FFmpeg-Render-Graph für die Timeline, PDF-Export (PDFsharp)
src/SimplexSuite        WinUI-3-Oberfläche (Windows App SDK, Community Toolkit SettingsCards)
tests/                  xUnit-Tests der Kernlogik
installer/              Inno-Setup-Skript
```

## Lizenzen der Komponenten

FFmpeg (GPL), whisper.cpp / Whisper.net (MIT), RNNoise-Modell (BSD), PDFsharp (MIT), NAudio (MIT), Windows App SDK (MIT), Windows Community Toolkit (MIT).
