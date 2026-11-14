namespace DiffWeave.App.Tests;

/// <summary>Sample inputs shared by tests and screenshots.</summary>
public static class TestData
{
    public const string Base = """
        using System;
        using System.Collections.Generic;

        namespace Inventory;

        /// <summary>Keeps track of items in stock.</summary>
        public sealed class Stock
        {
            private readonly Dictionary<string, int> _items = new();

            public int Count => _items.Count;

            public void Add(string name, int quantity)
            {
                if (quantity <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }

                _items[name] = Get(name) + quantity;
            }

            public int Get(string name) => _items.TryGetValue(name, out int q) ? q : 0;

            public bool Remove(string name, int quantity)
            {
                int current = Get(name);
                if (current < quantity)
                {
                    return false;
                }

                _items[name] = current - quantity;
                return true;
            }

            public override string ToString() => $"{Count} items";
        }

        """;

    public const string Left = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Inventory;

        /// <summary>Keeps track of items in stock.</summary>
        public sealed class Stock
        {
            private readonly Dictionary<string, int> _items = new(StringComparer.OrdinalIgnoreCase);

            public int Count => _items.Count;

            public int Total => _items.Values.Sum();

            public void Add(string name, int quantity)
            {
                if (quantity <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
                }

                _items[name] = Get(name) + quantity;
            }

            public int Get(string name) => _items.TryGetValue(name, out int q) ? q : 0;

            public bool Remove(string name, int quantity)
            {
                int current = Get(name);
                if (current < quantity)
                {
                    return false;
                }

                _items[name] = current - quantity;
                return true;
            }

            public override string ToString() => $"{Count} items";
        }

        """;

    public const string Right = """
        using System;
        using System.Collections.Generic;

        namespace Inventory;

        /// <summary>Keeps track of items in stock.</summary>
        public sealed class Stock
        {
            private readonly Dictionary<string, int> _items = new(StringComparer.Ordinal);

            public int Count => _items.Count;

            public void Add(string name, int quantity)
            {
                if (quantity <= 0)
                {
                    throw new ArgumentException("Invalid quantity", nameof(quantity));
                }

                _items[name] = Get(name) + quantity;
            }

            public int Get(string name) => _items.TryGetValue(name, out int q) ? q : 0;

            public bool Remove(string name, int quantity)
            {
                int current = Get(name);
                if (current < quantity)
                {
                    return false;
                }

                if (current == quantity)
                {
                    return _items.Remove(name);
                }

                _items[name] = current - quantity;
                return true;
            }

            public override string ToString() => $"Stock with {Count} items";
        }

        """;

    /// <summary>Writes base/left/right into a directory and returns their paths.</summary>
    public static (string Base, string Left, string Right) WriteMergeInputs(string dir)
    {
        string b = Path.Combine(dir, "Stock.base.cs"), l = Path.Combine(dir, "Stock.local.cs"), r = Path.Combine(dir, "Stock.remote.cs");
        File.WriteAllText(b, Base);
        File.WriteAllText(l, Left);
        File.WriteAllText(r, Right);
        return (b, l, r);
    }
}
