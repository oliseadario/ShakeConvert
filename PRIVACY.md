# Privacy policy

**In short: ShakeConvert runs entirely on your PC. It doesn't collect, send or sell any data.**

## What it does NOT do

- It doesn't connect to the internet while you use it. No telemetry, no analytics, no update checks, no accounts.
- It doesn't send your files to any server. Every conversion runs locally, including images, PDF, Office, audio, video and OCR.
- It doesn't log your keyboard or save anything that's on your screen.

## What it does, and why

| What | Why | Where it stays |
|---|---|---|
| Reads mouse movement while you drag a file | To recognize the shake. Only the position and the left button are used, and nothing is saved | In memory, for a moment |
| Captures the small area of the screen behind the ring | For the blurred glass effect, like Windows 11 menus | In memory only, never on disk |
| Reads the files you choose to convert | To convert them | The result is saved next to the original (or in a "Converted" subfolder). The original is never changed |
| Creates temporary files during a conversion | Some steps need them | `%TEMP%\ShakeConvert`, deleted when the conversion ends |
| Saves settings and an error log | To remember your preferences and help troubleshoot. The log may contain names and paths of files that failed, and is capped at 256 KB | `%AppData%\ShakeConvert` |
| Writes to the Windows registry | Only if you turn on "Start with Windows" | `HKCU\...\CurrentVersion\Run` |

## The only internet connections (only when you ask)

- **Installing LibreOffice**, from the installer or from Settings, and only if you request it. It uses **winget**, Microsoft's package manager, which downloads LibreOffice from its official sources.
- **Links in Settings** (the project page) open in your browser only if you click them.

## Third-party components

FFmpeg, 7-Zip, pdf2docx/PyMuPDF, clip_to_psd, psd-tools, pandoc, Magick.NET, PDFsharp, PdfPig, Markdig and the other bundled libraries run locally. None of them makes network connections during a conversion. If Microsoft Office or LibreOffice is installed and used for documents, its own privacy policy applies.

## Removing your data

Uninstalling ShakeConvert removes the app, the conversion engines, the startup entry and the temporary files. The uninstaller asks whether to delete your settings and error log too. You can also delete them yourself by removing the `%AppData%\ShakeConvert` folder.

## Contact

Questions? Open an issue on the project's GitHub page.
