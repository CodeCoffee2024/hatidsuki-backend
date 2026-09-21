using FluentAssertions;
using HatidSuki.Domain;
using HatidSuki.Domain.Orders;

namespace HatidSuki.Domain.Tests;

public class OrderTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Burger = Guid.NewGuid();
    private static readonly Guid Salad = Guid.NewGuid();

    private static PartDraft Person(string name, params (Guid item, string itemName, decimal price, int qty)[] lines) =>
        new(name, null, lines.Select(l => new LineDraft(l.item, l.itemName, l.price, l.qty, null)).ToList());

    /// <summary>The scenario the product exists for: one message, several people's orders.</summary>
    private static Order GroupOrder() => Order.Place(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), number: 1, "USD", OrderChannel.Public, null,
        "Organizer", "0917", null, "{}", null, null, false, null,
        [
            Person("Ana", (Burger, "Burger", 8m, 2)),
            Person("Ben", (Salad, "Salad", 6.5m, 1)),
            Person("Carlo", (Burger, "Burger", 8m, 1), (Salad, "Salad", 6.5m, 1))
        ],
        Now, "customer");

    [Fact]
    public void Total_is_the_sum_of_every_persons_lines()
    {
        var order = GroupOrder();
        order.Total.Should().Be(16m + 6.5m + 14.5m);
        order.Parts.Should().HaveCount(3);
        order.Status.Should().Be(OrderStatus.New);
    }

    [Fact]
    public void Order_becomes_ready_only_when_every_person_is_checked()
    {
        var order = GroupOrder();
        order.SetPartReady(order.Parts[0].Id, true, Now, "staff");
        order.SetPartReady(order.Parts[1].Id, true, Now, "staff");
        order.Status.Should().Be(OrderStatus.New, "Carlo's food isn't ready yet");
        order.ReadyCount.Should().Be(2);

        order.SetPartReady(order.Parts[2].Id, true, Now, "staff");
        order.Status.Should().Be(OrderStatus.Ready);
    }

    [Fact]
    public void Unchecking_a_person_takes_a_ready_order_back_to_new()
    {
        var order = GroupOrder();
        order.MarkAllReady(Now, "staff");
        order.Status.Should().Be(OrderStatus.Ready);

        order.SetPartReady(order.Parts[1].Id, false, Now, "staff");
        order.Status.Should().Be(OrderStatus.New);
    }

    [Fact]
    public void Payment_status_follows_per_person_cash_tags()
    {
        var order = GroupOrder();
        order.PaymentStatus.Should().Be(PaymentStatus.Unpaid);

        order.TagPartPaid(order.Parts[0].Id, true, Now, "staff");
        order.PaymentStatus.Should().Be(PaymentStatus.PartlyPaid);
        order.UnpaidAmount.Should().Be(6.5m + 14.5m);

        order.TagAllPaid(true, Now, "staff");
        order.PaymentStatus.Should().Be(PaymentStatus.Paid);
        order.UnpaidAmount.Should().Be(0m);

        order.TagPartPaid(order.Parts[2].Id, false, Now, "staff");
        order.PaymentStatus.Should().Be(PaymentStatus.PartlyPaid);
    }

    [Fact]
    public void Payment_and_status_are_independent()
    {
        var order = GroupOrder();
        order.TagAllPaid(true, Now, "staff");
        order.Status.Should().Be(OrderStatus.New, "paid does not mean prepared");

        order.TagAllPaid(false, Now, "staff");
        order.Serve(Now, "staff");
        order.PaymentStatus.Should().Be(PaymentStatus.Unpaid, "served but cash not collected yet");
    }

    [Fact]
    public void Adding_a_late_person_reopens_a_ready_order_and_raises_attention_again()
    {
        var order = GroupOrder();
        order.Acknowledge(Now);
        order.MarkAllReady(Now, "staff");

        var late = order.AddPart(Person("Dana", (Salad, "Salad", 6.5m, 1)), Now, "staff");

        late.IsLateAddition.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.New, "the new person isn't prepared yet");
        order.AcknowledgedAtUtc.Should().BeNull("a late addition must be noticed again");
        order.Total.Should().Be(16m + 6.5m + 14.5m + 6.5m);
    }

    [Fact]
    public void Cancelling_one_person_updates_the_total_and_can_complete_the_order()
    {
        var order = GroupOrder();
        order.SetPartReady(order.Parts[0].Id, true, Now, "staff");
        order.SetPartReady(order.Parts[1].Id, true, Now, "staff");

        order.CancelPart(order.Parts[2].Id, "changed their mind", Now, "staff");

        order.Total.Should().Be(16m + 6.5m);
        order.ActiveCount.Should().Be(2);
        order.Status.Should().Be(OrderStatus.Ready, "the only remaining people are already ready");
    }

    [Fact]
    public void Cancelling_everyone_cancels_the_order()
    {
        var order = GroupOrder();
        foreach (var p in order.Parts.ToList()) order.CancelPart(p.Id, "no longer needed", Now, "staff");
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Served_orders_are_final()
    {
        var order = GroupOrder();
        order.Serve(Now, "staff");

        order.Status.Should().Be(OrderStatus.Served);
        order.Parts.Should().OnlyContain(p => p.IsReady, "serving checks everyone off");
        FluentActions.Invoking(() => order.Cancel("oops", Now, "staff")).Should().Throw<DomainException>();
        FluentActions.Invoking(() => order.SetPartReady(order.Parts[0].Id, false, Now, "staff")).Should().Throw<DomainException>();
    }

    [Fact]
    public void Single_person_order_is_just_one_part()
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, "USD", OrderChannel.Public, null,
            "Solo", null, "a@b.co", "{}", null, null, false, null, [Person("Solo", (Burger, "Burger", 8m, 1))], Now, null);

        order.Parts.Should().HaveCount(1);
        order.SetPartReady(order.Parts[0].Id, true, Now, "staff");
        order.Status.Should().Be(OrderStatus.Ready);
    }

    [Fact]
    public void Line_price_is_snapshotted_on_the_order()
    {
        var order = GroupOrder();
        order.Parts[0].Lines[0].UnitPrice.Should().Be(8m);
        order.Parts[0].Lines[0].ItemName.Should().Be("Burger");
    }

    [Fact]
    public void Invalid_quantities_and_empty_people_are_rejected()
    {
        FluentActions.Invoking(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, "USD", OrderChannel.Public, null,
            "X", null, null, "{}", null, null, false, null, [Person("A", (Burger, "Burger", 8m, 0))], Now, null))
            .Should().Throw<DomainException>();

        FluentActions.Invoking(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, "USD", OrderChannel.Public, null,
            "X", null, null, "{}", null, null, false, null, [new PartDraft("A", null, [])], Now, null))
            .Should().Throw<DomainException>();

        FluentActions.Invoking(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5, "USD", OrderChannel.Public, null,
            "X", null, null, "{}", null, null, false, null, [], Now, null))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Staff_entered_orders_start_acknowledged_but_public_ones_do_not()
    {
        var staff = Order.Place(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 6, "USD", OrderChannel.Staff, "Phone",
            "X", null, null, "{}", null, null, false, null, [Person("A", (Burger, "Burger", 8m, 1))], Now, "staff");
        staff.AcknowledgedAtUtc.Should().NotBeNull();
        GroupOrder().AcknowledgedAtUtc.Should().BeNull("a new public order must be noticed by someone");
    }
}
