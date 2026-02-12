using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Cloud;

public partial class SubscriptionDetails : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpgradePlan))]
    public partial SubscriptionPlan Plan { get; set; }

    [ObservableProperty]
    public partial double UsedCredits { get; set; }

    [ObservableProperty]
    public partial double TotalCredits { get; set; }

    public bool CanUpgradePlan => Plan < SubscriptionPlan.Pro;
}