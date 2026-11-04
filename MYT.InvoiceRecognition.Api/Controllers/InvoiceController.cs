using Microsoft.AspNetCore.Mvc;
using MYT.InvoiceRecognition.Api.Models;
using MYT.InvoiceRecognition.Api.Services;

namespace MYT.InvoiceRecognition.Api.Controllers;

[ApiController]
[Route("")]
public sealed class InvoiceController : ControllerBase
{
    private readonly InvoiceRecognitionService _invoices;
    private readonly OpenAiService _ai;
    private readonly BankReconciliationService _bank;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(InvoiceRecognitionService invoices, OpenAiService ai, BankReconciliationService bank, ILogger<InvoiceController> logger)
    {
        _invoices = invoices;
        _ai = ai;
        _bank = bank;
        _logger = logger;
    }

    [HttpGet("")]
    public IActionResult Home() => Ok(new { message = "Welcome to MYT Invoice Parser" });

    [HttpPost("parse_invoice")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> ParseInvoice([FromForm(Name = "invoice_url")] string invoiceUrl, [FromQuery] string? currency, [FromQuery] string? documentType, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _invoices.ParseAsync(invoiceUrl, currency, false, documentType, cancellationToken);
            return Ok(new InvoiceResponse<InvoiceData> { Message = "invoice parsed successfully", Data = result });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return Error(error, "unable to parse invoice", "not parsable information"); }
    }

    [HttpPost("agent_invoice")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> AgentInvoice([FromForm(Name = "invoice_url")] string invoiceUrl, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _invoices.ParseAsync(invoiceUrl, null, true, cancellationToken);
            return Ok(new InvoiceResponse<InvoiceData> { Message = "invoice parsed successfully", Data = result });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return Error(error, "unable to parse invoice", "check logs for agent errors"); }
    }

    [HttpPost("supplier_validation")]
    public async Task<IActionResult> SupplierValidation([FromQuery(Name = "vendor_name")] string vendorName, CancellationToken cancellationToken)
    {
        try
        {
            if (!_ai.IsSupplierConfigured) throw new InvalidOperationException("OpenAI FineTunedSupplierModel must be configured for supplier matching.");
            var result = await _ai.PredictSupplierAsync(vendorName, cancellationToken);
            return Ok(new InvoiceResponse<Dictionary<string, string>> { Message = "vendor validated successfully", Data = result });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return Error(error, "unable to parse input", "supplier matching failed"); }
    }

    [HttpPost("bank_statement")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(22000000)]
    public async Task<IActionResult> BankStatement([FromForm(Name = "bank_statement_file")] IFormFile bankStatementFile, [FromForm(Name = "invoice_file")] IFormFile invoiceFile, CancellationToken cancellationToken)
    {
        try
        {
            var matches = await _bank.MatchAsync(bankStatementFile, invoiceFile, cancellationToken);
            return Ok(new InvoiceResponse<object> { Message = "invoice-bank statement validated successfully", Data = new { matches } });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return Error(error, "unable to parse input files", "invoice/bank statement not parsable"); }
    }

    private IActionResult Error(Exception error, string message, string details)
    {
        _logger.LogError(error, "Invoice API request failed.");
        var safeMessage = error is ArgumentException or InvalidOperationException or TimeoutException ? error.Message : "Processing failed; review server logs.";
        return BadRequest(new ErrorResponse { Message = message, Errors = safeMessage, Details = details });
    }
}
