﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common.StatusFeed;

namespace Wabbajack.Common
{
    public class WorkQueue : IDisposable
    {
        internal BlockingCollection<Func<Task>>
            Queue = new BlockingCollection<Func<Task>>(new ConcurrentStack<Func<Task>>());

        [ThreadStatic] private static int CpuId;

        internal static bool WorkerThread => CurrentQueue != null;
        [ThreadStatic] internal static WorkQueue CurrentQueue;

        private readonly Subject<CPUStatus> _Status = new Subject<CPUStatus>();
        public IObservable<CPUStatus> Status => _Status;

        public List<Thread> Threads { get; private set; }

        private CancellationTokenSource _cancel = new CancellationTokenSource();

        public WorkQueue(int threadCount = 0)
        {
            StartThreads(threadCount == 0 ? Environment.ProcessorCount : threadCount);
        }

        private void StartThreads(int threadCount)
        {
            ThreadCount = threadCount;
            Threads = Enumerable.Range(0, threadCount)
                .Select(idx =>
                {
                    var thread = new Thread(() => ThreadBody(idx).Wait());
                    thread.Priority = ThreadPriority.BelowNormal;
                    thread.IsBackground = true;
                    thread.Name = string.Format("Wabbajack_Worker_{0}", idx);
                    thread.Start();
                    return thread;
                }).ToList();
        }

        public int ThreadCount { get; private set; }

        private async Task ThreadBody(int idx)
        {
            CpuId = idx;
            CurrentQueue = this;

            try
            {
                while (true)
                {
                    Report("Waiting", 0, false);
                    if (_cancel.IsCancellationRequested) return;
                    var f = Queue.Take(_cancel.Token);
                    await f();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Report(string msg, int progress, bool isWorking = true)
        {
            _Status.OnNext(
                new CPUStatus
                {
                    Progress = progress,
                    ProgressPercent = progress / 100f,
                    Msg = msg,
                    ID = CpuId,
                    IsWorking = isWorking
                });
        }

        public void QueueTask(Func<Task> a)
        {
            Queue.Add(a);
        }

        public void Dispose()
        {
            _cancel.Cancel();
            Threads.Do(th =>
            {
                if (th.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
                {
                    th.Join();
                }
            });
            Queue?.Dispose();
        }
    }

    public class CPUStatus
    {
        public int Progress { get; internal set; }
        public float ProgressPercent { get; internal set; }
        public string Msg { get; internal set; }
        public int ID { get; internal set; }
        public bool IsWorking { get; internal set; }
    }
}
