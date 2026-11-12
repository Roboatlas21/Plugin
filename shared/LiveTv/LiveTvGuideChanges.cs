using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;

namespace Moonfin.LiveTv
{
    // Channel imports can raise thousands of library events. Publish once after
    // the guide task completes, or after a separate batch of channel edits settles.
    internal sealed class LiveTvGuideChanges : IDisposable
    {
        private readonly ILibraryManager _library;
        private readonly ITaskManager _tasks;
        private readonly Func<Task> _publish;
        private readonly Action<Exception> _log;
        private readonly Timer _timer;
        private readonly object _lock = new object();
        private bool _pending;
        private bool _disposed;

        public LiveTvGuideChanges(ILibraryManager library, ITaskManager tasks, Func<Task> publish, Action<Exception> log)
        {
            _library = library;
            _tasks = tasks;
            _publish = publish;
            _log = log;
            _timer = new Timer(state => _ = PublishAsync(), null, Timeout.Infinite, Timeout.Infinite);
            library.ItemAdded += OnChannelChanged;
            library.ItemUpdated += OnChannelChanged;
            library.ItemRemoved += OnChannelChanged;
            tasks.TaskCompleted += OnTaskCompleted;
        }

        private void OnChannelChanged(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item is LiveTvChannel) ChannelChanged();
        }

        private void OnTaskCompleted(object? sender, TaskCompletionEventArgs args)
        {
            if (args.Result.Key == "RefreshGuide")
                ChannelChanged();
        }

        public void ChannelChanged()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _pending = true;
                _timer.Change(500, Timeout.Infinite);
            }
        }

        private async Task PublishAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed || !_pending) return;
                    _pending = false;
                    if (_tasks.ScheduledTasks.Any(t => t.ScheduledTask.Key == "RefreshGuide" && t.State != TaskState.Idle)) return;
                }
                await _publish().ConfigureAwait(false);
            }
            catch (Exception ex) { _log(ex); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _timer.Dispose();
            }
            _library.ItemAdded -= OnChannelChanged;
            _library.ItemUpdated -= OnChannelChanged;
            _library.ItemRemoved -= OnChannelChanged;
            _tasks.TaskCompleted -= OnTaskCompleted;
        }
    }
}
