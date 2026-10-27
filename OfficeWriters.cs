using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShakeConvert
{
    internal static class Xml
    {
        static readonly Regex Invalid = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F￾￿]", RegexOptions.Compiled);
        public static string Esc(string s) => SecurityElement.Escape(Invalid.Replace(s ?? "", "")) ?? "";

        public static void Entry(ZipArchive zip, string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        public static void Entry(ZipArchive zip, string name, byte[] data)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            s.Write(data, 0, data.Length);
        }
    }

    // ======================================================================== editable Word (.docx)

    /// <summary>Creates real Word documents: Heading 1-3 styles (navigation), paragraphs, bold, tables, images.</summary>
    internal sealed class DocxBuilder
    {
        public const int Normal = 0, H1 = 1, H2 = 2, H3 = 3, Code = 9;
        const double TextWidthPt = 481.9; // A4 with 2 cm margins

        readonly StringBuilder _body = new StringBuilder();
        readonly List<(string name, byte[] data)> _media = new List<(string, byte[])>();
        int _ids = 1;

        public bool IsEmpty => _body.Length == 0;

        public void Paragraph(string text, int style = Normal, string align = null) =>
            Paragraph(style, align, new[] { (text, false, false) });

        public void Paragraph(int style, string align, IEnumerable<(string text, bool bold, bool italic)> runs)
        {
            _body.Append("<w:p><w:pPr>");
            string id = style switch { H1 => "Heading1", H2 => "Heading2", H3 => "Heading3", Code => "Code", _ => null };
            if (id != null) _body.Append($"<w:pStyle w:val=\"{id}\"/>");
            if (align != null) _body.Append($"<w:jc w:val=\"{align}\"/>");
            _body.Append("</w:pPr>");
            foreach (var (text, bold, italic) in runs)
            {
                if (string.IsNullOrEmpty(text)) continue;
                _body.Append("<w:r>");
                if (bold || italic) _body.Append("<w:rPr>").Append(bold ? "<w:b/>" : "").Append(italic ? "<w:i/>" : "").Append("</w:rPr>");
                var parts = text.Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) _body.Append("<w:br/>");
                    _body.Append($"<w:t xml:space=\"preserve\">{Xml.Esc(parts[i])}</w:t>");
                }
                _body.Append("</w:r>");
            }
            _body.Append("</w:p>");
        }

        public void Table(IList<string[]> rows, bool header)
        {
            if (rows.Count == 0) return;
            int cols = Math.Max(1, rows.Max(r => r.Length));
            int colW = 9638 / cols;
            const string b = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"";
            _body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>")
                 .Append($"<w:top {b}/><w:left {b}/><w:bottom {b}/><w:right {b}/><w:insideH {b}/><w:insideV {b}/>")
                 .Append("</w:tblBorders><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"100\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            for (int c = 0; c < cols; c++) _body.Append($"<w:gridCol w:w=\"{colW}\"/>");
            _body.Append("</w:tblGrid>");
            for (int r = 0; r < rows.Count; r++)
            {
                bool head = header && r == 0;
                _body.Append(head ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>");
                for (int c = 0; c < cols; c++)
                {
                    string t = c < rows[r].Length ? rows[r][c] ?? "" : "";
                    _body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{colW}\" w:type=\"dxa\"/>")
                         .Append(head ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"DEEAF6\"/>" : "")
                         .Append("</w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:r>")
                         .Append(head ? "<w:rPr><w:b/></w:rPr>" : "")
                         .Append($"<w:t xml:space=\"preserve\">{Xml.Esc(t)}</w:t></w:r></w:p></w:tc>");
                }
                _body.Append("</w:tr>");
            }
            _body.Append("</w:tbl><w:p/>");
        }

        public void Image(byte[] data, string ext, double wPt, double hPt)
        {
            if (data == null || data.Length == 0 || wPt <= 0 || hPt <= 0) return;
            if (wPt > TextWidthPt) { hPt *= TextWidthPt / wPt; wPt = TextWidthPt; }
            int id = _ids++;
            string name = $"image{id}.{ext}";
            _media.Add((name, data));
            long cx = (long)(wPt * 12700), cy = (long)(hPt * 12700);
            _body.Append("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing>")
                 .Append("<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">")
                 .Append($"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"Immagine {id}\"/>")
                 .Append("<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
                 .Append("<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
                 .Append($"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>")
                 .Append($"<pic:blipFill><a:blip r:embed=\"rIdImg{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>")
                 .Append($"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>")
                 .Append("</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
        }

        public void PageBreak() => _body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");

        public void Save(string path)
        {
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            Xml.Entry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "</Types>");
            Xml.Entry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            foreach (var (name, data) in _media)
            {
                string id = "rIdImg" + Regex.Match(name, @"\d+").Value;
                rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{name}\"/>");
                Xml.Entry(zip, "word/media/" + name, data);
            }
            rels.Append("</Relationships>");
            Xml.Entry(zip, "word/_rels/document.xml.rels", rels.ToString());
            Xml.Entry(zip, "word/styles.xml", Styles);

            if (_body.Length == 0) _body.Append("<w:p/>");
            Xml.Entry(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><w:body>" +
                _body +
                "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
                "</w:body></w:document>");
        }

        const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"it-IT\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"360\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri Light\" w:hAnsi=\"Calibri Light\"/><w:color w:val=\"1F3864\"/><w:sz w:val=\"36\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri Light\" w:hAnsi=\"Calibri Light\"/><w:color w:val=\"2F5496\"/><w:sz w:val=\"30\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"heading 3\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"200\" w:after=\"60\"/><w:outlineLvl w:val=\"2\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"1F3763\"/><w:sz w:val=\"24\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
            "</w:styles>";
    }

    // ======================================================================== ready-to-use Excel (.xlsx)

    internal struct XCell
    {
        public string Text;
        public double Num;
        public bool IsNum;
        public int Style;   // see XlsxBuilder.S*
    }

    /// <summary>Creates real Excel sheets: colored header, borders, numbers/dates/percentages/€ as values, filters, frozen row, fitted columns.</summary>
    internal sealed class XlsxBuilder
    {
        public const int SText = 0, SHeader = 1, SCell = 2, SDec = 3, SInt = 4, SPct = 5, SDate = 6, SEur = 7, STitle = 8;

        readonly List<(string name, string xml, string filter)> _sheets = new List<(string, string, string)>();

        public void AddSheet(string name, IList<XCell[]> rows, int headerRow)
        {
            name = SafeName(name);
            int cols = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Length));
            var widths = new double[cols];
            var sd = new StringBuilder();
            for (int r = 0; r < rows.Count; r++)
            {
                sd.Append($"<row r=\"{r + 1}\"{(r == headerRow ? " ht=\"20\" customHeight=\"1\"" : "")}>");
                for (int c = 0; c < rows[r].Length; c++)
                {
                    var cell = rows[r][c];
                    string refc = Col(c) + (r + 1);
                    if (cell.IsNum)
                        sd.Append($"<c r=\"{refc}\" s=\"{cell.Style}\"><v>{cell.Num.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    else if (!string.IsNullOrEmpty(cell.Text))
                        sd.Append($"<c r=\"{refc}\" s=\"{cell.Style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml.Esc(cell.Text)}</t></is></c>");
                    else if (r > headerRow && headerRow >= 0)
                        sd.Append($"<c r=\"{refc}\" s=\"{SCell}\"/>");

                    if (cell.Style != STitle)
                    {
                        double len = cell.IsNum ? FormatLen(cell) : (cell.Text ?? "").Split('\n').Max(l => l.Length);
                        if (r == headerRow) len *= 1.15;
                        widths[c] = Math.Max(widths[c], len);
                    }
                }
                sd.Append("</row>");
            }

            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
             .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">")
             .Append("<sheetViews><sheetView workbookViewId=\"0\"");
            x.Append(_sheets.Count == 0 ? " tabSelected=\"1\">" : ">");
            if (headerRow >= 0 && headerRow < rows.Count - 1)
                x.Append($"<pane ySplit=\"{headerRow + 1}\" topLeftCell=\"A{headerRow + 2}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            x.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/><cols>");
            for (int c = 0; c < cols; c++)
                x.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Min(60, Math.Max(9, widths[c] * 1.1 + 2)).ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            x.Append("</cols><sheetData>").Append(sd).Append("</sheetData>");

            string filter = null;
            if (headerRow >= 0 && headerRow < rows.Count - 1 && cols > 1)
            {
                filter = $"{Col(0)}{headerRow + 1}:{Col(cols - 1)}{rows.Count}";
                x.Append($"<autoFilter ref=\"{filter}\"/>");
            }
            x.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
            x.Append("<pageSetup orientation=\"").Append(cols > 6 ? "landscape" : "portrait").Append("\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
            x.Append("</worksheet>");
            _sheets.Add((name, x.ToString(), filter));
        }

        static double FormatLen(XCell c) => c.Style switch
        {
            SDate => 10,
            SEur => c.Num.ToString("#,##0.00", CultureInfo.GetCultureInfo("it-IT")).Length + 2,
            SPct => (c.Num * 100).ToString("0.00", CultureInfo.InvariantCulture).Length + 1,
            SDec => c.Num.ToString("#,##0.00", CultureInfo.GetCultureInfo("it-IT")).Length,
            _ => c.Num.ToString("#,##0", CultureInfo.GetCultureInfo("it-IT")).Length
        };

        string SafeName(string n)
        {
            n = Regex.Replace(n ?? L.T("Foglio", "Sheet"), @"[\[\]\*\?/\\:]", " ").Trim();
            if (n.Length == 0) n = L.T("Foglio", "Sheet");
            if (n.Length > 31) n = n.Substring(0, 31);
            string b = n; int i = 2;
            while (_sheets.Any(s => s.name.Equals(n, StringComparison.OrdinalIgnoreCase))) { string suf = $" ({i++})"; n = (b.Length + suf.Length > 31 ? b.Substring(0, 31 - suf.Length) : b) + suf; }
            return n;
        }

        public static string Col(int c)
        {
            string s = "";
            for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
            return s;
        }

        public void Save(string path)
        {
            if (_sheets.Count == 0) AddSheet(L.T("Foglio1", "Sheet1"), new List<XCell[]>(), -1);
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 0; i < _sheets.Count; i++)
                ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            ct.Append("</Types>");
            Xml.Entry(zip, "[Content_Types].xml", ct.ToString());

            Xml.Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var wr = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            var dn = new StringBuilder();
            for (int i = 0; i < _sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{Xml.Esc(_sheets[i].name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                wr.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Xml.Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", _sheets[i].xml);
                if (_sheets[i].filter != null)
                {
                    var parts = _sheets[i].filter.Split(':');
                    string abs(string a) => "$" + Regex.Match(a, "[A-Z]+").Value + "$" + Regex.Match(a, @"\d+").Value;
                    dn.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Xml.Esc(_sheets[i].name.Replace("'", "''"))}'!{abs(parts[0])}:{abs(parts[1])}</definedName>");
                }
            }
            wb.Append("</sheets>");
            if (dn.Length > 0) wb.Append("<definedNames>").Append(dn).Append("</definedNames>");
            wb.Append("</workbook>");
            wr.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Xml.Entry(zip, "xl/workbook.xml", wb.ToString());
            Xml.Entry(zip, "xl/_rels/workbook.xml.rels", wr.ToString());
            Xml.Entry(zip, "xl/styles.xml", Styles);
        }

        // cellXfs indexes = S* constants
        const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"&quot;€&quot;\\ #,##0.00\"/></numFmts>" +
            "<fonts count=\"3\">" +
              "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>" +
              "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>" +
              "<font><b/><sz val=\"14\"/><color rgb=\"FF1F3864\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>" +
            "</fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
              "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2F5597\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
              "<border><left style=\"thin\"><color rgb=\"FFD0D7E5\"/></left><right style=\"thin\"><color rgb=\"FFD0D7E5\"/></right><top style=\"thin\"><color rgb=\"FFD0D7E5\"/></top><bottom style=\"thin\"><color rgb=\"FFD0D7E5\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"9\">" +
              "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                                                                         // 0 text
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" + // 1 header
              "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +  // 2 cell
              "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>" +   // 3 decimal
              "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>" +   // 4 integer
              "<xf numFmtId=\"10\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>" +  // 5 percentage
              "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>" +  // 6 date
              "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>" + // 7 euro
              "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                            // 8 title
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        // ------------------------------------------------------------ value detection

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Turns text into a typed cell: numbers (1.234,56 or 1,234.56), €, %, dd/mm/yyyy dates.</summary>
        public static XCell Typed(string raw, int textStyle = SCell)
        {
            string s = (raw ?? "").Trim();
            var cell = new XCell { Text = s, Style = textStyle };
            if (s.Length == 0 || s.Length > 24) return cell;

            var dm = Regex.Match(s, @"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{2}|\d{4})$");
            if (dm.Success)
            {
                int d = int.Parse(dm.Groups[1].Value), m = int.Parse(dm.Groups[2].Value), y = int.Parse(dm.Groups[3].Value);
                if (y < 100) y += y < 50 ? 2000 : 1900;
                if (m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
                    return new XCell { IsNum = true, Num = new DateTime(y, m, d).ToOADate(), Style = SDate };
                return cell;
            }

            bool eur = false, pct = false, neg = false;
            string t = s.Replace(" ", "").Replace(" ", "").Replace("'", "");
            if (t.Contains("€") || t.StartsWith("EUR", StringComparison.OrdinalIgnoreCase) || t.EndsWith("EUR", StringComparison.OrdinalIgnoreCase))
            { eur = true; t = t.Replace("€", "").Replace("EUR", "").Replace("eur", ""); }
            if (t.EndsWith("%")) { pct = true; t = t.TrimEnd('%'); }
            if (t.StartsWith("(") && t.EndsWith(")")) { neg = true; t = t.Trim('(', ')'); }
            if (t.StartsWith("-") || t.StartsWith("−")) { neg = true; t = t.Substring(1); }
            else if (t.StartsWith("+")) t = t.Substring(1);
            if (t.EndsWith("-")) { neg = true; t = t.TrimEnd('-'); }

            if (!Regex.IsMatch(t, @"^\d[\d.,]*$")) return cell;
            if (t.Length > 1 && t[0] == '0' && char.IsDigit(t[1])) return cell; // codes with leading zeros
            if (t.Count(char.IsDigit) > 15) return cell;                         // IBANs, long codes

            int lastDot = t.LastIndexOf('.'), lastComma = t.LastIndexOf(',');
            string norm;
            if (lastDot >= 0 && lastComma >= 0)
                norm = lastComma > lastDot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
            else if (lastComma >= 0)
                norm = t.Count(ch => ch == ',') > 1 ? t.Replace(",", "") : t.Replace(',', '.');
            else if (lastDot >= 0)
                norm = t.Count(ch => ch == '.') > 1 || (t.Length - lastDot - 1 == 3 && lastDot <= 3) ? t.Replace(".", "") : t;
            else norm = t;

            if (!double.TryParse(norm, NumberStyles.Float, Inv, out double v)) return cell;
            if (neg) v = -v;
            bool hasDec = norm.Contains('.');
            int style = eur ? SEur : pct ? SPct : hasDec ? SDec : SInt;
            if (pct) v /= 100;
            return new XCell { IsNum = true, Num = v, Style = style };
        }

        /// <summary>Text rows → typed rows; finds the header (first "full" row of text only).</summary>
        public static List<XCell[]> TypedRows(IList<string[]> rows, out int headerRow)
        {
            headerRow = -1;
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            for (int r = 0; r < Math.Min(rows.Count, 8); r++)
            {
                int filled = rows[r].Count(c => !string.IsNullOrWhiteSpace(c));
                if (filled >= Math.Max(2, (cols + 1) / 2) && rows[r].All(c => string.IsNullOrWhiteSpace(c) || !Typed(c).IsNum)) { headerRow = r; break; }
            }
            var list = new List<XCell[]>();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = new XCell[rows[r].Length];
                for (int c = 0; c < row.Length; c++)
                {
                    if (r == headerRow) row[c] = new XCell { Text = (rows[r][c] ?? "").Trim(), Style = SHeader };
                    else if (headerRow >= 0 && r < headerRow) row[c] = new XCell { Text = (rows[r][c] ?? "").Trim(), Style = c == 0 ? STitle : SText };
                    else row[c] = Typed(rows[r][c], headerRow >= 0 ? SCell : SText);
                }
                list.Add(row);
            }
            return list;
        }
    }

    // ======================================================================== .xlsx reading (without Excel)

    internal static class XlsxReader
    {
        /// <summary>Reads the first sheet as a text grid (computed values, converted dates).</summary>
        public static List<string[]> ReadFirstSheet(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var shared = new List<string>();
            var sst = zip.GetEntry("xl/sharedStrings.xml");
            if (sst != null)
            {
                XDocument d; using (var s = sst.Open()) d = XDocument.Load(s);
                foreach (var si in d.Root.Elements(ns + "si"))
                    shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }

            var dateStyles = new HashSet<int>();
            var st = zip.GetEntry("xl/styles.xml");
            if (st != null)
            {
                XDocument d; using (var s = st.Open()) d = XDocument.Load(s);
                var custom = d.Root.Element(ns + "numFmts")?.Elements(ns + "numFmt")
                    .ToDictionary(e => (int)e.Attribute("numFmtId"), e => (string)e.Attribute("formatCode")) ?? new Dictionary<int, string>();
                int i = 0;
                foreach (var xf in d.Root.Element(ns + "cellXfs")?.Elements(ns + "xf") ?? Enumerable.Empty<XElement>())
                {
                    int id = (int?)xf.Attribute("numFmtId") ?? 0;
                    bool isDate = (id >= 14 && id <= 22) || (custom.TryGetValue(id, out var code) && Regex.IsMatch(Regex.Replace(code, "\"[^\"]*\"", ""), "[dmy]", RegexOptions.IgnoreCase) && !code.Contains("0"));
                    if (isDate) dateStyles.Add(i);
                    i++;
                }
            }

            var sheet = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$"))
                           .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).FirstOrDefault()
                        ?? throw new InvalidDataException(L.T("Nessun foglio trovato", "No sheet found"));
            XDocument x; using (var s = sheet.Open()) x = XDocument.Load(s);

            var rows = new List<string[]>();
            foreach (var row in x.Descendants(ns + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    string r = (string)c.Attribute("r") ?? "";
                    int col = ColIndex(Regex.Match(r, "^[A-Z]+").Value);
                    while (cells.Count < col) cells.Add("");
                    string t = (string)c.Attribute("t");
                    int s = (int?)c.Attribute("s") ?? 0;
                    string v = c.Element(ns + "v")?.Value ?? "";
                    string val = t switch
                    {
                        "s" => int.TryParse(v, out int k) && k < shared.Count ? shared[k] : "",
                        "inlineStr" => string.Concat(c.Descendants(ns + "t").Select(e => e.Value)),
                        "b" => v == "1" ? "VERO" : "FALSO",
                        _ => v
                    };
                    if (t == null && dateStyles.Contains(s) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double oa))
                        val = DateTime.FromOADate(oa).ToString(oa % 1 == 0 ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                    else if ((t == null || t == "n") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                        val = num.ToString("0.##########", CultureInfo.GetCultureInfo("it-IT"));
                    cells.Add(val);
                }
                int rr = (int?)row.Attribute("r") ?? rows.Count + 1;
                while (rows.Count < rr - 1) rows.Add(Array.Empty<string>());
                rows.Add(cells.ToArray());
            }
            return rows;
        }

        static int ColIndex(string letters)
        {
            int n = 0;
            foreach (char ch in letters) n = n * 26 + (ch - 'A' + 1);
            return Math.Max(0, n - 1);
        }
    }
}
