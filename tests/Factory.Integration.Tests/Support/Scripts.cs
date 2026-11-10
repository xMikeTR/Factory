namespace Factory.Integration.Tests.Support;

public static class Scripts
{
    /// <summary>
    /// Marks a stub script runnable. The stubs are /bin/sh scripts, so there is nothing to mark on Windows;
    /// the guard is also what lets the build pass with -warnaserror (CA1416).
    /// </summary>
    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
