using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Employee.Desktop;
// Shell balloons work for an unpackaged desktop app without registration or dependencies.
internal sealed class WindowsNotification : IDisposable
{
    private readonly HwndSource source;
    private readonly Action open;
    private readonly uint restartMessage;
    private NotifyData data;
    private bool added;
    private readonly Queue<(string Title, string Message, Action Open)> pending = new();
    private Action? balloonOpen;
    private bool disposed;
    public WindowsNotification(Window owner, Action open)
    {
        this.open = open;
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(),
            Window = source.Handle,
            Id = 1,
            Flags = 7,
            Callback = 0x8001,
            Icon = LoadIcon(IntPtr.Zero, 32516),
            Tip = "AI Employee Manager",
            Info = "",
            Title = ""
        };
        restartMessage = RegisterWindowMessage("TaskbarCreated");
        source.AddHook(Hook);
        Add();
    }

    private void Add()
    {
        data.Flags = 7;
        added = Shell_NotifyIcon(0, ref data);
    }

    public void Show(string title, string message, Action activate)
    {
        if (disposed)
            return;
        pending.Enqueue((title, message, activate));
        if (balloonOpen == null)
            ShowNext();
    }

    private void ShowNext()
    {
        if (disposed || balloonOpen != null || !pending.TryDequeue(out var notice))
            return;
        if (!added)
            Add();
        balloonOpen = notice.Open;
        data.Flags = 0x10;
        data.Title = notice.Title[..Math.Min(63, notice.Title.Length)];
        data.Info = notice.Message[..Math.Min(255, notice.Message.Length)];
        data.InfoFlags = 1;
        if (!Shell_NotifyIcon(1, ref data))
            FinishBalloon(false);
    }

    private void FinishBalloon(bool activate)
    {
        var target = balloonOpen;
        balloonOpen = null;
        // Advance after the current Shell callback has finished.
        source.Dispatcher.BeginInvoke(new Action(ShowNext));
        if (activate)
            target?.Invoke();
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == restartMessage)
        {
            data.Flags = 7;
            Add();
            FinishBalloon(false);
        }

        if (message == 0x8001)
        {
            switch (lParam.ToInt64())
            {
                case 0x202:
                    open();
                    handled = true;
                    break; // Tray icon click.
                case 0x405:
                    FinishBalloon(true);
                    handled = true;
                    break; // Balloon click.
                case 0x403: // Balloon hidden.
                case 0x404:
                    FinishBalloon(false);
                    handled = true;
                    break; // Balloon timed out.
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        disposed = true;
        pending.Clear();
        balloonOpen = null;
        Shell_NotifyIcon(2, ref data);
        source.RemoveHook(Hook);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint operation, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, int name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
