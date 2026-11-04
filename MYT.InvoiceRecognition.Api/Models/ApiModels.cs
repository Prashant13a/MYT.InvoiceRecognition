using System.Text.Json.Serialization;

namespace MYT.InvoiceRecognition.Api.Models;

public sealed class InvoiceResponse<T>
{
    [JsonPropertyName("data")] public T Data { get; set; } = default!;
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("errors")] public string? Errors { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; } = 200;
}

public sealed class ErrorResponse
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("errors")] public string Errors { get; set; } = "";
    [JsonPropertyName("details")] public string Details { get; set; } = "";
    [JsonPropertyName("status")] public int Status { get; set; } = 400;
}

public sealed class InvoiceData
{
    [JsonPropertyName("org")] public string Org { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("supplierVatNumber")] public string SupplierVatNumber { get; set; } = "";
    [JsonPropertyName("supplierCompanyNumber")] public string SupplierCompanyNumber { get; set; } = "";
    [JsonPropertyName("supplierCountry")] public string SupplierCountry { get; set; } = "";
    [JsonPropertyName("ivn")] public string Ivn { get; set; } = "";
    [JsonPropertyName("dat")] public string Dat { get; set; } = "";
    [JsonPropertyName("lin")] public List<string> Lin { get; set; } = new();
    [JsonPropertyName("tot")] public decimal? Tot { get; set; }
    [JsonPropertyName("vat")] public decimal? Vat { get; set; }
    [JsonPropertyName("net")] public decimal? Net { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    [JsonPropertyName("supplier_info")] public Dictionary<string, string> SupplierInfo { get; set; } = new();
    [JsonPropertyName("documentType")] public string DocumentType { get; set; } = "";
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("detectionMethod")] public string DetectionMethod { get; set; } = "";
    [JsonPropertyName("documentTypeConfidence")] public double? DocumentTypeConfidence { get; set; }
    [JsonPropertyName("reviewRequired")] public bool ReviewRequired { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public sealed class AzureInvoiceResult
{
    public InvoiceData Invoice { get; set; } = new();
    public string Content { get; set; } = "";
    public string RawCurrency { get; set; } = "";
    public bool HasReceiptType { get; set; }
}

// Per-call diagnostics, supplied explicitly so concurrent analyses never share state.
// These are not part of the public invoice response.
public sealed class AzureAnalysisTrace
{
    public Guid AttemptId { get; } = Guid.NewGuid();
    public List<AzureModelAttempt> Models { get; } = new();
}

public sealed class AzureModelAttempt
{
    public string DocumentType { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public string ParserVersion { get; init; } = "";
    public string Status { get; set; } = "Error";
    public bool? IsSelected { get; set; }
    public double? QualityScore { get; set; }
    public string? DetectionMethod { get; set; }
    public string? RawAzureResultJson { get; set; }
    public InvoiceData? ParsedResult { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; }
}
