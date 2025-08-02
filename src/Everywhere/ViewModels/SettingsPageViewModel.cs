using System.ComponentModel;
using System.Reflection;
using Avalonia.Threading;
using Everywhere.Attributes;
using Everywhere.Models;
using Everywhere.Utils;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

public class SettingsPageViewModel : ReactiveViewModelBase
{
    public IReadOnlyList<SettingsItemGroup> Groups { get; }

    private readonly DebounceHelper debounceHelper = new(TimeSpan.FromSeconds(1));

    public SettingsPageViewModel(Settings settings)
    {
        var itemMap = new Dictionary<string, SettingsItemBase>();
        Groups = typeof(Settings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .AsValueEnumerable()
            .Where(p => p.GetCustomAttribute<HiddenSettingsAttribute>() is null)
            .Where(p => p.PropertyType.IsAssignableTo(typeof(SettingsBase)))
            .Select(p =>
            {
                var group = p.GetValue(settings);
                return new SettingsItemGroup(
                    p.Name,
                    p.PropertyType
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .AsValueEnumerable()
                        .Where(pp => pp is { CanRead: true, CanWrite: true })
                        .Where(pp => pp.GetCustomAttribute<HiddenSettingsAttribute>() is null)
                        .Select(pp =>
                        {
                            var name = $"{p.Name}_{pp.Name}";
                            if (itemMap.ContainsKey(name))
                            {
                                throw new NotSupportedException(
                                    $"Property {name} is already registered");
                            }

                            SettingsItemBase? result = null;
                            SettingsItemBase? groupItem = null;

                            IValueProxy<bool>? isEnabledProxy = null;
                            if (pp.GetCustomAttribute<SettingsGroupAttribute>() is { } groupAttribute)
                            {
                                var groupProperty = p.PropertyType
                                    .GetProperty(
                                        groupAttribute.PropertyName,
                                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                                    .NotNull($"{groupAttribute.PropertyName} Property for Group is not found");

                                if (groupProperty.PropertyType != typeof(bool))
                                {
                                    throw new NotSupportedException(
                                        $"{groupAttribute.PropertyName} Property for Group has an invalid type");
                                }

                                if (!groupProperty.CanRead)
                                {
                                    throw new NotSupportedException(
                                        $"{groupAttribute.PropertyName} Property for Group is not readable");
                                }

                                itemMap.TryGetValue($"{p.Name}_{groupProperty.Name}", out groupItem);
                                isEnabledProxy = new SettingsValueProxy<bool>(group, groupProperty);
                            }

                            if (pp.GetCustomAttribute<SettingsSelectionItemAttribute>() is { } selectionAttribute)
                            {
                                var itemsSourceProperty = p.PropertyType
                                    .GetProperty(
                                        selectionAttribute.ItemsSource,
                                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                                    .NotNull($"{selectionAttribute.ItemsSource} Property for ItemsSource is not found");

                                if (!itemsSourceProperty.PropertyType.IsGenericType ||
                                    !itemsSourceProperty.PropertyType.GetGenericTypeDefinition().IsAssignableTo(typeof(IEnumerable<>)) ||
                                    itemsSourceProperty.PropertyType.GenericTypeArguments.Length != 1 ||
                                    itemsSourceProperty.PropertyType.GenericTypeArguments[0] != pp.PropertyType)
                                {
                                    throw new NotSupportedException(
                                        $"{selectionAttribute.ItemsSource} Property for ItemsSource has an invalid type");
                                }

                                if (itemsSourceProperty.GetMethod is not { } itemsSourceGetter)
                                {
                                    throw new NotSupportedException(
                                        $"{selectionAttribute.ItemsSource} Property for ItemsSource is not readable");
                                }

                                result = new SettingsSelectionItem(
                                    name,
                                    new SettingsValueProxy<object?>(group, pp),
                                    () =>
                                    {
                                        var itemsSource = itemsSourceGetter.IsStatic ?
                                            itemsSourceProperty.GetValue(null) :
                                            itemsSourceProperty.GetValue(group);
                                        return itemsSource.NotNull<IEnumerable>().Cast<object?>().Select(x =>
                                            new DynamicResourceKeyWrapper<object?>($"SettingsSelectionItem_{p.Name}_{pp.Name}_{x}", x));
                                    });
                            }
                            else
                            {
                                if (pp.PropertyType == typeof(bool))
                                {
                                    result = new SettingsBooleanItem(
                                        name,
                                        new SettingsValueProxy<bool?>(group, pp),
                                        false);
                                }

                                if (pp.PropertyType == typeof(bool?))
                                {
                                    result = new SettingsBooleanItem(
                                        name,
                                        new SettingsValueProxy<bool?>(group, pp),
                                        true);
                                }

                                if (pp.PropertyType == typeof(string))
                                {
                                    var attribute = pp.GetCustomAttribute<SettingsStringItemAttribute>();
                                    result = new SettingsStringItem(
                                        name,
                                        new SettingsValueProxy<string>(group, pp),
                                        attribute?.Watermark,
                                        attribute?.MaxLength ?? int.MaxValue,
                                        attribute?.IsMultiline ?? false,
                                        attribute?.IsPassword ?? false);
                                }

                                if (pp.PropertyType == typeof(int))
                                {
                                    var attribute = pp.GetCustomAttribute<SettingsIntegerItemAttribute>();
                                    result = new SettingsIntegerItem(
                                        name,
                                        new SettingsValueProxy<int>(group, pp),
                                        attribute?.Min ?? int.MinValue,
                                        attribute?.Max ?? int.MaxValue);
                                }

                                if (pp.PropertyType == typeof(double))
                                {
                                    var attribute = pp.GetCustomAttribute<SettingsDoubleItemAttribute>();
                                    result = new SettingsDoubleItem(
                                        name,
                                        new SettingsValueProxy<double>(group, pp),
                                        attribute?.Min ?? double.NegativeInfinity,
                                        attribute?.Max ?? double.PositiveInfinity,
                                        attribute?.Step ?? 0.1d);
                                }

                                // enums
                                if (pp.PropertyType == typeof(KeyboardHotkey))
                                {
                                    result = new SettingsKeyboardHotkeyItem(name, new SettingsValueProxy<KeyboardHotkey>(group, pp));
                                }
                                else if (pp.PropertyType.IsEnum)
                                {
                                    result = SettingsSelectionItem.FromEnum(pp.PropertyType, name, new SettingsValueProxy<object?>(group, pp));
                                }
                            }

                            if (result == null) return null;

                            result.IsEnabledProxy = isEnabledProxy;
                            itemMap[name] = result;

                            if (groupItem == null) return result;

                            groupItem.Items.Add(result);
                            return null;
                        })
                        .OfType<SettingsItemBase>()
                        .ToImmutableArray());
            }).ToImmutableArray();

        TrackableObject<SettingsBase>.AddPropertyChangedEventHandler((s, _) =>
        {
            if (s.GetType().GetCustomAttribute<HiddenSettingsAttribute>() is not null) return;
            debounceHelper.Execute(() => Dispatcher.UIThread.Invoke(() => ToastManager
                .CreateToast(LocaleKey.SettingsPage_Saved_Toast_Title.I18N())
                .OnBottomRight()
                .DismissOnClick()
                .WithDelay(1)
                .ShowSuccess()));
        });
    }

    private class SettingsValueProxy<T> : IValueProxy<T>, INotifyPropertyChanged
    {
        private readonly object? obj;
        private readonly PropertyInfo propertyInfo;

        public SettingsValueProxy(object? obj, PropertyInfo propertyInfo)
        {
            this.obj = obj;
            this.propertyInfo = propertyInfo;

            if (obj is INotifyPropertyChanged notifyPropertyChanged)
            {
                notifyPropertyChanged.PropertyChanged += HandleObjectPropertyChanged;
            }
        }

        ~SettingsValueProxy()
        {
            if (obj is INotifyPropertyChanged notifyPropertyChanged)
            {
                notifyPropertyChanged.PropertyChanged -= HandleObjectPropertyChanged;
            }
        }

        private void HandleObjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == propertyInfo.Name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public T Value
        {
            get => propertyInfo.GetValue(obj).To<T>()!;
            set
            {
                if (EqualityComparer<T>.Default.Equals(Value, value)) return;
                propertyInfo.SetValue(obj, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}