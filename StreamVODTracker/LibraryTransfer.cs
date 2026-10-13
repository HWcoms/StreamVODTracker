using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace StreamVODTracker;

internal sealed record TransferMedia(string Platform, string ChannelId, string ChannelName, string ChannelUrl,
    string VodId, string Title, string Url, DateTimeOffset? PublishedAt, string WatchStatus,
    double LastPositionSeconds, string LocalFilePath, string Memo, string UpdatedAt);

internal static class LibraryTransfer
{
    private static readonly string[] Columns = ["Platform", "ChannelId", "ChannelName", "ChannelUrl", "VodId", "Title", "Url", "PublishedAt", "WatchStatus", "LastPositionSeconds", "LocalFilePath", "Memo", "UpdatedAt"];

    public static void ExportCsv(string path, IEnumerable<SavedChannelData> channels)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        WriteRow(writer, Columns);
        foreach (var channel in Flatten(channels)) WriteTransferRow(writer, channel);
    }

    public static IReadOnlyList<TransferMedia> ImportCsv(string path)
    {
        var rows = ReadCsv(File.ReadAllText(path, Encoding.UTF8));
        if (rows.Count == 0) throw new InvalidDataException("CSV 파일에 데이터가 없습니다.");
        var headers = rows[0];
        int Col(string name) => headers.FindIndex(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var required in new[] { "Platform", "ChannelId", "Title" })
            if (Col(required) < 0) throw new InvalidDataException($"필수 열 {required}이(가) 없습니다.");
        string Value(List<string> row, string name) { var index = Col(name); return index >= 0 && index < row.Count ? row[index] : ""; }
        var result = new List<TransferMedia>();
        foreach (var row in rows.Skip(1))
        {
            var platform = Value(row, "Platform").Trim().ToUpperInvariant();
            var channelId = Value(row, "ChannelId").Trim();
            var title = Value(row, "Title").Trim();
            if (platform is not ("CHZZK" or "SOOP") || channelId.Length == 0 || title.Length == 0)
                throw new InvalidDataException("Platform은 CHZZK/SOOP, ChannelId와 Title은 필수입니다.");
            var url = Value(row, "Url").Trim();
            var channelUrl = Value(row, "ChannelUrl").Trim();
            if (channelUrl.Length > 0 && (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var channelUri) || channelUri.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"{channelId}의 ChannelUrl이 올바르지 않습니다.");
            if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var mediaUri) || mediaUri.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"{title}의 Url이 올바르지 않습니다.");
            DateTimeOffset? date = DateTimeOffset.TryParse(Value(row, "PublishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            var vodId = Value(row, "VodId").Trim();
            if (vodId.Length == 0) vodId = GetVodId(platform, url, Value(row, "LocalFilePath"), title);
            var position = double.TryParse(Value(row, "LastPositionSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? Math.Max(0, seconds) : 0;
            result.Add(new TransferMedia(platform, channelId, Value(row, "ChannelName").Trim(), channelUrl, vodId,
                title, url, date, Value(row, "WatchStatus").Trim(), position, Value(row, "LocalFilePath").Trim(), Value(row, "Memo"), Value(row, "UpdatedAt")));
        }
        return result;
    }

    public static void ExportXlsx(string path, IEnumerable<SavedChannelData> channels)
    {
        var all = Flatten(channels).ToList();
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteXml(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        WriteXml(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        WriteXml(archive, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"치지직\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"soop\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
        WriteXml(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>");
        WriteSheet(archive, "xl/worksheets/sheet1.xml", all.Where(row => row.Platform == "CHZZK"));
        WriteSheet(archive, "xl/worksheets/sheet2.xml", all.Where(row => row.Platform == "SOOP"));
    }

    public static string GetVodId(string platform, string url, string file, string title)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) return Uri.UnescapeDataString(segments[^1]);
        }
        var identity = file.Length > 0 ? file : title;
        return "local-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(platform + ":" + identity)))[..16];
    }

    private static IEnumerable<TransferMedia> Flatten(IEnumerable<SavedChannelData> channels) => channels.SelectMany(data => data.Media.Select(item =>
    {
        var url = item.OnlineUrl ?? (Uri.TryCreate(item.SourceValue, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? item.SourceValue : "");
        return new TransferMedia(data.Channel.Platform, data.Channel.ChannelId, data.Channel.Name, data.Channel.Url,
            GetVodId(data.Channel.Platform, url, item.LocalFilePath ?? "", item.Title), item.Title, url, item.PublishedAt,
            item.WatchStatus, item.LastPositionSeconds, item.LocalFilePath ?? "", item.Memo, (item.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
    }));

    private static void WriteTransferRow(TextWriter writer, TransferMedia row) =>
        WriteRow(writer, [row.Platform, row.ChannelId, row.ChannelName, row.ChannelUrl, row.VodId, row.Title, row.Url,
            row.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "", row.WatchStatus, row.LastPositionSeconds.ToString(CultureInfo.InvariantCulture), row.LocalFilePath, row.Memo, row.UpdatedAt]);

    private static void WriteSheet(ZipArchive archive, string name, IEnumerable<TransferMedia> data)
    {
        var entry = archive.CreateEntry(name);
        using var writer = XmlWriter.Create(entry.Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"); writer.WriteStartElement("sheetData");
        WriteExcelRow(writer, 1, Columns);
        var index = 2;
        foreach (var row in data) WriteExcelRow(writer, index++, [row.Platform, row.ChannelId, row.ChannelName, row.ChannelUrl, row.VodId, row.Title, row.Url,
            row.PublishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "", row.WatchStatus, row.LastPositionSeconds.ToString(CultureInfo.InvariantCulture), row.LocalFilePath, row.Memo, row.UpdatedAt]);
        writer.WriteEndElement(); writer.WriteEndElement();
    }

    private static void WriteExcelRow(XmlWriter writer, int rowIndex, IEnumerable<string> values)
    {
        writer.WriteStartElement("row"); writer.WriteAttributeString("r", rowIndex.ToString(CultureInfo.InvariantCulture));
        var col = 0;
        foreach (var value in values)
        {
            writer.WriteStartElement("c"); writer.WriteAttributeString("r", ColumnName(++col) + rowIndex); writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is"); writer.WriteStartElement("t"); writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve"); writer.WriteString(value); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }
    private static string ColumnName(int column) { var name = ""; while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; } return name; }
    private static void WriteXml(ZipArchive archive, string name, string xml) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
    private static void WriteRow(TextWriter writer, IEnumerable<string> fields) => writer.WriteLine(string.Join(',', fields.Select(value => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value)));
    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++) { var c = text[i]; if (quoted) { if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else if (c == '"') quoted = false; else field.Append(c); } else if (c == '"' && field.Length == 0) quoted = true; else if (c == ',') { row.Add(field.ToString()); field.Clear(); } else if (c is '\r' or '\n') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(field.ToString()); field.Clear(); if (row.Any(value => value.Length > 0)) rows.Add(row); row = []; } else field.Append(c); }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); } return rows;
    }
}
