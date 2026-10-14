using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Verse;

namespace CombatAI
{
    public class AsyncActions
    {
        private static readonly List<AsyncActions> running = new List<AsyncActions>();
        private static int _globalInstanceCounter;

        private readonly int hashOffset;

        public readonly object locker_Main = new object();
        public readonly object locker_offMain = new object();
        private readonly int mainLoopTickInterval;

        private readonly List<Action> queuedMainThreadActions = new List<Action>();
        private readonly List<Action> queuedOffThreadActions = new List<Action>();
        private readonly AutoResetEvent waitHandle = new AutoResetEvent(false);
        private readonly Thread thread;
        private volatile bool alive = true;
        private bool mainThreadActionQueueEmpty;

        public AsyncActions(int mainLoopTickInterval = 5)
        {
            this.mainLoopTickInterval = mainLoopTickInterval;
            hashOffset = Interlocked.Increment(ref _globalInstanceCounter) % 128;
            thread = new Thread(OffMainThreadActionLoop)
            {
                IsBackground = true,
                Name = "CombatAI.AsyncActions"
            };
        }

        public bool Alive => alive;

        public void Start()
        {
            thread.Start();
            running.Add(this);
        }

        public void ExecuteMainThreadActions()
        {
            MainThreadActionLoop();
        }

        public void Kill()
        {
            if (!alive)
            {
                running.Remove(this);
                return;
            }

            alive = false;
            running.Remove(this);

            lock (locker_Main)
            {
                queuedMainThreadActions.Clear();
                mainThreadActionQueueEmpty = true;
            }

            lock (locker_offMain)
            {
                queuedOffThreadActions.Clear();
            }

            // Wake a worker blocked in WaitOne so it can observe alive == false and exit.
            // Thread.Abort is unsupported on the RimWorld 1.7 / .NET 10 runtime and could
            // deadlock the old Mono/Boehm runtime even before the migration.
            try
            {
                waitHandle.Set();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (thread.IsAlive && Thread.CurrentThread != thread)
            {
                if (!thread.Join(1000))
                {
                    Log.Warning("CAI: AsyncActions worker did not stop within 1000 ms; it will exit cooperatively after its current action returns.");
                    return;
                }
            }

            waitHandle.Dispose();
        }

        public static void KillAll()
        {
            foreach (AsyncActions asyncActions in running.ToList())
            {
                asyncActions.Kill();
            }
        }

        public void EnqueueOffThreadAction(Action action)
        {
            if (!alive || action == null)
            {
                return;
            }

            lock (locker_offMain)
            {
                if (!alive)
                {
                    return;
                }
                if (queuedOffThreadActions.Count > 1024)
                {
                    Log.Error($"ISMA: AsyncActions is leaking memory with more than {queuedOffThreadActions.Count} actions queued!");
                }
                else
                {
                    queuedOffThreadActions.Add(action);
                    waitHandle.Set();
                }
            }
        }

        public void EnqueueMainThreadAction(Action action)
        {
            if (!alive || action == null)
            {
                return;
            }

            lock (locker_Main)
            {
                if (!alive)
                {
                    return;
                }
                queuedMainThreadActions.Add(action);
                mainThreadActionQueueEmpty = false;
            }
        }

        private Action DequeueOffThreadAction()
        {
            Action action = null;
            lock (locker_offMain)
            {
                if (queuedOffThreadActions.Count > 0)
                {
                    action = queuedOffThreadActions[0];
                    queuedOffThreadActions.RemoveAt(0);
                }
            }
            return action;
        }

        private Action DequeueMainThreadAction()
        {
            Action action = null;
            lock (locker_Main)
            {
                if (queuedMainThreadActions.Count > 0)
                {
                    action = queuedMainThreadActions[0];
                    queuedMainThreadActions.RemoveAt(0);
                }
                mainThreadActionQueueEmpty = queuedMainThreadActions.Count == 0;
            }
            return action;
        }

        private void MainThreadActionLoop()
        {
            if (!mainThreadActionQueueEmpty || (GenTicks.TicksGame + hashOffset) % mainLoopTickInterval == 0)
            {
                while (true)
                {
                    Action action = DequeueMainThreadAction();
                    if (action != null)
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception er)
                        {
                            Log.Error(er.ToString());
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void OffMainThreadActionLoop()
        {
            while (alive)
            {
                Action action = DequeueOffThreadAction();
                if (action != null)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception er)
                    {
                        Log.Error(er.ToString());
                    }
                }
                else if (alive)
                {
                    waitHandle.WaitOne();
                }
            }
        }
    }
}
