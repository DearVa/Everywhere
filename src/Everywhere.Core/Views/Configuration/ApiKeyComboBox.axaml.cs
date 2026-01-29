using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Common;
using Everywhere.Configuration;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class ApiKeyComboBox : TemplatedControl, IDisposable
{
    /// <summary>
    /// Defines the <see cref="SelectedId"/> property.
    /// </summary>
    public static readonly StyledProperty<Guid> SelectedIdProperty =
        AvaloniaProperty.Register<ApiKeyComboBox, Guid>(nameof(SelectedId));

    /// <summary>
    /// Gets or sets the API key ID.
    /// </summary>
    public Guid SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="DefaultName"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> DefaultNameProperty =
        AvaloniaProperty.Register<ApiKeyComboBox, string?>(nameof(DefaultName));

    /// <summary>
    /// Gets or sets the default name for a new API key.
    /// </summary>
    public string? DefaultName
    {
        get => GetValue(DefaultNameProperty);
        set => SetValue(DefaultNameProperty, value);
    }

    public ReadOnlyObservableCollection<ApiKey> ItemsSource { get; }

    private readonly ObservableCollection<ApiKey> _itemsSource;
    private readonly IDisposable _subscription;

    public ApiKeyComboBox(ObservableCollection<ApiKey> itemsSource)
    {
        _itemsSource = itemsSource;

        var head = new SourceList<ApiKey>();
        head.Add(ApiKey.Empty);

        ItemsSource = head.Connect()
            .Or(_itemsSource.ToObservableChangeSet())
            .BindEx(out var bindSub);

        _subscription = new CompositeDisposable(bindSub, head);
    }

    [RelayCommand]
    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        var form = new CreateApiKeyForm(DefaultName);
        var result = await ServiceLocator.Resolve<DialogManager>()
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_AddApiKey)
            .WithPrimaryButton(
                LocaleResolver.Common_OK,
                (_, e) => e.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        var apiKey = form.ApiKey;
        _itemsSource.Add(apiKey);
        SelectedId = apiKey.Id;
    }

    [RelayCommand]
    private async Task ManageApiKeyAsync(CancellationToken cancellationToken)
    {
        var form = new ManageApiKeyForm(_itemsSource, DefaultName);
        await ServiceLocator.Resolve<DialogManager>()
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_ManageApiKey)
            .WithPrimaryButton(LocaleResolver.Common_OK)
            .ShowAsync(cancellationToken);
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}