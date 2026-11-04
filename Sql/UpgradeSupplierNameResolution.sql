-- Run AFTER CreateInvoiceRecognitionLog.sql in the invoice database.
-- Rerunnable additive migration. No invoice data or approvals are changed.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.InvoiceRecognitionLog', N'U') IS NULL
    THROW 50010, 'Run CreateInvoiceRecognitionLog.sql first.', 1;

DECLARE @Columns TABLE (Name sysname, Definition nvarchar(200));
INSERT @Columns VALUES
    ('VATNumber', 'nvarchar(100) NULL'),
    ('CompanyNumber', 'nvarchar(100) NULL'),
    ('SupplierAddress', 'nvarchar(1000) NULL'),
    ('SupplierCountry', 'nvarchar(100) NULL'),
    ('SupplierNameAliasId', 'bigint NULL'),
    ('ResolvedSupplierName', 'nvarchar(500) NULL'),
    ('SupplierMatchSource', 'varchar(30) NULL'),
    ('SupplierResolvedOn', 'datetime2(3) NULL');
DECLARE @Name sysname, @Definition nvarchar(200), @Sql nvarchar(max);
DECLARE ColumnsToAdd CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Definition FROM @Columns;
OPEN ColumnsToAdd;
FETCH NEXT FROM ColumnsToAdd INTO @Name, @Definition;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH('dbo.InvoiceRecognitionLog', @Name) IS NULL
    BEGIN
        SET @Sql = N'ALTER TABLE dbo.InvoiceRecognitionLog ADD ' + QUOTENAME(@Name) + N' ' + @Definition;
        EXEC sys.sp_executesql @Sql;
    END;
    FETCH NEXT FROM ColumnsToAdd INTO @Name, @Definition;
END;
CLOSE ColumnsToAdd;
DEALLOCATE ColumnsToAdd;

IF OBJECT_ID(N'dbo.InvoiceSupplierNameAlias', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceSupplierNameAlias
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceSupplierNameAlias PRIMARY KEY,
        OriginalName nvarchar(500) NOT NULL,
        AliasKey nvarchar(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
        NormalizedName nvarchar(500) NULL,
        IsApproved bit NOT NULL CONSTRAINT DF_SupplierAlias_Approved DEFAULT 0,
        MatchSource varchar(30) NOT NULL CONSTRAINT DF_SupplierAlias_Source DEFAULT 'Exact',
        VATNumber nvarchar(100) NULL,
        CompanyNumber nvarchar(100) NULL,
        SupplierCountry nvarchar(100) NULL,
        SupplierAddress nvarchar(1000) NULL,
        VATKey nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_SupplierAlias_VATKey DEFAULT '',
        CompanyKey nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_SupplierAlias_CompanyKey DEFAULT '',
        CountryKey nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_SupplierAlias_CountryKey DEFAULT '',
        CreatedOn datetime2(3) NOT NULL CONSTRAINT DF_SupplierAlias_Created DEFAULT SYSUTCDATETIME(),
        UpdatedOn datetime2(3) NOT NULL CONSTRAINT DF_SupplierAlias_Updated DEFAULT SYSUTCDATETIME(),
        ApprovedBy nvarchar(256) NULL,
        ApprovedOn datetime2(3) NULL,
        UploadedInvoiceIds nvarchar(max) NULL,
        CONSTRAINT CK_SupplierAlias_Approved CHECK
            (IsApproved = 0 OR (LEN(LTRIM(RTRIM(NormalizedName))) > 0 AND NormalizedName IS NOT NULL
                AND ApprovedBy IS NOT NULL AND LEN(LTRIM(RTRIM(ApprovedBy))) > 0 AND ApprovedOn IS NOT NULL)),
        CONSTRAINT CK_SupplierAlias_Source CHECK
            (MatchSource IN ('Exact', 'Alias', 'VATNumber', 'CompanyNumber', 'Address', 'Manual'))
    );
    -- Identifier context prevents one OCR name from merging unrelated suppliers.
    CREATE UNIQUE INDEX UX_SupplierAlias_Context ON dbo.InvoiceSupplierNameAlias (AliasKey, VATKey, CompanyKey, CountryKey);
    CREATE INDEX IX_SupplierAlias_VAT ON dbo.InvoiceSupplierNameAlias (VATKey) INCLUDE (IsApproved);
    CREATE INDEX IX_SupplierAlias_Company ON dbo.InvoiceSupplierNameAlias (CompanyKey, CountryKey) INCLUDE (IsApproved);
END;

IF OBJECT_ID(N'dbo.InvoiceSupplierNameAliasInvoice', N'U') IS NULL
BEGIN
    DECLARE @IdType sysname;
    SELECT @IdType = TYPE_NAME(system_type_id) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.UploadedInvoice') AND name = N'Id';
    IF @IdType IS NULL OR @IdType NOT IN ('int', 'bigint', 'smallint', 'tinyint', 'uniqueidentifier')
        THROW 50011, 'Unsupported or missing UploadedInvoice.Id type.', 1;
    SET @Sql = N'CREATE TABLE dbo.InvoiceSupplierNameAliasInvoice
    (
        AliasId bigint NOT NULL REFERENCES dbo.InvoiceSupplierNameAlias(Id),
        UploadedInvoiceId ' + QUOTENAME(@IdType) + N' NOT NULL,
        CreatedOn datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_InvoiceSupplierNameAliasInvoice PRIMARY KEY (AliasId, UploadedInvoiceId)
    );';
    EXEC sys.sp_executesql @Sql;
END;
COMMIT TRANSACTION;
