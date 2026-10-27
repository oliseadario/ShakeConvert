using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImageMagick;
using UglyToad.PdfPig.Content;
using WinPdf = global::Windows.Data.Pdf;
using WinStorage = global::Windows.Storage;
using WinStreams = global::Windows.Storage.Streams;
using WinImaging = global::Windows.Graphics.Imaging;
using WinOcr = global::Windows.Media.Ocr;

namespace ShakeConvert
{
    /// <summary>
    /// Rebuilds the structure of a PDF (lines, columns, headings, tables, images) to create
    /// editable Word and Excel documents. Scanned pages are read with Windows OCR.
    /// </summary>
    internal static class PdfSmart
    {
        // ------------------------------------------------------------ model

        internal sealed class PWord { public string Text; public double Left, Right, Size; public bool Bold, Italic; }
        internal sealed class PSeg { public double Left, Right; public List<PWord> Words = new List<PWord>(); public string Text => string.Join(" ", Words.Select(w => w.Text)); }
        internal sealed class PLine
        {
            public double Top, Bottom, Left, Right, Size;
            public bool Bold;
            public List<PWord> Words = new List<PWord>();
            public List<PSeg> Segs = new List<PSeg>();
            public string Text => string.Join(" ", Words.Select(w => w.Text));
        }
        internal sealed class PImage { public byte[] Data; public string Ext; public double Top, Width, Height; }
        internal sealed class PPage { public double Width, Height; public List<PLine> Lines = new List<PLine>(); public List<PImage> Images = new List<PImage>(); public bool Ocr; }

        abstract class Block { public double Top; }
        sealed class ParaBlock : Block
        {
            public int Style; public string Align;
            public List<(string text, bool bold, bool italic)> Runs = new List<(string, bool, bool)>();
            public double LastBottom, LastRight, LastSize, Left;
            public string Text => string.Concat(Runs.Select(r => r.text));
        }
        sealed class TableBlock : Block { public List<string[]> Rows = new List<string[]>(); }
        sealed class ImageBlock : Block { public PImage Image; }

        // ------------------------------------------------------------ analysis

        public static async Task<List<PPage>> Analyze(Job j, string pdfPath, bool withImages)
        {
            var pages = new List<PPage>();
            WinPdf.PdfDocument winDoc = null;
            WinOcr.OcrEngine ocr = null;
            bool ocrTried = false;

            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
            int total = Math.Max(1, doc.NumberOfPages);
            int index = 0;
            foreach (var page in doc.GetPages())
            {
                j.Token.ThrowIfCancellationRequested();
                var pp = new PPage { Width = page.Width, Height = page.Height };
                var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();

                // "photographed" page: no real text, or very little text on top of a large image
                double bigImage = 0;
                try { bigImage = page.GetImages().Select(im => im.Bounds.Width * im.Bounds.Height).DefaultIfEmpty(0).Max() / (page.Width * page.Height); } catch { }
                bool scanned = words.Count < 3 || (bigImage > 0.5 && words.Count < 40);

                if (!scanned)
                {
                    pp.Lines = BuildLines(words.Select(ToPWord).ToList());
                }
                else
                {
                    // Windows OCR: the text becomes editable
                    if (!ocrTried) { ocrTried = true; ocr = CreateOcr(); }
                    winDoc ??= await WinPdf.PdfDocument.LoadFromFileAsync(await WinStorage.StorageFile.GetFileFromPathAsync(pdfPath));
                    if (ocr != null)
                    {
                        pp.Lines = await OcrPage(winDoc, (uint)index, ocr, page.Width, page.Height);
                        pp.Ocr = true;
                    }
                    if (pp.Lines.Count == 0 && words.Count > 0) pp.Lines = BuildLines(words.Select(ToPWord).ToList());
                    if (pp.Lines.Count == 0 && withImages)
                    {
                        // no readable text: at least keep the page as an image
                        var png = await RenderPagePng(winDoc, (uint)index);
                        pp.Images.Add(new PImage { Data = png, Ext = "png", Top = page.Height, Width = page.Width, Height = page.Height });
                        pp.Ocr = true;
                    }
                }

                if (withImages && !pp.Ocr)
                {
                    try
                    {
                        foreach (var img in page.GetImages())
                        {
                            var b = img.Bounds;
                            if (b.Width < 24 || b.Height < 24) continue;
                            if (b.Width * b.Height > page.Width * page.Height * 0.85) continue; // full-page backgrounds
                            var data = ImageBytes(img);
                            if (data.data != null) pp.Images.Add(new PImage { Data = data.data, Ext = data.ext, Top = b.Top, Width = b.Width, Height = b.Height });
                        }
                    }
                    catch { }
                }

                pages.Add(pp);
                index++;
                j.Report(0.8 * index / total);
            }
            return pages;
        }

        static (PWord w, double top, double bottom) ToPWord(Word w)
        {
            var letters = w.Letters;
            double size = letters.Count > 0 ? letters.Average(l => l.PointSize) : w.BoundingBox.Height;
            if (size <= 0) size = w.BoundingBox.Height;
            string font = w.FontName ?? (letters.Count > 0 ? letters[0].FontName : "") ?? "";
            return (new PWord
            {
                Text = w.Text,
                Left = w.BoundingBox.Left,
                Right = w.BoundingBox.Right,
                Size = size,
                Bold = Regex.IsMatch(font, "bold|black|heavy|semibold|demi", RegexOptions.IgnoreCase),
                Italic = Regex.IsMatch(font, "italic|oblique", RegexOptions.IgnoreCase)
            }, w.BoundingBox.Top, w.BoundingBox.Bottom);
        }

        /// <summary>Groups words into lines (top to bottom) and lines into segments split by wide gaps (columns).</summary>
        static List<PLine> BuildLines(List<(PWord w, double top, double bottom)> items)
        {
            var lines = new List<PLine>();
            foreach (var it in items.OrderByDescending(i => i.bottom).ThenBy(i => i.w.Left))
            {
                double h = Math.Max(1, it.top - it.bottom);
                var line = lines.LastOrDefault();
                if (line != null && Math.Abs(line.Bottom - it.bottom) <= Math.Max(1.5, Math.Min(h, line.Top - line.Bottom) * 0.45))
                {
                    line.Words.Add(it.w);
                    line.Top = Math.Max(line.Top, it.top);
                }
                else lines.Add(new PLine { Top = it.top, Bottom = it.bottom, Words = new List<PWord> { it.w } });
            }

            foreach (var l in lines)
            {
                l.Words.Sort((a, b) => a.Left.CompareTo(b.Left));
                l.Left = l.Words.First().Left;
                l.Right = l.Words.Max(w => w.Right);
                l.Size = l.Words.Average(w => w.Size);
                l.Bold = l.Words.Count(w => w.Bold) * 2 > l.Words.Count;

                PSeg seg = null;
                PWord prev = null;
                foreach (var w in l.Words)
                {
                    if (seg == null || w.Left - prev.Right > Math.Max(l.Size * 1.1, 6))
                    {
                        seg = new PSeg { Left = w.Left };
                        l.Segs.Add(seg);
                    }
                    seg.Words.Add(w);
                    seg.Right = w.Right;
                    prev = w;
                }
            }
            return lines.OrderByDescending(l => l.Top).ToList();
        }

        static (byte[] data, string ext) ImageBytes(IPdfImage img)
        {
            try { if (img.TryGetPng(out var png) && png != null && png.Length > 0) return (png, "png"); } catch { }
            try
            {
                var raw = img.RawBytes.ToArray();
                if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) return (raw, "jpeg");
            }
            catch { }
            return (null, null);
        }

        // ------------------------------------------------------------ OCR (Windows.Media.Ocr)

        public static WinOcr.OcrEngine CreateOcr()
        {
            try
            {
                return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("it-IT"))
                    ?? WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
            }
            catch { return null; }
        }

        static async Task<List<PLine>> OcrPage(WinPdf.PdfDocument doc, uint index, WinOcr.OcrEngine ocr, double pageW, double pageH)
        {
            using var page = doc.GetPage(index);
            uint width = (uint)Math.Min(WinOcr.OcrEngine.MaxImageDimension, Math.Max(1, page.Size.Width * 2.5));
            using var stream = new WinStreams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = width });
            stream.Seek(0);
            var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
            using var bmp = await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
            var result = await ocr.RecognizeAsync(bmp);
            double scale = bmp.PixelWidth / pageW; // pixels per point
            return OcrToLines(result, scale, pageH);
        }

        static async Task<byte[]> RenderPagePng(WinPdf.PdfDocument doc, uint index)
        {
            using var page = doc.GetPage(index);
            using var stream = new WinStreams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * 2) });
            stream.Seek(0);
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            return ms.ToArray();
        }

        /// <summary>OCR of any image (goes through Magick.NET, so PSD, HEIC, RAW… work too).</summary>
        public static async Task<(List<PLine> lines, double width, double height)> OcrImage(string path)
        {
            var ocr = CreateOcr() ?? throw new Exception(L.T("Il riconoscimento del testo (OCR) non è disponibile: aggiungi una lingua in Impostazioni → Ora e lingua → Lingua e area geografica.", "Text recognition (OCR) is not available: add a language in Settings → Time & language → Language & region."));
            string tmp = Converters.NewTemp();
            try
            {
                string png = Path.Combine(tmp, "ocr.png");
                using (var img = new MagickImage(path))
                {
                    img.AutoOrient();
                    uint max = WinOcr.OcrEngine.MaxImageDimension;
                    if (img.Width > max || img.Height > max) img.Resize(max, max);
                    if (img.Width < 1200) img.Resize(new Percentage(200)); // small images read better when enlarged
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaOption.Remove);
                    img.Write(png, MagickFormat.Png);
                }
                var file = await WinStorage.StorageFile.GetFileFromPathAsync(png);
                using var stream = await file.OpenAsync(WinStorage.FileAccessMode.Read);
                var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
                using var bmp = await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
                var result = await ocr.RecognizeAsync(bmp);
                // coordinates in fake "points": 1 px = 0.5 pt
                double scale = 2;
                return (OcrToLines(result, scale, bmp.PixelHeight / scale), bmp.PixelWidth / scale, bmp.PixelHeight / scale);
            }
            finally { Converters.TryDelete(tmp); }
        }

        static List<PLine> OcrToLines(WinOcr.OcrResult result, double scale, double pageH)
        {
            var items = new List<(PWord w, double top, double bottom)>();
            foreach (var line in result.Lines)
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    double top = pageH - r.Y / scale, bottom = pageH - (r.Y + r.Height) / scale;
                    items.Add((new PWord { Text = w.Text, Left = r.X / scale, Right = (r.X + r.Width) / scale, Size = r.Height / scale * 0.85 }, top, bottom));
                }
            return items.Count == 0 ? new List<PLine>() : BuildLines(items);
        }

        // ------------------------------------------------------------ document structure

        static double BodySize(List<PPage> pages)
        {
            var sizes = pages.SelectMany(p => p.Lines).SelectMany(l => l.Words).Select(w => Math.Round(w.Size * 2) / 2).ToList();
            if (sizes.Count == 0) return 11;
            return sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key;
        }

        static List<Block> Structure(PPage page, double body)
        {
            var blocks = new List<Block>();
            var lines = page.Lines;
            double textLeft = lines.Count > 0 ? lines.Min(l => l.Left) : 0;
            double textRight = lines.Count > 0 ? lines.Max(l => l.Right) : page.Width;
            double textW = Math.Max(1, textRight - textLeft);

            // 1) find tables: at least 3 consecutive lines with 2+ columns
            var isTable = new bool[lines.Count];
            for (int i = 0; i < lines.Count;)
            {
                int k = i;
                while (k < lines.Count && lines[k].Segs.Count >= 2) k++;
                if (k - i >= 3) for (int t = i; t < k; t++) isTable[t] = true;
                i = Math.Max(k, i + 1);
            }

            ParaBlock cur = null;
            var images = page.Images.OrderByDescending(im => im.Top).ToList();
            int imgIdx = 0;

            void FlushImagesAbove(double y)
            {
                while (imgIdx < images.Count && images[imgIdx].Top >= y)
                {
                    cur = null;
                    blocks.Add(new ImageBlock { Image = images[imgIdx], Top = images[imgIdx].Top });
                    imgIdx++;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                FlushImagesAbove(l.Top);

                if (isTable[i])
                {
                    int k = i;
                    while (k < lines.Count && isTable[k]) k++;
                    blocks.Add(new TableBlock { Top = l.Top, Rows = ToGrid(lines.GetRange(i, k - i), body) });
                    cur = null;
                    i = k - 1;
                    continue;
                }

                string text = l.Text;
                int style = 0;
                if (l.Size >= body * 1.75) style = DocxBuilder.H1;
                else if (l.Size >= body * 1.3) style = DocxBuilder.H2;
                else if (l.Bold && l.Size >= body * 0.95 && text.Length <= 90 && !text.EndsWith(".") && l.Right - l.Left < textW * 0.85) style = DocxBuilder.H3;

                bool bullet = Regex.IsMatch(text, @"^\s*([•·▪◦‣\-–—*]|\d{1,3}[.)]|[a-zA-Z][.)])\s");
                bool newPara = cur == null || cur.Style != style
                               || (cur.LastBottom - l.Top) > Math.Max(l.Size, cur.LastSize) * 0.75   // wide vertical gap
                               || bullet
                               || (style == 0 && Regex.IsMatch(cur.Text, @"[.:!?;]$") && cur.LastRight < textRight - body * 3) // previous line short and closed
                               || (style == 0 && l.Left > cur.Left + body * 1.5);                                           // first-line indent
                if (style > 0 && cur != null && cur.Style == style && (cur.LastBottom - l.Top) < l.Size * 0.8) newPara = false;

                if (newPara)
                {
                    cur = new ParaBlock { Style = style, Top = l.Top, Left = l.Left };
                    double center = (l.Left + l.Right) / 2;
                    if (Math.Abs(center - page.Width / 2) < body * 2.5 && l.Right - l.Left < textW * 0.7 && l.Left > textLeft + body * 2) cur.Align = "center";
                    blocks.Add(cur);
                }
                else
                {
                    // join with the previous line, removing hyphenation
                    if (cur.Runs.Count > 0)
                    {
                        var last = cur.Runs[cur.Runs.Count - 1];
                        if (last.text.EndsWith("-") && l.Words.Count > 0 && l.Words[0].Text.Length > 0 && char.IsLower(l.Words[0].Text[0]))
                            cur.Runs[cur.Runs.Count - 1] = (last.text.Substring(0, last.text.Length - 1), last.bold, last.italic);
                        else cur.Runs[cur.Runs.Count - 1] = (last.text + " ", last.bold, last.italic);
                    }
                    if (cur.Align == "center" && Math.Abs((l.Left + l.Right) / 2 - page.Width / 2) > body * 2.5) cur.Align = null;
                }

                foreach (var w in l.Words)
                {
                    bool b = style == 0 && w.Bold, it = w.Italic;
                    if (cur.Runs.Count > 0 && cur.Runs[cur.Runs.Count - 1].bold == b && cur.Runs[cur.Runs.Count - 1].italic == it)
                    {
                        var last = cur.Runs[cur.Runs.Count - 1];
                        string sep = last.text.Length == 0 || last.text.EndsWith(" ") ? "" : " ";
                        cur.Runs[cur.Runs.Count - 1] = (last.text + sep + w.Text, b, it);
                    }
                    else
                    {
                        if (cur.Runs.Count > 0 && !cur.Runs[cur.Runs.Count - 1].text.EndsWith(" "))
                        {
                            var last = cur.Runs[cur.Runs.Count - 1];
                            cur.Runs[cur.Runs.Count - 1] = (last.text + " ", last.bold, last.italic);
                        }
                        cur.Runs.Add((w.Text, b, it));
                    }
                }
                cur.LastBottom = l.Bottom;
                cur.LastRight = l.Right;
                cur.LastSize = l.Size;
            }
            FlushImagesAbove(double.MinValue);
            return blocks;
        }

        /// <summary>Lines with segments → cell grid, aligning columns by horizontal position.</summary>
        static List<string[]> ToGrid(List<PLine> lines, double body)
        {
            double tol = Math.Max(body * 1.2, 8);
            var starts = lines.Where(l => l.Segs.Count >= 2).SelectMany(l => l.Segs).Select(s => s.Left).OrderBy(x => x).ToList();
            var cols = new List<double>();
            var counts = new List<int>();
            foreach (var x in starts)
            {
                if (cols.Count > 0 && x - cols[cols.Count - 1] <= tol)
                {
                    int n = counts[counts.Count - 1];
                    cols[cols.Count - 1] = (cols[cols.Count - 1] * n + x) / (n + 1);
                    counts[counts.Count - 1] = n + 1;
                }
                else { cols.Add(x); counts.Add(1); }
            }
            if (cols.Count == 0) cols.Add(0);

            // columns that are too rare (a single hit across many lines) are merged into the previous one
            for (int c = cols.Count - 1; c > 0; c--)
                if (counts[c] == 1 && lines.Count > 4) { cols.RemoveAt(c); counts.RemoveAt(c); }

            var grid = new List<string[]>();
            foreach (var l in lines)
            {
                var row = new string[cols.Count];
                foreach (var s in l.Segs)
                {
                    int best = 0;
                    for (int c = 0; c < cols.Count; c++) if (cols[c] <= s.Left + tol) best = c;
                    // right-aligned text (numbers): use the column whose end is closest
                    row[best] = string.IsNullOrEmpty(row[best]) ? s.Text : row[best] + " " + s.Text;
                }
                for (int c = 0; c < row.Length; c++) row[c] ??= "";
                grid.Add(row);
            }
            return grid;
        }

        // ------------------------------------------------------------ outputs

        public static async Task<string> ToDocx(Job j)
        {
            var pages = await Analyze(j, j.Source, true);
            double body = BodySize(pages);
            var docx = new DocxBuilder();
            for (int p = 0; p < pages.Count; p++)
            {
                if (p > 0 && !docx.IsEmpty) docx.PageBreak();
                foreach (var b in Structure(pages[p], body))
                {
                    switch (b)
                    {
                        case ParaBlock pb when pb.Runs.Count > 0:
                            docx.Paragraph(pb.Style, pb.Align, pb.Runs.Select(r => (r.text, r.bold, r.italic)));
                            break;
                        case TableBlock tb:
                            docx.Table(tb.Rows, LooksLikeHeader(tb.Rows));
                            break;
                        case ImageBlock ib:
                            docx.Image(ib.Image.Data, ib.Image.Ext, ib.Image.Width, ib.Image.Height);
                            break;
                    }
                }
            }
            j.Report(0.95);
            string outp = Converters.OutPath(j.Source, "docx");
            docx.Save(outp);
            return outp;
        }

        public static async Task<string> ToXlsx(Job j)
        {
            var pages = await Analyze(j, j.Source, false);
            double body = BodySize(pages);
            var grids = pages.Select(p => p.Lines.Count == 0 ? new List<string[]>() : ToGrid(p.Lines, body)).ToList();
            var xlsx = new XlsxBuilder();

            int cols = grids.Where(g => g.Count > 0).Select(g => g.Max(r => r.Length)).DefaultIfEmpty(0).Max();
            bool sameShape = grids.Where(g => g.Count > 0).All(g => g.Max(r => r.Length) == cols) && cols >= 2;

            if (sameShape || grids.Count(g => g.Count > 0) <= 1)
            {
                // every page has the same columns: a single sheet, repeated headers removed
                var all = new List<string[]>();
                string[] header = null;
                foreach (var g in grids)
                    foreach (var row in g)
                    {
                        if (header != null && row.SequenceEqual(header)) continue;
                        all.Add(row);
                        if (header == null && row.Count(c => c.Length > 0) >= Math.Max(2, (cols + 1) / 2) && row.All(c => c.Length == 0 || !XlsxBuilder.Typed(c).IsNum)) header = row;
                    }
                var typed = XlsxBuilder.TypedRows(all, out int head);
                xlsx.AddSheet(L.T("Dati", "Data"), typed, head);
            }
            else
            {
                for (int p = 0; p < grids.Count; p++)
                {
                    if (grids[p].Count == 0) continue;
                    var typed = XlsxBuilder.TypedRows(grids[p], out int head);
                    xlsx.AddSheet(L.T($"Pagina {p + 1}", $"Page {p + 1}"), typed, head);
                }
            }
            j.Report(0.95);
            string outp = Converters.OutPath(j.Source, "xlsx");
            xlsx.Save(outp);
            return outp;
        }

        public static async Task<string> ToText(Job j, string fmt)
        {
            var pages = await Analyze(j, j.Source, fmt == "html");
            double body = BodySize(pages);
            var sb = new StringBuilder();
            for (int p = 0; p < pages.Count; p++)
            {
                if (p > 0) sb.Append(fmt switch { "html" => "<hr>\n", "md" => "\n---\n\n", _ => "\n\f\n" });
                foreach (var b in Structure(pages[p], body))
                {
                    switch (b)
                    {
                        case ParaBlock pb when pb.Runs.Count > 0:
                            if (fmt == "html")
                            {
                                string inner = string.Concat(pb.Runs.Select(r =>
                                {
                                    string t = WebUtility.HtmlEncode(r.text);
                                    if (r.bold) t = $"<strong>{t}</strong>";
                                    if (r.italic) t = $"<em>{t}</em>";
                                    return t;
                                }));
                                string tag = pb.Style switch { 1 => "h1", 2 => "h2", 3 => "h3", _ => "p" };
                                sb.Append($"<{tag}{(pb.Align == "center" ? " style=\"text-align:center\"" : "")}>{inner}</{tag}>\n");
                            }
                            else if (fmt == "md")
                            {
                                string inner = string.Concat(pb.Runs.Select(r => r.bold && pb.Style == 0 && r.text.Trim().Length > 0 ? $"**{r.text.Trim()}** " : r.text));
                                sb.Append(pb.Style > 0 ? new string('#', pb.Style) + " " : "").Append(inner.Trim()).Append("\n\n");
                            }
                            else sb.Append(pb.Text.Trim()).Append("\n\n");
                            break;

                        case TableBlock tb:
                            if (fmt == "html")
                            {
                                sb.Append("<table>");
                                for (int r = 0; r < tb.Rows.Count; r++)
                                {
                                    string tag = r == 0 && LooksLikeHeader(tb.Rows) ? "th" : "td";
                                    sb.Append("<tr>").Append(string.Concat(tb.Rows[r].Select(c => $"<{tag}>{WebUtility.HtmlEncode(c)}</{tag}>"))).Append("</tr>");
                                }
                                sb.Append("</table>\n");
                            }
                            else if (fmt == "md")
                            {
                                for (int r = 0; r < tb.Rows.Count; r++)
                                {
                                    sb.Append("| ").Append(string.Join(" | ", tb.Rows[r].Select(c => c.Replace("|", "\\|")))).Append(" |\n");
                                    if (r == 0) sb.Append("|").Append(string.Concat(tb.Rows[r].Select(_ => " --- |"))).Append("\n");
                                }
                                sb.Append('\n');
                            }
                            else
                            {
                                foreach (var row in tb.Rows) sb.Append(string.Join("\t", row)).Append('\n');
                                sb.Append('\n');
                            }
                            break;

                        case ImageBlock ib when fmt == "html":
                            sb.Append($"<p style=\"text-align:center\"><img style=\"max-width:100%\" src=\"data:image/{ib.Image.Ext};base64,{Convert.ToBase64String(ib.Image.Data)}\"></p>\n");
                            break;
                    }
                }
            }
            string ext = fmt == "html" ? "html" : fmt == "md" ? "md" : "txt";
            string outp = Converters.OutPath(j.Source, ext);
            string content = fmt == "html" ? Converters.HtmlPage(Path.GetFileNameWithoutExtension(j.Source), sb.ToString()) : sb.ToString().Trim() + "\n";
            File.WriteAllText(outp, content, new UTF8Encoding(false));
            return outp;
        }

        /// <summary>Image → recognized text (TXT) or editable Word document (DOCX).</summary>
        public static async Task<string> ImageOcr(Job j, string fmt)
        {
            j.Report(0.1);
            var (lines, w, h) = await OcrImage(j.Source);
            j.Report(0.7);
            if (lines.Count == 0) throw new Exception(L.T("Nessun testo riconosciuto nell'immagine.", "No text recognized in the image."));
            var page = new PPage { Width = w, Height = h, Lines = lines, Ocr = true };
            double body = BodySize(new List<PPage> { page });
            var blocks = Structure(page, body);
            string outp = Converters.OutPath(j.Source, fmt);
            if (fmt == "docx")
            {
                var docx = new DocxBuilder();
                foreach (var b in blocks)
                {
                    if (b is ParaBlock pb && pb.Runs.Count > 0) docx.Paragraph(pb.Style, pb.Align, pb.Runs.Select(r => (r.text, false, false)));
                    else if (b is TableBlock tb) docx.Table(tb.Rows, LooksLikeHeader(tb.Rows));
                }
                docx.Save(outp);
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var b in blocks)
                {
                    if (b is ParaBlock pb) sb.Append(pb.Text.Trim()).Append("\n\n");
                    else if (b is TableBlock tb) { foreach (var row in tb.Rows) sb.Append(string.Join("\t", row)).Append('\n'); sb.Append('\n'); }
                }
                File.WriteAllText(outp, sb.ToString().Trim() + "\n", new UTF8Encoding(false));
            }
            return outp;
        }

        static bool LooksLikeHeader(List<string[]> rows) =>
            rows.Count > 1 && rows[0].Count(c => c.Length > 0) >= 2 && rows[0].All(c => c.Length == 0 || !XlsxBuilder.Typed(c).IsNum);
    }
}
