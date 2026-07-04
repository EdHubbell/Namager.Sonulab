/// <summary>root\amp fake — thin front over FakeSlotBlobDevice (96 chunks, 12288 B).</summary>
public class FakeAmpDevice : FakeSlotBlobDevice
{
    public FakeAmpDevice() : base(@"root\amp", 96, 12288) { }
    public void SeedAmp(int index, string name, byte[] blob12288) => SeedSlot(index, name, blob12288);
}
