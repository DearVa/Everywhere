using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Cloud;

public partial class SubscriptionDetails : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpgradePlan))]
    public partial SubscriptionPlan Plan { get; set; }

    public bool CanUpgradePlan => Plan < SubscriptionPlan.Pro;

    [ObservableProperty]
    public partial long PlanCredits { get; set; }

    [ObservableProperty]
    public partial long TotalPlanCredits { get; set; }

    [ObservableProperty]
    public partial long BonusCredits { get; set; }

    [ObservableProperty]
    public partial DateTime? PeriodStart { get; set; }

    [ObservableProperty]
    public partial DateTime? PeriodEnd { get; set; }

    [ObservableProperty]
    public partial SubscriptionStatus Status { get; set; }
}