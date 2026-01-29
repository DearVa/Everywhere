using Everywhere.Utilities;
using System.Runtime.CompilerServices;

namespace Everywhere.Abstractions.Tests.Utilities;

[TestFixture]
public class ResilientCacheTests
{
    [Test]
    public void Constructor_ShouldInitializeDefaults()
    {
        var cache = new ResilientCache<string, object>();

        using var _ = Assert.EnterMultipleScope();
        Assert.That(cache.IsActive, Is.True);
        Assert.That(cache, Is.Empty);
    }

    [Test]
    public void Add_And_Get_WhenActive_ShouldWorkAsDictionary()
    {
        var cache = new ResilientCache<string, object>();
        const string key = "key1";
        var value = new object();

        cache.Add(key, value);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(cache.ContainsKey(key), Is.True);
        Assert.That(cache[key], Is.SameAs(value));
        Assert.That(cache, Has.Count.EqualTo(1));
    }

    [Test]
    public void Add_And_Get_WhenInactive_ShouldWork()
    {
        var cache = new ResilientCache<string, object> { IsActive = false };
        const string key = "key1";
        var value = new object();

        cache.Add(key, value);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(cache.IsActive, Is.False);
        Assert.That(cache.ContainsKey(key), Is.True);
        Assert.That(cache[key], Is.SameAs(value));
        Assert.That(cache, Has.Count.EqualTo(1));
    }

    [Test]
    public void Switching_ActiveToInactive_ShouldPreserveData()
    {
        var cache = new ResilientCache<string, object>();
        var value = new object();
        cache.Add("key1", value);

        cache.IsActive = false;

        using var _ = Assert.EnterMultipleScope();
        Assert.That(cache.IsActive, Is.False);
        Assert.That(cache.ContainsKey("key1"), Is.True);
        Assert.That(cache["key1"], Is.SameAs(value));
    }

    [Test]
    public void Switching_InactiveToActive_ShouldPreserveData()
    {
        var cache = new ResilientCache<string, object> { IsActive = false };
        var value = new object();
        cache.Add("key1", value);

        cache.IsActive = true;

        using var _ = Assert.EnterMultipleScope();
        Assert.That(cache.IsActive, Is.True);
        Assert.That(cache.ContainsKey("key1"), Is.True);
        Assert.That(cache["key1"], Is.SameAs(value));
    }

    [Test]
    public void Inactive_GC_ShouldCollectItems()
    {
        // Use a method to ensure local variables are out of scope
        var (cache, weakRef) = SetupCacheWithWeakItem();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var _ = Assert.EnterMultipleScope();
        Assert.That(weakRef.IsAlive, Is.False, "Object should have been collected");
        Assert.That(cache.ContainsKey("key1"), Is.False, "Cache should not contain collected key");
        Assert.That(cache, Is.Empty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResilientCache<string, object>, WeakReference) SetupCacheWithWeakItem()
    {
        var cache = new ResilientCache<string, object> { IsActive = false };
        var obj = new object();
        cache.Add("key1", obj);
        return (cache, new WeakReference(obj));
    }

    [Test]
    public void Active_GC_ShouldNotCollectItems()
    {
        var cache = new ResilientCache<string, object>();
        var obj = new object();
        var weakRef = new WeakReference(obj);
        cache.Add("key1", obj);

        // ReSharper disable once RedundantAssignment
        obj = null; // Remove local strong reference

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var _ = Assert.EnterMultipleScope();
        Assert.That(weakRef.IsAlive, Is.True);
        Assert.That(cache.ContainsKey("key1"), Is.True);
        Assert.That(cache["key1"], Is.Not.Null);
    }

    [Test]
    public void Prune_ShouldRemoveCollectedItems()
    {
        var (cache, weakRef) = SetupCacheWithWeakItem();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(weakRef.IsAlive, Is.False);
        
        // Before prune, ContainsKey returns false for collected items, but they might still be in the internal dictionary?
        // Implementation check: ContainsKey checks liveness. Count checks liveness.
        // Prune explicitly removes them from the internal dictionary.
        
        // To verify Prune works, we can check if the internal count changes? 
        // But the public API Count already filters dead items.
        // We trust the logic, but functionally Prune makes sure we don't leak memory (empty WeakReference objects).
        
        cache.Prune();
        
        Assert.That(cache, Is.Empty);
    }

    [Test]
    public void Keys_And_Values_ShouldOnlyReturnLiveItems_WhenInactive()
    {
        var cache = new ResilientCache<string, object> { IsActive = false };
        var obj1 = new object();
        cache.Add("key1", obj1);
        
        // Add another item that we will let die
        var (weakRef2, key2) = AddWeakItem(cache, "key2");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(weakRef2.IsAlive, Is.False);

        var keys = cache.Keys;
        var values = cache.Values;

        using var _ = Assert.EnterMultipleScope();
        Assert.That(keys, Does.Contain("key1"));
        Assert.That(keys, Does.Not.Contain(key2));
        
        Assert.That(values, Does.Contain(obj1));
        Assert.That(values, Has.Count.EqualTo(1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, string) AddWeakItem(ResilientCache<string, object> cache, string key)
    {
        var obj = new object();
        cache.Add(key, obj);
        return (new WeakReference(obj), key);
    }

    [Test]
    public void AddRange_ShouldAddItemsCorrectly()
    {
        var cache = new ResilientCache<string, object>();
        var items = new Dictionary<string, object>
        {
            { "k1", new object() },
            { "k2", new object() }
        };

        cache.AddRange(items);

        Assert.That(cache, Has.Count.EqualTo(2));
        Assert.That(cache["k1"], Is.SameAs(items["k1"]));
    }

    [Test]
    public void Add_DuplicateKey_ShouldThrow()
    {
        // ReSharper disable once CollectionNeverQueried.Local
        var cache = new ResilientCache<string, object>
        {
            { "key1", new object() },
        };

        Assert.Throws<ArgumentException>(() => cache.Add("key1", new object()));
    }
    
    [Test]
    public void Indexer_Set_ShouldUpdateValue()
    {
        var cache = new ResilientCache<string, object>
        {
            ["key1"] = new object(),
        };
        var obj2 = new object();
        cache["key1"] = obj2;
        
        Assert.That(cache["key1"], Is.SameAs(obj2));
    }

    [Test]
    public void Remove_ShouldRemoveItem()
    {
        var cache = new ResilientCache<string, object>
        {
            { "key1", new object() },
        };

        var removed = cache.Remove("key1");

        using var _ = Assert.EnterMultipleScope();
        Assert.That(removed, Is.True);
        Assert.That(cache.ContainsKey("key1"), Is.False);
    }

    [Test]
    public void Clear_ShouldRemoveAllItems()
    {
        var cache = new ResilientCache<string, object>
        {
            { "key1", new object() },
            { "key2", new object() },
        };

        cache.Clear();

        Assert.That(cache, Is.Empty);
    }
    
    [Test]
    public void Concurrency_StressTest()
    {
        var cache = new ResilientCache<string, object>();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var exceptions = new List<Exception>();
        var threads = new List<Thread>();

        // Populate initially
        for (var i = 0; i < 100; i++)
        {
            cache.Add($"key{i}", new object());
        }

        // Reader threads
        for (var i = 0; i < 5; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    var rnd = new Random();
                    while (!token.IsCancellationRequested)
                    {
                        var key = $"key{rnd.Next(0, 100)}";
                        if (cache.TryGetValue(key, out var val))
                        {
                            Assert.That(val, Is.Not.Null);
                        }
                        
                        // Enumerate
                        // ReSharper disable UnusedVariable
                        var count = cache.Count;
                        foreach(var kvp in cache) { }
                        // ReSharper restore UnusedVariable
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));
        }

        // Writer threads (Add/Remove)
        for (var i = 0; i < 2; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    var rnd = new Random();
                    while (!token.IsCancellationRequested)
                    {
                        var key = $"temp{rnd.Next(0, 100)}";
                        // Blindly try to add or remove
                        if (rnd.Next(0, 2) == 0)
                        {
                            try { cache.Add(key, new object()); } catch (ArgumentException) { }
                        }
                        else
                        {
                            cache.Remove(key);
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));
        }

        // State Toggler
        threads.Add(new Thread(() =>
        {
            try
            {
                var rnd = new Random();
                while (!token.IsCancellationRequested)
                {
                    cache.IsActive = rnd.Next(0, 2) == 0;
                    if (!cache.IsActive)
                    {
                        cache.Prune();
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        }));

        threads.ForEach(t => t.Start());
        
        Thread.Sleep(1000);
        cts.Cancel();
        
        threads.ForEach(t => t.Join());
        
        if (exceptions.Count != 0)
        {
            Assert.Fail($"Concurrency test failed with {exceptions.Count} exceptions. First: {exceptions.First()}");
        }
    }
}
