using AssetPulse.Api.Endpoints;
using ClosedXML.Excel;
using Xunit;

namespace AssetPulse.Tests;

/// <summary>Excel çıktısı için birim testleri (veritabanı gerekmez).</summary>
public class XlsxTests
{
    private static IXLWorksheet Open(byte[] bytes) => new XLWorkbook(new MemoryStream(bytes)).Worksheet(1);

    [Fact]
    public void Baslik_ve_tipli_degerler_dogru_yazilir()
    {
        var at = new DateTime(2026, 11, 12, 10, 30, 15, DateTimeKind.Utc);
        var bytes = Xlsx.Build("Deneme", ["Ad", "Zaman", "Değer", "Sayı"],
            [new object?[] { "Trafo 1", at, 71.4m, 5L }, new object?[] { "Trafo 2", null, 80m, 6 }]);

        var sheet = Open(bytes);
        Assert.Equal("Deneme", sheet.Name);
        Assert.Equal("Zaman", sheet.Cell(1, 2).GetString());
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);

        Assert.Equal("Trafo 1", sheet.Cell(2, 1).GetString());
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 2).DataType);
        Assert.Equal(at, sheet.Cell(2, 2).GetDateTime());
        Assert.Equal(XLDataType.Number, sheet.Cell(2, 3).DataType);
        Assert.Equal(71.4m, (decimal)sheet.Cell(2, 3).GetDouble());
        Assert.True(sheet.Cell(3, 2).IsEmpty());
        Assert.Equal(6, sheet.Cell(3, 4).GetDouble());
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1:A2)")]
    [InlineData("-2+3")]
    [InlineData("@cmd")]
    [InlineData("=HYPERLINK(\"http://kotu.example\",\"tikla\")")]
    public void Formul_gibi_gorunen_metin_formul_olarak_calismaz(string value)
    {
        var sheet = Open(Xlsx.Build("Deneme", ["Metin"], [new object?[] { value }]));

        var cell = sheet.Cell(2, 1);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(value, cell.GetString());
    }

    [Fact]
    public void Bos_liste_yalnizca_basligi_icerir()
    {
        var sheet = Open(Xlsx.Build("Bos", ["A", "B"], []));
        Assert.Equal("A", sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Cell(2, 1).IsEmpty());
    }
}
