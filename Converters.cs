using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ImageMagick;
using Markdig;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using SharpPdf = PdfSharp.Pdf;
using WinPdf = global::Windows.Data.Pdf;
using WinStorage = global::Windows.Storage;
using WinStreams = global::Windows.Storage.Streams;
using WinImaging = global::Windows.Graphics.Imaging;

namespace ShakeConvert
{
    /// <summary>A conversion job on a single file.</summary>
    public sealed class Job
    {
        public string Source;
        public IProgress<double> Progress;   // 0..1
        public CancellationToken Token;
        public void Report(double v) => Progress?.Report(Math.Max(0, Math.Min(1, v)));
    }

    public sealed class Conversion
    {
        public string Id;           // "to:pdf" — the same for any source format
        public string Label;        // "PDF"
        public string Description;  // "Convert to PDF"
        public string Glyph;        // Segoe Fluent icon
        public Func<Job, Task<string>> Run; // returns the created path (file or folder)
    }

    public static class Converters
    {
        // ---------------------------------------------------------------- icons (Segoe Fluent Icons)
        public const string GPdf = "\uEA90", GImage = "\uEB9F", GPhoto = "\uE91B", GDoc = "\uE8A5", GWeb = "\uE774",
                            GText = "\uE8D2", GAudio = "\uE8D6", GVideo = "\uE714", GCode = "\uE943", GTable = "\uE80A",
                            GIcon = "\uE790", GFile = "\uE7C3", GSlides = "\uE8EB", GLayers = "\uE81E", GBrush = "\uE771";

        static HashSet<string> Set(params string[] e) => new HashSet<string>(e, StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> ImageExt = Set(".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif",
            ".avif", ".jxl", ".psd", ".psb", ".tga", ".ico", ".svg", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw", ".xcf", ".dds", ".exr", ".hdr", ".pcx", ".ppm", ".pgm", ".jp2", ".j2k", ".jpf", ".jpx", ".icns", ".apng", ".mng", ".qoi", ".cur", ".xpm", ".xbm", ".wbmp", ".pam", ".pbm", ".pfm", ".sgi", ".rgb", ".ras", ".sun", ".miff", ".dcm", ".pcd", ".crw", ".nrw", ".3fr", ".iiq", ".mos", ".mrw", ".x3f", ".kdc", ".dcr", ".erf", ".raw", ".emf", ".wmf", ".svgz");
        static readonly HashSet<string> PostScriptExt = Set(".eps", ".ps");
        static readonly HashSet<string> WordExt = Set(".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".wpd", ".dotm");
        static readonly HashSet<string> SheetExt = Set(".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".xlt", ".xltx", ".xltm");
        static readonly HashSet<string> SlideExt = Set(".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp", ".potx", ".potm");
        static readonly HashSet<string> TextExt = Set(".txt", ".log", ".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml", ".cs", ".py", ".js",
            ".css", ".scss", ".sql", ".bat", ".cmd", ".ps1", ".sh", ".java", ".kt", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".srt", ".vtt", ".tex");
        static readonly HashSet<string> CodeExt = Set(".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml", ".cs", ".py", ".js", ".css", ".scss",
            ".sql", ".bat", ".cmd", ".ps1", ".sh", ".java", ".kt", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".log", ".csv", ".tex");
        static readonly HashSet<string> MdExt = Set(".md", ".markdown");
        static readonly HashSet<string> HtmlExt = Set(".html", ".htm", ".xhtml");
        static readonly HashSet<string> VideoExt = Set(".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".flv", ".mpeg", ".mpg", ".3gp", ".ts", ".mts", ".m2ts", ".ogv", ".vob", ".mod", ".mxf", ".rm", ".rmvb", ".swf", ".wtv", ".3g2", ".asf", ".f4v", ".divx", ".dv");
        static readonly HashSet<string> AudioExt = Set(".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".wma", ".opus", ".aiff", ".aif", ".alac", ".amr", ".ac3", ".mka", ".m4b", ".au", ".caf", ".weba", ".mp2", ".voc", ".wv", ".tta", ".w64", ".eac3", ".ape", ".mpc", ".tak", ".dsf", ".dff", ".dts", ".spx", ".ra");

        public static int Quality = 92;    // JPG / WEBP / AVIF
        public static int OutputMode;      // 0 = same folder, 1 = "Converted" subfolder

        public static void Init()
        {
            if (GlobalFontSettings.FontResolver == null)
                GlobalFontSettings.FontResolver = new WinFontResolver();
            try { MagickNET.Initialize(); } catch { }
        }

        // ---------------------------------------------------------------- catalog

        /// <summary>Conversions available for ALL the files (intersection), minus the ones turned off.</summary>
        public static List<Conversion> For(IList<string> files)
        {
            List<Conversion> result = null;
            foreach (var f in files)
            {
                var l = ForFile(f);
                result = result == null ? l : result.Where(r => l.Any(x => x.Id == r.Id)).ToList();
            }
            var disabled = App.Settings?.DisabledTargets;
            if (result != null && disabled != null && disabled.Count > 0)
                result = result.Where(c => !disabled.Contains(c.Id)).ToList();
            return result ?? new List<Conversion>();
        }

        /// <summary>Every output format, by category: used by settings to turn them on or off.</summary>
        public static (string category, string glyph, (string id, string label)[] items)[] AllTargets()
        {
            (string, string)[] T(params string[] ids) => ids.Select(i => ("to:" + i, i switch
            {
                "dir" => L.T("Estrai", "Extract"), "tgz" => "TAR.GZ", "shp" => "Shapefile", "jsonl" => "JSON Lines", "jxl" => "JPEG XL",
                "jp2" => "JPEG 2000", "md" => "Markdown", "tex" => "LaTeX", "typ" => "Typst", "wiki" => "MediaWiki",
                "dbk" => "DocBook", "ipynb" => "Jupyter", "vcf" => "vCard", "kepub" => "Kobo",
                _ => i.ToUpperInvariant()
            })).ToArray();
            return new[]
            {
                (L.T("Immagini", "Images"), GImage, T("jpg", "png", "webp", "heic", "avif", "jxl", "jp2", "gif", "tiff", "bmp", "ico", "psd", "tga", "qoi", "svg", "apng")),
                (L.T("Documenti", "Documents"), GDoc, T("pdf", "docx", "doc", "odt", "rtf", "txt", "html", "md", "epub")),
                (L.T("Fogli di calcolo e dati", "Spreadsheets & data"), GTable, T("xlsx", "xls", "ods", "csv", "tsv", "json", "jsonl", "yaml", "toml", "xml", "vcf")),
                (L.T("Presentazioni", "Presentations"), GSlides, T("pptx", "ppt", "odp")),
                ("Audio", GAudio, T("mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma", "wv", "tta", "w64", "ac3", "eac3")),
                (L.T("Video e sottotitoli", "Video & subtitles"), GVideo, T("mp4", "mkv", "webm", "mov", "avi", "srt", "ass", "vtt", "ttml", "lrc")),
                (L.T("Archivi ed e-book", "Archives & e-books"), "\uE7B8", T("dir", "zip", "7z", "tar", "tgz", "cbz", "mobi", "azw3", "kepub")),
                (L.T("Testo e markup", "Text & markup"), GCode, T("rst", "tex", "org", "typ", "wiki", "dbk", "textile", "ipynb")),
                (L.T("3D, font e mappe", "3D, fonts & maps"), GFile, T("glb", "gltf", "stl", "obj", "3mf", "ply", "ttf", "otf", "woff", "woff2", "geojson", "kml", "kmz", "gpx", "shp")),
            };
        }

        static string Norm(string ext) => ext switch
        {
            ".jpeg" or ".jfif" => ".jpg",
            ".tif" => ".tiff",
            ".heif" => ".heic",
            ".htm" or ".xhtml" => ".html",
            ".aif" => ".aiff",
            ".oga" => ".ogg",
            ".markdown" => ".md",
            ".mpeg" => ".mpg",
            _ => ext
        };

        public static List<Conversion> ForFile(string path)
        {
            string ext = Norm(Path.GetExtension(path).ToLowerInvariant());
            var list = new List<Conversion>();
            void Add(string target, string glyph, Func<Job, Task<string>> run)
            {
                if ("." + target != ext && !list.Any(c => c.Id == "to:" + target)) list.Add(Make(target, glyph, run));
            }

            // families handled entirely by the extra module (data, markup, e-books, archives, 3D, fonts, geo…)
            if (MoreFormats.Catalog(ext, path, Add)) { Relabel(ext, list); return list; }

            if (ImageExt.Contains(ext))
            {
                foreach (var t in new[] { "jpg", "png", "pdf", "webp", "ico", "psd" })
                    Add(t, GlyphForTarget(t), j => ImageTo(j, t));
                // text recognized with Windows OCR
                Add("docx", GDoc, j => PdfSmart.ImageOcr(j, "docx"));
                foreach (var t in new[] { "gif", "avif", "tiff", "bmp" })
                    Add(t, GlyphForTarget(t), j => ImageTo(j, t));
                Add("txt", GText, j => PdfSmart.ImageOcr(j, "txt"));
            }
            else if (ext == ".pdf")
            {
                Add("docx", GDoc, j => LocalEngine.PdfToDocx(j)); // local reconstruction (pdf2docx), OCR for scans
                Add("xlsx", GTable, j => LocalEngine.PdfToXlsx(j));
                if (Office.Soffice != null) Add("pptx", GSlides, j => Office.Convert(j, Office.Kind.PdfImpress, "pptx"));
                Add("jpg", GImage, j => PdfToImages(j, j.Source, "jpg"));
                Add("png", GPhoto, j => PdfToImages(j, j.Source, "png"));
                Add("txt", GText, j => PdfSmart.ToText(j, "txt"));
                Add("html", GWeb, j => PdfSmart.ToText(j, "html"));
                Add("md", GCode, j => PdfSmart.ToText(j, "md"));
                if (Office.CanDocs) Add("odt", GDoc, j => Office.Convert(j, Office.Kind.PdfImport, "odt"));
                Add("webp", GImage, j => PdfToImages(j, j.Source, "webp"));
            }
            else if (ext == ".clip")
            {
                // Clip Studio Paint: layers rebuilt and merged at full resolution (local engine)
                foreach (var t in new[] { "png", "jpg", "pdf", "webp", "psd", "tiff" })
                    Add(t, GlyphForTarget(t), j => LocalEngine.ClipTo(j, t));
            }
            else if ((ext == ".indd" || ext == ".indt" || ext == ".idml") && InDesignEngine.Available)
            {
                // InDesign installed: faithful export (every page)
                foreach (var t in new[] { "pdf", "jpg", "png" })
                    Add(t, GlyphForTarget(t), j => InDesignEngine.Convert(j, t));
                if (ext == ".idml")
                {
                    Add("docx", GDoc, j => Task.Run(() => EmbeddedPreview.IdmlTo(j, "docx")));
                    Add("txt", GText, j => Task.Run(() => EmbeddedPreview.IdmlTo(j, "txt")));
                }
            }
            else if (EmbeddedPreview.Ext.Contains(ext))
            {
                // InDesign, Clip Studio Paint, Krita, Procreate, Affinity, XD, Sketch, CorelDRAW: image embedded in the file
                foreach (var t in new[] { "png", "jpg", "pdf", "webp", "psd", "tiff" })
                    Add(t, GlyphForTarget(t), j => EmbeddedPreview.To(j, t));
                if (ext == ".indd" || ext == ".indt")   // without InDesign: only the thumbnail saved in the file
                    foreach (var c in list) c.Description = L.T("Miniatura in ", "Thumbnail as ") + c.Label;
            }
            else if (ext == ".idml")
            {
                Add("docx", GDoc, j => Task.Run(() => EmbeddedPreview.IdmlTo(j, "docx")));
                Add("txt", GText, j => Task.Run(() => EmbeddedPreview.IdmlTo(j, "txt")));
            }
            else if (ext == ".ai")
            {
                // Modern Illustrator files contain a full PDF
                if (IsPdfCompatible(path))
                {
                    Add("pdf", GPdf, j => Task.Run(() => CopyAs(j, "pdf")));
                    Add("png", GPhoto, j => AiToImages(j, "png"));
                    Add("jpg", GImage, j => AiToImages(j, "jpg"));
                    Add("webp", GImage, j => AiToImages(j, "webp"));
                }
            }
            else if (PostScriptExt.Contains(ext) && HasGhostscript)
            {
                foreach (var t in new[] { "pdf", "png", "jpg", "webp" })
                    Add(t, GlyphForTarget(t), j => ImageTo(j, t));
            }
            else if (WordExt.Contains(ext))
            {
                if (Office.CanDocs)
                {
                    Add("pdf", GPdf, j => Office.Convert(j, Office.Kind.Doc, "pdf"));
                    if (ext == ".docx") Add("xlsx", GTable, j => Task.Run(() => DocxToXlsx(j)));
                    Add("docx", GDoc, j => Office.Convert(j, Office.Kind.Doc, "docx"));
                    Add("txt", GText, j => Office.Convert(j, Office.Kind.Doc, "txt"));
                    Add("odt", GDoc, j => Office.Convert(j, Office.Kind.Doc, "odt"));
                    Add("rtf", GDoc, j => Office.Convert(j, Office.Kind.Doc, "rtf"));
                    Add("html", GWeb, j => Office.Convert(j, Office.Kind.Doc, "html"));
                    Add("jpg", GImage, j => Office.ToPageImages(j, Office.Kind.Doc, "jpg"));
                    Add("png", GPhoto, j => Office.ToPageImages(j, Office.Kind.Doc, "png"));
                    if (ext == ".docx") Add("md", GCode, j => Task.Run(() => DocxToMd(j)));
                    Add("doc", GDoc, j => Office.Convert(j, Office.Kind.Doc, "doc"));
                }
                else if (ext == ".docx" || ext == ".docm" || ext == ".dotx")
                {
                    Add("txt", GText, j => Task.Run(() => DocxToTxt(j)));
                    Add("html", GWeb, j => Task.Run(() => DocxToHtml(j)));
                    Add("md", GCode, j => Task.Run(() => DocxToMd(j)));
                    Add("xlsx", GTable, j => Task.Run(() => DocxToXlsx(j)));
                    Add("pdf", GPdf, j => Task.Run(() => DocxToPdfText(j)));
                }
            }
            else if (SheetExt.Contains(ext))
            {
                if (Office.CanSheets)
                {
                    Add("pdf", GPdf, j => Office.Convert(j, Office.Kind.Sheet, "pdf"));
                    Add("xlsx", GTable, j => Office.Convert(j, Office.Kind.Sheet, "xlsx"));
                    Add("ods", GTable, j => Office.Convert(j, Office.Kind.Sheet, "ods"));
                    Add("csv", GTable, j => Office.Convert(j, Office.Kind.Sheet, "csv"));
                    Add("html", GWeb, j => Office.Convert(j, Office.Kind.Sheet, "html"));
                    Add("xls", GTable, j => Office.Convert(j, Office.Kind.Sheet, "xls"));
                }
                if (ext == ".xlsx" || ext == ".xlsm")
                {
                    Add("csv", GTable, j => Task.Run(() => XlsxToCsv(j)));
                    Add("json", GCode, j => Task.Run(() => XlsxToJson(j)));
                    Add("html", GWeb, j => Task.Run(() => XlsxToHtml(j)));
                    Add("docx", GDoc, j => Task.Run(() => XlsxToDocx(j)));
                }
            }
            else if (ext == ".csv")
            {
                Add("xlsx", GTable, j => Task.Run(() => CsvToXlsx(j)));
                if (Office.CanSheets) Add("ods", GTable, j => Office.Convert(j, Office.Kind.Sheet, "ods"));
                Add("docx", GDoc, j => Task.Run(() => CsvToDocx(j)));
                Add("pdf", GPdf, j => Task.Run(() => TextFileToPdf(j)));
                Add("html", GWeb, j => Task.Run(() => CsvToHtml(j)));
                Add("json", GCode, j => Task.Run(() => CsvToJson(j)));
                Add("txt", GText, j => Task.Run(() => CopyAsTxt(j)));
            }
            else if (SlideExt.Contains(ext))
            {
                if (Office.CanSlides)
                {
                    Add("pdf", GPdf, j => Office.Convert(j, Office.Kind.Slide, "pdf"));
                    Add("pptx", GSlides, j => Office.Convert(j, Office.Kind.Slide, "pptx"));
                    Add("odp", GSlides, j => Office.Convert(j, Office.Kind.Slide, "odp"));
                    Add("png", GPhoto, j => Office.ToPageImages(j, Office.Kind.Slide, "png"));
                    Add("jpg", GImage, j => Office.ToPageImages(j, Office.Kind.Slide, "jpg"));
                    Add("ppt", GSlides, j => Office.Convert(j, Office.Kind.Slide, "ppt"));
                }
            }
            else if (MdExt.Contains(ext))
            {
                Add("html", GWeb, j => Task.Run(() => MdToHtml(j)));
                Add("pdf", GPdf, j => Task.Run(() => MdToPdf(j)));
                Add("docx", GDoc, j => Task.Run(() => MdToDocx(j)));
                Add("txt", GText, j => Task.Run(() => MdToTxt(j)));
            }
            else if (HtmlExt.Contains(ext))
            {
                if (Office.CanDocs)
                {
                    Add("pdf", GPdf, j => Office.Convert(j, Office.Kind.Doc, "pdf"));
                    Add("docx", GDoc, j => Office.Convert(j, Office.Kind.Doc, "docx"));
                }
                else Add("pdf", GPdf, j => Task.Run(() => HtmlToPdf(j)));
                Add("xlsx", GTable, j => Task.Run(() => HtmlToXlsx(j)));
                Add("txt", GText, j => Task.Run(() => HtmlToTxt(j)));
                Add("md", GCode, j => Task.Run(() => HtmlToMd(j)));
            }
            else if (TextExt.Contains(ext))
            {
                Add("pdf", GPdf, j => Task.Run(() => TextFileToPdf(j)));
                Add("docx", GDoc, j => Task.Run(() => TextToDocx(j)));
                Add("html", GWeb, j => Task.Run(() => TextToHtml(j)));
                Add("txt", GText, j => Task.Run(() => CopyAsTxt(j)));
            }
            else if (VideoExt.Contains(ext) && Media.Ffmpeg != null)
            {
                foreach (var t in new[] { "mp4", "mp3", "gif", "webm", "mov", "mkv", "wav", "avi", "m4a" })
                    Add(t, GlyphForTarget(t), j => Media.Convert(j, t));
            }
            else if (AudioExt.Contains(ext) && Media.Ffmpeg != null)
            {
                foreach (var t in new[] { "mp3", "wav", "flac", "m4a", "ogg", "opus", "aac", "wma" })
                    Add(t, GAudio, j => Media.Convert(j, t));
            }
            MoreFormats.Extend(ext, path, Add, ImageExt, VideoExt, AudioExt);
            Relabel(ext, list);
            return list;
        }

        internal static string GlyphForTarget(string t) => t switch
        {
            "pdf" => GPdf,
            "jpg" or "webp" or "avif" => GImage,
            "png" or "gif" or "tiff" or "bmp" => GPhoto,
            "ico" => GIcon,
            "psd" => GLayers,
            "mp4" or "mkv" or "webm" or "mov" or "avi" => GVideo,
            "mp3" or "wav" or "m4a" => GAudio,
            _ => GFile
        };

        /// <summary>Clearer labels where "Convert to X" isn't enough.</summary>
        static void Relabel(string ext, List<Conversion> list)
        {
            foreach (var c in list)
            {
                if (c.Id == "to:dir") { c.Label = L.T("ESTRAI", "EXTRACT"); c.Glyph = "\uE838"; c.Description = L.T("Estrai in una cartella", "Extract to a folder"); }
                else if (c.Id == "to:tgz") { c.Label = "TAR.GZ"; c.Description = L.T("Converti in TAR.GZ", "Convert to TAR.GZ"); }
                else if (c.Id == "to:shp") c.Description = L.T("Converti in Shapefile", "Convert to Shapefile");
                else if (c.Id == "to:zip" && MoreFormats.Glyph(ext) != "\uE7B8") c.Description = L.T("Comprimi in ZIP", "Compress to ZIP");
                else if (VideoExt.Contains(ext) && (c.Id == "to:jpg" || c.Id == "to:png")) c.Description = L.T("Fotogramma in ", "Frame as ") + c.Label;
                else if (VideoExt.Contains(ext) && (c.Id == "to:srt" || c.Id == "to:ass" || c.Id == "to:vtt")) c.Description = L.T("Estrai sottotitoli ", "Extract subtitles as ") + c.Label;
                else if (ImageExt.Contains(ext) && c.Id == "to:svg") c.Description = L.T("Vettorializza in SVG", "Vectorize to SVG");
                else if (ImageExt.Contains(ext) && (c.Id == "to:docx" || c.Id == "to:txt")) c.Description = L.T("Testo (OCR) in ", "Text (OCR) as ") + c.Label;
            }
        }

        public static string GlyphFor(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var more = MoreFormats.Glyph(ext);
            if (more != null) return more;
            if (ext == ".pdf") return GPdf;
            if (ext == ".ai" || EmbeddedPreview.Ext.Contains(ext)) return GBrush;
            if (ext == ".psd" || ext == ".psb") return GLayers;
            if (ImageExt.Contains(ext)) return GImage;
            if (VideoExt.Contains(ext)) return GVideo;
            if (AudioExt.Contains(ext)) return GAudio;
            if (SheetExt.Contains(ext) || ext == ".csv") return GTable;
            if (SlideExt.Contains(ext)) return GSlides;
            if (HtmlExt.Contains(ext)) return GWeb;
            if (CodeExt.Contains(ext)) return GCode;
            return GDoc;
        }

        /// <summary>Human-readable text for settings ("Supported formats").</summary>
        public static (string category, string formats)[] SupportedSummary() => new[]
        {
            (L.T("Immagini e foto", "Images & photos"), L.T("JPG, PNG, WEBP, AVIF, HEIC, GIF, TIFF, BMP, ICO, SVG, JPEG XL, TGA, RAW (CR2, CR3, NEF, ARW, DNG…) → JPG, PNG, WEBP, HEIC, PDF, AVIF, JPEG XL, ICO, GIF, TIFF, BMP, PSD, SVG · testo riconosciuto (OCR) → DOCX, TXT", "JPG, PNG, WEBP, AVIF, HEIC, GIF, TIFF, BMP, ICO, SVG, JPEG XL, TGA, RAW (CR2, CR3, NEF, ARW, DNG…) → JPG, PNG, WEBP, HEIC, PDF, AVIF, JPEG XL, ICO, GIF, TIFF, BMP, PSD, SVG · recognized text (OCR) → DOCX, TXT")),
            ("Adobe", L.T("Photoshop PSD/PSB → tutti i formati immagine e PDF · Illustrator AI → PDF, PNG, JPG, WEBP · InDesign INDD/IDML → PDF, JPG, PNG di tutte le pagine (con InDesign installato; altrimenti la miniatura) · IDML → Word, TXT · XD → immagine", "Photoshop PSD/PSB → every image format and PDF · Illustrator AI → PDF, PNG, JPG, WEBP · InDesign INDD/IDML → PDF, JPG, PNG of every page (with InDesign installed; otherwise the thumbnail) · IDML → Word, TXT · XD → image")),
            (L.T("App di disegno", "Drawing apps"), L.T("Clip Studio Paint (livelli uniti a piena risoluzione), Krita, OpenRaster, Procreate, Affinity Photo/Designer, Sketch, CorelDRAW, GIMP → PNG, JPG, PDF, WEBP, PSD, TIFF", "Clip Studio Paint (layers merged at full resolution), Krita, OpenRaster, Procreate, Affinity Photo/Designer, Sketch, CorelDRAW, GIMP → PNG, JPG, PDF, WEBP, PSD, TIFF")),
            ("PDF", L.T("PDF → Word modificabile (titoli, paragrafi, tabelle, immagini), Excel con le tabelle pronte, PowerPoint*, JPG, PNG, WEBP, TXT, HTML, Markdown · i PDF scansionati vengono letti con l'OCR", "PDF → editable Word (headings, paragraphs, tables, images), Excel with ready-to-use tables, PowerPoint*, JPG, PNG, WEBP, TXT, HTML, Markdown · scanned PDFs are read with OCR")),
            (L.T("Word e documenti", "Word & documents"), L.T("DOC, DOCX, ODT, RTF → PDF, DOCX, ODT, RTF, TXT, HTML, Markdown, JPG/PNG delle pagine · tabelle di Word → Excel", "DOC, DOCX, ODT, RTF → PDF, DOCX, ODT, RTF, TXT, HTML, Markdown, JPG/PNG of the pages · Word tables → Excel")),
            (L.T("Excel e fogli di calcolo", "Excel & spreadsheets"), L.T("XLS, XLSX, ODS, CSV → PDF, XLSX, ODS, CSV, HTML, JSON, DOCX · CSV e tabelle HTML → Excel formattato", "XLS, XLSX, ODS, CSV → PDF, XLSX, ODS, CSV, HTML, JSON, DOCX · CSV and HTML tables → formatted Excel")),
            (L.T("PowerPoint e presentazioni", "PowerPoint & presentations"), L.T("PPT, PPTX, ODP → PDF, PPTX, ODP, PNG/JPG delle slide", "PPT, PPTX, ODP → PDF, PPTX, ODP, PNG/JPG of the slides")),
            (L.T("Testo, codice e web", "Text, code & web"), L.T("TXT, Markdown, HTML, JSON, XML, codice sorgente → PDF, DOCX, HTML, TXT, Markdown", "TXT, Markdown, HTML, JSON, XML, source code → PDF, DOCX, HTML, TXT, Markdown")),
            ("Audio", "MP3, WAV, FLAC, M4A, M4B, AAC, OGG, OPUS, WMA, AIFF, AC3, DTS, APE, WavPack, DSD… → MP3, WAV, FLAC, M4A, OGG, OPUS, AAC, WMA, WavPack, TTA, W64, AC3, E-AC3"),
            ("Video", L.T("MP4, MKV, MOV, AVI, WEBM, WMV, FLV, MPEG, TS, 3GP, VOB, MXF, RM… → MP4, MKV, WEBM, MOV, AVI, GIF, APNG, MP3, WAV, M4A, fotogramma JPG/PNG, sottotitoli SRT/ASS/VTT", "MP4, MKV, MOV, AVI, WEBM, WMV, FLV, MPEG, TS, 3GP, VOB, MXF, RM… → MP4, MKV, WEBM, MOV, AVI, GIF, APNG, MP3, WAV, M4A, JPG/PNG frame, SRT/ASS/VTT subtitles")),
            (L.T("Vettoriali e CAD", "Vector & CAD"), L.T("SVG, EMF, WMF, AI, EPS · CorelDRAW, Visio, CGM, ODG → PDF, SVG, PNG · DXF → PDF, SVG, PNG · immagini → SVG (vettorializzazione)", "SVG, EMF, WMF, AI, EPS · CorelDRAW, Visio, CGM, ODG → PDF, SVG, PNG · DXF → PDF, SVG, PNG · images → SVG (vectorization)")),
            (L.T("Testo e markup", "Text & markup"), L.T("Markdown, reStructuredText, LaTeX, Org, Typst, MediaWiki, DocBook, Jupyter, Textile → Word, PDF, HTML, EPUB, ODT, PowerPoint e tra loro", "Markdown, reStructuredText, LaTeX, Org, Typst, MediaWiki, DocBook, Jupyter, Textile → Word, PDF, HTML, EPUB, ODT, PowerPoint and between each other")),
            (L.T("Dati", "Data"), L.T("JSON, JSON Lines, YAML, TOML, XML, CSV, TSV, Excel ↔ tra loro · vCard ↔ CSV, Excel", "JSON, JSON Lines, YAML, TOML, XML, CSV, TSV, Excel ↔ each other · vCard ↔ CSV, Excel")),
            (L.T("E-book e fumetti", "E-books & comics"), L.T("EPUB, FB2 → PDF, Word, TXT, HTML, Markdown · MOBI, AZW3 e altri con Calibre installato · CBZ, CBR, CB7 → PDF · PDF → EPUB, CBZ", "EPUB, FB2 → PDF, Word, TXT, HTML, Markdown · MOBI, AZW3 and more with Calibre installed · CBZ, CBR, CB7 → PDF · PDF → EPUB, CBZ")),
            (L.T("Sottotitoli", "Subtitles"), "SRT, ASS, SSA, VTT, SAMI, LRC, TTML, MicroDVD, SCC → SRT, ASS, VTT, TTML, LRC"),
            (L.T("Archivi", "Archives"), L.T("ZIP, 7Z, RAR, TAR, GZ, BZ2, XZ, ISO, CAB, DEB, RPM, JAR… → estrai, ZIP, 7Z, TAR, TAR.GZ · qualsiasi file → ZIP", "ZIP, 7Z, RAR, TAR, GZ, BZ2, XZ, ISO, CAB, DEB, RPM, JAR… → extract, ZIP, 7Z, TAR, TAR.GZ · any file → ZIP")),
            (L.T("3D, font e mappe", "3D, fonts & maps"), L.T("GLB, glTF, STL, OBJ, 3MF, PLY tra loro · TTF, OTF, WOFF, WOFF2 · GeoJSON, KML, KMZ, GPX, Shapefile, CSV", "GLB, glTF, STL, OBJ, 3MF, PLY between each other · TTF, OTF, WOFF, WOFF2 · GeoJSON, KML, KMZ, GPX, Shapefile, CSV")),
            ("E-mail", "EML, MHTML → PDF, HTML, TXT, Word"),
        };

        static Conversion Make(string target, string glyph, Func<Job, Task<string>> run) => new Conversion
        {
            Id = "to:" + target,
            Label = target.ToUpperInvariant(),
            Description = L.T("Converti in ", "Convert to ") + target.ToUpperInvariant(),
            Glyph = glyph,
            Run = run
        };

        // ---------------------------------------------------------------- paths

        static readonly string FallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShakeConvert");

        public static string OutDirFor(string src)
        {
            string dir = Path.GetDirectoryName(src);
            if (OutputMode == 1) dir = Path.Combine(dir, L.T("Convertiti", "Converted"));
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".sc{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                Directory.CreateDirectory(FallbackDir);   // protected folder: save to Documents\ShakeConvert
                return FallbackDir;
            }
        }

        public static string OutPath(string src, string ext)
        {
            string dir = OutDirFor(src);
            string name = Path.GetFileNameWithoutExtension(src);
            string p = Path.Combine(dir, $"{name}.{ext}");
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}).{ext}");
            return p;
        }

        static string UniqueDir(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i})");
            return p;
        }

        public static string NewTemp()
        {
            string d = Path.Combine(Path.GetTempPath(), "ShakeConvert", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static Task<T> RunSta<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var th = new Thread(() =>
            {
                try { tcs.SetResult(f()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            return tcs.Task;
        }

        internal static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
            {
                using var r = new StreamReader(new MemoryStream(b), true);
                return r.ReadToEnd();
            }
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
        }

        static void WriteUtf8(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

        static string CopyAs(Job j, string ext)
        {
            string outp = OutPath(j.Source, ext);
            File.Copy(j.Source, outp);
            return outp;
        }

        static bool IsPdfCompatible(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[Math.Min(2048, (int)Math.Min(fs.Length, 2048))];
                int n = fs.Read(buf, 0, buf.Length);
                return Encoding.ASCII.GetString(buf, 0, n).Contains("%PDF");
            }
            catch { return false; }
        }

        static bool HasGhostscript =>
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs"));

        // ---------------------------------------------------------------- images (Magick.NET)

        /// <param name="readFrom">file to read if different from the original (e.g. an extracted preview); the output name stays the original's</param>
        internal static Task<string> ImageTo(Job j, string fmt, string readFrom = null) => Task.Run(() =>
        {
            string ext = Path.GetExtension(readFrom ?? j.Source).ToLowerInvariant();
            var settings = new MagickReadSettings();
            if (ext == ".svg" || PostScriptExt.Contains(ext))
            {
                settings.Density = new Density(200);
                settings.BackgroundColor = MagickColors.Transparent;
            }

            using var img = new MagickImage(readFrom ?? j.Source, settings);
            j.Report(0.4);
            img.AutoOrient();
            j.Token.ThrowIfCancellationRequested();

            string outp = OutPath(j.Source, fmt);
            switch (fmt)
            {
                case "pdf":
                    ImageToPdf(img.ToByteArray(MagickFormat.Png), (double)img.Width, (double)img.Height, outp);
                    break;

                case "ico":
                    {
                        uint side = Math.Max(img.Width, img.Height);
                        img.BackgroundColor = MagickColors.Transparent;
                        img.Extent(side, side, Gravity.Center, MagickColors.Transparent);
                        if (side > 256) img.Resize(256, 256);
                        img.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
                        img.Write(outp, MagickFormat.Ico);
                        break;
                    }

                case "jpg":
                case "bmp":
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaOption.Remove);
                    img.Quality = (uint)Quality;
                    img.Write(outp, fmt == "jpg" ? MagickFormat.Jpeg : MagickFormat.Bmp);
                    break;

                default:
                    img.Quality = (uint)Quality;
                    img.Write(outp, fmt switch
                    {
                        "png" => MagickFormat.Png,
                        "webp" => MagickFormat.WebP,
                        "avif" => MagickFormat.Avif,
                        "gif" => MagickFormat.Gif,
                        "tiff" => MagickFormat.Tiff,
                        "psd" => MagickFormat.Psd,
                        "jxl" => MagickFormat.Jxl,
                        "jp2" => MagickFormat.Jp2,
                        "tga" => MagickFormat.Tga,
                        "qoi" => MagickFormat.Qoi,
                        _ => throw new NotSupportedException(fmt)
                    });
                    break;
            }
            return outp;
        });

        static void ImageToPdf(byte[] png, double pxW, double pxH, string outPath)
        {
            using var ms = new MemoryStream(png, 0, png.Length, false, true); // publiclyVisible: PDFsharp reads the internal buffer
            bool landscape = pxW > pxH;
            var doc = new SharpPdf.PdfDocument();
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(landscape ? 841.89 : 595.28);
            page.Height = XUnit.FromPoint(landscape ? 595.28 : 841.89);

            using (var g = XGraphics.FromPdfPage(page))
            using (var x = XImage.FromStream(ms))
            {
                const double m = 24;
                double pw = page.Width.Point - 2 * m, ph = page.Height.Point - 2 * m;
                double s = Math.Min(pw / pxW, ph / pxH);
                double w = pxW * s, h = pxH * s;
                g.DrawImage(x, m + (pw - w) / 2, m + (ph - h) / 2, w, h);
            }
            doc.Save(outPath);
        }

        // ---------------------------------------------------------------- PDF

        /// <summary>Renders PDF pages as images (Windows PDF engine). The job's source decides name and folder.</summary>
        public static async Task<string> PdfToImages(Job j, string pdfPath, string fmt)
        {
            var file = await WinStorage.StorageFile.GetFileFromPathAsync(pdfPath);
            var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
            uint n = doc.PageCount;
            string name = Path.GetFileNameWithoutExtension(j.Source);
            string dir = OutDirFor(j.Source);

            string outDir = dir;
            if (n > 1) { outDir = UniqueDir(dir, $"{name} - {fmt.ToUpperInvariant()}"); Directory.CreateDirectory(outDir); }

            string last = null;
            for (uint i = 0; i < n; i++)
            {
                j.Token.ThrowIfCancellationRequested();
                using var page = doc.GetPage(i);
                bool viaPng = fmt == "webp";
                var opts = new WinPdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * 2)), // ~190 dpi
                    BitmapEncoderId = fmt == "jpg" ? WinImaging.BitmapEncoder.JpegEncoderId : WinImaging.BitmapEncoder.PngEncoderId
                };
                using var stream = new WinStreams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, opts);

                last = n == 1 ? OutPath(j.Source, fmt) : Path.Combine(outDir, L.T($"{name} - pagina {i + 1:D2}.{fmt}", $"{name} - page {i + 1:D2}.{fmt}"));
                stream.Seek(0);
                if (viaPng)
                {
                    using var ms = new MemoryStream();
                    await stream.AsStreamForRead().CopyToAsync(ms);
                    ms.Position = 0;
                    using var img = new MagickImage(ms);
                    img.Quality = (uint)Quality;
                    img.Write(last, MagickFormat.WebP);
                }
                else
                {
                    using var fs = File.Create(last);
                    await stream.AsStreamForRead().CopyToAsync(fs);
                }
                j.Report((i + 1.0) / n);
            }
            return n == 1 ? last : outDir;
        }

        static async Task<string> AiToImages(Job j, string fmt)
        {
            string tmp = NewTemp();
            try
            {
                string pdf = Path.Combine(tmp, "file.pdf");
                File.Copy(j.Source, pdf);
                return await PdfToImages(j, pdf, fmt);
            }
            finally { TryDelete(tmp); }
        }

        static string PdfToText(Job j)
        {
            var sb = new StringBuilder();
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(j.Source))
            {
                int total = Math.Max(1, doc.NumberOfPages);
                foreach (var page in doc.GetPages())
                {
                    j.Token.ThrowIfCancellationRequested();
                    var lines = page.GetWords()
                        .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
                        .OrderByDescending(g => g.Key);
                    foreach (var line in lines)
                        sb.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
                    sb.AppendLine();
                    sb.AppendLine(L.T($"──── pagina {page.Number} ────", $"──── page {page.Number} ────"));
                    sb.AppendLine();
                    j.Report((double)page.Number / total);
                }
            }
            string outp = OutPath(j.Source, "txt");
            WriteUtf8(outp, sb.ToString());
            return outp;
        }

        internal static void TextToPdf(string text, string outPath, string family)
        {
            var doc = new SharpPdf.PdfDocument();
            var font = new XFont(family, family == "Consolas" ? 9.5 : 10.5);
            const double margin = 56;
            double lineH = font.Size * 1.4;

            SharpPdf.PdfPage page;
            XGraphics g = null;
            double y = 0, maxW = 0, maxY = 0;

            void NewPage()
            {
                g?.Dispose();
                page = doc.AddPage();
                page.Width = XUnit.FromPoint(595.28);
                page.Height = XUnit.FromPoint(841.89);
                g = XGraphics.FromPdfPage(page);
                y = margin;
                maxW = page.Width.Point - 2 * margin;
                maxY = page.Height.Point - margin;
            }

            NewPage();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n'))
            {
                foreach (var line in Wrap(g, raw, font, maxW).ToList())
                {
                    if (y + lineH > maxY) NewPage();
                    if (line.Length > 0)
                        g.DrawString(line, font, XBrushes.Black, new XRect(margin, y, maxW, lineH), XStringFormats.TopLeft);
                    y += lineH;
                }
            }
            g.Dispose();
            doc.Save(outPath);
        }

        static IEnumerable<string> Wrap(XGraphics g, string text, XFont font, double maxW)
        {
            if (text.Length == 0 || g.MeasureString(text, font).Width <= maxW) { yield return text; yield break; }

            var cur = new StringBuilder();
            foreach (var word in Regex.Split(text, @"(?<=\s)"))
            {
                string attempt = cur.ToString() + word;
                if (g.MeasureString(attempt, font).Width <= maxW) { cur.Append(word); continue; }

                if (cur.Length > 0) { yield return cur.ToString().TrimEnd(); cur.Clear(); }

                string w = word;
                while (w.Length > 1 && g.MeasureString(w, font).Width > maxW)
                {
                    int k = w.Length - 1;
                    while (k > 1 && g.MeasureString(w.Substring(0, k), font).Width > maxW) k--;
                    yield return w.Substring(0, k);
                    w = w.Substring(k);
                }
                cur.Append(w);
            }
            if (cur.Length > 0) yield return cur.ToString().TrimEnd();
        }

        // ---------------------------------------------------------------- text

        static string TextFileToPdf(Job j)
        {
            string outp = OutPath(j.Source, "pdf");
            TextToPdf(ReadText(j.Source), outp, CodeExt.Contains(Path.GetExtension(j.Source)) ? "Consolas" : "Segoe UI");
            return outp;
        }

        static string CopyAsTxt(Job j)
        {
            string outp = OutPath(j.Source, "txt");
            WriteUtf8(outp, ReadText(j.Source));
            return outp;
        }

        static string TextToHtml(Job j)
        {
            string outp = OutPath(j.Source, "html");
            string title = Path.GetFileName(j.Source);
            WriteUtf8(outp, Html(title, $"<h1>{WebUtility.HtmlEncode(title)}</h1><pre>{WebUtility.HtmlEncode(ReadText(j.Source))}</pre>"));
            return outp;
        }

        static string TextToDocx(Job j)
        {
            string outp = OutPath(j.Source, "docx");
            bool code = CodeExt.Contains(Path.GetExtension(j.Source));
            var paras = ReadText(j.Source).Replace("\r\n", "\n").Split('\n').Select(l => (0, l));
            WriteDocx(outp, paras, code ? "Consolas" : "Calibri");
            return outp;
        }

        static readonly MarkdownPipeline Md = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        static string MdToHtml(Job j)
        {
            string outp = OutPath(j.Source, "html");
            WriteUtf8(outp, Html(Path.GetFileNameWithoutExtension(j.Source), Markdown.ToHtml(ReadText(j.Source), Md)));
            return outp;
        }

        static string MdToTxt(Job j)
        {
            string outp = OutPath(j.Source, "txt");
            WriteUtf8(outp, Markdown.ToPlainText(ReadText(j.Source), Md));
            return outp;
        }

        static string MdToPdf(Job j)
        {
            string outp = OutPath(j.Source, "pdf");
            TextToPdf(Markdown.ToPlainText(ReadText(j.Source), Md), outp, "Segoe UI");
            return outp;
        }

        static string MdToDocx(Job j)
        {
            var paras = new List<(int, string)>();
            bool fence = false;
            foreach (var raw in ReadText(j.Source).Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.TrimStart().StartsWith("```")) { fence = !fence; continue; }
                if (fence) { paras.Add((0, raw)); continue; }
                var m = Regex.Match(raw, @"^(#{1,6})\s+(.*)$");
                if (m.Success) { paras.Add((m.Groups[1].Length, m.Groups[2].Value.Trim('#', ' '))); continue; }
                string plain = raw.Trim().Length == 0 ? "" : Markdown.ToPlainText(raw, Md).Trim('\r', '\n');
                if (Regex.IsMatch(raw, @"^\s*[-*+]\s+")) plain = "• " + plain;
                paras.Add((0, plain));
            }
            string outp = OutPath(j.Source, "docx");
            WriteDocx(outp, paras, "Calibri");
            return outp;
        }

        internal static string StripHtml(string html)
        {
            html = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|/p|/div|/h[1-6]|/li|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", "");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, @"[ \t]+", " ");
            return Regex.Replace(html, @"\n\s*\n\s*\n+", "\n\n").Trim();
        }

        static string HtmlToTxt(Job j)
        {
            string outp = OutPath(j.Source, "txt");
            WriteUtf8(outp, StripHtml(ReadText(j.Source)));
            return outp;
        }

        static string HtmlToPdf(Job j)
        {
            string outp = OutPath(j.Source, "pdf");
            TextToPdf(StripHtml(ReadText(j.Source)), outp, "Segoe UI");
            return outp;
        }

        static string HtmlToMd(Job j)
        {
            string html = ReadText(j.Source);
            html = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            for (int h = 1; h <= 6; h++)
                html = Regex.Replace(html, $@"<h{h}[^>]*>(.*?)</h{h}>", m => "\n\n" + new string('#', h) + " " + m.Groups[1].Value + "\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(b|strong)[^>]*>(.*?)</\1>", "**$2**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(i|em)[^>]*>(.*?)</\1>", "*$2*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<a[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", "[$2]($1)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            string outp = OutPath(j.Source, "md");
            WriteUtf8(outp, StripHtml(html));
            return outp;
        }

        // ---------------------------------------------------------------- DOCX (without Office)

        static List<(int level, string text)> ReadDocx(string path)
        {
            var list = new List<(int, string)>();
            using var z = ZipFile.OpenRead(path);
            var entry = z.GetEntry("word/document.xml") ?? throw new InvalidDataException(L.T("Documento Word non valido", "Invalid Word document"));
            XDocument x;
            using (var s = entry.Open()) x = XDocument.Load(s);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            foreach (var p in x.Descendants(w + "p"))
            {
                string style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
                int level = 0;
                var m = Regex.Match(style, @"(?i)(heading|titolo|berschrift|titre)\s*(\d)");
                if (m.Success) level = int.Parse(m.Groups[2].Value);
                else if (Regex.IsMatch(style, "(?i)^title$|^titolo$")) level = 1;

                var sb = new StringBuilder();
                foreach (var n in p.Descendants())
                {
                    if (n.Name == w + "t") sb.Append(n.Value);
                    else if (n.Name == w + "tab") sb.Append('\t');
                    else if (n.Name == w + "br") sb.Append('\n');
                }
                list.Add((level, sb.ToString()));
            }
            return list;
        }

        static string DocxToTxt(Job j)
        {
            string outp = OutPath(j.Source, "txt");
            WriteUtf8(outp, string.Join(Environment.NewLine, ReadDocx(j.Source).Select(p => p.text)));
            return outp;
        }

        static string DocxToMd(Job j)
        {
            var sb = new StringBuilder();
            foreach (var (level, text) in ReadDocx(j.Source))
                sb.AppendLine(level > 0 ? new string('#', Math.Min(level, 6)) + " " + text : text).AppendLine();
            string outp = OutPath(j.Source, "md");
            WriteUtf8(outp, Regex.Replace(sb.ToString(), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine));
            return outp;
        }

        static string DocxToHtml(Job j)
        {
            var sb = new StringBuilder();
            foreach (var (level, text) in ReadDocx(j.Source))
            {
                if (text.Trim().Length == 0) continue;
                string t = WebUtility.HtmlEncode(text).Replace("\n", "<br>");
                sb.Append(level > 0 ? $"<h{Math.Min(level, 6)}>{t}</h{Math.Min(level, 6)}>" : $"<p>{t}</p>").Append('\n');
            }
            string outp = OutPath(j.Source, "html");
            WriteUtf8(outp, Html(Path.GetFileNameWithoutExtension(j.Source), sb.ToString()));
            return outp;
        }

        static string DocxToPdfText(Job j)
        {
            string outp = OutPath(j.Source, "pdf");
            TextToPdf(string.Join("\n", ReadDocx(j.Source).Select(p => p.text)), outp, "Segoe UI");
            return outp;
        }

        static void WriteDocx(string outPath, IEnumerable<(int level, string text)> paras, string font)
        {
            var docx = new DocxBuilder();
            bool code = font == "Consolas";
            foreach (var (level, text) in paras)
                docx.Paragraph(text ?? "", code ? DocxBuilder.Code : Math.Min(level, 3));
            docx.Save(outPath);
        }

        // ---------------------------------------------------------------- tables → Excel / Word

        internal static List<List<string[]>> DocxTables(string path)
        {
            var tables = new List<List<string[]>>();
            using var z = ZipFile.OpenRead(path);
            var entry = z.GetEntry("word/document.xml") ?? throw new InvalidDataException(L.T("Documento Word non valido", "Invalid Word document"));
            XDocument x;
            using (var st = entry.Open()) x = XDocument.Load(st);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            foreach (var tbl in x.Descendants(w + "tbl").Where(t => !t.Ancestors(w + "tbl").Any()))
            {
                var rows = new List<string[]>();
                foreach (var tr in tbl.Elements(w + "tr"))
                    rows.Add(tr.Elements(w + "tc").Select(tc => string.Join("\n", tc.Elements(w + "p")
                        .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))))).ToArray());
                if (rows.Count > 0) tables.Add(rows);
            }
            return tables;
        }

        static string DocxToXlsx(Job j)
        {
            var tables = DocxTables(j.Source);
            var xlsx = new XlsxBuilder();
            if (tables.Count == 0)
            {
                // no tables: the text goes into column A
                var rows = ReadDocx(j.Source).Where(p => p.text.Trim().Length > 0).Select(p => new[] { p.text }).ToList();
                xlsx.AddSheet(L.T("Testo", "Text"), XlsxBuilder.TypedRows(rows, out int h0), h0);
            }
            for (int i = 0; i < tables.Count; i++)
            {
                var typed = XlsxBuilder.TypedRows(tables[i], out int head);
                xlsx.AddSheet(tables.Count == 1 ? L.T("Tabella", "Table") : L.T($"Tabella {i + 1}", $"Table {i + 1}"), typed, head);
            }
            string outp = OutPath(j.Source, "xlsx");
            xlsx.Save(outp);
            return outp;
        }

        static List<List<string[]>> HtmlTables(string html)
        {
            var tables = new List<List<string[]>>();
            foreach (Match t in Regex.Matches(html, @"<table[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var rows = new List<string[]>();
                foreach (Match tr in Regex.Matches(t.Groups[1].Value, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var cells = Regex.Matches(tr.Groups[1].Value, @"<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                        .Select(m => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(m.Groups[1].Value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase), "<[^>]+>", "")).Trim())
                        .ToArray();
                    if (cells.Length > 0) rows.Add(cells);
                }
                if (rows.Count > 0) tables.Add(rows);
            }
            return tables;
        }

        static string HtmlToXlsx(Job j)
        {
            var tables = HtmlTables(ReadText(j.Source));
            var xlsx = new XlsxBuilder();
            if (tables.Count == 0)
            {
                var rows = StripHtml(ReadText(j.Source)).Split('\n').Where(l => l.Trim().Length > 0).Select(l => new[] { l.Trim() }).ToList();
                xlsx.AddSheet(L.T("Testo", "Text"), XlsxBuilder.TypedRows(rows, out int h0), h0);
            }
            for (int i = 0; i < tables.Count; i++)
            {
                var typed = XlsxBuilder.TypedRows(tables[i], out int head);
                xlsx.AddSheet(tables.Count == 1 ? L.T("Tabella", "Table") : L.T($"Tabella {i + 1}", $"Table {i + 1}"), typed, head);
            }
            string outp = OutPath(j.Source, "xlsx");
            xlsx.Save(outp);
            return outp;
        }

        static string CsvToXlsx(Job j)
        {
            var rows = ParseCsv(ReadText(j.Source));
            var xlsx = new XlsxBuilder();
            var typed = XlsxBuilder.TypedRows(rows, out int head);
            xlsx.AddSheet(Path.GetFileNameWithoutExtension(j.Source), typed, head);
            string outp = OutPath(j.Source, "xlsx");
            xlsx.Save(outp);
            return outp;
        }

        static string CsvToDocx(Job j)
        {
            var docx = new DocxBuilder();
            docx.Paragraph(Path.GetFileNameWithoutExtension(j.Source), DocxBuilder.H1);
            docx.Table(ParseCsv(ReadText(j.Source)), true);
            string outp = OutPath(j.Source, "docx");
            docx.Save(outp);
            return outp;
        }

        static string XlsxToCsv(Job j)
        {
            var rows = XlsxReader.ReadFirstSheet(j.Source);
            var sb = new StringBuilder();
            foreach (var r in rows)
                sb.Append(string.Join(";", r.Select(c => c.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))).Append("\r\n");
            string outp = OutPath(j.Source, "csv");
            File.WriteAllText(outp, sb.ToString(), new UTF8Encoding(true)); // with BOM: Excel opens it with the right accented characters
            return outp;
        }

        static string XlsxToJson(Job j)
        {
            var rows = XlsxReader.ReadFirstSheet(j.Source).Where(r => r.Any(c => c.Length > 0)).ToList();
            var list = new List<Dictionary<string, string>>();
            if (rows.Count > 0)
            {
                var head = rows[0];
                foreach (var r in rows.Skip(1))
                {
                    var d = new Dictionary<string, string>();
                    for (int i = 0; i < Math.Max(head.Length, r.Length); i++)
                    {
                        string key = i < head.Length && head[i].Trim().Length > 0 ? head[i].Trim() : $"col{i + 1}";
                        while (d.ContainsKey(key)) key += "_";
                        d[key] = i < r.Length ? r[i] : "";
                    }
                    list.Add(d);
                }
            }
            string outp = OutPath(j.Source, "json");
            WriteUtf8(outp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return outp;
        }

        static string XlsxToHtml(Job j)
        {
            var rows = XlsxReader.ReadFirstSheet(j.Source);
            var sb = new StringBuilder("<table>");
            for (int r = 0; r < rows.Count; r++)
            {
                string tag = r == 0 ? "th" : "td";
                sb.Append("<tr>").Append(string.Concat(rows[r].Select(c => $"<{tag}>{WebUtility.HtmlEncode(c)}</{tag}>"))).Append("</tr>");
            }
            sb.Append("</table>");
            string outp = OutPath(j.Source, "html");
            WriteUtf8(outp, Html(Path.GetFileNameWithoutExtension(j.Source), $"<h1>{WebUtility.HtmlEncode(Path.GetFileName(j.Source))}</h1>{sb}"));
            return outp;
        }

        static string XlsxToDocx(Job j)
        {
            var docx = new DocxBuilder();
            docx.Paragraph(Path.GetFileNameWithoutExtension(j.Source), DocxBuilder.H1);
            docx.Table(XlsxReader.ReadFirstSheet(j.Source).Where(r => r.Length > 0).ToList(), true);
            string outp = OutPath(j.Source, "docx");
            docx.Save(outp);
            return outp;
        }

        // ---------------------------------------------------------------- CSV

        internal static List<string[]> ParseCsv(string text)
        {
            string first = text.Split('\n')[0];
            char sep = new[] { ';', ',', '\t' }.OrderByDescending(c => first.Count(ch => ch == c)).First();

            var rows = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                    row = new List<string>();
                }
                else cell.Append(c);
            }
            row.Add(cell.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
            return rows;
        }

        static string CsvToHtml(Job j)
        {
            var rows = ParseCsv(ReadText(j.Source));
            var sb = new StringBuilder("<table>");
            for (int r = 0; r < rows.Count; r++)
            {
                string tag = r == 0 ? "th" : "td";
                if (r == 0) sb.Append("<thead>");
                sb.Append("<tr>");
                foreach (var c in rows[r]) sb.Append($"<{tag}>{WebUtility.HtmlEncode(c)}</{tag}>");
                sb.Append("</tr>");
                if (r == 0) sb.Append("</thead><tbody>");
            }
            sb.Append("</tbody></table>");
            string outp = OutPath(j.Source, "html");
            WriteUtf8(outp, Html(Path.GetFileNameWithoutExtension(j.Source), $"<h1>{WebUtility.HtmlEncode(Path.GetFileName(j.Source))}</h1>{sb}"));
            return outp;
        }

        static string CsvToJson(Job j)
        {
            var rows = ParseCsv(ReadText(j.Source));
            var list = new List<Dictionary<string, string>>();
            if (rows.Count > 0)
            {
                var head = rows[0];
                foreach (var r in rows.Skip(1))
                {
                    var d = new Dictionary<string, string>();
                    for (int i = 0; i < head.Length; i++)
                    {
                        string key = string.IsNullOrWhiteSpace(head[i]) ? $"col{i + 1}" : head[i];
                        while (d.ContainsKey(key)) key += "_";
                        d[key] = i < r.Length ? r[i] : "";
                    }
                    list.Add(d);
                }
            }
            string outp = OutPath(j.Source, "json");
            WriteUtf8(outp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return outp;
        }

        const string Css = @"
:root{color-scheme:light dark}
body{font-family:'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;max-width:900px;margin:48px auto;padding:0 24px;line-height:1.6;color:#1b1b1b;background:#f9f9f9}
h1,h2,h3{font-family:'Segoe UI Variable Display','Segoe UI',sans-serif;font-weight:600;line-height:1.25}
pre,code{font-family:'Cascadia Mono',Consolas,monospace;font-size:.92em}
pre{background:#fff;border:1px solid #e5e5e5;border-radius:8px;padding:16px;overflow:auto;white-space:pre-wrap}
table{border-collapse:collapse;width:100%;background:#fff;border-radius:8px;overflow:hidden;border:1px solid #e5e5e5}
th,td{padding:8px 12px;border-bottom:1px solid #eee;text-align:left}
th{background:#f3f3f3;font-weight:600}
a{color:#005fb8}
blockquote{margin:0;padding-left:16px;border-left:3px solid #005fb8;color:#555}
@media (prefers-color-scheme:dark){
 body{background:#202020;color:#f0f0f0}
 pre,table{background:#2b2b2b;border-color:#3a3a3a}
 th{background:#323232} th,td{border-color:#3a3a3a}
 a{color:#99ebff} blockquote{color:#bbb;border-color:#99ebff}
}";

        public static string HtmlPage(string title, string body) => Html(title, body);

        static string Html(string title, string body) =>
            "<!doctype html>\n<html lang=\"it\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{WebUtility.HtmlEncode(title)}</title><style>{Css}</style></head><body>\n{body}\n</body></html>";
    }

    /// <summary>Fonts for PDFsharp, read from C:\Windows\Fonts.</summary>
    internal sealed class WinFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            string f = familyName.ToLowerInvariant();
            if (f.Contains("consolas")) return new FontResolverInfo(bold ? "consolab" : "consola");
            return new FontResolverInfo(bold ? "segoeuib" : "segoeui");
        }

        public byte[] GetFont(string faceName)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string p = Path.Combine(dir, faceName + ".ttf");
            if (!File.Exists(p)) p = Path.Combine(dir, faceName.StartsWith("consola") ? "cour.ttf" : "arial.ttf");
            return File.ReadAllBytes(p);
        }
    }
}
