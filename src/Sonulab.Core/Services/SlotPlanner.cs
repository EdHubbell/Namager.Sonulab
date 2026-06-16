namespace Sonulab.Core.Services;

public static class SlotPlanner
{
    public static int[] Move(int[] occupants, int from, int to)
    {
        var list = new List<int>(occupants);
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return list.ToArray();
    }

    public static (int Min, int Max) ChangedRange(int from, int to) =>
        from <= to ? (from, to) : (to, from);
}
