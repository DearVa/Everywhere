using Everywhere.Common;

namespace Everywhere.Interop;

public enum NativeMessageBoxResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
    Retry,
    Ignore
}

public enum NativeMessageBoxButtons
{
    None,
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
    RetryCancel,
    AbortRetryIgnore
}

public enum NativeMessageBoxIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
    Stop,
    Hand,
    Asterisk
}

public static partial class NativeMessageBox
{
    public static IExceptionHandler ExceptionHandler { get; } = new ExceptionHandlerImpl();

    private class ExceptionHandlerImpl : IExceptionHandler
    {
        public void HandleException(Exception exception, string? message = null, object? source = null, int lineNumber = 0)
        {
            Show(
                $"Error at [{source}:{lineNumber}]",
                $"{message ?? "An error occurred."}\n\n{exception.GetFriendlyMessage()}",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }
    }

    public static NativeMessageBoxResult Show(
        string title,
        string message,
        NativeMessageBoxButtons buttons = NativeMessageBoxButtons.Ok,
        NativeMessageBoxIcon icon = NativeMessageBoxIcon.None)
    {
#if WINDOWS
        return ShowWindowsMessageBox(title, message, buttons, icon);
#elif MACCATALYST
        return ShowMacCatalystMessageBox(title, message, buttons, icon);
#elif LINUX
        return ShowLinuxMessageBox(title, message, buttons);
#else
        #error Platform not supported
        throw new PlatformNotSupportedException();
#endif
    }

#if WINDOWS
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, int type);

    private enum Win32MessageBoxResult
    {
        None = 0,
        Ok = 1,
        Cancel = 2,
        Yes = 6,
        No = 7,
        Retry = 4,
        Ignore = 5
    }

    private enum Win32MessageBoxButtons
    {
        Ok = 0x00000000,
        OkCancel = 0x00000001,
        YesNo = 0x00000004,
        YesNoCancel = 0x00000003,
        RetryCancel = 0x00000005,
        AbortRetryIgnore = 0x00000002
    }

    private enum Win32MessageBoxIcon
    {
        None = 0x00000000,
        Information = 0x00000040,
        Warning = 0x00000030,
        Error = 0x00000010,
        Question = 0x00000020,
        Stop = Error,
        Hand = Error,
        Asterisk = Information
    }

    private static NativeMessageBoxResult ShowWindowsMessageBox(
        string title,
        string message,
        NativeMessageBoxButtons buttons,
        NativeMessageBoxIcon icon)
    {
        var buttonFlags = buttons switch
        {
            NativeMessageBoxButtons.Ok => (int)Win32MessageBoxButtons.Ok,
            NativeMessageBoxButtons.OkCancel => (int)Win32MessageBoxButtons.OkCancel,
            NativeMessageBoxButtons.YesNo => (int)Win32MessageBoxButtons.YesNo,
            NativeMessageBoxButtons.YesNoCancel => (int)Win32MessageBoxButtons.YesNoCancel,
            NativeMessageBoxButtons.RetryCancel => (int)Win32MessageBoxButtons.RetryCancel,
            NativeMessageBoxButtons.AbortRetryIgnore => (int)Win32MessageBoxButtons.AbortRetryIgnore,
            _ => 0
        };

        var iconFlags = icon switch
        {
            NativeMessageBoxIcon.Information => (int)Win32MessageBoxIcon.Information,
            NativeMessageBoxIcon.Warning => (int)Win32MessageBoxIcon.Warning,
            NativeMessageBoxIcon.Error => (int)Win32MessageBoxIcon.Error,
            NativeMessageBoxIcon.Question => (int)Win32MessageBoxIcon.Question,
            NativeMessageBoxIcon.Stop => (int)Win32MessageBoxIcon.Stop,
            NativeMessageBoxIcon.Hand => (int)Win32MessageBoxIcon.Hand,
            NativeMessageBoxIcon.Asterisk => (int)Win32MessageBoxIcon.Asterisk,
            _ => 0
        };

        var result = MessageBox(IntPtr.Zero, message, title, buttonFlags | iconFlags);
        return result switch
        {
            (int)Win32MessageBoxResult.Ok => NativeMessageBoxResult.Ok,
            (int)Win32MessageBoxResult.Cancel => NativeMessageBoxResult.Cancel,
            (int)Win32MessageBoxResult.Yes => NativeMessageBoxResult.Yes,
            (int)Win32MessageBoxResult.No => NativeMessageBoxResult.No,
            (int)Win32MessageBoxResult.Retry => NativeMessageBoxResult.Retry,
            (int)Win32MessageBoxResult.Ignore => NativeMessageBoxResult.Ignore,
            _ => NativeMessageBoxResult.None
        };
    }
#endif

#if MACCATALYST
    private static NativeMessageBoxResult ShowMacCatalystMessageBox(string title, string message, NativeMessageBoxButtons buttons)
    {
        // Implement Mac Catalyst specific message box logic here
        throw new NotImplementedException("Mac Catalyst message box not implemented.");
    }
#endif

#if LINUX
    private static NativeMessageBoxResult ShowLinuxMessageBox(string title, string message, NativeMessageBoxButtons buttons)
    {
        // Implement Linux specific message box logic here
        throw new NotImplementedException("Linux message box not implemented.");
    }
#endif
}