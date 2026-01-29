using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Common;

/// <summary>
/// A binding wrapper that includes an IsChecked property for use in checkable controls.
/// </summary>
/// <typeparam name="T"></typeparam>
public partial class IsCheckedBindingWrapper<T> : BindingWrapper<T>
{
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public IsCheckedBindingWrapper() { }

    [JsonConstructor]
    [SetsRequiredMembers]
    public IsCheckedBindingWrapper(T value) : base(value) { }
}