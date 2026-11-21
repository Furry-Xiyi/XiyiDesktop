using Microsoft.UI.Dispatching;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using XiyiDesktop.DesktopItems;

namespace XiyiDesktop.DesktopHost
{
    public enum DesktopHostState
    {
        Searching,
        Attached,
        WaitingForExplorer
    }

    public sealed class DesktopHostController : IDisposable
    {
        private readonly IntPtr _windowHandle;
        private readonly WindowsDesktopHost _desktopHost = new();
        private readonly DispatcherQueueTimer _timer;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly TypedEventHandler<DispatcherQueueTimer, object> _timerHandler;
        private IntPtr _attachedParent;
        private IntPtr _attachedShellView;
        private IntPtr _attachedListView;
        private WindowRestoreState _originalWindowState;
        private uint _attachedProcessId;
        private bool _nativeIconViewWasVisible;
        private bool _isAttached;
        private bool _disposed;
        private bool _windowLostRaised;
        private int _discoveryInProgress;

        public event EventHandler<DesktopHostStateChangedEventArgs>? StateChanged;
        public event EventHandler? WindowHandleLost;

        public bool IsAttached => _isAttached;
        /// <summary>The current Explorer desktop layer used as the owner for desktop-owned windows.</summary>
        public IntPtr AttachedParentHandle => _isAttached && NativeMethods.IsWindow(_attachedParent)
            ? _attachedParent
            : IntPtr.Zero;
        public IntPtr DesktopSurfaceHandle => _isAttached && NativeMethods.IsWindow(_windowHandle)
            ? _windowHandle
            : IntPtr.Zero;

        public DesktopHostController(IntPtr windowHandle, DispatcherQueue dispatcherQueue)
        {
            _windowHandle = windowHandle;
            _dispatcherQueue = dispatcherQueue;
            _timer = dispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timerHandler = (_, _) => RefreshAttachment();
            _timer.Tick += _timerHandler;
        }

        public void Start()
        {
            if (_disposed)
                return;

            RefreshAttachment();
            _timer.Start();
        }

        public void Refresh() => RefreshAttachment(forceRetry: true);

        /// <summary>Recomputes the attached desktop surface bounds after a display change.</summary>
        public void RefreshLayout() => RefreshAttachment();

        /// <summary>Asks Explorer to show its own background menu, including installed Shell extensions.</summary>
        public bool OpenNativeDesktopContextMenu() =>
            !_disposed && _isAttached && NativeMethods.PostNativeDesktopContextMenu(_attachedShellView);

        /// <summary>Shows the classic Shell background menu for the attached Explorer desktop view.</summary>
        public bool ShowClassicDesktopBackgroundContextMenu() =>
            !_disposed && _isAttached &&
            ShellContextMenuService.ShowForDesktopBackground(_windowHandle);

        private void RefreshAttachment(bool forceRetry = false)
        {
            if (_disposed)
                return;

            if (!NativeMethods.IsWindow(_windowHandle))
            {
                if (_isAttached)
                {
                    RestoreNativeIconView();
                    _isAttached = false;
                    _attachedListView = IntPtr.Zero;
                }

                if (!_windowLostRaised)
                {
                    _windowLostRaised = true;
                    RaiseState(DesktopHostState.WaitingForExplorer, null);
                    WindowHandleLost?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            if (!forceRetry && _isAttached && NativeMethods.IsWindow(_attachedParent) &&
                NativeMethods.GetParentWindow(_windowHandle) == _attachedParent &&
                NativeMethods.GetWindowProcessId(_attachedParent) == _attachedProcessId &&
                IsValidatedShellViewAttachment(new DesktopHostAttachment(
                    _attachedParent,
                    "Explorer desktop icon layer",
                    DesktopHostKind.ShellViewOverlay,
                    _attachedShellView,
                    usesUndocumentedShellBehavior: true)))
            {
                if (NativeMethods.PositionAboveShellView(_windowHandle, _attachedParent, _attachedShellView))
                    return;
            }

            if (_isAttached)
            {
                RestoreNativeIconView();
                NativeMethods.RestoreTopLevelWindow(_windowHandle, _originalWindowState, showWindow: false);
                _isAttached = false;
                _attachedParent = IntPtr.Zero;
                _attachedShellView = IntPtr.Zero;
                _attachedListView = IntPtr.Zero;
                _attachedProcessId = 0;
            }

            if (Interlocked.Exchange(ref _discoveryInProgress, 1) != 0)
                return;

            RaiseState(DesktopHostState.Searching, null);
            _ = DiscoverAndAttachAsync();
        }

        private async Task DiscoverAndAttachAsync()
        {
            (bool Found, DesktopHostAttachment? Attachment) result;
            try
            {
                result = await Task.Run(() =>
                {
                    var found = _desktopHost.TryAttach(_windowHandle, out var attachment);
                    return (found, attachment);
                });
            }
            catch
            {
                result = (false, null);
            }

            try
            {
                if (!_dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (_disposed)
                            return;

                        if (!NativeMethods.IsWindow(_windowHandle))
                        {
                            if (!_windowLostRaised)
                            {
                                _windowLostRaised = true;
                                RaiseState(DesktopHostState.WaitingForExplorer, null);
                                WindowHandleLost?.Invoke(this, EventArgs.Empty);
                            }
                            return;
                        }

                        if (result.Found && result.Attachment != null &&
                            IsValidatedShellViewAttachment(result.Attachment) &&
                            NativeMethods.TryAttachWindow(
                                _windowHandle,
                                result.Attachment.ParentHandle,
                                result.Attachment.ZOrderAnchor,
                                out _originalWindowState))
                        {
                            _attachedParent = result.Attachment.ParentHandle;
                            _attachedShellView = result.Attachment.ZOrderAnchor;
                            _attachedListView = NativeMethods.FindWindowEx(
                                _attachedShellView, IntPtr.Zero, "SysListView32", null);
                            _nativeIconViewWasVisible = NativeMethods.IsWindowVisible(_attachedListView);
                            if (_nativeIconViewWasVisible)
                                NativeMethods.HideNativeIconView(_attachedListView);
                            _attachedProcessId = NativeMethods.GetWindowProcessId(_attachedParent);
                            _isAttached = true;
                            RaiseState(DesktopHostState.Attached, result.Attachment.HostName);
                            return;
                        }

                        RaiseState(DesktopHostState.WaitingForExplorer, null);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _discoveryInProgress, 0);
                    }
                }))
                {
                    Interlocked.Exchange(ref _discoveryInProgress, 0);
                }
            }
            catch (Exception exception) when (exception is COMException || exception is ObjectDisposedException)
            {
                // The dispatcher may close between the background search and its UI
                // continuation. The window is already going away in that case.
                Interlocked.Exchange(ref _discoveryInProgress, 0);
            }
        }

        private void RaiseState(DesktopHostState state, string? hostName) =>
            StateChanged?.Invoke(this, new DesktopHostStateChangedEventArgs(state, hostName));

        private void RestoreNativeIconView()
        {
            if (_nativeIconViewWasVisible)
                NativeMethods.ShowNativeIconView(_attachedListView);
            _nativeIconViewWasVisible = false;
        }

        private static bool IsValidatedShellViewAttachment(DesktopHostAttachment attachment)
        {
            var parentClass = NativeMethods.GetClassName(attachment.ParentHandle);
            if (attachment.Kind != DesktopHostKind.ShellViewOverlay ||
                !attachment.UsesUndocumentedShellBehavior ||
                attachment.ParentHandle == IntPtr.Zero ||
                attachment.ZOrderAnchor == IntPtr.Zero ||
                !NativeMethods.IsWindow(attachment.ParentHandle) ||
                !NativeMethods.IsWindow(attachment.ZOrderAnchor) ||
                (!parentClass.Equals("Progman", StringComparison.Ordinal) &&
                 !parentClass.Equals("WorkerW", StringComparison.Ordinal)) ||
                !NativeMethods.GetClassName(attachment.ZOrderAnchor).Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
                NativeMethods.GetParentWindow(attachment.ZOrderAnchor) != attachment.ParentHandle ||
                NativeMethods.GetWindowProcessId(attachment.ParentHandle) == 0 ||
                NativeMethods.GetWindowProcessId(attachment.ParentHandle) != NativeMethods.GetWindowProcessId(attachment.ZOrderAnchor))
            {
                return false;
            }

            var listView = NativeMethods.FindWindowEx(attachment.ZOrderAnchor, IntPtr.Zero, "SysListView32", null);
            return listView != IntPtr.Zero &&
                NativeMethods.GetParentWindow(listView) == attachment.ZOrderAnchor &&
                NativeMethods.GetWindowProcessId(listView) == NativeMethods.GetWindowProcessId(attachment.ParentHandle);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= _timerHandler;
            if (_isAttached)
            {
                RestoreNativeIconView();
                NativeMethods.RestoreTopLevelWindow(_windowHandle, _originalWindowState, showWindow: false);
                _isAttached = false;
                _attachedListView = IntPtr.Zero;
            }
        }
    }

    public sealed class DesktopHostStateChangedEventArgs : EventArgs
    {
        public DesktopHostStateChangedEventArgs(DesktopHostState state, string? hostName)
        {
            State = state;
            HostName = hostName;
        }

        public DesktopHostState State { get; }
        public string? HostName { get; }
    }
}
