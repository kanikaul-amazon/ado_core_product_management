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
                WITH ProductStats AS (
                    SELECT 
                        ProductId,
                        AVG(Price) OVER() as AvgPrice,
                        COUNT(*) OVER() as TotalProducts
                    FROM Products
                )
                SELECT 
                    p.ProductId,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.StockQuantity,
                    p.CreatedDate,
                    p.ModifiedDate,
                    CASE 
                        WHEN p.Price > ps.AvgPrice THEN 'Above Average'
                        WHEN p.Price < ps.AvgPrice THEN 'Below Average'
                        ELSE 'Average'
                    END as PriceCategory,
                    ROUND((p.Price / ps.AvgPrice) * 100, 2) as PricePercentageOfAverage
                FROM Products p
                INNER JOIN ProductStats ps ON p.ProductId = ps.ProductId
                ORDER BY 
                    CASE 
                        WHEN p.Price > ps.AvgPrice THEN 1
                        ELSE 2
                    END,
                    p.Name";

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
                WITH ProductHistory AS (
                    SELECT 
                        ProductId,
                        LAG(Price) OVER (ORDER BY ModifiedDate) as PreviousPrice,
                        LAG(StockQuantity) OVER (ORDER BY ModifiedDate) as PreviousStock
                    FROM Products
                    WHERE ProductId = :ProductId
                )
                SELECT 
                    p.ProductId,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.StockQuantity,
                    p.CreatedDate,
                    p.ModifiedDate,
                    ph.PreviousPrice,
                    ph.PreviousStock,
                    CASE 
                        WHEN ph.PreviousPrice IS NOT NULL THEN 
                            ROUND(((p.Price - ph.PreviousPrice) / ph.PreviousPrice) * 100, 2)
                        ELSE NULL
                    END as PriceChangePercentage
                FROM Products p
                LEFT JOIN ProductHistory ph ON p.ProductId = ph.ProductId
                WHERE p.ProductId = :ProductId";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ProductId", productId);

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
                INSERT INTO Products (Name, Description, Price, StockQuantity)
                VALUES (:Name, :Description, :Price, :StockQuantity)
                RETURNING ProductId;";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Name", product.Name);
            command.Parameters.AddWithValue("Description", (object)product.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("Price", product.Price);
            command.Parameters.AddWithValue("StockQuantity", product.StockQuantity);

            var newProductId = Convert.ToInt32(await command.ExecuteScalarAsync());
            
            // Now handle transaction for history and stats separately
            using (var transaction = await connection.BeginTransactionAsync())
            {
                try
                {
                    // Log the insertion
                    const string historyInsertSql = @"
                        INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
                        VALUES (:ProductId, 'INSERT', NULL, :Price, NULL, :StockQuantity, NOW());";
                    
                    using (var historyCommand = new NpgsqlCommand(historyInsertSql, connection, transaction))
                    {
                        historyCommand.Parameters.AddWithValue("ProductId", newProductId);
                        historyCommand.Parameters.AddWithValue("Price", product.Price);
                        historyCommand.Parameters.AddWithValue("StockQuantity", product.StockQuantity);
                        await historyCommand.ExecuteNonQueryAsync();
                    }

                    // Update product statistics
                    const string updateStatsSql = @"
                        UPDATE ProductStats
                        SET 
                            TotalProducts = TotalProducts + 1,
                            AveragePrice = (AveragePrice * TotalProducts + :Price) / (TotalProducts + 1),
                            LastUpdated = NOW()
                        WHERE StatId = 1;";
                    
                    using (var statsCommand = new NpgsqlCommand(updateStatsSql, connection, transaction))
                    {
                        statsCommand.Parameters.AddWithValue("Price", product.Price);
                        await statsCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            return newProductId;
        }

        public async Task UpdateProductAsync(Product product)
        {
            var connection = await GetConnectionAsync();

            using (var transaction = await connection.BeginTransactionAsync())
            {
                try
                {
                    // Store old values for history
                    decimal oldPrice = 0;
                    int oldStock = 0;
                    
                    const string selectOldValuesSql = @"
                        SELECT Price, StockQuantity
                        FROM Products
                        WHERE ProductId = :ProductId;";
                    
                    using (var selectCommand = new NpgsqlCommand(selectOldValuesSql, connection, transaction))
                    {
                        selectCommand.Parameters.AddWithValue("ProductId", product.ProductId);
                        using (var reader = await selectCommand.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                oldPrice = reader.GetDecimal(0);
                                oldStock = reader.GetInt32(1);
                            }
                        }
                    }
                    
                    // Update the product
                    const string updateSql = @"
                        UPDATE Products
                        SET 
                            Name = :Name,
                            Description = :Description,
                            Price = :Price,
                            StockQuantity = :StockQuantity,
                            ModifiedDate = NOW()
                        WHERE ProductId = :ProductId;";
                    
                    using (var updateCommand = new NpgsqlCommand(updateSql, connection, transaction))
                    {
                        updateCommand.Parameters.AddWithValue("ProductId", product.ProductId);
                        updateCommand.Parameters.AddWithValue("Name", product.Name);
                        updateCommand.Parameters.AddWithValue("Description", (object)product.Description ?? DBNull.Value);
                        updateCommand.Parameters.AddWithValue("Price", product.Price);
                        updateCommand.Parameters.AddWithValue("StockQuantity", product.StockQuantity);
                        await updateCommand.ExecuteNonQueryAsync();
                    }
                    
                    // Log the changes
                    const string historyInsertSql = @"
                        INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
                        VALUES (:ProductId, 'UPDATE', :OldPrice, :NewPrice, :OldStock, :NewStock, NOW());";
                    
                    using (var historyCommand = new NpgsqlCommand(historyInsertSql, connection, transaction))
                    {
                        historyCommand.Parameters.AddWithValue("ProductId", product.ProductId);
                        historyCommand.Parameters.AddWithValue("OldPrice", oldPrice);
                        historyCommand.Parameters.AddWithValue("NewPrice", product.Price);
                        historyCommand.Parameters.AddWithValue("OldStock", oldStock);
                        historyCommand.Parameters.AddWithValue("NewStock", product.StockQuantity);
                        await historyCommand.ExecuteNonQueryAsync();
                    }
                    
                    // Update product statistics
                    const string updateStatsSql = @"
                        UPDATE ProductStats
                        SET 
                            AveragePrice = (AveragePrice * TotalProducts - :OldPrice + :NewPrice) / TotalProducts,
                            LastUpdated = NOW()
                        WHERE StatId = 1;";
                    
                    using (var statsCommand = new NpgsqlCommand(updateStatsSql, connection, transaction))
                    {
                        statsCommand.Parameters.AddWithValue("OldPrice", oldPrice);
                        statsCommand.Parameters.AddWithValue("NewPrice", product.Price);
                        await statsCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
        }

        public async Task DeleteProductAsync(int productId)
        {
            var connection = await GetConnectionAsync();

            using (var transaction = await connection.BeginTransactionAsync())
            {
                try
                {
                    // Store product info for history
                    decimal oldPrice = 0;
                    int oldStock = 0;
                    
                    const string selectOldValuesSql = @"
                        SELECT Price, StockQuantity
                        FROM Products
                        WHERE ProductId = :ProductId;";
                    
                    using (var selectCommand = new NpgsqlCommand(selectOldValuesSql, connection, transaction))
                    {
                        selectCommand.Parameters.AddWithValue("ProductId", productId);
                        using (var reader = await selectCommand.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                oldPrice = reader.GetDecimal(0);
                                oldStock = reader.GetInt32(1);
                            }
                        }
                    }
                    
                    // Log the deletion
                    const string historyInsertSql = @"
                        INSERT INTO ProductHistory (ProductId, Action, OldPrice, NewPrice, OldStock, NewStock, ActionDate)
                        VALUES (:ProductId, 'DELETE', :OldPrice, NULL, :OldStock, NULL, NOW());";
                    
                    using (var historyCommand = new NpgsqlCommand(historyInsertSql, connection, transaction))
                    {
                        historyCommand.Parameters.AddWithValue("ProductId", productId);
                        historyCommand.Parameters.AddWithValue("OldPrice", oldPrice);
                        historyCommand.Parameters.AddWithValue("OldStock", oldStock);
                        await historyCommand.ExecuteNonQueryAsync();
                    }
                    
                    // Delete the product
                    const string deleteSql = @"
                        DELETE FROM Products 
                        WHERE ProductId = :ProductId;";
                    
                    using (var deleteCommand = new NpgsqlCommand(deleteSql, connection, transaction))
                    {
                        deleteCommand.Parameters.AddWithValue("ProductId", productId);
                        await deleteCommand.ExecuteNonQueryAsync();
                    }
                    
                    // Update product statistics
                    const string updateStatsSql = @"
                        UPDATE ProductStats
                        SET 
                            TotalProducts = TotalProducts - 1,
                            AveragePrice = CASE 
                                WHEN TotalProducts > 1 
                                THEN (AveragePrice * TotalProducts - :OldPrice) / (TotalProducts - 1)
                                ELSE 0
                            END,
                            LastUpdated = NOW()
                        WHERE StatId = 1;";
                    
                    using (var statsCommand = new NpgsqlCommand(updateStatsSql, connection, transaction))
                    {
                        statsCommand.Parameters.AddWithValue("OldPrice", oldPrice);
                        await statsCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
        }

        public async Task<List<Product>> GetProductsByPriceRangeAsync(decimal minPrice, decimal maxPrice)
        {
            var products = new List<Product>();
            var connection = await GetConnectionAsync();

            const string sql = @"
                WITH RankedProducts AS (
                    SELECT 
                        p.*,
                        RANK() OVER (ORDER BY p.Price) as PriceRank,
                        PERCENT_RANK() OVER (ORDER BY p.Price) as PricePercentile
                    FROM Products p
                    WHERE p.Price BETWEEN :MinPrice AND :MaxPrice
                )
                SELECT 
                    rp.*,
                    CASE 
                        WHEN rp.PricePercentile <= 0.25 THEN 'Budget'
                        WHEN rp.PricePercentile <= 0.75 THEN 'Mid-Range'
                        ELSE 'Premium'
                    END as PriceSegment
                FROM RankedProducts rp
                ORDER BY rp.PriceRank";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("MinPrice", minPrice);
            command.Parameters.AddWithValue("MaxPrice", maxPrice);

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
                WITH StockAnalysis AS (
                    SELECT 
                        p.*,
                        AVG(StockQuantity) OVER() as AvgStock,
                        MIN(StockQuantity) OVER() as MinStock,
                        MAX(StockQuantity) OVER() as MaxStock
                    FROM Products p
                )
                SELECT 
                    sa.*,
                    CASE 
                        WHEN StockQuantity <= :Threshold THEN 'Critical'
                        WHEN StockQuantity <= AvgStock * 0.5 THEN 'Low'
                        ELSE 'Adequate'
                    END as StockStatus,
                    ROUND((StockQuantity / AvgStock) * 100, 2) as StockPercentageOfAverage
                FROM StockAnalysis sa
                WHERE StockQuantity <= :Threshold
                ORDER BY StockQuantity";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Threshold", threshold);

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
                ProductId = Convert.ToInt32(reader["ProductId"]),
                Name = reader["Name"].ToString(),
                Description = reader["Description"] == DBNull.Value ? null : reader["Description"].ToString(),
                Price = Convert.ToDecimal(reader["Price"]),
                StockQuantity = Convert.ToInt32(reader["StockQuantity"]),
                CreatedDate = Convert.ToDateTime(reader["CreatedDate"]),
                ModifiedDate = reader["ModifiedDate"] == DBNull.Value ? null : (DateTime?)Convert.ToDateTime(reader["ModifiedDate"])
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