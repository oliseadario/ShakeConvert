using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShakeConvert
{
    // ======================================================================== external processes

    internal static class Proc
    {
        public static async Task<(int code, string stderr)> Run(string exe, IEnumerable<string> args, CancellationToken ct,
            Action<string> onOut = null, Action<string> onErr = null, IDictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var p = new Process { StartInfo = psi };
            var err = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) onOut?.Invoke(e.Data); };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (err) { if (err.Length < 200_000) err.AppendLine(e.Data); }
                onErr?.Invoke(e.Data);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync();

            ct.ThrowIfCancellationRequested();
            return (p.ExitCode, err.ToString());
        }

        public static string LastLine(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
    }

    // ======================================================================== audio / video (FFmpeg)

    internal static class Media
    {
        static readonly Lazy<string> _ffmpeg = new Lazy<string>(() =>
        {
            string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        });

        public static string Ffmpeg => _ffmpeg.Value;

        static string[] ArgsFor(string fmt) => fmt switch
        {
            "mp4" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" },
            "mov" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k" },
            "mkv" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-c:a", "aac", "-b:a", "192k" },
            "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k" },
            "avi" => new[] { "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "3" },
            "gif" => new[] { "-vf", "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0" },
            "mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
            "wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
            "flac" => new[] { "-vn", "-c:a", "flac" },
            "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
            "aac" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
            "ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
            "opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" },
            "wma" => new[] { "-vn", "-c:a", "wmav2", "-b:a", "192k" },
            "wv" => new[] { "-vn", "-c:a", "wavpack" },
            "tta" => new[] { "-vn", "-c:a", "tta" },
            "w64" => new[] { "-vn", "-c:a", "pcm_s16le", "-f", "w64" },
            "ac3" => new[] { "-vn", "-c:a", "ac3", "-b:a", "384k" },
            "eac3" => new[] { "-vn", "-c:a", "eac3", "-b:a", "384k" },
            _ => throw new NotSupportedException(fmt)
        };

        public static Task<string> Convert(Job j, string fmt) =>
            Ffmpeg2(j, Converters.OutPath(j.Source, fmt), Array.Empty<string>(), ArgsFor(fmt));

        /// <summary>Runs FFmpeg with free-form arguments (before and after the input), with progress and cancellation.</summary>
        public static async Task<string> Ffmpeg2(Job j, string outp, string[] beforeInput, string[] afterInput, string label = null)
        {
            if (Ffmpeg == null) throw new Exception(L.T("FFmpeg non trovato. Reinstalla ShakeConvert.", "FFmpeg not found. Please reinstall ShakeConvert."));
            var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
            args.AddRange(beforeInput);
            args.AddRange(new[] { "-i", j.Source });
            args.AddRange(afterInput);
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", outp });

            double duration = 0;
            var durRx = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            void OnErr(string line)
            {
                if (duration > 0) return;
                var m = durRx.Match(line);
                if (m.Success)
                    duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                               double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            void OnOut(string line)
            {
                // out_time_us=12345678 (or out_time_ms, which is actually in microseconds)
                if (duration <= 0 || !line.StartsWith("out_time_")) return;
                int eq = line.IndexOf('=');
                if (eq > 0 && long.TryParse(line.Substring(eq + 1), out long us))
                    j.Report(us / 1_000_000.0 / duration);
            }

            try
            {
                var (code, err) = await Proc.Run(Ffmpeg, args, j.Token, OnOut, OnErr);
                if (code != 0)
                {
                    Converters.TryDelete(outp);
                    string msg = Proc.LastLine(err);
                    throw new Exception(string.IsNullOrEmpty(msg) ? L.T("FFmpeg ha restituito un errore", "FFmpeg returned an error") : msg);
                }
            }
            catch (OperationCanceledException)
            {
                Converters.TryDelete(outp);
                throw;
            }
            return outp;
        }
    }

    // ======================================================================== Office / LibreOffice

    internal static class Office
    {
        public enum Kind { Doc, Sheet, Slide, PdfImport, PdfImpress, Draw }

        static bool ProgId(string id)
        {
            try { return Type.GetTypeFromProgID(id) != null; } catch { return false; }
        }

        static readonly Lazy<bool> _word = new Lazy<bool>(() => ProgId("Word.Application"));
        static readonly Lazy<bool> _excel = new Lazy<bool>(() => ProgId("Excel.Application"));
        static readonly Lazy<bool> _ppt = new Lazy<bool>(() => ProgId("PowerPoint.Application"));

        public static bool HasWord => _word.Value;
        public static bool HasExcel => _excel.Value;
        public static bool HasPowerPoint => _ppt.Value;
        public static bool HasMsOffice => HasWord || HasExcel || HasPowerPoint;

        /// <summary>Path of soffice.exe (checked every time, so it works right after installing).</summary>
        public static string Soffice
        {
            get
            {
                foreach (var root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        public static bool CanDocs => HasWord || Soffice != null;
        public static bool CanSheets => HasExcel || Soffice != null;
        public static bool CanSlides => HasPowerPoint || Soffice != null;

        static bool UseMs(Kind k) => k switch
        {
            Kind.Doc or Kind.PdfImport => HasWord,
            Kind.Sheet => HasExcel,
            Kind.PdfImpress => false,          // PowerPoint can't open PDFs: LibreOffice only
            Kind.Draw => false,                // CorelDRAW, Visio, CGM, ODG: LibreOffice Draw
            _ => HasPowerPoint
        };

        public static async Task<string> Convert(Job j, Kind k, string fmt)
        {
            string outp = Converters.OutPath(j.Source, fmt);
            j.Report(0.1);
            if (UseMs(k))
            {
                await Converters.RunSta(() => { MsConvert(k, j.Source, outp, fmt); return 0; });
            }
            else
            {
                if (Soffice == null)
                    throw new Exception(L.T("Per questo formato serve Microsoft Office oppure LibreOffice (lo puoi installare dalle impostazioni di ShakeConvert).", "This format needs Microsoft Office or LibreOffice (you can install it from ShakeConvert settings)."));
                await LoConvert(j, k, fmt, outp);
            }
            j.Report(1);
            if (!File.Exists(outp) && !Directory.Exists(outp))
                throw new Exception(L.T("La conversione non ha prodotto alcun file.", "The conversion did not produce any file."));
            return outp;
        }

        /// <summary>Document/presentation → one image per page (through a temporary PDF).</summary>
        public static async Task<string> ToPageImages(Job j, Kind k, string fmt)
        {
            string tmp = Converters.NewTemp();
            try
            {
                string pdf = Path.Combine(tmp, Path.GetFileNameWithoutExtension(j.Source) + ".pdf");
                if (UseMs(k)) await Converters.RunSta(() => { MsConvert(k, j.Source, pdf, "pdf"); return 0; });
                else
                {
                    if (Soffice == null) throw new Exception(L.T("Serve Microsoft Office oppure LibreOffice.", "Microsoft Office or LibreOffice is required."));
                    await LoConvert(j, k, "pdf", pdf);
                }
                j.Report(0.4);
                var inner = new Job { Source = j.Source, Token = j.Token, Progress = new Progress<double>(v => j.Report(0.4 + v * 0.6)) };
                return await Converters.PdfToImages(inner, pdf, fmt);
            }
            finally { Converters.TryDelete(tmp); }
        }

        // ------------------------------------------------------------ Microsoft Office (COM)

        internal static void MsConvert(Kind k, string src, string outp, string fmt)
        {
            object app = null;
            try
            {
                switch (k)
                {
                    case Kind.Doc:
                    case Kind.PdfImport:
                        {
                            dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"));
                            app = word;
                            bool hadDocs = false;
                            try { hadDocs = word.Documents.Count > 0; } catch { }
                            try
                            {
                                word.DisplayAlerts = 0;
                                dynamic doc = word.Documents.Open(FileName: src, ConfirmConversions: false, ReadOnly: true,
                                                                  AddToRecentFiles: false, Visible: false);
                                int code = fmt switch
                                {
                                    "pdf" => 17, "docx" => 16, "doc" => 0, "odt" => 23, "rtf" => 6, "txt" => 2, "html" => 10,
                                    _ => throw new NotSupportedException(fmt)
                                };
                                if (fmt == "txt") doc.SaveAs2(FileName: outp, FileFormat: code, Encoding: 65001);
                                else doc.SaveAs2(FileName: outp, FileFormat: code);
                                doc.Close(SaveChanges: 0);
                                Marshal.FinalReleaseComObject(doc);
                            }
                            finally
                            {
                                if (!hadDocs) { try { word.Quit(SaveChanges: 0); } catch { } }
                            }
                            break;
                        }

                    case Kind.Sheet:
                        {
                            dynamic excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application"));
                            app = excel;
                            bool hadBooks = false;
                            try { hadBooks = excel.Workbooks.Count > 0; } catch { }
                            try
                            {
                                excel.DisplayAlerts = false;
                                dynamic wb = excel.Workbooks.Open(src, 0, true);
                                if (fmt == "pdf") wb.ExportAsFixedFormat(0, outp);
                                else
                                {
                                    int code = fmt switch
                                    {
                                        "xlsx" => 51, "xls" => 56, "ods" => 60, "csv" => 62, "html" => 44,
                                        _ => throw new NotSupportedException(fmt)
                                    };
                                    wb.SaveAs(outp, code);
                                }
                                wb.Close(false);
                                Marshal.FinalReleaseComObject(wb);
                            }
                            finally
                            {
                                if (!hadBooks) { try { excel.Quit(); } catch { } }
                            }
                            break;
                        }

                    case Kind.Slide:
                        {
                            dynamic ppt = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application"));
                            app = ppt;
                            bool hadPres = false;
                            try { hadPres = ppt.Presentations.Count > 0; } catch { }
                            try
                            {
                                dynamic pres = ppt.Presentations.Open(src, -1, 0, 0); // ReadOnly, Untitled=no, WithWindow=no
                                int code = fmt switch
                                {
                                    "pdf" => 32, "pptx" => 24, "ppt" => 1, "odp" => 35,
                                    _ => throw new NotSupportedException(fmt)
                                };
                                pres.SaveAs(outp, code);
                                pres.Close();
                                Marshal.FinalReleaseComObject(pres);
                            }
                            finally
                            {
                                if (!hadPres) { try { ppt.Quit(); } catch { } }
                            }
                            break;
                        }
                }
            }
            finally
            {
                if (app != null) { try { Marshal.FinalReleaseComObject(app); } catch { } }
            }
        }

        // ------------------------------------------------------------ LibreOffice (headless)

        internal static async Task LoConvert(Job j, Kind k, string fmt, string outp)
        {
            string tmp = Converters.NewTemp();
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShakeConvert", "lo-profile");
            Directory.CreateDirectory(profile);

            string filter = (k, fmt) switch
            {
                (_, "txt") => "txt:Text (encoded):UTF8",
                (Kind.Sheet, "csv") => "csv:Text - txt - csv (StarCalc):44,34,76",
                (Kind.PdfImport, "docx") => "docx:MS Word 2007 XML",
                (Kind.PdfImport, "odt") => "odt:writer8",
                (Kind.PdfImpress, "pptx") => "pptx:Impress MS PowerPoint 2007 XML",
                (Kind.Draw, "svg") => "svg:draw_svg_Export",
                (Kind.Draw, "png") => "png:draw_png_Export",
                (Kind.Draw, "pdf") => "pdf:draw_pdf_Export",
                (Kind.Doc, "doc") => "doc:MS Word 97",
                (Kind.Sheet, "xls") => "xls:MS Excel 97",
                (Kind.Slide, "ppt") => "ppt:MS PowerPoint 97",
                _ => fmt
            };

            var args = new List<string>
            {
                "-env:UserInstallation=file:///" + profile.Replace('\\', '/'),
                "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck"
            };
            if (k == Kind.PdfImport) args.Add("--infilter=writer_pdf_import");
            if (k == Kind.PdfImpress) args.Add("--infilter=impress_pdf_import");
            args.AddRange(new[] { "--convert-to", filter, "--outdir", tmp, j.Source });

            try
            {
                var (code, err) = await Proc.Run(Office.Soffice, args, j.Token);
                string produced = Directory.GetFiles(tmp).FirstOrDefault();
                if (produced == null)
                {
                    string msg = Proc.LastLine(err);
                    throw new Exception(L.T("LibreOffice non è riuscito a convertire il file.", "LibreOffice could not convert the file.") + (msg.Length > 0 ? " " + msg : ""));
                }
                File.Move(produced, outp);
            }
            finally { Converters.TryDelete(tmp); }
        }

        // ------------------------------------------------------------ LibreOffice installation

        public static async Task<bool> InstallLibreOffice(CancellationToken ct = default)
        {
            try
            {
                var (code, _) = await Proc.Run("winget.exe", new[]
                {
                    "install", "-e", "--id", "TheDocumentFoundation.LibreOffice",
                    "--silent", "--accept-package-agreements", "--accept-source-agreements"
                }, ct);
                return Soffice != null;
            }
            catch { return false; }
        }
    }
}
