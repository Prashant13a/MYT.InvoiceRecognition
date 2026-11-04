# MYT Invoice Recognition - ASP.NET Core 8 Web API

A C# migration of `aiml-invoice-formrecognizer-master` (original FastAPI/Python project).

**Migration status:** Complete source implementation, **not production-verified**. The conversion is based on the supplied ZIP. It has not been compiled, restored, or exercised against live Azure/OpenAI services in the authoring environment (no .NET SDK or outbound package network was available). Build and run the included smoke tests on your own .NET 8 SDK workstation before deployment. See **Validation checklist** below.

## Scope and compatibility

| Original FastAPI endpoint | C# endpoint | Notes |
|---|---|---|
| `GET /` | `GET /` | Same welcome message |
| `POST /parse_invoice` | `POST /parse_invoice` | **Priority endpoint**; `invoice_url` as form field; optional `currency` as query parameter; same response keys |
| `POST /agent_invoice` | `POST /agent_invoice` | Same input and response structure; replaces fragile LangChain chain with Azure + optional OpenAI verification of missing fields |
| `POST /supplier_validation?vendor_name=...` | Same | Same query parameter; configurable fine-tuned OpenAI model |
| `POST /bank_statement` | Same | Same two multipart files; supports CSV, XLS, XLSX; OpenAI returns matches |
| `/docs`, `/openapi.json`, `/redoc` | Same paths | Swagger UI, OpenAPI JSON, ReDoc |

`/parse_invoice` response (illustrative):

```json
{
  "data": {
    "org": "ABC Limited",
    "address": "Main Street London",
    "ivn": "INV-10025",
    "dat": "21/10/2026",
    "lin": ["Product A"],
    "tot": 1200.00,
    "vat": 200.00,
    "net": 1000.00,
    "currency": "GBP",
    "supplier_info": {}
  },
  "message": "invoice parsed successfully",
  "errors": null,
  "status": 200
}
```

The response field names match Python. A few intentionally improved behaviors are **not byte-for-byte identical**: when Azure has no numeric field, the API returns `null` rather than the Python implementation's inconsistent empty `[]`; date is consistently `dd/MM/yyyy`; currency favors Azure ISO code rather than always calling OpenAI; `supplier_info` defaults to `{}` if the fine-tuned model is not configured/unavailable. If your MVC client expects exact legacy edge-case types, test against your existing invoice samples.

## Azure-only console application

`MYT.InvoiceRecognition.Console` is a .NET 8 console application in the same solution. It processes pending invoices from SQL Server by default, with no command-line arguments required. It calls Azure Document Intelligence directly, reusing the Azure parser without compiling or calling the OpenAI services.

Configure Azure using environment variables:

```powershell
$env:AzureDocumentIntelligence__Endpoint = "https://YOUR-RESOURCE.cognitiveservices.azure.com/"
$env:AzureDocumentIntelligence__ApiKey = "YOUR-AZURE-KEY"
dotnet run --project MYT.InvoiceRecognition.Console
```

The console reads its own `appsettings.json`, the same user secrets as the API (`MYT.InvoiceRecognition.Net8.Local`), then environment variables in that order. Existing Azure user secrets therefore work automatically. Keep API keys in secrets or environment variables. OpenAI settings are ignored.

Each database `FilePath` URL must be public and directly downloadable by Azure. The console uses the existing Azure auto-detection of invoices and receipts. JSON summary output goes to stdout, errors and progress go to stderr. Exit codes: `0` success, `1` processing failure, `2` invalid arguments, `130` Ctrl+C cancellation. Use `--help` for usage. The optional `parse_invoice` command is still accepted; the `--invoice_url` and `--database` flags have been removed.

### Process invoices from SQL Server

Exceptions caught by the general exception handlers in console `Program.cs` are also appended to `Log/yyyy-MM-dd HH.txt` under the application directory (for example, `bin/Debug/net8.0/Log` during local development). Entries include a local timestamp with UTC offset, the database setting name when applicable, and the full exception including inner exceptions and stack traces. The folder is created automatically and must be writable by the account running the console. If file logging fails, the logger reports the failure and original error to stderr. Files are retained until removed manually. Run `dotnet run --project MYT.InvoiceRecognition.SmokeTests -- --error-text-log` to check text logging locally.

The console processes `ConnectionStrings:InvoiceDatabase` first, then `ConnectionStrings:InvoiceDatabaseIN`. Set both connection strings in the console's `appsettings.json`, shared user secrets, or environment variables (`ConnectionStrings__InvoiceDatabase` and `ConnectionStrings__InvoiceDatabaseIN`). The new `InvoiceDatabaseIN` setting is empty until configured. The Windows account running the console needs read access to `Invoice` and read/update access to `UploadedInvoice` in each database.

Each database runs independently through `InvoiceProcessingRunner.RunAsync(connectionString, options, cancellationToken, progress)`, including supplier resolution for that database's successfully saved invoices. The console logs timestamped `START` and `END` messages to stderr with the database setting name, final status, and duration. A database failure does not prevent processing the second database; any failure returns exit code `1`. Ctrl+C stops processing and returns `130`. Connection strings are not included in the start/end logs. Each database's JSON output includes `Database` and `Result`; `Result` contains `Summary`, `SupplierResolution`, and `ExitCode`.

```powershell
dotnet run --project MYT.InvoiceRecognition.Console
```

This selects `UploadedInvoice` rows with `TaskStatus IN ('Error', 'PENDING')`, joins `Invoice` on `InvoiceId`, and processes each `FilePath` URL sequentially using Azure only. Each successful parse updates the corresponding `UploadedInvoice`:

| Azure field | Database column |
|---|---|
| Invoice number (`ivn`) | `InvoiceNumber` |
| Supplier/merchant (`org`) | `InvoiceVendorName` |
| Invoice/transaction date (`dat`) | `InvoiceMissionDate` |
| Currency (`currency`) | `Currency` |
| Net (`net`) | `InvoiceBaseAmount` |
| VAT (`vat`) | `InvoiceVatAmount` |
| Total (`tot`) | `InvoiceTotalAmount` |

The same update sets `TaskStatus = 'Success'`, `IsReviewed = 0`, `IsRejected = 0`, and `IsDuplicated = 0`. Missing Azure fields preserve existing column values, including missing amounts; extracted zero amounts are saved. Amounts use decimal SQL parameters. Date values support SQL date/datetime columns and text columns (`dd/MM/yyyy`). Line items are not persisted because their database columns were not provided.

If processing a row fails, the console saves `TaskStatus = 'ERROR'` and the exception's `Message` in `DuplicatResult`, then continues with the next row. Both `Error` and `ERROR` statuses are eligible for retry alongside `PENDING`. Updates check that status, invoice ID, and file path still match; changed rows are skipped. If saving the error also fails, the console reports that failure and continues. Cancellation stops processing without marking the current row as an error. Run one batch instance at a time to avoid duplicate Azure analysis. Azure warnings do not prevent the requested Success status or flag reset. Progress is written to stderr and a JSON count summary to stdout; any per-row failure produces exit code `1`.

Override the connection with `ConnectionStrings__InvoiceDatabase` or the shared user secret `ConnectionStrings:InvoiceDatabase`. Microsoft.Data.SqlClient enables encryption by default; configure a trusted SQL Server certificate as needed. Windows integrated authentication also requires a working Windows/SQL Server security context.

### Azure recognition history

Run [`Sql/CreateInvoiceRecognitionLog.sql`](Sql/CreateInvoiceRecognitionLog.sql) in the invoice database before using recognition logging. The script creates `dbo.InvoiceRecognitionLog` and its indexes, matching the ID types of the existing `dbo.UploadedInvoice` and `dbo.Invoice` tables. If the log table already exists, it makes no changes; an older manually created table must match the supplied schema. The console account also needs INSERT permission on the log table.

Database batch mode saves one history row for each invoice/receipt model actually attempted, grouping a document's models under one new `AttemptId` per processing attempt. Retries append history. Each row records model/API/application version, UTC timing, status/error, quality score, selection, review warnings, invoice date/number, supplier, currency, and amounts. `RawAzureResultJson` preserves Azure's complete response, including OCR text, confidence, and full line-item fields; `ParsedResultJson` preserves the parsed invoice after corrections, including line-item descriptions. The raw response remains available if parsing fails. Azure error responses are retained when they are valid JSON; failures without a JSON response have a null raw result.

History is written after model selection completes, before updating `UploadedInvoice`, and also when analysis throws. Rows are inserted together in a transaction. `IsSelected` is true for the winner, false for other attempts after a completed selection, and null when no selection completed. Classifier and high-quality shortcuts log only models actually attempted; classifier calls themselves are not stored as model rows. API requests do not write SQL history.

Log writes have a separate 10-second timeout. A logging failure is reported to stderr with the uploaded invoice ID, invoice ID, attempt ID, and full exception details (message, inner exceptions, stack trace, and SQL error diagnostics), without changing the analysis outcome or hiding its original error. Completed model attempts are also logged on cancellation; the canceled in-progress model is omitted. A process crash before the history write can leave that attempt unlogged. Keep access to the history table limited like the original invoices: its contents include document text and file URLs. Human-reviewed invoice values can later be compared with this history to measure extraction accuracy.

The console uses the installed .NET 8 ASP.NET Core shared framework for configuration, options, and logging abstractions, plus Microsoft.Data.SqlClient for SQL Server; it does not start a web server or require the API to run.

### Supplier names, approval, and historical replay

Before running the updated console, run `Sql/CreateInvoiceRecognitionLog.sql` (for a new database), then
[`Sql/UpgradeSupplierNameResolution.sql`](Sql/UpgradeSupplierNameResolution.sql) in the invoice database.
For an existing log table, run the upgrade script directly. It adds missing columns and creates the alias tables;
it can be run repeatedly. SQL Server 2016 or later is required for the alias context index.
The console checks the supplier schema before starting OCR. These scripts are supplied for deployment;
they are not automatically executed by the console.

`InvoiceRecognitionLog` now records `VATNumber`, `CompanyNumber`, `SupplierAddress`, and `SupplierCountry`,
plus the applied alias ID, resolved name, match source, and UTC resolution time. `SupplierName` remains
the original extraction. New optional JSON response fields are `supplierVatNumber`, `supplierCompanyNumber`,
and `supplierCountry`. Only explicitly supplier-labelled structured Azure fields are used: `VendorTaxId` /
`MerchantTaxId`, `VendorCompanyNumber` / `MerchantCompanyNumber`, and the supplier address. Not every Azure
model exposes these fields (especially company numbers); unavailable values stay empty/null. No external
company lookup or arbitrary OCR-number guessing is performed. `SupplierCountry` retains Azure's original
value; matching normalizes common GB/IE country variants separately.

After every completed batch, a supplier pass reads the latest successful, selected recognition log only for
uploaded invoices successfully saved in that batch, checking that the invoice ID and file path still match.
Failed, skipped, and historical invoices are excluded; an empty successful batch performs no supplier work.
Filtering happens in SQL before ranking the logs. It backfills available supplier
details from preserved structured Azure JSON without calling Azure again. Rows without a selected successful
log cannot be resolved this way. Invalid JSON is left untouched; no invoice is inferred from an unsuccessful attempt.

Supplier names must have more than three characters after trimming. Eligible names are registered in
`InvoiceSupplierNameAlias`, keyed by normalized OCR name plus VAT/company/country context. The same OCR name
with different identifiers gets a separate alias, preventing a global correction from merging distinct vendors.
Oversized identifiers/names are skipped rather than truncated. `OriginalName` is the OCR value; `AliasKey`
is a deterministic lookup key; `NormalizedName` is the human-approved canonical company name.
Approved references can suggest names by VAT number, company number with country, name alias, or exact stored
address. Known identifier conflicts and conflicting canonical names leave the suggestion unresolved.
Suggestions never approve themselves: each new alias starts with `IsApproved = 0`.

`InvoiceSupplierNameAliasInvoice` stores distinct alias/invoice links, matching the existing uploaded ID type.
`UploadedInvoiceIds` is refreshed as a comma-separated, ordered list from those links, so replay does not duplicate IDs.
Approved mappings retain their `NormalizedName`, `ApprovedBy`, `ApprovedOn`, and `MatchSource` during replay.
Approvers should review the linked documents and supplier identifiers before approval. Do not manually edit the
lookup key columns; they are generated from the extraction. The schema uses `ApprovedBy nvarchar(256)` to accommodate
your external review application's user identifier as text.

Review pending aliases:

```sql
SELECT Id, OriginalName, NormalizedName, VATNumber, CompanyNumber, SupplierCountry,
       SupplierAddress, MatchSource, UploadedInvoiceIds
FROM dbo.InvoiceSupplierNameAlias
WHERE IsApproved = 0
ORDER BY CreatedOn, Id;
```

Your approval handler should update the selected alias with parameterized values for the canonical name and reviewer:

```sql
UPDATE dbo.InvoiceSupplierNameAlias
SET NormalizedName = @NormalizedName, IsApproved = 1, MatchSource = 'Manual',
    ApprovedBy = @ApprovedBy, ApprovedOn = SYSUTCDATETIME(), UpdatedOn = SYSUTCDATETIME()
WHERE Id = @AliasId AND IsApproved = 0;
```

Only an approved alias with a populated canonical name and consistent approved evidence can update
`UploadedInvoice.InvoiceVendorName`. New identifier-based matches remain suggestions until that alias is approved.
The vendor pass updates no invoice amounts, dates, statuses, or review flags. It skips updates to reviewed invoices,
non-successful invoices, manually changed vendor names, changed files, and rows with newer selected recognition results.
Pending/error batch selection and both success/error saves also exclude `IsReviewed = 1`, including reviews completed
while OCR is running. The review page's backend is outside this repository: it continues to work, but automatically
learning from its manual corrections requires a separate integration. Invoice review does not imply alias approval.

Explicitly replay historical supplier resolution after approving aliases (no Azure configuration or calls required).
This command covers all eligible saved logs; it is never called automatically by normal batch processing:

```powershell
dotnet run --project MYT.InvoiceRecognition.Console -- resolve_suppliers
```

The vendor-only command runs sequentially against both configured databases and reports `Selected`, `Updated`, `Pending`, `Skipped`, and `Failed` under each result's `SupplierResolution` (`Summary` is null); `Updated` counts guarded
update statements applied, including an already canonical name on replay. Normal batch output retains the existing
batch counts and adds `SupplierResolution`. The database account needs SELECT/UPDATE on the log, SELECT/INSERT/UPDATE
on aliases, SELECT/INSERT on alias/invoice links, and SELECT/UPDATE on uploaded invoices. Schema deployment needs DDL
permission. Alias approvals need an authorized review handler; no approval UI is included here.

Focused checks:

```powershell
dotnet run --project MYT.InvoiceRecognition.SmokeTests -- --supplier-matching
dotnet run --project MYT.InvoiceRecognition.SmokeTests -- --supplier-batch
dotnet run --project MYT.InvoiceRecognition.SmokeTests -- --supplier-sql
```

The SQL command is opt-in and targets only local `.\SQLEXPRESS` with Windows authentication. It creates a uniquely
named temporary test database, tests migration/replay/review protection, and drops that test database in `finally`.
It does not read application connection strings or access the configured invoice database.

## Requirements

- Visual Studio 2022 (with ASP.NET and web development workload) and .NET 8 SDK
- Azure Document Intelligence resource with `prebuilt-invoice` enabled, endpoint and key
- Optional: OpenAI API key for missing date/currency extraction, agent enrichment and bank statement matching
- Optional: an accessible fine-tuned OpenAI model ID for `supplier_info` and `/supplier_validation`
- NuGet packages: `ExcelDataReader` 3.7.0, `Swashbuckle.AspNetCore` 6.6.2, `System.Text.Encoding.CodePages` 8.0.0

**OpenAI ChatGPT subscription and API usage are billed separately.** Model access and availability depend on your OpenAI API account. The old fine-tuned model ID from the Python source is deliberately not embedded. Set a valid model ID that your API key can use.

## Visual Studio local setup

1. Extract ZIP and open `MYT.InvoiceRecognition.Net8.sln`.
2. Restore NuGet packages.
3. In the API project, configure secrets using .NET User Secrets or environment variables:

   ```powershell
   dotnet user-secrets init --project MYT.InvoiceRecognition.Api
   dotnet user-secrets set "AzureDocumentIntelligence:Endpoint" "https://YOUR-RESOURCE.cognitiveservices.azure.com/" --project MYT.InvoiceRecognition.Api
   dotnet user-secrets set "AzureDocumentIntelligence:ApiKey" "YOUR-AZURE-KEY" --project MYT.InvoiceRecognition.Api
   dotnet user-secrets set "OpenAI:ApiKey" "YOUR-OPENAI-API-KEY" --project MYT.InvoiceRecognition.Api
   # Optional supplier prediction:
   dotnet user-secrets set "OpenAI:FineTunedSupplierModel" "YOUR-FINE-TUNED-MODEL-ID" --project MYT.InvoiceRecognition.Api
   ```

   These values are intentionally missing from `appsettings.json`. The file must stay secret-free.
4. Run the API (`http` profile) and open `http://localhost:8000/docs`.
5. Invoke `/parse_invoice` with a **public, directly fetchable** PDF/JPG URL. A private SharePoint URL, local file path or login-required URL will not work with Azure's `urlSource`.

Or from a terminal:

```powershell
 dotnet restore MYT.InvoiceRecognition.Net8.sln
 dotnet build MYT.InvoiceRecognition.Net8.sln -c Debug
 dotnet run --project MYT.InvoiceRecognition.SmokeTests
 dotnet run --project MYT.InvoiceRecognition.Api --launch-profile http
```

### curl examples

```bash
curl -X POST "http://localhost:8000/parse_invoice" -H "X-API-Key: YOUR_INTERNAL_KEY" -d "invoice_url=https://example.com/public-invoice.pdf"

curl -X POST "http://localhost:8000/parse_invoice?currency=GBP" -H "X-API-Key: YOUR_INTERNAL_KEY" -d "invoice_url=https://example.com/public-invoice.pdf"

curl -X POST "http://localhost:8000/agent_invoice" -H "X-API-Key: YOUR_INTERNAL_KEY" -d "invoice_url=https://example.com/public-invoice.pdf"

curl -X POST "http://localhost:8000/supplier_validation?vendor_name=ABC%20Limited" -H "X-API-Key: YOUR_INTERNAL_KEY"

curl -X POST "http://localhost:8000/bank_statement" -H "X-API-Key: YOUR_INTERNAL_KEY" -F "bank_statement_file=@bank.csv" -F "invoice_file=@invoices.xlsx"
```

`X-API-Key` is required **only when** `Security:ApiKey` is configured. In production, always set one and use HTTPS via a reverse proxy.

## Deploy on Ubuntu Docker (port 8000)

1. Copy the project folder to the Ubuntu server.
2. Copy `.env.example` to `.env` and fill in actual credentials. Keep `.env` out of version control.
3. Run `docker compose up -d --build` from the folder containing `docker-compose.yml`.
4. Check `docker compose logs -f invoice-recognition`.
5. Reverse-proxy HTTPS from Nginx or Azure Application Gateway to the container. Avoid exposing port 8000 publicly without authentication and rate limiting.

The Docker image includes only ASP.NET Core. **Tesseract, Python, Poppler and LangChain are not required**: Azure Document Intelligence performs the OCR and provides the full text. This reduces runtime deployment dependencies. For Azure analysis, the original model's 2023-07-31 REST version is retained (same family as `azure-ai-formrecognizer` 3.3.x).

## How the new pipeline works

`/parse_invoice` -> validate public URL -> Azure Document Intelligence `prebuilt-invoice` (REST POST -> poll operation-location) -> extract `VendorName`, `VendorAddress`, `InvoiceId`, `InvoiceDate`, `Items`, `InvoiceTotal`, `TotalTax`, `SubTotal`, `currencyCode` -> optional OpenAI for missing date/currency -> optional supplier fine-tuned prediction -> same JSON keys to MVC.

`/agent_invoice` adds an OpenAI call to fill fields Azure could not extract, **only when required**, rather than invoking the previous malformed LangChain tools. This is an intentional behavior change; test accuracy with representative invoices before treating it as equivalent.

`/bank_statement` uses ExcelDataReader for XLS/XLSX; a local CSV parser supports quoted CSV fields and Latin1 fallback. Both input files are size/row limited, and the resulting rows are given to OpenAI for reconciliation. AI matches are **suggestions requiring accounting verification**, not automatically posted financial transactions. A full deterministic date/amount reconciliation engine was not present in the original and is not implemented in this migration.

## Configuration

| Setting | Required | Purpose |
|---|---|---|
| `AzureDocumentIntelligence:Endpoint` | Yes | Azure resource URL (HTTPS) |
| `AzureDocumentIntelligence:ApiKey` | Yes | Azure resource subscription key |
| `AzureDocumentIntelligence:ApiVersion` | Default `2023-07-31` | Original Azure REST model compatibility |
| `OpenAI:ApiKey` | Optional for `/parse_invoice` | AI fallback/enrichment; required for bank reconciliation |
| `OpenAI:ExtractionModel` | Default `gpt-4o` | Missing currency/date |
| `OpenAI:AgentModel` | Default `gpt-4o-mini` | Missing field recovery in `/agent_invoice` |
| `OpenAI:ReconciliationModel` | Default `gpt-4o` | Invoice-bank matching |
| `OpenAI:FineTunedSupplierModel` | Optional | Supplier ledger/account prediction; needs valid account access |
| `Security:ApiKey` | Recommended | Requires `X-API-Key` header for POST endpoints |
| `Limits:*` | Defaults provided | Upload size, spreadsheet row count, prompt characters |
| `RecognitionLog:SaveTimeoutSeconds` | Default `60` | Console database mode: total budget for saving recognition history, including connection, inserts, and commit. Must be positive. Independent of invoice cancellation. |

Use environment-variable section separators `__` on Ubuntu, e.g. `AzureDocumentIntelligence__Endpoint`.

For the console, set `RecognitionLog__SaveTimeoutSeconds=120` to allow up to two minutes for recognition history. A log-save timeout prints the invoice and attempt identifiers and leaves the analysis result or original analysis error intact. Check SQL blocking and connectivity if it recurs. SQL connection and command timeouts still apply independently; increasing the connection-string timeout does not extend this total log-save budget.

Console database batches process only `.pdf`, `.jpg`, `.jpeg`, `.png`, `.bmp`, `.tif`, `.tiff`, and `.heif` file paths. Extension checks ignore case, query strings, and fragments. Other extensions (including `.doc` and `.docx`) and paths without extensions are counted as skipped before Azure analysis. Skipped rows are reported in console output and left unchanged in SQL, so they remain eligible for selection on later runs. The extension check does not validate the file contents.

## Validation checklist before replacing the original service

- [ ] `dotnet restore` and `dotnet build` succeed in Visual Studio 2022.
- [ ] `dotnet run --project MYT.InvoiceRecognition.SmokeTests` passes the parser and CSV tests.
- [ ] Configure Azure credentials (and optional OpenAI credentials) using secrets, **not** in source.
- [ ] Test at least 10 real MYT invoices: dates, currency, totals, VAT, line items and response field types.
- [ ] Confirm MYT MVC posts `invoice_url` as form data, not JSON, and accepts `dd/MM/yyyy`.
- [ ] If supplier prediction is required, confirm the fine-tuned model ID works for the API account.
- [ ] Exercise `/agent_invoice`, `/supplier_validation` and `/bank_statement` with expected and invalid inputs.
- [ ] Test timeouts, unreachable URLs and private addresses.
- [ ] Configure HTTPS, authentication, rate limiting, secure logs, back-up and monitoring.

## What still needs live verification

This is a source-level conversion, **not** a guaranteed zero-error replacement: real-world Azure field variations, model availability, server networking, NuGet restore and the MYT MVC integration must be tested in the intended environment. The original project does not include full automated integration tests or its supplier training dataset, so exact inference parity cannot be proven from the ZIP alone.
