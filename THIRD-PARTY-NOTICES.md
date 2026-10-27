# Third-party notices

ShakeConvert's own source code is released under the MIT License (see `LICENSE`).
The installer also bundles or uses the following third-party components, each under its own license.

| Component | Use | License | Source |
|---|---|---|---|
| .NET 8 runtime | Application runtime (self-contained) | MIT | https://github.com/dotnet/runtime |
| Magick.NET / ImageMagick | Image conversion (PSD, RAW, HEIC, WEBP, AVIF, SVG…) | Apache-2.0 / ImageMagick License | https://github.com/dlemstra/Magick.NET |
| PDFsharp | PDF creation | MIT | https://github.com/empira/PDFsharp |
| PdfPig | PDF text extraction | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Markdig | Markdown conversion | BSD-2-Clause | https://github.com/xoofx/markdig |
| FFmpeg (BtbN Windows build, GPL variant) | Audio and video conversion | GPL-3.0 | https://ffmpeg.org — https://github.com/BtbN/FFmpeg-Builds |
| Python (embeddable distribution) | Runtime for the local PDF/CLIP engines | PSF License | https://www.python.org |
| pdf2docx | PDF → editable Word reconstruction | MIT | https://github.com/ArtifexSoftware/pdf2docx |
| PyMuPDF / MuPDF | PDF parsing used by pdf2docx | AGPL-3.0 (or Artifex commercial) | https://github.com/pymupdf/PyMuPDF |
| python-docx, fonttools, numpy, opencv-python-headless, fire | Dependencies of pdf2docx | MIT / MIT / BSD-3 / Apache-2.0 / Apache-2.0 | https://pypi.org |
| psd-tools | Layer compositing for Clip Studio files | MIT | https://github.com/psd-tools/psd-tools |
| Pillow, attrs | Dependencies of psd-tools | MIT-CMU / MIT | https://pypi.org |
| clip_to_psd | Clip Studio Paint (.clip) layer reader, included in `engine/` | MIT | https://github.com/dobrokot/clip_to_psd |
| pandoc (via pypandoc_binary) | Markup, text and e-book conversion (Markdown, reST, LaTeX, Org, Typst, MediaWiki, DocBook, Jupyter, EPUB, FB2…) | GPL-2.0-or-later (pypandoc: MIT) | https://pandoc.org |
| 7-Zip (7z.exe, 7z.dll) | Archives (ZIP, 7Z, RAR, TAR, GZ, ISO, CAB…) | LGPL-2.1 + unRAR restriction (BSD parts) | https://www.7-zip.org |
| trimesh | 3D models (GLB, glTF, STL, OBJ, 3MF, PLY) | MIT | https://github.com/mikedh/trimesh |
| ezdxf | CAD drawings (DXF → PDF, SVG, PNG) | MIT | https://github.com/mozman/ezdxf |
| pyshp | Shapefile read/write | MIT | https://github.com/GeospatialPython/pyshp |
| vtracer | Raster → SVG tracing | MIT | https://github.com/visioncortex/vtracer |
| pillow-heif (with libheif and x265) | HEIC output | BSD-3-Clause (libheif: LGPL-3.0, x265: GPL-2.0) | https://github.com/bigcat88/pillow_heif |
| PyYAML, tomli-w, xmltodict, brotli | Structured data (YAML, TOML, XML) and WOFF2 fonts | MIT | https://pypi.org |

## Notes on copyleft components

- **FFmpeg (GPL-3.0)**, **pandoc (GPL-2.0+)**, **7-Zip (LGPL-2.1 + unRAR restriction)** and **PyMuPDF (AGPL-3.0)** run as separate programs/processes invoked by ShakeConvert. They are distributed unmodified. Their complete source code is available at the links above; if you redistribute the installer, you must keep these notices and make the corresponding source available on request.
- MOBI/AZW and other rare e-book formats use **Calibre** only if already installed (not bundled).
- Office conversions use **Microsoft Office** or **LibreOffice** only if already installed on the user's PC (LibreOffice can optionally be installed through winget). They are not bundled.
