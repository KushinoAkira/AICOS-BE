using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICOS.Ordering.Api.Features.Analytics;

public sealed class AprioriOptions
{
    public const string SectionName = "Apriori";

    /// <summary>Base URL of ai-models, e.g. http://localhost:8090 (no trailing slash).</summary>
    public string AiModelsUrl { get; set; } = "http://localhost:8090";

    public double MinSupport { get; set; } = 0.05;

    public double MinConfidence { get; set; } = 0.4;

    public double PromoMinSupport { get; set; } = 0.05;

    public double PromoMinConfidence { get; set; } = 0.4;

    public double PromoMinLift { get; set; } = 1.2;

    public int LookbackDays { get; set; } = 90;

    public int MineIntervalHours { get; set; } = 6;
}

public sealed record AprioriRuleDto(
    [property: JsonPropertyName("antecedent")] List<string> Antecedent,
    [property: JsonPropertyName("consequent")] List<string> Consequent,
    [property: JsonPropertyName("support")] double Support,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("lift")] double Lift,
    [property: JsonPropertyName("transactionCount")] int TransactionCount);

public sealed record AprioriMineResponse(
    [property: JsonPropertyName("rules")] List<AprioriRuleDto> Rules,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    [property: JsonPropertyName("ruleCount")] int RuleCount);

public interface IAprioriClient
{
    Task<AprioriMineResponse> MineAsync(IReadOnlyList<IReadOnlyList<string>> transactions, double minSupport, double minConfidence, CancellationToken ct);
}

public sealed class HttpAprioriClient(HttpClient http, Microsoft.Extensions.Options.IOptions<AprioriOptions> options) : IAprioriClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<AprioriMineResponse> MineAsync(IReadOnlyList<IReadOnlyList<string>> transactions, double minSupport, double minConfidence, CancellationToken ct)
    {
        var body = new { transactions, minSupport, minConfidence };
        using var res = await http.PostAsJsonAsync($"{options.Value.AiModelsUrl.TrimEnd('/')}/api/v1/ai/apriori/mine", body, ct);
        res.EnsureSuccessStatusCode();
        var parsed = await res.Content.ReadFromJsonAsync<AprioriMineResponse>(Json, ct);
        return parsed ?? new AprioriMineResponse([], 0, 0);
    }
}
