using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Projecte1_WPF_
{
    // One sheet of a workbook: a header row and the rows (string, number or DateTime cells)
    public class XlsxSheet
    {
        public string Name;
        public List<string> Header = new List<string>();
        public List<object[]> Rows = new List<object[]>();
    }

    // EXCEL WORKBOOK (.xlsx) with several sheets, written by hand: an .xlsx is a zip of XML parts, so no package is needed.
    // Each sheet: bold header on a light background, filters, the header row frozen, columns as wide as their longest value,
    // dates as real Excel dates (yyyy-mm-dd hh:mm:ss). Used by the sector panel's export
    public static class XlsxWriter
    {
        private const string MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PKG = "http://schemas.openxmlformats.org/package/2006/relationships";

        // cellXfs: 0 normal, 1 header, 2 date-time, 3 number with one decimal
        private const string STYLES =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + MAIN + "\">" +
            "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm:ss\"/><numFmt numFmtId=\"165\" formatCode=\"0.0\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FF1F2D3D\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCE7F3\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF8A97A6\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

        public static void Write(string path, IList<XlsxSheet> sheets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                var workbook = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{MAIN}\" xmlns:r=\"{REL}\"><sheets>");
                var rels = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{PKG}\">");
                var names = new StringBuilder("<definedNames>");
                for (int i = 0; i < sheets.Count; i++)
                {
                    string name = SheetName(sheets[i].Name, i);
                    types.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                    workbook.Append($"<sheet name=\"{Escape(name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                    rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{REL}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                    int columns = Math.Max(1, sheets[i].Header.Count);
                    names.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Escape(name.Replace("'", "''"))}'!$A$1:${Column(columns - 1)}${sheets[i].Rows.Count + 1}</definedName>");
                    WriteSheet(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i]);
                }
                rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"{REL}/styles\" Target=\"styles.xml\"/></Relationships>");
                workbook.Append("</sheets>").Append(names).Append("</definedNames></workbook>");
                types.Append("</Types>");
                Part(zip, "[Content_Types].xml", types.ToString());
                Part(zip, "_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{PKG}\"><Relationship Id=\"rId1\" Type=\"{REL}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Part(zip, "xl/workbook.xml", workbook.ToString());
                Part(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
                Part(zip, "xl/styles.xml", STYLES);
            }
        }

        private static void WriteSheet(ZipArchive zip, string part, XlsxSheet sheet)
        {
            int columns = Math.Max(1, sheet.Header.Count);
            using (Stream stream = zip.CreateEntry(part).Open())
            using (XmlWriter x = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                x.WriteStartDocument(true);
                x.WriteStartElement("worksheet", MAIN);
                x.WriteStartElement("sheetViews");
                x.WriteStartElement("sheetView");
                x.WriteAttributeString("workbookViewId", "0");
                x.WriteStartElement("pane");
                x.WriteAttributeString("ySplit", "1");
                x.WriteAttributeString("topLeftCell", "A2");
                x.WriteAttributeString("activePane", "bottomLeft");
                x.WriteAttributeString("state", "frozen");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();

                // Widths: the longest text of each column (header with room for the filter button), within 8-60
                x.WriteStartElement("cols");
                for (int c = 0; c < columns; c++)
                {
                    int width = (c < sheet.Header.Count ? sheet.Header[c].Length : 0) + 4;
                    foreach (object[] row in sheet.Rows)
                        if (c < row.Length)
                            width = Math.Max(width, Text(row[c]).Length + 2);
                    x.WriteStartElement("col");
                    x.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
                    x.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
                    x.WriteAttributeString("width", Math.Max(8, Math.Min(60, width)).ToString(CultureInfo.InvariantCulture));
                    x.WriteAttributeString("customWidth", "1");
                    x.WriteEndElement();
                }
                x.WriteEndElement();

                x.WriteStartElement("sheetData");
                Row(x, 1, sheet.Header.Cast<object>().ToArray(), header: true);
                for (int r = 0; r < sheet.Rows.Count; r++)
                    Row(x, r + 2, sheet.Rows[r], header: false);
                x.WriteEndElement();

                x.WriteStartElement("autoFilter");
                x.WriteAttributeString("ref", $"A1:{Column(columns - 1)}{sheet.Rows.Count + 1}");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndDocument();
            }
        }

        private static void Row(XmlWriter x, int number, object[] values, bool header)
        {
            x.WriteStartElement("row");
            x.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
            for (int c = 0; c < values.Length; c++)
            {
                object v = values[c];
                if (v == null)
                    continue;
                x.WriteStartElement("c");
                x.WriteAttributeString("r", Column(c) + number);
                switch (v)
                {
                    case string s:
                        x.WriteAttributeString("t", "inlineStr");
                        if (header) x.WriteAttributeString("s", "1");
                        x.WriteStartElement("is");
                        x.WriteElementString("t", XmlSafe(s));
                        x.WriteEndElement();
                        break;
                    case DateTime d:
                        x.WriteAttributeString("s", "2");
                        x.WriteElementString("v", d.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case double dbl:
                        x.WriteAttributeString("s", "3");
                        x.WriteElementString("v", dbl.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    default:
                        x.WriteElementString("v", Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                        break;
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }

        private static string Text(object v) =>
            v is DateTime ? "yyyy-mm-dd hh:mm:ss" : v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

        // Column letters: A..Z, AA..
        private static string Column(int index) => index < 26 ? ((char)('A' + index)).ToString() : Column(index / 26 - 1) + (char)('A' + index % 26);

        // Excel sheet names: at most 31 characters, none of []:*?/\
        private static string SheetName(string name, int i)
        {
            string clean = new string((name ?? "Sheet" + (i + 1)).Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray());
            return clean.Length > 31 ? clean.Substring(0, 31) : clean;
        }

        private static string Escape(string s) => System.Security.SecurityElement.Escape(XmlSafe(s));

        // Characters XML cannot hold (control characters from a damaged input line) would stop the writer: dropped.
        // Excel also limits a cell to 32,767 characters
        private static string XmlSafe(string s)
        {
            string clean = new string(s.Where(XmlConvert.IsXmlChar).ToArray());
            return clean.Length > 32767 ? clean.Substring(0, 32767) : clean;
        }

        private static void Part(ZipArchive zip, string name, string xml)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                writer.Write(xml);
        }
    }
}
