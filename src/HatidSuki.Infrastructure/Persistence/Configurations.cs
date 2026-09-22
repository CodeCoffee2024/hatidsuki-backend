using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HatidSuki.Infrastructure.Persistence;

public class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("workspaces");
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.Slug).HasMaxLength(40);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Timezone).HasMaxLength(64);
        b.Property(x => x.PhoneCountryCode).HasMaxLength(4);
        b.HasIndex(x => x.Slug).IsUnique();
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.Email).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.Property(x => x.TokenHash).HasMaxLength(100);
        b.HasIndex(x => x.TokenHash).IsUnique();
    }
}

public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("items");
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Category).HasMaxLength(60);
        b.Property(x => x.Unit).HasMaxLength(20);
        b.Property(x => x.Price).HasPrecision(18, 2);
        b.Property(x => x.OptionsJson).HasColumnType("jsonb");
        b.Ignore(x => x.OptionGroups);
        b.HasIndex(x => new { x.WorkspaceId, x.Category, x.SortOrder });
    }
}

public class FormConfiguration : IEntityTypeConfiguration<Form>
{
    public void Configure(EntityTypeBuilder<Form> b)
    {
        b.ToTable("forms");
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.ShortCode).HasMaxLength(12);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DraftJson).HasColumnType("jsonb");
        b.HasIndex(x => x.ShortCode).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
        b.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.FormId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class FormVersionConfiguration : IEntityTypeConfiguration<FormVersion>
{
    public void Configure(EntityTypeBuilder<FormVersion> b)
    {
        b.ToTable("form_versions");
        b.Property(x => x.DefinitionJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.FormId, x.Number }).IsUnique();
    }
}

public class FormSourceConfiguration : IEntityTypeConfiguration<FormSource>
{
    public void Configure(EntityTypeBuilder<FormSource> b)
    {
        b.ToTable("form_sources");
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.Code).HasMaxLength(12);
        b.HasIndex(x => new { x.FormId, x.Code }).IsUnique();
    }
}

public class DeliveryLocationConfiguration : IEntityTypeConfiguration<DeliveryLocation>
{
    public void Configure(EntityTypeBuilder<DeliveryLocation> b)
    {
        b.ToTable("delivery_locations");
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.Note).HasMaxLength(200);
        b.HasIndex(x => new { x.WorkspaceId, x.SortOrder });
    }
}

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("orders");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ManualType).HasMaxLength(40);
        b.Property(x => x.CustomerName).HasMaxLength(120);
        b.Property(x => x.CustomerPhone).HasMaxLength(40);
        b.Property(x => x.CustomerEmail).HasMaxLength(200);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.SourceCode).HasMaxLength(12);
        b.Property(x => x.SourceName).HasMaxLength(60);
        b.Property(x => x.DeliveryLocationName).HasMaxLength(80);
        b.Property(x => x.DeliveryNote).HasMaxLength(200);
        b.Property(x => x.TrackingToken).HasMaxLength(64);
        b.Property(x => x.IdempotencyKey).HasMaxLength(64);
        b.Property(x => x.NotifiedChannel).HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.Total).HasPrecision(18, 2);
        b.Property(x => x.AnswersJson).HasColumnType("jsonb");

        // Postgres' xmin system column doubles as an optimistic-concurrency token: two staff changing the same order
        // at once can never silently overwrite each other.
        b.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        b.HasIndex(x => new { x.WorkspaceId, x.Number }).IsUnique();
        b.HasIndex(x => x.TrackingToken).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        b.HasIndex(x => new { x.WorkspaceId, x.Status, x.CreatedAtUtc });
        b.HasIndex(x => x.FormId);

        // Computed views over Parts must not be mistaken for a second relationship.
        b.Ignore(x => x.ActiveParts);
        b.HasMany(x => x.Parts).WithOne().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Parts).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class OrderPartConfiguration : IEntityTypeConfiguration<OrderPart>
{
    public void Configure(EntityTypeBuilder<OrderPart> b)
    {
        b.ToTable("order_parts");
        b.Property(x => x.PersonLabel).HasMaxLength(60);
        b.Property(x => x.Note).HasMaxLength(300);
        b.Property(x => x.PaidBy).HasMaxLength(80);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("order_lines");
        b.Property(x => x.ItemName).HasMaxLength(120);
        b.Property(x => x.Note).HasMaxLength(200);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Property(x => x.OptionsJson).HasColumnType("jsonb");
        b.Ignore(x => x.Options);
    }
}

public class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> b)
    {
        b.ToTable("order_events");
        b.Property(x => x.Type).HasMaxLength(30);
        b.Property(x => x.Message).HasMaxLength(400);
        b.Property(x => x.By).HasMaxLength(80);
    }
}
