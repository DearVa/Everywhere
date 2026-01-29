using Everywhere.Utilities;
using System.Runtime.CompilerServices;

namespace Everywhere.Abstractions.Tests.Utilities;

/// <summary>
/// Tests for <see cref="ResilientReference{T}"/>.
/// </summary>
[TestFixture]
public class ResilientReferenceTests
{
    [Test]
    public void Constructor_ShouldInitializeCorrectly_WhenActive()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: true);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(reference.IsActive, Is.True);
        Assert.That(reference.Target, Is.SameAs(target));
    }

    [Test]
    public void Constructor_ShouldInitializeCorrectly_WhenInactive()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: false);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(reference.IsActive, Is.False);
        Assert.That(reference.Target, Is.SameAs(target));
    }

    [Test]
    public void Constructor_ShouldHandleNullTarget()
    {
        var reference = new ResilientReference<object>();
        Assert.That(reference.Target, Is.Null);
        
        reference = new ResilientReference<object>(isActive: false);
        Assert.That(reference.Target, Is.Null);
    }

    [Test]
    public void IsActive_SetToSameValue_ShouldDoNothing()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: true);

        using var _ = Assert.EnterMultipleScope();
        reference.IsActive = true;
        Assert.That(reference.IsActive, Is.True);
        Assert.That(reference.Target, Is.SameAs(target));

        reference.IsActive = false;
        reference.IsActive = false;
        Assert.That(reference.IsActive, Is.False);
        Assert.That(reference.Target, Is.SameAs(target));
    }

    [Test]
    public void SwitchingToInactive_ShouldKeepReferenceAlive_BeforeGC()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: true)
        {
            IsActive = false,
        };

        using var _ = Assert.EnterMultipleScope();
        Assert.That(reference.IsActive, Is.False);
        Assert.That(reference.Target, Is.SameAs(target));
    }

    [Test]
    [NonParallelizable]
    public void SwitchingToInactive_ShouldAllowCollection()
    {
        // Setup in a separate method to ensure variable scope is exited
        var (reference, originalWeakRef) = CreateWeakReference();

        // Trigger GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var _ = Assert.EnterMultipleScope();
        Assert.That(reference.IsActive, Is.False);
        Assert.That(reference.Target, Is.Null, "Target should have been collected");
        Assert.That(originalWeakRef.IsAlive, Is.False, "Original object should be collected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResilientReference<object>, WeakReference) CreateWeakReference()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: false);
        var weakRef = new WeakReference(target);
        return (reference, weakRef);
    }

    [Test]
    public void SwitchingToActive_ShouldResurrectStrongReference()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: false)
        {
            IsActive = true,
        };

        // Force GC to prove it's strongly held now
        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var _ = Assert.EnterMultipleScope();
        Assert.That(reference.IsActive, Is.True);
        Assert.That(reference.Target, Is.SameAs(target));
    }
    
    [Test]
    public void SwitchingToActive_ShouldHandleCollectedObject()
    {
        var (reference, _) = CreateWeakReference();
        
        // Trigger GC to collect the internal weak reference
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        // Now switch to active. Since target is gone, strong reference should become null
        reference.IsActive = true;
        
        Assert.That(reference.Target, Is.Null);
    }

    [Test]
    public void SetTarget_ShouldUpdateReference_WhenActive()
    {
        var reference = new ResilientReference<object>(new object(), isActive: true);
        var newTarget = new object();

        reference.Target = newTarget;

        Assert.That(reference.Target, Is.SameAs(newTarget));
    }

    [Test]
    public void SetTarget_ShouldUpdateReference_WhenInactive()
    {
        var reference = new ResilientReference<object>(new object(), isActive: false);
        var newTarget = new object();

        reference.Target = newTarget;

        Assert.That(reference.Target, Is.SameAs(newTarget));
    }
    
    [Test]
    public void SetTarget_Null_ShouldClearReference()
    {
        var reference = new ResilientReference<object>(new object(), isActive: true)
        {
            Target = null,
        };
        Assert.That(reference.Target, Is.Null);
        
        reference.IsActive = false;
        reference.Target = new object();
        reference.Target = null;
        Assert.That(reference.Target, Is.Null);
    }

    [Test]
    public void NullTarget_StateTransitions_ShouldNotThrow()
    {
        var reference = new ResilientReference<object>
        {
            IsActive = false,
        };

        Assert.That(reference.Target, Is.Null);
        
        reference.IsActive = true;
        Assert.That(reference.Target, Is.Null);
    }

    [Test]
    public void TryGetTarget_ShouldReturnTrueAndTarget_WhenAvailable()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target);

        var result = reference.TryGetTarget(out var retrieved);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(result, Is.True);
        Assert.That(retrieved, Is.SameAs(target));
    }

    [Test]
    public void TryGetTarget_ShouldReturnFalse_WhenCollected()
    {
        var (reference, _) = CreateWeakReference();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var result = reference.TryGetTarget(out var retrieved);

        using var _ = Assert.EnterMultipleScope();
        Assert.That(result, Is.False);
        Assert.That(retrieved, Is.Null);
    }

    [Test]
    public void Concurrency_StressTest()
    {
        var target = new object();
        var reference = new ResilientReference<object>(target, isActive: true);
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var exceptions = new List<Exception>();

        var threads = new List<Thread>();

        // Reader threads
        for (var i = 0; i < 5; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {

                        // ReSharper disable UnusedVariable
                        var t = reference.Target;
                        var isActive = reference.IsActive;
                        // ReSharper restore UnusedVariable
                        reference.TryGetTarget(out _);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));
        }

        // Writer threads (Toggling state)
        for (var i = 0; i < 2; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    var rng = new Random();
                    while (!token.IsCancellationRequested)
                    {
                        reference.IsActive = rng.Next(0, 2) == 0;
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));
        }
        
        // Writer threads (Setting target)
         threads.Add(new Thread(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        reference.Target = new object();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }));

        threads.ForEach(t => t.Start());

        Thread.Sleep(500); // Run for 500ms
        cts.Cancel();

        threads.ForEach(t => t.Join());

        Assert.That(exceptions, Is.Empty, "Exceptions occurred during concurrency test");
    }
}
