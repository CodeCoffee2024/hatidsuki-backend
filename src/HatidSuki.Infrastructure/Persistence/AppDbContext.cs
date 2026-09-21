using System.Reflection;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HatidSuki.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser current) : DbContext(options), IAppDbContext
{
    public const string Schema = "public";

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Form> Forms => Set<Form>();
    public DbSet<FormVersion> FormVersions => Set<FormVersion>();
    public DbSet<FormSource> FormSources => Set<FormSource>();
    public DbSet<DeliveryLocation> DeliveryLocations => Set<DeliveryLocation>();
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>
    /// The tenant every query is scoped to. Null for anonymous callers (public pages) and background work, in which case
    /// the global filter matches nothing unless the code explicitly calls IgnoreQueryFilters() and scopes by workspace itself.
    /// </summary>
    private Guid? TenantId => current.WorkspaceId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Ids are generated in code (Guid.NewGuid). Without this, EF treats a new child added to an already-tracked order
        // as an existing row to UPDATE (key is set) instead of a row to INSERT, and saving fails with a concurrency error.
        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)).Select(t => t.ClrType))
            b.Entity(type).Property(nameof(Entity.Id)).ValueGeneratedNever();

        // Every tenant-owned entity is filtered by workspace automatically.
        var method = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)).Select(t => t.ClrType))
            method.MakeGenericMethod(type).Invoke(this, [b]);
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantEntity =>
        b.Entity<T>().HasQueryFilter(e => TenantId != null && e.WorkspaceId == TenantId);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        GuardTenantWrites();
        return base.SaveChangesAsync(ct);
    }

    /// <summary>Refuses to save a tenant-owned row that belongs to a different workspace than the signed-in user's.</summary>
    private void GuardTenantWrites()
    {
        if (TenantId is not { } tenant) return;
        var foreign = ChangeTracker.Entries<ITenantEntity>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.WorkspaceId != tenant);
        if (foreign) throw new InvalidOperationException("Attempt to write data belonging to another workspace.");
    }

    public async Task<int> NextOrderNumberAsync(Guid workspaceId, CancellationToken ct)
    {
        // A single atomic UPDATE ... RETURNING: two customers ordering at once can never get the same number.
        var connection = Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = $"UPDATE {Schema}.workspaces SET next_order_number = next_order_number + 1 WHERE id = @id RETURNING next_order_number - 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = workspaceId;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Workspace not found.");
            return Convert.ToInt32(result);
        }
        finally
        {
            if (openedHere) await Database.CloseConnectionAsync();
        }
    }
}
