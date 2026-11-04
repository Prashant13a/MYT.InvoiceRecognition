# Validation report - ASP.NET Core 8 conversion

## Checks performed here

- Verified `appsettings.json` and `launchSettings.json` parse as JSON.
- Verified both project files parse as XML.
- Verified 11 C# source files have balanced braces, parentheses and brackets using a lexical (not compiler) check.
- Verified the original four POST routes and their legacy parameter names appear in `InvoiceController.cs`.
- Confirmed the Azure 2023-07-31 document-analysis REST route against Microsoft Learn.
- Confirmed the selected ExcelDataReader, Swashbuckle and CodePages NuGet package versions exist.
- Inspected source for hardcoded API secrets; only placeholder values are included.

## Checks that could NOT be performed here

- `dotnet restore` / `dotnet build`: the authoring container does not have the .NET SDK; outbound NuGet downloads are blocked.
- Execution of the included `MYT.InvoiceRecognition.SmokeTests` C# test runner.
- Live Azure Document Intelligence analysis and OpenAI calls: no credentials or live service access.
- End-to-end MYT ASP.NET MVC integration: application instance not available.
- Real invoice accuracy evaluation: no ground-truth sample PDFs supplied.

**Status: source conversion delivered; compile, API, and production equivalence remain unverified.**

## Recommended verification on Visual Studio 2022

```powershell
dotnet restore MYT.InvoiceRecognition.Net8.sln
dotnet build MYT.InvoiceRecognition.Net8.sln -c Debug
dotnet run --project MYT.InvoiceRecognition.SmokeTests
dotnet run --project MYT.InvoiceRecognition.Api --launch-profile http
```

Test `/parse_invoice` first using 10 public sample invoices and compare field-by-field against the running Python implementation. Confirm supplier matching (if required) with a model ID the OpenAI API account can access before running the optional endpoints.
