using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.ViewModels;

public interface INavigationItem
{
    DynamicResourceKeyBase TitleKey { get; }

    object Content { get; }
}

public record NavigationItem(DynamicResourceKeyBase TitleKey, object Content) : INavigationItem;

public sealed partial class MainViewModel : ReactiveViewModelBase, IDisposable
{
    public ReadOnlyObservableCollection<NavigationBarItem> Pages { get; }

    public NavigationBarItem? SelectedPage
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (value is not null) Navigate(new ShadNavigationItem(value));
        }
    }

    public ReadOnlyObservableCollection<INavigationItem> NavigationItems { get; }

    [ObservableProperty] public partial INavigationItem? CurrentNavigationItem { get; private set; }

    /// <summary>
    /// Use public property for MVVM binding
    /// </summary>
    public PersistentState PersistentState { get; }

    private readonly SourceList<NavigationBarItem> _pagesSource = new();
    private readonly SourceList<INavigationItem> _navigationItemsSource = new();
    private readonly CompositeDisposable _disposables = new(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;

    public MainViewModel(IServiceProvider serviceProvider, Settings settings, PersistentState persistentState)
    {
        PersistentState = persistentState;
        _serviceProvider = serviceProvider;
        _settings = settings;

        Pages = _pagesSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);

        NavigationItems = _navigationItemsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);
    }

    public void Navigate(INavigationItem navigationItem)
    {
        _navigationItemsSource.Edit(items =>
        {
            if (!items.Contains(navigationItem))
            {
                items.Add(navigationItem);
                if (items.Count > 10)
                {
                    items.RemoveAt(0);
                }
            }

            CurrentNavigationItem ??= navigationItem;
        });
    }

    protected internal override Task ViewLoaded(CancellationToken cancellationToken)
    {
        if (_pagesSource.Count > 0) return base.ViewLoaded(cancellationToken);

        _pagesSource.AddRange(
            _serviceProvider
                .GetServices<IMainViewPageFactory>()
                .SelectMany(f => f.CreatePages())
                .Concat(_serviceProvider.GetServices<IMainViewPage>())
                .OrderBy(p => p.Index)
                .Select(p => new NavigationBarItem
                {
                    Content = p.Title.ToTextBlock(),
                    Route = p,
                    Icon = new LucideIcon { Kind = p.Icon, Size = 20 },
                    [!NavigationBarItem.ToolTipProperty] = p.Title.ToBinding()
                }));
        SelectedPage = _pagesSource.Items.FirstOrDefault();

        ShowOobeDialogOnDemand();

        return base.ViewLoaded(cancellationToken);
    }

    /// <summary>
    /// Shows the OOBE dialog if the application is launched for the first time or after an update.
    /// </summary>
    private void ShowOobeDialogOnDemand()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (!Version.TryParse(PersistentState.PreviousLaunchVersion, out var previousLaunchVersion)) previousLaunchVersion = null;
        if (_settings.Model.CustomAssistants.Count == 0)
        {
            DialogManager
                .CreateCustomDialog(ServiceLocator.Resolve<WelcomeView>())
                .ShowAsync();
        }
        else if (previousLaunchVersion != version)
        {
            DialogManager
                .CreateCustomDialog(ServiceLocator.Resolve<ChangeLogView>())
                .Dismissible()
                .ShowAsync();
        }

        PersistentState.PreviousLaunchVersion = version?.ToString();
    }

    protected internal override Task ViewUnloaded()
    {
        ShowHideToTrayNotificationOnDemand();

        return base.ViewUnloaded();
    }

    private void ShowHideToTrayNotificationOnDemand()
    {
        if (PersistentState.IsHideToTrayIconNotificationShown) return;

        ServiceLocator.Resolve<INativeHelper>().ShowDesktopNotificationAsync(LocaleResolver.MainView_EverywhereHasMinimizedToTray);
        PersistentState.IsHideToTrayIconNotificationShown = true;
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    /// <summary>
    /// A navigation item that wraps a ShadUI NavigationBarItem.
    /// </summary>
    private sealed class ShadNavigationItem : INavigationItem
    {
        public DynamicResourceKeyBase TitleKey { get; }

        public object Content { get; }

        private readonly NavigationBarItem _navigationBarItem;

        public ShadNavigationItem(NavigationBarItem navigationBarItem)
        {
            _navigationBarItem = navigationBarItem;
            var mainViewPage = navigationBarItem.Route.NotNull<IMainViewPage>();
            TitleKey = mainViewPage.Title;
            Content = mainViewPage;
        }

        public override bool Equals(object? obj) =>
            obj is ShadNavigationItem other && other._navigationBarItem == _navigationBarItem ||
            obj is NavigationBarItem item && item == _navigationBarItem;

        public override int GetHashCode() => _navigationBarItem.GetHashCode();
    }
}