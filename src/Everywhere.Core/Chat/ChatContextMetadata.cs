using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>Chat context metadata persisted along with the object graph.</summary>
[MessagePackObject(AllowPrivate = true)]
public partial class ChatContextMetadata(Guid id, DateTimeOffset dateCreated, DateTimeOffset dateModified, string? topic) : ObservableObject
{
    /// <summary>
    /// Stable ID (Guid v7) to align with database primary key.
    /// </summary>
    [Key(0)]
    public Guid Id { get; } = id;

    [Key(1)]
    public DateTimeOffset DateCreated { get; } = dateCreated;

    [Key(2)]
    [field: IgnoreMember]
    public DateTimeOffset DateModified
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(LocalDateModified));
        }
    } = dateModified;

    [IgnoreMember]
    public DateTime LocalDateModified => DateModified.ToLocalTime().DateTime;

    [Key(3)]
    [field: IgnoreMember]
    public string? Topic
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(ActualTopic));
        }
    } = topic;

    [IgnoreMember]
    public string? ActualTopic
    {
        get
        {
            if (IsTemporary) return LocaleResolver.ChatContext_Temporary;
            if (string.IsNullOrWhiteSpace(Topic)) return LocaleResolver.ChatContext_Metadata_Topic_Default;
            return Topic;
        }
        set => Topic = value?.Trim();
    }

    /// <summary>
    /// Indicates whether the topic is being generated.
    /// </summary>
    [IgnoreMember]
    public bool IsGeneratingTopic { get; set; }

    [IgnoreMember]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualTopic))]
    public partial bool IsTemporary { get; set; }

    /// <summary>
    /// Used for selection in UI lists.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    public void StartRenaming() => IsRenaming = true;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Notify listeners that metadata has changed.
        WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(null, this, e.PropertyName));
    }

    public override bool Equals(object? obj) => obj is ChatContextMetadata other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}