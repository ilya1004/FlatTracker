using FlatTracker.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FlatTracker.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AdRecord> Ads => Set<AdRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdRecord>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.AdId)
                .IsUnique();
            
            entity.Property(e => e.AdLink).IsRequired();
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.LastUpdateTime).IsRequired();
            entity.Property(e => e.FirstSeen).IsRequired();

            entity.Property(e => e.IsCompany)
                .HasConversion<int>();
        });
    }
}
