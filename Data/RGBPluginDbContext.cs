using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BTCPayServer.Plugins.RgbUtexo.Data;

public class RGBPluginDbContext : DbContext
{
    static readonly ValueConverter<ulong, long> IssuedSupplyRoundTripsThroughBigintWithoutLosingBits =
        new(supply => unchecked((long)supply), stored => unchecked((ulong)stored));

    public RGBPluginDbContext(DbContextOptions<RGBPluginDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public DbSet<RGBWallet> RGBWallets { get; set; } = null!;
    public DbSet<RGBInvoice> RGBInvoices { get; set; } = null!;
    public DbSet<RGBAsset> RGBAssets { get; set; } = null!;
    public DbSet<RGBStoreAutoReplenishment> RGBStoreAutoReplenishments { get; set; } = null!;
    public DbSet<RGBStoreNoticeState> RGBStoreNoticeStates { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RGBWallet>(entity =>
        {
            entity.ToTable("RGB_Wallets");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.StoreId).IsUnique().HasFilter("\"IsActive\" = true");
            entity.Property(e => e.XpubVanilla).IsRequired();
            entity.Property(e => e.XpubColored).IsRequired();
            entity.Property(e => e.MasterFingerprint).IsRequired();
            entity.Property(e => e.MaxAllocationsPerUtxo).HasDefaultValue(10);
            entity.Property(e => e.NeedsRecovery).HasDefaultValue(false);
            entity.Property(e => e.InvoiceScanCursor);
            entity.Property(e => e.DiscoveryScanCursor);
            entity.Property(e => e.DiscoveryAssetPage).HasDefaultValue(0);
        });

        modelBuilder.Entity<RGBInvoice>(entity =>
        {
            entity.ToTable("RGB_Invoices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.RecipientId);
            entity.HasIndex(e => e.BtcPayInvoiceId);
            entity.HasIndex(e => e.Status);
            
            entity.HasOne(e => e.Wallet)
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RGBStoreAutoReplenishment>(entity =>
        {
            entity.ToTable("RGB_StoreAutoReplenishment");
            entity.HasKey(e => e.StoreId);
        });

        modelBuilder.Entity<RGBStoreNoticeState>(entity =>
        {
            entity.ToTable("RGB_StoreNoticeState");
            entity.HasKey(e => e.StoreId);
        });

        modelBuilder.Entity<RGBAsset>(entity =>
        {
            entity.ToTable("RGB_Assets");
            entity.HasKey(e => new { e.WalletId, e.AssetId });
            entity.HasIndex(e => e.WalletId);
            entity.Property(e => e.IssuedSupply)
                .HasConversion(IssuedSupplyRoundTripsThroughBigintWithoutLosingBits);
            
            entity.HasOne(e => e.Wallet)
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
