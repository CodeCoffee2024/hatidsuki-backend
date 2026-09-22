using FluentAssertions;
using HatidSuki.Domain.Identity;

namespace HatidSuki.Domain.Tests;

public class WorkspaceTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Workspace NewWorkspace() => Workspace.Create("Ana's Bakery", "anas-bakery", "USD", "UTC", Now);

    [Fact]
    public void A_new_workspace_is_not_suspended()
    {
        NewWorkspace().IsSuspended.Should().BeFalse();
    }

    [Fact]
    public void Suspending_records_when_and_why()
    {
        var ws = NewWorkspace();
        ws.Suspend("unpaid invoice", Now);

        ws.IsSuspended.Should().BeTrue();
        ws.SuspendedAtUtc.Should().Be(Now);
        ws.SuspendedReason.Should().Be("unpaid invoice");
    }

    [Fact]
    public void A_blank_reason_is_kept_as_no_reason()
    {
        var ws = NewWorkspace();
        ws.Suspend("   ", Now);
        ws.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public void An_overly_long_reason_is_capped()
    {
        var ws = NewWorkspace();
        ws.Suspend(new string('x', 500), Now);
        ws.SuspendedReason!.Length.Should().Be(300);
    }

    [Fact]
    public void Reactivating_clears_every_suspension_field()
    {
        var ws = NewWorkspace();
        ws.Suspend("abuse", Now);
        ws.Reactivate();

        ws.IsSuspended.Should().BeFalse();
        ws.SuspendedAtUtc.Should().BeNull();
        ws.SuspendedReason.Should().BeNull();
    }
}
