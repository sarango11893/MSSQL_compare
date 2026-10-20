-- ============================================================
--  SqlSuiteDemo · ORIGEN (source)
--  Contiene TODOS los tipos de objeto de SQL Server que DacFx
--  sabe comparar, para verificar la deteccion de diferencias.
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

-- ---------------------------- TIPOS ----------------------------
CREATE TYPE dbo.PhoneNumber FROM varchar(20) NOT NULL;
GO
CREATE TYPE dbo.OrderLineType AS TABLE
(
    ProductId int NOT NULL,
    Qty       int NOT NULL
);
GO

-- -------------------------- SECUENCIAS -------------------------
CREATE SEQUENCE dbo.Seq_OrderNumbers START WITH 1000 INCREMENT BY 1;
GO

-- ---------------------------- TABLAS ---------------------------
CREATE TABLE dbo.Customers
(
    Id        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name      nvarchar(100)     NOT NULL,
    Email     nvarchar(200)     NULL,
    CreatedAt datetime2(3)      NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Products
(
    Id     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Sku    varchar(32)       NOT NULL,
    Price  decimal(18,2)     NOT NULL,
    Legacy bit               NULL,
    CONSTRAINT CK_Products_Price CHECK (Price >= 0)
);
CREATE UNIQUE INDEX UX_Products_Sku ON dbo.Products (Sku);

CREATE TABLE dbo.OrderLines
(
    OrderId    int NOT NULL,
    LineNumber int NOT NULL,
    ProductId  int NOT NULL,
    Qty        int NOT NULL,
    CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber),
    CONSTRAINT FK_OrderLines_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products (Id)
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
    Code char(4)           NOT NULL CONSTRAINT UQ_Warehouses_Code UNIQUE,
    Name nvarchar(60)      NOT NULL
);

-- Sin PK: la comparacion de datos debe usar la restriccion UNIQUE como clave.
CREATE TABLE dbo.Settings
(
    KeyName nvarchar(50)  NOT NULL CONSTRAINT UQ_Settings_Key UNIQUE,
    Value   nvarchar(200) NULL
);

CREATE TABLE dbo.ChangeLog
(
    Id        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChangeLog PRIMARY KEY,
    TableName nvarchar(60)      NOT NULL,
    Action    varchar(10)       NOT NULL,
    ChangedAt datetime2(3)      NOT NULL CONSTRAINT DF_ChangeLog_ChangedAt DEFAULT (SYSUTCDATETIME())
);

CREATE INDEX IX_Customers_Name ON dbo.Customers (Name) INCLUDE (Email);
CREATE INDEX IX_Audit_Label   ON dbo.Audit (Label) WHERE Label IS NOT NULL;
GO

-- --------------------------- VISTAS ----------------------------
CREATE VIEW dbo.vw_Customers
AS
    SELECT Id, Name, Email
    FROM dbo.Customers;
GO
CREATE VIEW sales.vw_OrderSummary
AS
    SELECT OrderId, SUM(Qty) AS TotalQty
    FROM dbo.OrderLines
    GROUP BY OrderId;
GO
CREATE VIEW dbo.vw_ActiveProducts
WITH SCHEMABINDING
AS
    SELECT Id, Sku, Price
    FROM dbo.Products
    WHERE Legacy = 0 OR Legacy IS NULL;
GO

-- ------------------------ PROCEDIMIENTOS -----------------------
CREATE PROCEDURE dbo.usp_GetCustomer
    @CustomerId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name, Email FROM dbo.Customers WHERE Id = @CustomerId;
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
    INSERT INTO dbo.OrderLines (OrderId, LineNumber, ProductId, Qty)
    VALUES (@OrderId, @LineNumber, @ProductId, @Qty);
END
GO
CREATE PROCEDURE dbo.usp_NoChanges
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS Total FROM dbo.Customers;
END
GO

-- --------------------------- FUNCIONES -------------------------
CREATE FUNCTION dbo.fn_ProductTotal (@productId int)
RETURNS decimal(18,2)
AS
BEGIN
    DECLARE @total decimal(18,2);
    SELECT @total = Price FROM dbo.Products WHERE Id = @productId;
    RETURN @total;
END
GO
CREATE FUNCTION dbo.fn_CustomersByDomain (@domain nvarchar(100))
RETURNS TABLE
AS
RETURN
    SELECT Id, Name, Email
    FROM dbo.Customers
    WHERE Email LIKE '%@' + @domain;
GO

-- --------------------------- SINONIMOS -------------------------
CREATE SYNONYM dbo.syn_Customers FOR dbo.Customers;
GO

-- ------------------------- DATOS DE MUESTRA -------------------
SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, Name, Email, CreatedAt) VALUES
    (1, N'Ana Torres', N'ana@example.com',   '2024-01-10T09:00:00'),
    (2, N'Luis Perez', N'luis@example.com',  '2024-02-11T10:30:00'),
    (3, N'Marta Ruiz', N'marta@example.com', '2024-03-12T11:45:00');
SET IDENTITY_INSERT dbo.Customers OFF;

SET IDENTITY_INSERT dbo.Products ON;
INSERT INTO dbo.Products (Id, Sku, Price, Legacy) VALUES
    (1, 'SKU-001', 19.99, 0),
    (2, 'SKU-002', 45.50, 1),
    (3, 'SKU-003',  7.25, 0);
SET IDENTITY_INSERT dbo.Products OFF;

INSERT INTO dbo.OrderLines (OrderId, LineNumber, ProductId, Qty) VALUES
    (1, 1, 1, 2),
    (1, 2, 2, 5),
    (2, 1, 3, 1);

INSERT INTO dbo.Audit (Amount, Label) VALUES
    (10.00, N'alta'),
    (25.50, N'ajuste');

SET IDENTITY_INSERT dbo.Warehouses ON;
INSERT INTO dbo.Warehouses (Id, Code, Name) VALUES
    (1, 'MAD', N'Madrid'),
    (2, 'BCN', N'Barcelona');
SET IDENTITY_INSERT dbo.Warehouses OFF;

INSERT INTO dbo.Settings (KeyName, Value) VALUES
    (N'currency', N'EUR'),
    (N'timezone', N'Europe/Madrid'),
    (N'theme',    N'Azul');
GO

-- --------------------------- TRIGGERS -------------------------
-- Se crean despues de sembrar para no ensuciar ChangeLog en ambos lados.
CREATE TRIGGER dbo.trg_Customers_ChangeLog
ON dbo.Customers
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ChangeLog (TableName, Action)
    SELECT 'Customers', 'INSERT' FROM inserted;
END
GO
CREATE TRIGGER dbo.trg_Products_ChangeLog
ON dbo.Products
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ChangeLog (TableName, Action)
    SELECT 'Products', 'DELETE' FROM deleted;
END
GO

-- --------------------- PROPIEDADES EXTENDIDAS ----------------
EXEC sys.sp_addextendedproperty
    @name = N'MS_Description', @value = N'Clientes activos',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE',  @level1name = N'Customers';
GO
PRINT '[source] OK';
GO
