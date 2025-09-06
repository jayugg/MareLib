using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace MareLib;

/// <summary>
/// Provides method to load all supported binaries for current platform.
/// https://github.com/maltiez2/VintageStory_ImGui/blob/master/VSImGui/source/Utils/NativesLoader.cs
/// </summary>
internal static class NativesLoader
{
    /// <summary>
    /// Ensures that FreeTypeSharp will use our bundled freetype library on Mac
    /// </summary>
    /// <param name="modFolder">Path to mod folder</param>
    /// <remarks>Must be called before any FreeTypeSharp method is used</remarks>
    private static bool _set;
    private static void EnsureResolver(string modFolder)
    {
        if (_set) return;
        NativeLibrary.SetDllImportResolver(
            typeof(FreeTypeSharp.FreeTypeLibrary).Assembly,
            (name, asm, path) =>
            {
                if (name is "freetype" or "libfreetype" or "libfreetype.6")
                {
                    var p1 = Path.Combine(modFolder, "native", "mac", "freetype.dylib");
                    var p2 = Path.Combine(modFolder, "native", "mac", "libfreetype.dylib");
                    if (File.Exists(p1) && NativeLibrary.TryLoad(p1, out IntPtr h)) return h;
                    if (File.Exists(p2) && NativeLibrary.TryLoad(p2, out IntPtr h2)) return h2;
                }
                return IntPtr.Zero;
            });
        _set = true;
    }
    
    /// <summary>
    /// Loads all supported binaries for current platform
    /// </summary>
    /// <param name="logger">To report errors</param>
    /// <param name="mod">To get path to folder with natives</param>
    /// <returns></returns>
    public static bool Load(ILogger logger, ModSystem mod)
    {
        EnsureResolver(((ModContainer)mod.Mod).FolderPath);
        DllLoader loader = DllLoader.Loader();
        foreach (string library in _nativeLibraries)
        {
            if (!loader.Load(library, logger, mod.Mod)) return false;
        }
        return true;
    }
    /// <summary>
    /// Supported libraries to load
    /// </summary>
    private static readonly HashSet<string> _nativeLibraries = new()
    {
        "freetype",
        "FastNoise"
    };
}

/// <summary>
/// Base class for native dll loaders for different platforms
/// </summary>
internal abstract class DllLoader
{
    /// <summary>
    /// Returns loader for current platform
    /// </summary>
    /// <returns></returns>
    public static DllLoader Loader()
    {
        return RuntimeEnv.OS switch
        {
            OS.Windows => new WindowsDllLoader(),
            OS.Mac => new MacDllLoader(),
            OS.Linux => new LinuxDllLoader(),
            _ => new WindowsDllLoader()
        };
    }

    /// <summary>
    /// Loads specified native dll.<br/>
    /// Platform specific paths:
    /// <list type="bullet">
    /// <item>Windows: '/native/win/{<paramref name="dllName"/>}.dll'</item>
    /// <item>Linux: '/native/linux/{<paramref name="dllName"/>}.so'</item>
    /// <item>Mac: '/native/mac/{<paramref name="dllName"/>}.dylib'</item>
    /// </list>
    /// </summary>
    /// <param name="dllName">Native dll name, not path and without extension</param>
    /// <param name="logger">To log errors</param>
    /// <param name="mod">Mod that has specified native library in /native/{platform} directory</param>
    /// <returns>true if was successfully loaded</returns>
    public bool Load(string dllName, ILogger logger, Mod mod)
    {
        string suffix = RuntimeEnv.OS switch
        {
            OS.Windows => ".dll",
            OS.Mac => ".dylib",
            OS.Linux => ".so",
            _ => ".so"
        };
        string prefix = RuntimeEnv.OS switch
        {
            OS.Windows => "win/",
            OS.Mac => "mac/",
            OS.Linux => "linux/",
            _ => "linux"
        };

        string dllPath = Path.Combine(((ModContainer)mod).FolderPath, "native", $"{prefix}{dllName}{suffix}");

        return Load(dllPath, logger);
    }

    /// <summary>
    /// Load dll using platofrm specific functions
    /// </summary>
    /// <param name="dllPath">Full path to dll</param>
    /// <param name="logger">To log errors</param>
    /// <returns>true if was successfuly loaded</returns>
    protected abstract bool Load(string dllPath, ILogger logger);
}

/// <summary>
/// Loads native dll on Windows
/// </summary>
internal class WindowsDllLoader : DllLoader
{
    /// <summary>
    /// Function from 'kernel32.dll' that is used to load dlls on windows
    /// </summary>
    /// <param name="fileName">Full path to dll</param>
    /// <returns><see cref="IntPtr.Zero"/> if failed to load library</returns>
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string fileName);
    /// <summary>
    /// Retrieves error code of error occurred while loading dynamic library 
    /// </summary>
    /// <returns>Error code</returns>
    [DllImport("kernel32")]
    private static extern uint GetLastError();
    /// <summary>
    /// Retrieves error message for error code provided by <see cref="GetLastError"/>
    /// </summary>
    /// <param name="dwFlags"></param>
    /// <param name="lpSource"></param>
    /// <param name="dwMessageId">Error code from <see cref="GetLastError"/></param>
    /// <param name="dwLanguageId"></param>
    /// <param name="lpBuffer">Error message will be written into this object</param>
    /// <param name="nSize"></param>
    /// <param name="Arguments"></param>
    /// <returns></returns>
    [DllImport("kernel32")]
    private static extern uint FormatMessage(uint dwFlags, IntPtr lpSource, uint dwMessageId,
        uint dwLanguageId, [Out] StringBuilder lpBuffer, uint nSize, IntPtr[]? Arguments);

    /// <summary>
    /// Loads specified dll using windows specific functions
    /// </summary>
    /// <param name="dllPath">Full path to dll</param>
    /// <param name="logger">To log errors</param>
    /// <returns>true if was successfully loaded</returns>
    protected override bool Load(string dllPath, ILogger logger)
    {
        IntPtr? handle = LoadLibrary(dllPath);

        if (handle == IntPtr.Zero)
        {
            uint errorCode = GetLastError();

            StringBuilder errorMessageBuilder = new(255);
            _ = FormatMessage(0x00001000, IntPtr.Zero, errorCode, 0, errorMessageBuilder, 255, null);
            string errorMessage = errorMessageBuilder.ToString();

            Exception innerException = new Win32Exception();
            Exception exception = new DllNotFoundException("Unable to load library: " + dllPath, innerException);
            logger.Fatal($"Failed to load embedded DLL:\nDlError:\n{errorMessage}\nException:\n{exception}");
            return false;
        }

        return true;
    }
}

internal partial class LinuxDllLoader : DllLoader
{
    [DllImport("libdl.so.2", CharSet = CharSet.Unicode)]
    static extern IntPtr dlopen(string fileName, int flags);
    [LibraryImport("libdl.so.2")]
    private static partial IntPtr dlerror();

    /// <summary>
    /// Loads specified dll using linux specific functions
    /// </summary>
    /// <param name="dllPath">Full path to dll</param>
    /// <param name="logger">To log errors</param>
    /// <returns>true if was successfully loaded</returns>
    protected override bool Load(string dllPath, ILogger logger)
    {
        IntPtr? handle = dlopen(dllPath, 1);

        if (handle == IntPtr.Zero)
        {
            Exception innerException = new Win32Exception();
            Exception exception = new DllNotFoundException("Unable to load library: " + dllPath, innerException);
            logger.Fatal($"Failed to load embedded DLL:\nDlError:\n{Marshal.PtrToStringAnsi(dlerror())}\nException:\n{exception}");
            return false;
        }

        return true;
    }
}

internal class MacDllLoader : DllLoader
{
    [DllImport("libdl.dylib", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string filename, int flags);
    [DllImport("libdl.dylib")]
    private static extern IntPtr dlerror();

    /// <summary>
    /// Loads specified dll using OSX (Mac) specific functions
    /// </summary>
    /// <param name="dllPath">Full path to dll</param>
    /// <param name="logger">To log errors</param>
    /// <returns>true if was successfully loaded</returns>
    protected override bool Load(string dllPath, ILogger logger)
    {
        IntPtr? handle = dlopen(dllPath, 1);

        if (handle == IntPtr.Zero)
        {
            Exception innerException = new Win32Exception();
            Exception exception = new DllNotFoundException("Unable to load library: " + dllPath, innerException);
            logger.Fatal($"Failed to load embedded DLL:\nDlError:\n{Marshal.PtrToStringAnsi(dlerror())}\nException:\n{exception}");
            return false;
        }

        return true;
    }
}
