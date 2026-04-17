using System.Runtime.InteropServices;
using System.Text;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Linux.Interop.AtspiBackend.Native;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop.AtspiBackend.Elements;

public partial class AtspiElement
{
    public string? GetText(int maxLength = -1)
    {
        try
        {
            if (AtspiNative.atspi_accessible_is_text(_gobject.Handle) == 0)
            {
                return null;
            }

            // object character placeholders U+FFFC
            var childCount = AtspiNative.atspi_accessible_get_child_count(
                _gobject.Handle,
                IntPtr.Zero);

            if (childCount > 0)
            {
                // AT-SPI returns object chars for embedded objects, skip
                return null;
            }

            var charCount = AtspiNative.atspi_text_get_character_count(
                _gobject.Handle,
                IntPtr.Zero);

            var endOffset = maxLength == -1 ? charCount : Math.Min(maxLength, charCount);

            return AtspiNative.atspi_text_get_text(
                _gobject.Handle,
                0,
                endOffset,
                IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get text from element");
            return null;
        }
    }
    public string? GetSelectionText()
    {
        try
        {
            if (AtspiNative.atspi_accessible_is_text(_gobject.Handle) == 0)
            {
                return null;
            }

            var selectionCount = AtspiNative.atspi_text_get_n_selections(
                _gobject.Handle,
                IntPtr.Zero);

            if (selectionCount == 0)
            {
                return null;
            }

            var builder = new StringBuilder();

            for (var i = 0; i < selectionCount; i++)
            {
                var rangePtr = AtspiNative.atspi_text_get_selection(
                    _gobject.Handle,
                    i,
                    IntPtr.Zero);

                if (rangePtr == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var range = Marshal.PtrToStructure<AtspiNative.AtspiRange>(rangePtr);
                    var text = AtspiNative.atspi_text_get_text(
                        _gobject.Handle,
                        range.start,
                        range.end,
                        IntPtr.Zero);

                    if (!string.IsNullOrEmpty(text))
                    {
                        builder.Append(text);
                    }
                }
                finally
                {
                    // Range is wrapped in GObject, need to unref
                    var rangeObj = Core.GObjectWrapper.WrapOwned(rangePtr);
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get selection text");
            return null;
        }
    }
    public void Invoke()
    {
        try
        {
            if (AtspiNative.atspi_accessible_is_action(_gobject.Handle) == 0)
            {
                _logger.LogDebug("Element does not implement Action interface");
                return;
            }

            var actionCount = AtspiNative.atspi_action_get_n_actions(
                _gobject.Handle,
                IntPtr.Zero);

            if (actionCount == 0)
            {
                _logger.LogDebug("Element has no actions available");
                return;
            }

            // Invoke primary action
            var result = AtspiNative.atspi_action_do_action(
                _gobject.Handle,
                0,
                IntPtr.Zero);

            if (result == 0)
            {
                _logger.LogWarning("Failed to invoke action on element");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking element action");
        }
    }
    public void SetText(string text)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        try
        {
            // Check if read-only
            if (States.HasFlag(VisualElementStates.ReadOnly))
            {
                _logger.LogWarning("Cannot set text on read-only element");
                return;
            }

            // Check if editable
            if (AtspiNative.atspi_accessible_is_editable_text(_gobject.Handle) == 0)
            {
                _logger.LogWarning("Element does not implement EditableText interface");
                return;
            }

            // Marshal text to UTF-8
            var textPtr = Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                var result = AtspiNative.atspi_editable_text_set_text_contents(
                    _gobject.Handle,
                    textPtr,
                    IntPtr.Zero);

                if (result == 0)
                {
                    _logger.LogWarning("Failed to set text content");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(textPtr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting text content");
        }
    }
    public void SendShortcut(KeyboardShortcut shortcut)
    {

        try
        {
            // Must be a component to grab focus
            if (AtspiNative.atspi_accessible_is_component(_gobject.Handle) == 0)
            {
                _logger.LogWarning("Element is not a component, cannot grab focus");
                return;
            }

            // Grab focus
            var focusResult = AtspiNative.atspi_component_grab_focus(
                _gobject.Handle,
                IntPtr.Zero);

            if (focusResult == 0)
            {
                _logger.LogWarning("Failed to grab focus for shortcut");
                return;
            }
            _windowBackend.SendKeyboardShortcut(shortcut);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending keyboard shortcut");
        }
    }
}