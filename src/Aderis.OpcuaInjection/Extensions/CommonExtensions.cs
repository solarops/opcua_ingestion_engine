#nullable disable
namespace Aderis.OpcuaInjection.Extensions;

public static class CommonExtensions
{
    /// <summary>
    /// Works just like a Venn diagram
    /// </summary>
    public static Venn2<TValue1, TValue2> GetVennSet<TValue1, TValue2, TKey>(this IEnumerable<TValue1> myItems, IEnumerable<TValue2> otherItems, Func<TValue1, TKey> myKeyGetter, Func<TValue2, TKey> otherKeyGetter)
    {
        var myLookup = myItems.ToDictionary(myKeyGetter);
        var otherLookup = otherItems.ToDictionary(otherKeyGetter);

        //  Console.WriteLine(otherLookup);
        var venn = new Venn2<TValue1, TValue2>();
        foreach (var pair in myLookup.ToArray())
        {
            if (!otherLookup.TryGetValue(pair.Key, out var other))
                venn.OnlyInMyItems.Add(pair.Value);
            else
            {
                venn.InBoth.Add(new VennMatchingPair2<TValue1, TValue2>(pair.Value, other));
                otherLookup.Remove(pair.Key);
            }

            myLookup.Remove(pair.Key);
        }

        foreach (var pair in otherLookup)
            venn.OnlyInOtherItems.Add(pair.Value);

        return venn;

    }

    public class Venn2<TValue1, TValue2>
    {
        public List<TValue1> OnlyInMyItems = new();
        public List<TValue2> OnlyInOtherItems = new();
        public List<VennMatchingPair2<TValue1, TValue2>> InBoth = new();
    }

    public class VennMatchingPair2<TValue1, TValue2>(TValue1 myItem, TValue2 otherItem)
    {
        public TValue1 MyItem = myItem;
        public TValue2 OtherItem = otherItem;
    }
}

