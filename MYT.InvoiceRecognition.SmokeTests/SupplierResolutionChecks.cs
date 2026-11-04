extern alias InvoiceConsole;
using Supplier = InvoiceConsole::MYT.InvoiceRecognition.ConsoleApp;
using Microsoft.Data.SqlClient;
using System.Text.Json;

internal static class SupplierResolutionChecks
{
    public static void CheckMatching(Action<bool, string> assert)
    {
        assert(!Supplier.SupplierNameMatching.IsEligible(new("   ")) && !Supplier.SupplierNameMatching.IsEligible(new("ABC"))
            && Supplier.SupplierNameMatching.IsEligible(new(" Aldi ")), "Supplier names must contain more than three trimmed characters");
        assert(Supplier.SupplierNameMatching.NameKey(" Aldi  s.tores ") == "ALDI S TORES", "Supplier lookup normalizes casing, whitespace, and punctuation");
        var approved = new[] { new Supplier.ApprovedSupplierAlias(1, "ALDI SHOES", "ALDI STORES LTD", "GB123", "00123", "GB", "1 Main Street London") };
        var vat = Supplier.SupplierNameMatching.Suggest(new("OTHER OCR", "GB 123", Country: "GBR"), approved);
        assert(vat?.Name == "ALDI STORES LTD" && vat.Source == "VATNumber", "VAT identity can suggest a canonical name across OCR aliases");
        assert(Supplier.SupplierNameMatching.Suggest(new("ALDI SHOES", "GB999", Country: "GB"), approved) == null,
            "Conflicting VAT identifiers block name matches");
        assert(Supplier.SupplierNameMatching.Suggest(new("OTHER OCR", CompanyNumber: "00123", Country: "GBR"), approved)?.Source == "CompanyNumber"
            && Supplier.SupplierNameMatching.Suggest(new("OTHER OCR", CompanyNumber: "00123"), approved) == null,
            "Company-number suggestions require matching country context and preserve leading zeroes");
        assert(Supplier.SupplierNameMatching.Suggest(new("OTHER OCR", Address: "1 Main Street London"), approved)?.Source == "Address",
            "Approved addresses provide review suggestions");
        var conflicting = approved.Append(approved[0] with { Id = 2, NormalizedName = "OTHER COMPANY" });
        assert(Supplier.SupplierNameMatching.Suggest(new("ALDI SHOES", "GB123"), conflicting) == null,
            "Conflicting approved canonical names remain unresolved");
        foreach (var kind in new[] { "Invoice", "Receipt" })
        {
            var prefix = kind == "Invoice" ? "Vendor" : "Merchant";
            var fields = new Dictionary<string, object>
            {
                [prefix + "Name"] = new { valueString = "Supplier Limited" },
                [prefix + "TaxId"] = new { valueString = "GB123" },
                [prefix + "CompanyNumber"] = new { valueString = "00123" },
                [prefix + "Address"] = new { valueAddress = new { road = "Main Street", countryRegion = "GBR" } },
                ["CustomerTaxId"] = new { valueString = "GB999" }
            };
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { analyzeResult = new { documents = new[] { new { fields } } } }));
            var parsed = InvoiceConsole::MYT.InvoiceRecognition.Api.Services.AzureInvoiceParser.Parse(json.RootElement, kind).Invoice;
            assert(parsed.SupplierVatNumber == "GB123" && parsed.SupplierCompanyNumber == "00123" && parsed.SupplierCountry == "GBR"
                && parsed.Address.Contains("Main Street"), $"{kind} extracts supplier identifiers and address without using customer VAT");
        }
        using var customerOnly = JsonDocument.Parse("""
            {"analyzeResult":{"content":"VAT GB999 Company number 99999", "documents":[{"fields":{"CustomerTaxId":{"valueString":"GB999"}}}]}}
            """);
        var noSupplier = InvoiceConsole::MYT.InvoiceRecognition.Api.Services.AzureInvoiceParser.Parse(customerOnly.RootElement).Invoice;
        assert(noSupplier.SupplierVatNumber == "" && noSupplier.SupplierCompanyNumber == "", "Missing supplier identifiers are not guessed from customer fields or arbitrary OCR text");
    }

    public static async Task CheckBatchScopeAsync(Action<bool, string> assert)
    {
        var failAll = false;
        var store = new FakeInvoiceStore
        {
            Rows = new[]
            {
                new Supplier.PendingInvoice(1, 11, "https://example.com/good.pdf"),
                new Supplier.PendingInvoice(2, 12, "https://example.com/failed.pdf"),
                new Supplier.PendingInvoice(3, 13, ""),
                new Supplier.PendingInvoice(4, 14, "https://example.com/save-failed.pdf"),
                new Supplier.PendingInvoice(5, 15, "https://example.com/changed.pdf"),
                new Supplier.PendingInvoice(6, 16, "https://example.com/unsupported.doc")
            }
        };
        var batch = new Supplier.InvoiceBatchProcessor(store, (row, token) =>
        {
            if (failAll || row.Id.Equals(2)) throw new InvalidOperationException("Analysis failed");
            return Task.FromResult(new InvoiceConsole::MYT.InvoiceRecognition.Api.Models.InvoiceData());
        });
        var summary = await batch.RunAsync(TextWriter.Null, CancellationToken.None);
        assert(summary == new Supplier.BatchSummary(6, 1, 3, 2)
            && batch.SuccessfullySavedInvoiceIds.SequenceEqual(new object[] { 1 }),
            "Supplier batch scope contains only saved successes, excluding analysis/save failures, changed rows and unsupported files");
        failAll = true;
        await batch.RunAsync(TextWriter.Null, CancellationToken.None);
        assert(batch.SuccessfullySavedInvoiceIds.Count == 0, "A new batch does not retain successful IDs from a previous run");
        var resolver = new Supplier.SqlSupplierNameResolver("This connection string must never be opened");
        var empty = await resolver.RunBatchAsync(Array.Empty<object>(), TextWriter.Null, CancellationToken.None);
        assert(empty == new Supplier.SupplierResolutionSummary(0, 0, 0, 0, 0),
            "An empty supplier batch returns immediately without connecting or replaying historical invoices");
    }

    // Explicit opt-in integration test. Never reads the application's database connection string.
    public static async Task CheckSqlAsync(Action<bool, string> assert)
    {
        var database = "InvoiceRecognitionSupplierTests_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = @".\SQLEXPRESS", InitialCatalog = "master", IntegratedSecurity = true,
            TrustServerCertificate = true, Encrypt = SqlConnectionEncryptOption.Optional, ConnectTimeout = 10
        };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
        await ExecuteAsync(master, $"CREATE DATABASE [{database}]");
        try
        {
            builder.InitialCatalog = database;
            await using (var connection = new SqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, """
                    CREATE TABLE dbo.Invoice (Id int PRIMARY KEY, FilePath nvarchar(1000), ClientId int);
                    CREATE TABLE dbo.Acc_Company (ClientId int, IsEnableMultiCurrency bit);
                    CREATE TABLE dbo.UploadedInvoice
                    (
                        Id int IDENTITY(1,1) PRIMARY KEY, InvoiceId int, InvoiceVendorName nvarchar(500), IsReviewed bit,
                        TaskStatus varchar(20), InvoiceNumber nvarchar(255), InvoiceMissionDate date,
                        Currency nvarchar(10), CurrencyId int, InvoiceBaseAmount decimal(19,4), InvoiceVatAmount decimal(19,4),
                        InvoiceTotalAmount decimal(19,4), DuplicatResult nvarchar(max), ReviewedDate datetime2,
                        ReviewedBy int, IsDateVerificationRequired bit, IsRejected bit, IsDuplicated bit
                    );
                    """);
                var setup = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, "Sql", "CreateInvoiceRecognitionLog.sql"));
                var upgrade = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, "Sql", "UpgradeSupplierNameResolution.sql"));
                await ExecuteAsync(connection, setup);
                await ExecuteAsync(connection, upgrade);
                await ExecuteAsync(connection, upgrade);
                assert(true, "Supplier migration runs twice without losing data or failing");
                await ExecuteAsync(connection, """
                    INSERT dbo.Invoice(Id, FilePath) VALUES (1, 'https://example.com/1.pdf'), (2, 'https://example.com/2.pdf'),
                        (3, 'https://example.com/3.pdf'), (4, 'https://example.com/4.pdf'), (5, 'https://example.com/5.pdf'),
                        (6, 'https://example.com/6.pdf'), (7, 'https://example.com/7.pdf'), (8, 'https://example.com/8.pdf');
                    SET IDENTITY_INSERT dbo.UploadedInvoice ON;
                    INSERT dbo.UploadedInvoice(Id, InvoiceId, InvoiceVendorName, IsReviewed, TaskStatus, InvoiceTotalAmount)
                    VALUES (1, 1, 'ALDI SHOES', 0, 'Success', 120), (2, 2, 'ALDI SHOES', 1, 'Success', 120),
                        (3, 3, 'Manually changed', 0, 'Success', 120), (4, 4, 'ALDI SHOES', 0, 'Success', 120),
                        (5, 5, 'ABC', 0, 'Success', 120), (6, 6, 'Different OCR', 0, 'Success', 120),
                        (7, 7, '', 0, 'Success', 120), (8, 8, 'ALDI SHOES', 1, 'PENDING', 120);
                    SET IDENTITY_INSERT dbo.UploadedInvoice OFF;
                    INSERT dbo.InvoiceRecognitionLog
                        (AttemptId, UploadedInvoiceId, InvoiceId, FilePath, DocumentType, ModelId, Status,
                        IsSelected, SupplierName, RawAzureResultJson, StartedAtUtc, CompletedAtUtc)
                    SELECT NEWID(), Id, Id, FilePath, 'Invoice', 'prebuilt-invoice', 'Success', 1,
                        CASE Id WHEN 5 THEN 'ABC' WHEN 6 THEN 'Different OCR' WHEN 7 THEN '' ELSE 'ALDI SHOES' END,
                        N'{"analyzeResult":{"documents":[{"fields":{"VendorName":{"valueString":"ALDI SHOES"},"VendorTaxId":{"valueString":"GB 123"},"VendorCompanyNumber":{"valueString":"00123"},"VendorAddress":{"valueAddress":{"road":"Main Street","city":"London","countryRegion":"GBR"}}}}]}}',
                        SYSUTCDATETIME(), SYSUTCDATETIME() FROM dbo.Invoice;
                    UPDATE dbo.InvoiceRecognitionLog SET VATNumber = 'GB999' WHERE UploadedInvoiceId = 4;
                    """);
                var resolver = new Supplier.SqlSupplierNameResolver(builder.ConnectionString);
                var scopedIds = Enumerable.Range(1000, 1100).Select(id => (object)id).Concat(new object[] { 1, 1 }).ToArray();
                var scoped = await resolver.RunBatchAsync(scopedIds, TextWriter.Null, CancellationToken.None);
                assert(scoped.Selected == 1 && scoped.Failed == 0
                    && await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceRecognitionLog WHERE CompanyNumber IS NOT NULL") == 1
                    && await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceSupplierNameAliasInvoice") == 1,
                    "SQL batch filtering accepts source IDENTITY IDs, excludes historical invoices, deduplicates IDs and supports large batches");
                var first = await resolver.RunAsync(TextWriter.Null, CancellationToken.None);
                assert(first.Failed == 0 && first.Updated == 0, "First pass registers unapproved aliases without updating invoices");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceSupplierNameAlias") == 3,
                    "Short/empty names are excluded and conflicting VAT contexts create separate aliases");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceRecognitionLog WHERE VATNumber IS NOT NULL AND CompanyNumber = '00123' AND SupplierAddress LIKE '%London%'") == 8,
                    "Historical raw Azure JSON backfills supplier VAT, company number and address");
                await ExecuteAsync(connection, """
                    UPDATE dbo.InvoiceSupplierNameAlias SET NormalizedName = 'ALDI STORES LTD', IsApproved = 1,
                        MatchSource = 'Manual', ApprovedBy = 'test-reviewer', ApprovedOn = SYSUTCDATETIME()
                    WHERE OriginalName = 'ALDI SHOES' AND VATKey = 'GB123';
                    """);
                var second = await resolver.RunAsync(TextWriter.Null, CancellationToken.None);
                assert(second.Failed == 0 && second.Updated == 1, "Only an approved alias updates an eligible unreviewed invoice");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.UploadedInvoice WHERE (Id = 1 AND InvoiceVendorName = 'ALDI STORES LTD') OR (Id = 2 AND InvoiceVendorName = 'ALDI SHOES' AND IsReviewed = 1) OR (Id = 3 AND InvoiceVendorName = 'Manually changed') OR (Id = 4 AND InvoiceVendorName = 'ALDI SHOES')") == 4,
                    "Reviewed invoices, manual name edits and conflicting VAT contexts are protected");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceSupplierNameAlias WHERE OriginalName = 'Different OCR' AND NormalizedName = 'ALDI STORES LTD' AND IsApproved = 0 AND MatchSource = 'VATNumber'") == 1,
                    "VAT-based cross-name suggestion stays pending human approval");
                var third = await resolver.RunAsync(TextWriter.Null, CancellationToken.None);
                assert(third.Failed == 0 && await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceSupplierNameAliasInvoice") == 6,
                    "Repeated resolution does not duplicate alias/invoice links");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.InvoiceSupplierNameAlias WHERE VATKey = 'GB123' AND OriginalName = 'ALDI SHOES' AND UploadedInvoiceIds = '1,2,3,8'") == 1,
                    "Comma-separated invoice IDs are distinct and ordered");
                assert(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.UploadedInvoice WHERE InvoiceTotalAmount = 120") == 8,
                    "Supplier replay leaves all invoice amounts unchanged");
                var store = new Supplier.SqlInvoiceStore(builder.ConnectionString);
                assert((await store.LoadPendingAsync(CancellationToken.None)).Count == 0, "Batch selection skips reviewed pending invoices");
                var pendingRow = new Supplier.PendingInvoice(8, 8, "https://example.com/8.pdf");
                assert(!await store.SaveAsync(pendingRow, new InvoiceConsole::MYT.InvoiceRecognition.Api.Models.InvoiceData { Org = "Overwritten" }, CancellationToken.None)
                    && !await store.SaveErrorAsync(pendingRow, "Error", CancellationToken.None), "Batch success/error writes cannot overwrite concurrently reviewed rows");
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            // database was generated above, never read from configuration or caller input.
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, @"^InvoiceRecognitionSupplierTests_[a-f0-9]{32}$"))
                throw new InvalidOperationException("Unexpected integration-test database name.");
            await ExecuteAsync(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
