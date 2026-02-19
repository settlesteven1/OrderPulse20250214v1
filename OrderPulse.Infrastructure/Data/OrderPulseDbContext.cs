using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;

namespace OrderPulse.Infrastructure.Data;

public class OrderPulseDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;

    public OrderPulseDbContext(DbContextOptions<OrderPulseDbContext> options, ITenantProvider tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Retailer> Retailers => Set<Retailer>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<Return> Returns => Set<Return>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Global tenant filter ──
        // This ensures every query automatically filters by the current tenant.
        // Combined with Azure SQL RLS, this provides defense-in-depth.
        var tenantId = _tenantProvider.GetTenantId();

        modelBuilder.Entity<EmailMessage>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<Shipment>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<Delivery>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<Return>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<Refund>().HasQueryFilter(e => e.TenantId == tenantId);
        modelBuilder.Entity<OrderEvent>().HasQueryFilter(e => e.TenantId == tenantId);

        // ── Tenant ──
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.TenantId);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.MailboxProvider).HasConversion<string>().HasMaxLength(50);
        });

        // ── Retailer ──
        modelBuilder.Entity<Retailer>(entity =>
        {
            entity.HasKey(e => e.RetailerId);
            entity.HasIndex(e => e.NormalizedName);
        });

        // ── EmailMessage ──
        modelBuilder.Entity<EmailMessage>(entity =>
        {
            entity.HasKey(e => e.EmailMessageId);
            entity.HasIndex(e => new { e.TenantId, e.GraphMessageId }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ProcessingStatus });
            entity.Property(e => e.ClassificationType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ProcessingStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.OriginalFromAddress).HasMaxLength(320);
            entity.HasOne(e => e.Tenant).WithMany(t => t.EmailMessages).HasForeignKey(e => e.TenantId);
        });

        // ── Order ──
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.OrderId);
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => new { e.TenantId, e.ExternalOrderNumber });
            entity.HasIndex(e => new { e.TenantId, e.OrderDate });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Subtotal).HasColumnType("decimal(10,2)");
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ShippingCost).HasColumnType("decimal(10,2)");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(10,2)");
            entity.HasOne(e => e.Tenant).WithMany(t => t.Orders).HasForeignKey(e => e.TenantId);
            entity.HasOne(e => e.Retailer).WithMany(r => r.Orders).HasForeignKey(e => e.RetailerId);
            entity.HasOne(e => e.SourceEmail).WithMany().HasForeignKey(e => e.SourceEmailId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.LastUpdatedEmail).WithMany().HasForeignKey(e => e.LastUpdatedEmailId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── OrderLine ──
        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(e => e.OrderLineId);
            entity.HasIndex(e => new { e.OrderId, e.LineNumber }).IsUnique();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(10,2)");
            entity.Property(e => e.LineTotal).HasColumnType("decimal(10,2)");
            entity.HasOne(e => e.Order).WithMany(o => o.Lines).HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Shipment ──
        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.ShipmentId);
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => e.OrderId);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.HasOne(e => e.Order).WithMany(o => o.Shipments).HasForeignKey(e => e.OrderId);
            entity.HasOne(e => e.SourceEmail).WithMany().HasForeignKey(e => e.SourceEmailId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── ShipmentLine ──
        modelBuilder.Entity<ShipmentLine>(entity =>
        {
            entity.HasKey(e => e.ShipmentLineId);
            entity.HasOne(e => e.Shipment).WithMany(s => s.Lines).HasForeignKey(e => e.ShipmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.OrderLine).WithMany(ol => ol.ShipmentLines).HasForeignKey(e => e.OrderLineId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── Delivery ──
        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.HasKey(e => e.DeliveryId);
            entity.HasIndex(e => e.ShipmentId);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.IssueType).HasConversion<string>().HasMaxLength(100);
            entity.HasOne(e => e.Shipment).WithOne(s => s.Delivery).HasForeignKey<Delivery>(e => e.ShipmentId);
            entity.HasOne(e => e.SourceEmail).WithMany().HasForeignKey(e => e.SourceEmailId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── Return ──
        modelBuilder.Entity<Return>(entity =>
        {
            entity.HasKey(e => e.ReturnId);
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => e.OrderId);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ReturnMethod).HasConversion<string>().HasMaxLength(100);
            entity.HasOne(e => e.Order).WithMany(o => o.Returns).HasForeignKey(e => e.OrderId);
            entity.HasOne(e => e.SourceEmail).WithMany().HasForeignKey(e => e.SourceEmailId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── ReturnLine ──
        modelBuilder.Entity<ReturnLine>(entity =>
        {
            entity.HasKey(e => e.ReturnLineId);
            entity.HasOne(e => e.Return).WithMany(r => r.Lines).HasForeignKey(e => e.ReturnId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.OrderLine).WithMany(ol => ol.ReturnLines).HasForeignKey(e => e.OrderLineId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── Refund ──
        modelBuilder.Entity<Refund>(entity =>
        {
            entity.HasKey(e => e.RefundId);
            entity.HasIndex(e => e.OrderId);
            entity.Property(e => e.RefundAmount).HasColumnType("decimal(10,2)");
            entity.HasOne(e => e.Order).WithMany(o => o.Refunds).HasForeignKey(e => e.OrderId);
            entity.HasOne(e => e.Return).WithOne(r => r.Refund).HasForeignKey<Refund>(e => e.ReturnId);
            entity.HasOne(e => e.SourceEmail).WithMany().HasForeignKey(e => e.SourceEmailId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── OrderEvent ──
        modelBuilder.Entity<OrderEvent>(entity =>
        {
            entity.HasKey(e => e.EventId);
            entity.HasIndex(e => new { e.OrderId, e.EventDate });
            entity.HasIndex(e => new { e.TenantId, e.EventDate });
            entity.HasOne(e => e.Order).WithMany(o => o.Events).HasForeignKey(e => e.OrderId);
            entity.HasOne(e => e.EmailMessage).WithMany().HasForeignKey(e => e.EmailMessageId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
