using System;
using System.Threading;
using System.Windows.Forms;
using SharpDX.DirectInput;

namespace AFVReplay.Input
{
    /// <summary>
    /// Watches the Replay binding with DirectInput, the same way vatSys watches PTT,
    /// so it works even when vatSys isn't the focused window.
    /// </summary>
    internal sealed class InputPoller : IDisposable
    {
        private readonly Func<Control> owner;
        private readonly Action onPressed;
        private readonly Thread thread;
        private readonly object sync = new object();
        private DirectInput directInput;
        private Device device;
        private InputBinding binding;
        private bool bindingChanged;
        private volatile bool paused;
        private volatile bool disposed;
        private bool isDown;

        public InputPoller(Func<Control> owner, Action onPressed)
        {
            this.owner = owner;
            this.onPressed = onPressed;
            thread = new Thread(Loop) { IsBackground = true, Name = "AFVReplay input" };
        }

        public void Start() => thread.Start();

        public void SetBinding(string json)
        {
            lock (sync)
            {
                binding = InputBinding.Parse(json);
                bindingChanged = true;
            }
        }

        /// <summary>Stops listening while a new key is being captured.</summary>
        public bool Paused { get => paused; set => paused = value; }

        private void Loop()
        {
            while (!disposed)
            {
                try
                {
                    InputBinding b;
                    lock (sync)
                    {
                        b = binding;
                        if (bindingChanged) { ReleaseDevice(); bindingChanged = false; }
                    }

                    if (paused || b == null)
                    {
                        ReleaseDevice();
                        Thread.Sleep(50);
                        continue;
                    }

                    if (device == null && !TryCreateDevice(b))
                    {
                        Thread.Sleep(3000);
                        continue;
                    }

                    PollOnce(b);
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    // Device unplugged, lost focus rights, etc. Recreate and carry on.
                    Diag.Warn("input poll failed: " + ex.GetType().Name);
                    ReleaseDevice();
                    Thread.Sleep(2000);
                }
            }
            ReleaseDevice();
            directInput?.Dispose();
        }

        private void PollOnce(InputBinding b)
        {
            device.Poll();
            switch (device)
            {
                case Keyboard kb:
                    foreach (var u in kb.GetBufferedData())
                        if (u.Key == b.Key) Edge(u.IsPressed);
                    break;
                case Mouse mouse:
                    foreach (var u in mouse.GetBufferedData())
                        if (u.Offset == b.MouseButton) Edge(u.Value > 0);
                    break;
                case Joystick joy:
                    foreach (var u in joy.GetBufferedData())
                        if (u.Offset == b.JoystickButton) Edge(u.Value > 0);
                    break;
            }
        }

        private void Edge(bool pressed)
        {
            if (pressed && !isDown)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { onPressed(); }
                    catch (Exception ex) { Diag.Warn("replay press failed: " + ex.GetType().Name); }
                });
            }
            isDown = pressed;
        }

        private bool TryCreateDevice(InputBinding b)
        {
            var form = owner();
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return false;
            directInput = directInput ?? new DirectInput();

            if (b.InputType == InputBinding.Kind.Joystick && !directInput.IsDeviceAttached(b.DeviceGuid)) return false;

            Device created = null;
            form.Invoke((MethodInvoker)(() =>
            {
                switch (b.InputType)
                {
                    case InputBinding.Kind.Keyboard: created = new Keyboard(directInput); break;
                    case InputBinding.Kind.Mouse: created = new Mouse(directInput); break;
                    case InputBinding.Kind.Joystick: created = new Joystick(directInput, b.DeviceGuid); break;
                }
                created.Properties.BufferSize = 128;
                created.SetCooperativeLevel(form.Handle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                created.Acquire();
            }));
            device = created;
            isDown = false;
            return device != null;
        }

        private void ReleaseDevice()
        {
            if (device == null) return;
            try { device.Unacquire(); } catch { }
            try { device.Dispose(); } catch { }
            device = null;
            isDown = false;
        }

        public void Dispose()
        {
            disposed = true;
            if (thread.IsAlive) thread.Join(500);
        }
    }
}
