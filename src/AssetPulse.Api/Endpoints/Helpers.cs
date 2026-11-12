using System.Text.RegularExpressions;

namespace AssetPulse.Api.Endpoints;

internal static partial class Helpers
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{1,39}$")]
    public static partial Regex CodePattern();

    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] },
            title: "Geçersiz istek");

    public static IResult Conflict(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status409Conflict, title: "Çakışma");

    public static IResult NotFound(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status404NotFound, title: "Bulunamadı");

    /// <summary>Kullanıcı girdisi tek tırnakla (') başlayarak hücrenin formül olarak yorumlanması engellenir.</summary>
    public static string CsvCell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }
}
