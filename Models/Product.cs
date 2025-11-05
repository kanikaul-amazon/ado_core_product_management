using System;

namespace AdoCore.Models
{
    public class Product
    {
        public int ProductId { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }
        public decimal Price { get; set; }
        public int StockQuantity { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public override string ToString()
        {
            return $"ID: {ProductId}\n" +
                   $"Name: {Name}\n" +
                   $"Description: {Description ?? "N/A"}\n" +
                   $"Price: ${Price:F2}\n" +
                   $"Stock: {StockQuantity}\n" +
                   $"Created: {CreatedDate:g}\n" +
                   $"Modified: {(ModifiedDate.HasValue ? ModifiedDate.Value.ToString("g") : "N/A")}";
        }
    }
}