namespace MYT.InvoiceRecognition.Api.Options;

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string ExtractionModel { get; set; } = "gpt-4o";
    public string AgentModel { get; set; } = "gpt-4o-mini";
    public string ReconciliationModel { get; set; } = "gpt-4o";
    public string FineTunedSupplierModel { get; set; } = "";
}

public sealed class SecurityOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed class LimitsOptions
{
    public long MaxUploadBytes { get; set; } = 10485760;
    public int MaxRowsPerFile { get; set; } = 500;
    public int MaxTextChars { get; set; } = 35000;
}
