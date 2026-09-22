using FluentAssertions;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Items;
using HatidSuki.Domain.Orders;
using HatidSuki.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.IntegrationTests;

/// <summary>Talks to the real EF Core model (in memory) the way the API does: a fresh context per request.</summary>
public class PersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Burger = Guid.NewGuid();

    private sealed class FakeUser(Guid? workspace) : ICurrentUser
    {
        public Guid? UserId => Guid.NewGuid();
        public Guid? WorkspaceId => workspace;
        public Role? Role => Domain.Role.Owner;
        public string? Name => "Tester";
        public Guid RequireWorkspaceId() => workspace ?? throw new ForbiddenException();
    }

    private static AppDbContext Db(string store, Guid? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store).Options, new FakeUser(tenant));

    private static PartDraft Person(string name, int qty = 1) =>
        new(name, null, [new LineDraft(Burger, "Burger", 8m, qty, null)]);

    private static Order NewOrder(Guid workspace, int number, params string[] people) => Order.Place(
        workspace, Guid.NewGuid(), Guid.NewGuid(), number, "USD", OrderChannel.Public, null, "Organizer", null, null, "[]",
        null, null, false, null, people.Select(p => Person(p)).ToList(), Now, "customer");

    private static IQueryable<Order> Full(AppDbContext db) =>
        db.Orders.Include(o => o.Parts).ThenInclude(p => p.Lines).Include(o => o.Events);

    [Fact]
    public async Task A_person_added_to_an_already_saved_order_is_saved()
    {
        // Regression: new children carry client-generated ids, and EF used to treat them as existing rows to UPDATE,
        // so every change to a saved order failed with a concurrency error.
        var store = Guid.NewGuid().ToString(); var ws = Guid.NewGuid(); Guid id;
        await using (var db = Db(store, ws)) { var o = NewOrder(ws, 1, "Ana"); db.Orders.Add(o); await db.SaveChangesAsync(); id = o.Id; }

        await using (var db = Db(store, ws))
        {
            var o = await Full(db).SingleAsync(x => x.Id == id);
            o.AddPart(Person("Ben", 2), Now, "staff");
            await db.SaveChangesAsync();
        }

        await using var check = Db(store, ws);
        var saved = await Full(check).SingleAsync(x => x.Id == id);
        saved.Parts.Should().HaveCount(2);
        saved.Parts.Single(p => p.PersonLabel == "Ben").IsLateAddition.Should().BeTrue();
        saved.Total.Should().Be(24m);
        saved.Events.Should().Contain(e => e.Type == "part-added");
    }

    [Fact]
    public async Task Ticking_every_person_ready_is_saved_and_moves_the_order_to_ready()
    {
        var store = Guid.NewGuid().ToString(); var ws = Guid.NewGuid(); Guid id;
        await using (var db = Db(store, ws)) { var o = NewOrder(ws, 1, "Ana", "Ben"); db.Orders.Add(o); await db.SaveChangesAsync(); id = o.Id; }

        foreach (var index in new[] { 0, 1 })
        {
            await using var db = Db(store, ws);
            var o = await Full(db).SingleAsync(x => x.Id == id);
            o.SetPartReady(o.Parts.OrderBy(p => p.SortOrder).ElementAt(index).Id, true, Now, "staff");
            await db.SaveChangesAsync();
        }

        await using var check = Db(store, ws);
        var saved = await Full(check).SingleAsync(x => x.Id == id);
        saved.Status.Should().Be(OrderStatus.Ready);
        saved.Parts.Should().OnlyContain(p => p.IsReady);
    }

    [Fact]
    public async Task Cash_tags_and_cancelled_people_are_saved()
    {
        var store = Guid.NewGuid().ToString(); var ws = Guid.NewGuid(); Guid id;
        await using (var db = Db(store, ws)) { var o = NewOrder(ws, 1, "Ana", "Ben"); db.Orders.Add(o); await db.SaveChangesAsync(); id = o.Id; }
        await using (var db = Db(store, ws))
        {
            var o = await Full(db).SingleAsync(x => x.Id == id);
            o.TagPartPaid(o.Parts[0].Id, true, Now, "staff");
            o.CancelPart(o.Parts[1].Id, "left", Now, "staff");
            await db.SaveChangesAsync();
        }

        await using var check = Db(store, ws);
        var saved = await Full(check).SingleAsync(x => x.Id == id);
        saved.PaymentStatus.Should().Be(PaymentStatus.Paid, "the only remaining person paid");
        saved.Total.Should().Be(8m);
    }

    // ---- item options & variants: order lines snapshot the choice, not a live link (FS-008) --------

    [Fact]
    public async Task An_order_lines_selected_options_survive_later_catalog_edits()
    {
        var store = Guid.NewGuid().ToString(); var ws = Guid.NewGuid(); Guid itemId; Guid orderId;
        await using (var db = Db(store, ws))
        {
            var coffee = Item.Create(ws, "Coffee", 80m, null, null, null, 1, Now);
            var size = new ItemOptionGroup { Name = "Size", Required = true, Options = [new() { Name = "Large", PriceDelta = 20m }] };
            coffee.SetOptionGroups([size]);
            db.Items.Add(coffee);
            await db.SaveChangesAsync();
            itemId = coffee.Id;

            var (unitPrice, selected) = coffee.PriceFor(new Dictionary<Guid, List<Guid>> { [size.Id] = [size.Options[0].Id] });
            var order = Order.Place(ws, Guid.NewGuid(), Guid.NewGuid(), 1, "USD", OrderChannel.Public, null, "Ana", null, null, "{}",
                null, null, false, null, [new PartDraft("Ana", null, [new LineDraft(itemId, "Coffee", unitPrice, 1, null, selected)])], Now, "customer");
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        // The business renames the option and changes its price after the order was placed.
        await using (var db = Db(store, ws))
        {
            var item = await db.Items.SingleAsync(i => i.Id == itemId);
            var edited = item.OptionGroups;
            edited[0].Name = "Cup size";
            edited[0].Options[0].Name = "Extra large";
            edited[0].Options[0].PriceDelta = 999m;
            item.SetOptionGroups(edited);
            await db.SaveChangesAsync();
        }

        await using var check = Db(store, ws);
        var saved = await Full(check).SingleAsync(o => o.Id == orderId);
        var line = saved.Parts.Single().Lines.Single();
        line.UnitPrice.Should().Be(100m); // 80 base + 20 delta, frozen at the time of the order
        line.Options.Single().GroupName.Should().Be("Size");
        line.Options.Single().OptionName.Should().Be("Large");
        line.Options.Single().PriceDelta.Should().Be(20m);
    }

    // ---- platform admin: suspending a workspace persists and can be reversed ------------------------

    [Fact]
    public async Task Suspending_a_workspace_persists_and_reactivating_clears_it()
    {
        var store = Guid.NewGuid().ToString(); Guid wsId;
        await using (var db = Db(store, null))
        {
            var ws = Workspace.Create("Ana's Bakery", "anas-bakery-" + store[..8], "USD", "UTC", Now);
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            wsId = ws.Id;
        }

        await using (var db = Db(store, null))
        {
            var ws = await db.Workspaces.SingleAsync(w => w.Id == wsId);
            ws.Suspend("unpaid invoice", Now);
            await db.SaveChangesAsync();
        }

        await using (var check = Db(store, null))
        {
            var ws = await check.Workspaces.SingleAsync(w => w.Id == wsId);
            ws.IsSuspended.Should().BeTrue();
            ws.SuspendedReason.Should().Be("unpaid invoice");
        }

        await using (var db = Db(store, null))
        {
            var ws = await db.Workspaces.SingleAsync(w => w.Id == wsId);
            ws.Reactivate();
            await db.SaveChangesAsync();
        }

        await using var final = Db(store, null);
        var reactivated = await final.Workspaces.SingleAsync(w => w.Id == wsId);
        reactivated.IsSuspended.Should().BeFalse();
        reactivated.SuspendedReason.Should().BeNull();
    }

    // ---- tenant isolation: one business must never see another's data ------------------------------

    [Fact]
    public async Task A_business_only_sees_its_own_items_and_orders()
    {
        var store = Guid.NewGuid().ToString(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await using (var db = Db(store, null))
        {
            db.Items.AddRange(Item.Create(a, "A item", 1, null, null, null, 1, Now), Item.Create(b, "B item", 1, null, null, null, 1, Now));
            db.Orders.AddRange(NewOrder(a, 1, "Ana"), NewOrder(b, 1, "Bea"));
            await db.SaveChangesAsync();
        }

        await using var asA = Db(store, a);
        (await asA.Items.Select(i => i.Name).ToListAsync()).Should().Equal("A item");
        (await asA.Orders.CountAsync()).Should().Be(1);

        await using var asB = Db(store, b);
        (await asB.Items.Select(i => i.Name).ToListAsync()).Should().Equal("B item");
    }

    [Fact]
    public async Task Anonymous_callers_see_nothing_unless_they_scope_explicitly()
    {
        var store = Guid.NewGuid().ToString(); var a = Guid.NewGuid();
        await using (var db = Db(store, null)) { db.Items.Add(Item.Create(a, "A item", 1, null, null, null, 1, Now)); await db.SaveChangesAsync(); }

        await using var anon = Db(store, null);
        (await anon.Items.CountAsync()).Should().Be(0, "no tenant means the filter matches nothing");
        (await anon.Items.IgnoreQueryFilters().Where(i => i.WorkspaceId == a).CountAsync()).Should().Be(1, "public pages resolve the workspace themselves");
    }

    [Fact]
    public async Task Writing_another_businesses_data_is_refused()
    {
        var store = Guid.NewGuid().ToString(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await using var asA = Db(store, a);
        asA.Items.Add(Item.Create(b, "Sneaky", 1, null, null, null, 1, Now));

        await FluentActions.Awaiting(() => asA.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
    }
}
