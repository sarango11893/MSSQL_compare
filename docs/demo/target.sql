-- ============================================================
--  SqlSuiteDemo · DESTINO (target)
--  Misma base con divergencias DELIBERADAS en cada tipo de objeto
--  para comprobar que el comparador las detecta.
-- ============================================================
IF DB_ID('SqlSuiteDemo') IS NOT NULL
BEGIN
    ALTER DATABASE SqlSuiteDemo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SqlSuiteDemo;
END
GO
CREATE DATABASE SqlSuiteDemo;
GO
USE SqlSuiteDemo;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

CREATE SCHEMA sales;
GO

-- ============================================================
--  DIVERGENCIAS INTENCIONADAS (resumen)
--  SOLO EN ORIGEN  -> se generan como CREATE
--    type dbo.PhoneNumber, type dbo.OrderLineType, sequence dbo.Seq_OrderNumbers,
--    view dbo.vw_ActiveProducts, function dbo.fn_CustomersByDomain,
--    synonym dbo.syn_Customers, trigger dbo.trg_Products_ChangeLog,
--    check CK_Products_Price, foreign key FK_OrderLines_Products,
--    unique UQ_Warehouses_Code, filtered index IX_Audit_Label,
--    extended property MS_Description
--  SOLO EN DESTINO -> se generan como DROP (si se marca la opcion)
--    table dbo.Legacy, view dbo.vw_LegacyReport, procedure dbo.usp_LegacyPurge
--  ALTERADOS
--    table Customers (Name 150 / falta Email / sobra Notes)
--    table Products  (falta Legacy / falta UX_Products_Sku)
--    table OrderLines (falta ProductId)
--    table Settings  (Value 100 en lugar de 200)
--    view dbo.vw_Customers (columnas distintas)
--    view sales.vw_OrderSummary (agregado distinto)
--    procedure dbo.usp_GetCustomer (cuerpo distinto)
--    procedure sales.usp_AddOrderLine (cuerpo distinto)
--    function dbo.fn_ProductTotal (cuerpo distinto)
--    trigger dbo.trg_Customers_ChangeLog (acciones distintas)
--    unique UQ_Settings_Key con otro nombre (UQ_Settings_KeyName)
--  IDENTICOS (control negativo)
--    table dbo.Audit, table dbo.ChangeLog, procedure dbo.usp_NoChanges
-- ============================================================

-- ---------------------------- TIPOS ----------------------------
CREATE TYPE dbo.PhoneNumber FROM varchar(15) NOT NULL;   -- distinto tamano
GO
CREATE TYPE dbo.OrderLineType AS TABLE
(
    ProductId int NOT NULL
);                                                        -- falta la columna Qty
GO

-- -------------------------- SECUENCIAS -------------------------
-- No existe dbo.Seq_OrderNumbers en destino.

-- ---------------------------- TABLAS ---------------------------
CREATE TABLE dbo.Customers
(
    Id        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name      nvarchar(150)     NOT NULL,
    Notes     nvarchar(max)     NULL,
    CreatedAt datetime2(3)      NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Products
(
    Id    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Sku   varchar(32)       NOT NULL,
    Price decimal(18,2)     NOT NULL
);

CREATE TABLE dbo.OrderLines
(
    OrderId    int NOT NULL,
    LineNumber int NOT NULL,
    Qty        int NOT NULL,
    CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber)
);

CREATE TABLE dbo.Audit
(
    Id      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Audit PRIMARY KEY,
    Amount  decimal(9,2)      NOT NULL CONSTRAINT DF_Audit_Amount DEFAULT (0),
    Label   nvarchar(50)      NULL,
    Total   AS (Amount * 2),
    Version rowversion
);

CREATE TABLE dbo.Warehouses
(
    Id   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouses PRIMARY KEY,
    Code char(4)           NOT NULL,
    Name nvarchar(60)      NOT NULL
);

CREATE TABLE dbo.Settings
(
    KeyName nvarchar(50)  NOT NULL CONSTRAINT UQ_Settings_KeyName UNIQUE,
    Value   nvarchar(100) NULL
);

CREATE TABLE dbo.ChangeLog
(
    Id        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChangeLog PRIMARY KEY,
    TableName nvarchar(60)      NOT NULL,
    Action    varchar(10)       NOT NULL,
    ChangedAt datetime2(3)      NOT NULL CONSTRAINT DF_ChangeLog_ChangedAt DEFAULT (SYSUTCDATETIME())
);

-- Objeto sobrante en destino -> DROP TABLE
CREATE TABLE dbo.Legacy
(
    Id     int NOT NULL CONSTRAINT PK_Legacy PRIMARY KEY,
    Remark nvarchar(100) NULL
);

CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);          -- sin INCLUDE
CREATE INDEX IX_Audit_Label   ON dbo.Audit (Label);              -- sin filtro
GO

-- --------------------------- VISTAS ----------------------------
CREATE VIEW dbo.vw_Customers
AS
    SELECT Id, Name
    FROM dbo.Customers;
GO
CREATE VIEW sales.vw_OrderSummary
AS
    SELECT OrderId, MAX(Qty) AS TotalQty
    FROM dbo.OrderLines
    GROUP BY OrderId;
GO
-- Objeto sobrante en destino -> DROP VIEW
CREATE VIEW dbo.vw_LegacyReport
AS
    SELECT Id, Remark FROM dbo.Legacy;
GO
-- dbo.vw_ActiveProducts no existe en destino.

-- ------------------------ PROCEDIMIENTOS -----------------------
CREATE PROCEDURE dbo.usp_GetCustomer
    @CustomerId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name FROM dbo.Customers WHERE Id = @CustomerId;
END
GO
CREATE PROCEDURE sales.usp_AddOrderLine
    @OrderId    int,
    @LineNumber int,
    @ProductId  int,
    @Qty        int
AS
BEGIN
    SET NOCOUNT ON;
    IF @Qty <= 0
        RETURN;
    INSERT INTO dbo.OrderLines (OrderId, LineNumber, Qty)
    VALUES (@OrderId, @LineNumber, @Qty);
END
GO
CREATE PROCEDURE dbo.usp_NoChanges
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS Total FROM dbo.Customers;
END
GO
-- Objeto sobrante en destino -> DROP PROCEDURE
CREATE PROCEDURE dbo.usp_LegacyPurge
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.Legacy;
END
GO

-- --------------------------- FUNCIONES -------------------------
CREATE FUNCTION dbo.fn_ProductTotal (@productId int)
RETURNS decimal(18,2)
AS
BEGIN
    DECLARE @total decimal(18,2) = 0;
    SELECT @total = Price FROM dbo.Products WHERE Id = @productId;
    RETURN ISNULL(@total, 0);
END
GO
-- dbo.fn_CustomersByDomain no existe en destino.

-- --------------------------- SINONIMOS -------------------------
-- dbo.syn_Customers no existe en destino.

-- ------------------------- DATOS DE MUESTRA -------------------
SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, Name, Notes, CreatedAt) VALUES
    (1, N'Ana Torres', NULL,           '2024-01-10T09:00:00'),
    (2, N'Luis P.',    N'cliente vip', '2024-02-11T10:30:00'),
    (3, N'Marta Ruiz', NULL,           '2024-03-12T11:45:00'),
    (4, N'Pedro Gil',  NULL,           '2024-04-01T08:00:00');
SET IDENTITY_INSERT dbo.Customers OFF;

SET IDENTITY_INSERT dbo.Products ON;
INSERT INTO dbo.Products (Id, Sku, Price) VALUES
    (1, 'SKU-001', 21.00),
    (2, 'SKU-002', 45.50),
    (3, 'SKU-003',  6.95);
SET IDENTITY_INSERT dbo.Products OFF;

-- Sin filas a proposito: la columna ProductId + FK solo existen en el origen,
-- y unas filas huerfanas impedirian crear la FK al desplegar el script de esquema.

INSERT INTO dbo.Audit (Amount, Label) VALUES
    (10.00, N'alta'),
    (30.00, N'ajuste');

SET IDENTITY_INSERT dbo.Warehouses ON;
INSERT INTO dbo.Warehouses (Id, Code, Name) VALUES
    (1, 'MAD', N'Madrid'),
    (2, 'BCN', N'Barcelona'),
    (3, 'VLC', N'Valencia');
SET IDENTITY_INSERT dbo.Warehouses OFF;

INSERT INTO dbo.Settings (KeyName, Value) VALUES
    (N'currency', N'EUR'),
    (N'timezone', N'UTC'),
    (N'theme',    N'Naranja'),
    (N'locale',   N'es-ES');

INSERT INTO dbo.Legacy (Id, Remark) VALUES (1, N'obsoleta');
GO

-- --------------------------- TRIGGERS -------------------------
CREATE TRIGGER dbo.trg_Customers_ChangeLog
ON dbo.Customers
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ChangeLog (TableName, Action)
    SELECT 'Customers', 'MIXED' FROM inserted;
END
GO
-- dbo.trg_Products_ChangeLog no existe en destino.

-- --------------------- PROPIEDADES EXTENDIDAS ----------------
-- MS_Description no existe en destino.

PRINT '[target] OK';
GO
