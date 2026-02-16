using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Everywhere.Common;

namespace Everywhere.Configuration;

public interface ISettingsControl
{
    Control CreateControl(object owner);
}

/// <summary>
/// Represents a settings control associated with a specific type of control.
/// </summary>
/// <typeparam name="TControl"></typeparam>
public class SettingsControl<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl> : ISettingsControl
    where TControl : Control
{
    private readonly Func<object, TControl>? _factory;

    private TControl? _control;

    public SettingsControl() { }

    public SettingsControl(TControl control)
    {
        _control = control;
    }

    public SettingsControl(Func<object, TControl> factory)
    {
        _factory = factory;
    }

    public Control CreateControl(object owner)
    {
        if (_control is not null) return _control;
        if (_factory is not null) return _control = _factory(owner);
        return _control = ServiceLocator.Resolve<TControl>();
    }
}