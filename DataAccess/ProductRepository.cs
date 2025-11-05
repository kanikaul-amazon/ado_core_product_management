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
        private readonly string _connectionString = string.Empty;
        private NpgsqlConnection? _connection;
        private readonly IConfiguration _configuration;

        public ProductRepository(IConfiguration configuration)
        {
            _configuration = configuration;
            var environment = _configuration["Environment"];
            var connectionName = environment == "Production" ? "ProdConnection" : "DevConnection";
            _connectionString = _configuration.GetConnectionString(connectionName) ?? string.Empty;
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
                WITH productstats AS (
                    SELECT 
                        productid, 
                        AVG(price) OVER () AS avgprice, 
                        COUNT(*) OVER () AS totalproducts
                    FROM products
                )
                SELECT
                    p.productid, 
                    p.name, 
                    p.description, 
                    p.price, 
                    p.stockquantity, 
                    p.createddate, 
                    p.modifieddate,
                    CASE
                        WHEN p.price > ps.avgprice THEN 'Above Average'
                        WHEN p.price < ps.avgprice THEN 'Below Average'
                        ELSE 'Average'
                    END AS pricecategory, 
                    ROUND((p.price / ps.avgprice) * 100, 2) AS pricepercentageofaverage
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
                        lag(price) OVER (ORDER BY modifieddate) AS previousprice, 
                        lag(stockquantity) OVER (ORDER BY modifieddate) AS previousstock
                    FROM products
                    WHERE productid = @ProductId
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
                        WHEN ph.previousprice IS NOT NULL THEN ROUND(((p.price - ph.previousprice) / ph.previousprice) * 100, 2)
                        ELSE NULL
                    END AS pricechangepercentage
                FROM products AS p
                LEFT OUTER JOIN producthistory AS ph
                    ON p.productid = ph.productid
                WHERE p.productid = @ProductId;";

            using var command = new NpgsqlCommand(sql, connection);
            // PostgreSQL parameter naming convention: Use @ symbol for parameter names
            command.Parameters.AddWithValue("@ProductId", productId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapProductFromReader(reader);
            }

            return null!;
        }

        public async Task<int> InsertProductAsync(Product product)
        {
            var connection = await GetConnectionAsync();
            
            // Begin an explicit transaction for PostgreSQL
            using var transaction = await connection.BeginTransactionAsync();
            
            try 
            {
                // Insert the new product and get the ID - using RETURNING clause (PostgreSQL specific feature)
                const string insertSql = @"
                    INSERT INTO products (name, description, price, stockquantity)
                    VALUES (@Name, @Description, @Price, @StockQuantity)
                    RETURNING productid;";

                using var insertCommand = new NpgsqlCommand(insertSql, connection);
                insertCommand.Transaction = transaction;
                insertCommand.Parameters.AddWithValue("@Name", product.Name);
                insertCommand.Parameters.AddWithValue("@Description", product.Description as object ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@Price", product.Price);
                insertCommand.Parameters.AddWithValue("@StockQuantity", product.StockQuantity);

                // Execute the query and get the new product ID
                var newProductId = Convert.ToInt32(await insertCommand.ExecuteScalarAsync());
                
                // Log the insertion - using same transaction
                const string logSql = @"
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@NewProductId, 'INSERT', NULL, @Price, NULL, @StockQuantity, CURRENT_TIMESTAMP AT TIME ZONE 'UTC');";
                
                using var logCommand = new NpgsqlCommand(logSql, connection);
                logCommand.Transaction = transaction;
                logCommand.Parameters.AddWithValue("@NewProductId", newProductId);
                logCommand.Parameters.AddWithValue("@Price", product.Price);
                logCommand.Parameters.AddWithValue("@StockQuantity", product.StockQuantity);
                
                await logCommand.ExecuteNonQueryAsync();
                
                // Update product statistics - using same transaction
                const string statsSql = @"
                    UPDATE productstats
                    SET 
                        totalproducts = totalproducts + 1,
                        averageprice = (averageprice * totalproducts + @Price) / (totalproducts + 1),
                        lastupdated = CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                    WHERE statid = 1;";
                
                using var statsCommand = new NpgsqlCommand(statsSql, connection);
                statsCommand.Transaction = transaction;
                statsCommand.Parameters.AddWithValue("@Price", product.Price);
                
                await statsCommand.ExecuteNonQueryAsync();
                
                // Commit the transaction
                await transaction.CommitAsync();
                
                return newProductId;
            }
            catch (Exception)
            {
                // Rollback on any error
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task UpdateProductAsync(Product product)
        {
            var connection = await GetConnectionAsync();
            
            // Begin an explicit transaction for PostgreSQL
            using var transaction = await connection.BeginTransactionAsync();
            
            try 
            {
                // Store old values for history
                const string getOldValuesSql = @"
                    SELECT price AS oldprice, stockquantity AS oldstock
                    FROM products
                    WHERE productid = @ProductId;";
                
                using var getOldValuesCommand = new NpgsqlCommand(getOldValuesSql, connection);
                getOldValuesCommand.Transaction = transaction;
                getOldValuesCommand.Parameters.AddWithValue("@ProductId", product.ProductId);
                
                decimal oldPrice = 0;
                int oldStock = 0;
                
                using (var reader = await getOldValuesCommand.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        oldPrice = reader.GetDecimal(0);
                        oldStock = reader.GetInt32(1);
                    }
                }
                
                // Update the product
                const string updateSql = @"
                    UPDATE products
                    SET 
                        name = @Name,
                        description = @Description,
                        price = @Price,
                        stockquantity = @StockQuantity,
                        modifieddate = CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                    WHERE productid = @ProductId;";
                
                using var updateCommand = new NpgsqlCommand(updateSql, connection);
                updateCommand.Transaction = transaction;
                updateCommand.Parameters.AddWithValue("@ProductId", product.ProductId);
                updateCommand.Parameters.AddWithValue("@Name", product.Name);
                updateCommand.Parameters.AddWithValue("@Description", product.Description as object ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@Price", product.Price);
                updateCommand.Parameters.AddWithValue("@StockQuantity", product.StockQuantity);
                
                await updateCommand.ExecuteNonQueryAsync();
                
                // Log the changes
                const string logSql = @"
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@ProductId, 'UPDATE', @OldPrice, @Price, @OldStock, @StockQuantity, CURRENT_TIMESTAMP AT TIME ZONE 'UTC');";
                
                using var logCommand = new NpgsqlCommand(logSql, connection);
                logCommand.Transaction = transaction;
                logCommand.Parameters.AddWithValue("@ProductId", product.ProductId);
                logCommand.Parameters.AddWithValue("@OldPrice", oldPrice);
                logCommand.Parameters.AddWithValue("@Price", product.Price);
                logCommand.Parameters.AddWithValue("@OldStock", oldStock);
                logCommand.Parameters.AddWithValue("@StockQuantity", product.StockQuantity);
                
                await logCommand.ExecuteNonQueryAsync();
                
                // Update product statistics
                const string statsSql = @"
                    UPDATE productstats
                    SET 
                        averageprice = (averageprice * totalproducts - @OldPrice + @Price) / totalproducts,
                        lastupdated = CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                    WHERE statid = 1;";
                
                using var statsCommand = new NpgsqlCommand(statsSql, connection);
                statsCommand.Transaction = transaction;
                statsCommand.Parameters.AddWithValue("@OldPrice", oldPrice);
                statsCommand.Parameters.AddWithValue("@Price", product.Price);
                
                await statsCommand.ExecuteNonQueryAsync();
                
                // Commit the transaction
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                // Rollback on any error
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task DeleteProductAsync(int productId)
        {
            var connection = await GetConnectionAsync();
            
            // Begin an explicit transaction for PostgreSQL
            using var transaction = await connection.BeginTransactionAsync();
            
            try 
            {
                // Store old values for history
                const string getOldValuesSql = @"
                    SELECT price AS oldprice, stockquantity AS oldstock
                    FROM products
                    WHERE productid = @ProductId;";
                
                using var getOldValuesCommand = new NpgsqlCommand(getOldValuesSql, connection);
                getOldValuesCommand.Transaction = transaction;
                getOldValuesCommand.Parameters.AddWithValue("@ProductId", productId);
                
                decimal oldPrice = 0;
                int oldStock = 0;
                int totalProducts = 0;
                
                using (var reader = await getOldValuesCommand.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        oldPrice = reader.GetDecimal(0);
                        oldStock = reader.GetInt32(1);
                    }
                }
                
                // Get total products count for average price calculation
                const string getStatsSql = @"
                    SELECT totalproducts
                    FROM productstats
                    WHERE statid = 1;";
                
                using var getStatsCommand = new NpgsqlCommand(getStatsSql, connection);
                getStatsCommand.Transaction = transaction;
                
                totalProducts = Convert.ToInt32(await getStatsCommand.ExecuteScalarAsync());
                
                // Log the deletion
                const string logSql = @"
                    INSERT INTO producthistory (productid, action, oldprice, newprice, oldstock, newstock, actiondate)
                    VALUES (@ProductId, 'DELETE', @OldPrice, NULL, @OldStock, NULL, CURRENT_TIMESTAMP AT TIME ZONE 'UTC');";
                
                using var logCommand = new NpgsqlCommand(logSql, connection);
                logCommand.Transaction = transaction;
                logCommand.Parameters.AddWithValue("@ProductId", productId);
                logCommand.Parameters.AddWithValue("@OldPrice", oldPrice);
                logCommand.Parameters.AddWithValue("@OldStock", oldStock);
                
                await logCommand.ExecuteNonQueryAsync();
                
                // Delete the product
                const string deleteSql = @"
                    DELETE FROM products 
                    WHERE productid = @ProductId;";
                
                using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
                deleteCommand.Transaction = transaction;
                deleteCommand.Parameters.AddWithValue("@ProductId", productId);
                
                await deleteCommand.ExecuteNonQueryAsync();
                
                // Update product statistics
                const string statsSql = @"
                    UPDATE productstats
                    SET 
                        totalproducts = totalproducts - 1,
                        averageprice = CASE 
                            WHEN totalproducts > 1 
                            THEN (averageprice * @TotalProducts - @OldPrice) / (@TotalProducts - 1)
                            ELSE 0
                        END,
                        lastupdated = CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                    WHERE statid = 1;";
                
                using var statsCommand = new NpgsqlCommand(statsSql, connection);
                statsCommand.Transaction = transaction;
                statsCommand.Parameters.AddWithValue("@OldPrice", oldPrice);
                statsCommand.Parameters.AddWithValue("@TotalProducts", totalProducts);
                
                await statsCommand.ExecuteNonQueryAsync();
                
                // Commit the transaction
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                // Rollback on any error
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<List<Product>> GetProductsByPriceRangeAsync(decimal minPrice, decimal maxPrice)
        {
            var products = new List<Product>();
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH rankedproducts AS (
                    SELECT
                        p.*, 
                        RANK() OVER (ORDER BY p.price) AS pricerank, 
                        percent_rank() OVER (ORDER BY p.price) AS pricepercentile
                    FROM products AS p
                    WHERE p.price BETWEEN @MinPrice AND @MaxPrice
                )
                SELECT
                    rp.*,
                    CASE
                        WHEN rp.pricepercentile <= 0.25 THEN 'Budget'
                        WHEN rp.pricepercentile <= 0.75 THEN 'Mid-Range'
                        ELSE 'Premium'
                    END AS pricesegment
                FROM rankedproducts AS rp
                ORDER BY rp.pricerank NULLS FIRST;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@MinPrice", minPrice);
            command.Parameters.AddWithValue("@MaxPrice", maxPrice);

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
                        AVG(stockquantity) OVER () AS avgstock, 
                        MIN(stockquantity) OVER () AS minstock, 
                        MAX(stockquantity) OVER () AS maxstock
                    FROM products AS p
                )
                SELECT
                    sa.*,
                    CASE
                        WHEN stockquantity <= @Threshold THEN 'Critical'
                        WHEN stockquantity <= avgstock * 0.5 THEN 'Low'
                        ELSE 'Adequate'
                    END AS stockstatus, 
                    ROUND((stockquantity / avgstock) * 100, 2) AS stockpercentageofaverage
                FROM stockanalysis AS sa
                WHERE stockquantity <= @Threshold
                ORDER BY stockquantity NULLS FIRST;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Threshold", threshold);

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