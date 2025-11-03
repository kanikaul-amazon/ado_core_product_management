using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;
using Microsoft.Extensions.Configuration;
using AdoCore.Models;

namespace AdoCore.DataAccess
{
    public class ProductRepository : IAsyncDisposable
    {
        private readonly string _connectionString;
        private NpgsqlConnection _connection;
        private readonly IConfiguration _configuration;

        public ProductRepository(IConfiguration configuration)
        {
            _configuration = configuration;
            var environment = _configuration["Environment"];
            var connectionName = environment == "Production" ? "ProdConnection" : "DevConnection";
            _connectionString = _configuration.GetConnectionString(connectionName);
        }

        private async Task<NpgsqlConnection> GetConnectionAsync()
        {
            if (_connection == null)
            {
                _connection = new NpgsqlConnection(_connectionString);
            }
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync();
            }
            return _connection;
        }

        public async Task<List<Product>> GetAllProductsAsync()
        {
            var products = new List<Product>();
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH productstats
                AS (SELECT
                    productid, AVG(price) OVER () AS avgprice, COUNT(*) OVER () AS totalproducts
                    FROM products)
                SELECT
                    p.productid, p.name, p.description, p.price, p.stockquantity, p.createddate, p.modifieddate,
                    CASE
                        WHEN p.price > ps.avgprice THEN 'Above Average'
                        WHEN p.price < ps.avgprice THEN 'Below Average'
                        ELSE 'Average'
                    END AS pricecategory, ROUND((p.price / ps.avgprice) * 100, 2) AS pricepercentageofaverage
                    FROM products AS p
                    INNER JOIN productstats AS ps
                        ON p.productid = ps.productid
                    ORDER BY
                    CASE
                        WHEN p.price > ps.avgprice THEN 1
                        ELSE 2
                    END NULLS FIRST, p.name NULLS FIRST;";

            using var command = new NpgsqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                products.Add(MapProductFromReader(reader));
            }

            return products;
        }

        public async Task<Product> GetProductByIdAsync(int productId)
        {
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH producthistory AS (
                    SELECT 
                        productid,
                        LAG(price) OVER (ORDER BY modifieddate) as previousprice,
                        LAG(stockquantity) OVER (ORDER BY modifieddate) as previousstock
                    FROM products
                    WHERE productid = @productid
                )
                SELECT 
                    p.productid,
                    p.name,
                    p.description,
                    p.price,
                    p.stockquantity,
                    p.createddate,
                    p.modifieddate,
                    ph.previousprice,
                    ph.previousstock,
                    CASE 
                        WHEN ph.previousprice IS NOT NULL THEN 
                            ROUND(((p.price - ph.previousprice) / ph.previousprice) * 100, 2)
                        ELSE NULL
                    END as pricechangepercentage
                FROM products p
                LEFT JOIN producthistory ph ON p.productid = ph.productid
                WHERE p.productid = @productid;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@productid", productId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapProductFromReader(reader);
            }

            return null;
        }

        public async Task<int> InsertProductAsync(Product product)
        {
            var connection = await GetConnectionAsync();

            const string sql = @"
                BEGIN;
                    -- Insert the new product
                    INSERT INTO products (name, description, price, stockquantity)
                    VALUES (@name, @description, @price, @stockquantity)
                    RETURNING productid INTO @newproductid;
                    
                    -- Log the insertion
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@newproductid, 'INSERT', NULL, @price, NULL, @stockquantity, CURRENT_TIMESTAMP);
                    
                    -- Update product statistics
                    UPDATE productstats
                    SET 
                        totalproducts = totalproducts + 1,
                        averageprice = (averageprice * totalproducts + @price) / (totalproducts + 1),
                        lastupdated = CURRENT_TIMESTAMP
                    WHERE statid = 1;
                COMMIT;
                
                SELECT @newproductid;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@name", product.Name);
            command.Parameters.AddWithValue("@description", (object)product.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@price", product.Price);
            command.Parameters.AddWithValue("@stockquantity", product.StockQuantity);
            command.Parameters.Add(new NpgsqlParameter("@newproductid", NpgsqlTypes.NpgsqlDbType.Integer) { Direction = ParameterDirection.Output });

            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task UpdateProductAsync(Product product)
        {
            var connection = await GetConnectionAsync();

            const string sql = @"
                BEGIN;
                    -- Store old values for history
                    SELECT price INTO @oldprice, stockquantity INTO @oldstock
                    FROM products
                    WHERE productid = @productid;
                    
                    -- Update the product
                    UPDATE products
                    SET 
                        name = @name,
                        description = @description,
                        price = @price,
                        stockquantity = @stockquantity,
                        modifieddate = CURRENT_TIMESTAMP
                    WHERE productid = @productid;
                    
                    -- Log the changes
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@productid, 'UPDATE', @oldprice, @price, @oldstock, @stockquantity, CURRENT_TIMESTAMP);
                    
                    -- Update product statistics
                    UPDATE productstats
                    SET 
                        averageprice = (averageprice * totalproducts - @oldprice + @price) / totalproducts,
                        lastupdated = CURRENT_TIMESTAMP
                    WHERE statid = 1;
                COMMIT;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@productid", product.ProductId);
            command.Parameters.AddWithValue("@name", product.Name);
            command.Parameters.AddWithValue("@description", (object)product.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@price", product.Price);
            command.Parameters.AddWithValue("@stockquantity", product.StockQuantity);
            command.Parameters.Add(new NpgsqlParameter("@oldprice", NpgsqlTypes.NpgsqlDbType.Numeric) { Direction = ParameterDirection.Output });
            command.Parameters.Add(new NpgsqlParameter("@oldstock", NpgsqlTypes.NpgsqlDbType.Integer) { Direction = ParameterDirection.Output });

            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteProductAsync(int productId)
        {
            var connection = await GetConnectionAsync();

            const string sql = @"
                BEGIN;
                    -- Store product info for history
                    SELECT price INTO @oldprice, stockquantity INTO @oldstock
                    FROM products
                    WHERE productid = @productid;
                    
                    -- Log the deletion
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@productid, 'DELETE', @oldprice, NULL, @oldstock, NULL, CURRENT_TIMESTAMP);
                    
                    -- Delete the product
                    DELETE FROM products 
                    WHERE productid = @productid;
                    
                    -- Update product statistics
                    UPDATE productstats
                    SET 
                        totalproducts = totalproducts - 1,
                        averageprice = CASE 
                            WHEN totalproducts > 1 
                            THEN (averageprice * totalproducts - @oldprice) / (totalproducts - 1)
                            ELSE 0
                        END,
                        lastupdated = CURRENT_TIMESTAMP
                    WHERE statid = 1;
                COMMIT;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@productid", productId);
            command.Parameters.Add(new NpgsqlParameter("@oldprice", NpgsqlTypes.NpgsqlDbType.Numeric) { Direction = ParameterDirection.Output });
            command.Parameters.Add(new NpgsqlParameter("@oldstock", NpgsqlTypes.NpgsqlDbType.Integer) { Direction = ParameterDirection.Output });

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<Product>> GetProductsByPriceRangeAsync(decimal minPrice, decimal maxPrice)
        {
            var products = new List<Product>();
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH rankedproducts AS (
                    SELECT 
                        p.*,
                        RANK() OVER (ORDER BY p.price) as pricerank,
                        PERCENT_RANK() OVER (ORDER BY p.price) as pricepercentile
                    FROM products p
                    WHERE p.price BETWEEN @minprice AND @maxprice
                )
                SELECT 
                    rp.*,
                    CASE 
                        WHEN rp.pricepercentile <= 0.25 THEN 'Budget'
                        WHEN rp.pricepercentile <= 0.75 THEN 'Mid-Range'
                        ELSE 'Premium'
                    END as pricesegment
                FROM rankedproducts rp
                ORDER BY rp.pricerank;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@minprice", minPrice);
            command.Parameters.AddWithValue("@maxprice", maxPrice);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                products.Add(MapProductFromReader(reader));
            }

            return products;
        }

        public async Task<List<Product>> GetLowStockProductsAsync(int threshold)
        {
            var products = new List<Product>();
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH stockanalysis AS (
                    SELECT 
                        p.*,
                        AVG(stockquantity) OVER() as avgstock,
                        MIN(stockquantity) OVER() as minstock,
                        MAX(stockquantity) OVER() as maxstock
                    FROM products p
                )
                SELECT 
                    sa.*,
                    CASE 
                        WHEN stockquantity <= @threshold THEN 'Critical'
                        WHEN stockquantity <= avgstock * 0.5 THEN 'Low'
                        ELSE 'Adequate'
                    END as stockstatus,
                    ROUND((stockquantity / avgstock) * 100, 2) as stockpercentageofaverage
                FROM stockanalysis sa
                WHERE stockquantity <= @threshold
                ORDER BY stockquantity;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@threshold", threshold);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                products.Add(MapProductFromReader(reader));
            }

            return products;
        }

        public async Task ExecuteInTransactionAsync(Func<Task> action)
        {
            var connection = await GetConnectionAsync();
            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await action();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private static Product MapProductFromReader(NpgsqlDataReader reader)
        {
            return new Product
            {
                ProductId = Convert.ToInt32(reader["productid"]),
                Name = reader["name"].ToString(),
                Description = reader["description"] == DBNull.Value ? null : reader["description"].ToString(),
                Price = Convert.ToDecimal(reader["price"]),
                StockQuantity = Convert.ToInt32(reader["stockquantity"]),
                CreatedDate = Convert.ToDateTime(reader["createddate"]),
                ModifiedDate = reader["modifieddate"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(reader["modifieddate"])
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection != null)
            {
                if (_connection.State == ConnectionState.Open)
                {
                    await _connection.CloseAsync();
                }
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
    }
}