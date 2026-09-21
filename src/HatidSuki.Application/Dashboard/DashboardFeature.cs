using HatidSuki.Application.Common;
using HatidSuki.Application.Orders;
using HatidSuki.Domain;
using HatidSuki.Domain.Orders;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Application.Dashboard;

public record Kpis(int Orders, int People, decimal Sales, decimal Collected, decimal Outstanding, decimal AverageOrder, int Cancelled);
public record DayPoint(string Date, int Orders, decimal Sales);
public record TopItem(string Name, int Quantity, decimal Revenue);
public record SourceRow(string Source, int Orders, decimal Sales);
public record StatusRow(string Status, int Count);
public record HourCell(int DayOfWeek, int Hour, int Orders);

public record LocationRow(string Location, int Orders, int People);

public record DashboardDto(string From, string To, string Currency, string Timezone, Kpis Kpis, Kpis? Previous,
    List<DayPoint> Series, List<TopItem> TopItems, List<SourceRow> BySource, List<StatusRow> ByStatus, List<HourCell> Busiest,
    List<LocationRow> ByLocation);

/// <summary>Range is inclusive of both dates, in the business's timezone. Defaults to today.</summary>
public record GetDashboardQuery(DateOnly? From, DateOnly? To) : IRequest<DashboardDto>;

/// <remarks>
/// Metric definitions (docs/tasks/FS-028): cancelled orders/people and test orders never count toward sales;
/// "Collected" is cash tagged paid inside the range; "Outstanding" is all unpaid cash on non-cancelled orders.
/// </remarks>
public class GetDashboardHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<DashboardDto> Handle(GetDashboardQuery q, CancellationToken ct)
    {
        // Revenue figures are for owners and managers only.
        if (current.Role is not (Role.Owner or Role.Manager)) throw new ForbiddenException("Only owners and managers can see the dashboard.");

        var wsId = current.RequireWorkspaceId();
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == wsId, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ws.Timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, tz));
        var from = q.From ?? today; var to = q.To ?? from;
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 366) throw new DomainException("Choose a range of up to a year.");

        var (fromUtc, toUtc) = Bounds(from, to, tz);
        var days = to.DayNumber - from.DayNumber + 1;
        var prevFrom = from.AddDays(-days); var prevTo = from.AddDays(-1);
        var (prevFromUtc, _) = Bounds(prevFrom, prevTo, tz);

        // One read covers the range and the equal-length period before it, so tiles can show "vs previous".
        var orders = await db.Orders.AsNoTracking().WithParts()
            .Where(o => !o.IsTest && o.CreatedAtUtc >= prevFromUtc && o.CreatedAtUtc < toUtc).ToListAsync(ct);
        var current_ = orders.Where(o => o.CreatedAtUtc >= fromUtc).ToList();
        var previous = orders.Where(o => o.CreatedAtUtc < fromUtc).ToList();

        var unpaidOrders = await db.Orders.AsNoTracking().WithParts()
            .Where(o => !o.IsTest && o.Status != OrderStatus.Cancelled && o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc == null)).ToListAsync(ct);
        var outstanding = unpaidOrders.Sum(o => o.UnpaidAmount);

        // Cash collected is dated by when it was tagged paid, which can differ from when the order was placed.
        var paidInRange = await db.Orders.AsNoTracking().WithParts()
            .Where(o => !o.IsTest && o.Status != OrderStatus.Cancelled && o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc >= fromUtc && p.PaidAtUtc < toUtc))
            .ToListAsync(ct);
        var collected = paidInRange.Sum(o => o.ActiveParts.Where(p => p.PaidAtUtc >= fromUtc && p.PaidAtUtc < toUtc).Sum(p => p.Subtotal));

        var kpis = ComputeKpis(current_, collected, outstanding);
        var prevPaid = await db.Orders.AsNoTracking().WithParts()
            .Where(o => !o.IsTest && o.Status != OrderStatus.Cancelled && o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc >= prevFromUtc && p.PaidAtUtc < fromUtc))
            .ToListAsync(ct);
        var prevCollected = prevPaid.Sum(o => o.ActiveParts.Where(p => p.PaidAtUtc >= prevFromUtc && p.PaidAtUtc < fromUtc).Sum(p => p.Subtotal));
        var prevKpis = ComputeKpis(previous, prevCollected, outstanding);

        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
        var live = current_.Where(o => o.Status != OrderStatus.Cancelled).ToList();

        var series = Enumerable.Range(0, days).Select(d => from.AddDays(d)).Select(date =>
        {
            var day = live.Where(o => Local(o.CreatedAtUtc) == date).ToList();
            return new DayPoint(date.ToString("yyyy-MM-dd"), day.Count, day.Sum(o => o.Total));
        }).ToList();

        var top = live.SelectMany(o => o.ActiveParts).SelectMany(p => p.Lines).GroupBy(l => l.ItemName)
            .Select(g => new TopItem(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal)))
            .OrderByDescending(t => t.Quantity).ThenByDescending(t => t.Revenue).Take(8).ToList();

        var bySource = live.GroupBy(o => o.SourceName ?? "Direct link")
            .Select(g => new SourceRow(g.Key, g.Count(), g.Sum(o => o.Total))).OrderByDescending(s => s.Orders).ToList();

        var byStatus = current_.GroupBy(o => o.Status).Select(g => new StatusRow(g.Key.ToString(), g.Count())).ToList();

        var busiest = live.Select(o => TimeZoneInfo.ConvertTimeFromUtc(o.CreatedAtUtc, tz))
            .GroupBy(t => (t.DayOfWeek, t.Hour)).Select(g => new HourCell((int)g.Key.DayOfWeek, g.Key.Hour, g.Count())).ToList();

        var byLocation = live.GroupBy(o => o.DeliveryLocationName ?? "No location")
            .Select(g => new LocationRow(g.Key, g.Count(), g.Sum(o => o.ActiveCount))).OrderByDescending(l => l.Orders).ToList();

        return new DashboardDto(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), ws.Currency, ws.Timezone,
            kpis, prevKpis, series, top, bySource, byStatus, busiest, byLocation);
    }

    private static Kpis ComputeKpis(List<Order> inRange, decimal collected, decimal outstanding)
    {
        var live = inRange.Where(o => o.Status != OrderStatus.Cancelled).ToList();
        var sales = live.Sum(o => o.Total);
        return new Kpis(live.Count, live.Sum(o => o.ActiveCount), sales, collected, outstanding,
            live.Count == 0 ? 0 : decimal.Round(sales / live.Count, 2), inRange.Count(o => o.Status == OrderStatus.Cancelled));
    }

    private static (DateTime FromUtc, DateTime ToUtc) Bounds(DateOnly from, DateOnly to, TimeZoneInfo tz) =>
        (TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz),
         TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
}
