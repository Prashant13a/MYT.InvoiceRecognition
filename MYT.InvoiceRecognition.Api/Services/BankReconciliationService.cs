using System.Text.Json;

namespace MYT.InvoiceRecognition.Api.Services;

public sealed class BankReconciliationService
{
    private readonly SpreadsheetService _files;
    private readonly OpenAiService _ai;

    public BankReconciliationService(SpreadsheetService files, OpenAiService ai)
    {
        _files = files;
        _ai = ai;
    }

    public async Task<JsonElement> MatchAsync(IFormFile statementFile, IFormFile invoiceFile, CancellationToken cancellationToken)
    {
        var statement = await _files.ReadAsync(statementFile, cancellationToken);
        var invoices = await _files.ReadAsync(invoiceFile, cancellationToken);
        foreach (var required in new[] { "Memo", "Date" })
        {
            if (!statement[0].ContainsKey(required)) throw new ArgumentException($"Bank statement is missing required column '{required}'.");
        }
        foreach (var required in new[] { "CustomerCompany", "InvoiceDate" })
        {
            if (!invoices[0].ContainsKey(required)) throw new ArgumentException($"Invoice file is missing required column '{required}'.");
        }
        return await _ai.ReconcileAsync(JsonSerializer.Serialize(statement), JsonSerializer.Serialize(invoices), cancellationToken);
    }
}
