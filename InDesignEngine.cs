using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShakeConvert
{
    /// <summary>
    /// InDesign (.indd, .indt, .idml) → PDF / JPG / PNG faithful to the original.
    /// The .indd format is proprietary: only InDesign can read it. If InDesign is installed we drive it
    /// automatically (invisibly, with no dialogs) to export the PDF;
    /// JPG and PNG are then rendered from that PDF, one image per page.
    /// </summary>
    internal static class InDesignEngine
    {
        // InDesign scripting constants
        const int ExportPdf = 1952403524;          // idExportFormat.idPDFType
        const int SaveNo = 1852776480;             // idSaveOptions.idNo
        const int NeverInteract = 1699640946;      // idUserInteractionLevels.idNeverInteract

        static readonly Lazy<Type> _type = new Lazy<Type>(() =>
        {
            try { return Type.GetTypeFromProgID("InDesign.Application"); } catch { return null; }
        });

        public static bool Available => _type.Value != null;

        public static async Task<string> Convert(Job j, string fmt)
        {
            if (fmt == "pdf")
            {
                string outp = Converters.OutPath(j.Source, "pdf");
                j.Report(0.1);
                await Converters.RunSta(() => { ExportPdfFile(j.Source, outp); return 0; });
                j.Report(1);
                return outp;
            }

            string tmp = Converters.NewTemp();
            try
            {
                string pdf = Path.Combine(tmp, "pagine.pdf");
                j.Report(0.05);
                await Converters.RunSta(() => { ExportPdfFile(j.Source, pdf); return 0; });
                j.Report(0.5);
                var inner = new Job { Source = j.Source, Token = j.Token, Progress = new Progress<double>(v => j.Report(0.5 + v * 0.5)) };
                return await Converters.PdfToImages(inner, pdf, fmt);
            }
            finally { Converters.TryDelete(tmp); }
        }

        static void ExportPdfFile(string src, string pdf)
        {
            bool wasRunning = Process.GetProcessesByName("InDesign").Length > 0;
            dynamic app = Activator.CreateInstance(_type.Value);
            try
            {
                try { app.ScriptPreferences.UserInteractionLevel = NeverInteract; } catch { } // no font or link warnings
                dynamic doc = app.Open(src, false);                                            // without opening windows
                try { doc.Export(ExportPdf, pdf); }
                finally
                {
                    try { doc.Close(SaveNo); } catch { }
                    try { Marshal.FinalReleaseComObject(doc); } catch { }
                }
            }
            finally
            {
                if (!wasRunning) { try { app.Quit(SaveNo); } catch { } }  // only quit InDesign if we started it
                try { Marshal.FinalReleaseComObject(app); } catch { }
            }
            if (!File.Exists(pdf)) throw new Exception(L.T("InDesign non ha prodotto il PDF.", "InDesign did not produce the PDF."));
        }
    }
}
