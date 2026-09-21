using System.Text.Json;
using HatidSuki.Application.Common;
using HatidSuki.Application.Forms;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Infrastructure.Persistence;

/// <summary>
/// Development-only demo business so the app is worth looking at on first run. Turned on by Seed:Demo=true.
/// Safe to run repeatedly: does nothing if the demo workspace already exists.
/// </summary>
public static class DemoSeeder
{
    public const string Email = "demo@hatidsuki.local";
    public const string Password = "HatidSuki1!";
    public const string Slug = "sunrise-bakery";

    public static async Task SeedAsync(AppDbContext db, IPasswordService passwords, IClock clock, CancellationToken ct)
    {
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Slug == Slug, ct);
        if (existing is not null)
        {
            await EnsureDeliveryAsync(db, existing, clock.UtcNow, ct); // demo data created before delivery locations existed
            return;
        }

        var now = clock.UtcNow;
        var ws = Workspace.Create("Sunrise Bakery", Slug, "PHP", "Asia/Manila", now.AddDays(-10));
        ws.UpdateProfile("Sunrise Bakery", "PHP", "Asia/Manila", "63");
        var owner = User.Create(ws.Id, Email, "Demo Owner", Role.Owner, now.AddDays(-10));
        owner.SetPasswordHash(passwords.Hash(Password));
        var staff = User.Create(ws.Id, "staff@hatidsuki.local", "Counter Staff", Role.Staff, now.AddDays(-10));
        staff.SetPasswordHash(passwords.Hash(Password));
        db.Workspaces.Add(ws);
        db.Users.AddRange(owner, staff);
        await db.SaveChangesAsync(ct);

        // ---- the item list --------------------------------------------------------------------
        (string Name, decimal Price, string Category)[] menu =
        [
            ("Pandesal (6 pcs)", 60, "Bread"), ("Ensaymada", 45, "Bread"), ("Cheese Roll", 40, "Bread"), ("Banana Bread", 70, "Bread"),
            ("Ube Cake (slice)", 120, "Cakes"), ("Chocolate Cake (slice)", 110, "Cakes"),
            ("Chicken Sandwich", 95, "Savory"), ("Empanada", 55, "Savory"),
            ("Brewed Coffee", 80, "Drinks"), ("Iced Latte", 120, "Drinks")
        ];
        var items = menu.Select((m, i) => Item.Create(ws.Id, m.Name, m.Price, m.Category, null, null, i + 1, now.AddDays(-10))).ToList();
        db.Items.AddRange(items);
        var item = items.ToDictionary(i => i.Name);

        // ---- a published order form with QR sources ---------------------------------------------
        var form = Form.Create(ws.Id, "Lunch Orders", "demo2026", FormTemplates.Simple(), now.AddDays(-9));
        var version = form.Publish(now.AddDays(-9));
        db.Forms.Add(form);
        var sources = new[] { "Table 1", "Table 2", "Table 3", "Front window", "Instagram bio" }
            .Select((n, i) => FormSource.Create(ws.Id, form.Id, n, $"src{i + 1}x", now.AddDays(-9))).ToList();
        sources.ForEach(s => { for (var k = 0; k < 6 + s.Name.Length % 5; k++) s.RecordScan(); });
        db.FormSources.AddRange(sources);
        await db.SaveChangesAsync(ct);

        var def = FormDefinition.FromJson(version.DefinitionJson);
        string Answers(string who, string phone, string when, string? notes = null)
        {
            var list = new List<object>();
            foreach (var f in def.Fields)
            {
                string? v = f.Role switch { FieldRoles.CustomerName => who, FieldRoles.CustomerPhone => phone, FieldRoles.Notes => notes, _ => null };
                if (f.Label == "When do you need it?") v = when;
                if (v is not null) list.Add(new { fieldId = f.Id, label = f.Label, type = f.Type, values = new[] { v } });
            }
            return JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        PartDraft Person(string name, params (string item, int qty)[] lines) =>
            new(name, null, lines.Select(l => new LineDraft(item[l.item].Id, l.item, item[l.item].Price, l.qty, null)).ToList());

        async Task<Order> Place(DateTime at, string customer, string phone, string when, FormSource? source, params PartDraft[] parts)
        {
            var number = await db.NextOrderNumberAsync(ws.Id, ct);
            var o = Order.Place(ws.Id, form.Id, version.Id, number, "PHP", OrderChannel.Public, null, customer, phone, null,
                Answers(customer, phone, when), source?.Code, source?.Name ?? "Direct link", false, null, parts, at, "customer");
            db.Orders.Add(o);
            return o;
        }

        // ---- last week's finished orders (feed the dashboard) -----------------------------------
        var rng = new Random(42);
        string[] names = ["Liza", "Marco", "Nina", "Paolo", "Rita", "Sam", "Tess", "Vic", "Wendy", "Yna"];
        for (var day = 7; day >= 1; day--)
        {
            var count = 3 + rng.Next(0, 4) + (day is 2 or 1 ? 2 : 0);
            for (var n = 0; n < count; n++)
            {
                var at = now.Date.AddDays(-day).AddHours(1 + rng.Next(0, 9)).AddMinutes(rng.Next(0, 60)); // 9am–6pm Manila-ish
                var people = rng.Next(0, 4) == 0 ? rng.Next(2, 5) : 1;
                var parts = Enumerable.Range(0, people).Select(p => Person(people == 1 ? names[rng.Next(names.Length)] : $"Guest {p + 1}",
                    (menu[rng.Next(menu.Length)].Name, rng.Next(1, 3)), (menu[rng.Next(menu.Length)].Name, 1))).ToArray();
                var src = rng.Next(0, 3) == 0 ? sources[rng.Next(sources.Count)] : null;
                var o = await Place(at, names[rng.Next(names.Length)], "0917 55" + rng.Next(10000, 99999), "today", src, parts);
                o.Serve(at.AddMinutes(18), staff.Name);
                if (rng.Next(0, 9) != 0) o.TagAllPaid(true, at.AddMinutes(20), staff.Name);
            }
        }

        // ---- today: the board has something real on it -----------------------------------------
        // A five-person office order that nobody has opened yet — this is exactly the order that gets missed in chat.
        await Place(now.AddMinutes(-9), "Maria Santos", "0917 123 4567", "12:30pm", sources[0],
            Person("Ana", ("Ensaymada", 2), ("Iced Latte", 1)), Person("Ben", ("Chicken Sandwich", 1), ("Brewed Coffee", 1)),
            Person("Carlo", ("Banana Bread", 1)), Person("Dana", ("Cheese Roll", 2), ("Iced Latte", 1)), Person("Eli", ("Ube Cake (slice)", 1)));

        var partial = await Place(now.AddMinutes(-22), "Lito Reyes", "0918 765 4321", "12pm", sources[3],
            Person("Lito", ("Pandesal (6 pcs)", 1), ("Brewed Coffee", 1)), Person("Joy", ("Empanada", 2)), Person("Ken", ("Chocolate Cake (slice)", 1)));
        partial.Acknowledge(now.AddMinutes(-20));
        partial.SetPartReady(partial.Parts[0].Id, true, now.AddMinutes(-12), staff.Name);
        partial.SetPartReady(partial.Parts[1].Id, true, now.AddMinutes(-11), staff.Name);
        partial.TagPartPaid(partial.Parts[0].Id, true, now.AddMinutes(-19), staff.Name);

        var ready = await Place(now.AddMinutes(-31), "Joy Cruz", "0920 555 0101", "now", null, Person("Joy", ("Pandesal (6 pcs)", 2), ("Brewed Coffee", 1)));
        ready.Acknowledge(now.AddMinutes(-30));
        ready.MarkAllReady(now.AddMinutes(-6), staff.Name);

        var served = await Place(now.AddMinutes(-70), "Rico Tan", "0919 222 3333", "now", sources[1], Person("Rico", ("Chicken Sandwich", 1), ("Iced Latte", 1)));
        served.Serve(now.AddMinutes(-52), staff.Name); // served but cash not collected yet: shows on "cash to collect"

        await db.SaveChangesAsync(ct);
        await EnsureDeliveryAsync(db, ws, now, ct);
    }

    /// <summary>Gives the demo business its delivery locations and puts every demo order somewhere. Safe to repeat.</summary>
    private static async Task EnsureDeliveryAsync(AppDbContext db, Workspace ws, DateTime now, CancellationToken ct)
    {
        var locations = await db.DeliveryLocations.IgnoreQueryFilters().Where(l => l.WorkspaceId == ws.Id).OrderBy(l => l.SortOrder).ToListAsync(ct);
        if (locations.Count == 0)
        {
            (string Name, string? Note)[] places =
            [
                ("Front counter", "Pick up at the window. We call your number."),
                ("Building A lobby", "Ground floor, next to the guard"),
                ("Building B lobby", "Ground floor"),
                ("Rooftop terrace", "Take the lift to floor 8"),
                ("Parking area", "Add your plate number below")
            ];
            locations = places.Select((p, i) => DeliveryLocation.Create(ws.Id, p.Name, p.Note, i + 1, now)).ToList();
            db.DeliveryLocations.AddRange(locations);
            await db.SaveChangesAsync(ct);
        }

        var homeless = await db.Orders.IgnoreQueryFilters().Where(o => o.WorkspaceId == ws.Id && o.DeliveryLocationName == null).OrderBy(o => o.Number).ToListAsync(ct);
        string?[] notes = [null, "Room 402", null, "By the blue door", null, "3rd floor", null];
        for (var i = 0; i < homeless.Count; i++)
        {
            var place = locations[i % locations.Count];
            homeless[i].SetDelivery(place.Id, place.Name, place.Name.StartsWith("Building") ? notes[i % notes.Length] : null);
        }
        if (homeless.Count > 0) await db.SaveChangesAsync(ct);
    }
}
