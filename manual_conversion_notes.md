# Manual SQL Conversion Notes

## Transaction Blocks - DMS Limitations

The DMS tool returned simplified conversions for transaction blocks. These have been manually converted following PostgreSQL best practices.

### InsertProductAsync Transaction (Step 4)

**Original SQL Server:**
```sql
DECLARE @NewProductId INT;

BEGIN TRANSACTION;
    INSERT INTO Products (Name, Description, Price, StockQuantity)
    VALUES (@Name, @Description, @Price, @StockQuantity);
    
    SET @NewProductId = SCOPE_IDENTITY();
    
    INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
    VALUES (@NewProductId, 'INSERT', NULL, @Price, NULL, @StockQuantity, GETDATE());
    
    UPDATE ProductStats
    SET 
        TotalProducts = TotalProducts + 1,
        AveragePrice = (AveragePrice * TotalProducts + @Price) / (TotalProducts + 1),
        LastUpdated = GETDATE()
    WHERE StatId = 1;
COMMIT;

SELECT @NewProductId;
```

**Converted PostgreSQL:**
```sql
INSERT INTO productmanagement_dbo.products (name, description, price, stockquantity)
VALUES (@Name, @Description, @Price, @StockQuantity)
RETURNING productid;
```

**Note:** The transaction is handled at the ADO.NET level using BeginTransactionAsync(). SCOPE_IDENTITY() is replaced with RETURNING clause. The history and stats updates will need to be separate statements within the C# transaction.

### UpdateProductAsync Transaction (Step 5)

**Original SQL Server:**
```sql
BEGIN TRANSACTION;
    DECLARE @OldPrice DECIMAL(18,2);
    DECLARE @OldStock INT;
    
    SELECT @OldPrice = Price, @OldStock = StockQuantity
    FROM Products
    WHERE ProductId = @ProductId;
    
    UPDATE Products
    SET 
        Name = @Name,
        Description = @Description,
        Price = @Price,
        StockQuantity = @StockQuantity,
        ModifiedDate = GETDATE()
    WHERE ProductId = @ProductId;
    
    INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
    VALUES (@ProductId, 'UPDATE', @OldPrice, @Price, @OldStock, @StockQuantity, GETDATE());
    
    UPDATE ProductStats
    SET 
        AveragePrice = (AveragePrice * TotalProducts - @OldPrice + @Price) / TotalProducts,
        LastUpdated = GETDATE()
    WHERE StatId = 1;
COMMIT;
```

**Converted PostgreSQL:**
```sql
WITH old_values AS (
    SELECT price, stockquantity
    FROM productmanagement_dbo.products
    WHERE productid = @ProductId
)
UPDATE productmanagement_dbo.products
SET 
    name = @Name,
    description = @Description,
    price = @Price,
    stockquantity = @StockQuantity,
    modifieddate = CURRENT_TIMESTAMP
WHERE productid = @ProductId;

INSERT INTO productmanagement_dbo.producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
SELECT @ProductId, 'UPDATE', price, @Price, stockquantity, @StockQuantity, CURRENT_TIMESTAMP
FROM old_values;

UPDATE productmanagement_dbo.productstats
SET 
    averageprice = (averageprice * totalproducts - (SELECT price FROM old_values) + @Price) / totalproducts,
    lastupdated = CURRENT_TIMESTAMP
WHERE statid = 1;
```

**Note:** Transaction managed by ADO.NET. GETDATE() converted to CURRENT_TIMESTAMP.

### DeleteProductAsync Transaction (Step 6)

**Original SQL Server:**
```sql
BEGIN TRANSACTION;
    DECLARE @OldPrice DECIMAL(18,2);
    DECLARE @OldStock INT;
    
    SELECT @OldPrice = Price, @OldStock = StockQuantity
    FROM Products
    WHERE ProductId = @ProductId;
    
    INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
    VALUES (@ProductId, 'DELETE', @OldPrice, NULL, @OldStock, NULL, GETDATE());
    
    DELETE FROM Products 
    WHERE ProductId = @ProductId;
    
    UPDATE ProductStats
    SET 
        TotalProducts = TotalProducts - 1,
        AveragePrice = CASE 
            WHEN TotalProducts > 1 
            THEN (AveragePrice * TotalProducts - @OldPrice) / (TotalProducts - 1)
            ELSE 0
        END,
        LastUpdated = GETDATE()
    WHERE StatId = 1;
COMMIT;
```

**Converted PostgreSQL:**
```sql
WITH deleted_product AS (
    DELETE FROM productmanagement_dbo.products
    WHERE productid = @ProductId
    RETURNING productid, price, stockquantity
)
INSERT INTO productmanagement_dbo.producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
SELECT productid, 'DELETE', price, NULL, stockquantity, NULL, CURRENT_TIMESTAMP
FROM deleted_product;

UPDATE productmanagement_dbo.productstats
SET 
    totalproducts = totalproducts - 1,
    averageprice = CASE 
        WHEN totalproducts > 1 
        THEN (averageprice * totalproducts - (SELECT price FROM deleted_product)) / (totalproducts - 1)
        ELSE 0
    END,
    lastupdated = CURRENT_TIMESTAMP
WHERE statid = 1;
```

**Note:** Using DELETE...RETURNING to capture old values. Transaction managed by ADO.NET.

## Key Conversion Patterns

1. **Schema Names:** `dbo.Products` → `productmanagement_dbo.products`
2. **Column Names:** All converted to lowercase (PostgreSQL convention)
3. **GETDATE():** Converted to `CURRENT_TIMESTAMP` or `NOW()`
4. **SCOPE_IDENTITY():** Replaced with `RETURNING` clause
5. **Window Functions:** Syntax preserved, works in PostgreSQL
6. **CASE Expressions:** Syntax preserved
7. **Parameter Syntax:** `@Parameter` works with Npgsql (no change needed)
8. **Transactions:** Managed at ADO.NET level with BeginTransactionAsync()
9. **NULLS FIRST:** Added to ORDER BY clauses by DMS for PostgreSQL compatibility
