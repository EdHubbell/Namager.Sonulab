using System.Net.Sockets;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceDisconnectedExceptionTests
{
    [Fact] public void Bare_message_names_the_transport()
    {
        var ex = new DeviceDisconnectedException("USB");
        Assert.Equal("Device disconnected (USB).", ex.Message);
        Assert.Equal("USB", ex.Transport);
        Assert.Null(ex.SlotNoun);
        Assert.False(ex.WasWriting);
    }

    [Fact] public void Derives_from_IOException_so_existing_catch_sites_still_fire()
    {
        // AmpListViewModel:429 and IrListViewModel:276 already catch IOException and show
        // ex.Message; they must keep working with no edit.
        Assert.IsAssignableFrom<System.IO.IOException>(new DeviceDisconnectedException("USB"));
    }

    [Fact] public void ForSlot_names_the_at_risk_slot_when_writing()
    {
        var ex = new DeviceDisconnectedException("USB").ForSlot("Amp", 12, writing: true);
        Assert.Equal(
            "Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.",
            ex.Message);
        Assert.Equal("Amp", ex.SlotNoun);
        Assert.Equal(12, ex.SlotIndex);
        Assert.True(ex.WasWriting);
    }

    [Fact] public void ForSlot_on_a_read_stays_bare()
    {
        // A dropped read damages nothing — do not scare the user about a slot that is fine.
        var ex = new DeviceDisconnectedException("WiFi").ForSlot("IR", 3, writing: false);
        Assert.Equal("Device disconnected (WiFi).", ex.Message);
        Assert.Equal(3, ex.SlotIndex);
    }

    [Fact] public void ForSlot_preserves_transport_and_the_whole_chain()
    {
        // ForSlot chains on the transport-level instance (which carries the throw site inside
        // SerialSonuLink), not on its inner — the raw exception stays reachable one hop further
        // down, so nothing is lost for log forensics.
        var raw = new System.IO.IOException("port gone");
        var fromTransport = new DeviceDisconnectedException("WiFi", raw);
        var ex = fromTransport.ForSlot("IR", 1, writing: true);

        Assert.Equal("WiFi", ex.Transport);
        Assert.Same(fromTransport, ex.InnerException);
        Assert.Same(raw, ex.InnerException!.InnerException);   // original still reachable
    }

    [Fact] public void Repeat_returns_a_distinct_instance_wrapping_the_original()
    {
        // SonuClient rethrows a copy so each throw carries its own stack trace instead of
        // overwriting the latched instance's.
        var first = new DeviceDisconnectedException("USB");
        var again = first.Repeat();
        Assert.NotSame(first, again);
        Assert.Same(first, again.InnerException);
        Assert.Equal(first.Message, again.Message);
    }

    [Theory]
    [InlineData(typeof(System.IO.IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void IsFatal_matches_link_death(Type t)
    {
        var ex = t == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("test")
            : (Exception)Activator.CreateInstance(t)!;
        Assert.True(DeviceDisconnectedException.IsFatal(ex));
    }

    [Fact] public void IsFatal_matches_SocketException()
        => Assert.True(DeviceDisconnectedException.IsFatal(new SocketException(10054)));

    [Fact] public void IsFatal_excludes_cancellation()
    {
        // A user/caller cancel is not a disconnect. Misclassifying it would wedge the UI
        // permanently on a routine cancel.
        Assert.False(DeviceDisconnectedException.IsFatal(new OperationCanceledException()));
        Assert.False(DeviceDisconnectedException.IsFatal(new TaskCanceledException()));
    }

    [Fact] public void IsFatal_excludes_timeout_and_already_classified()
    {
        Assert.False(DeviceDisconnectedException.IsFatal(new TimeoutException()));
        Assert.False(DeviceDisconnectedException.IsFatal(new DeviceDisconnectedException("USB")));
    }
}
