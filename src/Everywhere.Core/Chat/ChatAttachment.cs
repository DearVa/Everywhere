using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using MessagePack;
using Serilog;

namespace Everywhere.Chat;

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(ChatVisualElementAttachment))]
[Union(1, typeof(ChatTextSelectionAttachment))]
[Union(2, typeof(ChatTextAttachment))]
[Union(3, typeof(ChatFileAttachment))]
public abstract partial class ChatAttachment(DynamicResourceKeyBase headerKey) : ObservableObject
{
    public abstract LucideIconKind Icon { get; }

    [Key(0)]
    public virtual DynamicResourceKeyBase HeaderKey => headerKey;

    /// <summary>
    /// Indicates whether the attachment is presently focused in the UI.
    /// </summary>
    [IgnoreMember]
    public bool IsPrimary { get; set; }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatVisualElementAttachment : ChatAttachment
{
    [Key(1)]
    public override LucideIconKind Icon { get; }

    /// <summary>
    /// The text representation of the visual element.
    /// </summary>
    [Key(2)]
    public string? Content { get; set; }

    /// <summary>
    /// Ignore this property during serialization because it should already be converted into prompts and shouldn't appear in history.
    /// </summary>
    [IgnoreMember]
    public ResilientReference<IVisualElement>? Element { get; }

    /// <summary>
    /// Indicates whether the visual element is valid.
    /// </summary>
    [IgnoreMember]
    public bool IsElementValid => Element?.Target is not null;

    [SerializationConstructor]
    protected ChatVisualElementAttachment(DynamicResourceKeyBase headerKey, LucideIconKind icon) : base(headerKey)
    {
        Icon = icon;
    }

    public ChatVisualElementAttachment(DynamicResourceKeyBase headerKey, LucideIconKind icon, IVisualElement? element) : base(headerKey)
    {
        Icon = icon;
        Element = element is null ? null : new ResilientReference<IVisualElement>(element);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatTextSelectionAttachment : ChatVisualElementAttachment
{
    /// <summary>
    /// Override to prevent serialization of HeaderKey.
    /// </summary>
    [IgnoreMember]
    public override DynamicResourceKeyBase HeaderKey => base.HeaderKey;

    [IgnoreMember]
    public override LucideIconKind Icon => LucideIconKind.TextCursorInput;

    [Key(0)]
    public string Text { get; }

    [SerializationConstructor]
    private ChatTextSelectionAttachment(string text) : base(CreateHeaderKey(text), LucideIconKind.TextSelect)
    {
        Text = text;
        IsPrimary = true;
    }

    public ChatTextSelectionAttachment(string text, IVisualElement? element) : base(
        CreateHeaderKey(text),
        LucideIconKind.TextSelect,
        element)
    {
        Text = text;
        IsPrimary = true;
    }

    /// <summary>
    /// Creates a header key based on the provided text. It trims the middle part of the text to 30 characters if it's too long.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static DirectResourceKey CreateHeaderKey(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new DirectResourceKey(string.Empty);
        }

        const int MaxLength = 30;
        const string MiddlePart = " ";

        var resultLength = text.Length <= MaxLength ? text.Length : MaxLength;

        var result = string.Create(resultLength, text, (span, state) =>
        {
            if (state.Length <= MaxLength)
            {
                for (var i = 0; i < state.Length; i++)
                {
                    var c = state[i];
                    span[i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }
            }
            else
            {
                var prefixLength = (MaxLength - MiddlePart.Length) / 2;
                var suffixLength = MaxLength - MiddlePart.Length - prefixLength;

                for (var i = 0; i < prefixLength; i++)
                {
                    var c = state[i];
                    span[i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }

                MiddlePart.AsSpan().CopyTo(span.Slice(prefixLength, MiddlePart.Length));

                var suffixStart = state.Length - suffixLength;
                var destStart = prefixLength + MiddlePart.Length;

                for (var i = 0; i < suffixLength; i++)
                {
                    var c = state[suffixStart + i];
                    span[destStart + i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }
            }
        });

        return new DirectResourceKey(result);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatTextAttachment(DynamicResourceKeyBase headerKey, string text) : ChatAttachment(headerKey)
{
    public override LucideIconKind Icon => LucideIconKind.TextInitial;

    [Key(1)]
    public string Text => text;
}

/// <summary>
/// Represents a file attachment in a chat message.
/// Supports image, video, audio, document, and plain file types.
/// </summary>
/// <param name="headerKey"></param>
/// <param name="filePath"></param>
/// <param name="sha256"></param>
/// <param name="mimeType"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatFileAttachment(
    DynamicResourceKeyBase headerKey,
    string filePath,
    string sha256,
    string mimeType,
    string? description = null
) : ChatAttachment(headerKey)
{
    public override LucideIconKind Icon => LucideIconKind.File;

    [Key(1)]
    public string FilePath
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            _isImageLoaded = false;
        }
    } = filePath;

    [Key(2)]
    public string Sha256 { get; } = sha256;

    [Key(3)]
    public string MimeType { get; } = FileUtilities.VerifyMimeType(mimeType);

    /// <summary>
    /// Extra description for the file attachment passed to the LLM.
    /// e.g. From clipboard, downloaded from URL, etc.
    /// </summary>
    [Key(4)]
    public string? Description { get; set; } = description;

    public bool IsImage => FileUtilities.IsOfCategory(MimeType, FileTypeCategory.Image);

    public Bitmap? Image
    {
        get
        {
            if (_isImageLoaded) return field;

            Task.Run(async () =>
            {
                try
                {
                    field = await LoadImageAsync();
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    Log.Logger.ForContext<ChatFileAttachment>().Error(ex, "Failed to load image from file: {FilePath}", FilePath);
                    field = null;
                }
                finally
                {
                    _isImageLoaded = true;
                    OnPropertyChanged();
                }
            });

            return field;

            async ValueTask<Bitmap?> LoadImageAsync()
            {
                const int maxWidth = 512;
                const int maxHeight = 512;

                if (!IsImage) return null;
                if (!File.Exists(FilePath)) return null;

                await using var stream = File.OpenRead(FilePath);
                var bitmap = Bitmap.DecodeToWidth(stream, maxWidth);
                return await ResizeImageOnDemandAsync(bitmap, maxWidth, maxHeight).ConfigureAwait(false);
            }
        }
    }

    [IgnoreMember]
    private bool _isImageLoaded;

    public override string ToString()
    {
        return $"{MimeType}: {Path.GetFileName(FilePath)}";
    }

    /// <summary>
    /// Creates a new ChatFileAttachment from a file path.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="mimeType">null for auto-detection</param>
    /// <param name="description"></param>
    /// <param name="maxBytesSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown if the file does not exist.
    /// </exception>
    /// <exception cref="OverflowException">
    /// Thrown if the file size exceeds the maximum allowed size.
    /// </exception>
    public static Task<ChatFileAttachment> CreateAsync(
        string filePath,
        string? mimeType = null,
        string? description = null,
        long maxBytesSize = 25L * 1024 * 1024,
        CancellationToken cancellationToken = default) => Task.Run(
        async () =>
        {
            await using var stream = File.OpenRead(filePath);
            if (stream.Length > maxBytesSize)
            {
                throw new HandledException(
                    new NotSupportedException($"File size exceeds the maximum allowed size of {maxBytesSize} bytes."),
                    new FormattedDynamicResourceKey(
                        LocaleKey.ChatFileAttachment_Create_FileTooLarge,
                        new DirectResourceKey(FileUtilities.HumanizeBytes(stream.Length)),
                        new DirectResourceKey(FileUtilities.HumanizeBytes(maxBytesSize))),
                    showDetails: false);
            }

            mimeType = await FileUtilities.EnsureMimeTypeAsync(mimeType, filePath, cancellationToken);

            var sha256 = await SHA256.HashDataAsync(stream, cancellationToken);
            var sha256String = Convert.ToHexString(sha256).ToLowerInvariant();
            return new ChatFileAttachment(new DirectResourceKey(Path.GetFileName(filePath)), filePath, sha256String, mimeType, description);
        },
        cancellationToken);

    private async static ValueTask<Bitmap> ResizeImageOnDemandAsync(Bitmap image, int maxWidth = 2560, int maxHeight = 2560)
    {
        if (image.PixelSize.Width <= maxWidth && image.PixelSize.Height <= maxHeight)
        {
            return image;
        }

        var scale = Math.Min(maxWidth / (double)image.PixelSize.Width, maxHeight / (double)image.PixelSize.Height);
        var newWidth = (int)(image.PixelSize.Width * scale);
        var newHeight = (int)(image.PixelSize.Height * scale);

        return await Task.Run(() => image.CreateScaledBitmap(new PixelSize(newWidth, newHeight)));
    }
}