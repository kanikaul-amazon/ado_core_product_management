-- Create ProductManagement Database
CREATE DATABASE ProductManagement
WITH
ENCODING = 'UTF8';

\c ProductManagement;

-- Create Products Table
CREATE TABLE products(
    productid SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    description VARCHAR(500) NULL,
    price DECIMAL(18, 2) NOT NULL,
    stockquantity INTEGER NOT NULL,
    createddate TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    modifieddate TIMESTAMP NULL
);

-- Create Stored Procedure for Getting All Products
CREATE OR REPLACE FUNCTION sp_getallproducts()
RETURNS TABLE (
    productid INTEGER,
    name VARCHAR(100),
    description VARCHAR(500),
    price DECIMAL(18,2),
    stockquantity INTEGER,
    createddate TIMESTAMP,
    modifieddate TIMESTAMP
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.productid, p.name, p.description, p.price, p.stockquantity, p.createddate, p.modifieddate
    FROM products p
    ORDER BY p.name;
END;
$$ LANGUAGE plpgsql;

-- Create Stored Procedure for Getting Product by ID
CREATE OR REPLACE FUNCTION sp_getproductbyid(p_productid INTEGER)
RETURNS TABLE (
    productid INTEGER,
    name VARCHAR(100),
    description VARCHAR(500),
    price DECIMAL(18,2),
    stockquantity INTEGER,
    createddate TIMESTAMP,
    modifieddate TIMESTAMP
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.productid, p.name, p.description, p.price, p.stockquantity, p.createddate, p.modifieddate
    FROM products p
    WHERE p.productid = p_productid;
END;
$$ LANGUAGE plpgsql;

-- Create Stored Procedure for Inserting Product
CREATE OR REPLACE FUNCTION sp_insertproduct(
    p_name VARCHAR(100),
    p_description VARCHAR(500),
    p_price DECIMAL(18,2),
    p_stockquantity INTEGER
) RETURNS INTEGER AS $$
DECLARE
    new_productid INTEGER;
BEGIN
    INSERT INTO products (name, description, price, stockquantity)
    VALUES (p_name, p_description, p_price, p_stockquantity)
    RETURNING productid INTO new_productid;
    
    RETURN new_productid;
END;
$$ LANGUAGE plpgsql;

-- Create Stored Procedure for Updating Product
CREATE OR REPLACE FUNCTION sp_updateproduct(
    p_productid INTEGER,
    p_name VARCHAR(100),
    p_description VARCHAR(500),
    p_price DECIMAL(18,2),
    p_stockquantity INTEGER
) RETURNS VOID AS $$
BEGIN
    UPDATE products
    SET name = p_name,
        description = p_description,
        price = p_price,
        stockquantity = p_stockquantity,
        modifieddate = CURRENT_TIMESTAMP
    WHERE productid = p_productid;
END;
$$ LANGUAGE plpgsql;

-- Create Stored Procedure for Deleting Product
CREATE OR REPLACE FUNCTION sp_deleteproduct(p_productid INTEGER)
RETURNS VOID AS $$
BEGIN
    DELETE FROM products
    WHERE productid = p_productid;
END;
$$ LANGUAGE plpgsql;

-- Insert Sample Data
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM products LIMIT 1) THEN
    PERFORM sp_insertproduct('Laptop', 'High-performance laptop', 999.99, 10);
    PERFORM sp_insertproduct('Mouse', 'Wireless gaming mouse', 49.99, 20);
    PERFORM sp_insertproduct('Keyboard', 'Mechanical keyboard', 129.99, 15);
  END IF;
END $$;