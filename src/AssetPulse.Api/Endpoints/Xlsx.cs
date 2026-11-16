using ClosedXML.Excel;

namespace AssetPulse.Api.Endpoints;

/// <summary>Excel (.xlsx) çıktısı üretir. Metin hücreleri her zaman düz metin olarak yazılır; "=" ile başlayan kullanıcı girdisi formül olarak çalışmaz.</summary>
internal static class Xlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Build(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);

        for (var c = 0; c < headers.Count; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        var header = sheet.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EEF5");

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                switch (row[c])
                {
                    case null: break;
                    case string s: cell.SetValue(s); break;
                    case DateTime d:
                        cell.SetValue(d);
                        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                        break;
                    case decimal m: cell.SetValue(m); break;
                    case int i: cell.SetValue(i); break;
                    case long l: cell.SetValue(l); break;
                    default: cell.SetValue(row[c]!.ToString() ?? ""); break;
                }
            }
            r++;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 500), 8, 60);
        if (r > 2) sheet.Range(1, 1, r - 1, headers.Count).SetAutoFilter();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
