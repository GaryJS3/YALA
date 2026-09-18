using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using YALA.Data.Entities;

namespace YALA.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<ItemAlias> ItemAliases => Set<ItemAlias>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StoreOffer> StoreOffers => Set<StoreOffer>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<PurchaseHistory> PurchaseHistory => Set<PurchaseHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var timestampConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var optionalTimestampConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.UtcTicks : null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(x => x.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(timestampConverter);
            else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(optionalTimestampConverter);
        }

        builder.Entity<HouseholdMember>().HasKey(x => new { x.HouseholdId, x.UserId });
        builder.Entity<HouseholdMember>().HasOne(x => x.Household).WithMany(x => x.Members).HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<HouseholdMember>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Category>().HasIndex(x => new { x.HouseholdId, x.Name }).IsUnique();
        builder.Entity<Category>().HasAlternateKey(x => new { x.HouseholdId, x.Id });
        builder.Entity<CatalogItem>().HasAlternateKey(x => new { x.HouseholdId, x.Id });
        builder.Entity<ProductVariant>().HasAlternateKey(x => new { x.HouseholdId, x.Id });
        builder.Entity<ProductVariant>().HasAlternateKey(x => new { x.HouseholdId, x.CatalogItemId, x.Id });
        builder.Entity<Store>().HasAlternateKey(x => new { x.HouseholdId, x.Id });
        builder.Entity<StoreOffer>().HasAlternateKey(x => new { x.HouseholdId, x.Id });
        builder.Entity<CatalogItem>().HasIndex(x => new { x.HouseholdId, x.Name });
        builder.Entity<ShoppingListItem>().HasIndex(x => new { x.HouseholdId, x.CatalogItemId }).IsUnique();
        builder.Entity<ProductBarcode>().HasIndex(x => new { x.HouseholdId, x.Barcode }).IsUnique();
        builder.Entity<Store>().HasIndex(x => new { x.HouseholdId, x.Name }).IsUnique();
        builder.Entity<StoreOffer>().HasIndex(x => new { x.HouseholdId, x.CatalogItemId, x.StoreId, x.ProductVariantId }).IsUnique();
        builder.Entity<PriceHistory>().Property(x => x.Price).HasPrecision(10, 2);
        builder.Entity<ShoppingListItem>().Property(x => x.Quantity).HasPrecision(10, 3);
        builder.Entity<PurchaseHistory>().Property(x => x.Quantity).HasPrecision(10, 3);
        builder.Entity<PurchaseHistory>().Property(x => x.Price).HasPrecision(10, 2);

        builder.Entity<CatalogItem>().HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Category>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Store>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CatalogItem>().HasOne(x => x.Category).WithMany(x => x.Items)
            .HasForeignKey(x => new { x.HouseholdId, x.CategoryId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ShoppingListItem>().HasOne(x => x.CatalogItem).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ShoppingListItem>().HasOne(x => x.AssignedStore).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.AssignedStoreId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ItemAlias>().HasOne(x => x.CatalogItem).WithMany(x => x.Aliases)
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ProductVariant>().HasOne(x => x.CatalogItem).WithMany(x => x.Variants)
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ProductBarcode>().HasOne(x => x.ProductVariant).WithMany(x => x.Barcodes)
            .HasForeignKey(x => new { x.HouseholdId, x.ProductVariantId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<StoreOffer>().HasOne(x => x.CatalogItem).WithMany(x => x.StoreOffers)
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<StoreOffer>().HasOne(x => x.Store).WithMany(x => x.Offers)
            .HasForeignKey(x => new { x.HouseholdId, x.StoreId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<StoreOffer>().HasOne(x => x.ProductVariant).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId, x.ProductVariantId }).HasPrincipalKey(x => new { x.HouseholdId, x.CatalogItemId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<PriceHistory>().HasOne(x => x.StoreOffer).WithMany(x => x.Prices)
            .HasForeignKey(x => new { x.HouseholdId, x.StoreOfferId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PurchaseHistory>().HasOne(x => x.CatalogItem).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.CatalogItemId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PurchaseHistory>().HasOne(x => x.ProductVariant).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.ProductVariantId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<PurchaseHistory>().HasOne(x => x.Store).WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.StoreId }).HasPrincipalKey(x => new { x.HouseholdId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
