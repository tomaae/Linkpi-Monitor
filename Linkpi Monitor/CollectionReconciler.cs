using System.Collections.ObjectModel;

namespace Linkpi_Monitor;

internal static class CollectionReconciler
{
    public static void Update<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> values,
        Func<T, TKey> key, Action<T, T> update) where TKey : notnull
    {
        var existing = target.ToDictionary(key);
        for (var index = 0; index < values.Count; index++)
        {
            var incoming = values[index];
            if (existing.TryGetValue(key(incoming), out var item))
            {
                update(item, incoming);
                var currentIndex = target.IndexOf(item);
                if (currentIndex != index) target.Move(currentIndex, index);
            }
            else target.Insert(index, incoming);
        }
        while (target.Count > values.Count) target.RemoveAt(target.Count - 1);
    }
}
