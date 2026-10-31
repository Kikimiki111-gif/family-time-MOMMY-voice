using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FamilyTimeVoice
{
    public sealed class MainThreadQueue
    {
        private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        private int count, overflow;
        public bool TakeOverflow() => Interlocked.Exchange(ref overflow, 0) != 0;
        public void Enqueue(Action action)
        {
            if (Interlocked.Increment(ref count) > 256)
            { Interlocked.Decrement(ref count); Interlocked.Exchange(ref overflow, 1); return; }
            queue.Enqueue(action);
        }
        public bool TryDequeue(out Action action)
        {
            if (!queue.TryDequeue(out action)) return false;
            Interlocked.Decrement(ref count); return true;
        }
    }
}
