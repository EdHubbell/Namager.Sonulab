/// <summary>root\ir fake — thin front over FakeSlotBlobDevice (32 chunks, 4096 B).</summary>
public class FakeIrDevice : FakeSlotBlobDevice
{
    public FakeIrDevice() : base(@"root\ir", 32, 4096) { }
    public void SeedIr(int index, string name, byte[] blob4096) => SeedSlot(index, name, blob4096);
}
