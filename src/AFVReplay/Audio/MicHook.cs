using System;
using System.Threading;
using AFVReplay.VatSys;

namespace AFVReplay.Audio
{
    /// <summary>
    /// Listens to your own mic exactly as vatSys sends it: the AFV mic (recorded while PTT is held)
    /// and the coord mic (recorded while vatSys is sending it to an open line). Read-only towards
    /// vatSys: one event handler on each, re-attached when vatSys restarts its audio, removed on Unhook.
    /// </summary>
    internal sealed class MicHook : IDisposable
    {
        private readonly Func<object> getAfvInput;
        private readonly Func<object> getMumble;
        private readonly OwnTxRecorder ownTx;
        private readonly CoordRecorder coord;
        private readonly Func<string> ownCallsign;
        private readonly Func<object, bool> isCoordSending;
        private readonly object sync = new object();
        private Timer timer;
        private object afvInput, coordInput, mumble;
        private Delegate afvSub, coordSub;

        public MicHook(Func<object> getAfvInput, Func<object> getMumble, OwnTxRecorder ownTx, CoordRecorder coord, Func<string> ownCallsign,
                       Func<object, bool> isCoordSending = null)
        {
            this.isCoordSending = isCoordSending ?? VatSysBridge.IsCoordSending;
            this.getAfvInput = getAfvInput;
            this.getMumble = getMumble;
            this.ownTx = ownTx;
            this.coord = coord;
            this.ownCallsign = ownCallsign;
        }

        public bool AfvHooked { get { lock (sync) return afvSub != null; } }
        public bool CoordHooked { get { lock (sync) return coordSub != null; } }

        public void Start(TimeSpan interval)
        {
            timer = new Timer(_ => { try { Tick(); } catch (Exception ex) { Diag.Warn("mic tick failed: " + ex.GetType().Name); } },
                              null, TimeSpan.Zero, interval);
        }

        public void Tick()
        {
            lock (sync)
            {
                var a = getAfvInput();
                if (!ReferenceEquals(a, afvInput))
                {
                    if (afvSub != null) { try { VatSysBridge.UnsubscribeInput(afvInput, afvSub); } catch { } }
                    afvSub = a == null ? null : VatSysBridge.SubscribeInput(a, (buf, off, len) => ownTx.Feed(buf, off, len));
                    afvInput = a;
                }

                var m = getMumble();
                var c = m == null ? null : VatSysBridge.GetMumbleInputMix(m);
                if (!ReferenceEquals(c, coordInput))
                {
                    if (coordSub != null) { try { VatSysBridge.UnsubscribeInput(coordInput, coordSub); } catch { } }
                    coordSub = c == null ? null : VatSysBridge.SubscribeInput(c, OnCoordMic);
                    coordInput = c;
                }
                mumble = m;
            }
            ownTx.Tick();
        }

        private void OnCoordMic(byte[] buf, int off, int len)
        {
            // vatSys captures the mic all the time; only what it actually sends to an open line counts.
            if (!isCoordSending(mumble)) return;
            coord.Feed(ownCallsign() ?? "You", buf, off, len, own: true);
        }

        public void Unhook()
        {
            lock (sync)
            {
                if (afvSub != null) { try { VatSysBridge.UnsubscribeInput(afvInput, afvSub); } catch { } }
                if (coordSub != null) { try { VatSysBridge.UnsubscribeInput(coordInput, coordSub); } catch { } }
                afvSub = coordSub = null;
                afvInput = coordInput = mumble = null;
            }
            ownTx.Reset();
        }

        public void Dispose()
        {
            timer?.Dispose();
            Unhook();
        }
    }
}
