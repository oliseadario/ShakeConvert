using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImageMagick;
using PdfSharp.Drawing;
using SharpPdf = PdfSharp.Pdf;

namespace ShakeConvert
{
    /// <summary>
    /// Extra format families, all local:
    /// data (JSON/YAML/TOML/XML/CSV/XLSX), markup and text (pandoc), e-books and comics, subtitles, archives (7-Zip),
    /// CAD (DXF), 3D models, fonts, geodata, e-mail, vectors (LibreOffice Draw), Live Photo,
    /// rare documents/spreadsheets/presentations (LibreOffice) and extra outputs for images, PDF, audio and video.
    /// </summary>
    internal static class MoreFormats
    {
        // icons
        const string GArchive = "", GFont = "", GMap = "", GSub = "", GBook = "", GMail = "",
                     GData = "", GModel = "", GDraw = "";

        static HashSet<string> Set(params string[] e) => new HashSet<string>(e, StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> DataExt = Set(".json", ".jsonl", ".ndjson", ".yaml", ".yml", ".toml", ".xml");
        static readonly HashSet<string> MarkupExt = Set(".rst", ".tex", ".latex", ".org", ".typ", ".wiki", ".mediawiki", ".dbk", ".ipynb", ".textile", ".opml");
        static readonly HashSet<string> PandocBookExt = Set(".epub", ".fb2");
        static readonly HashSet<string> CalibreExt = Set(".mobi", ".azw", ".azw3", ".azw4", ".prc", ".kepub", ".lit", ".lrf", ".chm", ".pdb", ".pml", ".snb", ".tcr", ".oeb", ".htmlz", ".txtz", ".lrb");
        static readonly HashSet<string> ComicExt = Set(".cbz", ".cbr", ".cb7", ".cbt", ".cbc");
        static readonly HashSet<string> SubExt = Set(".srt", ".ass", ".ssa", ".vtt", ".smi", ".sami", ".lrc", ".ttml", ".dfxp", ".sub", ".scc");
        static readonly HashSet<string> ArchiveExt = Set(".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".lz", ".lzma", ".zst", ".jar", ".cab", ".deb", ".rpm", ".dmg", ".iso", ".img", ".lha", ".lzh", ".arj", ".z");
        static readonly HashSet<string> MeshExt = Set(".glb", ".gltf", ".stl", ".obj", ".3mf", ".ply", ".off");
        static readonly HashSet<string> FontExt = Set(".ttf", ".otf", ".woff", ".woff2");
        static readonly HashSet<string> GeoExt = Set(".geojson", ".kml", ".kmz", ".gpx", ".shp");
        static readonly HashSet<string> DrawExt = Set(".cdr", ".cgm", ".vsd", ".vsdx", ".vdx", ".odg", ".fodg", ".otg");
        static readonly HashSet<string> LoDocExt = Set(".pages", ".pub", ".wps", ".sdw", ".cwk", ".mcw", ".602", ".psw", ".lwp", ".abw", ".fodt", ".ott", ".hwp");
        static readonly HashSet<string> LoSheetExt = Set(".numbers", ".ets", ".dif", ".fods", ".ots", ".sdc", ".wk1", ".wks", ".123");
        static readonly HashSet<string> LoSlideExt = Set(".key", ".dps", ".sda", ".otp", ".fodp", ".sdd");
        static readonly HashSet<string> MailExt = Set(".eml", ".mht", ".mhtml");
        static readonly HashSet<string> VideoSubSources = Set(".mkv", ".mp4", ".mov", ".webm", ".m4v");

        // ------------------------------------------------------------ engines

        static string EngineDir => Path.Combine(AppContext.BaseDirectory, "engine");
        static string Python => Path.Combine(EngineDir, "python", "python.exe");
        static string Omni => Path.Combine(EngineDir, "omni.py");
        static string PandocExe => Path.Combine(EngineDir, "python", "Lib", "site-packages", "pypandoc", "files", "pandoc.exe");
        static string SevenZip => Path.Combine(AppContext.BaseDirectory, "7zip", "7z.exe");
        static string Calibre
        {
            get
            {
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                {
                    string p = Path.Combine(root ?? "", "Calibre2", "ebook-convert.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        static bool HasOmni => File.Exists(Python) && File.Exists(Omni);
        static bool HasPandoc => File.Exists(PandocExe);
        static bool Has7z => File.Exists(SevenZip);
        static bool HasLo => Office.Soffice != null;
        static bool HasFfmpeg => Media.Ffmpeg != null;

        static Dictionary<string, string> PyEnv => new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "utf-8", ["PYTHONWARNINGS"] = "ignore", ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["SHAKECONVERT_LANG"] = L.It ? "it" : "en"   // engine output (column names, file names) follows the app language
        };

        static async Task RunTool(Job j, string exe, IEnumerable<string> args, string expected, string name)
        {
            var (code, err) = await Proc.Run(exe, args, j.Token, null, null, PyEnv);
            if (code != 0 || (expected != null && !File.Exists(expected) && !Directory.Exists(expected)))
            {
                string last = Proc.LastLine(err);
                throw new Exception(L.T($"{name}: conversione non riuscita", $"{name}: conversion failed") + (last.Length > 0 ? " — " + last : "."));
            }
        }

        static Task RunOmni(Job j, string op, string src, string dst) =>
            RunTool(j, Python, new[] { Omni, op, src, dst }, dst, "Motore");

        static readonly Dictionary<string, string> PandocTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["txt"] = "plain", ["md"] = "gfm", ["wiki"] = "mediawiki", ["dbk"] = "docbook", ["tex"] = "latex",
            ["typ"] = "typst", ["adoc"] = "asciidoc", ["rst"] = "rst", ["org"] = "org", ["textile"] = "textile",
            ["ipynb"] = "ipynb", ["html"] = "html", ["docx"] = "docx", ["odt"] = "odt", ["epub"] = "epub", ["pptx"] = "pptx", ["rtf"] = "rtf"
        };
        static readonly Dictionary<string, string> PandocFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".wiki"] = "mediawiki", [".mediawiki"] = "mediawiki", [".dbk"] = "docbook", [".tex"] = "latex", [".latex"] = "latex",
            [".md"] = "markdown", [".markdown"] = "markdown", [".txt"] = "markdown", [".htm"] = "html", [".xhtml"] = "html"
        };

        static async Task Pandoc(Job j, string src, string dst, string fmt)
        {
            var args = new List<string> { src, "-s", "-o", dst, "--wrap=none" };
            if (PandocFrom.TryGetValue(Path.GetExtension(src), out var from)) { args.Add("-f"); args.Add(from); }
            if (PandocTo.TryGetValue(fmt, out var to)) { args.Add("-t"); args.Add(to); }
            if (fmt == "epub") { args.Add("--metadata"); args.Add("title=" + Path.GetFileNameWithoutExtension(src)); }
            j.Report(0.2);
            await RunTool(j, PandocExe, args, dst, "pandoc");
            j.Report(1);
        }

        // ------------------------------------------------------------ helpers

        static string Out(Job j, string fmt) => Converters.OutPath(j.Source, fmt);

        /// <summary>Name without double extensions (archive.tar.gz → archive).</summary>
        static string Stem(string path)
        {
            string n = Path.GetFileName(path);
            var m = Regex.Match(n, @"^(.*?)(\.tar)?\.[^.]+$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(n);
        }

        static string OutNamed(Job j, string fmt)
        {
            string dir = Converters.OutDirFor(j.Source), name = Stem(j.Source);
            string p = Path.Combine(dir, $"{name}.{fmt}");
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}).{fmt}");
            return p;
        }

        static string UniqueDir(Job j, string name)
        {
            string dir = Converters.OutDirFor(j.Source);
            string p = Path.Combine(dir, name);
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i})");
            return p;
        }

        static async Task<string> WithTemp(Func<string, Task<string>> work)
        {
            string tmp = Converters.NewTemp();
            try { return await work(tmp); }
            finally { Converters.TryDelete(tmp); }
        }

        /// <summary>HTML → PDF: LibreOffice or Word if available (real layout), otherwise a text PDF.</summary>
        static async Task HtmlFileToPdf(Job j, string html, string pdf)
        {
            if (HasLo) await Office.LoConvert(new Job { Source = html, Token = j.Token }, Office.Kind.Doc, "pdf", pdf);
            else if (Office.HasWord) await Converters.RunSta(() => { Office.MsConvert(Office.Kind.Doc, html, pdf, "pdf"); return 0; });
            else Converters.TextToPdf(Converters.StripHtml(Converters.ReadText(html)), pdf, "Segoe UI");
        }

        static void WriteCsv(IEnumerable<string[]> rows, string path)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
                sb.Append(string.Join(",", r.Select(c => (c ?? "").IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void CsvToStyledXlsx(string csv, string xlsx, string sheet)
        {
            var rows = Converters.ParseCsv(Converters.ReadText(csv));
            var b = new XlsxBuilder();
            b.AddSheet(sheet, XlsxBuilder.TypedRows(rows, out int head), head);
            b.Save(xlsx);
        }

        static readonly Regex Natural = new Regex(@"\d+");
        static string NaturalKey(string s) => Natural.Replace(s.ToLowerInvariant(), m => m.Value.PadLeft(10, '0'));

        /// <summary>One PDF page per image, at the image's size (comics, collections).</summary>
        static void ImagesToPdf(IEnumerable<string> images, string pdf)
        {
            var doc = new SharpPdf.PdfDocument();
            foreach (var path in images)
            {
                using var img = new MagickImage(path);
                img.AutoOrient();
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
                byte[] bytes = img.ToByteArray(MagickFormat.Jpeg);
                double w = img.Width * 0.75, h = img.Height * 0.75; // 96 dpi → points
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(w);
                page.Height = XUnit.FromPoint(h);
                using var ms = new MemoryStream(bytes, 0, bytes.Length, false, true);
                using var g = XGraphics.FromPdfPage(page);
                using var x = XImage.FromStream(ms);
                g.DrawImage(x, 0, 0, w, h);
            }
            if (doc.PageCount == 0) throw new Exception(L.T("Nessuna immagine trovata.", "No images found."));
            doc.Save(pdf);
        }

        static IEnumerable<string> ImagesIn(string dir) =>
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => Regex.IsMatch(f, @"\.(jpe?g|png|webp|gif|bmp|tiff?|avif|jxl)$", RegexOptions.IgnoreCase))
                     .OrderBy(f => NaturalKey(Path.GetRelativePath(dir, f)));

        static async Task Extract7z(Job j, string archive, string dir)
        {
            Directory.CreateDirectory(dir);
            await RunTool(j, SevenZip, new[] { "x", archive, "-o" + dir, "-y", "-bso0", "-bsp0" }, null, "7-Zip");
        }

        static async Task Pack7z(Job j, string dir, string outFile, string type)
        {
            if (type == "tgz")
            {
                string tar = Path.Combine(Path.GetDirectoryName(dir), "pack.tar");
                await RunTool(j, SevenZip, new[] { "a", "-ttar", tar, Path.Combine(dir, "*"), "-bso0", "-bsp0" }, tar, "7-Zip");
                await RunTool(j, SevenZip, new[] { "a", "-tgzip", outFile, tar, "-bso0", "-bsp0" }, outFile, "7-Zip");
            }
            else await RunTool(j, SevenZip, new[] { "a", "-t" + type, outFile, Path.Combine(dir, "*"), "-mx=7", "-bso0", "-bsp0" }, outFile, "7-Zip");
        }

        // ------------------------------------------------------------ catalog: new families

        /// <summary>Returns true if the format is handled entirely here.</summary>
        public static bool Catalog(string ext, string path, Action<string, string, Func<Job, Task<string>>> add)
        {
            // --- structured data
            if (DataExt.Contains(ext) && HasOmni)
            {
                foreach (var t in new[] { "json", "yaml", "toml", "xml", "csv", "tsv", "jsonl" })
                    add(t, GData, j => RunOmniOut(j, "data", t));
                add("xlsx", Converters.GTable, j => DataToXlsx(j));
                if (ext == ".xml" || ext == ".json")
                {
                    // the "as text" conversions stay available too
                    add("pdf", Converters.GPdf, j => Task.Run(() => { string o = Out(j, "pdf"); Converters.TextToPdf(Converters.ReadText(j.Source), o, "Consolas"); return o; }));
                    add("txt", Converters.GText, j => Task.Run(() => { string o = Out(j, "txt"); File.Copy(j.Source, o); return o; }));
                }
                return true;
            }
            if (ext == ".vcf" && HasOmni)
            {
                add("csv", Converters.GTable, j => RunOmniOut(j, "vcard", "csv"));
                add("xlsx", Converters.GTable, j => WithTemp(async tmp =>
                {
                    string csv = Path.Combine(tmp, "rubrica.csv");
                    await RunOmni(j, "vcard", j.Source, csv);
                    string o = Out(j, "xlsx"); CsvToStyledXlsx(csv, o, L.T("Contatti", "Contacts")); return o;
                }));
                add("json", GData, j => RunOmniOut(j, "vcard", "json"));
                return true;
            }

            // --- markup and text (pandoc)
            if (MarkupExt.Contains(ext) && HasPandoc)
            {
                foreach (var t in new[] { "docx", "pdf", "html", "md", "epub", "odt", "txt", "pptx", "rst", "tex", "org", "typ", "wiki", "dbk", "textile", "ipynb" })
                    if ("." + t != ext) add(t, Converters.GlyphForTarget(t), j => PandocOut(j, t));
                return true;
            }

            // --- e-book
            if (PandocBookExt.Contains(ext) && (HasPandoc || Calibre != null))
            {
                foreach (var t in new[] { "pdf", "docx", "epub", "txt", "html", "md", "odt", "mobi", "azw3", "kepub" })
                {
                    if ("." + t == ext) continue;
                    bool calibreOnly = t == "mobi" || t == "azw3" || t == "kepub";
                    if (calibreOnly && Calibre == null) continue;
                    add(t, t == "epub" || calibreOnly ? GBook : Converters.GlyphForTarget(t), j => EbookOut(j, t));
                }
                return true;
            }
            if (CalibreExt.Contains(ext))
            {
                if (Calibre == null) return true; // without Calibre they can't be read: no entries
                foreach (var t in new[] { "epub", "pdf", "docx", "txt", "html", "mobi", "azw3" })
                    if ("." + t != ext) add(t, GBook, j => CalibreOut(j, t));
                return true;
            }

            // --- comics
            if (ComicExt.Contains(ext) && (ext == ".cbz" || Has7z))
            {
                add("pdf", Converters.GPdf, j => ComicToPdf(j));
                if (ext != ".cbz") add("cbz", GBook, j => ArchiveOut(j, "zip", "cbz"));
                add("zip", GArchive, j => ArchiveOut(j, "zip", "zip"));
                return true;
            }

            // --- subtitles
            if (SubExt.Contains(ext) && HasFfmpeg)
            {
                foreach (var t in new[] { "srt", "ass", "vtt", "ttml", "lrc" })
                    if ("." + t != ext) add(t, GSub, j => Media.Ffmpeg2(j, Out(j, t), new string[0], new string[0]));
                return true;
            }

            // --- archives
            if (ArchiveExt.Contains(ext) && Has7z)
            {
                add("dir", GArchive, j => ArchiveExtract(j));
                foreach (var t in new[] { "zip", "7z", "tar", "tgz" })
                    if ("." + t != ext) add(t, GArchive, j => ArchiveOut(j, t == "tgz" ? "tgz" : t, t == "tgz" ? "tar.gz" : t));
                if (ext == ".zip" && ZipHasShapefile(path) && HasOmni)
                    foreach (var t in new[] { "geojson", "kml", "gpx" }) add(t, GMap, j => RunOmniOut(j, "geo", t));
                return true;
            }

            // --- CAD, 3D, font, geo
            if (ext == ".dxf" && HasOmni)
            {
                foreach (var t in new[] { "pdf", "svg", "png" }) add(t, Converters.GlyphForTarget(t), j => RunOmniOut(j, "cad", t));
                return true;
            }
            if (MeshExt.Contains(ext) && HasOmni)
            {
                foreach (var t in new[] { "glb", "stl", "obj", "3mf", "ply", "gltf" })
                    if ("." + t != ext) add(t, GModel, j => RunOmniOut(j, "mesh", t));
                return true;
            }
            if (FontExt.Contains(ext) && HasOmni)
            {
                foreach (var t in new[] { "ttf", "woff2", "woff", "otf" })
                {
                    if ("." + t == ext) continue;
                    if (t == "otf" && ext == ".ttf") continue; // TTF → OTF would require redrawing the outlines
                    add(t, GFont, j => RunOmniOut(j, "font", t));
                }
                return true;
            }
            if (GeoExt.Contains(ext) && HasOmni)
            {
                foreach (var t in new[] { "geojson", "kml", "kmz", "gpx", "csv", "zip" })
                    if ("." + t != ext) add(t == "zip" ? "shp" : t, GMap, j => RunOmniOut(j, "geo", t));
                add("xlsx", Converters.GTable, j => WithTemp(async tmp =>
                {
                    string csv = Path.Combine(tmp, "geo.csv");
                    await RunOmni(j, "geo", j.Source, csv);
                    string o = Out(j, "xlsx"); CsvToStyledXlsx(csv, o, L.T("Luoghi", "Places")); return o;
                }));
                return true;
            }

            // --- e-mail and saved web pages
            if (MailExt.Contains(ext) && HasOmni)
            {
                add("pdf", Converters.GPdf, j => WithTemp(async tmp =>
                {
                    string html = Path.Combine(tmp, "mail.html");
                    await RunOmni(j, "eml", j.Source, html);
                    string o = Out(j, "pdf"); await HtmlFileToPdf(j, html, o); return o;
                }));
                add("html", Converters.GWeb, j => RunOmniOut(j, "eml", "html"));
                add("txt", Converters.GText, j => RunOmniOut(j, "eml", "txt"));
                if (HasPandoc) add("docx", Converters.GDoc, j => WithTemp(async tmp =>
                {
                    string html = Path.Combine(tmp, "mail.html");
                    await RunOmni(j, "eml", j.Source, html);
                    string o = Out(j, "docx"); await Pandoc(j, html, o, "docx"); return o;
                }));
                return true;
            }

            // --- CorelDRAW, Visio, CGM, ODG vectors (LibreOffice Draw)
            if (DrawExt.Contains(ext) && HasLo)
            {
                foreach (var t in new[] { "pdf", "svg", "png" })
                    add(t, Converters.GlyphForTarget(t), j => LoOut(j, Office.Kind.Draw, t));
                return true;
            }

            // --- Live Photo (image + video)
            if (ext == ".livp")
            {
                foreach (var t in new[] { "jpg", "png" })
                    add(t, Converters.GlyphForTarget(t), j => LivePhoto(j, t));
                if (HasFfmpeg) foreach (var t in new[] { "mp4", "mov", "gif" }) add(t, Converters.GlyphForTarget(t), j => LivePhoto(j, t));
                return true;
            }

            // --- documents, spreadsheets and presentations only LibreOffice can open
            if (LoDocExt.Contains(ext) && HasLo)
            {
                foreach (var t in new[] { "pdf", "docx", "odt", "txt", "html" })
                    add(t, t == "pdf" ? Converters.GPdf : t == "txt" ? Converters.GText : t == "html" ? Converters.GWeb : Converters.GDoc, j => LoOut(j, Office.Kind.Doc, t));
                return true;
            }
            if (LoSheetExt.Contains(ext) && HasLo)
            {
                foreach (var t in new[] { "pdf", "xlsx", "ods", "csv" }) add(t, t == "pdf" ? Converters.GPdf : Converters.GTable, j => LoOut(j, Office.Kind.Sheet, t));
                return true;
            }
            if (LoSlideExt.Contains(ext) && HasLo)
            {
                foreach (var t in new[] { "pdf", "pptx", "odp" }) add(t, t == "pdf" ? Converters.GPdf : Converters.GSlides, j => LoOut(j, Office.Kind.Slide, t));
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ catalog: extra outputs for existing families

        public static void Extend(string ext, string path, Action<string, string, Func<Job, Task<string>>> add,
                                  ISet<string> imageExt, ISet<string> videoExt, ISet<string> audioExt)
        {
            if (imageExt.Contains(ext))
            {
                if (HasOmni) add("heic", Converters.GImage, j => ImageToHeic(j));
                foreach (var t in new[] { "jxl", "jp2", "tga", "qoi" }) add(t, Converters.GImage, j => Converters.ImageTo(j, t));
                if (HasOmni) add("svg", GDraw, j => TraceToSvg(j));
                if ((ext == ".gif" || ext == ".apng") && HasFfmpeg)
                {
                    add("mp4", Converters.GVideo, j => Media.Convert(j, "mp4"));
                    add("webm", Converters.GVideo, j => Media.Convert(j, "webm"));
                }
            }
            if (ext == ".pdf")
            {
                add("epub", GBook, j => PdfToEpub(j));
                add("tiff", Converters.GPhoto, j => PdfToTiff(j));
                if (HasOmni) add("svg", GDraw, j => RunOmniOut(j, "pdfsvg", "svg"));
                add("cbz", GBook, j => PdfToCbz(j));
            }
            if (videoExt.Contains(ext) && HasFfmpeg)
            {
                add("apng", Converters.GPhoto, j => Media.Ffmpeg2(j, Out(j, "apng"), new string[0],
                    new[] { "-vf", "fps=12,scale='min(480,iw)':-2:flags=lanczos", "-plays", "0", "-f", "apng" }));
                add("jpg", Converters.GImage, j => Media.Ffmpeg2(j, Out(j, "jpg"), new string[0], new[] { "-vf", "thumbnail=120", "-frames:v", "1", "-q:v", "2" }, L.T("Fotogramma", "Frame")));
                add("png", Converters.GPhoto, j => Media.Ffmpeg2(j, Out(j, "png"), new string[0], new[] { "-vf", "thumbnail=120", "-frames:v", "1" }, L.T("Fotogramma", "Frame")));
                if (VideoSubSources.Contains(ext))
                    foreach (var t in new[] { "srt", "ass", "vtt" })
                        add(t, GSub, j => Media.Ffmpeg2(j, Out(j, t), new string[0], new[] { "-map", "0:s:0" }, L.T("Sottotitoli", "Subtitles")));
            }
            if (audioExt.Contains(ext) && HasFfmpeg)
            {
                foreach (var t in new[] { "wv", "tta", "w64", "ac3", "eac3" })
                    if ("." + t != ext) add(t, Converters.GAudio, j => Media.Convert(j, t));
            }
            if (ext == ".csv" && HasOmni)
            {
                foreach (var t in new[] { "yaml", "xml", "tsv", "jsonl", "toml" }) add(t, GData, j => RunOmniOut(j, "data", t));
                add("vcf", GMail, j => RunOmniOut(j, "vcard", "vcf"));
                foreach (var t in new[] { "geojson", "kml", "gpx" }) add(t, GMap, j => RunOmniOut(j, "geo", t));
            }
            if (ext == ".tsv")
            {
                add("xlsx", Converters.GTable, j => Task.Run(() => { string o = Out(j, "xlsx"); CsvToStyledXlsx(j.Source, o, Stem(j.Source)); return o; }));
                if (HasOmni) foreach (var t in new[] { "csv", "json", "yaml", "xml" }) add(t, GData, j => RunOmniOut(j, "data", t));
            }
            if ((ext == ".xlsx" || ext == ".xlsm") && HasOmni)
            {
                foreach (var t in new[] { "yaml", "xml", "tsv", "toml" }) add(t, GData, j => XlsxToData(j, t));
                add("vcf", GMail, j => WithTemp(async tmp =>
                {
                    string csv = Path.Combine(tmp, "dati.csv");
                    WriteCsv(XlsxReader.ReadFirstSheet(j.Source), csv);
                    string o = Out(j, "vcf"); await RunOmni(j, "vcard", csv, o); return o;
                }));
            }
            if (HasPandoc)
            {
                if (ext == ".docx" || ext == ".odt" || ext == ".html" || ext == ".htm" || ext == ".md" || ext == ".markdown" || ext == ".txt" || ext == ".rtf")
                {
                    foreach (var t in new[] { "epub", "odt", "rst", "tex", "org", "typ", "wiki", "dbk", "textile", "ipynb" })
                        if ("." + t != ext) add(t, t == "epub" ? GBook : Converters.GCode, j => PandocOut(j, t));
                    if (ext == ".md" || ext == ".markdown") add("pptx", Converters.GSlides, j => PandocOut(j, "pptx"));
                }
            }
            // any file: compress it to ZIP (last in the list)
            if (!ArchiveExt.Contains(ext))
                add("zip", GArchive, j => Task.Run(() =>
                {
                    string o = Out(j, "zip");
                    using var z = ZipFile.Open(o, ZipArchiveMode.Create);
                    z.CreateEntryFromFile(j.Source, Path.GetFileName(j.Source), CompressionLevel.Optimal);
                    return o;
                }));
        }

        public static string Glyph(string ext)
        {
            if (ArchiveExt.Contains(ext) || ComicExt.Contains(ext)) return GArchive;
            if (FontExt.Contains(ext)) return GFont;
            if (GeoExt.Contains(ext)) return GMap;
            if (SubExt.Contains(ext)) return GSub;
            if (PandocBookExt.Contains(ext) || CalibreExt.Contains(ext)) return GBook;
            if (MailExt.Contains(ext) || ext == ".vcf") return GMail;
            if (MeshExt.Contains(ext)) return GModel;
            if (DrawExt.Contains(ext) || ext == ".dxf") return GDraw;
            if (DataExt.Contains(ext) || MarkupExt.Contains(ext)) return GData;
            return null;
        }

        // ------------------------------------------------------------ implementations

        static async Task<string> RunOmniOut(Job j, string op, string fmt)
        {
            string o = fmt == "zip" ? OutNamed(j, "zip") : Out(j, fmt);
            j.Report(0.2);
            await RunOmni(j, op, j.Source, o);
            j.Report(1);
            return o;
        }

        static async Task<string> PandocOut(Job j, string fmt)
        {
            string o = Out(j, fmt);
            if (fmt == "pdf")
                return await WithTemp(async tmp =>
                {
                    string html = Path.Combine(tmp, Stem(j.Source) + ".html");
                    await Pandoc(j, j.Source, html, "html");
                    await HtmlFileToPdf(j, html, o);
                    return o;
                });
            await Pandoc(j, j.Source, o, fmt);
            return o;
        }

        static async Task<string> DataToXlsx(Job j) => await WithTemp(async tmp =>
        {
            string csv = Path.Combine(tmp, "dati.csv");
            await RunOmni(j, "data", j.Source, csv);
            string o = Out(j, "xlsx"); CsvToStyledXlsx(csv, o, Stem(j.Source)); return o;
        });

        static async Task<string> XlsxToData(Job j, string fmt) => await WithTemp(async tmp =>
        {
            string csv = Path.Combine(tmp, "dati.csv");
            WriteCsv(XlsxReader.ReadFirstSheet(j.Source), csv);
            string o = Out(j, fmt); await RunOmni(j, "data", csv, o); return o;
        });

        static async Task<string> EbookOut(Job j, string fmt)
        {
            if (Calibre != null && (fmt == "mobi" || fmt == "azw3" || fmt == "kepub" || fmt == "pdf"))
                return await CalibreOut(j, fmt);
            if (fmt == "pdf")
                return await WithTemp(async tmp =>
                {
                    string html = Path.Combine(tmp, Stem(j.Source) + ".html");
                    await Pandoc(j, j.Source, html, "html");
                    string o = Out(j, "pdf"); await HtmlFileToPdf(j, html, o); return o;
                });
            string outp = Out(j, fmt);
            await Pandoc(j, j.Source, outp, fmt);
            return outp;
        }

        static async Task<string> CalibreOut(Job j, string fmt)
        {
            string o = fmt == "kepub" ? OutNamed(j, "kepub.epub") : Out(j, fmt);
            j.Report(0.1);
            await RunTool(j, Calibre, new[] { j.Source, o }, o, "Calibre");
            j.Report(1);
            return o;
        }

        static async Task<string> LoOut(Job j, Office.Kind kind, string fmt)
        {
            if (!HasLo) throw new Exception(L.T("Per questo formato serve LibreOffice (installabile dalle impostazioni).", "This format needs LibreOffice (you can install it from settings)."));
            string o = Out(j, fmt);
            j.Report(0.1);
            await Office.LoConvert(j, kind, fmt, o);
            j.Report(1);
            return o;
        }

        static async Task<string> ComicToPdf(Job j) => await WithTemp(async tmp =>
        {
            string dir = Path.Combine(tmp, "pagine");
            if (Path.GetExtension(j.Source).Equals(".cbz", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(j.Source, dir);
            else await Extract7z(j, j.Source, dir);
            j.Report(0.4);
            string o = Out(j, "pdf");
            await Task.Run(() => ImagesToPdf(ImagesIn(dir), o));
            return o;
        });

        static async Task<string> ArchiveExtract(Job j)
        {
            string dir = UniqueDir(j, Stem(j.Source));
            j.Report(0.1);
            if (Path.GetExtension(j.Source).Equals(".zip", StringComparison.OrdinalIgnoreCase) && !Has7z) ZipFile.ExtractToDirectory(j.Source, dir);
            else await Extract7z(j, j.Source, dir);
            j.Report(1);
            return dir;
        }

        static async Task<string> ArchiveOut(Job j, string type, string ext) => await WithTemp(async tmp =>
        {
            string dir = Path.Combine(tmp, "contenuto");
            if (Path.GetExtension(j.Source).Equals(".cbz", StringComparison.OrdinalIgnoreCase) || (Path.GetExtension(j.Source).Equals(".zip", StringComparison.OrdinalIgnoreCase) && !Has7z))
                ZipFile.ExtractToDirectory(j.Source, dir);
            else await Extract7z(j, j.Source, dir);
            j.Report(0.5);
            string o = OutNamed(j, ext);
            if (type == "zip" && !Has7z) ZipFile.CreateFromDirectory(dir, o, CompressionLevel.Optimal, false);
            else await Pack7z(j, dir, o, type);
            return o;
        });

        static bool ZipHasShapefile(string path)
        {
            try { using var z = ZipFile.OpenRead(path); return z.Entries.Any(e => e.Name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        static async Task<string> TraceToSvg(Job j) => await WithTemp(async tmp =>
        {
            string png = Path.Combine(tmp, "immagine.png");
            await Task.Run(() =>
            {
                using var img = new MagickImage(j.Source);
                img.AutoOrient();
                if (img.Width > 2000 || img.Height > 2000) img.Resize(2000, 2000);
                img.Write(png, MagickFormat.Png);
            });
            string o = Out(j, "svg");
            await RunOmni(j, "trace", png, o);
            return o;
        });

        /// <summary>Any image → HEIC (pillow-heif HEVC encoder), at the quality chosen in settings.</summary>
        static async Task<string> ImageToHeic(Job j) => await WithTemp(async tmp =>
        {
            string png = Path.Combine(tmp, "immagine.png");
            await Task.Run(() =>
            {
                using var img = new MagickImage(j.Source);
                img.AutoOrient();
                img.Write(png, MagickFormat.Png);
            });
            j.Report(0.4);
            string o = Out(j, "heic");
            await RunTool(j, Python, new[] { Omni, "heic", png, o, Converters.Quality.ToString(CultureInfo.InvariantCulture) }, o, "HEIC");
            j.Report(1);
            return o;
        });

        static async Task<string> PdfToEpub(Job j)
        {
            if (!HasPandoc) throw new Exception(L.T("Componente pandoc non trovato.", "pandoc component not found."));
            return await WithTemp(async tmp =>
            {
                string copy = Path.Combine(tmp, Path.GetFileName(j.Source));
                File.Copy(j.Source, copy);
                var inner = new Job { Source = copy, Token = j.Token, Progress = new Progress<double>(v => j.Report(v * 0.7)) };
                string html = await PdfSmart.ToText(inner, "html");  // structure (headings, paragraphs, tables, images)
                string o = Out(j, "epub");
                await Pandoc(j, html, o, "epub");
                return o;
            });
        }

        static async Task<string> PdfToTiff(Job j) => await WithTemp(async tmp =>
        {
            string copy = Path.Combine(tmp, "doc.pdf");
            File.Copy(j.Source, copy);
            var inner = new Job { Source = copy, Token = j.Token, Progress = new Progress<double>(v => j.Report(v * 0.8)) };
            string pages = await Converters.PdfToImages(inner, copy, "png");
            var files = Directory.Exists(pages) ? Directory.GetFiles(pages, "*.png").OrderBy(NaturalKey).ToArray() : new[] { pages };
            string o = Out(j, "tiff");
            await Task.Run(() =>
            {
                using var col = new MagickImageCollection();
                foreach (var f in files) col.Add(new MagickImage(f));
                foreach (var im in col) im.Settings.Compression = CompressionMethod.LZW;
                col.Write(o, MagickFormat.Tiff);
            });
            return o;
        });

        static async Task<string> PdfToCbz(Job j) => await WithTemp(async tmp =>
        {
            string copy = Path.Combine(tmp, "doc.pdf");
            File.Copy(j.Source, copy);
            var inner = new Job { Source = copy, Token = j.Token, Progress = new Progress<double>(v => j.Report(v * 0.9)) };
            string pages = await Converters.PdfToImages(inner, copy, "jpg");
            var files = Directory.Exists(pages) ? Directory.GetFiles(pages, "*.jpg").OrderBy(NaturalKey).ToArray() : new[] { pages };
            string o = Out(j, "cbz");
            using (var z = ZipFile.Open(o, ZipArchiveMode.Create))
                for (int i = 0; i < files.Length; i++) z.CreateEntryFromFile(files[i], $"{i + 1:D3}.jpg", CompressionLevel.NoCompression);
            return o;
        });

        static async Task<string> LivePhoto(Job j, string fmt) => await WithTemp(async tmp =>
        {
            ZipFile.ExtractToDirectory(j.Source, tmp, true);
            var all = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories);
            if (fmt == "mp4" || fmt == "mov" || fmt == "gif")
            {
                string mov = all.FirstOrDefault(f => Regex.IsMatch(f, @"\.(mov|mp4)$", RegexOptions.IgnoreCase)) ?? throw new Exception(L.T("Video non trovato nella Live Photo.", "No video found in the Live Photo."));
                var inner = new Job { Source = mov, Token = j.Token, Progress = j.Progress };
                string o = Out(j, fmt);
                string made = await Media.Convert(inner, fmt);
                File.Move(made, o);
                return o;
            }
            string img = all.FirstOrDefault(f => Regex.IsMatch(f, @"\.(heic|heif|jpe?g)$", RegexOptions.IgnoreCase)) ?? throw new Exception(L.T("Immagine non trovata nella Live Photo.", "No image found in the Live Photo."));
            return await Converters.ImageTo(j, fmt, img);
        });
    }
}
