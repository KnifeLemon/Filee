// Topmost windows use a panel while shown, then restore their original class/style when hidden or before close (#805).
// Objective-C test doubles exercise the real interop calls without creating AppKit UI on xUnit's worker thread.

using System.Runtime.InteropServices;
using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class MacOSWindowTests
{
    [Fact]
    public void Raising_and_restoring_a_window_repeatedly_restores_its_native_class_and_style()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires the macOS Objective-C runtime.");
            return;
        }

        _ = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        // Production checks these exact Avalonia class names. This test runs in the platform-only test process,
        // which does not load Avalonia.Native; never replace a real class if another host has loaded it.
        if (GetClass("AvnWindow") != 0 || GetClass("AvnPanel") != 0)
        {
            Assert.Skip("Requires a process without Avalonia.Native window classes.");
            return;
        }

        nint behavior = (1 << 1) | (1 << 6) | (1 << 7) | (1 << 9) | (1 << 16) | (1 << 17) | (1 << 18);
        const nint nonactivatingPanel = 1 << 7;
        const nint unrelatedStyle = (1 << 2) | (1 << 11);
        nint style = unrelatedStyle;
        nint level = 0;
        var orders = 0;
        var hidesOnDeactivate = true;
        var styleChanges = 0;
        var hidesOnDeactivateChanges = 0;
        GetInteger getBehavior = (_, _) => behavior;
        GetInteger getStyle = (_, _) => style;
        SetInteger setBehavior = (_, _, value) => behavior = value;
        SetInteger setLevel = (_, _, value) => level = value;
        SetInteger setStyle = (_, _, value) =>
        {
            style = value;
            styleChanges++;
        };
        SetInteger setHidesOnDeactivate = (_, _, value) =>
        {
            hidesOnDeactivate = value != 0;
            hidesOnDeactivateChanges++;
        };
        SendAction orderFront = (_, _) => orders++;
        Delegate[] callbacks = [getBehavior, getStyle, setBehavior, setLevel, setStyle, setHidesOnDeactivate, orderFront];

        var baseClass = AllocateClass(GetClass("NSObject"), "FileeRaiseTopmostTestWindow", 0);
        Assert.NotEqual(0, baseClass);
        nint windowClass = 0, panelClass = 0, window = 0, foreign = 0;
        try
        {
            AddMethod(baseClass, "collectionBehavior", getBehavior, "q@:");
            AddMethod(baseClass, "styleMask", getStyle, "q@:");
            AddMethod(baseClass, "setCollectionBehavior:", setBehavior, "v@:q");
            AddMethod(baseClass, "setLevel:", setLevel, "v@:q");
            AddMethod(baseClass, "setStyleMask:", setStyle, "v@:q");
            AddMethod(baseClass, "setHidesOnDeactivate:", setHidesOnDeactivate, "v@:q");
            AddMethod(baseClass, "orderFrontRegardless", orderFront, "v@:");
            RegisterClass(baseClass);

            var platform = new MacOSPlatformServices();
            platform.RaiseTopmost(0);
            platform.RestoreWindow(0);
            foreign = Send(Send(baseClass, Selector("alloc")), Selector("init"));
            Assert.NotEqual(0, foreign);
            platform.RaiseTopmost(foreign);
            platform.RestoreWindow(foreign);
            Assert.Equal(baseClass, ObjectClass(foreign));
            Assert.Equal(unrelatedStyle, style);
            Assert.True(hidesOnDeactivate);
            Assert.Equal(0, styleChanges);
            Assert.Equal(0, hidesOnDeactivateChanges);
            orders = 0;

            windowClass = RegisterSubclass(baseClass, "AvnWindow");
            panelClass = RegisterSubclass(baseClass, "AvnPanel");
            window = Send(Send(windowClass, Selector("alloc")), Selector("init"));
            Assert.NotEqual(0, window);

            for (var i = 1; i <= 3; i++)
            {
                platform.RaiseTopmost(window);
                Assert.Equal(panelClass, ObjectClass(window));
                Assert.Equal((nint)(1 | (1 << 6) | (1 << 8)), behavior);
                Assert.Equal(unrelatedStyle | nonactivatingPanel, style);
                Assert.False(hidesOnDeactivate);
                Assert.Equal((nint)3, level);
                Assert.Equal(2 * i - 1, orders);
                Assert.Equal(2 * i - 1, styleChanges);
                Assert.Equal(i, hidesOnDeactivateChanges);

                platform.RaiseTopmost(window);
                Assert.Equal(panelClass, ObjectClass(window));
                Assert.Equal(unrelatedStyle | nonactivatingPanel, style);
                Assert.False(hidesOnDeactivate);
                Assert.Equal(2 * i, orders);
                Assert.Equal(2 * i - 1, styleChanges);
                Assert.Equal(i, hidesOnDeactivateChanges);

                platform.RestoreWindow(window);
                Assert.Equal(windowClass, ObjectClass(window));
                Assert.Equal(unrelatedStyle, style);
                Assert.False(hidesOnDeactivate);
                Assert.Equal(2 * i, styleChanges);

                platform.RestoreWindow(window);
                Assert.Equal(windowClass, ObjectClass(window));
                Assert.Equal(unrelatedStyle, style);
                Assert.Equal(2 * i, styleChanges);
            }

            var styleChangesBeforeForeignRestore = styleChanges;
            var hidesOnDeactivateChangesBeforeForeignRestore = hidesOnDeactivateChanges;
            platform.RaiseTopmost(foreign);
            platform.RestoreWindow(foreign);
            Assert.Equal(baseClass, ObjectClass(foreign));
            Assert.Equal(unrelatedStyle, style);
            Assert.Equal(styleChangesBeforeForeignRestore, styleChanges);
            Assert.Equal(hidesOnDeactivateChangesBeforeForeignRestore, hidesOnDeactivateChanges);
        }
        finally
        {
            if (window != 0)
                Send(window, Selector("release"));
            if (foreign != 0)
                Send(foreign, Selector("release"));
            if (panelClass != 0)
                DisposeClass(panelClass);
            if (windowClass != 0)
                DisposeClass(windowClass);
            DisposeClass(baseClass);
            GC.KeepAlive(callbacks);
        }
    }

    private static nint RegisterSubclass(nint superclass, string name)
    {
        var result = AllocateClass(superclass, name, 0);
        Assert.NotEqual(0, result);
        RegisterClass(result);
        return result;
    }

    private static void AddMethod(nint cls, string name, Delegate callback, string signature) =>
        Assert.True(AddNativeMethod(cls, Selector(name), Marshal.GetFunctionPointerForDelegate(callback), signature));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInteger(nint receiver, nint selector);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetInteger(nint receiver, nint selector, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendAction(nint receiver, nint selector);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClass(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(nint cls);

    [DllImport(ObjC, EntryPoint = "objc_disposeClassPair")]
    private static extern void DisposeClass(nint cls);

    [DllImport(ObjC, EntryPoint = "object_getClass")]
    private static extern nint ObjectClass(nint instance);

    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddNativeMethod(nint cls, nint selector, nint implementation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signature);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
}
