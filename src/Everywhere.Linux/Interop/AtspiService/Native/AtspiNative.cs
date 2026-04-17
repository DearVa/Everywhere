using System.Runtime.InteropServices;
using GObj = GObject.Object;
using GObjHandle = GObject.Internal.ObjectHandle;

namespace Everywhere.Linux.Interop.AtspiBackend.Native;

public static partial class AtspiNative
{
    private const string LibAtspi = "libatspi.so.0";
    private const string LibGLib = "libglib-2.0.so.0";

    [LibraryImport(LibAtspi)]
    public static partial int atspi_init();

    [LibraryImport(LibAtspi)]
    public static partial void atspi_exit();

    [LibraryImport(LibAtspi)]
    public static partial void atspi_event_main();

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_get_desktop(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AtspiEventListenerCallback(IntPtr atspiEvent, IntPtr userData);

    [DllImport(LibAtspi, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr atspi_event_listener_new(
        AtspiEventListenerCallback callback,
        IntPtr userData,
        IntPtr destroyCallback);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_register(
        IntPtr listener,
        IntPtr eventType,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_deregister(
        IntPtr listener,
        IntPtr eventType,
        IntPtr error);

    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_accessible_get_name(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_role(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_state_set(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_parent(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_accessible_get_accessible_id(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_process_id(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_child_count(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_child_at_index(
        GObjHandle accessible,
        int index,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_index_in_parent(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_relation_set(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_n_targets(GObjHandle relation);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_relation_type(GObjHandle relation);

    /// <summary>Get target accessible at index.</summary>
    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_relation_get_target(GObjHandle relation, int index);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_state_set_contains(GObjHandle stateSet, int state);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_application(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_component(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_text(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_editable_text(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_action(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_component_get_extents(
        GObjHandle component,
        int coordType,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_get_layer(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial short atspi_component_get_mdi_z_order(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_grab_focus(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_character_count(GObjHandle text, IntPtr error);

    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_text_get_text(
        GObjHandle text,
        int startOffset,
        int endOffset,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_n_selections(GObjHandle text, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_text_get_selection(
        GObjHandle text,
        int selectionNum,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_editable_text_set_text_contents(
        GObjHandle editableText,
        IntPtr newContents,
        IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_get_n_actions(GObjHandle action, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_do_action(GObjHandle action, int index, IntPtr error);

    [LibraryImport(LibGLib)]
    public static partial void g_free(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    public struct AtspiRect
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AtspiEvent
    {
        public IntPtr type;
        public IntPtr source;
        public int detail1;
        public int detail2;
        public IntPtr anyValueType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] anyValueData;
        public IntPtr sender;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AtspiRange
    {
        public int start;
        public int end;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeGArray
    {
        public IntPtr Data;
        public uint Length;
    }
}