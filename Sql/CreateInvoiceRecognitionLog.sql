-- Run in the same database as dbo.UploadedInvoice and dbo.Invoice.
-- Read the existing identifier types instead of assuming bigint.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.InvoiceRecognitionLog', N'U') IS NOT NULL
BEGIN
    PRINT 'dbo.InvoiceRecognitionLog already exists; no changes made.';
    RETURN;
END;

DECLARE @UploadedInvoiceIdType sysname,
        @InvoiceIdType sysname,
        @UploadedInvoiceInvoiceIdType sysname;

SELECT @UploadedInvoiceIdType = TYPE_NAME(system_type_id)
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.UploadedInvoice') AND name = N'Id';
SELECT @InvoiceIdType = TYPE_NAME(system_type_id)
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Invoice') AND name = N'Id';
SELECT @UploadedInvoiceInvoiceIdType = TYPE_NAME(system_type_id)
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.UploadedInvoice') AND name = N'InvoiceId';

IF @UploadedInvoiceIdType IS NULL OR @InvoiceIdType IS NULL OR @UploadedInvoiceInvoiceIdType IS NULL
    THROW 50001, 'Required dbo.UploadedInvoice / dbo.Invoice identifier columns were not found.', 1;
IF @UploadedInvoiceIdType NOT IN (N'int', N'bigint', N'smallint', N'tinyint', N'uniqueidentifier')
    OR @InvoiceIdType NOT IN (N'int', N'bigint', N'smallint', N'tinyint', N'uniqueidentifier')
    THROW 50002, 'Unsupported identifier type. Match the log identifier columns to the source schema manually.', 1;
IF @InvoiceIdType <> @UploadedInvoiceInvoiceIdType
    THROW 50003, 'Invoice.Id and UploadedInvoice.InvoiceId have different types. Review the source schema first.', 1;

DECLARE @Sql nvarchar(max) = N'
CREATE TABLE dbo.InvoiceRecognitionLog
(
    Id                  bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_InvoiceRecognitionLog PRIMARY KEY,
    AttemptId           uniqueidentifier NOT NULL,
    UploadedInvoiceId   ' + QUOTENAME(@UploadedInvoiceIdType) + N' NOT NULL,
    InvoiceId           ' + QUOTENAME(@InvoiceIdType) + N' NOT NULL,
    FilePath            nvarchar(max) NOT NULL,

    DocumentType        varchar(20) NOT NULL,
    ModelId             nvarchar(100) NOT NULL,
    ApiVersion          nvarchar(30) NULL,
    ParserVersion       nvarchar(256) NULL,

    Status              varchar(20) NOT NULL,
    IsSelected          bit NULL,
    QualityScore        float NULL,
    DetectionMethod     nvarchar(50) NULL,
    ReviewRequired      bit NULL,

    InvoiceDate         date NULL,
    InvoiceNumber       nvarchar(255) NULL,
    SupplierName        nvarchar(500) NULL,
    VATNumber           nvarchar(100) NULL,
    CompanyNumber       nvarchar(100) NULL,
    SupplierAddress     nvarchar(1000) NULL,
    SupplierCountry     nvarchar(100) NULL,
    SupplierNameAliasId  bigint NULL,
    ResolvedSupplierName nvarchar(500) NULL,
    SupplierMatchSource varchar(30) NULL,
    SupplierResolvedOn  datetime2(3) NULL,
    NetAmount           decimal(19,4) NULL,
    VatAmount           decimal(19,4) NULL,
    TotalAmount         decimal(19,4) NULL,
    Currency            nvarchar(10) NULL,

    RawAzureResultJson  nvarchar(max) NULL,
    ParsedResultJson    nvarchar(max) NULL,
    ErrorMessage        nvarchar(max) NULL,
    StartedAtUtc        datetime2(3) NOT NULL,
    CompletedAtUtc      datetime2(3) NOT NULL,
    CreatedAtUtc        datetime2(3) NOT NULL
        CONSTRAINT DF_InvoiceRecognitionLog_CreatedAtUtc DEFAULT SYSUTCDATETIME(),

    CONSTRAINT CK_InvoiceRecognitionLog_RawJson
        CHECK (RawAzureResultJson IS NULL OR ISJSON(RawAzureResultJson) = 1),
    CONSTRAINT CK_InvoiceRecognitionLog_ParsedJson
        CHECK (ParsedResultJson IS NULL OR ISJSON(ParsedResultJson) = 1),
    CONSTRAINT CK_InvoiceRecognitionLog_Status CHECK (Status IN (''Success'', ''Error'')),
    CONSTRAINT CK_InvoiceRecognitionLog_DocumentType CHECK (DocumentType IN (''Invoice'', ''Receipt''))
);

CREATE INDEX IX_InvoiceRecognitionLog_UploadedInvoice
    ON dbo.InvoiceRecognitionLog (UploadedInvoiceId, CreatedAtUtc DESC);
CREATE INDEX IX_InvoiceRecognitionLog_Attempt
    ON dbo.InvoiceRecognitionLog (AttemptId);
';

BEGIN TRANSACTION;
EXEC sys.sp_executesql @Sql;
COMMIT TRANSACTION;

-- No foreign keys: retain historical logs if source invoices are later removed.
