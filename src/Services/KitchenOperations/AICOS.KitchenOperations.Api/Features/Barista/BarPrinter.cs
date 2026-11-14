using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
namespace AICOS.KitchenOperations.Api.Features.Barista;

public sealed record PrinterResult(string Status, string? ErrorCode = null, bool IsTest = false);
public interface IBarPrinter
{
    Task<PrinterResult> PrintAsync(KitchenPrintJob job, CancellationToken ct);
}
public sealed class VerifiedPrintAgent(HttpClient http, IConfiguration config) : IBarPrinter
{
    private sealed record Ack(Guid JobId, string Status, string Signature);
    public async Task<PrinterResult> PrintAsync(KitchenPrintJob job, CancellationToken ct)
    {
        var endpoint = config["BaristaWorkflow:PrintAgentUrl"];
        var secret = config["BaristaWorkflow:PrintAgentKey"];
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(secret))
            return new("FAILED", "printer_not_configured");
        if (secret.Length < 32 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new("FAILED", "printer_configuration_invalid");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { job.Id, job.Kind, job.PayloadHash, Document = BarPrintDocument.Read(job.Payload), Html = BarPrintDocument.Read(job.Payload).Html(job.CreatedAtUtc) }) };
            message.Headers.Add("Idempotency-Key", job.Id.ToString());
            message.Headers.Add("X-Aicos-Print-Signature", Sign(secret, job.Id, job.PayloadHash, "REQUEST"));
            using var response = await http.SendAsync(message, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden)
                return new("FAILED", "printer_rejected");
            if (!response.IsSuccessStatusCode)
                return new("UNKNOWN", "printer_result_unverified");
            var ack = await response.Content.ReadFromJsonAsync<Ack>(cancellationToken: ct);
            var isTest = ack?.Status == "TEST_PRINTED";
            var testAllowed = config.GetValue<bool>("BaristaWorkflow:AllowTestPrinter") && config["ASPNETCORE_ENVIRONMENT"] == "Development" && new Npgsql.NpgsqlConnectionStringBuilder(config.GetConnectionString("Default")).Database == "aicos_billstack_kitchen";
            if (ack is null || ack.JobId != job.Id || (ack.Status != "PRINTED" && !(isTest && testAllowed)) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ack.Signature ?? ""), Encoding.UTF8.GetBytes(Sign(secret, job.Id, job.PayloadHash, ack.Status))))
                return new("UNKNOWN", "printer_ack_invalid");
            return new("PRINTED", IsTest: isTest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return new("UNKNOWN", "printer_result_unknown"); }
    }
    public static string Sign(string secret, Guid id, string hash, string status) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{id}|{hash}|{status}")));
}
