# MYT Azure Document Intelligence: invoice + receipt update (.NET 8)

This update is for the **existing** `MYT.InvoiceRecognition.Api` project in `MYT.InvoiceRecognition.Net8.sln`. It preserves the `/parse_invoice` form field `invoice_url`, the existing invoice JSON fields, and the older two-argument `AzureDocumentService.AnalyzeAsync` overload.

## Installation

Back up the original project, then copy the six updated files into the same relative paths under `MYT.InvoiceRecognition.Api`:

- `Services/AzureDocumentService.cs`
- `Services/InvoiceRecognitionService.cs`
- `Models/ApiModels.cs`
- `Options/AppOptions.cs`
- `Controllers/InvoiceController.cs`
- `appsettings.json` (merge configuration rather than overwriting your environment-specific settings)

No new NuGet packages or `Program.cs` registration changes are required. The project uses the existing typed `HttpClient<AzureDocumentService>` and Options configuration.

## Configuration

`AzureDocumentIntelligence:Endpoint`: `https://mytai2023.cognitiveservices.azure.com/`

`AzureDocumentIntelligence:ApiKey`: set with Visual Studio User Secrets or an environment secret, never in source control.

`AzureDocumentIntelligence:ApiVersion`: `2023-07-31`

`AzureDocumentIntelligence:ClassifierId`: empty until a classifier is trained (optional).

`AzureDocumentIntelligence:InvoiceClassName`: `Invoice` (must match your trained classifier label exactly).

`AzureDocumentIntelligence:ReceiptClassName`: `Receipt` (must match your trained classifier label exactly).

`AzureDocumentIntelligence:ClassifierMinimumConfidence`: `0.8`

`AzureDocumentIntelligence:AutoCompareModels`: `true`

`AzureDocumentIntelligence:AmbiguousScoreMargin`: `8`

## API usage

Use `POST /parse_invoice` with form data `invoice_url=https://public.example.com/invoice.pdf`.

Optional query parameter `documentType=Auto|Invoice|Receipt` (default: `Auto`). Existing query parameter `currency=GBP` is retained; it only sets a currency code, not currency conversion. If configured, include the internal `X-API-Key` header.

- `documentType=Invoice`: exactly one Azure `prebuilt-invoice` analysis.
- `documentType=Receipt`: exactly one Azure `prebuilt-receipt` analysis.
- `documentType=Auto` with **trained classifier**: classifier selects model if its confidence is >= threshold; otherwise tries both prebuilt models.
- `documentType=Auto` without classifier: **runs both prebuilt models** and chooses the more complete/plausible structured result using a heuristic score. This normally incurs **two Azure analysis charges per document**.
- Set `AutoCompareModels=false` to skip the receipt analysis when invoice extraction scores 90 or above. This can save cost but may misclassify some receipts.

## Response additions

The existing `data.org`, `data.address`, `data.ivn`, `data.dat`, `data.lin`, `data.net`, `data.vat`, `data.tot`, `data.currency`, and `data.supplier_info` remain available.

Additional fields in `data`: `documentType` (`Invoice` / `Receipt`), `modelId`, `detectionMethod`, `documentTypeConfidence` (null unless a trained classifier was used), `reviewRequired`, `warnings`.

For **receipts**, `ivn` is intentionally empty unless an explicitly supported receipt identifier is added later. The code does **not** substitute service dates for invoice date.

Missing tax or missing net is reported for review rather than assumed to be zero. When `subtotal` is missing but total and VAT exist, net is calculated as `total - VAT`.

`reviewRequired` flags missing key data, inconsistent amounts, multiple detected documents or uncertain model selection. The heuristic quality score is *not* an Azure classifier confidence.

## Example call

```bash
curl -X POST "http://localhost:8000/parse_invoice?documentType=Auto" -H "X-API-Key: YOUR_INTERNAL_KEY" -d "invoice_url=https://example.com/public-invoice.pdf"
```

The document must be accessible to Azure over a valid public URL. This endpoint does not accept file bytes, so private MYT uploads need a securely scoped temporary URL or a separate bytes-upload implementation.

## Validate before production

1. Build in Visual Studio 2022 with .NET 8 SDK installed.
2. Run the existing `MYT.InvoiceRecognition.SmokeTests` project (included in the optional full solution ZIP).
3. Run 10+ real invoices and 10+ real receipts through both models, compare all numeric fields and documentType to human verified results.
4. Check whether OCR recognizes UK fuel receipts (faded dates and VAT receipts may require manual review).
5. Configure the Document Intelligence key as a secret, and keep your internal API key enabled in production.

This version was statically reviewed in the current environment, but no live Azure calls or actual C# compilation were possible here. Azure response formats and invoices in your tenant must be tested before deploying to production.
