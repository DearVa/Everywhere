using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;

namespace Everywhere.Common;

public partial class ObservableKeyValuePair<TKey, TValue> : ObservableObject, IKeyValuePair
{
    [ObservableProperty] public required partial TKey Key { get; set; }

    [ObservableProperty] public required partial TValue Value { get; set; }

    object? IKeyValuePair.Key => Key;
    object? IKeyValuePair.Value => Value;

    public ObservableKeyValuePair() { }

    [SetsRequiredMembers]
    public ObservableKeyValuePair(TKey key, TValue value)
    {
        Key = key;
        Value = value;
    }
}