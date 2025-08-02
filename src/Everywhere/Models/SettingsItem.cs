using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Models;

public class SettingsItemGroup(string name, IReadOnlyList<SettingsItemBase> items)
{
    public DynamicResourceKey HeaderKey => $"SettingsGroup_{name}_Header";
    public IReadOnlyList<SettingsItemBase> Items => items;
}

public abstract class SettingsItemBase(string name) : ObservableObject
{
    public DynamicResourceKey HeaderKey => $"Settings_{name}_Header";
    public DynamicResourceKey DescriptionKey => $"Settings_{name}_Description";
    public IValueProxy<bool>? IsEnabledProxy { get; set; }
    public List<SettingsItemBase> Items { get; } = [];
}

public interface IValueProxy<T>
{
    public T Value { get; set; }
}

public abstract class SettingsItemBase<T>(string name, IValueProxy<T> valueProxy) : SettingsItemBase(name)
{
    public IValueProxy<T> ValueProxy => valueProxy;
}

public class SettingsBooleanItem(
    string name,
    IValueProxy<bool?> valueProxy,
    bool canBeNull
) : SettingsItemBase<bool?>(name, valueProxy)
{
    public bool CanBeNull => canBeNull;
}

public class SettingsStringItem(
    string name,
    IValueProxy<string> valueProxy,
    string? watermark,
    int maxLength,
    bool isMultiline,
    bool isPassword
) : SettingsItemBase<string>(name, valueProxy)
{
    public string? Watermark => watermark;

    public int MaxLength => maxLength;

    public bool IsMultiline => isMultiline;

    public char PasswordChar => isPassword ? '*' : '\0';
}

public class SettingsIntegerItem(
    string name,
    IValueProxy<int> valueProxy,
    int minValue,
    int maxValue
) : SettingsItemBase<int>(name, valueProxy)
{
    public int MinValue => minValue;

    public int MaxValue => maxValue;
}

public class SettingsDoubleItem(
    string name,
    IValueProxy<double> valueProxy,
    double minValue,
    double maxValue,
    double step
) : SettingsItemBase<double>(name, valueProxy)
{
    public double MinValue => minValue;

    public double MaxValue => maxValue;

    public double Step => step;
}

public class SettingsSelectionItem(
    string name,
    IValueProxy<object?> valueProxy,
    Func<IEnumerable<DynamicResourceKeyWrapper<object?>>> itemsSource
) : SettingsItemBase<object?>(name, valueProxy)
{
    public IEnumerable<DynamicResourceKeyWrapper<object?>> ItemsSource => itemsSource();

    /// <summary>
    /// ComboBox.SelectedValue is not working correctly!
    /// </summary>
    public DynamicResourceKeyWrapper<object?> SelectedItem
    {
        get => ItemsSource.First(i => Equals(i.Value, ValueProxy.Value));
        set => ValueProxy.Value = value.Value;
    }

    public static SettingsSelectionItem FromEnum(Type enumType, string name, IValueProxy<object?> valueProxy)
    {
        if (!enumType.IsEnum)
        {
            throw new ArgumentException("Type is not an enum", nameof(enumType));
        }

        return new SettingsSelectionItem(
            name,
            valueProxy,
            () =>
            {
                return Enum.GetValues(enumType).Cast<object>().Select(x =>
                {
                    if (Enum.GetName(enumType, x) is { } enumName &&
                        enumType.GetField(enumName)?.GetCustomAttribute<DynamicResourceKeyAttribute>() is { } ppAttribute)
                    {
                        return new DynamicResourceKeyWrapper<object?>(ppAttribute.Key, x);
                    }

                    return new DynamicResourceKeyWrapper<object?>($"{enumType.Name}_{x}", x);
                });
            });
    }
}

public class SettingsKeyboardHotkeyItem(
    string name,
    IValueProxy<KeyboardHotkey> valueProxy
) : SettingsItemBase<KeyboardHotkey>(name, valueProxy);