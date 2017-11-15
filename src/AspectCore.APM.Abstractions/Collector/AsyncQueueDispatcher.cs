﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AspectCore.APM.Logger;
using AspectCore.APM.Transport;

namespace AspectCore.APM.Collector
{
    internal class AsyncQueueDispatcher : IPayloadDispatcher
    {
        const int _maxCapacity = 10000;
        const int _timeoutOnStopMs = 3000;

        private readonly IPayloadSender _payloadSender;
        private readonly ILogger _logger;

        private readonly ConcurrentQueue<IPayload> _queue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ManualResetEventSlim _eventSlim;
        private readonly Task _processQueueTask;

        public AsyncQueueDispatcher(IPayloadSender payloadSender, ILogger logger = null)
        {
            _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
            _logger = logger;
            _queue = new ConcurrentQueue<IPayload>();
            _cancellationTokenSource = new CancellationTokenSource();
            _eventSlim = new ManualResetEventSlim(false, spinCount: 1);
            _processQueueTask = Task.Factory.StartNew(Consumer, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void Consumer()
        {
            _logger?.LogInformation($"Start {Name}.");

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                ProcessQueue();

                try
                {
                    _eventSlim.Wait(_cancellationTokenSource.Token);
                    _eventSlim.Reset();
                }
                catch (OperationCanceledException ex)
                {
                    // expected
                    _logger?.LogError("AsyncQueueDispatcher exception.", ex);
                    break;
                }
            }

            ProcessQueue(); // one last time for the remaining messages
        }

        private void ProcessQueue()
        {
            while (_queue.TryDequeue(out var payload))
            {
                _payloadSender.SendAsync(payload, _cancellationTokenSource.Token);
            }
        }

        public string Name => "AsyncQueueDispatcher";

        public bool Dispatch(IPayload payload)
        {
            if (!_cancellationTokenSource.IsCancellationRequested && _queue.Count < _maxCapacity)
            {
                _queue.Enqueue(payload);
                if (!_eventSlim.IsSet)
                {
                    _eventSlim.Set();
                }
                return true;
            }
            return false;
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel(throwOnFirstException: false);
            _processQueueTask.Wait(_timeoutOnStopMs);
            _logger?.LogInformation($"Stop {Name}.");
        }
    }
}