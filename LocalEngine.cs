using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ShakeConvert
{
    /// <summary>
    /// Local PDF reconstruction with pdf2docx (portable Python bundled with the install).
    /// No connection, no API: everything runs on the PC.
    /// - PDFs with text: faithful editable Word (paragraphs, fonts, colors, tables, images).
    /// - Scanned PDFs: Windows OCR (built-in engine), because pdf2docx can't read images.
    /// </summary>
    internal static class LocalEngine
    {
        static string Dir => Path.Combine(AppContext.BaseDirectory, "engine");
        static string Python => Path.Combine(Dir, "python", "python.exe");
        static string Script => Path.Combine(Dir, "pdfengine.py");

        static string ClipScript => Path.Combine(Dir, "clipengine.py");

        public static bool Available => File.Exists(Python) && File.Exists(Script);
        public static bool ClipAvailable => File.Exists(Python) && File.Exists(ClipScript);

        static Dictionary<string, string> Env => new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONWARNINGS"] = "ignore",
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["SHAKECONVERT_LANG"] = L.It ? "it" : "en"   // engine output (column names, file names) follows the app language
        };

        /// <summary>Runs pdfengine.py; progress arrives as "(page/total) Page".</summary>
        static Task Run(Job j, string mode, string src, string outPath, double from, double to, int passes) =>
            RunScript(j, new[] { Script, mode, src, outPath }, outPath, from, to, passes);

        static async Task RunScript(Job j, string[] args, string outPath, double from, double to, int passes)
        {
            int seen = 0;
            var rx = new Regex(@"\((\d+)/(\d+)\) Page");
            void OnErr(string line)
            {
                var m = rx.Match(line);
                if (!m.Success) return;
                int total = int.Parse(m.Groups[2].Value);
                seen++;
                j.Report(from + (to - from) * Math.Min(1.0, seen / (double)(total * passes)));
            }

            var (code, err) = await Proc.Run(Python, args, j.Token, null, OnErr, Env);
            if (code != 0 || !File.Exists(outPath))
            {
                string last = Proc.LastLine(err);
                throw new Exception(L.T("Ricostruzione non riuscita", "Reconstruction failed") + (last.Length > 0 ? ": " + last : "."));
            }
        }

        /// <summary>How many pages are "photos" (no real text)?</summary>
        static double ScannedRatio(string pdf)
        {
            try
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
                int pages = 0, scanned = 0;
                foreach (var p in doc.GetPages())
                {
                    pages++;
                    if (p.GetWords().Count(w => !string.IsNullOrWhiteSpace(w.Text)) < 3) scanned++;
                }
                return pages == 0 ? 0 : scanned / (double)pages;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------ Clip Studio Paint → full-resolution image

        /// <summary>Rebuilds every layer of the .clip and merges them; if that fails, uses the preview stored in the file.</summary>
        public static async Task<string> ClipTo(Job j, string fmt)
        {
            if (!ClipAvailable) return await EmbeddedPreview.To(j, fmt);
            string tmp = Converters.NewTemp();
            try
            {
                string png = Path.Combine(tmp, "canvas.png");
                await RunScript(j, new[] { ClipScript, j.Source, png }, png, 0.05, 0.7, 1);
                return await Converters.ImageTo(j, fmt, png);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                App.Log(ex);
                return await EmbeddedPreview.To(j, fmt);
            }
            finally { Converters.TryDelete(tmp); }
        }

        // ------------------------------------------------------------ PDF → Word

        public static async Task<string> PdfToDocx(Job j)
        {
            // scans (or PDFs that are almost all images): OCR needed → built-in engine
            if (!Available || ScannedRatio(j.Source) > 0.5) return await PdfSmart.ToDocx(j);

            string outp = Converters.OutPath(j.Source, "docx");
            try
            {
                await Run(j, "docx", j.Source, outp, 0.02, 0.98, 2);
                return outp;
            }
            catch (OperationCanceledException) { Converters.TryDelete(outp); throw; }
            catch (Exception ex)
            {
                App.Log(ex);
                Converters.TryDelete(outp);
                return await PdfSmart.ToDocx(j); // fallback: built-in engine
            }
        }

        // ------------------------------------------------------------ PDF → Excel

        public static async Task<string> PdfToXlsx(Job j)
        {
            if (!Available || ScannedRatio(j.Source) > 0.5) return await PdfSmart.ToXlsx(j);

            var tables = new List<List<string[]>>();
            string tmp = Converters.NewTemp();
            try
            {
                // 1) tables with borders
                string json = Path.Combine(tmp, "tables.json");
                await Run(j, "tables", j.Source, json, 0.02, 0.45, 1);
                using (var doc = JsonDocument.Parse(File.ReadAllText(json)))
                    foreach (var page in doc.RootElement.EnumerateArray())
                        foreach (var t in page.EnumerateArray())
                            tables.Add(t.EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString() ?? "").ToArray()).ToList());

                // 2) no bordered tables: take the ones (borderless too) rebuilt in the Word file
                if (Useful(tables).Count == 0)
                {
                    string docx = Path.Combine(tmp, "tmp.docx");
                    await Run(j, "docx", j.Source, docx, 0.45, 0.9, 2);
                    tables = Converters.DocxTables(docx);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { App.Log(ex); tables.Clear(); }
            finally { Converters.TryDelete(tmp); }

            tables = Useful(tables);
            if (tables.Count == 0) return await PdfSmart.ToXlsx(j); // 3) built-in column engine

            var xlsx = new XlsxBuilder();
            int cols = tables[0].Max(r => r.Length);
            if (tables.All(t => t.Max(r => r.Length) == cols))
            {
                // same structure on every page: a single sheet, repeated headers removed
                var header = tables[0][0];
                var all = new List<string[]>();
                foreach (var t in tables)
                    foreach (var row in t)
                        if (all.Count == 0 || !row.SequenceEqual(header)) all.Add(row);
                var typed = XlsxBuilder.TypedRows(all, out int head);
                xlsx.AddSheet(L.T("Dati", "Data"), typed, head);
            }
            else
            {
                for (int i = 0; i < tables.Count; i++)
                {
                    var typed = XlsxBuilder.TypedRows(tables[i], out int head);
                    xlsx.AddSheet(L.T($"Tabella {i + 1}", $"Table {i + 1}"), typed, head);
                }
            }
            j.Report(0.98);
            string outp = Converters.OutPath(j.Source, "xlsx");
            xlsx.Save(outp);
            return outp;
        }

        /// <summary>Drops "tables" that are just layout (bulleted lists, single cells).</summary>
        static List<List<string[]>> Useful(List<List<string[]>> tables) =>
            tables.Where(t => t.Count >= 2
                              && t.Max(r => r.Length) >= 2
                              && !t.All(r => r.Length > 0 && Regex.IsMatch(r[0].Trim(), @"^([•·▪◦\-–]|\d{1,2}[.)])?$")))
                  .Select(t =>
                  {
                      int c = t.Max(r => r.Length);
                      return t.Select(r => r.Length == c ? r : r.Concat(Enumerable.Repeat("", c - r.Length)).ToArray()).ToList();
                  })
                  .ToList();
    }
}
