using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using ImageMagick;

namespace ShakeConvert
{
    /// <summary>
    /// "Closed" formats from graphics apps: there's no complete public reader, but almost all of them
    /// store an image of the artwork inside the file (merged or preview). We extract and convert it.
    /// - Krita (.kra), OpenRaster (.ora): merged image at full resolution
    /// - Clip Studio Paint (.clip), Affinity (.afphoto/.afdesign): canvas preview
    /// - Procreate (.procreate), Adobe XD (.xd), Sketch (.sketch), CorelDRAW (.cdr): preview
    /// - InDesign (.indd/.indt): thumbnail of the first page (low resolution)
    /// - InDesign IDML (.idml): story text → Word/TXT
    /// </summary>
    internal static class EmbeddedPreview
    {
        public static readonly HashSet<string> Ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".indd", ".indt", ".clip", ".kra", ".ora", ".procreate", ".afphoto", ".afdesign", ".xd", ".sketch", ".cdr" };

        // known images inside zip files, in order of preference (the first ones are full resolution)
        static readonly string[] ZipCandidates =
        {
            "mergedimage.png",                 // Krita, OpenRaster
            "previews/preview.png",            // Sketch
            "preview.png",                     // Adobe XD
            "QuickLook/Thumbnail.png",         // Procreate
            "previews/thumbnail.png",          // CorelDRAW
            "Thumbnails/thumbnail.png",
            "thumbnail.png",
            "Thumbnails/thumbnail.jpg",
        };

        public static async Task<string> To(Job j, string fmt)
        {
            string tmp = Converters.NewTemp();
            try
            {
                j.Report(0.1);
                var (data, ext) = await Task.Run(() => Extract(j.Source));
                string file = Path.Combine(tmp, "preview." + ext);
                File.WriteAllBytes(file, data);
                j.Report(0.4);
                return await Converters.ImageTo(j, fmt, file);
            }
            finally { Converters.TryDelete(tmp); }
        }

        public static (byte[] data, string ext) Extract(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] head = new byte[4];
            using (var fs = File.OpenRead(path)) fs.Read(head, 0, 4);
            bool isZip = head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;

            if (isZip)
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var name in ZipCandidates)
                {
                    var e = zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (e != null && e.Length > 0) return (ReadEntry(e), Path.GetExtension(e.Name).TrimStart('.'));
                }
                // otherwise the largest image in the zip
                var best = zip.Entries.Where(x => Regex.IsMatch(x.Name, @"\.(png|jpe?g|bmp)$", RegexOptions.IgnoreCase))
                                      .OrderByDescending(x => x.Length).FirstOrDefault();
                if (best != null) return (ReadEntry(best), Path.GetExtension(best.Name).TrimStart('.'));
            }

            byte[] bytes = File.ReadAllBytes(path);

            if (ext == ".indd" || ext == ".indt")
            {
                // InDesign stores page thumbnails as base64 inside the XMP metadata
                var img = InDesignThumbnail(bytes);
                if (img != null) return (img, "jpg");
            }

            // binary format: look for embedded PNG/JPEG images and keep the largest
            var found = BestEmbeddedImage(bytes);
            if (found.data != null) return found;

            throw new Exception(L.T("Questo file non contiene un'immagine di anteprima. Esportalo come PNG o PDF dal programma originale.", "This file has no embedded preview. Export it as PNG or PDF from the original app."));
        }

        static byte[] ReadEntry(ZipArchiveEntry e)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        static byte[] InDesignThumbnail(byte[] bytes)
        {
            string text = Encoding.Latin1.GetString(bytes);
            byte[] best = null;
            foreach (Match m in Regex.Matches(text, @"<xmpGImg:image>(.*?)</xmpGImg:image>", RegexOptions.Singleline))
            {
                try
                {
                    string b64 = m.Groups[1].Value.Replace("&#xA;", "").Replace("&#xa;", "").Replace("\n", "").Replace("\r", "").Trim();
                    var data = Convert.FromBase64String(b64);
                    if (best == null || data.Length > best.Length) best = data;
                }
                catch { }
            }
            return best;
        }

        static (byte[] data, string ext) BestEmbeddedImage(byte[] b)
        {
            byte[] bestData = null; string bestExt = null; long bestArea = 0;
            void Consider(byte[] data, string ext)
            {
                try
                {
                    var info = new MagickImageInfo(data);
                    long area = (long)info.Width * info.Height;
                    if (area > bestArea) { bestArea = area; bestData = data; bestExt = ext; }
                }
                catch { }
            }

            // PNG: signature 89 50 4E 47 0D 0A 1A 0A … up to the IEND chunk
            for (int i = 0; i + 8 < b.Length; i++)
            {
                if (b[i] != 0x89 || b[i + 1] != 0x50 || b[i + 2] != 0x4E || b[i + 3] != 0x47 || b[i + 4] != 0x0D || b[i + 5] != 0x0A || b[i + 6] != 0x1A || b[i + 7] != 0x0A) continue;
                int p = i + 8;
                bool ok = false;
                while (p + 12 <= b.Length)
                {
                    int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
                    if (len < 0 || p + 12L + len > b.Length) break;
                    bool iend = b[p + 4] == 'I' && b[p + 5] == 'E' && b[p + 6] == 'N' && b[p + 7] == 'D';
                    p += 12 + len;
                    if (iend) { ok = true; break; }
                }
                if (ok)
                {
                    var data = new byte[p - i];
                    Buffer.BlockCopy(b, i, data, 0, data.Length);
                    Consider(data, "png");
                    i = p - 1;
                }
            }

            // JPEG: FF D8 FF … FF D9 (tries the possible ends, at most 3)
            for (int i = 0; i + 3 < b.Length; i++)
            {
                if (b[i] != 0xFF || b[i + 1] != 0xD8 || b[i + 2] != 0xFF) continue;
                int tries = 0;
                for (int k = i + 3; k + 1 < b.Length && tries < 3; k++)
                {
                    if (b[k] != 0xFF || b[k + 1] != 0xD9) continue;
                    tries++;
                    if (k - i < 2048) continue;
                    var data = new byte[k + 2 - i];
                    Buffer.BlockCopy(b, i, data, 0, data.Length);
                    long before = bestArea;
                    Consider(data, "jpg");
                    if (bestArea != before) { i = k + 1; break; }
                }
            }
            return (bestData, bestExt);
        }

        // ------------------------------------------------------------ InDesign IDML → text

        public static string IdmlTo(Job j, string fmt)
        {
            using var zip = ZipFile.OpenRead(j.Source);
            var order = new List<string>();
            var design = zip.GetEntry("designmap.xml");
            if (design != null)
            {
                XDocument d; using (var s = design.Open()) d = XDocument.Load(s);
                order = d.Descendants().Where(e => e.Name.LocalName == "Story")
                         .Select(e => (string)e.Attribute("src")).Where(x => x != null).ToList();
            }
            if (order.Count == 0)
                order = zip.Entries.Where(e => e.FullName.StartsWith("Stories/", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName).ToList();

            var paras = new List<(int level, string text)>();
            foreach (var src in order)
            {
                var entry = zip.GetEntry(src);
                if (entry == null) continue;
                XDocument x; using (var s = entry.Open()) x = XDocument.Load(s);
                foreach (var psr in x.Descendants().Where(e => e.Name.LocalName == "ParagraphStyleRange"))
                {
                    string style = (string)psr.Attribute("AppliedParagraphStyle") ?? "";
                    int level = Regex.IsMatch(style, "(?i)heading ?1|titolo ?1|^.*/title$") ? 1
                              : Regex.IsMatch(style, "(?i)heading ?2|titolo ?2|subtitle|sottotitolo") ? 2
                              : Regex.IsMatch(style, "(?i)heading|titolo") ? 3 : 0;
                    var sb = new StringBuilder();
                    foreach (var n in psr.Descendants())
                    {
                        if (n.Name.LocalName == "Content") sb.Append(n.Value);
                        else if (n.Name.LocalName == "Br") sb.Append('\n');
                    }
                    foreach (var line in sb.ToString().Split('\n'))
                        if (line.Trim().Length > 0) paras.Add((level, line.Trim()));
                }
            }
            if (paras.Count == 0) throw new Exception(L.T("Nel file IDML non è stato trovato testo.", "No text found in the IDML file."));

            string outp = Converters.OutPath(j.Source, fmt);
            if (fmt == "docx")
            {
                var docx = new DocxBuilder();
                foreach (var (level, text) in paras) docx.Paragraph(text, level);
                docx.Save(outp);
            }
            else File.WriteAllText(outp, string.Join(Environment.NewLine + Environment.NewLine, paras.Select(p => p.text)), new UTF8Encoding(false));
            return outp;
        }
    }
}
