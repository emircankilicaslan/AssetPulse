using AssetPulse.Api.Data;

namespace AssetPulse.Api.Endpoints;

public static class AuditEndpoints
{
    private const int MaxExportRows = 10_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").RequireAuthorization("AdminOnly").WithTags("Denetim kaydı");

        group.MapGet("/", async (string? actor, string? action, DateTime? from, DateTime? to, int? page, int? pageSize,
                AuditStore audit, CancellationToken ct) =>
            {
                var p = page ?? 1;
                var size = pageSize ?? 25;
                if (p < 1) return Helpers.Invalid("page", "Sayfa 1 veya daha büyük olmalıdır.");
                if (size is < 1 or > 100) return Helpers.Invalid("pageSize", "Sayfa boyutu 1-100 olmalıdır.");
                if (from is not null && to is not null && from >= to) return Helpers.Invalid("from", "'from' değeri 'to' değerinden önce olmalıdır.");
                return Results.Ok(await audit.ListAsync(new AuditFilter(actor, action, from?.ToUniversalTime(), to?.ToUniversalTime()), p, size, ct));
            })
            .WithSummary("Denetim kaydını süzer ve sayfalar (?actor, ?action, ?from, ?to). En yeni önce (yalnızca Admin).");

        group.MapGet("/export.xlsx", async (string? actor, string? action, DateTime? from, DateTime? to,
                AuditStore audit, CancellationToken ct) =>
            {
                if (from is not null && to is not null && from >= to) return Helpers.Invalid("from", "'from' değeri 'to' değerinden önce olmalıdır.");
                var page = await audit.ListAsync(new AuditFilter(actor, action, from?.ToUniversalTime(), to?.ToUniversalTime()), 1, MaxExportRows, ct);
                var bytes = Xlsx.Build("Denetim kaydı",
                    ["Id", "Zaman (UTC)", "Kullanıcı", "Rol", "İşlem", "Varlık türü", "Kayıt", "Ayrıntı"],
                    page.Items.Select(a => (IReadOnlyList<object?>)[a.Id, a.OccurredAt, a.Actor, a.ActorRole, a.Action, a.EntityType, a.EntityId, a.Details]));
                return Results.File(bytes, Xlsx.ContentType, "denetim-kaydi.xlsx");
            })
            .WithSummary($"Denetim kaydını Excel olarak indirir (en fazla {MaxExportRows} satır; yalnızca Admin).");
    }
}
