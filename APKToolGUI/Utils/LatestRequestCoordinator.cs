using System;
using System.Threading;
using System.Threading.Tasks;

namespace APKToolGUI.Utils
{
    public sealed class RequestHandle<T>
    {
        internal RequestHandle(long id, string key, CancellationToken token, Task<T> completion)
        {
            Id = id;
            Key = key;
            Token = token;
            Completion = completion;
        }

        public long Id { get; private set; }
        public string Key { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<T> Completion { get; private set; }
    }

    /// <summary>
    /// Runs one request at a time. Starting a request with a new key cancels the running one, and
    /// starting the same key while it is still running returns the existing handle instead of
    /// stacking up a duplicate request. A superseded request never writes its result back.
    /// </summary>
    public sealed class LatestRequestCoordinator<T>
    {
        private sealed class Request
        {
            public long Id;
            public string Key;
            public CancellationTokenSource Cancellation;
            public TaskCompletionSource<T> Completion;
            public RequestHandle<T> Handle;
        }

        private readonly object sync = new object();
        private Request current;
        private long nextId;
        private int activeCount;

        /// <summary>Number of operations currently executing (0 or 1).</summary>
        public int ActiveCount
        {
            get { lock (sync) return activeCount; }
        }

        /// <summary>True while <paramref name="requestId"/> is still the request whose result may be shown.</summary>
        public bool IsCurrent(long requestId)
        {
            lock (sync) return current != null && current.Id == requestId;
        }

        public RequestHandle<T> Start(string key, Func<long, CancellationToken, Task<T>> operation)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentException("A request key is required.", "key");
            if (operation == null) throw new ArgumentNullException("operation");

            Request request;
            Request previous;
            lock (sync)
            {
                if (current != null && !current.Completion.Task.IsCompleted &&
                    String.Equals(current.Key, key, StringComparison.OrdinalIgnoreCase))
                    return current.Handle;

                previous = current;
                if (previous != null && !previous.Completion.Task.IsCompleted)
                {
                    try { previous.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }

                request = new Request
                {
                    Id = ++nextId,
                    Key = key,
                    Cancellation = new CancellationTokenSource(),
                    Completion = new TaskCompletionSource<T>(),
                };
                request.Handle = new RequestHandle<T>(request.Id, request.Key,
                    request.Cancellation.Token, request.Completion.Task);
                current = request;
            }

            // Run on the thread pool so the operation never starts on the caller's (UI) thread.
            Task.Run(() => ExecuteAsync(request, previous, operation));
            return request.Handle;
        }

        public void CancelCurrent()
        {
            Request request;
            lock (sync) request = current;

            if (request != null && !request.Completion.Task.IsCompleted)
            {
                try { request.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task ExecuteAsync(Request request, Request previous, Func<long, CancellationToken, Task<T>> operation)
        {
            try
            {
                if (previous != null && !previous.Completion.Task.IsCompleted)
                {
                    // Wait for the previous operation to release its own resources before starting.
                    try { await previous.Completion.Task.ConfigureAwait(false); }
                    catch (Exception) { }
                }

                request.Cancellation.Token.ThrowIfCancellationRequested();

                lock (sync) activeCount++;
                T result;
                try
                {
                    result = await operation(request.Id, request.Cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (sync) activeCount--;
                }

                request.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                request.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
            finally
            {
                try { request.Cancellation.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
    }
}