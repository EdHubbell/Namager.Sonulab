namespace Sonulab.Core.Services;

public static class SlotPlanner
{
    public static int[] Move(int[] occupants, int from, int to)
    {
        if (from < 0 || from >= occupants.Length) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= occupants.Length) throw new ArgumentOutOfRangeException(nameof(to));
        var list = new List<int>(occupants);
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return list.ToArray();
    }

    public static (int Min, int Max) ChangedRange(int from, int to) =>
        from <= to ? (from, to) : (to, from);
}
