using System.Net.Sockets;

namespace Sonulab.Core.Transport;

/// <summary>The link to the pedal died mid-conversation (cable pulled, pedal powered off, socket
/// reset). Distinct from "this command failed": the transport has already closed its port and
/// SonuClient latches the first one, so nothing further will succeed on this link.
///
/// Derives from IOException deliberately — AmpListViewModel and IrListViewModel already
/// `catch (IOException ex)` and display ex.Message, so they report a disconnect correctly with no
/// edit. The trade-off: a catch written for FILE I/O at those sites will also catch a device drop.
/// Both of those sites already span a file read AND a device upload, so that is their intent.</summary>
public sealed class DeviceDisconnectedException : IOException
{
    /// <summary>"USB" or "WiFi".</summary>
    public string Transport { get; }

    /// <summary>User-facing noun of the slot list in play ("Amp", "IR"), when known.
    /// Supplied by SlotBlobService, which knows it as SlotBlobKind.Noun.</summary>
    public string? SlotNoun { get; }

    public int? SlotIndex { get; }

    /// <summary>True when the drop interrupted a WRITE burst, meaning the slot may be half-written
    /// and the rollback path is dead too. Only then is the slot named in the message.</summary>
    public bool WasWriting { get; }

    public DeviceDisconnectedException(string transport, Exception? inner = null,
        string? slotNoun = null, int? slotIndex = null, bool wasWriting = false)
        : base(Compose(transport, slotNoun, slotIndex, wasWriting), inner)
    {
        Transport = transport;
        SlotNoun = slotNoun;
        SlotIndex = slotIndex;
        WasWriting = wasWriting;
    }

    /// <summary>A copy carrying slot context. Callers that know which slot was in play attach it on
    /// the way out (SlotBlobService.UploadAsync).</summary>
    public DeviceDisconnectedException ForSlot(string noun, int index, bool writing) =>
        new(Transport, InnerException, noun, index, writing);

    /// <summary>A fresh instance wrapping this one, for SonuClient's latch. Rethrowing a single
    /// stored instance would overwrite its stack trace on every throw.</summary>
    public DeviceDisconnectedException Repeat() =>
        new(Transport, this, SlotNoun, SlotIndex, WasWriting);

    private static string Compose(string transport, string? noun, int? index, bool writing)
    {
        var s = $"Device disconnected ({transport}).";
        if (writing && noun is not null && index is not null)
            s += $" {noun} slot {index} may be partially written — verify it after reconnecting.";
        return s;
    }

    /// <summary>Does this exception mean the link is dead? Shared by SerialSonuLink and
    /// TcpSonuLink so there is ONE definition.
    ///
    /// Excludes cancellation (a routine user cancel must not wedge the session) and
    /// TimeoutException (transient — Read is only called after BytesToRead > 0, so it should not
    /// fire, and if it does it is not proof the device is gone). Already-classified exceptions
    /// pass through unwrapped.</summary>
    public static bool IsFatal(Exception ex) =>
        ex is not DeviceDisconnectedException
        && ex is not OperationCanceledException
        && ex is IOException or ObjectDisposedException or UnauthorizedAccessException
              or InvalidOperationException or SocketException;
}
