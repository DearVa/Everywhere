﻿using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;

namespace Everywhere.Utils;

public delegate void TrackableObjectPropertyChangedEventHandler<in TScope>(TScope sender, PropertyChangedEventArgs e);

public class TrackableObject<TScope> : ObservableObject where TScope : TrackableObject<TScope>
{
    private static readonly HashSet<TrackableObjectPropertyChangedEventHandler<TScope>> ScopeHandlers = [];

    public static IDisposable AddPropertyChangedEventHandler(TrackableObjectPropertyChangedEventHandler<TScope> handler)
    {
        lock (ScopeHandlers)
        {
            ScopeHandlers.Add(handler);
        }

        return new AnonymousDisposable(() =>
        {
            lock (ScopeHandlers)
            {
                ScopeHandlers.Remove(handler);
            }
        });
    }

    [JsonIgnore]
    [IgnoreMember]
    protected bool isTrackingEnabled;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        NotifyHandlers(e);
    }

    protected void NotifyHandlers(string propertyName)
    {
        if (!isTrackingEnabled) return;
        NotifyHandlers(new PropertyChangedEventArgs(propertyName));
    }

    protected void NotifyHandlers(PropertyChangedEventArgs e)
    {
        if (!isTrackingEnabled) return;

        lock (ScopeHandlers)
        {
            foreach (var handler in ScopeHandlers)
            {
                handler((TScope)this, e);
            }
        }
    }
}