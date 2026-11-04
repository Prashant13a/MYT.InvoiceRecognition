extern alias InvoiceConsole;

using ConsoleBatch = InvoiceConsole::MYT.InvoiceRecognition.ConsoleApp;
using ConsoleInvoice = InvoiceConsole::MYT.InvoiceRecognition.Api.Models.InvoiceData;
using ConsoleTrace = InvoiceConsole::MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace;
using ConsoleAzure = InvoiceConsole::MYT.InvoiceRecognition.Api.Services.AzureDocumentService;
using ConsoleAzureOptions = InvoiceConsole::MYT.InvoiceRecognition.Api.Options.AzureDocumentIntelligenceOptions;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Options;
using MYT.InvoiceRecognition.Api.Options;
using MYT.InvoiceRecognition.Api.Services;
using MYT.InvoiceRecognition.Api.Utilities;

var checks = 0;
void Assert(bool valid, string message)
{
    if (!valid) throw new Exception("FAILED: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

ErrorTextLogChecks.Check(Assert);
if (args is ["--error-text-log"]) { Console.WriteLine($"{checks} error text log checks passed."); return; }

SupplierResolutionChecks.CheckMatching(Assert);
await SupplierResolutionChecks.CheckBatchScopeAsync(Assert);
if (args is ["--supplier-sql"])
{
    await SupplierResolutionChecks.CheckSqlAsync(Assert);
    Console.WriteLine($"{checks} supplier checks passed.");
    return;
}
if (args is ["--supplier-matching"]) { Console.WriteLine($"{checks} supplier checks passed."); return; }
if (args is ["--supplier-batch"]) { Console.WriteLine($"{checks} supplier checks passed."); return; }

foreach (var outcome in new[] { "success", "failure", "cancellation" })
{
    using var output = new StringWriter();
    using var callerCancellation = new CancellationTokenSource();
    Exception originalError = outcome == "cancellation"
        ? new OperationCanceledException(callerCancellation.Token)
        : new InvalidOperationException("Original analysis failure");
    var history = new FakeRecognitionLogStore { WaitForCancellation = true };
    var analyzer = new ConsoleBatch.LoggedInvoiceAnalyzer((row, trace, token) =>
    {
        trace.Models.Add(new InvoiceConsole::MYT.InvoiceRecognition.Api.Models.AzureModelAttempt());
        if (outcome == "cancellation") callerCancellation.Cancel();
        if (outcome != "success") throw originalError;
        return Task.FromResult(new ConsoleInvoice { Ivn = "INV-TIMEOUT" });
    }, history, output, TimeSpan.FromMilliseconds(20));
    ConsoleInvoice? result = null;
    Exception? actualError = null;
    try
    {
        result = await analyzer.AnalyzeAsync(new ConsoleBatch.PendingInvoice(123, 456, "https://example.com/logged.pdf"), callerCancellation.Token);
    }
    catch (Exception error) { actualError = error; }
    Assert(outcome == "success" ? result?.Ivn == "INV-TIMEOUT" && actualError == null : ReferenceEquals(actualError, originalError),
        $"Log-save timeout preserves analysis {outcome}");
    Assert(history.Trace?.Models.Count == 1 && !history.CanceledAtSave,
        $"Log save receives an independent token after analysis {outcome}");
    Assert(output.ToString().Contains("UploadedInvoice 123, Invoice 456, Attempt ") &&
        output.ToString().Contains("recognition log save timed out after") &&
        output.ToString().Contains("RecognitionLog:SaveTimeoutSeconds") &&
        !output.ToString().Contains("TaskCanceledException"),
        $"Log-save timeout has actionable diagnostics after analysis {outcome}");
}

if (args is ["--log-diagnostics"])
{
    using var output = new StringWriter();
    var row = new ConsoleBatch.PendingInvoice(123, 456, "https://example.com/logged.pdf");
    var originalError = new InvalidOperationException("Original analysis failure");
    foreach (var failAnalysis in new[] { false, true })
    {
        var analyzer = new ConsoleBatch.LoggedInvoiceAnalyzer((invoiceRow, trace, token) =>
        {
            trace.Models.Add(new InvoiceConsole::MYT.InvoiceRecognition.Api.Models.AzureModelAttempt());
            if (failAnalysis) throw originalError;
            return Task.FromResult(new ConsoleInvoice { Ivn = "INV-TEST" });
        }, new FakeRecognitionLogStore { Fail = true }, output);
        if (!failAnalysis)
            Assert((await analyzer.AnalyzeAsync(row, CancellationToken.None)).Ivn == "INV-TEST", "Diagnostic output preserves successful analysis");
        else
        {
            Exception? actualError = null;
            try { await analyzer.AnalyzeAsync(row, CancellationToken.None); }
            catch (Exception error) { actualError = error; }
            Assert(ReferenceEquals(actualError, originalError), "Diagnostic output preserves the original analysis exception");
        }
    }
    Assert(output.ToString().Contains("UploadedInvoice 123, Invoice 456, Attempt ") &&
        output.ToString().Contains("Log database unavailable") &&
        output.ToString().Contains("INSERT permission denied on InvoiceRecognitionLog") &&
        output.ToString().Contains(nameof(FakeRecognitionLogStore)),
        "Recognition log errors print identifiers, exception message, inner error, and stack trace");
    Console.WriteLine($"{checks} diagnostic checks passed.");
    return;
}

var supportedPaths = new[]
{
    "https://example.com/invoice.PDF?download=invoice.doc#preview",
    "https://example.com/invoice.jpg", "https://example.com/invoice.jpeg",
    "https://example.com/invoice.png", "https://example.com/invoice.bmp",
    "https://example.com/invoice.tif", "https://example.com/invoice.tiff",
    "https://example.com/invoice.heif"
};
var unsupportedPaths = new[]
{
    "https://app.mytsoftware.co.uk/API/Uploads/RatioInvoice/a42bf7da-0816-4373-a7d5-75d4147c5f24.doc",
    "https://example.com/invoice.docx", "https://example.com/invoice.gif",
    "https://example.com/invoice.doc?download=invoice.pdf",
    "https://example.com/invoice%2EDOC#invoice.pdf", "https://example.com/download?id=invoice.pdf"
};
var extensionStore = new FakeInvoiceStore
{
    Rows = unsupportedPaths.Concat(supportedPaths).Select((path, index) =>
        new ConsoleBatch.PendingInvoice(index + 100, index + 200, path)).ToArray()
};
var extensionAnalyzed = new List<string>();
var extensionBatch = new ConsoleBatch.InvoiceBatchProcessor(extensionStore, (row, token) =>
{
    extensionAnalyzed.Add(row.FilePath);
    return Task.FromResult(new ConsoleInvoice());
});
using (var extensionProgress = new StringWriter())
{
    var extensionSummary = await extensionBatch.RunAsync(extensionProgress, CancellationToken.None);
    Assert(extensionSummary == new ConsoleBatch.BatchSummary(supportedPaths.Length + unsupportedPaths.Length,
        supportedPaths.Length, 0, unsupportedPaths.Length), "Unsupported extensions count as skipped rather than failed");
    Assert(extensionAnalyzed.SequenceEqual(supportedPaths), "Only supported file paths reach analysis, ignoring case, query strings, and fragments");
    Assert(extensionStore.SaveAttempts.Count == supportedPaths.Length && extensionStore.ErrorSaveAttempts.Count == 0,
        "Skipped file types do not update invoice results or errors");
    Assert(extensionProgress.ToString().Contains("file extension '.doc' is unsupported") &&
        extensionProgress.ToString().Contains("file extension '(none)' is unsupported"), "Skipped files report the extension and reason");
}
if (args is ["--file-extensions"]) { Console.WriteLine($"{checks} focused checks passed."); return; }

const string azureSample = """
{
  "status": "succeeded",
  "analyzeResult": {
    "content": "ABC Limited Invoice INV-10025 21/10/2026 GBP 1200 VAT 200",
    "documents": [{
      "fields": {
        "VendorName": { "type": "string", "valueString": "ABC Limited" },
        "VendorAddress": { "valueAddress": { "road": "Main Street", "city": "London", "postalCode": "SW1A 1AA", "countryRegion": "GBR" } },
        "InvoiceId": { "valueString": "INV-10025" },
        "InvoiceDate": { "valueDate": "2026-10-21" },
        "InvoiceTotal": { "valueCurrency": { "amount": 1200.00, "currencyCode": "GBP", "currencySymbol": "£" } },
        "TotalTax": { "valueCurrency": { "amount": 200.00, "currencyCode": "GBP" } },
        "Items": { "valueArray": [{ "valueObject": { "Description": { "valueString": "Product A" } } }] }
      }
    }]
  }
}
""";
using (var sample = JsonDocument.Parse(azureSample))
{
    var invoice = AzureInvoiceParser.Parse(sample.RootElement).Invoice;
    Assert(invoice.Org == "ABC Limited", "Azure supplier extraction");
    Assert(invoice.Ivn == "INV-10025", "Azure invoice number extraction");
    Assert(invoice.Dat == "21/10/2026", "UK invoice date normalization");
    Assert(invoice.Tot == 1200m && invoice.Vat == 200m && invoice.Net == 1000m, "Invoice amounts and net calculation");
    Assert(invoice.Currency == "GBP", "ISO currency code");
    Assert(invoice.Lin.Count == 1 && invoice.Lin[0] == "Product A", "Invoice line items");
    Assert(invoice.Address.Contains("London"), "Azure vendor address");
}
var amountCases = new (string Name, decimal? Net, decimal? Vat, decimal? Total, decimal? ExpectedNet, decimal? ExpectedVat, decimal? ExpectedTotal)[]
{
    ("zero net", 0m, 20m, 120m, 100m, 20m, 120m),
    ("missing net", null, 20m, 120m, 100m, 20m, 120m),
    ("zero VAT", 100m, 0m, 120m, 100m, 20m, 120m),
    ("missing VAT", 100m, null, 120m, 100m, 20m, 120m),
    ("zero total", 100m, 20m, 0m, 100m, 20m, 120m),
    ("missing total", 100m, 20m, null, 100m, 20m, 120m),
    ("total only with zeros", 0m, 0m, 120m, 120m, 0m, 120m),
    ("total only with missing amounts", null, null, 120m, 120m, null, 120m),
    ("net only with zeros", 100m, 0m, 0m, 100m, 0m, 100m),
    ("net only with missing amounts", 100m, null, null, 100m, null, 100m),
    ("reconciled amounts unchanged", 100m, 20m, 120m, 100m, 20m, 120m),
    ("nonzero mismatch unchanged", 100m, 20m, 130m, 100m, 20m, 130m),
    ("all missing stay null", null, null, null, null, null, null),
    ("all zeros stay zero", 0m, 0m, 0m, 0m, 0m, 0m),
    ("total below VAT unchanged", 0m, 20m, 10m, 0m, 20m, 10m),
    ("total below net unchanged", 100m, 0m, 90m, 100m, 0m, 90m)
};
foreach (var documentType in new[] { "Invoice", "Receipt" })
{
    foreach (var test in amountCases)
    {
        var fields = new Dictionary<string, object>();
        if (test.Net.HasValue) fields[documentType == "Receipt" ? "Subtotal" : "SubTotal"] = new { valueNumber = test.Net.Value };
        if (test.Vat.HasValue) fields["TotalTax"] = new { valueNumber = test.Vat.Value };
        if (test.Total.HasValue) fields[documentType == "Receipt" ? "Total" : "InvoiceTotal"] = new { valueNumber = test.Total.Value };
        using var sample = JsonDocument.Parse(JsonSerializer.Serialize(new { analyzeResult = new { documents = new[] { new { fields } } } }));
        var invoice = AzureInvoiceParser.Parse(sample.RootElement, documentType).Invoice;
        Assert(invoice.Net == test.ExpectedNet && invoice.Vat == test.ExpectedVat && invoice.Tot == test.ExpectedTotal,
            $"{documentType} amount correction: {test.Name}");
        if (test.Name == "nonzero mismatch unchanged")
            Assert(invoice.ReviewRequired && invoice.Warnings.Any(warning => warning.Contains("does not reconcile")),
                $"{documentType} unresolved amount mismatch still warns");
    }
}
Assert(InvoiceUtilities.UkDate("2026-01-09") == "09/01/2026", "ISO date conversion");
Assert(InvoiceUtilities.CurrencyCode("$") == "", "Ambiguous dollar sign isn't guessed");
Assert(InvoiceUtilities.IsPrivateAddress(System.Net.IPAddress.Parse("169.254.169.254")), "Cloud metadata IP blocked");
Assert(!InvoiceUtilities.IsPrivateAddress(System.Net.IPAddress.Parse("8.8.8.8")), "Public IP allowed");
var parsed = SpreadsheetService.ParseCsv("Memo,Amount\n\"Shop, London\",\"125.00\"\n");
Assert(parsed.Count == 2 && parsed[1][0] == "Shop, London", "Quoted CSV field parsing");
var fakeAzure = new FakeAzureHandler(azureSample);
var azureService = new AzureDocumentService(new HttpClient(fakeAzure), Options.Create(new AzureDocumentIntelligenceOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var fakeAzureResult = await azureService.AnalyzeAsync("https://example.com/test.pdf", CancellationToken.None);
Assert(fakeAzure.Requests == 2 && fakeAzureResult.Invoice.Ivn == "INV-10025", "Azure REST POST and polling works against stub service");
var fakeAi = new FakeOpenAiHandler();
var openAi = new OpenAiService(new HttpClient(fakeAi), Options.Create(new OpenAIOptions { ApiKey = "TEST_KEY", FineTunedSupplierModel = "ft:dummy" }), Options.Create(new LimitsOptions()));
var supplier = await openAi.PredictSupplierAsync("ABC Limited", CancellationToken.None);
Assert(supplier.GetValueOrDefault("AnalysisLedger") == "Expenses", "Supplier fine-tuned output parsing");
const string receiptSample = """
{
  "status": "succeeded",
  "analyzeResult": {
    "content": "TESCO Pay at Pump 43.75 litre £87.02 VAT £14.50",
    "documents": [{
      "docType": "receipt.retail",
      "fields": {
        "MerchantName": { "type": "string", "valueString": "TESCO", "confidence": 0.98 },
        "TransactionDate": { "valueDate": "2026-10-21" },
        "Total": { "valueNumber": 87.02, "content": "£87.02" },
        "TotalTax": { "valueNumber": 14.50 },
        "Subtotal": { "valueNumber": 72.52 },
        "ReceiptType": { "valueString": "Itemized" },
        "Items": { "valueArray": [{ "valueObject": { "Description": { "valueString": "Diesel" } } }] }
      }
    }]
  }
}
""";
using (var sample = JsonDocument.Parse(receiptSample))
{
    var receipt = AzureInvoiceParser.Parse(sample.RootElement, "Receipt").Invoice;
    Assert(receipt.DocumentType == "Receipt" && receipt.ModelId == "prebuilt-receipt", "Receipt document type and model ID");
    Assert(receipt.Org == "TESCO" && receipt.Ivn == "", "Receipt supplier and no invented invoice number");
    Assert(receipt.Dat == "21/10/2026" && receipt.Currency == "GBP", "Receipt date and GBP from pound symbol");
    Assert(receipt.Tot == 87.02m && receipt.Vat == 14.50m && receipt.Net == 72.52m, "Receipt net, VAT, and total");
    Assert(receipt.Lin.Count == 1 && receipt.Lin[0] == "Diesel", "Receipt line item");
}
var dual = new FakeDualAzureHandler(azureSample, receiptSample);
var dualAzure = new AzureDocumentService(new HttpClient(dual), Options.Create(new AzureDocumentIntelligenceOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var comparisonTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
var detected = await dualAzure.AnalyzeAsync("https://example.com/test.jpg", "Auto", CancellationToken.None, comparisonTrace);
Assert(detected.Invoice.DocumentType == "Invoice" && dual.InvoicePosts == 1 && dual.ReceiptPosts == 1, "Auto compares invoice and receipt analyses");
Assert(comparisonTrace.Models.Count == 2 && comparisonTrace.Models.Count(model => model.IsSelected == true) == 1 &&
    comparisonTrace.Models[0].IsSelected == true && comparisonTrace.Models[1].IsSelected == false &&
    comparisonTrace.Models.All(model => model.QualityScore.HasValue && model.Status == "Success" &&
        model.CompletedAtUtc >= model.StartedAtUtc && model.DetectionMethod == "ModelComparison" && !string.IsNullOrEmpty(model.ParserVersion)),
    "Comparison trace includes both models, scores, timing, version, and one winner");
using (var raw = JsonDocument.Parse(comparisonTrace.Models[0].RawAzureResultJson!))
{
    var rawFields = raw.RootElement.GetProperty("analyzeResult").GetProperty("documents")[0].GetProperty("fields");
    Assert(rawFields.GetProperty("Items").GetProperty("valueArray").GetArrayLength() == 1 && !rawFields.TryGetProperty("SubTotal", out _) &&
        comparisonTrace.Models[0].ParsedResult?.Net == 1000m,
        "Trace preserves raw line items and original amounts alongside corrected parsed amounts");
}
var manualTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
var forcedReceipt = await dualAzure.AnalyzeAsync("https://example.com/test.jpg", "Receipt", CancellationToken.None, manualTrace);
Assert(forcedReceipt.Invoice.DocumentType == "Receipt" && forcedReceipt.Invoice.DetectionMethod == "Manual", "Force receipt model");
Assert(manualTrace.Models.Count == 1 && manualTrace.Models[0].IsSelected == true && manualTrace.Models[0].DetectionMethod == "Manual" &&
    manualTrace.AttemptId != comparisonTrace.AttemptId, "Forced model logs only its own result and retries have distinct attempt IDs");
var shortcutHandler = new FakeDualAzureHandler(azureSample, receiptSample);
var shortcutAzure = new AzureDocumentService(new HttpClient(shortcutHandler), Options.Create(new AzureDocumentIntelligenceOptions
    { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY", AutoCompareModels = false }));
var shortcutTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
await shortcutAzure.AnalyzeAsync("https://example.com/test.jpg", "Auto", CancellationToken.None, shortcutTrace);
Assert(shortcutTrace.Models.Count == 1 && shortcutTrace.Models[0].IsSelected == true && shortcutHandler.ReceiptPosts == 0 &&
    shortcutTrace.Models[0].DetectionMethod == "HighQualityInvoice", "High-quality shortcut does not log an unattempted receipt model");
var classifiedHandler = new FakeDualAzureHandler(azureSample, receiptSample, classifier: true);
var classifiedAzure = new AzureDocumentService(new HttpClient(classifiedHandler), Options.Create(new AzureDocumentIntelligenceOptions
    { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY", ClassifierId = "test-classifier" }));
var classifiedTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
await classifiedAzure.AnalyzeAsync("https://example.com/test.jpg", "Auto", CancellationToken.None, classifiedTrace);
Assert(classifiedTrace.Models.Count == 1 && classifiedTrace.Models[0] is { DocumentType: "Receipt", IsSelected: true, DetectionMethod: "Classifier" } &&
    classifiedTrace.Models[0].ParsedResult!.DocumentTypeConfidence == 0.99 && classifiedHandler.InvoicePosts == 0,
    "Classifier selection logs only the selected model and preserves classifier confidence");
var partialAzure = new AzureDocumentService(new HttpClient(new FakeDualAzureHandler(azureSample, receiptSample, failInvoice: true)),
    Options.Create(new AzureDocumentIntelligenceOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var partialTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
await partialAzure.AnalyzeAsync("https://example.com/test.jpg", "Auto", CancellationToken.None, partialTrace);
Assert(partialTrace.Models[0] is { Status: "Error", IsSelected: false, ParsedResult: null } &&
    partialTrace.Models[0].RawAzureResultJson!.Contains("BadDocument") && partialTrace.Models[0].ErrorMessage!.Contains("HTTP 400") &&
    partialTrace.Models[1] is { Status: "Success", IsSelected: true } && partialTrace.Models[1].ParsedResult!.ReviewRequired,
    "One model failure retains the Azure error JSON and the other model's selection and warnings");
const string unparseableAzure = """{"status":"succeeded","analyzeResult":{"documents":[]}}""";
var parseFailureAzure = new AzureDocumentService(new HttpClient(new FakeDualAzureHandler(unparseableAzure, receiptSample)),
    Options.Create(new AzureDocumentIntelligenceOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var parseFailureTrace = new MYT.InvoiceRecognition.Api.Models.AzureAnalysisTrace();
await parseFailureAzure.AnalyzeAsync("https://example.com/test.jpg", "Auto", CancellationToken.None, parseFailureTrace);
Assert(parseFailureTrace.Models[0].Status == "Error" && parseFailureTrace.Models[0].RawAzureResultJson == unparseableAzure &&
    parseFailureTrace.Models[0].ParsedResult == null, "Raw Azure response survives a parser failure");

var loggedRow = new ConsoleBatch.PendingInvoice(123, 456, "https://example.com/logged.pdf");
var historyStore = new FakeRecognitionLogStore();
var consoleAzure = new ConsoleAzure(new HttpClient(new FakeDualAzureHandler(azureSample, receiptSample)),
    Options.Create(new ConsoleAzureOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var loggedAnalyzer = new ConsoleBatch.LoggedInvoiceAnalyzer(async (row, trace, token) =>
    (await consoleAzure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice, historyStore, TextWriter.Null);
var loggedResult = await loggedAnalyzer.AnalyzeAsync(loggedRow, CancellationToken.None);
Assert(historyStore.SavedRow == loggedRow && historyStore.Trace!.Models.Count == 2 &&
    ReferenceEquals(historyStore.Trace.Models.Single(model => model.IsSelected == true).ParsedResult, loggedResult),
    "Database analyzer carries source identifiers and both final model results to the log store");
var bothFailedAzure = new ConsoleAzure(new HttpClient(new FakeDualAzureHandler(azureSample, receiptSample, true, true)),
    Options.Create(new ConsoleAzureOptions { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
var failedHistoryStore = new FakeRecognitionLogStore();
var failedAnalyzer = new ConsoleBatch.LoggedInvoiceAnalyzer(async (row, trace, token) =>
    (await bothFailedAzure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice, failedHistoryStore, TextWriter.Null);
var bothFailed = false;
try { await failedAnalyzer.AnalyzeAsync(loggedRow, CancellationToken.None); }
catch (InvalidOperationException error) { bothFailed = error.Message.Contains("Neither Azure model"); }
Assert(bothFailed && failedHistoryStore.Trace!.Models.Count == 2 &&
    failedHistoryStore.Trace.Models.All(model => model.Status == "Error" && model.IsSelected == null),
    "Both failed model attempts are logged before the original analysis exception propagates");
using (var logProgress = new StringWriter())
{
    var brokenHistoryStore = new FakeRecognitionLogStore { Fail = true };
    var resilientAnalyzer = new ConsoleBatch.LoggedInvoiceAnalyzer(async (row, trace, token) =>
        (await consoleAzure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice, brokenHistoryStore, logProgress);
    Assert((await resilientAnalyzer.AnalyzeAsync(loggedRow, CancellationToken.None)).Ivn == "INV-10025" &&
        logProgress.ToString().Contains("recognition log could not be saved"), "Log persistence failure does not fail successful analysis");
    Assert(logProgress.ToString().Contains("UploadedInvoice 123, Invoice 456, Attempt ") &&
        logProgress.ToString().Contains("Log database unavailable") &&
        logProgress.ToString().Contains("INSERT permission denied on InvoiceRecognitionLog") &&
        logProgress.ToString().Contains(nameof(FakeRecognitionLogStore)),
        "Recognition log errors print identifiers, exception message, inner error, and stack trace");
    var resilientFailure = new ConsoleBatch.LoggedInvoiceAnalyzer(async (row, trace, token) =>
        (await bothFailedAzure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice, brokenHistoryStore, logProgress);
    var originalPreserved = false;
    try { await resilientFailure.AnalyzeAsync(loggedRow, CancellationToken.None); }
    catch (InvalidOperationException error) { originalPreserved = error.Message.Contains("Neither Azure model"); }
    Assert(originalPreserved, "Log persistence failure does not replace the original analysis error");
}
using (var analysisCancellation = new CancellationTokenSource())
{
    var cancelAzure = new ConsoleAzure(new HttpClient(new FakeDualAzureHandler(azureSample, receiptSample,
        cancelOnReceipt: analysisCancellation)), Options.Create(new ConsoleAzureOptions
        { Endpoint = "https://resource.cognitiveservices.azure.com/", ApiKey = "TEST_KEY" }));
    var canceledHistory = new FakeRecognitionLogStore();
    var cancelAnalyzer = new ConsoleBatch.LoggedInvoiceAnalyzer(async (row, trace, token) =>
        (await cancelAzure.AnalyzeAsync(row.FilePath, "Auto", token, trace)).Invoice, canceledHistory, TextWriter.Null);
    var wasCanceled = false;
    try { await cancelAnalyzer.AnalyzeAsync(loggedRow, analysisCancellation.Token); }
    catch (OperationCanceledException) { wasCanceled = true; }
    Assert(wasCanceled && canceledHistory.Trace!.Models.Count == 1 &&
        canceledHistory.Trace.Models[0] is { DocumentType: "Invoice", Status: "Success", IsSelected: null },
        "Cancellation preserves completed model history and omits the interrupted model");
}
var batchStore = new FakeInvoiceStore();
var analyzedUrls = new List<string>();
var batch = new ConsoleBatch.InvoiceBatchProcessor(batchStore, (row, token) =>
{
    var url = row.FilePath;
    analyzedUrls.Add(url);
    if (url.EndsWith("/failed.pdf", StringComparison.Ordinal)) throw new InvalidOperationException("Azure failed");
    return Task.FromResult(new ConsoleInvoice { Ivn = "INV-BATCH", Org = "Batch Vendor", Dat = "21/10/2026", Currency = "GBP" });
});
var batchSummary = await batch.RunAsync(TextWriter.Null, CancellationToken.None);
Assert(batchSummary == new ConsoleBatch.BatchSummary(5, 1, 3, 1), "Batch counts success, analysis failure, empty URL, save failure, and changed row");
Assert(analyzedUrls.SequenceEqual(new[] { "https://example.com/good.pdf", "https://example.com/failed.pdf", "https://example.com/save-failed.pdf", "https://example.com/changed.pdf" }), "Batch uses FilePath and continues after per-row failures");
Assert(batchStore.SaveAttempts.SequenceEqual(new[] { 1, 4, 5 }), "Batch saves only successfully analyzed documents");
Assert(batchStore.SavedInvoice is { Ivn: "INV-BATCH", Org: "Batch Vendor", Dat: "21/10/2026", Currency: "GBP" }, "Batch passes extracted fields to database store");
Assert(batchStore.SavedErrors.SequenceEqual(new[] { (2, "Azure failed"), (3, "FilePath is empty."), (4, "Database update failed") }),
    "Batch saves the original exception messages for failed rows");
var errorFailureStore = new FakeInvoiceStore { ErrorSaveFailureId = 2, ErrorSaveSkippedId = 5 };
var errorFailureBatch = new ConsoleBatch.InvoiceBatchProcessor(errorFailureStore, (url, token) =>
    throw new InvalidOperationException("Analysis failed"));
using (var errorProgress = new StringWriter())
{
    var errorSummary = await errorFailureBatch.RunAsync(errorProgress, CancellationToken.None);
    Assert(errorSummary == new ConsoleBatch.BatchSummary(5, 0, 5, 0) && errorFailureStore.ErrorSaveAttempts.SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
        "Batch continues after error persistence fails or a failed row changes");
    Assert(errorFailureStore.SavedErrors.Select(error => error.Id).SequenceEqual(new[] { 1, 3, 4 }) &&
        errorProgress.ToString().Contains("error could not be saved") && errorProgress.ToString().Contains("row or file changed"),
        "Batch reports failed and skipped error updates accurately");
}
using (var batchCancellation = new CancellationTokenSource())
{
    var canceledStore = new FakeInvoiceStore();
    var canceledBatch = new ConsoleBatch.InvoiceBatchProcessor(canceledStore, (url, token) =>
    {
        batchCancellation.Cancel();
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ConsoleInvoice());
    });
    var cancellationPropagated = false;
    try { await canceledBatch.RunAsync(TextWriter.Null, batchCancellation.Token); }
    catch (OperationCanceledException) { cancellationPropagated = true; }
    Assert(cancellationPropagated && canceledStore.SaveAttempts.Count == 0 && canceledStore.ErrorSaveAttempts.Count == 0,
        "Batch cancellation stops processing without saving an error");
}
Console.WriteLine($"{checks} local checks passed.");

internal sealed class FakeInvoiceStore : ConsoleBatch.IInvoiceStore
{
    public IReadOnlyList<ConsoleBatch.PendingInvoice>? Rows { get; init; }
    public List<int> SaveAttempts { get; } = new();
    public List<int> ErrorSaveAttempts { get; } = new();
    public List<(int Id, string Message)> SavedErrors { get; } = new();
    public int? ErrorSaveFailureId { get; init; }
    public int? ErrorSaveSkippedId { get; init; }
    public ConsoleInvoice? SavedInvoice { get; private set; }

    public Task<IReadOnlyList<ConsoleBatch.PendingInvoice>> LoadPendingAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConsoleBatch.PendingInvoice>>(Rows ?? new[]
        {
            new ConsoleBatch.PendingInvoice(1, 11, "https://example.com/good.pdf"),
            new ConsoleBatch.PendingInvoice(2, 12, "https://example.com/failed.pdf"),
            new ConsoleBatch.PendingInvoice(3, 13, ""),
            new ConsoleBatch.PendingInvoice(4, 14, "https://example.com/save-failed.pdf"),
            new ConsoleBatch.PendingInvoice(5, 15, "https://example.com/changed.pdf")
        });

    public Task<bool> SaveAsync(ConsoleBatch.PendingInvoice row, ConsoleInvoice invoice, CancellationToken cancellationToken)
    {
        var id = (int)row.Id;
        SaveAttempts.Add(id);
        if (id == 4) throw new InvalidOperationException("Database update failed");
        if (id == 5) return Task.FromResult(false);
        SavedInvoice = invoice;
        return Task.FromResult(true);
    }

    public Task<bool> SaveErrorAsync(ConsoleBatch.PendingInvoice row, string message, CancellationToken cancellationToken)
    {
        var id = (int)row.Id;
        ErrorSaveAttempts.Add(id);
        if (id == ErrorSaveFailureId) throw new InvalidOperationException("Error update failed");
        if (id == ErrorSaveSkippedId) return Task.FromResult(false);
        SavedErrors.Add((id, message));
        return Task.FromResult(true);
    }
}

internal sealed class FakeAzureHandler : HttpMessageHandler
{
    private readonly string _sample;
    public int Requests { get; private set; }
    public FakeAzureHandler(string sample) { _sample = sample; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.Method == HttpMethod.Post)
        {
            var result = new HttpResponseMessage(HttpStatusCode.Accepted);
            result.Headers.Add("operation-location", "https://resource.cognitiveservices.azure.com/formrecognizer/documentModels/prebuilt-invoice/analyzeResults/123?api-version=2023-07-31");
            return Task.FromResult(result);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_sample) });
    }
}

internal sealed class FakeOpenAiHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "AnalysisLedger: Expenses\nAnalysisAccount: Office Supplies" } } } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}

internal sealed class FakeRecognitionLogStore : ConsoleBatch.IRecognitionLogStore
{
    public bool Fail { get; init; }
    public bool WaitForCancellation { get; init; }
    public bool CanceledAtSave { get; private set; }
    public ConsoleBatch.PendingInvoice? SavedRow { get; private set; }
    public ConsoleTrace? Trace { get; private set; }

    public Task SaveAsync(ConsoleBatch.PendingInvoice row, ConsoleTrace trace, CancellationToken cancellationToken)
    {
        CanceledAtSave = cancellationToken.IsCancellationRequested;
        if (Fail) throw new InvalidOperationException("Log database unavailable",
            new Exception("INSERT permission denied on InvoiceRecognitionLog"));
        SavedRow = row;
        Trace = trace;
        if (WaitForCancellation) return Task.Delay(Timeout.Infinite, cancellationToken);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDualAzureHandler : HttpMessageHandler
{
    private readonly string _invoice;
    private readonly string _receipt;
    private readonly bool _failInvoice;
    private readonly bool _failReceipt;
    private readonly bool _classifier;
    private readonly CancellationTokenSource? _cancelOnReceipt;
    public int InvoicePosts { get; private set; }
    public int ReceiptPosts { get; private set; }
    public FakeDualAzureHandler(string invoice, string receipt, bool failInvoice = false, bool failReceipt = false,
        bool classifier = false, CancellationTokenSource? cancelOnReceipt = null)
    {
        _invoice = invoice; _receipt = receipt; _failInvoice = failInvoice; _failReceipt = failReceipt;
        _classifier = classifier; _cancelOnReceipt = cancelOnReceipt;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_classifier && request.RequestUri!.AbsolutePath.Contains("documentClassifiers", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Method == HttpMethod.Post)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Add("operation-location", "https://resource.cognitiveservices.azure.com/formrecognizer/documentClassifiers/test-classifier/analyzeResults/123");
                return Task.FromResult(accepted);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"status":"succeeded","analyzeResult":{"documents":[{"docType":"Receipt","confidence":0.99}]}}""") });
        }
        var receipt = request.RequestUri!.AbsolutePath.Contains("prebuilt-receipt", StringComparison.OrdinalIgnoreCase);
        if (receipt && _cancelOnReceipt != null)
        {
            _cancelOnReceipt.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (request.Method == HttpMethod.Post)
        {
            if (receipt) ReceiptPosts++; else InvoicePosts++;
            if (receipt ? _failReceipt : _failInvoice)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("""{"error":{"code":"BadDocument","message":"Cannot analyze document"}}""") });
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.Add("operation-location", $"https://resource.cognitiveservices.azure.com/formrecognizer/documentModels/{(receipt ? "prebuilt-receipt" : "prebuilt-invoice")}/analyzeResults/test?api-version=2023-07-31");
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(receipt ? _receipt : _invoice) });
    }
}
