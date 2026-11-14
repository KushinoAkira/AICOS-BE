using System.Net;
using System.Text;
using System.Text.Json;
namespace AICOS.KitchenOperations.Api.Features.Barista;
public sealed record BarPrintLine(Guid ItemId, string Name, string? Options, int Copies);
public sealed record BarPrintDocument(string Brand, string Kind, string OrderCode, string Location, DateTime? OrderedAtUtc, DateTime AcceptedAtUtc, string AcceptedName, int WidthMm, int HeightMm, IReadOnlyList<BarPrintLine> Lines)
{
    public string Html(DateTime? requestedAtUtc = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string Local(DateTime? t) => t?.AddHours(7).ToString("dd/MM/yyyy HH:mm") ?? "Ch\u01b0a x\u00e1c minh";
        var html = new StringBuilder($"<!doctype html><html lang=vi><meta charset=utf-8><style>@page{{size:{WidthMm}mm {(Kind == "LABELS" ? HeightMm + "mm" : "auto")};margin:2mm}}body{{font:12px sans-serif;width:{WidthMm - 4}mm}}article{{break-after:page}}h2{{font-size:15px}}</style><body>");
        if (Kind == "SLIP")
        {
            html.Append($"<h2>{E(Brand)}</h2><b>{E(OrderCode)}</b><p>{E(Location)}</p><p>Giờ đặt: {Local(OrderedAtUtc)}<br>Tiếp nhận: {Local(AcceptedAtUtc)}<br>Barista: {E(AcceptedName)}</p>");
            if (requestedAtUtc is { } requested)
                html.Append($"<p>Th\u1eddi gian g\u1eedi in: {Local(requested)}</p>");
            foreach (var line in Lines)
                html.Append($"<p><b>{line.Copies} × {E(line.Name)}</b><br>{E(line.Options)}</p>");
        }
        else
            foreach (var line in Lines)
                for (var copy = 0; copy < line.Copies; copy++)
                    html.Append($"<article><b>{E(Brand)}</b><h2>{E(line.Name)}</h2>{E(OrderCode)}<br>{E(Location)}<br>{E(line.Options)}<br>{Local(OrderedAtUtc)}</article>");
        return html.Append("</body></html>").ToString();
    }
    public static BarPrintDocument Read(string json) => JsonSerializer.Deserialize<BarPrintDocument>(json)!;
}
