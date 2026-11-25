using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VideoArchive;

// Keep Explorer's thumbnail view, with directory navigation owned by the picker.
public sealed class ExplorerResultsHost : HwndHost
{
    const uint LargeIconView = 1;
    const int IconSize = 96;
    const uint NoDropTarget = 0x200;
    const uint NavigateOnce = 0x1;
    const uint NoPersistViewState = 0x80;
    const uint AutoArrange = 0x1;
    const uint SingleSelection = 0x40;
    const uint NoWebView = 0x4000;
    static readonly Guid BrowserClass = new("71f96385-ddd6-48d3-a0c1-ae06e8b055fb");
    IExplorerBrowser? browser;
    IResultsFolder? results;
    BrowserSite? site;
    IntPtr container;
    bool initialized;
    bool resizeQueued;

    public event Action? SelectionChanged;
    public event Action<string>? ItemActivated;
    public event Action? CancelRequested;
    public string? InitializationError { get; private set; }
    public bool Ready => results != null;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const uint childHost = 0x56010000; // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_TABSTOP
        container = CreateWindowEx(0, "STATIC", null, childHost, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (container == 0) throw new System.ComponentModel.Win32Exception();
        return new HandleRef(this, container);
    }

    internal void InitializeBrowser()
    {
        if (container == 0 || initialized) return;
        // Shell initialization can pump messages, so never do it inside WPF layout.
        try
        {
            browser = (IExplorerBrowser)Activator.CreateInstance(Type.GetTypeFromCLSID(BrowserClass, true)!)!;
            site = new BrowserSite(this);
            ((IObjectWithSite)browser).SetSite(site);
            var settings = new FolderSettings(LargeIconView, SingleSelection | AutoArrange | NoWebView);
            GetClientRect(container, out var bounds);
            browser.Initialize(container, bounds, settings);
            initialized = true;
            browser.SetOptions(NavigateOnce | NoPersistViewState);
            browser.FillFromObject(0, NoDropTarget);
            var view = GetView();
            try
            {
                view.GetFolder(typeof(IResultsFolder).GUID, out var folder);
                results = (IResultsFolder)folder;
                view.SetViewModeAndIconSize(LargeIconView, IconSize);
            }
            finally { Marshal.ReleaseComObject(view); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            InitializationError = $"无法打开 Windows 缩略图浏览区（0x{ex.HResult:X8}）。";
            ReleaseBrowser();
        }
    }

    public void Clear() => browser!.RemoveAll();

    public void Add(string path)
    {
        int hr = SHCreateItemFromParsingName(path, 0, typeof(IShellItem).GUID, out var item);
        if (hr < 0 || item == null) return; // An item may have disappeared during enumeration.
        try { results!.AddItem(item); }
        finally { Marshal.ReleaseComObject(item); }
    }

    public void ApplyThumbnailView()
    {
        var view = GetView();
        try { view.SetViewModeAndIconSize(LargeIconView, IconSize); }
        finally { Marshal.ReleaseComObject(view); }
    }

    IFolderView2 GetView()
    {
        browser!.GetCurrentView(typeof(IFolderView2).GUID, out var view);
        return (IFolderView2)view;
    }

    public string? SelectedPath()
    {
        if (!Ready) return null;
        IFolderView2? view = null;
        try
        {
            view = GetView();
            if (view.GetSelectedItem(-1, out int index) != 0) return null;
            view.GetItem(index, typeof(IShellItem).GUID, out var selected);
            try
            {
                ((IShellItem)selected).GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.ReleaseComObject(selected); }
        }
        catch (COMException) { return null; }
        finally { if (view != null) Marshal.ReleaseComObject(view); }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005 && initialized && !resizeQueued)
        {
            resizeQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                resizeQueued = false;
                if (container == 0 || !initialized || !GetClientRect(container, out var rect)) return;
                try { browser?.SetRect(0, rect); }
                catch (COMException) { }
            });
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (!Ready) return false;
        object? view = null;
        try
        {
            browser!.GetCurrentView(typeof(IShellView).GUID, out view);
            ((IShellView)view).UIActivate(2); // SVUIA_ACTIVATE_FOCUS
            return true;
        }
        catch (COMException) { return false; }
        finally { if (view != null) Marshal.ReleaseComObject(view); }
    }

    protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (msg.message == 0x0100 && msg.wParam == 0x1B)
        {
            Dispatcher.BeginInvoke(() => CancelRequested?.Invoke());
            return true;
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        container = 0;
        if (browser != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, ReleaseBrowser);
        DestroyWindow(hwnd.Handle);
    }

    internal void ReleaseBrowser()
    {
        if (results != null) { Marshal.ReleaseComObject(results); results = null; }
        if (browser == null) return;
        try
        {
            try { ((IObjectWithSite)browser).SetSite(null); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { }
            if (initialized) browser.Destroy();
        }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(browser); browser = null; initialized = false; site = null; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class BrowserSite(ExplorerResultsHost host) : IServiceProviderNative, ICommDlgBrowser
    {
        public int QueryService(in Guid service, in Guid iid, out IntPtr result)
        {
            result = 0;
            if (service != typeof(ICommDlgBrowser).GUID) return unchecked((int)0x80004002);
            IntPtr unknown = Marshal.GetIUnknownForObject(this);
            try { return Marshal.QueryInterface(unknown, iid, out result); }
            finally { Marshal.Release(unknown); }
        }

        public int OnDefaultCommand(IntPtr view)
        {
            if (host.SelectedPath() is { } path)
                host.Dispatcher.BeginInvoke(() => host.ItemActivated?.Invoke(path));
            return 0; // The picker decides whether to navigate or choose the item.
        }

        public int OnStateChange(IntPtr view, uint change)
        {
            if (change == 2) host.Dispatcher.BeginInvoke(() => host.SelectionChanged?.Invoke());
            if (change == 3) return 1; // CDBOSC_RENAME: decline renaming from a selection window.
            return 0;
        }

        public int IncludeObject(IntPtr view, IntPtr item) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    readonly struct FolderSettings(uint mode, uint flags)
    {
        readonly uint viewMode = mode;
        readonly uint folderFlags = flags;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string? title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid iid, out IShellItem? item);

    [ComImport, Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerBrowser
    {
        void Initialize(IntPtr hwnd, in NativeRect rect, in FolderSettings settings);
        void Destroy();
        void SetRect(IntPtr position, NativeRect rect);
        void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string name);
        void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFolderSettings(in FolderSettings settings);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void BrowseToIDList(IntPtr item, uint flags);
        void BrowseToObject(IntPtr item, uint flags);
        void FillFromObject(IntPtr source, uint flags);
        void RemoveAll();
        void GetCurrentView(in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("96e5ae6d-6ae1-4b1c-900c-c6480eaa8828"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IResultsFolder
    {
        [PreserveSig] int AddItem(IShellItem item);
    }

    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView2
    {
        // IFolderView methods must precede the IFolderView2 methods in the native vtable.
        void GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode(uint mode);
        void GetFolder(in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object folder);
        void Item(int index, out IntPtr item);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object items);
        void GetSelectionMarkedItem(out int index);
        void GetFocusedItem(out int index);
        void GetItemPosition(IntPtr item, IntPtr point);
        void GetSpacing(IntPtr point);
        void GetDefaultSpacing(IntPtr point);
        [PreserveSig] int GetAutoArrange();
        void SelectItem(int index, uint flags);
        void SelectAndPositionItems(uint count, IntPtr items, IntPtr points, uint flags);
        void SetGroupBy(IntPtr key, int ascending);
        void GetGroupBy(IntPtr key, IntPtr ascending);
        void SetViewProperty(IntPtr item, IntPtr key, IntPtr value);
        void GetViewProperty(IntPtr item, IntPtr key, IntPtr value);
        void SetTileViewProperties(IntPtr item, IntPtr properties);
        void SetExtendedTileViewProperties(IntPtr item, IntPtr properties);
        void SetText(int type, IntPtr text);
        void SetCurrentFolderFlags(uint mask, uint flags);
        void GetCurrentFolderFlags(out uint flags);
        void GetSortColumnCount(out int count);
        void SetSortColumns(IntPtr columns, int count);
        void GetSortColumns(IntPtr columns, int count);
        void GetItem(int index, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
        void GetVisibleItem(int start, int previous, out int index);
        [PreserveSig] int GetSelectedItem(int start, out int index);
        void GetSelection(int noneImpliesFolder, out IntPtr array);
        void GetSelectionState(IntPtr item, out uint flags);
        void InvokeVerbOnSelection(IntPtr verb);
        void SetViewModeAndIconSize(uint mode, int size);
        void GetViewModeAndIconSize(out uint mode, out int size);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, in Guid handler, in Guid iid, out IntPtr value);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out IntPtr name);
    }

    [ComImport, Guid("000214e3-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enter);
        [PreserveSig] int TranslateAccelerator(ref MSG message);
        void EnableModeless(int enable);
        void UIActivate(uint state);
    }

    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    }

    [ComVisible(true), Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProviderNative
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, out IntPtr result);
    }

    [ComVisible(true), Guid("000214f1-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICommDlgBrowser
    {
        [PreserveSig] int OnDefaultCommand(IntPtr view);
        [PreserveSig] int OnStateChange(IntPtr view, uint change);
        [PreserveSig] int IncludeObject(IntPtr view, IntPtr item);
    }
}
