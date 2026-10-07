using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LampaWin.Desktop;

// Read-only MMDevice subscription. Never changes Windows defaults or endpoint volume.
internal sealed class AudioDeviceMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NotificationClient _sink;
    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;
    private string? _lastDefault;
    public event Action? OutputChanged;

    public AudioDeviceMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        _lastDefault = DefaultOutputId;
        _sink = new NotificationClient(QueueChange);
        try { Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_sink)); }
        catch { Marshal.ReleaseComObject(_enumerator); throw; }
    }

    public string? DefaultOutputId
    {
        get
        {
            if (_disposed || _enumerator.GetDefaultAudioEndpoint(0, 0, out var device) < 0) return null;
            try { Marshal.ThrowExceptionForHR(device.GetId(out var id)); return id; }
            finally { Marshal.ReleaseComObject(device); }
        }
    }

    private void QueueChange(string? deviceId, bool defaultChanged)
    {
        // Core Audio callbacks must return promptly and may arrive on an MTA thread.
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            var current = DefaultOutputId;
            var affectsOutput = defaultChanged || current != _lastDefault
                || deviceId == current || deviceId == _lastDefault;
            _lastDefault = current;
            if (affectsOutput) OutputChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(_sink);
        Marshal.ReleaseComObject(_enumerator);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class NotificationClient(Action<string?, bool> changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint state) { changed(id, false); return 0; }
        public int OnDeviceAdded(string id) { changed(id, false); return 0; }
        public int OnDeviceRemoved(string id) { changed(id, false); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        { if (flow == 0 && role == 0) changed(id, true); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid Format; public uint Id; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, uint context, nint activation, out nint result);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
}
