namespace MYT.InvoiceRecognition.Api.Options;

public sealed class AzureDocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiVersion { get; set; } = "2023-07-31";
    public int PollIntervalSeconds { get; set; } = 2;
    public int MaxPollAttempts { get; set; } = 75;
    public string ClassifierId { get; set; } = "";
    public string InvoiceClassName { get; set; } = "Invoice";
    public string ReceiptClassName { get; set; } = "Receipt";
    public double ClassifierMinimumConfidence { get; set; } = 0.80;
    public bool AutoCompareModels { get; set; } = true;
    public double AmbiguousScoreMargin { get; set; } = 8;
}
