using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Text.Json.Serialization;
using Avalonia.Reactive;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Utilities;
using MessagePack;
using ZLinq;

namespace Everywhere.I18N;

/// <summary>
/// MessagePack serializable base class for dynamic resource keys. Make them happy.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
[Union(0, typeof(DynamicResourceKey))]
[Union(1, typeof(DirectResourceKey))]
[Union(2, typeof(FormattedDynamicResourceKey))]
[Union(3, typeof(AggregateDynamicResourceKey))]
public abstract partial class DynamicResourceKeyBase : IObservable<object?>
{
    /// <summary>
    /// so why axaml DOES NOT SUPPORT {Binding .^} ???????
    /// </summary>
    [JsonIgnore]
    [IgnoreMember]
    public DynamicResourceKeyBase Self => this;

    public abstract IDisposable Subscribe(IObserver<object?> observer);

    public static implicit operator DynamicResourceKeyBase(string key) => new DirectResourceKey(key);
}

/// <summary>
/// This class is used to create a dynamic resource key for axaml Binding.
/// </summary>
/// <param name="key"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public class DynamicResourceKey(object? key) : DynamicResourceKeyBase, IRecipient<LocaleChangedMessage>
{
    [Key(0)]
    public object Key { get; } = key ?? string.Empty; // avoid null key (especially for MessagePack)

    // TODO: Check whether Memory leak occurs here
    [IgnoreMember] private readonly Dictionary<int, IObserver<object?>> _observers = new(1); // usually only one subscriber

    /// <summary>
    /// Subscribes an observer to receive updates when the locale changes.
    /// </summary>
    /// <remarks>
    /// The Avalonia's implementation of IObservable (GetResourceObservable) has issues which can cause memory leaks.
    /// It holds strong references to observers, preventing them from being garbage collected.
    /// This implementation uses weak references to avoid memory leaks.
    /// Also brings better performance by avoiding unnecessary resource lookups when there are no subscribers.
    /// </remarks>
    /// <param name="observer"></param>
    /// <returns></returns>
    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        // Only allow subscription on UI thread
        Dispatcher.UIThread.VerifyAccess();

        var id = _observers.Count;
        if (id == 0)
        {
            WeakReferenceMessenger.Default.Register(this); // register for locale change messages
        }

        while (_observers.ContainsKey(id)) id++; // ensure unique id
        
        _observers.Add(id, observer);
        observer.OnNext(ToString());

        return Disposable.Create(() =>
        {
            _observers.Remove(id);
            if (_observers.Count == 0)
            {
                WeakReferenceMessenger.Default.Unregister<LocaleChangedMessage>(this);
            }
        });
    }

    [return: NotNullIfNotNull(nameof(key))]
    public static implicit operator DynamicResourceKey?(string? key) => key == null ? null : new DynamicResourceKey(key);

    public static bool Exists(object key) => LocaleManager.Shared.TryGetResource(key, null, out _);

    public static bool TryResolve(object key, [NotNullWhen(true)] out string? result)
    {
        if (LocaleManager.Shared.TryGetResource(key, null, out var resource))
        {
            result = resource?.ToString() ?? string.Empty;
            return true;
        }

        result = null;
        return false;
    }

    public static string Resolve(object? key)
    {
        if (key is not null && LocaleManager.Shared.TryGetResource(key, null, out var resource))
        {
            return resource?.ToString() ?? string.Empty;
        }

        return key?.ToString() ?? string.Empty;
    }

    public void Receive(LocaleChangedMessage message)
    {
        foreach (var observer in _observers.Values.AsValueEnumerable())
        {
            observer.OnNext(ToString());
        }
    }

    public override string? ToString() => Resolve(Key);

    public override bool Equals(object? obj) => obj is DynamicResourceKey other && Equals(Key, other.Key);

    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>
/// Directly wraps a raw string for use in axaml.
/// This is useful for cases where you want to use a string as a resource key without any formatting or dynamic behavior.
/// </summary>
/// <param name="key"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class DirectResourceKey(object key) : DynamicResourceKey(key)
{
    public static DirectResourceKey Empty { get; } = new(string.Empty);

    private static readonly IDisposable NullDisposable = Disposable.Empty;

    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        observer.OnNext(Key);
        return NullDisposable;
    }

    /// <summary>
    /// For direct resource key, just return the key itself (even if it's null).
    /// </summary>
    /// <returns></returns>
    public override string? ToString() => Key.ToString();

    public override bool Equals(object? obj) => obj is DynamicResourceKey other && Equals(Key, other.Key);

    public override int GetHashCode() => Key.GetHashCode();

    public static implicit operator DirectResourceKey(string key) => new(key);
}

/// <summary>
/// This class is used to create a dynamic resource key for axaml Binding with formatted arguments.
/// It first resolves the resource key, then formats it with the provided arguments.
/// Arguments will be also resolved if they are dynamic resource keys.
/// </summary>
/// <param name="key"></param>
/// <param name="args"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class FormattedDynamicResourceKey(object key, params IReadOnlyList<DynamicResourceKeyBase> args) : DynamicResourceKey(key)
{
    [Key(1)]
    private IReadOnlyList<DynamicResourceKeyBase> Args { get; } = args;

    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        var formatter = new AnonymousObserver<object?>(_ => observer.OnNext(ToString()));
        var disposeCollector = new DisposeCollector();
        disposeCollector.Add(base.Subscribe(formatter));
        Args.ForEach(arg => disposeCollector.Add(arg.Subscribe(formatter)));
        return disposeCollector;
    }

    public override string ToString()
    {
        var resolvedKey = Resolve(Key);
        return string.IsNullOrEmpty(resolvedKey) ?
            string.Empty :
            string.Format(resolvedKey, Args.AsValueEnumerable().Select(object? (a) => a.ToString()).ToArray());
    }

    public override bool Equals(object? obj) => obj is FormattedDynamicResourceKey other &&
           Equals(Key, other.Key) &&
           Args.SequenceEqual(other.Args);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        foreach (var arg in Args) hash.Add(arg);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Aggregates multiple dynamic resource keys into one.
/// </summary>
/// <param name="keys"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class AggregateDynamicResourceKey(IReadOnlyList<DynamicResourceKeyBase> keys, string separator = ", ") : DynamicResourceKeyBase
{
    [Key(0)]
    private IReadOnlyList<DynamicResourceKeyBase> Keys { get; } = keys;

    [Key(1)]
    private string Separator { get; } = separator;

    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        var formatter = new AnonymousObserver<object?>(_ => observer.OnNext(ToString()));
        var disposeCollector = new DisposeCollector();
        Keys.OfType<DynamicResourceKey>().ForEach(key => disposeCollector.Add(key.Subscribe(formatter)));
        return disposeCollector;
    }

    public override string ToString()
    {
        if (Keys is not { Count: > 0 })
        {
            return string.Empty;
        }

        var resolvedKeys = new object?[Keys.Count];
        for (var i = 0; i < Keys.Count; i++)
        {
            if (Keys[i] is DynamicResourceKey dynamicKey) resolvedKeys[i] = dynamicKey.ToString();
            else resolvedKeys[i] = Keys[i];
        }

        return string.Join(Separator, resolvedKeys);
    }

    public override bool Equals(object? obj) => obj is AggregateDynamicResourceKey other &&
           Keys.SequenceEqual(other.Keys) &&
           Separator == other.Separator;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys) hash.Add(key);
        hash.Add(Separator);
        return hash.ToHashCode();
    }
}