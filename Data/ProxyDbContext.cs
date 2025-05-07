using LLMProxy.Models;

using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Data
{
    public class ProxyDbContext : DbContext
    {
        public DbSet<ApiLogEntry> ApiLogEntries
        {
            get; set;
        }

        public ProxyDbContext(DbContextOptions<ProxyDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // You can add specific configurations here if needed
        }
    }
}